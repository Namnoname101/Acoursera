(function(args) {
    const norm = value => String(value || '').replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim().toLowerCase();
    const identity = raw => {
        try {
            const url = new URL(raw);
            if (url.protocol !== 'https:' || !/^(www\.)?coursera\.org$/i.test(url.hostname) || url.username || url.password || url.port) return null;
            const match = url.pathname.match(/^\/learn\/([^/]+)\/peer\/([^/]+)\/([^/]+)(?:\/(.*))?\/?$/);
            if (!match) return null;
            return { key: match.slice(1, 4).join('|'), review: /^(review|reviews|review-next|give-feedback)(\/|$)/.test(match[4] || ''),
                submission: /^(instructions|submit|submission|my-submission)?\/?$/.test(match[4] || '') };
        } catch (_) { return null; }
    };
    let remaining = null, completed = null, required = null, groupCount = 0, filledComments = 0, emptyCommentCount = 0;
    let reviewKey = '', submissionConfirmed = false, reviewsCompleted = false;
    let assignmentTitle = '', submissionText = '', rubricText = '', contextFingerprint = '';
    let commentPrompts = [];
    const result = (State, Detail = '') => JSON.stringify({ State, Detail, Remaining: remaining,
        Completed: completed, Required: required, GroupCount: groupCount, FilledComments: filledComments,
        EmptyCommentCount: emptyCommentCount, ReviewKey: reviewKey, SubmissionConfirmed: submissionConfirmed,
        ReviewsCompleted: reviewsCompleted, AssignmentTitle: assignmentTitle, SubmissionText: submissionText,
        RubricText: rubricText, ContextFingerprint: contextFingerprint, CommentPrompts: commentPrompts });
    const expected = identity(args.expectedUrl), actual = identity(location.href);
    if (!expected || !actual || actual.key !== expected.key) return result('STALE_DOCUMENT', 'ASSIGNMENT_MISMATCH');
    const action = args.action || 'probe';
    if (!['probe', 'start', 'fill', 'submit', 'next'].includes(action)) return result('WAIT', 'Unsupported action.');
    const hidden = el => {
        for (let p = el; p && p.nodeType === 1; p = p.parentElement) {
            const style = getComputedStyle(p);
            if (p.hidden || p.hasAttribute('inert') || p.getAttribute('aria-hidden') === 'true' ||
                style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse' ||
                Number(style.opacity) === 0) return true;
        }
        return false;
    };
    const visible = el => !!el && el.isConnected && !hidden(el) &&
        Array.from(el.getClientRects()).some(rect => rect.width > 0 && rect.height > 0);
    if (!actual.review) {
        if (!actual.submission || action !== 'probe') return result('STALE_DOCUMENT', 'NOT_REVIEW_ROUTE');
        // The sidebar must explicitly mark this assignment's review completed.
        // Redirecting to /submit alone never proves either completion or passing.
        reviewsCompleted = Array.from(document.querySelectorAll('a[href]')).filter(visible).some(link => {
            try {
                const linked = identity(new URL(link.getAttribute('href'), location.href).href);
                const label = norm(link.getAttribute('aria-label'));
                return linked?.key === actual.key && linked.review &&
                    /^review your peers,/.test(label) && /,\s*completed$/.test(label) &&
                    !!link.querySelector('[data-testid="learn-item-success-icon"]');
            } catch (_) { return false; }
        });
        return result('OWN_SUBMISSION', 'Review flow reached this assignment\'s own submission or instructions.');
    }
    const enabled = el => !el.disabled && !el.matches(':disabled') && !el.closest('[aria-disabled="true"]');
    const inContent = el => !el.closest('aside,nav,[role="navigation"],header,footer');
    const text = el => norm(el?.innerText || el?.textContent);
    const accessibleLabel = el => norm([
        el.getAttribute('aria-label'),
        ...(el.getAttribute('aria-labelledby') || '').split(/\s+/).map(id => document.getElementById(id)?.innerText || ''),
        ...Array.from(el.labels || []).map(label => label.innerText),
        el.getAttribute('placeholder'), el.innerText
    ].filter(Boolean).join(' '));
    // Coursera mounts an empty bai-StyleReset role=dialog (width>0, height=0).
    // A DOM rectangle alone does not mean a popup is showing. Keep real modal
    // children that overflow a zero-sized wrapper blocking, without class-based bypasses.
    const paintedInViewport = el => visible(el) && Array.from(el.getClientRects()).some(rect =>
        rect.width > 0 && rect.height > 0 && rect.bottom > 0 && rect.right > 0 &&
        rect.top < innerHeight && rect.left < innerWidth);
    const blockingDialogs = Array.from(document.querySelectorAll('[role="dialog"],[role="alertdialog"],[aria-modal="true"],dialog[open]'))
        .filter(el => !hidden(el) && (paintedInViewport(el) || Array.from(el.querySelectorAll('*')).some(paintedInViewport)));
    if (blockingDialogs.length) {
        const dialog = blockingDialogs[0], rect = dialog.getBoundingClientRect();
        return result('BLOCKING_DIALOG', 'Visible ' + (dialog.getAttribute('role') || dialog.tagName.toLowerCase()) +
            ' (' + Math.round(rect.width) + 'x' + Math.round(rect.height) + '). Close the popup to continue.');
    }
    const semanticMains = Array.from(document.querySelectorAll('main,[role="main"]')).filter(visible);
    // Current Coursera WebView2 markup has no semantic main/form elements.
    const mains = semanticMains.length ? semanticMains : Array.from(document.querySelectorAll('.rc-PeerItemPage')).filter(visible);
    const roots = mains.filter(el => !mains.some(other => other !== el && el.contains(other)));
    if (roots.length > 1) return result('AMBIGUOUS_FORM', 'Multiple visible content regions.');
    const root = roots[0] || document.body;
    if (document.readyState === 'loading' || root.matches('[aria-busy="true"]') ||
        Array.from(root.querySelectorAll('[aria-busy="true"]')).some(visible))
        return result('WAIT', 'The review page is still loading.');
    const contentText = (element, counterOnly = false) => {
        const parts = [], walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
        while (walker.nextNode()) {
            const el = walker.currentNode.parentElement;
            if (el && visible(el) && inContent(el) && !el.closest('script,style,textarea,input,[contenteditable="true"]') &&
                (!counterOnly || !el.closest('form,fieldset,[role="radiogroup"],article,[data-testid*="submission"],.rc-Submission,.rc-SubmissionPartView')))
                parts.push(walker.currentNode.textContent);
        }
        return norm(parts.join(' '));
    };
    const visibleText = (element, maximum) => {
        if (!element) return '';
        const parts = [], walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
        while (walker.nextNode()) {
            const el = walker.currentNode.parentElement;
            if (el && visible(el) && inContent(el) &&
                !el.closest('script,style,textarea,input,[contenteditable="true"],nav,aside,header,footer'))
                parts.push(walker.currentNode.textContent || '');
        }
        return parts.join(' ').replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim().slice(0, maximum);
    };
    const fingerprintOf = value => {
        let hash = 2166136261;
        for (let i = 0; i < value.length; i++) hash = Math.imul(hash ^ value.charCodeAt(i), 16777619);
        return (hash >>> 0).toString(16) + ':' + value.length;
    };
    const controls = Array.from(root.querySelectorAll('button,a,[role="button"]')).filter(visible).filter(inContent);
    const buttons = controls.filter(el => !controls.some(other => other !== el && el.contains(other)));
    const named = (regex, enabledOnly = true) => buttons.filter(el => !enabledOnly || enabled(el)).filter(el => regex.test(norm(el.getAttribute('aria-label') || el.innerText || el.textContent)));
    const reviewSubmit = named(/^(submit review|submit feedback|send feedback|submit your review|gửi (đánh giá|nhận xét))$/, false);
    const plainSubmit = named(/^(submit|gửi)$/, false);
    const rawRadios = Array.from(root.querySelectorAll('input[type="radio"],[role="radio"]')).filter(inContent);
    const radioVisible = el => visible(el) || (el.matches('input[type="radio"]') && !hidden(el.parentElement) &&
        Array.from(el.labels || []).some(label => visible(label) && root.contains(label) && inContent(label)));
    const radios = rawRadios.filter(radioVisible).filter(el =>
        !(!el.matches('input') && Array.from(el.querySelectorAll('input[type="radio"]')).some(radioVisible)));
    const hasRadios = radios.length > 0;
    // Completion text in the student's artifact or inside an active grading form is not proof.
    const pageText = contentText(root);
    const countNodes = Array.from(root.querySelectorAll('[data-testid="review-count"],[data-test="review-count"],[data-testid*="review-progress"]'))
        .filter(visible).filter(inContent).filter(el => !el.closest('article,[data-testid*="submission"],.rc-Submission'));
    const statusText = contentText(root, true);
    const countText = countNodes.length ? countNodes.map(el => contentText(el)).join(' ') : statusText;
    {
        const countPatterns = [
            /(?:you (?:have )?(?:completed|reviewed)|completed|reviewed)\s*(\d+)\s*(?:of|out of|\/)\s*(\d+)\s*(?:required\s*)?(?:peer\s*)?(?:reviews|assignments)/,
            /(\d+)\s*(?:of|out of|\/)\s*(\d+)\s*(?:required\s*)?(?:peer\s*)?reviews(?:\s*(?:completed|done))?/,
            /(?:reviews completed|completed reviews)\s*:?\s*(\d+)\s*(?:of|out of|\/)\s*(\d+)/
        ];
        for (const pattern of countPatterns) {
            const match = countText.match(pattern);
            if (match) { completed = Number(match[1]); required = Number(match[2]); remaining = Math.max(0, required - completed); break; }
        }
        // Coursera's terminal review page does not show "0 left". It changes the
        // summary to e.g. "Reviews 4 complete" and renders an explicit finished
        // banner instead. Retain the completed count, but only infer zero from the
        // banner as well so an intermediate "1 complete" summary cannot end early.
        const compactCompleted = countText.match(/\breviews?\s+(\d+)\s+(?:complete|completed)\b/);
        if (completed === null && compactCompleted) completed = Number(compactCompleted[1]);
        const left = countText.match(/(?:you (?:still )?need to (?:review|complete)|review)\s+(\d+)\s+more\s+(?:(?:peer\s*)?(?:assignments|reviews|peers))/) ||
            countText.match(/(\d+)\s+(?:required\s*)?(?:peer\s*)?reviews?\s+(?:left|remaining)/) ||
            countText.match(/reviews?\s*:?\s*(\d+)\s+left to complete/) ||
            (countNodes.length ? countText.match(/(\d+)\s+left to complete/) : null);
        if (left) remaining = Number(left[1]);
        const zeroLeft = remaining === 0 && (required === null || required > 0);
        // The compact counter and the terminal banner are separate siblings on
        // Coursera's current page. statusText is deliberately built with
        // counterOnly=true, so learner artifacts/forms cannot forge this signal.
        const completePhrase = /(?:you have|you've|you) completed (?:all (?:of )?(?:your |the )?|(?:your |the ))required peer reviews|(?:all (?:of )?)?(?:your |the )required peer reviews (?:are )?(?:complete|completed)|you (?:have )?reviewed (?:the|all) required (?:number of )?(?:peer )?assignments|(?:you(?:'|’)?ve|you have) finished (?:all (?:of )?)?your (?:required )?(?:peer )?reviews/.test(statusText);
        if (zeroLeft || (completePhrase && !(remaining > 0))) {
            reviewsCompleted = true;
            if (remaining === null) {
                remaining = 0;
                if (completed !== null && required === null) required = completed;
            }
            return result('REVIEWS_COMPLETE');
        }
        if (!hasRadios && /no (?:peer )?(?:assignments|submissions|reviews) (?:are )?(?:currently )?available|there (?:are no|aren't any) (?:peer )?(?:assignments|submissions|reviews) (?:available|to review)|check back later(?: to review)?|no peers? to review/.test(statusText))
            return result('NO_REVIEWS_AVAILABLE');
    }
    const memoryKey = '__acosePeerReviews_' + expected.key;
    const memory = window[memoryKey] || (window[memoryKey] = { cycle: 0, count: 0,
        clicked: new Set(), filled: '', submitted: new Set() });
    const budget = remaining ?? (Number.isInteger(args.remainingBudget) ? args.remainingBudget : null);
    if (action !== 'probe' && !(budget > 0)) return result('COUNT_REQUIRED', 'Read the remaining review count before taking an action.');
    const clickOnce = (button, state) => {
        const token = action + '|' + location.pathname + '|' + (remaining ?? args.remainingBudget) + '|' + reviewKey;
        if (memory.count >= 40) return result('ACTION_LIMIT');
        if (memory.clicked.has(token)) return result('WAIT', 'Already clicked this control.');
        memory.clicked.add(token); memory.count++;
        button.scrollIntoView({ block: 'center' }); button.click();
        return result(state);
    };
    if (!hasRadios) {
        const submitted = /(?:your (?:peer )?review|your feedback) (?:has been |was )?(?:successfully )?(?:submitted|saved)|thank you for (?:submitting your review|reviewing)|review submitted(?: successfully)?/.test(statusText);
        submissionConfirmed = submitted;
        const next = named(/^(review (?:another|next)(?: (?:assignment|submission|peer))?|next (?:assignment|review)|đánh giá bài (?:khác|tiếp theo))$/);
        if (next.length > 1) return result('AMBIGUOUS_FORM', 'Multiple next-review controls.');
        if (next.length === 1) {
            if (action === 'next') {
                // A next-review button is explicit navigation, but never resubmit the prior form.
                const response = clickOnce(next[0], 'NEXT_CLICKED');
                if (JSON.parse(response).State === 'NEXT_CLICKED') {
                    memory.cycle++; memory.filled = '';
                }
                return response;
            }
            return result('NEXT_READY');
        }
        if (submitted) return result('REVIEW_SUBMITTED');
        const starts = named(/^(start reviewing|start (?:peer )?review|review assignments|review peers|review (?:your|fellow) peers|begin reviewing|bắt đầu đánh giá|đánh giá bài tập)$/);
        if (starts.length > 1) return result('AMBIGUOUS_FORM', 'Multiple start-review controls.');
        if (starts.length === 1) return action === 'start' ? clickOnce(starts[0], 'START_CLICKED') : result('START_READY');
        if (reviewSubmit.length) return result('UNSUPPORTED_RUBRIC', 'Review form has no identifiable radio rubric.');
        return result('WAIT');
    }
    // A rubric alone is not enough: require a unique review submit control in the same form.
    const submitCandidates = reviewSubmit.length ? reviewSubmit : plainSubmit;
    if (submitCandidates.length !== 1) return result('AMBIGUOUS_FORM', 'Expected one review submit control.');
    const submit = submitCandidates[0];
    const form = submit.closest('form,.rc-SubmittedSubmissionView') || root;
    if (radios.some(el => !form.contains(el))) return result('AMBIGUOUS_FORM', 'Radio controls outside the review form.');
    const groups = new Map();
    const nodeIds = new Map();
    const nodeId = el => { if (!nodeIds.has(el)) nodeIds.set(el, nodeIds.size + 1); return nodeIds.get(el); };
    for (const radio of radios) {
        const container = radio.closest('[role="radiogroup"],fieldset');
        const name = radio.matches('input') ? radio.name : '';
        if (!container && !name) return result('UNSUPPORTED_RUBRIC', 'A radio option has no unambiguous group.');
        const key = container ? 'container:' + nodeId(container) + (name ? '|name:' + name : '') : 'name:' + name;
        if (!groups.has(key)) groups.set(key, []);
        groups.get(key).push(radio);
    }
    groupCount = groups.size;
    const selections = [];
    for (const group of groups.values()) {
        if (group.length < 2 || group.some(radio => !enabled(radio)))
            return result('UNSUPPORTED_RUBRIC', 'A rubric group is incomplete or disabled.');
        const options = group.map(radio => {
            const label = accessibleLabel(radio);
            const scores = Array.from(label.matchAll(/(-?\d+(?:[.,]\d+)?)\s*(?:points?|pts?\.?|điểm)\b|\b(?:points?|score)\s*:\s*(-?\d+(?:[.,]\d+)?)/g))
                .map(match => Number((match[1] || match[2]).replace(',', '.')));
            const unique = [...new Set(scores)];
            return { radio, label, score: unique.length === 1 ? unique[0] : null };
        });
        if (options.some(option => option.score === null || !Number.isFinite(option.score)))
            return result('UNSUPPORTED_RUBRIC', 'Each option must display one explicit numeric point score.');
        const max = Math.max(...options.map(option => option.score));
        const best = options.filter(option => option.score === max);
        if (best.length !== 1) return result('UNSUPPORTED_RUBRIC', 'Maximum score is tied or ambiguous.');
        selections.push({ best: best[0], options });
    }
    const selected = radio => radio.matches('input') ? radio.checked : radio.getAttribute('aria-checked') === 'true';
    const editors = Array.from(form.querySelectorAll('textarea,[contenteditable="true"]')).filter(visible).filter(inContent);
    const editable = editors.filter(el => enabled(el) && !el.readOnly);
    const declared = label => /honou?r|academic integrity|i (?:certify|declare|confirm|agree|acknowledge)|identity|signature|full name|email|tôi (?:cam kết|xác nhận|đồng ý)|họ và tên/.test(label);
    if (editable.some(el => declared(accessibleLabel(el)))) return result('INCOMPLETE_FORM', 'An identity or honor declaration needs manual input.');
    const unsupportedRequired = Array.from(form.querySelectorAll('input,select,textarea,[contenteditable="true"]'))
        .filter(el => visible(el) && enabled(el) && (el.required || el.getAttribute('aria-required') === 'true'))
        .filter(el => !radios.includes(el) && !editable.includes(el))
        .filter(el => el.matches('input[type="checkbox"]') ? !el.checked : !String(el.value || '').trim());
    if (unsupportedRequired.length) return result('INCOMPLETE_FORM', 'A required non-review field needs manual input.');
    // Neither arbitrary radio values nor option position is used to infer the grade.
    const submissionId = root.querySelector('[data-review-id],[data-submission-id]');
    let identityToken = submissionId?.getAttribute('data-review-id') || submissionId?.getAttribute('data-submission-id') || '';
    if (!identityToken) {
        // /review-next is reused for every learner. The uploaded artifact path,
        // unlike signed query parameters, is stable across reloads of that review.
        const parts = Array.from(form.querySelectorAll('.rc-SubmissionPartView')).filter(visible);
        const artifacts = parts.flatMap(part => Array.from(part.querySelectorAll('a[href]')).map(link => {
            try { const url = new URL(link.href); return /^https?:$/.test(url.protocol) ? url.origin + url.pathname : ''; }
            catch (_) { return ''; }
        })).filter(Boolean).sort();
        const fingerprint = artifacts.length ? artifacts.join('|') : parts.map(part => contentText(part)).join('|');
        if (fingerprint) {
            // Keep learner text, file names and signed URLs out of diagnostic state.
            let hash = 2166136261;
            for (let i = 0; i < fingerprint.length; i++) hash = Math.imul(hash ^ fingerprint.charCodeAt(i), 16777619);
            identityToken = 'artifact:' + (hash >>> 0).toString(16) + ':' + fingerprint.length;
        }
    }
    if (/\/review-next\/?$/.test(location.pathname) && !identityToken)
        return result('AMBIGUOUS_FORM', 'Cannot identify the submission on the shared review-next route.');
    const signature = location.pathname.replace(/\/+$/, '') + '|' + identityToken + '|' + selections.map(group => group.options.map(option => option.label).join('~')).join('|');
    reviewKey = signature;
    const titleNode = Array.from(root.querySelectorAll('h1,h2,[data-testid*="title"]'))
        .filter(visible).filter(inContent).find(el => !el.closest('article,[data-testid*="submission"],.rc-SubmissionPartView'));
    assignmentTitle = visibleText(titleNode, 500);
    const submissionParts = Array.from(form.querySelectorAll(
        '.rc-SubmissionPartView,[data-testid*="submission-part"],[data-test*="submission-part"]'))
        .filter(visible).filter(inContent);
    submissionText = [...new Set(submissionParts.map(part => visibleText(part, 12000)).filter(Boolean))]
        .join('\n').slice(0, 24000);
    const rubricParts = [
        ...Array.from(groups.values()).map(group => group[0].closest('[role="radiogroup"],fieldset') || group[0].parentElement),
        ...editable.map(editor => editor.closest('fieldset,[data-testid*="rubric"],.rc-RubricQuestion') || editor.parentElement)
    ].filter(Boolean);
    rubricText = [...new Set(rubricParts.map(part => visibleText(part, 4000)).filter(Boolean))]
        .join('\n').slice(0, 16000);
    commentPrompts = editable.map((editor, index) => {
        const label = accessibleLabel(editor);
        const nearby = editor.closest('fieldset,[data-testid*="rubric"],.rc-RubricQuestion') || editor.parentElement;
        const prompt = (label || visibleText(nearby, 1000) || ('Comment ' + (index + 1))).slice(0, 1000);
        const maximum = Math.min(Number(editor.getAttribute('maxlength')) || 2000, 2000);
        return (prompt + '\nMaximum ' + maximum + ' characters.').slice(0, 1200);
    });
    const contextSource = [reviewKey, assignmentTitle, submissionText, rubricText, ...commentPrompts].join('\n\u241f\n');
    contextFingerprint = fingerprintOf(contextSource);
    if (memory.submitted.has(signature)) return result('SUBMIT_PENDING', 'Waiting for confirmation of the submitted review.');
    const valueOf = el => String(el.matches('textarea') ? el.value : el.textContent || '').trim();
    const scoresReady = selections.every(group => selected(group.best.radio) && group.options.filter(option => selected(option.radio)).length === 1);
    const commentsReady = editable.every(el => !!valueOf(el));
    filledComments = editable.filter(el => !!valueOf(el)).length;
    emptyCommentCount = editable.length - filledComments;
    if (!scoresReady || !commentsReady) {
        memory.filled = '';
        if (memory.fillSession) memory.fillSession.verified = '';
    }
    if (action === 'fill') {
        const suppliedComments = Array.isArray(args.reviewComments) ? args.reviewComments : null;
        if (emptyCommentCount > 0 && (!suppliedComments || suppliedComments.length !== editable.length ||
            args.reviewContext !== contextFingerprint))
            return result(args.reviewContext && args.reviewContext !== contextFingerprint
                ? 'STALE_REVIEW_CONTEXT' : 'AI_COMMENTS_REQUIRED',
                'A context-matched AI comment plan is required before editing this form.');
        if (suppliedComments && suppliedComments.some((value, index) => {
            const text = typeof value === 'string' ? value.trim() : '';
            const maximum = Number(editable[index]?.getAttribute('maxlength')) || 4000;
            return !text || text.length > Math.min(maximum, 4000) || /[\u0000-\u0008\u000b\u000c\u000e-\u001f]/.test(text);
        })) return result('INCOMPLETE_FORM', 'The AI comment plan does not fit the visible fields.');
        // React may replace the whole form after any input. Mutate ONE field per
        // invocation, then return to the host event loop and query fresh DOM next time.
        // Never validate the references captured before a click/input dispatch.
        memory.filled = '';
        const now = Date.now();
        const shape = signature + '|' + editable.map(accessibleLabel).join('|');
        if (!memory.fillSession || memory.fillSession.shape !== shape)
            memory.fillSession = { shape, firstSeen: now, lastAction: 0, attempts: {}, verified: '', verifiedAt: 0 };
        const session = memory.fillSession;
        if (now - session.firstSeen < 1000)
            return result('FORM_FILLING', 'Waiting for the review form to settle.');
        if (now - session.lastAction < 750)
            return result('FORM_FILLING', 'Waiting for the page to accept the previous field.');
        const attempt = key => {
            if ((session.attempts[key] || 0) >= 3) return false;
            session.attempts[key] = (session.attempts[key] || 0) + 1;
            session.lastAction = now;
            session.verified = '';
            return true;
        };
        const missingGroup = selections.findIndex(group => !selected(group.best.radio) ||
            group.options.filter(option => selected(option.radio)).length !== 1);
        if (missingGroup >= 0) {
            const radio = selections[missingGroup].best.radio;
            if (!radio.isConnected) return result('FORM_FILLING', 'The rubric is being redrawn.');
            if (!attempt('radio:' + missingGroup))
                return result('INCOMPLETE_FORM', 'The page did not retain rubric group ' + (missingGroup + 1) + ' after 3 spaced attempts.');
            radio.scrollIntoView({ block: 'center' });
            radio.click();
            return result('FORM_FILLING', 'Waiting for rubric group ' + (missingGroup + 1) + ' to be retained.');
        }
        const missingEditor = editable.findIndex(editor => !valueOf(editor));
        if (missingEditor >= 0) {
            const editor = editable[missingEditor];
            if (!editor.isConnected) return result('FORM_FILLING', 'The comment fields are being redrawn.');
            if (!attempt('comment:' + missingEditor))
                return result('INCOMPLETE_FORM', 'The page did not retain comment ' + (missingEditor + 1) + ' after 3 spaced attempts.');
            const comment = suppliedComments[missingEditor].trim();
            if (editor.matches('textarea')) {
                const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')?.set;
                if (!setter) return result('INCOMPLETE_FORM', 'Textarea setter is unavailable.');
                setter.call(editor, comment);
            } else editor.textContent = comment;
            editor.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: comment }));
            if (editor.isConnected) editor.dispatchEvent(new Event('change', { bubbles: true }));
            filledComments++;
            return result('FORM_FILLING', 'Waiting for comment ' + (missingEditor + 1) + ' to be retained.');
        }
        const verified = signature + '|' + editable.map(valueOf).join('|');
        if (session.verified !== verified) { session.verified = verified; session.verifiedAt = now; }
        if (now - session.verifiedAt < 750)
            return result('FORM_FILLING', 'Verifying that the current form retains all scores and comments.');
        memory.filled = signature;
        return result('FORM_FILLED');
    }
    if (action === 'submit') {
        if (!enabled(submit)) return result('FORM_FILLING', 'Waiting for Submit Review to become enabled.');
        if (memory.filled !== signature || !scoresReady || !commentsReady) return result('INCOMPLETE_FORM', 'Fill and verify this rubric before submitting.');
        if (Array.from(form.querySelectorAll('input,select,textarea')).filter(visible).some(el => enabled(el) && el.willValidate && !el.checkValidity()))
            return result('INCOMPLETE_FORM', 'The browser reports an invalid field.');
        const response = clickOnce(submit, 'SUBMIT_CLICKED');
        if (JSON.parse(response).State === 'SUBMIT_CLICKED') memory.submitted.add(signature);
        return response;
    }
    return result(memory.filled === signature && scoresReady && commentsReady ? 'SUBMIT_READY' : 'FORM_READY');
})(__PEER_REVIEW_ARGUMENTS__);
