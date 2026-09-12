const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const test = require('node:test');
const source = fs.readFileSync('Packages/dev.gryphprime.avatar-wardrobe/Web/upload.js', 'utf8');
function harness() {
  const calls = [], elements = {};
  const context = {
    upRunning: false, upJobToken: 0, upJobPolling: false, upJobTimer: null,
    T: key => key, crypto: { randomUUID() { throw Error('Recovery must not start an upload'); } },
    setInterval: () => 1, upStopPoll() {}, upEndJob() {},
    upShowJob() { context.upRunning = true; },
    upEl(id) { return elements[id] || (elements[id] = {style: {}, removeAttribute(key) { delete this[key]; }}); },
    request(url) {
      calls.push(url);
      return Promise.resolve({job: 'existing', done: 0, index: 2, total: 5, current: 'Third',
        stage: 'Uploading file', uploadProgress: .5, presetSeconds: 83, quietSeconds: 0});
    }
  };
  vm.createContext(context);
  return {context, calls, elements};
}
test('refresh discovers and immediately polls the existing job without resubmitting', async () => {
  const {context, calls, elements} = harness();
  vm.runInContext(source.slice(source.indexOf('  var upReconnectFlight=false;'),
    source.indexOf('  upEl("upJobCancel").onclick=')), context);
  context.upReconnectJob();
  await new Promise(setImmediate);
  assert.deepEqual(calls, ['/api/batch_job', '/api/batch_job?job=existing']);
  assert.match(elements.upJobLabel.textContent, /Third \(3\/5\)/);
  assert.equal(elements.upPresetBar.value, 50);
  context.upReconnectJob();
  await new Promise(setImmediate);
  assert.equal(calls.length, 2);
});
test('build progress stays indeterminate and quiet time is visible until completion', () => {
  const {context, elements} = harness();
  Object.assign(context, {token: 1, upJobToken: 1, label: 'Uploading', onDone: null});
  vm.runInContext(source.slice(source.indexOf('    function handleResult(q){'),
    source.indexOf('    function followJob(r){')), context);
  context.handleResult({index: 1, total: 5, current: 'Second', stage: 'Uploading', uploadProgress: .42});
  assert.equal(elements.upPresetBar.value, 42);
  assert.equal(elements.upJobBar.style.width, '20%');
  context.handleResult({index: 2, total: 5, current: 'Third', stage: 'Building', uploadProgress: -1,
    presetSeconds: 62, quietSeconds: 45});
  assert.equal(elements.upPresetBar.value, undefined);
  assert.match(elements.upPresetTiming.textContent, /No new SDK progress for 45s/);
  assert.doesNotMatch(elements.upPresetStage.textContent, /%/);
  context.handleResult({done: 1, ok: 1, stage: 'Complete', index: 5, total: 5});
  assert.equal(elements.upPresetProgress.hidden, true);
});
