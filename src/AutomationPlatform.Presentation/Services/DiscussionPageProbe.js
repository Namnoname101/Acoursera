(function () {
    const expected = new URL(__EXPECTED_URL__), current = new URL(location.href);
    const reply = __EXPECTED_REPLY__, action = __ACTION__;
    const expectedContext = typeof __EXPECTED_CONTEXT__ === 'undefined' ? null : __EXPECTED_CONTEXT__;
    let context = { Title: '', Prompt: '' };
    const answer = (State, HasDraft = false) => JSON.stringify({ State, HasDraft, ...context });
    if (current.origin !== expected.origin || current.pathname !== expected.pathname || current.search !== expected.search ||
        expected.protocol !== 'https:' || expected.port || expected.username || expected.password ||
        current.protocol !== 'https:' || current.port || current.username || current.password ||
        !/^(?:www\.)?coursera\.org$/i.test(current.hostname) ||
        !/\/learn\/[^/]+\/discussionprompt\//i.test(current.pathname)) return answer('STALE');
    // React can navigate to another discussion without creating a new window.
    // Editor/submission guards belong to the current document only.
    const documentKey = current.origin + current.pathname + current.search;
    if (window.__acoseDiscussionDocument !== documentKey) {
        window.__acoseDiscussionDocument = documentKey;
        window.__acoseDiscussionEditor = null;
        window.__acoseDiscussionSubmitAttempted = false;
    }
    const norm = value => String(value || '').replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim().toLowerCase();
    const text = element => String(element?.value ?? element?.innerText ?? element?.textContent ?? '').trim();
    // Rich-text editors render placeholder/toolbar text as real DOM children. It is
    // not a learner draft. Ignore structural editor UI, never a matching phrase:
    // a learner who actually types "Type your response here..." still owns a draft.
    const editorUiSelector = '[data-slate-placeholder="true"],[data-lexical-placeholder="true"],' +
        '.public-DraftEditorPlaceholder-root,.public-DraftEditorPlaceholder-inner,' +
        '[contenteditable="false"][data-placeholder],[contenteditable="false"].placeholder,' +
        '[contenteditable="false"][aria-hidden="true"],[role="toolbar"],button,[role="button"],select,script,style';
    const editorText = editor => {
        if (typeof editor.value === 'string') return editor.value.trim();
        const walker = document.createTreeWalker(editor, NodeFilter.SHOW_TEXT | NodeFilter.SHOW_ELEMENT);
        const parts = [];
        let previousBlock = null;
        while (walker.nextNode()) {
            const node = walker.currentNode, parent = node.parentElement;
            if (node.nodeType === Node.ELEMENT_NODE) {
                if (node.tagName === 'BR' && !node.closest(editorUiSelector)) parts.push('\n');
                continue;
            }
            if (parent?.closest(editorUiSelector)) continue;
            const block = parent?.closest('p,div,li,blockquote,h1,h2,h3,pre');
            if (previousBlock && block !== previousBlock) parts.push('\n');
            parts.push(node.nodeValue || '');
            previousBlock = block;
        }
        const content = parts.join('').replace(/[\u200b\u200c\u200d\ufeff]/g, '').trim();
        // An embedded image/attachment is also a draft, even without prose.
        return content || (Array.from(editor.querySelectorAll('img,video,audio,iframe,[data-slate-void="true"]'))
            .some(item => !item.closest(editorUiSelector)) ? '[embedded content]' : '');
    };
    const visible = element => {
        if (!(element instanceof HTMLElement) || !element.isConnected || element.closest('[hidden],[aria-hidden="true"],[inert]')) return false;
        for (let ancestor = element; ancestor; ancestor = ancestor.parentElement) {
            const style = getComputedStyle(ancestor);
            if (style.display === 'none' || /hidden|collapse/.test(style.visibility) || Number(style.opacity || '1') <= 0) return false;
        }
        const r = element.getBoundingClientRect();
        return r.width > 0 && r.height > 0;
    };
    const enabled = element => visible(element) && !element.matches(':disabled,[readonly]') && !element.closest('[aria-disabled="true"],[disabled]');
    const name = element => norm(element.getAttribute('aria-label') ||
        (element.getAttribute('aria-labelledby') || '').split(/\s+/).map(id => document.getElementById(id)?.textContent || '').join(' ') ||
        element.getAttribute('title') || element.innerText || element.textContent);
    const main = document.querySelector('main,[role="main"]') || document.body;
    const threadSelector = '[data-testid*="comment"],[data-testid*="reply"],[data-testid*="thread"],' +
        '[class~="rc-DiscussionThread"],[class~="rc-DiscussionReply"],[class~="rc-DiscussionReplies"]';
    const nonTaskSelector = 'aside,[role="complementary"],nav,[role="navigation"],[role="dialog"],' + threadSelector;
    const buttonsIn = root => Array.from(root.querySelectorAll('button,[role="button"],a')).filter(visible);
    const controls = buttonsIn(main).filter(control => !control.closest(nonTaskSelector));
    const editorSelector = 'textarea,[contenteditable="true"],[role="textbox"]';
    const candidates = Array.from(main.querySelectorAll(editorSelector)).filter(visible)
        .filter(element => !element.closest('aside,[role="complementary"],nav,[role="dialog"]'))
        .filter(element => !Array.from(element.querySelectorAll(editorSelector)).some(visible));
    const label = editor => norm(editor.getAttribute('aria-label') ||
        (editor.getAttribute('aria-labelledby') || '').split(/\s+/).map(id => document.getElementById(id)?.textContent || '').join(' ') ||
        (editor.labels ? Array.from(editor.labels).map(l => l.textContent).join(' ') : '') ||
        editor.getAttribute('placeholder') || editor.getAttribute('data-placeholder'));
    const named = candidates.filter(editor => /(?:your|write|enter|add|post)?\s*(?:reply|response|answer)\b/.test(label(editor)));
    const editors = named.length ? named : candidates.filter(editor => !/search|comment|message/.test(label(editor)));
    const hasDraft = editors.some(editor => editorText(editor).length > 0);
    // Coursera renders the acknowledgement as either one sentence or a banner:
    // "Response submitted" + "Your response has been submitted. Engage and
    // discuss with other learners below!". Match that complete UI message, not
    // a substring of a learner's reply or arbitrary page content.
    const isConfirmationText = value => /^(?:response submitted[.!]?\s*)?(?:your (?:response|reply) has been submitted|response has been submitted)[.!]?(?:\s*engage and discuss with other learners below[.!]?)?$/i.test(norm(value)) ||
        /^đã gửi (?:phản hồi|câu trả lời)[.!]?$/i.test(norm(value));
    const confirmation = Array.from(main.querySelectorAll('p,div,span,[role="status"],[role="alert"]')).filter(visible)
        .filter(element => !element.closest(nonTaskSelector + ',' + editorSelector) &&
            !element.querySelector(editorSelector + ',' + threadSelector))
        .some(element => isConfirmationText(element.innerText));
    const viewResponse = controls.some(control => /^(?:view my response|view your response|view response)$/.test(name(control)) && enabled(control));
    if (confirmation || (viewResponse && editors.length === 0)) {
        if (action === 'advance') {
            // The course footer may be a sibling of main rather than inside it.
            const next = buttonsIn(document).filter(control => !control.closest(nonTaskSelector) &&
                name(control) === 'go to next item' && enabled(control));
            if (next.length === 1) {
                const anchor = next[0].closest('a[href]');
                if (anchor) {
                    const destination = new URL(anchor.href, current);
                    if (destination.origin !== current.origin || destination.pathname.match(/\/learn\/([^/]+)/)?.[1] !==
                        current.pathname.match(/\/learn\/([^/]+)/)?.[1]) return answer('CONFIRMED_NO_NEXT');
                }
                next[0].click();
                return answer('ADVANCED');
            }
            return answer('CONFIRMED_NO_NEXT');
        }
        return answer('CONFIRMED');
    }
    // Login and access failures are different from a slow discussion editor.
    if (controls.some(control => /^(?:log in|sign in|enroll|start free trial)$/.test(name(control)))) return answer('ACCESS_REQUIRED', hasDraft);
    if (Array.from(document.querySelectorAll('[role="dialog"],[aria-modal="true"]')).some(visible)) return answer('BLOCKED_DIALOG', hasDraft);
    if (action === 'previous') {
        if (hasDraft || window.__acoseDiscussionSubmitAttempted) return answer('BACK_BLOCKED', hasDraft);
        const previous = buttonsIn(document).filter(control => enabled(control) &&
            [name(control), norm(control.innerText || control.textContent), norm(control.getAttribute('title'))]
                .some(value => /^(?:go to previous item|previous item|previous|bài trước)$/.test(value)));
        if (previous.length !== 1) return answer(previous.length ? 'AMBIGUOUS' : 'NO_PREVIOUS');
        const anchor = previous[0].closest('a[href]');
        if (anchor) {
            const destination = new URL(anchor.href, current);
            if (destination.origin !== current.origin || destination.pathname === current.pathname ||
                destination.pathname.match(/\/learn\/([^/]+)/)?.[1] !==
                current.pathname.match(/\/learn\/([^/]+)/)?.[1]) return answer('BACK_BLOCKED');
        }
        previous[0].click();
        return answer('PREVIOUS_CLICKED');
    }
    if (!editors.length) return answer('NOT_READY');
    if (editors.length !== 1) return answer('AMBIGUOUS', hasDraft);
    const editor = editors[0];
    const readContext = () => {
        // Restrict context to the activity heading and instruction text BEFORE
        // the owned editor. Never send the thread/replies beneath it to the AI.
        const excluded = 'aside,nav,header,footer,[role="complementary"],[role="navigation"],' +
            '[role="dialog"],[role="toolbar"],[role="status"],[role="alert"],button,[role="button"],input,textarea,' +
            '[contenteditable],[role="textbox"],script,style,[hidden],[aria-hidden="true"],' + threadSelector;
        const precedes = element => !!(element.compareDocumentPosition(editor) & Node.DOCUMENT_POSITION_FOLLOWING);
        const headings = Array.from(main.querySelectorAll('h1,h2,[role="heading"][aria-level="1"],[role="heading"][aria-level="2"]'))
            .filter(heading => visible(heading) && !heading.closest(excluded) && precedes(heading));
        if (!headings.length) return { Title: '', Prompt: '' };
        const primary = headings.filter(heading => heading.matches('h1,[role="heading"][aria-level="1"]'));
        // Two primary headings can indicate a still-changing or ambiguous layout.
        if (primary.length > 1) return { Title: '', Prompt: '' };
        const heading = primary[0] || headings[0];
        const title = text(heading);
        if (!title || title.length > 500) return { Title: '', Prompt: '' };
        let root = editor.parentElement;
        while (root && !root.contains(heading)) root = root.parentElement;
        if (!root || !main.contains(root)) return { Title: '', Prompt: '' };
        const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT), parts = [];
        while (walker.nextNode()) {
            const node = walker.currentNode, parent = node.parentElement;
            if (!parent || heading.contains(parent) || !visible(parent) || parent.closest(excluded)) continue;
            if (!(heading.compareDocumentPosition(node) & Node.DOCUMENT_POSITION_FOLLOWING) ||
                !(node.compareDocumentPosition(editor) & Node.DOCUMENT_POSITION_FOLLOWING)) continue;
            const value = String(node.nodeValue || '').replace(/\s+/g, ' ').trim();
            if (!value || /^(?:your reply|your response|reply|response)$/i.test(value)) continue;
            parts.push(value);
        }
        const prompt = parts.join('\n').trim();
        // Do not silently truncate instructions and generate from incomplete data.
        return prompt && prompt.length <= 10000 ? { Title: title, Prompt: prompt } : { Title: '', Prompt: '' };
    };
    context = readContext();
    // Bind AI text to the current DOM instructions in the same synchronous task
    // as focus/insert authorization/submit. A previous host-side check is not
    // enough when React can replace the instructions without changing the URL.
    if (expectedContext && (context.Title !== expectedContext.Title || context.Prompt !== expectedContext.Prompt))
        return answer('CONTEXT_CHANGED', hasDraft);
    if (!enabled(editor)) return answer('NOT_READY', hasDraft);
    if (hasDraft && norm(editorText(editor)) !== norm(reply)) return answer('EXISTING_DRAFT', true);
    // A submit must belong to this editor's form/container. Never pick the first global Reply.
    let scope = editor.parentElement, submits = [];
    while (scope && scope !== document.body && scope !== main.parentElement) {
        const otherEditors = editors.filter(item => scope.contains(item));
        const scoped = buttonsIn(scope).filter(control => /^(?:reply|post|submit(?: response| reply)?|post reply|send reply)$/.test(name(control)));
        if (scoped.length) { submits = otherEditors.length === 1 ? scoped : []; break; }
        if (scope === main || scope.tagName === 'FORM') break;
        scope = scope.parentElement;
    }
    if (!submits.length) return answer('NOT_READY', hasDraft);
    if (submits.length !== 1) return answer('AMBIGUOUS', hasDraft);
    const submit = submits[0];
    if (!hasDraft) {
        if (action === 'can-insert') return answer(window.__acoseDiscussionEditor === editor &&
            (document.activeElement === editor || editor.contains(document.activeElement)) ? 'FOCUSED' : 'FOCUS_FAILED');
        if (action === 'focus') {
            editor.focus({ preventScroll: true });
            // No selectAll/delete: preserve a draft that arrives during React hydration.
            if (document.activeElement !== editor && !editor.contains(document.activeElement)) return answer('FOCUS_FAILED');
            window.__acoseDiscussionEditor = editor;
            return answer('FOCUSED');
        }
        return answer('EMPTY');
    }
    if (!enabled(submit)) return answer('SUBMIT_NOT_READY', true);
    if (action === 'submit') {
        if (window.__acoseDiscussionSubmitAttempted) return answer('SUBMIT_PENDING', true);
        window.__acoseDiscussionSubmitAttempted = true;
        submit.click();
        return answer('SUBMIT_ATTEMPTED', true);
    }
    return answer('READY', true);
})();
