(function () {
    'use strict';
    // Read-only. Do not learn feedback from a previous page, a login redirect,
    // another course, hidden cached React trees, or editable ungraded questions.
    const expectedUrl = __EXPECTED_URL__;
    const trusted = url => url.protocol === 'https:' && !url.username && !url.password &&
        (!url.port || url.port === '443') && /^(?:www\.)?coursera\.org$/i.test(url.hostname);
    try {
        const expected = new URL(expectedUrl), current = new URL(location.href);
        if (!trusted(expected) || !trusted(current) || expected.href !== current.href) return '[]';
    } catch (_) { return '[]'; }
    const norm = value => String(value || '').replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim();
    const key = value => norm(value).toLocaleLowerCase().replace(/[!.:]$/, '').trim();
    const visible = el => {
        if (!el?.isConnected || !el.getClientRects().length ||
            el.closest('[aria-hidden="true"],[hidden],[inert],aside,nav,[role="navigation"]')) return false;
        for (let p = el; p; p = p.parentElement) {
            const style = getComputedStyle(p);
            if (style.display === 'none' || /hidden|collapse/.test(style.visibility) || Number(style.opacity) === 0) return false;
        }
        return true;
    };
    const rootSelector = '[data-testid^="part-Submission_"],[id^="part-Submission_"]';
    const feedbackSelector = '.cds-Feedback,[class*="Feedback"],[data-testid*="feedback" i],[id$="-feedback"],[role="alert"]';
    const choiceSelector = 'input[type="radio"],input[type="checkbox"],[role="radio"],[role="checkbox"]';
    const textSelector = 'textarea,input:not([type]),input[type="text"],input[type="number"]';
    // The page/dialog itself may be named ViewFeedback. A matching ancestor
    // outside this question (or the question root itself) is not an option's
    // feedback box and must not cause every prompt to be discarded.
    const closestWithinQuestion = (node, selector, question) => {
        const match = node?.closest(selector);
        return match && match !== question && question.contains(match) ? match : null;
    };
    const positive = new Set(['correct', 'nice work', 'chính xác', 'đúng']);
    const negative = new Set(['incorrect', 'try again', 'this should not be selected', 'chưa chính xác', 'không chính xác', 'sai']);
    const explicitAnswer = new Set(['correct answer', 'correct response', 'đáp án đúng', 'câu trả lời đúng']);
    const explicitWrong = new Set(['incorrect answer', 'incorrect response', 'đáp án sai', 'câu trả lời sai']);
    const knownLabel = text => positive.has(key(text)) || negative.has(key(text)) ||
        explicitAnswer.has(key(text)) || explicitWrong.has(key(text));
    const feedbackNodes = scope => [...new Set([
        ...scope.querySelectorAll(feedbackSelector),
        ...Array.from(scope.querySelectorAll('input[aria-describedby],textarea[aria-describedby],[role="checkbox"][aria-describedby],[role="radio"][aria-describedby]'))
            .flatMap(control => (control.getAttribute('aria-describedby') || '').split(/\s+/).filter(Boolean)
                .map(id => document.getElementById(id))).filter(node => node && scope.contains(node))
    ])].filter(node => visible(node) && !node.closest('[data-ai-instructions],[data-assessment-checkpoint]'));
    const roots = Array.from(document.querySelectorAll(rootSelector)).filter(q => visible(q) && !q.parentElement?.closest(rootSelector));
    const results = [];

    // Only exact markers count. In particular, "Incorrect" never matches
    // "Correct", and arbitrary SVG decoration never means an option is selected.
    function markers(scope, exclude = () => false, allowText = () => true) {
        let good = false, bad = false, answer = false;
        const nodes = [scope, ...scope.querySelectorAll('*')];
        for (const node of nodes) {
            if (!visible(node) || exclude(node)) continue;
            const testId = key(node.getAttribute('data-testid'));
            if (testId === 'icon-correct') good = true;
            if (testId === 'icon-incorrect') bad = true;
            const labels = [node.getAttribute('aria-label'), node.getAttribute('title')];
            // Grade text must be a separate label, not a substring in a prompt,
            // answer option, or explanatory paragraph containing "correct".
            if (node.children.length === 0 && allowText(node)) labels.push(node.textContent);
            // A question container named Feedback is not a grade label: its
            // combined text includes markers from several different options.
            if (node.matches(feedbackSelector) && !node.matches(rootSelector))
                labels.push(...String(node.innerText || '').split(/\r?\n/));
            for (const label of labels) {
                if (positive.has(key(label))) good = true;
                if (negative.has(key(label)) || explicitWrong.has(key(label))) bad = true;
                if (explicitAnswer.has(key(label))) answer = true;
            }
        }
        return { good, bad, answer };
    }

    function cleanText(element) {
        if (!element) return '';
        const clone = element.cloneNode(true);
        clone.querySelectorAll('input,textarea,button,svg,[aria-hidden="true"],[hidden],[data-ai-instructions],[data-assessment-checkpoint],script,style,' + feedbackSelector)
            .forEach(child => child.remove());
        clone.querySelectorAll('[aria-label],[data-testid]').forEach(child => {
            const id = key(child.getAttribute('data-testid'));
            if (id === 'icon-correct' || id === 'icon-incorrect' || knownLabel(child.getAttribute('aria-label'))) child.remove();
        });
        // Detached textContent otherwise joins adjacent paragraphs ("oneTwo")
        // and no longer matches the solver's whitespace-normalized innerText.
        clone.querySelectorAll('br').forEach(child => child.replaceWith(' '));
        clone.querySelectorAll('p,div,li,ul,ol,section,article,table,tr,td,th,blockquote,pre,h1,h2,h3,h4,h5,h6')
            .forEach(child => { child.prepend(' '); child.append(' '); });
        return norm(clone.textContent);
    }

    function selected(control) {
        if (control.tagName === 'INPUT') return control.checked === true;
        return control.getAttribute('aria-checked') === 'true';
    }

    for (const q of roots) {
        const prompt = q.querySelector('[id^="prompt-"]') || q.querySelector('[data-testid="cml-viewer"],.rc-CML');
        if (!prompt || !visible(prompt) || closestWithinQuestion(prompt, 'label,[role="radio"],[role="checkbox"],' + feedbackSelector, q)) continue;
        const question = cleanText(prompt);
        if (!question || question.length > 20000) continue;
        const native = Array.from(q.querySelectorAll('input[type="radio"],input[type="checkbox"]'));
        const controls = native.length ? native : Array.from(q.querySelectorAll('[role="radio"],[role="checkbox"]'));
        const options = [];
        let kind = '', invalid = false;
        for (const control of controls) {
            const type = control.type || control.getAttribute('role');
            const optionKind = type === 'radio' ? 'single_choice' : type === 'checkbox' ? 'multi_choice' : '';
            if (!optionKind || (kind && kind !== optionKind)) { invalid = true; break; }
            kind = optionKind;
            const label = Array.from(control.labels || []).find(el => q.contains(el)) ||
                control.closest('label') || (control.matches('[role="radio"],[role="checkbox"]') ? control : null);
            if (!label || !q.contains(label) || !visible(label) ||
                (control.tagName !== 'INPUT' && !['true', 'false'].includes(control.getAttribute('aria-checked')))) { invalid = true; break; }
            const textElement = label.querySelector('.cds-checkboxAndRadio-labelText,[data-testid="cml-viewer"],.rc-CML') || label;
            const text = cleanText(textElement) || norm(control.getAttribute('aria-label'));
            if (!text || text.length > 10000 || options.some(option => option.text === text)) { invalid = true; break; }
            let wrapper = label;
            // Associate feedback only when the wrapper contains this one option.
            // Never borrow the next option's feedback or a shared question grade.
            for (let ancestor = label.parentElement; ancestor && ancestor !== q && !ancestor.contains(prompt); ancestor = ancestor.parentElement) {
                const descendants = Array.from(ancestor.querySelectorAll(choiceSelector))
                    .filter(el => el.tagName === 'INPUT' || !el.querySelector('input[type="radio"],input[type="checkbox"]'));
                if (descendants.length !== 1 || descendants[0] !== control) break;
                wrapper = ancestor;
            }
            const grade = markers(wrapper,
                node => node === control || (textElement !== label && textElement.contains(node)),
                node => textElement !== label || !!closestWithinQuestion(node, feedbackSelector, q));
            if (grade.bad && (grade.good || grade.answer)) { invalid = true; break; }
            const checked = selected(control);
            // A green marker on an UNSELECTED checkbox usually means exclusion
            // was correct. It is not evidence that the option belongs in the answer.
            const correct = !grade.bad && (grade.answer || (checked && grade.good));
            const wrong = checked && grade.bad && !grade.good && !grade.answer;
            options.push({ text, checked, correct, wrong, wrapper });
        }
        if (invalid || controls.length !== options.length) continue;
        const textControls = controls.length ? [] : Array.from(q.querySelectorAll(textSelector)).filter(visible);
        if (!kind && textControls.length === 1) kind = textControls[0].tagName === 'TEXTAREA' ? 'long_text' : 'short_text';
        if (!kind) continue;
        const excluded = node => prompt.contains(node) || options.some(option => option.wrapper.contains(node)) ||
            textControls.some(control => control.contains(node)) || !!node.closest('button');
        const grade = markers(q, excluded);
        if (grade.good && grade.bad) continue;
        let failed = grade.bad && !grade.good, correctQuestion = grade.good && !grade.bad;
        const scores = [];
        for (const node of q.querySelectorAll('*')) {
            if (!visible(node) || excluded(node) || node.children.length > 0) continue;
            const score = norm(node.textContent).match(/^(\d+(?:\.\d+)?)\s*(?:\/|out of)\s*(\d+(?:\.\d+)?)\s*(?:points?|điểm)?$/i);
            if (score && Number(score[2]) > 0 && Number(score[1]) <= Number(score[2])) scores.push(Number(score[1]) === Number(score[2]));
        }
        // Contradictory grade/score UI is often a transitional React render;
        // don't promote it into durable answer evidence.
        if (scores.length) {
            if (!scores.every(value => value === scores[0])) continue;
            if ((failed && scores[0]) || (correctQuestion && !scores[0])) continue;
            else { failed = !scores[0]; correctQuestion = scores[0]; }
        }
        const selectedAnswers = options.filter(option => option.checked).map(option => option.text);
        if (textControls.length === 1 && norm(textControls[0].value)) selectedAnswers.push(norm(textControls[0].value));
        if (kind === 'single_choice' && selectedAnswers.length > 1) continue;
        const correctAnswers = options.filter(option => option.correct).map(option => option.text);
        const wrongAnswers = options.filter(option => option.wrong).map(option => option.text);
        if ((correctQuestion && wrongAnswers.length) ||
            (failed && kind === 'single_choice' && selectedAnswers.some(answer => correctAnswers.includes(answer)))) continue;
        if (!correctQuestion && wrongAnswers.length) failed = true;
        if (correctQuestion) {
            for (const answer of selectedAnswers) if (!wrongAnswers.includes(answer) && !correctAnswers.includes(answer)) correctAnswers.push(answer);
        }
        // Failure of a multi-select combination never labels every selected
        // member incorrect. Preserve only the failed set plus explicit evidence.
        if (failed && kind !== 'multi_choice' && selectedAnswers.length === 1 && !correctAnswers.includes(selectedAnswers[0]) && !wrongAnswers.includes(selectedAnswers[0]))
            wrongAnswers.push(selectedAnswers[0]);
        const feedback = [...new Set(feedbackNodes(q).filter(node => !prompt.contains(node)).map(node => norm(node.innerText)).filter(Boolean))].join('\n').slice(0, 12000);
        if (!failed && !correctQuestion && !correctAnswers.length && !wrongAnswers.length) continue;
        results.push({ Question: question, Kind: kind, Options: options.map(option => option.text), SelectedAnswers: selectedAnswers,
            CorrectAnswers: correctAnswers, WrongAnswers: wrongAnswers, IsFailedQuestion: failed, IsCorrectQuestion: correctQuestion, Feedback: feedback });
    }
    return JSON.stringify(results);
})();
