const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const root = path.resolve(__dirname, '..');
const html = fs.readFileSync(path.join(root, 'docs/index.html'), 'utf8');
const script = fs.readFileSync(path.join(root, 'docs/localization.js'), 'utf8');
const catalog = JSON.parse(fs.readFileSync(path.join(root, 'Packages/dev.gryphprime.avatar-wardrobe/Web/lang.json'), 'utf8'));
const repositoryUrl = 'https://gryphprime.github.io/avatar-wardrobe-vpm/index.json';

function loadPage({ query = '', saved = null, languages = ['en-US'], storageBlocked = false, clipboardBlocked = false } = {}) {
  const elements = new Map();
  function element(id = '') {
    const value = { id, dataset: {}, innerHTML: '', textContent: '', value: '', hidden: true, handlers: {} };
    value.addEventListener = (event, handler) => { value.handlers[event] = handler; };
    if (id) elements.set(id, value);
    return value;
  }
  for (const id of ['language', 'language-control', 'repository-url', 'copy-status', 'copy-url']) element(id);
  elements.get('repository-url').textContent = repositoryUrl;
  const translated = [...html.matchAll(/data-i18n="([^"]+)"/g)].map(match => {
    const value = element();
    value.dataset.i18n = match[1];
    return value;
  });
  const document = {
    documentElement: { lang: 'en' }, title: '',
    getElementById: id => elements.get(id),
    querySelectorAll: () => translated,
    createRange: () => ({ selectNodeContents: value => { document.selected = value; } })
  };
  const window = {
    location: new URL(`https://gryphprime.github.io/avatar-wardrobe-vpm/${query}#update`),
    history: { replaceState: (_state, _title, url) => { window.location = new URL(url); } },
    getSelection: () => ({ removeAllRanges() {}, addRange() {} })
  };
  const storage = {
    getItem() { if (storageBlocked) throw new Error('Storage blocked'); return saved; },
    setItem(_key, value) { if (storageBlocked) throw new Error('Storage blocked'); saved = value; }
  };
  const navigator = {
    languages,
    clipboard: { async writeText(value) { if (clipboardBlocked) throw new Error('Clipboard blocked'); navigator.copied = value; } }
  };
  vm.runInNewContext(script, { document, window, navigator, localStorage: storage, URL, URLSearchParams });
  return {
    document, window, navigator, translated, elements,
    saved: () => saved,
    async copy() { await elements.get('copy-url').handlers.click(); },
    change(code) { elements.get('language').value = code; elements.get('language').handlers.change(); },
    content(key) { return translated.find(value => value.dataset.i18n === key).innerHTML; }
  };
}

test('all supported app languages translate every VPM page label and preserve identifiers', () => {
  const codes = catalog.langs.map(language => language.code);
  const options = [...html.matchAll(/<option value="([^"]+)"/g)].map(match => match[1]);
  assert.deepEqual(options, codes);
  for (const code of codes) {
    const page = loadPage({ query: `?lang=${code}` });
    assert.equal(page.document.documentElement.lang, code === 'zh' ? 'zh-Hans' : code);
    assert.equal(page.elements.get('language').value, code);
    assert.equal(page.elements.get('language-control').hidden, false);
    for (const value of page.translated) {
      assert.equal(typeof value.innerHTML, 'string', `${code}: ${value.dataset.i18n}`);
      assert.ok(value.innerHTML.trim(), `${code}: ${value.dataset.i18n}`);
    }
    assert.ok(page.content('dependencies').includes('href="https://modular-avatar.nadena.dev/docs/intro"'));
    assert.ok(page.content('migration').includes('<code>Assets/OutfitToggleGenerator</code>'));
    assert.ok(page.content('openInstructions').includes('Tools → Avatar Wardrobe'));
    assert.equal(page.elements.get('repository-url').textContent, repositoryUrl);
  }
  assert.ok(html.includes(`href="vcc://vpm/addRepo?url=${encodeURIComponent(repositoryUrl)}"`));
});

test('explicit language overrides saved preference and browser languages', () => {
  assert.equal(loadPage({ query: '?lang=ja', saved: 'ko', languages: ['zh-CN'] }).document.documentElement.lang, 'ja');
  assert.equal(loadPage({ saved: 'ko', languages: ['ja'] }).document.documentElement.lang, 'ko');
  assert.equal(loadPage({ languages: ['fr-CA', 'zh-CN', 'ja'] }).document.documentElement.lang, 'zh-Hans');
  assert.equal(loadPage({ query: '?lang=__proto__', saved: 'constructor', languages: ['de'] }).document.documentElement.lang, 'en');
});

test('language switching preserves the update anchor and works with unavailable storage', () => {
  const page = loadPage({ query: '?from=wardrobe', storageBlocked: true, languages: ['ko-KR'] });
  assert.equal(page.document.documentElement.lang, 'ko');
  page.change('ja');
  assert.equal(page.document.documentElement.lang, 'ja');
  assert.equal(page.window.location.searchParams.get('lang'), 'ja');
  assert.equal(page.window.location.searchParams.get('from'), 'wardrobe');
  assert.equal(page.window.location.hash, '#update');
  const persisted = loadPage();
  persisted.change('zh');
  assert.equal(persisted.saved(), 'zh');
});

test('copy uses the unchanged repository URL and success stays localized after switching', async () => {
  const page = loadPage({ query: '?lang=ja' });
  await page.copy();
  assert.equal(page.navigator.copied, repositoryUrl);
  assert.ok(page.elements.get('copy-status').textContent.startsWith('コピーしました。'));
  page.change('ko');
  assert.ok(page.elements.get('copy-status').textContent.startsWith('복사했습니다.'));
  page.change('zh');
  assert.ok(page.elements.get('copy-status').textContent.startsWith('已复制。'));
});

test('unavailable clipboard selects the URL and shows translated manual instructions', async () => {
  const page = loadPage({ query: '?lang=ko', clipboardBlocked: true });
  await page.copy();
  assert.equal(page.document.selected, page.elements.get('repository-url'));
  assert.ok(page.elements.get('copy-status').textContent.startsWith('위의 URL을 선택'));
  page.change('en');
  assert.equal(page.elements.get('copy-status').textContent, 'Select and copy the URL above, then paste it into ALCOM.');
});
