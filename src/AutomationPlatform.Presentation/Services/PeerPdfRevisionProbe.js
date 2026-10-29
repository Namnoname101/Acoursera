(function(args) {
    'use strict';
    // All page strings are untrusted data. This probe never uploads, submits,
    // changes grading mode, follows links or opens another learner's work.
    const norm = value => String(value || '').replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim();
    const lower = value => norm(value).toLowerCase();
    const identity = raw => {
        try {
            const url = new URL(raw);
            if (url.protocol !== 'https:' || !/^(www\.)?coursera\.org$/i.test(url.hostname) || url.username || url.password || url.port) return '';
            const parts = url.pathname.split('/').filter(Boolean);
            if (parts.length < 5 || parts.length > 6 || parts[0] !== 'learn' || parts[2] !== 'peer') return '';
            if (parts.length === 6 && !/^(instructions|submit|submission|my-submission)$/i.test(parts[5])) return '';
            return parts.slice(1, 5).join('|');
        } catch (_) { return ''; }
    };
    const data = { Title: '', FileName: '', Prompt: '', Rubric: '', Feedback: '', SnapshotToken: '', DialogToken: '', CanStartAttempt: false,
        EarnedPoints: null, PassingPoints: null, TotalPoints: null };
    const result = (State, Detail = '') => JSON.stringify({ State, Detail, ...data });
    const expected = identity(args.expectedUrl);
    if (window !== window.top || !expected || identity(location.href) !== expected) return result('STALE_DOCUMENT');
    if (!['inspect', 'startAttempt', 'confirmAttempt', 'removePreviousFile'].includes(args.action)) return result('INVALID_ACTION');
    const rendered = (el, allowModalBackground = false) => {
        if (!el?.isConnected || !el.getClientRects().length) return false;
        for (let p = el; p; p = p.parentElement) {
            const s = getComputedStyle(p);
            if (p.hidden || (!allowModalBackground && (p.hasAttribute('inert') || p.getAttribute('aria-hidden') === 'true')) || s.display === 'none' ||
                /^(hidden|collapse)$/.test(s.visibility) || Number(s.opacity) === 0) return false;
            if (p.tagName === 'DETAILS' && !p.open && !p.querySelector('summary')?.contains(el)) return false;
        }
        return true;
    };
    const visible = el => rendered(el);
    const excluded = 'script,style,noscript,template,textarea,input,[contenteditable="true"],aside,nav,header,footer,' +
        '[role="navigation"],[class*="Sidebar"],[class*="sidebar"],[data-ai-instructions],[data-assessment-checkpoint]';
    const content = el => visible(el) && !el.closest(excluded);
    const enabled = el => !el.disabled && el.getAttribute('aria-disabled') !== 'true';
    const label = el => lower(el.getAttribute('aria-label') || el.innerText || el.textContent);
    const fileName = value => {
        const name = norm(value);
        return /^[^\\/\x00<>:"|?*\r\n]{1,180}\.pdf$/i.test(name) ? name : '';
    };
    const dialogSelector = '[role="dialog"],[role="alertdialog"],[aria-modal="true"],dialog[open]';
    const dialogNodes = Array.from(document.querySelectorAll(dialogSelector)).filter(visible);
    // Libraries may put aria-modal on a wrapper around role=dialog. One nested
    // chain is one modal; sibling modal branches remain ambiguous.
    const dialogLeaves = dialogNodes.filter(el => !dialogNodes.some(other => other !== el && el.contains(other)));
    if (dialogNodes.length) {
        const blocked = detail => result('BLOCKING_DIALOG', detail || 'The visible dialog is not the recognized new-attempt confirmation.');
        if (dialogLeaves.length !== 1) return blocked('Multiple visible dialogs.');
        const dialog = dialogNodes.find(el => !dialogNodes.some(other => other !== el && other.contains(el)));
        const unsafeDialog = excluded + ',.rc-SubmissionPartView,.rc-SubmissionPart,.rc-CML,.cml-viewer,article,blockquote,' +
            '[data-testid*="rubric" i],[class*="Rubric"],[class*="rubric"],.rc-OptionFeedbackTable,[class*="Comment"],[data-testid*="comment" i]';
        if (dialog.closest(unsafeDialog)) return blocked();
        const canonical = text => norm(text).replace(/[\u2018\u2019]/g, "'");
        const heading = 'Ready to start a new attempt?';
        const warning = "Your new attempt will clear your previous submission and AI grade. If you cancel and don't attempt again, what you've submitted will count towards your final grade.";
        const dialogText = (el, omit = '') => {
            const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT), pieces = [];
            while (walker.nextNode()) {
                const parent = walker.currentNode.parentElement;
                if (parent && visible(parent) && !parent.closest(excluded + ',svg' + (omit ? ',' + omit : '')))
                    pieces.push(walker.currentNode.textContent);
            }
            return canonical(pieces.join(' '));
        };
        const headingNodes = Array.from(dialog.querySelectorAll('h1,h2,h3,h4,h5,h6,[role="heading"]')).filter(visible);
        const labelledHeading = dialogNodes.map(el => el.getAttribute('aria-labelledby') || '').join(' ').split(/\s+/).filter(Boolean)
            .map(id => document.getElementById(id)).filter(el => el && dialog.contains(el) && visible(el));
        // Coursera points aria-labelledby at a heading-group wrapper that contains
        // the real H2. Treat that wrapper and its semantic heading as one label;
        // keep unrelated labelled nodes so ambiguous/mismatched dialogs still fail.
        const labelledHeadingLeaves = labelledHeading.filter(el => {
            const nestedHeadings = headingNodes.filter(headingNode => headingNode !== el && el.contains(headingNode));
            return nestedHeadings.length !== 1 || dialogText(el) !== dialogText(nestedHeadings[0]);
        });
        const headings = Array.from(new Set([...headingNodes, ...labelledHeadingLeaves]));
        if (headings.length !== 1 || dialogText(headings[0]) !== heading ||
            dialogText(dialog, 'button,[role="button"]') !== heading + ' ' + warning) return blocked();
        const allButtons = Array.from(dialog.querySelectorAll('button,[role="button"]')).filter(visible);
        const buttons = allButtons.filter(el => !allButtons.some(other => other !== el && el.contains(other)));
        if (buttons.some(el => el.matches('a,[type="submit"]') || !/^(continue|cancel|close|dismiss|×|✕|x)$/.test(label(el)))) return blocked();
        const continues = buttons.filter(el => label(el) === 'continue');
        const cancels = buttons.filter(el => label(el) === 'cancel');
        if (continues.length !== 1 || cancels.length !== 1) return blocked('Confirmation actions are ambiguous.');

        // aria-hidden/inert on the covered application is normal modal behavior.
        // Read only known background attachment labels; never unhide the DOM or
        // accept arbitrary PDF-looking text from a prompt, rubric or dialog.
        const background = el => rendered(el, true) && !el.closest(excluded) && !dialogNodes.some(node => node.contains(el));
        const candidates = Array.from(document.querySelectorAll('main,[role="main"],.rc-PeerItemPage')).filter(background);
        const roots = candidates.filter(el => !candidates.some(other => other !== el && other.contains(el)));
        if (roots.length !== 1) return blocked('No single own-submission background was found.');
        const root = roots[0];
        const tabs = Array.from(root.querySelectorAll('button,a,[role="tab"]')).filter(background)
            .filter(el => /^(my submissions?|bài nộp của tôi)$/.test(label(el)));
        const roleTabs = tabs.filter(el => el.getAttribute('role') === 'tab');
        const tab = roleTabs.length === 1 ? roleTabs[0] : tabs.length === 1 ? tabs[0] : null;
        const suffix = new URL(location.href).pathname.split('/').filter(Boolean)[5] || '';
        if (tabs.length > 1 && !tab || tab?.getAttribute('aria-selected') === 'false' ||
            /^(instructions)$/i.test(suffix) || !suffix && tab?.getAttribute('aria-selected') !== 'true')
            return blocked('My submission is not the active assignment result.');
        const panelId = tab?.getAttribute('aria-controls');
        const panel = panelId ? document.getElementById(panelId) : null;
        if (panel && (!root.contains(panel) || !background(panel))) return blocked('The submission panel is not available.');
        const scope = panel || root;
        const resultOmit = excluded + ',.rc-SubmissionPartView,.rc-SubmissionPart,.rc-CML,.cml-viewer,article,blockquote,' +
            '[data-testid*="artifact" i],[data-testid*="submission-part" i],[data-testid*="rubric" i],' +
            '[class*="Rubric"],[class*="rubric"],.rc-OptionFeedbackTable,[class*="Comment"],[data-testid*="comment" i],' +
            '[role="tablist"],button,[role="button"]';
        const resultWalker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT), resultPieces = [];
        while (resultWalker.nextNode()) {
            const parent = resultWalker.currentNode.parentElement;
            if (parent && background(parent) && !parent.closest(resultOmit)) resultPieces.push(resultWalker.currentNode.textContent);
        }
        const resultText = norm(resultPieces.join(' '));
        const numeric = resultText.match(/\byou earned\s+(\d+(?:[.,]\d+)?)\s+points?,?\s+but you need to earn at least\s+(\d+(?:[.,]\d+)?)\s*\/\s*(\d+(?:[.,]\d+)?)\s+points? to pass\b/i);
        const points = numeric?.slice(1).map(value => Number(value.replace(',', '.')));
        const below = points && points[2] > 0 && points[0] >= 0 && points[0] <= points[2] && points[1] > 0 && points[1] <= points[2] && points[0] < points[1];
        const failed = below || /\b(?:you did not pass|you haven't passed|not passed|failed)\b|chưa đạt/i.test(resultText);
        if (!failed) return blocked('The covered result does not establish a failed assignment.');
        if (below) [data.EarnedPoints, data.PassingPoints, data.TotalPoints] = points;
        const excludedFile = 'table,.rc-OptionFeedbackTable,[class*="Rubric"],[class*="rubric"],blockquote,article,.rc-CML,.cml-viewer,' +
            '.rc-Prompt,[data-testid="submission-prompt"],[data-testid*="comment" i],[class*="Comment"]';
        const names = new Set();
        for (const el of Array.from(scope.querySelectorAll('a[href],[download],[data-testid*="attachment" i],[data-testid*="uploaded-file" i],' +
            '[data-testid="embedded-url-block-filename"],a[data-testid="embedded-url-block-link"],.rc-FileUploadItem [title],.uppy-Dashboard-Item-name'))
            .filter(background).filter(el => el.matches('[data-testid="embedded-url-block-filename"],a[data-testid="embedded-url-block-link"]') ||
                !el.closest(excludedFile))) {
            const name = fileName(el.getAttribute('download')) || fileName(el.getAttribute('title')) || fileName(el.textContent);
            if (name) names.add(name);
        }
        if (names.size !== 1) return blocked('No single submitted PDF attachment is identifiable behind the confirmation.');
        data.FileName = Array.from(names)[0];
        data.DialogToken = JSON.stringify([expected, data.FileName, dialogText(dialog), heading + ' ' + warning, resultText]);
        if (args.expectedFileName && data.FileName !== args.expectedFileName) return result('FILE_MISMATCH');
        if (args.action !== 'inspect' && args.action !== 'confirmAttempt')
            return result(enabled(continues[0]) && enabled(cancels[0]) ? 'ATTEMPT_CONFIRM_READY' : 'ATTEMPT_CONFIRM_WAIT');
        if (args.action === 'confirmAttempt') {
            if (!args.expectedFileName || data.FileName !== args.expectedFileName) return result('FILE_MISMATCH');
            if (!args.expectedDialogToken || data.DialogToken !== args.expectedDialogToken) return result('DIALOG_CHANGED');
            const key = '__acosePeerRevisionConfirmed_' + expected;
            if (window[key] === data.DialogToken) return result('ATTEMPT_CONFIRM_ALREADY_CLICKED');
            if (!enabled(continues[0]) || !enabled(cancels[0])) return result('ATTEMPT_CONFIRM_WAIT');
            window[key] = data.DialogToken;
            continues[0].click();
            return result('ATTEMPT_CONFIRM_CLICKED');
        }
        return result(enabled(continues[0]) && enabled(cancels[0]) ? 'ATTEMPT_CONFIRM_READY' : 'ATTEMPT_CONFIRM_WAIT');
    }
    if (args.action === 'confirmAttempt') return result('DIALOG_GONE', 'The new-attempt confirmation is no longer open.');
    const candidates = Array.from(document.querySelectorAll('main,[role="main"],.rc-PeerItemPage')).filter(content);
    const roots = candidates.filter(el => !candidates.some(other => other !== el && other.contains(el)));
    if (roots.length > 1) return result('CONTEXT_INCOMPLETE', 'Multiple visible assignment regions.');
    const root = roots[0] || document.body;
    const allControls = Array.from(root.querySelectorAll('button,a,[role="button"],[role="tab"]')).filter(content);
    const controls = allControls.filter(el => !allControls.some(other => other !== el && el.contains(other)));
    const tabs = controls.filter(el => /^(my submissions?|bài nộp của tôi)$/.test(label(el)));
    const roleTabs = tabs.filter(el => el.getAttribute('role') === 'tab');
    const tab = roleTabs.length === 1 ? roleTabs[0] : tabs.length === 1 ? tabs[0] : null;
    const panelId = tab?.getAttribute('aria-controls');
    const panel = panelId ? document.getElementById(panelId) : null;
    if (tabs.length > 1 && !tab) return result('CONTEXT_INCOMPLETE', 'My submission tab is ambiguous.');
    if (tab?.getAttribute('aria-selected') === 'false' || (panel && !visible(panel))) return result('WAIT', 'My submission is not selected.');
    const suffix = new URL(location.href).pathname.split('/').filter(Boolean)[5] || '';
    if ((!suffix || /^instructions$/i.test(suffix)) && tab?.getAttribute('aria-selected') !== 'true')
        return result('WAIT', 'The own-submission result is not active.');
    const scope = panel && content(panel) ? panel : root;
    const inScope = el => scope.contains(el) && content(el);
    if (scope.matches('[aria-busy="true"]') || Array.from(scope.querySelectorAll('[aria-busy="true"],[role="progressbar"]')).some(content))
        return result('WAIT', 'The submission result is still loading.');
    const read = (el, omit = '') => {
        const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT), pieces = [];
        while (walker.nextNode()) {
            const node = walker.currentNode, parent = node.parentElement;
            if (!parent || !content(parent) || (omit && parent.closest(omit))) continue;
            const text = norm(node.textContent);
            if (text) pieces.push(text);
        }
        return pieces;
    };
    data.Title = Array.from(root.querySelectorAll('[data-testid="header-title"],.assignment-title,h1'))
        .filter(content).map(el => norm(el.innerText)).find(Boolean) || '';
    const names = new Set();
    // File links are preferred, but the current Coursera result sometimes renders
    // the filename as plain text. Only a whole text node ending in .pdf is accepted.
    for (const el of Array.from(scope.querySelectorAll('a,[download],[title],[data-testid*="attachment" i],[data-testid*="uploaded-file" i]')).filter(inScope)) {
        if (el.closest('table,.rc-OptionFeedbackTable,[class*="Rubric"],[class*="rubric"],blockquote,article')) continue;
        const name = fileName(el.getAttribute('download')) || fileName(el.getAttribute('title')) || fileName(el.innerText);
        if (name) names.add(name);
    }
    const pieces = read(scope, 'button,[role="button"],[role="tab"],[role="tablist"]');
    for (const piece of read(scope, 'table,.rc-OptionFeedbackTable,[class*="Rubric"],[class*="rubric"],blockquote,article,button,[role="button"],[role="tab"]')) {
        const name = fileName(piece);
        if (name) names.add(name);
    }
    if (names.size === 1) data.FileName = Array.from(names)[0];
    const addButtons = controls.filter(inScope).filter(enabled).filter(el => /^(add file|thêm tệp|thêm tập tin)$/.test(label(el)));
    const fileInputs = Array.from(scope.querySelectorAll('input[type="file"]')).filter(el => enabled(el) &&
        !el.hasAttribute('webkitdirectory') && !el.hasAttribute('directory') && visible(el.parentElement));
    const uploadRemovals = controls.filter(inScope).filter(enabled).filter(el =>
        el.matches('button,[role="button"]') && !el.matches('a,[type="submit"]') &&
        /^(remove(?: uploaded)? file|remove attachment|delete file|xóa tệp|xóa tập tin)$/i.test(label(el)));
    if (addButtons.length || fileInputs.length || uploadRemovals.length) {
        if (args.action === 'removePreviousFile') {
            if (names.size !== 1 || !args.expectedFileName || data.FileName !== args.expectedFileName)
                return result('FILE_MISMATCH', 'Only the exact previous failed attachment may be replaced.');
            if (addButtons.length > 1 || fileInputs.length > 1 || fileInputs.some(el => el.files?.length))
                return result('CONTEXT_INCOMPLETE', 'Multiple or newly selected upload files.');
            const containers = Array.from(scope.querySelectorAll('.rc-FileUploadItem,.uppy-Dashboard-Item,[data-testid="uploaded-file"],[data-testid="attachment"]'))
                .filter(inScope).filter(el => read(el).some(text => fileName(text) === data.FileName));
            // The current Coursera form exposes only an accessible
            // "Remove uploaded file" button. Its wrapper has no stable class/test id,
            // so accept the narrowest ancestor that owns that exact button and the
            // exact saved PDF text node, never the whole form/page.
            for (const button of uploadRemovals) {
                for (let node = button.parentElement, depth = 0; node && node !== scope && depth < 8; node = node.parentElement, depth++) {
                    if (read(node).some(text => fileName(text) === data.FileName) &&
                        uploadRemovals.filter(other => node.contains(other)).length === 1) {
                        containers.push(node);
                        break;
                    }
                }
            }
            const uniqueContainers = Array.from(new Set(containers));
            const removals = controls.filter(inScope).filter(enabled).filter(el =>
                el.matches('button,[role="button"]') && !el.matches('a,[type="submit"]') &&
                (/^(remove(?: uploaded)? file|remove attachment|delete file|xóa tệp|xóa tập tin)$/i.test(label(el)) ||
                    label(el) === lower('Remove ' + data.FileName) || label(el) === lower('Remove file ' + data.FileName)) &&
                uniqueContainers.some(container => container.contains(el)));
            if (removals.length !== 1) return result('REPLACE_CONTROL_UNKNOWN', 'No single removal control attached to the previous file.');
            const removalKey = '__acosePeerRevisionRemoved_' + expected;
            if (window[removalKey] === data.FileName) return result('REPLACE_ALREADY_CLICKED');
            window[removalKey] = data.FileName;
            removals[0].click();
            return result('PREVIOUS_FILE_REMOVED');
        }
        if (names.size || fileInputs.some(el => el.files?.length)) return result('FORM_RETAINS_FILE', 'The new attempt still contains a file; no file was removed or overwritten.');
        if (addButtons.length > 1 || fileInputs.length > 1) return result('CONTEXT_INCOMPLETE', 'Multiple PDF upload prompts.');
        return result('FORM_READY');
    }
    if (args.action === 'removePreviousFile') return result('NOT_EDITABLE', 'Removal is permitted only in the new attempt form.');
    const nonResult = '.rc-SubmissionPartView,.rc-SubmissionPart,.rc-CML,.cml-viewer,article,blockquote,' +
        '[data-testid*="artifact" i],[data-testid*="submission-part" i],[data-testid*="rubric" i],' +
        '[class*="Rubric"],[class*="rubric"],.rc-OptionFeedbackTable,[class*="Comment"],[data-testid*="comment" i],[role="tablist"]';
    const resultText = norm(read(root, nonResult).join(' '));
    const numeric = resultText.match(/\byou earned\s+(\d+(?:[.,]\d+)?)\s+points?,?\s+but you need to earn at least\s+(\d+(?:[.,]\d+)?)\s*\/\s*(\d+(?:[.,]\d+)?)\s+points? to pass\b/i);
    const points = numeric?.slice(1).map(value => Number(value.replace(',', '.')));
    const below = points && points[2] > 0 && points[0] >= 0 && points[0] <= points[2] && points[1] > 0 && points[1] <= points[2] && points[0] < points[1];
    const failed = below || /\b(?:you did not pass|you haven't passed|not passed|failed)\b|chưa đạt/i.test(resultText);
    if (!failed) return result('NOT_FAILED', 'No current failed submission was established.');
    if (below) [data.EarnedPoints, data.PassingPoints, data.TotalPoints] = points;
    const failedSentence = below ? norm(numeric[0]) : resultText.match(/(?:[^.!?]*\b(?:you did not pass|you haven't passed|not passed|failed)\b[^.!?]*|[^.!?]*chưa đạt[^.!?]*)/i)?.[0] || '';
    const promptIndices = pieces.map((text, i) => /^prompt\s+\d+\s*:?$/i.test(text) ? i : -1).filter(i => i >= 0);
    const rubricIndex = pieces.findIndex(text => /^(rubric|grading rubric|tiêu chí chấm điểm)\s*:?$/i.test(text));
    if (promptIndices.length === 1 && rubricIndex > promptIndices[0]) {
        data.Prompt = pieces.slice(promptIndices[0] + 1, rubricIndex).filter(text => text !== data.FileName).join('\n');
        let end = pieces.findIndex((text, i) => i > rubricIndex && /^(comments|information\s*:|if you're unsatisfied with your scores)/i.test(text));
        if (end < 0) end = pieces.length;
        data.Rubric = pieces.slice(rubricIndex + 1, end).join('\n');
    }
    // Some layouts put column headers before both columns. Use explicit submission
    // prompt/rubric regions where available, never an entire page as a prompt.
    if (!data.Prompt) {
        const prompts = Array.from(scope.querySelectorAll('[data-testid="submission-prompt"],.rc-SubmissionPartView .rc-Prompt,.rc-SubmissionPart .rc-Prompt'))
            .filter(inScope);
        if (prompts.length === 1) data.Prompt = read(prompts[0]).join('\n');
    }
    if (!data.Rubric) {
        const tables = Array.from(scope.querySelectorAll('.rc-OptionFeedbackTable')).filter(inScope);
        data.Rubric = tables.map(el => read(el).join('\n')).filter(Boolean).join('\n\n');
    }
    const awarded = [];
    for (const table of Array.from(scope.querySelectorAll('.rc-OptionFeedbackTable')).filter(inScope)) {
        const selected = Array.from(table.querySelectorAll('[aria-selected="true"],[aria-checked="true"],input:checked'));
        for (const el of selected) {
            // Coursera currently mounts each rubric option as a DIV directly below
            // TBODY. The checked radio's LABEL is intentionally textless, while a
            // sibling contains the awarded points and description. Find the narrowest
            // ancestor that owns exactly one radio and one points display; retain the
            // semantic-row fallback for older table markup.
            let row = el.closest('tr,[role="row"]');
            if (!row || !table.contains(row) || !read(row).length) {
                row = null;
                for (let node = el.parentElement; node && node !== table; node = node.parentElement) {
                    if (node.querySelectorAll('input[type="radio"]').length === 1 &&
                        node.querySelectorAll('.rc-FeedbackPointsDisplay').length === 1 &&
                        node.querySelector('.rc-CML,[data-testid="cml-viewer"]')) {
                        row = node;
                        break;
                    }
                }
            }
            if (row && table.contains(row) && content(row)) {
                const part = table.closest('.rc-DisplayPart,.rc-OptionsDisplayPart');
                const criterion = part ? Array.from(part.querySelectorAll('.rc-CML,[data-testid="cml-viewer"]'))
                    .filter(node => !table.contains(node) && content(node)).map(node => read(node).join(' ')).find(Boolean) || '' : '';
                const option = read(row).join(' ');
                if (option) awarded.push((criterion ? criterion + ': ' : '') + option);
            }
        }
    }
    const feedbackNodes = Array.from(scope.querySelectorAll('[data-testid="grader-feedback"],[data-testid="submission-feedback"],.rc-ReviewerComment,[data-testid="reviewer-comment"]')).filter(inScope);
    data.Feedback = [norm(failedSentence), ...feedbackNodes.map(el => read(el).join('\n')),
        ...Array.from(new Set(awarded)).map(text => 'Explicit selected rubric option: ' + text)].filter(Boolean).join('\n\n');
    if (names.size !== 1) return result('CONTEXT_INCOMPLETE', names.size ? 'Multiple submitted PDF files.' : 'No submitted PDF filename was found.');
    if (!data.Title || !data.Prompt || !data.Rubric) return result('CONTEXT_INCOMPLETE', 'The assignment prompt or rubric has not been read in full.');
    if (data.Title.length > 500 || data.Prompt.length > 24000 || data.Rubric.length > 48000 || data.Feedback.length > 24000)
        return result('CONTEXT_INCOMPLETE', 'The result context exceeds safe limits; no text was truncated.');
    // Exact, non-hashed content equality intentionally avoids collision concerns.
    // The host can hash this token for its durable journal. Never show this to users.
    data.SnapshotToken = JSON.stringify([expected, data.FileName, data.Title, data.Prompt, data.Rubric, data.Feedback]);
    const starts = controls.filter(inScope).filter(enabled).filter(el =>
        /^(start new attempt|bắt đầu lần (?:nộp|thử) mới)$/.test(label(el)) && !el.matches('a,[type="submit"]'));
    data.CanStartAttempt = starts.length > 0;
    if (args.action === 'inspect') return result('FAILED_READY');
    if (!args.expectedFileName || data.FileName !== args.expectedFileName) return result('FILE_MISMATCH');
    if (!args.expectedSnapshotToken || data.SnapshotToken !== args.expectedSnapshotToken) return result('SNAPSHOT_CHANGED');
    if (!starts.length) return result('WAIT', 'Start new attempt is not ready.');
    const key = '__acosePeerRevisionStarted_' + expected;
    if (window[key] === data.SnapshotToken) return result('ATTEMPT_ALREADY_CLICKED');
    // Top and bottom copies of Coursera's Start new attempt are the same action.
    // A single dispatch is sufficient; the host waits for a clean editable form.
    window[key] = data.SnapshotToken;
    starts[0].click();
    return result('NEW_ATTEMPT_CLICKED');
})(__PEER_REVISION_ARGUMENTS__);
