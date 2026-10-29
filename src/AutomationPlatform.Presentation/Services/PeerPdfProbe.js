(function(args) {
    const norm = text => String(text || '').replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim().toLowerCase();
    const identity = raw => {
        try {
            const u = new URL(raw);
            if (u.protocol !== 'https:' || !/^(www\.)?coursera\.org$/i.test(u.hostname) || u.username || u.password || u.port) return '';
            const p = u.pathname.split('/').filter(Boolean);
            if (p.length < 5 || p.length > 6 || p[0] !== 'learn' || p[2] !== 'peer') return '';
            if (p.length === 6 && !/^(instructions|submit|submission|my-submission)$/i.test(p[5])) return '';
            return [p[1], p[3], p[4]].join('|');
        } catch (_) { return ''; }
    };
    const expected = identity(args.expectedUrl);
    const result = (State, Detail = '') => JSON.stringify({ State, Title: title, Detail });
    let title = '';
    if (!expected || identity(location.href) !== expected) return result('STALE_DOCUMENT');
    const hidden = el => {
        for (let p = el; p && p.nodeType === 1; p = p.parentElement) {
            const s = getComputedStyle(p);
            if (p.hidden || p.hasAttribute('inert') || p.getAttribute('aria-hidden') === 'true' ||
                s.display === 'none' || s.visibility === 'hidden' || s.visibility === 'collapse') return true;
        }
        return false;
    };
    const visible = el => !!el && el.isConnected && el.getClientRects().length > 0 && !hidden(el);
    const enabled = el => !el.disabled && el.getAttribute('aria-disabled') !== 'true';
    // Uppy mounts a second input for folder uploads. It is not a competing PDF prompt.
    const filePicker = el => enabled(el) && !el.hasAttribute('webkitdirectory') && !el.hasAttribute('directory');
    const inContent = el => !el.closest('aside,nav,[role="navigation"],header,footer');
    const controls = Array.from(document.querySelectorAll('button,a,[role="button"],[role="tab"]')).filter(visible).filter(inContent);
    const label = el => norm(el.getAttribute('aria-label') || el.innerText || el.textContent);
    const markInput = input => {
        document.querySelectorAll('[data-acose-peer-upload]').forEach(el => el.removeAttribute('data-acose-peer-upload'));
        input.setAttribute('data-acose-peer-upload', 'true');
        return result('FILE_INPUT_READY');
    };
    // Add File can open an HTML Uppy dialog, not the operating system chooser.
    // The dialog is often rendered outside main and makes main aria-hidden.
    const dialogCandidates = Array.from(document.querySelectorAll(
        '[role="dialog"],[role="alertdialog"],[aria-modal="true"],dialog[open],.uppy-Dashboard--modal .uppy-Dashboard-inner'))
        .filter(visible);
    const dialogs = dialogCandidates.filter(el => !dialogCandidates.some(other => other !== el && el.contains(other)));
    if (dialogs.length) {
        const owned = dialogs.filter(el => {
            const heading = norm(el.getAttribute('aria-label') || Array.from(el.querySelectorAll('h1,h2,h3,[role="heading"],.uppy-Dashboard-title')).map(h => h.innerText).join(' '));
            return /^(upload files?|tải (tệp|tập tin) lên)$/.test(heading) ||
                (/^upload files?\b/.test(norm(el.innerText)) && /browse files?|drop files? here/.test(norm(el.innerText))) ||
                (el.matches('.uppy-Dashboard-inner') && !!el.closest('.uppy-Dashboard--modal'));
        });
        if (owned.length !== 1 || dialogs.length !== 1) return result(owned.length > 1 ? 'AMBIGUOUS_UPLOAD' : 'BLOCKING_DIALOG');
        const dialog = owned[0];
        const statusScope = dialog.parentElement?.closest('[role="dialog"],[aria-modal="true"],dialog[open]') || dialog;
        const dialogText = norm(statusScope.innerText);
        const dialogInputs = Array.from(dialog.querySelectorAll('input[type="file"]')).filter(filePicker);
        const dialogControls = Array.from(dialog.querySelectorAll('button,a,[role="button"],.uppy-Dashboard-browse')).filter(visible).filter(enabled);
        const leafControls = dialogControls.filter(el => !dialogControls.some(other => other !== el && el.contains(other)));
        const browse = leafControls.filter(el => /^(browse files?|choose files?|select files?|duyệt tệp|chọn tệp|chọn tập tin)$/.test(label(el)) || el.matches('.uppy-Dashboard-browse'));
        const fileName = norm(args.expectedFileName);
        const hasExpectedFile = !!fileName && (dialogInputs.some(el => Array.from(el.files || []).some(f => norm(f.name) === fileName)) ||
            Array.from(dialog.querySelectorAll('.uppy-Dashboard-Item-name,[data-testid*="uploaded-file"],a[href]')).filter(visible)
                .some(el => norm(el.innerText) === fileName || norm(el.getAttribute('title')) === fileName));
        const error = Array.from(statusScope.querySelectorAll('[role="alert"],.uppy-Informer,.uppy-StatusBar-statusPrimary,[data-testid*="error"]')).filter(visible)
            .find(el => /upload failed|failed to upload|could not upload|file too large|exceeds|invalid file|not supported|tải lên thất bại/i.test(el.innerText));
        if (error) return result('UPLOAD_FAILED', norm(error.innerText).slice(0, 250));
        const uploading = /\buploading\b|đang tải lên/.test(dialogText) || Array.from(statusScope.querySelectorAll('.uppy-StatusBar.is-uploading,[role="progressbar"]')).some(el =>
            visible(el) && Number(el.getAttribute('aria-valuenow') || 0) < Number(el.getAttribute('aria-valuemax') || 100));
        if (uploading) return result('UPLOADING');
        if (dialogInputs.length > 1 || browse.length > 1) return result('AMBIGUOUS_UPLOAD');
        if (hasExpectedFile) {
            const startButtons = leafControls.filter(el => /^upload(?:\s+\d+)?\s*(?:files?)?$|^tải lên(?:\s+\d+\s+tệp)?$/.test(label(el)) ||
                el.matches('.uppy-StatusBar-actionBtn--upload'));
            if (startButtons.length > 1) return result('AMBIGUOUS_UPLOAD');
            if (args.action === 'verifyUpload' && startButtons.length === 1) {
                const memory = '__acosePeerUploadStarts_' + expected;
                const clicked = window[memory] || (window[memory] = new WeakSet());
                if (clicked.has(startButtons[0])) return result('FILE_SELECTED');
                clicked.add(startButtons[0]);
                startButtons[0].click();
                return result('UPLOAD_CLICKED');
            }
            const completed = /upload complete|upload successful|tải lên thành công/.test(dialogText) ||
                Array.from(dialog.querySelectorAll('.uppy-StatusBar.is-complete,.uppy-Dashboard-Item--isComplete')).some(visible);
            if (completed && args.action === 'verifyUpload') {
                const done = leafControls.filter(el => /^(done|xong)$/.test(label(el)));
                const finish = done.length ? done : leafControls.filter(el => /^(close|đóng|close modal)$/.test(label(el)) || el.matches('.uppy-Dashboard-close'));
                if (finish.length === 1) { finish[0].click(); return result('UPLOAD_DIALOG_CLOSED'); }
            }
            // Selected file or a filename in the modal is not server acceptance.
            return result('FILE_SELECTED');
        }
        if (args.action === 'verifyUpload' || args.action === 'submit') return result('UPLOAD_DIALOG_READY');
        if (dialogInputs.length === 1) return markInput(dialogInputs[0]);
        if (args.action === 'chooseFile' && browse.length === 1) {
            browse[0].click();
            return result('BROWSE_FILES_CLICKED');
        }
        return result('UPLOAD_DIALOG_READY');
    }
    const tabs = controls.filter(el => /^my submissions?$/.test(label(el)) || label(el) === 'bài nộp của tôi');
    const roleTabs = tabs.filter(el => el.getAttribute('role') === 'tab');
    const tab = roleTabs.length === 1 ? roleTabs[0] : tabs.length === 1 ? tabs[0] : null;
    const panelId = tab?.getAttribute('aria-controls');
    const panel = panelId && document.getElementById(panelId);
    const scope = panel && visible(panel) ? panel : document.querySelector('main,[role="main"]') || document.body;
    // Text nodes only: a hidden or sidebar submission must not mark this form as submitted.
    const contentRoot = document.querySelector('main,[role="main"]') || document.body;
    const walker = document.createTreeWalker(contentRoot, NodeFilter.SHOW_TEXT);
    const pieces = [], resultPieces = [];
    // A learner's artifact, rubric examples and sidebar labels are not the
    // platform's result banner. Keep them out of pass/fail classification.
    const nonResultContent = 'article,blockquote,textarea,input,[contenteditable="true"],' +
        '[data-ai-instructions],[data-assessment-checkpoint],[class*="Sidebar"],[class*="sidebar"],' +
        '.rc-SubmissionPartView,.rc-SubmissionPart,.rc-CML,.cml-viewer,' +
        '[data-testid*="artifact" i],[data-testid*="submission-part" i],' +
        '[data-testid*="rubric" i],[class*="Rubric"],[class*="rubric"],.rc-OptionFeedbackTable';
    const resultVisible = el => {
        for (let p = el; p; p = p.parentElement) {
            if (Number(getComputedStyle(p).opacity) === 0) return false;
            if (p.tagName === 'DETAILS' && !p.open && !p.querySelector('summary')?.contains(el)) return false;
        }
        return true;
    };
    while (walker.nextNode()) {
        const node = walker.currentNode, el = node.parentElement;
        if (el && visible(el) && inContent(el) && !el.closest('script,style,textarea,input,[role="tablist"]') &&
            !(el.closest('[role="tabpanel"]') && !scope.contains(el)))
        {
            pieces.push(node.textContent);
            if (!el.closest(nonResultContent) && resultVisible(el)) resultPieces.push(node.textContent);
        }
    }
    const pageText = norm(pieces.join(' '));
    const resultText = norm(resultPieces.join(' '));
    const headings = Array.from(document.querySelectorAll('[data-testid="header-title"],.assignment-title,main h1,[role="main"] h1,h1'));
    title = headings.filter(visible).filter(inContent).map(el => (el.innerText || '').trim()).find(Boolean) || '';
    const within = el => scope.contains(el) && inContent(el);
    const addButtons = controls.filter(within).filter(enabled).filter(el => /^(add file|thêm tệp|thêm tập tin)$/.test(label(el)));
    const inputs = Array.from(scope.querySelectorAll('input[type="file"]')).filter(el =>
        el.isConnected && filePicker(el) && inContent(el) && !hidden(el.parentElement));
    const attachments = Array.from(scope.querySelectorAll('a[href],.uppy-Dashboard-Item-name,[data-testid*="attachment"],[data-testid*="uploaded-file"],.rc-FileUploadItem [title],.rc-FileUploadItem a')).filter(visible).filter(inContent)
        .filter(el => !el.matches('.uppy-Dashboard-Item-name') || !el.closest('.uppy-Dashboard-Item') || el.closest('.uppy-Dashboard-Item--isComplete'));
    const hasPdfAttachment = attachments.some(el => /\.pdf$/i.test(norm(el.innerText) || norm(el.getAttribute('title'))));
    const editorHasAttachment = hasPdfAttachment && Array.from(scope.querySelectorAll('input[type="text"],input:not([type]),input[type="checkbox"]')).some(visible);
    const editing = addButtons.length > 0 || inputs.length > 0 || editorHasAttachment;
    const scopedControls = controls.filter(within);
    const hasNewAttempt = scopedControls.some(el => /^(start new attempt|edit submission|bắt đầu lần (nộp|thử) mới|chỉnh sửa bài nộp)$/.test(label(el)));
    const hasDate = /\bsubmitted on\b|đã nộp (vào|ngày)/.test(pageText);
    const needsReviews = /not reviewed enough peers|review more peers to see your grade|complete (your |the )?required peer reviews|chưa.*đánh giá.*đủ/.test(pageText);
    // Coursera's AI grader does not always use the older "Results pending"
    // wording.  Treat the live grading banner as authoritative result text,
    // but deliberately ignore learner artifacts/rubrics/sidebar content.
    const pending = /\bresults? pending\b|\bawaiting (?:a |your )?grade\b|\bwaiting for (?:a |your )?grade\b|\byour submission is currently being graded by ai\b|\bonce the scores are available\b|bài nộp[^.]{0,100}đang (?:được )?(?:ai )?chấm|đang chờ (?:ai )?chấm|chờ kết quả/.test(resultText);
    // Current AI grading uses a numerical result sentence instead of "failed".
    // Do not treat "Try again", review completion, or a low rubric score alone as failure.
    const earned = resultText.match(/\byou earned\s+(\d+(?:[.,]\d+)?)\s+points?,?\s+but you need to earn at least\s+(\d+(?:[.,]\d+)?)\s*\/\s*(\d+(?:[.,]\d+)?)\s+points? to pass\b/);
    const grade = earned ? earned.slice(1).map(value => Number(value.replace(',', '.'))) : null;
    const belowPassingGrade = grade && grade[2] > 0 && grade[0] >= 0 && grade[0] <= grade[2] &&
        grade[1] > 0 && grade[1] <= grade[2] && grade[0] < grade[1];
    const failed = belowPassingGrade || /\b(not passed|you did not pass|you haven't passed|failed)\b|chưa đạt/.test(resultText);
    const passed = /\b(you passed|you have passed|congratulations[^.]{0,80}passed)\b|đã đạt yêu cầu/.test(resultText);
    const explicitlyInactive = tab && (tab.getAttribute('aria-selected') === 'false' || (panel && !visible(panel)));
    if (!explicitlyInactive && !editing && (hasDate || hasNewAttempt || pending || passed || failed)) {
        if (failed) return result('SUBMITTED_FAILED', belowPassingGrade
            ? `Bài nộp đạt ${grade[0]}/${grade[2]} điểm; cần ít nhất ${grade[1]}/${grade[2]} điểm để đạt.` : '');
        if (needsReviews) return result('SUBMITTED_PENDING_REVIEWS');
        if (pending) {
            if (args.action === 'refreshGrade') {
                // Refreshing a pending grade is the only action allowed here.
                // Match the complete accessible name and stay inside the active
                // submission pane so similarly named navigation is never used.
                const leafControls = scopedControls.filter(el =>
                    el.matches('button,[role="button"]') &&
                    !el.matches('a,[type="submit"]') &&
                    !el.closest('[role="dialog"],[role="alertdialog"]') &&
                    !scopedControls.some(other => other !== el && el.contains(other)));
                const refreshButtons = leafControls.filter(enabled).filter(el =>
                    getComputedStyle(el).pointerEvents !== 'none' && /^(refresh|làm mới)$/.test(label(el)));
                if (refreshButtons.length > 1) return result('GRADE_REFRESH_AMBIGUOUS');
                if (!refreshButtons.length) return result('GRADE_REFRESH_UNAVAILABLE');
                const memory = '__acosePeerGradeRefresh_' + expected;
                const clicked = window[memory] || (window[memory] = new WeakSet());
                if (clicked.has(refreshButtons[0])) return result('GRADE_REFRESH_WAIT');
                clicked.add(refreshButtons[0]);
                refreshButtons[0].click();
                return result('GRADE_REFRESH_CLICKED');
            }
            return result('SUBMITTED_PENDING_GRADE');
        }
        if (passed) return result('SUBMITTED_PASSED');
        return result('ALREADY_SUBMITTED');
    }
    // Respect explicit inactive tabs; never upload into a different pane.
    if (explicitlyInactive || (!editing && tab && !scope.querySelector('input[type="file"]'))) {
        if (args.action === 'open' && enabled(tab)) {
            const memory = '__acosePeerTab_' + expected;
            if (window[memory]) return result('WAIT');
            window[memory] = true;
            tab.click();
            return result('MY_SUBMISSION_CLICKED');
        }
        if (explicitlyInactive) return result('WAIT');
    }
    if (args.action === 'prepareTitle') {
        const plain = (value, maximum) => String(value || '').replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim().slice(0, maximum);
        const describedBy = el => norm([
            el.getAttribute('aria-label'), el.placeholder, el.name, el.id,
            ...Array.from(el.labels || []).map(l => l.innerText),
            ...(el.getAttribute('aria-labelledby') || '').split(/\s+/).map(id => document.getElementById(id)?.innerText || '')
        ].filter(Boolean).join(' '));
        const editable = el => visible(el) && enabled(el) && inContent(el) && !el.readOnly && !el.closest('[role="dialog"],aside,nav');
        const textInputs = Array.from(scope.querySelectorAll('input[type="text"],input:not([type])')).filter(editable);
        const projectFields = textInputs.filter(el =>
            /project[ _-]?(title|name)|assignment[ _-]?(title|name)|give your project a descriptive title|tiêu đề|tên (bài|dự án)|^(title|name)$/.test(describedBy(el)));
        const fileTitleFields = textInputs.filter(el =>
            /enter an optional title for the uploaded file|uploaded file[ _-]?title|file[ _-]?title|tiêu đề (tệp|tập tin)/.test(describedBy(el)));
        const captionFields = Array.from(scope.querySelectorAll('textarea')).filter(editable).filter(el =>
            /\bcaption\b|chú thích|mô tả (tệp|tập tin)/.test(describedBy(el)));
        const fields = Array.from(new Set([...projectFields, ...fileTitleFields, ...captionFields]));
        const pageTitle = plain(title, 200);
        const projectTitle = plain(args.projectTitle, 200) || pageTitle;
        const fileTitle = plain(args.fileTitle, 200) || projectTitle || pageTitle;
        const caption = plain(args.caption, 800) || fileTitle || projectTitle || pageTitle;
        let changed = 0;
        for (const field of fields) {
            if (field.value.trim()) continue;
            // A generic "Title" label can overlap a file-title selector. Prefer
            // the more specific field kind so each value lands in the right box.
            let value = captionFields.includes(field) ? caption :
                fileTitleFields.includes(field) ? fileTitle : projectTitle;
            const maximum = field.maxLength > 0 ? field.maxLength : (captionFields.includes(field) ? 800 : 200);
            value = plain(value, maximum);
            if (!value) continue;
            const prototype = field instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
            Object.getOwnPropertyDescriptor(prototype, 'value').set.call(field, value);
            field.dispatchEvent(new Event('input', { bubbles: true }));
            field.dispatchEvent(new Event('change', { bubbles: true }));
            changed++;
        }
        return result(changed ? 'TITLE_FILLED' : fields.length ? 'TITLE_READY' : 'TITLE_NOT_FOUND');
    }
    if (addButtons.length > 1 || inputs.length > 1) return result('AMBIGUOUS_UPLOAD');
    if (args.expectedFileName) {
        const fileName = norm(args.expectedFileName);
        const alerts = Array.from(scope.querySelectorAll('[role="alert"],.uppy-Informer,[data-testid*="error"]')).filter(visible);
        const error = alerts.find(el => /upload failed|failed to upload|could not upload|file too large|exceeds|invalid file|not supported|tải lên thất bại/i.test(el.innerText));
        if (error) return result('UPLOAD_FAILED', norm(error.innerText).slice(0, 250));
        const busy = /\buploading\b|đang tải lên/.test(pageText) || Array.from(scope.querySelectorAll('[role="progressbar"],.uppy-StatusBar.is-uploading')).some(el =>
            visible(el) && Number(el.getAttribute('aria-valuenow') || 0) < Number(el.getAttribute('aria-valuemax') || 100));
        if (busy) return result('UPLOADING');
        const selected = inputs.some(el => Array.from(el.files || []).some(f => norm(f.name) === fileName));
        const namedAttachment = attachments.some(el =>
            norm(el.innerText) === fileName || norm(el.getAttribute('title')) === fileName);
        if (selected && !namedAttachment) return result('FILE_SELECTED');
        const differentAttachment = attachments.some(el => {
            const actual = norm(el.innerText) || norm(el.getAttribute('title'));
            return /\.pdf$/i.test(actual) && actual !== fileName;
        });
        if (differentAttachment) return result(args.action === 'verifyUpload' ? 'WAIT' : 'DIFFERENT_UPLOAD');
        if (namedAttachment) {
            if (args.action !== 'submit' && args.action !== 'prepareSubmit') return result('UPLOADED');
            if (!editing) return result('WAIT');
            const honorCodes = Array.from(scope.querySelectorAll('input[type="checkbox"]')).filter(el => !hidden(el) && inContent(el)).filter(el =>
                el.id === 'agreement-checkbox-base' || /honor code|understand and agree|i agree|tôi.*đồng ý/.test(norm([
                    el.getAttribute('aria-label'), ...Array.from(el.labels || []).map(label => label.innerText)].join(' '))));
            if (honorCodes.length > 1) return result('SUBMIT_AMBIGUOUS', 'Multiple Honor Code confirmations');
            const buttons = scopedControls.filter(el => el.matches('button,[role="button"]')).filter(el =>
                /^(submit|submit assignment|submit project|nộp bài)$/.test(label(el)) && !el.closest('[role="dialog"],[role="alertdialog"]'));
            if (buttons.length > 1) return result('SUBMIT_AMBIGUOUS', 'Multiple primary submit buttons');
            if (!buttons.length) return result('SUBMIT_NOT_FOUND');
            if (honorCodes.length && !honorCodes[0].checked) {
                if (!enabled(honorCodes[0])) return result('SUBMIT_WAITING');
                honorCodes[0].click();
                return result('HONOR_CONFIRMATION_CLICKED');
            }
            if (!enabled(buttons[0]) || getComputedStyle(buttons[0]).pointerEvents === 'none') return result('SUBMIT_WAITING');
            if (args.action === 'prepareSubmit') return result('SUBMIT_READY');
            buttons[0].scrollIntoView({ block: 'center' });
            buttons[0].click();
            return result('SUBMIT_CLICKED');
        }
        if (args.action === 'submit') return result('WAIT');
        if (args.action === 'verifyUpload') return result('WAIT');
    }
    if (addButtons.length > 1 || inputs.length > 1) return result('AMBIGUOUS_UPLOAD');
    if (args.action === 'upload' && addButtons.length === 1) {
        addButtons[0].scrollIntoView({ block: 'center' });
        addButtons[0].click();
        return result('ADD_FILE_CLICKED');
    }
    if (addButtons.length === 1) return result('ADD_FILE_READY');
    if (inputs.length === 1) {
        return markInput(inputs[0]);
    }
    if (editorHasAttachment) return result('EXISTING_UPLOAD');
    const textEditors = Array.from(scope.querySelectorAll('textarea,[contenteditable="true"]')).filter(visible);
    if (textEditors.length && !/upload.*pdf|pdf.*only/.test(pageText)) return result('NO_PDF_FORM');
    return result('WAIT');
})(__PEER_ARGUMENTS__);
