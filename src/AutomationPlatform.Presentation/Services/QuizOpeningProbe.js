(function () {
    const expected = new URL(__EXPECTED_URL__), current = new URL(location.href);
    if (current.origin !== expected.origin || current.pathname !== expected.pathname)
        return 'STALE';
    const allowPageActions = __ALLOW_PAGE_ACTIONS__, feedbackRead = __FEEDBACK_READ__;
    const norm = value => String(value || '').replace(/\s+/g, ' ').trim().toLowerCase();
    const visible = element => {
        if (!element || !element.isConnected || element.getClientRects().length === 0) return false;
        for (let node = element; node instanceof HTMLElement; node = node.parentElement) {
            const style = getComputedStyle(node);
            if (node.hidden || node.getAttribute('aria-hidden') === 'true' || node.hasAttribute('inert') ||
                style.display === 'none' || /hidden|collapse/.test(style.visibility) || Number(style.opacity) === 0) return false;
        }
        return true;
    };
    const enabled = element => visible(element) && !element.matches(':disabled,[readonly]') &&
        !element.closest('[aria-disabled="true"],[disabled]');
    const name = element => norm(element.innerText || element.textContent || element.getAttribute('aria-label'));
    const controls = scope => Array.from(scope.querySelectorAll('button,a,[role="button"]')).filter(visible);
    const isContinue = element => /^(continue|tiếp tục)$/.test(name(element));
    const isAttemptTitle = value => /^(?:start (?:a )?new attempt\??|bắt đầu (?:một )?lần (?:làm|thi|thử) mới\??)$/.test(norm(value));
    const documentKey = current.origin + current.pathname;
    if (window.__acoseQuizOpening?.key !== documentKey)
        window.__acoseQuizOpening = { key: documentKey, startClicked: false, feedbackClicked: false, continued: new WeakSet() };
    const state = window.__acoseQuizOpening;

    // Filter out the fullscreen quiz dialog itself and empty containers
    const dialogs = Array.from(document.querySelectorAll(
        '[role="dialog"],[role="alertdialog"],[aria-modal="true"],dialog[open],.cds-Modal-container'))
        .filter(visible)
        .filter(dialog => !dialog.classList.contains('cds-FullscreenDialog-dialog'))
        .filter(dialog => !dialog.closest('.cds-FullscreenDialog-dialog'))
        .filter(dialog => !dialog.querySelector('.cds-FullscreenDialog-dialog'))
        .filter(dialog => !dialog.querySelector('[data-testid^="part-Submission_"]'))
        .filter(dialog => !dialog.querySelector('[data-testid="CoverPageActionButton"]'))
        .filter(dialog => (dialog.innerText || '').trim().length > 0);

    // Find "Start a new attempt?" confirmation modal
    const titles = Array.from(document.querySelectorAll('h1,h2,h3,h4,[role="heading"],div,span,p'))
        .filter(element => visible(element) && isAttemptTitle(element.innerText || element.textContent));
    const scopes = dialogs.filter(dialog => /start (?:a )?new attempt|bắt đầu (?:một )?lần (?:làm|thi|thử) mới/i.test(dialog.innerText || dialog.textContent) &&
        controls(dialog).some(isContinue));
    for (const title of titles) {
        for (let node = title.parentElement, depth = 0; node && node !== document.body && depth < 8; node = node.parentElement, depth++) {
            const buttons = controls(node);
            if (buttons.some(isContinue) && buttons.some(button => /^(cancel|hủy)$/.test(name(button)))) {
                scopes.push(node);
                break;
            }
        }
    }
    const unique = [...new Set(scopes)];
    const attempts = unique.filter(scope => !unique.some(other => other !== scope && scope.contains(other)));
    if (attempts.length) {
        if (attempts.length !== 1)
            return 'WAITING_DIALOG';
        const buttons = controls(attempts[0]).filter(isContinue).filter(enabled);
        if (buttons.length !== 1) return 'WAITING_CONTINUE';
        if (state.continued.has(buttons[0])) return 'WAITING_ATTEMPT';
        state.continued.add(buttons[0]);
        buttons[0].click();
        return 'CLICKED_CONTINUE';
    }

    // Buttons on page / banner
    const pageButtons = controls(document).filter(enabled)
        .filter(button => !button.closest('[data-testid^="part-Submission_"]'));
    const start = pageButtons.find(button => button.getAttribute('data-testid') === 'CoverPageActionButton') ||
        pageButtons.find(button => /^(start( assignment)?|resume( assignment)?|retake( assignment)?|retry|try again|làm lại|tiếp tục)$/i.test(name(button)));
    const feedback = pageButtons.find(button => name(button) === 'view feedback');

    // Auto-detect if page is already showing submission feedback
    const hasGradeBanner = /your (?:grade|latest|highest)\s*:/i.test(document.body?.innerText || '');
    const isAlreadyFeedback = hasGradeBanner && !!document.querySelector('svg[data-testid="icon-incorrect"], svg[data-testid="icon-correct"]');

    // Result pages retain radio inputs; their presence is not an open attempt.
    if (allowPageActions && !state.startClicked && !state.feedbackClicked) {
        if (!feedbackRead && (feedback || isAlreadyFeedback)) {
            state.feedbackClicked = true;
            if (feedback) feedback.click();
            return 'FEEDBACK';
        }
        if (start) {
            state.startClicked = true;
            start.click();
            return 'CLICKED_START';
        }
    }

    // Check if questions are ready to solve
    const questions = Array.from(document.querySelectorAll('[data-testid^="part-Submission_"]')).filter(visible);
    const editable = questions.some(question => Array.from(question.querySelectorAll(
        'input:not([type="hidden"]),textarea,[contenteditable="true"]')).some(enabled));

    if (!hasGradeBanner && editable) {
        return 'READY';
    }

    if (dialogs.length) return 'WAITING_DIALOG';

    if (start || feedback || hasGradeBanner)
        return 'WAITING_ATTEMPT';

    return editable ? 'READY' : 'WAITING_QUESTIONS';
})();
