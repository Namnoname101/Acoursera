(function () {
    const result = (State, control, Href = null) => JSON.stringify({ State, Href, Label: control ? label(control) : null });
    const expected = new URL(__EXPECTED_URL_JSON__);
    const current = new URL(location.href);
    if (current.origin !== expected.origin || current.pathname !== expected.pathname || current.search !== expected.search ||
        current.protocol !== 'https:' || !/^(?:www\.)?coursera\.org$/i.test(current.hostname) ||
        !/\/learn\/[^/]+(?:\/|$)/.test(current.pathname)) return result('STALE_DOCUMENT');
    const courseSlug = current.pathname.match(/\/learn\/([^/]+)/)[1];
    const normalize = value => String(value || '').replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim().toLowerCase();
    const label = element => normalize(element.getAttribute('aria-label') || element.getAttribute('title') || element.innerText || element.textContent);
    const visible = element => {
        if (!(element instanceof HTMLElement) || !element.isConnected || element.closest('[hidden],[aria-hidden="true"],[inert]')) return false;
        const style = getComputedStyle(element), rect = element.getBoundingClientRect();
        return style.display !== 'none' && !/hidden|collapse/.test(style.visibility) && Number(style.opacity || 1) > 0 && rect.width > 0 && rect.height > 0;
    };
    const enabled = element => visible(element) && !element.matches(':disabled,[aria-disabled="true"]');
    const controlsIn = root => Array.from(root.querySelectorAll('button,a,[role="button"]')).filter(enabled)
        .filter(control => !Array.from(control.querySelectorAll('button,a,[role="button"]')).some(enabled));
    const trustedHref = element => {
        const anchor = element.closest('a[href]');
        if (!anchor) return true;
        try {
            const url = new URL(anchor.href, location.href);
            return url.origin === current.origin && url.pathname.match(/\/learn\/([^/]+)/)?.[1] === courseSlug;
        } catch { return false; }
    };
    // A React rerender must not resubmit the same enrollment confirmation.
    const memoryKey = '__acoseCourseEnrollmentV1';
    let memory = window[memoryKey];
    if (!memory || memory.document !== expected.href) memory = window[memoryKey] = { document: expected.href, clicks: new Set() };
    const clickOnce = (control, state, key) => {
        if (!trustedHref(control)) return result('AMBIGUOUS');
        if (memory.clicks.has(key)) return result('WAIT');
        memory.clicks.add(key);
        control.click();
        return result(state, control);
    };
    const pickEquivalent = controls => {
        if (!controls.length) return null;
        // Identical sticky/mobile/desktop CTAs for this course may coexist.
        const keys = new Set(controls.map(control => label(control) + '|' + (control.closest('a[href]')?.href || 'button')));
        return keys.size === 1 && controls.every(trustedHref) ? controls[0] : false;
    };
    const paid = text => /\b(?:trial|buy now|purchase|subscribe|subscription|payment|checkout|credit card|per month|per year|billed|renew|upgrade|choose a plan)\b|[$€£₫]\s*\d|\d\s*(?:usd|vnd|eur)\b/.test(text);
    const dialogs = Array.from(document.querySelectorAll('[role="dialog"],[aria-modal="true"],#rc-TopLevelModal'))
        .filter(visible).filter(dialog => !Array.from(dialog.querySelectorAll('[role="dialog"],[aria-modal="true"]')).some(visible));
    if (dialogs.length > 1) return result('ENROLL_AMBIGUOUS');
    if (dialogs.length === 1) {
        const dialog = dialogs[0], text = normalize(dialog.innerText || dialog.textContent);
        const controls = controlsIn(dialog);
        const organization = /enroll through my organi[sz]ation/.test(text) && /this course is available in the learning program created by your organi[sz]ation/.test(text);
        const free = /(?:this course is free|enroll (?:in this course )?for free|100% (?:discount|off)|full discount|no (?:additional )?cost|included in your organi[sz]ation(?:'|’)s plan)/.test(text);
        const confirms = controls.filter(control => /^(?:continue(?: enrolling)?|enroll(?: now| for free)?|join for free|start learning(?: from this course)?|go to course)$/.test(label(control)));
        if (paid(text)) return result('PAID_OR_TRIAL');
        if (!organization && !free) return result('CONFIRMATION_REQUIRED');
        const candidate = pickEquivalent(confirms);
        if (candidate === false) return result('ENROLL_AMBIGUOUS');
        if (candidate) return clickOnce(candidate, 'CONFIRM_CLICKED', 'confirm|' + text + '|' + label(candidate));
        return result('WAIT');
    }
    const controls = controlsIn(document).filter(control => !control.closest('aside,[role="complementary"],footer,nav'));
    const entryLabels = new Set(['go to course', 'continue learning', 'continue course', 'resume course', 'start course']);
    const entries = controls.filter(control => {
        const attributes = ['data-e2e', 'data-testid', 'data-track-component', 'data-track-action']
            .map(name => normalize(control.getAttribute(name))).join(' ');
        return trustedHref(control) && (entryLabels.has(label(control)) ||
            /(?:^|[\s_-])go[\s_-]?to[\s_-]?course(?:$|[\s_-])/.test(attributes));
    });
    const entry = pickEquivalent(entries);
    if (entry === false) return result('AMBIGUOUS');
    if (entry) {
        const href = entry.closest('a[href]')?.href;
        return href ? result('OPEN_HREF', entry, href) : clickOnce(entry, 'CLICKED', 'entry|' + label(entry));
    }
    const enrolls = controls.filter(control => /^(?:enroll(?: for free)?|join for free)(?:\s+starts\s+.+)?$/.test(label(control)) && trustedHref(control));
    const enroll = pickEquivalent(enrolls);
    if (enroll === false) return result('ENROLL_AMBIGUOUS');
    if (enroll) {
        if (!__ALLOW_ENROLLMENT_START__) return result('WAIT');
        // Opens the enrollment dialog; paid/trial confirmations are handled separately above.
        return clickOnce(enroll, 'ENROLL_CLICKED', 'enroll');
    }
    // Only explicit payment actions count; unrelated Plus advertising on the page does not.
    if (controls.some(control => /^(?:start (?:a )?free trial|buy now|purchase|subscribe|choose a plan|start trial)$/.test(label(control)))) return result('PAID_OR_TRIAL');
    return result('NOT_FOUND');
})();
