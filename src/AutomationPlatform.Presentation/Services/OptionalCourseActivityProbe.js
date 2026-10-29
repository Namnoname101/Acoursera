(function () {
    const args = __OPTIONAL_ACTIVITY_ARGUMENTS__;
    const normalize = value => String(value || '').replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim();
    const result = (state, kind = '') => JSON.stringify({ State: state, Kind: kind });
    const coursePath = value => {
        try {
            const uri = new URL(value, location.href);
            if (uri.protocol !== 'https:' || !/^(?:www\.)?coursera\.org$/i.test(uri.hostname) || uri.port)
                return '';
            return uri.pathname.replace(/^\/programs\/[^/]+(?=\/learn\/)/i, '').replace(/\/+$/, '').toLowerCase();
        } catch (_) { return ''; }
    };
    const currentPath = coursePath(location.href);
    if (!currentPath || currentPath !== coursePath(args.expectedUrl) ||
        !/^\/learn\/[^/]+\/[^/]+\/[^/]+/.test(currentPath)) return result('STALE_DOCUMENT');
    // A label on a neighbouring lesson must never make a graded activity skippable.
    if (/\/learn\/[^/]+\/(?:peer|quiz|exam|assignment-submission|gradedlti)(?:\/|$)/i.test(currentPath))
        return result('NOT_OPTIONAL');
    const rendered = element => {
        if (!element || !element.isConnected || element.closest('[hidden], [inert], [aria-hidden="true"]')) return false;
        for (let node = element; node && node.nodeType === 1; node = node.parentElement) {
            const style = getComputedStyle(node);
            if (style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity || 1) === 0) return false;
        }
        const rect = element.getBoundingClientRect();
        return rect.width > 0 && rect.height > 0;
    };
    const activeLinks = Array.from(document.querySelectorAll('a[href]'))
        .filter(link => coursePath(link.href) === currentPath && rendered(link));
    const activeText = normalize(activeLinks.map(link => {
        const row = link.closest('li, [data-testid="rc-Item"], .rc-Item') || link;
        // Inline title/type spans need a separator; innerText can concatenate
        // them as "Practical ScenarioReal-world application".
        const labels = Array.from(row.querySelectorAll('span')).filter(rendered)
            .map(el => el.innerText || el.textContent);
        return [link.getAttribute('aria-label'), row.innerText || row.textContent, ...labels].join(' ');
    }).join(' '));
    const headingText = normalize(Array.from(document.querySelectorAll('h1, [data-testid="page-header-wrapper"]'))
        .filter(rendered).filter(el => !el.closest('aside, nav'))
        .map(el => el.innerText || el.textContent).join(' '));
    if (/\bgraded\s+(?:assignment|quiz|exam|plugin|app\s+item)|\bpeer[- ]graded\b/i.test(activeText + ' ' + headingText))
        return result('NOT_OPTIONAL');
    const isWidgetRoute = /^\/learn\/[^/]+\/ungradedwidget\//i.test(currentPath);
    const isUngradedPlugin = /\bUngraded\s+Plugin\b/i.test(activeText);
    const chat = Array.from(document.querySelectorAll('textarea, input, [contenteditable="true"]')).some(el =>
        rendered(el) && /message\s+coursera\s+ai\s+chat/i.test([
            el.getAttribute('aria-label'), el.getAttribute('placeholder'), el.getAttribute('data-placeholder')
        ].join(' ')));
    const headerHasRealWorld = Array.from(document.querySelectorAll('header, [data-testid="page-header-wrapper"], h1, h2, span, p'))
        .filter(rendered).filter(el => !el.closest('aside, nav, a'))
        .some(el => /^Real[- ]world application$/i.test(normalize(el.innerText || el.textContent)));
    const isRealWorld = /\bReal[- ]world application\b/i.test(activeText) || (headerHasRealWorld && chat);
    const kind = isWidgetRoute || isUngradedPlugin ? 'Ungraded Plugin' : isRealWorld ? 'Real-world application' : '';
    if (!kind) return result('NOT_OPTIONAL');
    if (args.action !== 'next') return result('OPTIONAL_READY', kind);
    if (Array.from(document.querySelectorAll('[role="dialog"], [role="alertdialog"], [aria-modal="true"], dialog[open]')).some(rendered))
        return result('DIALOG_OPEN', kind);
    // Some course footers render the arrow as text, without an aria-label.
    // Match the same exact action after stripping only its decorative suffix.
    const nextLabel = el => normalize(el.getAttribute('aria-label') || el.innerText || el.textContent)
        .replace(/\s*(?:[→➜➝⟶➔›»]|->)\s*$/, '').trim();
    const candidates = Array.from(document.querySelectorAll('button, a[href], [role="button"]'))
        .filter(rendered).filter(el => !el.closest('aside, nav'))
        .filter(el => /^(?:Go to next item|Next item|Chuyển sang mục tiếp theo)$/i.test(nextLabel(el)));
    if (candidates.length !== 1) return result(candidates.length ? 'NEXT_AMBIGUOUS' : 'NEXT_NOT_FOUND', kind);
    const next = candidates[0];
    const anchor = next.closest('a[href]');
    if (anchor) {
        const destination = new URL(anchor.href, location.href);
        if (destination.origin !== location.origin ||
            coursePath(destination.href).split('/')[2] !== currentPath.split('/')[2])
            return result('NEXT_OUTSIDE_COURSE', kind);
    }
    if (next.disabled || next.matches(':disabled') || next.getAttribute('aria-disabled') === 'true')
        return result('NEXT_DISABLED', kind);
    next.scrollIntoView({ block: 'center', inline: 'center' });
    const rect = next.getBoundingClientRect();
    const receiver = document.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height / 2);
    if (!receiver || (receiver !== next && !next.contains(receiver))) return result('NEXT_OBSCURED', kind);
    next.click();
    return result('NEXT_CLICKED', kind);
})();
