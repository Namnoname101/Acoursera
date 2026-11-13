const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

// Run the actual scripts embedded in MainWindow against a small course outline.
const source = fs.readFileSync(path.join(__dirname,
  '../../src/AutomationPlatform.Presentation/MainWindow.xaml.cs'), 'utf8');
const progress = source.match(/string script = """\s*(\(function\(\) \{\s*const skipGradedAppItems[\s\S]*?)\s*"""/)[1];
const scanner = source.match(/string jsCode = @"\s*(\(function\(\) \{\s*var skipGradedAppItems[\s\S]*?)\r?\n\s*";/)[1].replaceAll('""', '"');

function run(script, skipPdf, skipReview, definitions) {
  let clicked = null;
  const lessons = definitions.map(([kind, completed = false]) => {
    const href = kind === 'pdf' ? '/learn/course/peer/id/project' :
      kind === 'review' ? '/learn/course/peer/id/project/give-feedback' :
      '/learn/course/quiz/quiz-id/quiz';
    const title = kind === 'review' ? 'Review your peers' :
      kind === 'pdf' ? 'Peer-graded Assignment' : 'Quiz';
    const lesson = {
      innerText: title, innerHTML: completed ? '>Completed<' : '', children: [],
      getAttribute: name => name === 'href' ? href : name === 'aria-label' ? title : null,
      querySelector: () => null,
      click: () => { clicked = kind; }
    };
    lesson.closest = () => lesson;
    return lesson;
  });
  const module = {
    innerText: 'Module 1',
    getAttribute: name => name === 'aria-label' ? 'Module 1' :
      name === 'aria-current' ? 'page' : name === 'href' ? '/learn/course/home/week/1' : null
  };
  const location = new URL('https://www.coursera.org/learn/course/home/week/1');
  const document = { querySelectorAll: selector => selector.includes('WeekNavigationItem')
    ? [module] : lessons };
  const replacements = {
    __SKIP_PEER_PDF__: String(skipPdf), __SKIP_PEER__: String(skipReview),
    __SKIP_GRADED__: 'false', __SKIP_PRACTICE__: 'false'
  };
  const rendered = script.replace(/__[A-Z_]+__/g, key => replacements[key] ?? '[]');
  const result = vm.runInNewContext(rendered, { document, window: { location }, URL });
  return { result, clicked };
}

for (const skipPdf of [false, true]) {
  for (const skipReview of [false, true]) {
    const label = `PDF=${skipPdf}, review=${skipReview}`;
    test(`outline chooses the first enabled activity (${label})`, () => {
      const { clicked } = run(scanner, skipPdf, skipReview,
        [['pdf'], ['review'], ['quiz']]);
      assert.equal(clicked, !skipPdf ? 'pdf' : !skipReview ? 'review' : 'quiz');
    });
    test(`review stays independent of PDF choice (${label})`, () => {
      const { clicked } = run(scanner, skipPdf, skipReview,
        [['review'], ['pdf'], ['quiz']]);
      assert.equal(clicked, !skipReview ? 'review' : !skipPdf ? 'pdf' : 'quiz');
    });
    test(`progress excludes only selected PDF activities (${label})`, () => {
      const { result } = run(progress, skipPdf, skipReview, [['pdf'], ['quiz', true]]);
      assert.equal(JSON.parse(result).Progress, skipPdf ? 100 : 50);
    });
    test(`progress excludes only selected review activities (${label})`, () => {
      const { result } = run(progress, skipPdf, skipReview, [['review'], ['quiz', true]]);
      assert.equal(JSON.parse(result).Progress, skipReview ? 100 : 50);
    });
  }
}

test('all skipped peer items remain visible in completion status', () => {
  const { result, clicked } = run(scanner, true, true, [['pdf'], ['review']]);
  assert.equal(clicked, null);
  assert.match(result, /Peer\/Review.*bỏ qua/);
  assert.doesNotMatch(result, /hoàn thành toàn bộ/);
});
