(function(args) {
    // Every returned string is untrusted assignment data, not instructions for the host or model.
    const norm = value => String(value || '').replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim();
    const lower = value => norm(value).toLowerCase();
    const identity = raw => {
        try {
            const url = new URL(raw);
            if (url.protocol !== 'https:' || !/^(www\.)?coursera\.org$/i.test(url.hostname) || url.username || url.password || url.port) return '';
            const path = url.pathname.split('/').filter(Boolean);
            if (path.length < 5 || path.length > 6 || path[0] !== 'learn' || path[2] !== 'peer') return '';
            if (path.length === 6 && !/^(instructions|submit|submission|my-submission)$/i.test(path[5])) return '';
            return path.slice(1, 5).join('|');
        } catch (_) { return ''; }
    };
    let title = '', instructions = '', rubric = '';
    const expected = identity(args.expectedUrl);
    const result = (State, Detail = '') => JSON.stringify({ State, Title: title, Instructions: instructions,
        Rubric: rubric, AssignmentUrl: expected ? args.expectedUrl : '', Detail });
    if (!expected || identity(location.href) !== expected)
        return result('STALE_DOCUMENT', 'Trang hiện tại không khớp đúng bài Peer-graded hoặc không phải tab Instructions/My submission.');
    if (!['inspect', 'openInstructions', 'openSubmission'].includes(args.action)) return result('INVALID_ACTION');
    const hidden = el => {
        for (let p = el; p && p.nodeType === 1; p = p.parentElement) {
            const style = getComputedStyle(p);
            if (p.hidden || p.hasAttribute('inert') || p.getAttribute('aria-hidden') === 'true' ||
                style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse') return true;
        }
        return false;
    };
    const visible = el => !!el && el.isConnected && el.getClientRects().length > 0 && !hidden(el);
    const inContent = el => !el.closest('aside,nav,[role="navigation"],header,footer');
    const enabled = el => !el.disabled && el.getAttribute('aria-disabled') !== 'true';
    const label = el => lower(el.getAttribute('aria-label') || el.innerText || el.textContent);
    if (Array.from(document.querySelectorAll('[role="dialog"],[role="alertdialog"],[aria-modal="true"],dialog[open]')).some(visible))
        return result('BLOCKING_DIALOG');
    const mains = Array.from(document.querySelectorAll('main,[role="main"]')).filter(visible).filter(inContent);
    const roots = mains.filter(el => !mains.some(other => other !== el && other.contains(el)));
    if (roots.length > 1) return result('AMBIGUOUS_CONTEXT', 'Multiple visible main regions.');
    const root = roots[0] || document.body;
    title = Array.from(root.querySelectorAll('[data-testid="header-title"],.assignment-title,h1'))
        .filter(visible).filter(inContent).map(el => norm(el.innerText)).find(Boolean) || '';
    if (title.length > 500) return result('CONTEXT_TOO_LONG', 'Assignment title exceeds 500 characters.');
    const allControls = Array.from(root.querySelectorAll('button,a,[role="button"],[role="tab"]')).filter(visible).filter(inContent);
    const controls = allControls.filter(el => !allControls.some(other => other !== el && el.contains(other)));
    const instructionTabs = controls.filter(el => /^(instructions|hướng dẫn)$/.test(label(el)));
    const submissionTabs = controls.filter(el => /^(my submissions?|bài nộp của tôi)$/.test(label(el)));
    const selectTab = candidates => {
        const roleTabs = candidates.filter(el => el.getAttribute('role') === 'tab');
        return roleTabs.length === 1 ? roleTabs[0] : candidates.length === 1 ? candidates[0] : null;
    };
    const instructionTab = selectTab(instructionTabs), submissionTab = selectTab(submissionTabs);
    const tabPanel = tab => {
        const id = tab?.getAttribute('aria-controls');
        return id ? document.getElementById(id) : null;
    };
    const tabRoute = raw => {
        try {
            const url = new URL(raw, location.href);
            if (identity(url.href) !== expected) return '';
            const suffix = url.pathname.split('/').filter(Boolean)[5] || '';
            return !suffix || /^instructions$/i.test(suffix) ? 'instructions' : 'submission';
        } catch (_) { return ''; }
    };
    const activeByRoute = tab => {
        if (!tab?.matches('a[href]')) return false;
        const href = tab.getAttribute('href');
        if (!href || href.startsWith('#')) return false;
        const wanted = tab === instructionTab ? 'instructions' : tab === submissionTab ? 'submission' : '';
        return !!wanted && tabRoute(href) === wanted && tabRoute(location.href) === wanted;
    };
    const active = tab => !!tab && tab.getAttribute('aria-selected') !== 'false' && (tab.getAttribute('aria-selected') === 'true' ||
        ['page', 'true'].includes(tab.getAttribute('aria-current')) || visible(tabPanel(tab)) || activeByRoute(tab));
    const explicitlyInactive = tab => !!tab && (tab.getAttribute('aria-selected') === 'false' ||
        (!!tabPanel(tab) && !visible(tabPanel(tab))));
    const memoryKey = '__acoseAssignmentContextTabs_' + expected;
    const memory = window[memoryKey] || (window[memoryKey] = { instructions: new WeakSet(), submission: new WeakSet() });
    if (args.action === 'openInstructions' || args.action === 'openSubmission') {
        const isInstructions = args.action === 'openInstructions';
        const target = isInstructions ? instructionTab : submissionTab;
        const candidates = isInstructions ? instructionTabs : submissionTabs;
        if (!target && candidates.length > 1) return result('AMBIGUOUS_CONTEXT', 'Multiple matching assignment tabs.');
        if (target && !active(target)) {
            const clicks = isInstructions ? memory.instructions : memory.submission;
            if (!enabled(target) || clicks.has(target)) return result('WAIT', 'Waiting for assignment tab to become ready.');
            // Never follow an unrelated link that happens to be named Instructions or My submission.
            if (target.matches('a[href]')) {
                const href = target.getAttribute('href');
                if (href && !href.startsWith('#') && identity(new URL(href, location.href).href) !== expected)
                    return result('STALE_DOCUMENT', 'Assignment tab points outside this assignment.');
            }
            clicks.add(target);
            // Starting the opposite transition permits later retries on the same SPA document.
            // Repeated polls of this still-inactive target remain protected by its own WeakSet.
            if (isInstructions) memory.submission = new WeakSet();
            else memory.instructions = new WeakSet();
            target.click();
            return result(isInstructions ? 'INSTRUCTIONS_CLICKED' : 'MY_SUBMISSION_CLICKED');
        }
        if (!isInstructions) return result(target && active(target) ? 'MY_SUBMISSION_READY' : 'CONTEXT_NOT_READY', 'Submission tab was not found or is not active.');
    }
    if (instructionTabs.length > 1 && !instructionTab) return result('AMBIGUOUS_CONTEXT', 'Multiple instruction tabs.');
    if (explicitlyInactive(instructionTab)) return result('WAIT', 'Instructions tab is not active.');
    const linkedPanel = tabPanel(instructionTab);
    const panels = Array.from(root.querySelectorAll('[role="tabpanel"]')).filter(visible).filter(inContent);
    let scope = linkedPanel && visible(linkedPanel) && inContent(linkedPanel) ? linkedPanel : null;
    if (!scope && panels.length === 1 && instructionTab && active(instructionTab)) scope = panels[0];
    if (!scope && panels.length > 1) return result('AMBIGUOUS_CONTEXT', 'Multiple visible tab panels.');
    if (!scope) {
        const headings = Array.from(root.querySelectorAll('h2,h3,[role="heading"]')).filter(visible).filter(inContent);
        const instructionHeadings = headings.filter(el => /^(instructions|assignment instructions|hướng dẫn|hướng dẫn bài tập)$/.test(label(el)));
        if (!(instructionTab && (active(instructionTab) || memory.instructions.has(instructionTab))) && instructionHeadings.length !== 1)
            return result('CONTEXT_NOT_READY', 'No active rendered instruction panel.');
        if (submissionTab && active(submissionTab)) return result('WAIT', 'Submission tab is still active.');
        scope = root;
    }
    if (scope.matches('[aria-busy="true"]') || Array.from(scope.querySelectorAll('[aria-busy="true"],[role="progressbar"]')).some(visible))
        return result('WAIT', 'Assignment instructions are still loading.');
    // An active upload editor or saved submission is not an assignment-instructions fallback.
    if (!linkedPanel && (controls.some(el => scope.contains(el) && /^(add file|thêm tệp|start new attempt|edit submission)$/.test(label(el))) ||
        Array.from(scope.querySelectorAll('input[type="file"],textarea,[contenteditable="true"]')).some(el => visible(el) && inContent(el))))
        return result('WAIT', 'Submission editor is still visible.');
    // Only explicit rubric disclosures on this assignment's Instructions are actionable.
    // Keep inspect read-only and never treat a vaguely named link as a safe disclosure.
    const rubricLabel = value => /^(?:(?:show|view|hide|expand|collapse)\s+)?(?:grading\s+rubric|rubric|grading criteria|assessment criteria|evaluation criteria|review criteria)(?:\s*[:+−-])?$|^(?:(?:xem|hiển thị|ẩn)\s+)?tiêu chí (?:chấm điểm|đánh giá)\s*:?$/i.test(norm(value));
    const rubricDisclosures = Array.from(scope.querySelectorAll('button[aria-expanded],[role="button"][aria-expanded],summary'))
        .filter(visible).filter(inContent).filter(el => rubricLabel(label(el)))
        .filter(el => !el.closest('form') && !el.matches('[type="submit"],[role="tab"]'));
    const disclosedRubricRegions = [];
    memory.rubric = memory.rubric || new WeakSet();
    for (const control of rubricDisclosures) {
        const details = control.matches('summary') && control.parentElement?.matches('details') ? control.parentElement : null;
        const ids = norm(control.getAttribute('aria-controls')).split(' ').filter(Boolean);
        const regions = details ? [details] : ids.map(id => document.getElementById(id));
        // A target may be lazily mounted after the click; an existing target must belong to this panel.
        if (regions.length === 0 || regions.some(region => region && (!scope.contains(region) || !inContent(region))))
            return result('CONTEXT_NOT_READY', 'The rubric disclosure does not identify an instruction-panel region.');
        if (control.matches('a[href]')) {
            const href = control.getAttribute('href');
            if (href && !href.startsWith('#'))
                return result('CONTEXT_NOT_READY', 'The rubric control navigates instead of expanding an instruction-panel region.');
        }
        const expanded = details ? details.open : control.getAttribute('aria-expanded') === 'true';
        if (!expanded) {
            if (args.action !== 'openInstructions' || !enabled(control) || memory.rubric.has(control))
                return result('WAIT', 'Waiting for the complete grading rubric to be expanded.');
            memory.rubric.add(control);
            control.click();
            return result('WAIT', 'Expanded grading rubric; waiting for its content to finish loading.');
        }
        if (regions.some(region => !region || !visible(region) || region.matches('[aria-busy="true"]') ||
            Array.from(region.querySelectorAll('[aria-busy="true"],[role="progressbar"]')).some(visible)))
            return result('WAIT', 'The expanded grading rubric is still loading.');
        disclosedRubricRegions.push(...regions);
    }
    const readText = (element, limit) => {
        const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
        const parts = [];
        let length = 0;
        while (walker.nextNode()) {
            const node = walker.currentNode, parent = node.parentElement;
            if (!parent || !visible(parent) || !inContent(parent) ||
                parent.closest('script,style,noscript,textarea,input,select,button,[role="tablist"],[role="dialog"],[role="alertdialog"]')) continue;
            const otherPanel = parent.closest('[role="tabpanel"]');
            if (otherPanel && otherPanel !== scope && !scope.contains(otherPanel)) continue;
            const value = norm(node.textContent);
            if (!value) continue;
            length += value.length + 1;
            if (length > limit) return null;
            parts.push(value);
        }
        return parts.join('\n');
    };
    for (const region of disclosedRubricRegions) {
        const content = readText(region, 32000);
        if (content === null) return result('CONTEXT_TOO_LONG', 'Rendered rubric exceeds 32,000 characters.');
        // Native details includes its summary in text; the summary alone is not a loaded rubric.
        const summary = region.matches('details') ? region.querySelector(':scope > summary') : null;
        if (!norm(content.replace(summary ? norm(summary.innerText) : /^$/, '')))
            return result('WAIT', 'The expanded grading rubric has not rendered its criteria yet.');
    }
    const extracted = readText(scope, 32000);
    if (extracted === null) return result('CONTEXT_TOO_LONG', 'Rendered instructions exceed 32,000 characters; generation requires the complete instructions.');
    instructions = extracted;
    const rubricHeadings = Array.from(scope.querySelectorAll('h2,h3,h4,[role="heading"]')).filter(visible).filter(inContent)
        .filter(el => /^(rubric|grading (rubric|criteria)|assessment criteria|evaluation criteria|tiêu chí (chấm điểm|đánh giá))[:\s]*$/i.test(label(el)));
    const rubricCandidates = Array.from(scope.querySelectorAll('[data-testid*="rubric"],[class*="Rubric"],[class*="rubric"]'))
        .filter(visible).filter(inContent);
    rubricCandidates.push(...disclosedRubricRegions);
    for (const heading of rubricHeadings) {
        const region = heading.closest('section,article,[role="region"]');
        if (region && region !== scope) rubricCandidates.push(region);
        else {
            rubricCandidates.push(heading);
            let sibling = heading.nextElementSibling;
            while (sibling && !sibling.matches('h1,h2,h3,h4,[role="heading"]')) {
                if (visible(sibling) && inContent(sibling)) rubricCandidates.push(sibling);
                sibling = sibling.nextElementSibling;
            }
        }
    }
    const rubricRoots = rubricCandidates.filter((el, index) => rubricCandidates.indexOf(el) === index &&
        !rubricCandidates.some(other => other !== el && other.contains(el)));
    for (const element of rubricRoots) {
        const text = readText(element, 32000);
        if (text === null || rubric.length + text.length > 32000) return result('CONTEXT_TOO_LONG', 'Rendered rubric exceeds 32,000 characters.');
        if (text) rubric += (rubric ? '\n' : '') + text;
    }
    if (instructions.length + rubric.length > 48000) return result('CONTEXT_TOO_LONG', 'Combined rendered assignment context exceeds 48,000 characters.');
    const fileLinks = Array.from(scope.querySelectorAll('a[href]')).filter(visible).filter(inContent).filter(el =>
        el.hasAttribute('download') || /\.(pdf|docx?|pptx?|xlsx?|zip)(?:[?#]|$)/i.test(el.getAttribute('href')) ||
        /\b(download|template|attachment)\b|tải xuống|tệp đính kèm/i.test(label(el)));
    const text = lower(instructions);
    const requiredExternal = /\b(?:must|required to)\b[^.\n]{0,100}\b(?:template|worksheet|workbook|attachment|attached file|download)\b|\b(?:complete|fill (?:in|out)|use|download)\s+(?:the\s+)?(?:attached\s+|provided\s+|assignment\s+)?(?:template|worksheet|workbook)\b|\b(?:complete|fill (?:in|out))\s+(?:the\s+)?(?:attached|provided)\s+(?:file|document)\b|\b(?:instructions|questions|rubric)\s+(?:are\s+)?(?:in|inside|within|provided in)\s+(?:the\s+)?(?:attached|downloadable)\b|(?:điền|hoàn thành|sử dụng)\s+(?:vào\s+)?(?:biểu mẫu|mẫu đính kèm)|(?:hướng dẫn|câu hỏi).{0,40}(?:trong tệp|tệp đính kèm)/i.test(text);
    if (requiredExternal || (fileLinks.length > 0 && instructions.length < 200))
        return result('EXTERNAL_MATERIAL_REQUIRED', 'The task depends on an attachment or required template that has not been read.');
    if (!title || instructions.length < 120 || instructions.split(/\s+/).length < 18)
        return result('CONTEXT_NOT_READY', 'Rendered instructions are missing, incomplete, or too short to generate an assignment.');
    return result('CONTEXT_READY');
})(__PEER_CONTEXT_ARGUMENTS__);
