const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const test = require('node:test');
const web = path.join(__dirname, '../Packages/dev.gryphprime.avatar-wardrobe/Web');
const source = fs.readFileSync(path.join(web, 'runtime.js'), 'utf8');

// Small DOM fixture for the shared key handler. Selector matching is independent
// of its tab-stop rules, so omitting a native summary reproduces the original loop.
function setup() {
  const handlers = {}, document = {activeElement:null};
  function element(tag, attrs = {}, ...children) {
    const node = {
      tagName:tag.toUpperCase(), attrs, children, parentElement:null,
      get tabIndex() { return 'tabindex' in attrs ? Number(attrs.tabindex) : /^(BUTTON|INPUT|SELECT|TEXTAREA|SUMMARY|IFRAME)$/.test(this.tagName) || this.tagName === 'A' && 'href' in attrs ? 0 : -1; },
      get open() { return 'open' in attrs; },
      get type() { return attrs.type || ''; }, get name() { return attrs.name || ''; },
      get checked() { return !!attrs.checked; }, get form() { return attrs.form || null; },
      get isContentEditable() { return attrs.contenteditable === 'true' || !!(this.parentElement && this.parentElement.isContentEditable); },
      hasAttribute(name) { return name in attrs; }, getAttribute(name) { return attrs[name] ?? null; },
      matches(selector) {
        if (selector === ':disabled') return !!attrs.disabled || !!attrs.disabledByFieldset;
        return selector.split(',').some(part => {
          const tag = part.match(/^[a-z]+/i), attributes = [...part.matchAll(/\[([\w-]+)(?:="?([^\]"]+)"?)?\]/g)];
          return (!tag || this.tagName === tag[0].toUpperCase()) && attributes.every(([, name, value]) => name in attrs && (value === undefined || String(attrs[name]) === value));
        });
      },
      closest(selector) { for (let current = this; current; current = current.parentElement) if (current.matches(selector)) return current; return null; },
      contains(other) { for (let current = other; current; current = current.parentElement) if (current === this) return true; return false; },
      querySelectorAll(selector) { return children.flatMap(child => [...(child.matches(selector) ? [child] : []), ...child.querySelectorAll(selector)]); },
      getClientRects() { return attrs.noRect ? [] : [{width:100, height:30}]; },
      getRootNode() { return document; }, focus() { document.activeElement = this; }
    };
    children.forEach(child => { child.parentElement = node; });
    return node;
  }
  const root = element('main');
  document.addEventListener = (type, handler) => { handlers[type] = handler; };
  document.querySelectorAll = selector => root.querySelectorAll(selector);
  const window = {getComputedStyle:node => ({visibility:node.attrs.visibility || 'visible'})};
  vm.runInNewContext(source, {window, document});
  function mount(...children) { root.children.push(...children); children.forEach(child => { child.parentElement = root; }); }
  function dialog(...children) { return element('div', {role:'dialog', 'aria-modal':'true', tabindex:-1}, ...children); }
  function tab(active, shiftKey = false) {
    document.activeElement = active;
    const event = {key:'Tab', shiftKey, prevented:false, preventDefault() { this.prevented = true; }};
    handlers.keydown(event);
    return {active:document.activeElement, prevented:event.prevented};
  }
  return {element, mount, dialog, tab};
}

test('native disclosure summaries remain in the tab sequence instead of looping to Close', () => {
  const {element:e, mount, dialog, tab} = setup();
  const close = e('button'), technical = e('summary'), advanced = e('summary'), wear = e('button');
  mount(dialog(close, e('details', {}, technical), e('details', {}, advanced), wear));
  assert.equal(tab(technical).prevented, false, 'Tab must continue from Technical details toward Advanced options');
  assert.equal(tab(advanced).prevented, false, 'Tab must continue from Advanced options toward Wear');
  assert.equal(tab(advanced, true).prevented, false, 'Shift+Tab between disclosures remains native');
  assert.equal(tab(wear).active, close);
  assert.equal(tab(close, true).active, wear);
});

test('hidden, inert, disabled-fieldset and negative-tabindex controls cannot become loop boundaries', () => {
  const {element:e, mount, dialog, tab} = setup();
  const first = e('button'), last = e('button');
  mount(dialog(e('button', {tabindex:-2}), e('button', {disabledByFieldset:true}), first, last,
    e('button', {disabled:true}), e('button', {noRect:true}), e('button', {visibility:'hidden'}),
    e('div', {inert:true}, e('button')), e('div', {hidden:true}, e('button'))));
  assert.equal(tab(last).active, first);
  assert.equal(tab(first, true).active, last);
});

test('closed details expose their summary, and opening them admits their controls', () => {
  const {element:e, mount, dialog, tab} = setup();
  const first = e('button'), summary = e('summary'), nested = e('button'), details = e('details', {}, summary, nested);
  mount(dialog(first, details));
  assert.equal(tab(first, true).active, summary);
  details.attrs.open = true;
  assert.equal(tab(first, true).active, nested);
  assert.equal(tab(summary).prevented, false);
});

test('positive tabindex precedes natural stops and native editable content is included', () => {
  const {element:e, mount, dialog, tab} = setup();
  const natural = e('button'), second = e('button', {tabindex:2}), first = e('button', {tabindex:1}), editor = e('div', {contenteditable:'true'});
  mount(dialog(natural, second, first, editor));
  assert.equal(tab(editor).active, first);
  assert.equal(tab(first, true).active, editor);
  assert.equal(tab(natural).prevented, false);
});

test('radio groups use the checked stop, retaining the active unchecked member in reverse', () => {
  const {element:e, mount, dialog, tab} = setup();
  const first = e('button'), selected = e('input', {type:'radio', name:'mode', checked:true}), other = e('input', {type:'radio', name:'mode'});
  mount(dialog(first, selected, other));
  assert.equal(tab(selected).active, first, 'Unchecked trailing radio must not let focus escape the dialog');
  assert.equal(tab(first, true).active, selected);
  selected.attrs.checked = false;
  assert.equal(tab(other).active, first, 'Reverse entry to unchecked group may focus its last member');
});

test('an empty dialog retains focus and the last visible dialog owns the boundary', () => {
  const {element:e, mount, dialog, tab} = setup();
  const empty = dialog(e('button', {disabled:true}));
  mount(empty);
  assert.equal(tab(empty).active, empty);
  assert.equal(tab(empty, true).prevented, true);
  const first = e('button'), last = e('button');
  mount(dialog(first, last), e('div', {role:'dialog', 'aria-modal':'true', noRect:true}, e('button')));
  assert.equal(tab(last).active, first);
});

test('primary action fills meet normal-text contrast in both default and hover states', () => {
  const css = fs.readFileSync(path.join(web, 'wardrobe.css'), 'utf8');
  const normal = css.match(/--accent-fill:(#[\da-f]{6})/i)[1];
  const hover = css.match(/button\.primary:hover[^}]*background:(#[\da-f]{6})/i)[1];
  function luminance(hex) {
    const values = hex.slice(1).match(/../g).map(value => parseInt(value, 16) / 255).map(value => value <= .04045 ? value / 12.92 : ((value + .055) / 1.055) ** 2.4);
    return values[0] * .2126 + values[1] * .7152 + values[2] * .0722;
  }
  for (const fill of [normal, hover]) assert(1.05 / (luminance(fill) + .05) >= 4.5, `${fill} must keep white functional text readable`);
});
