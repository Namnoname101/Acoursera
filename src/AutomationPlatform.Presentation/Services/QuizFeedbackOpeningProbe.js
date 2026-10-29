(function () {
    'use strict';
    const expected = new URL(__EXPECTED_URL__), current = new URL(location.href), action = __ACTION__;
    const result = (State, extra = {}) => JSON.stringify({ State, ...extra });
    const trusted = u => u.protocol === 'https:' && !u.username && !u.password &&
        (!u.port || u.port === '443') && /^(www\.)?coursera\.org$/i.test(u.hostname);
    const activity = u => u.pathname.match(/^(?:\/programs\/[^/]+)?\/learn\/([^/]+)\/(quiz|exam|assignment-submission)\/([^/]+)(?:\/[^/]+)?(?:\/(?:attempt|view-attempt|view-feedback))?\/?$/i)?.slice(1).join('|');
    if (!trusted(expected) || !trusted(current) || !activity(expected) || activity(expected) !== activity(current)) return result('STALE');
    const norm = s => String(s || '').replace(/\s+/g, ' ').trim();
    const visible = e => {
        if (!e?.isConnected || !e.getClientRects().length) return false;
        for (let p = e; p; p = p.parentElement) {
            const s = getComputedStyle(p);
            if (p.hidden || p.hasAttribute('inert') || p.getAttribute('aria-hidden') === 'true' ||
                s.display === 'none' || /hidden|collapse/.test(s.visibility) || Number(s.opacity) === 0) return false;
        }
        return true;
    };
    const disabled = e => e.matches(':disabled,[readonly],[aria-disabled="true"]') || !!e.closest('[disabled],[aria-disabled="true"]');
    const rootSelector = '[data-testid^="part-Submission_"],[id^="part-Submission_"]';
    const controls = q => [...q.querySelectorAll('input[type="checkbox"],input[type="radio"],textarea,input[type="text"],[role="radio"],[role="checkbox"]')];
    const roots = [...document.querySelectorAll(rootSelector)].filter(q => visible(q) && !q.parentElement?.closest(rootSelector));
    const questions = roots.filter(q => q.querySelector('[id^="prompt-"],[data-testid="cml-viewer"],.rc-CML') && controls(q).length);
    const editable = questions.some(q => controls(q).some(e => !disabled(e) &&
        (visible(e) || [...(e.labels || [])].some(visible))));
    const graded = questions.filter(q => {
        const marker = [...q.querySelectorAll('[data-testid="icon-correct"],[data-testid="icon-incorrect"],.cds-Feedback,[class*="Feedback"],[data-testid*="feedback" i],[role="alert"]')]
            .some(e => visible(e) && (e.matches('[data-testid="icon-correct"],[data-testid="icon-incorrect"]') ||
                /^(correct|incorrect|nice work|try again|this should not be selected|đúng|sai|chính xác)[!.:]?$/i.test(norm(e.textContent))));
        return marker || [...q.querySelectorAll('*')].some(e => visible(e) && !e.children.length &&
            /^\d+(?:\.\d+)?\s*(?:\/|out of)\s*\d+(?:\.\d+)?\s*(?:points?|điểm)?$/i.test(norm(e.textContent)));
    });
    const pageButtons = [...document.querySelectorAll('button,a,[role="button"]')].filter(e => visible(e) &&
        !e.closest(rootSelector + ',nav,aside,[role="navigation"]'));
    const exact = pageButtons.filter(e => e.getAttribute('data-testid') === 'view-feedback-button');
    const candidates = exact.length ? exact : pageButtons.filter(e => /^(view feedback|xem phản hồi)$/i.test(norm(e.innerText || e.textContent || e.getAttribute('aria-label'))));
    const foreground = e => {
        const r = e.getBoundingClientRect();
        const left = Math.max(0, r.left), right = Math.min(innerWidth, r.right);
        const top = Math.max(0, r.top), bottom = Math.min(innerHeight, r.bottom);
        if (left >= right || top >= bottom) return false;
        const hit = document.elementFromPoint((left + right) / 2, (top + bottom) / 2);
        return !!hit && (hit === e || e.contains(hit));
    };
    // Fullscreen feedback may cover rather than remove the old result banner.
    // Prefer confirmed foreground graded detail over its occluded cover button.
    const openedOverCover = !editable && graded.some(foreground) && !candidates.some(foreground);

    // A cover button means the result detail is not yet open. Hidden cached
    // questions and editable attempts must never satisfy feedback readiness.
    if (candidates.length && !openedOverCover) {
        if (candidates.length !== 1) return result('AMBIGUOUS_FEEDBACK');
        const button = candidates[0];
        if (disabled(button)) return result('WAITING_BUTTON');
        if (action === 'prepareClick' || action === 'click') button.scrollIntoView({block:'center', inline:'nearest', behavior:'instant'});
        const r = button.getBoundingClientRect(), X = r.left + r.width / 2, Y = r.top + r.height / 2;
        if (action === 'prepareClick' || action === 'click') {
            const hit = document.elementFromPoint(X, Y);
            if (!hit || !(hit === button || button.contains(hit))) return result('CLICK_BLOCKED');
        }
        if (action === 'click') {
            // Re-find and act in this document, without mixing DOM coordinates
            // with the host's emulated/scaled viewport. Readiness is verified later.
            button.click();
            return result('CLICK_SENT');
        }
        return result('CLICK_READY', {X, Y, QuestionCount: questions.length});
    }
    if (editable) return result('ACTIVE_ATTEMPT');
    if (!questions.length || !graded.length) return result('WAITING_FEEDBACK', {QuestionCount: questions.length});
    let scroller = document.scrollingElement;
    for (let p = questions[0].parentElement; p; p = p.parentElement) {
        if (p === document.body || p === document.documentElement) continue;
        if (visible(p) && p.scrollHeight > p.clientHeight + 2 && /auto|scroll/.test(getComputedStyle(p).overflowY)) { scroller = p; break; }
    }
    if (scroller && action === 'scroll') scroller.scrollTop = Math.min(scroller.scrollHeight, scroller.scrollTop + Math.max(500, scroller.clientHeight * .85));
    if (scroller && action === 'top') scroller.scrollTop = 0;
    return result('READY', {
        QuestionCount: questions.length, GradedCount: graded.length,
        AtEnd: !scroller || scroller.scrollTop + scroller.clientHeight >= scroller.scrollHeight - 3,
        Signature: JSON.stringify(questions.map(q => [q.getAttribute('data-testid') || q.id, norm(q.querySelector('[id^="prompt-"],[data-testid="cml-viewer"],.rc-CML')?.textContent)]))
    });
})();
