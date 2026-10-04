const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');

function setup() {
  const clock = { now: 0 };
  const context = vm.createContext({
    assert, performance: { now: () => clock.now },
    Date: class extends Date { static now() { return 1_700_000_000_000 + clock.now; } },
    document: { hidden: false, addEventListener() {} },
    window: {}, localStorage: { getItem() { return null; } },
    EventSource: { OPEN: 1 }, console,
  });
  const run = code => vm.runInContext(code, context);
  run(fs.readFileSync(path.join(__dirname, '../assets/app.js'), 'utf8'));
  run(`
    for (const key of ['deviceMeta', 'deviceMac', 'deviceIpSeparator', 'deviceActivity', 'deviceNetwork', 'deviceIp', 'connectionDuration', 'systemUptime', 'deviceFocus', 'appView'])
      elements[key] = { hidden: false, textContent: '', title: '' };
    state.devices = [{ id: 'device-1', status: 'online', macAddress: 'A4:83:E7:19:2C:5A',
      connection: { id: 'session-1', durationMs: 10000, remoteIpAddress: '2001:db8::20' },
      clientStatus: { systemUptimeMs: 10000,
        observedAt: '2026-10-04T00:00:00Z', ageMs: 1000, fresh: true, validForMs: 44000 } }];
    state.selectedId = 'device-1'; state.timingSynced = true;
    observeDevice(selectedDevice());
    render = () => { throw new Error('timer must not rebuild controls'); };
  `);
  return { context, clock, run };
}

test('ticks update only text and keep connection and system time distinct', () => {
  const { clock, run } = setup();
  clock.now = 2000;
  run(`refreshSelectedDeviceMeta();
    assert.equal(elements.connectionDuration.textContent, '当前在线 12 秒');
    assert.equal(elements.systemUptime.textContent, '系统运行 13 秒');
    assert.equal(elements.deviceIp.textContent, '2001:db8::20');`);
});

test('SSE interruption freezes both clocks and never guesses offline', () => {
  const { clock, run } = setup();
  clock.now = 2000; run('pauseDeviceTiming();');
  clock.now = 12000;
  run(`renderDeviceNetwork(selectedDevice());
    assert.equal(selectedDevice().status, 'online');
    assert.equal(elements.connectionDuration.textContent, '当前在线 12 秒（待同步）');
    assert.equal(elements.deviceIp.textContent, '2001:db8::20（待同步）');
    assert.equal(elements.systemUptime.textContent, '系统运行 13 秒（待更新）');`);
});

test('browser offline event closes the stream and freezes immediately', () => {
  const { clock, run } = setup();
  run(`
    window.handlers = {}; window.addEventListener = (event, handler) => { window.handlers[event] = handler; };
    window.setInterval = () => 1;
    for (const key of ['loginForm', 'togglePassword', 'createToken', 'emptyCreateToken', 'logout', 'deviceAction',
      'removeDevice', 'copyToken', 'copyServer', 'tokenDialog', 'confirmDialog', 'confirmCancel', 'confirmSubmit'])
      elements[key] = { addEventListener() {} };
    bindInteractions();
    let streamClosed = false;
    state.events = { close() { streamClosed = true; } };
  `);
  clock.now = 2000;
  run(`window.handlers.offline(); assert.equal(streamClosed, true); assert.equal(state.events, null);`);
  clock.now = 12000;
  run(`renderDeviceNetwork(selectedDevice());
    assert.equal(elements.connectionDuration.textContent, '当前在线 12 秒（待同步）');`);
});

test('hidden transition retains elapsed time before freezing', () => {
  const { clock, run } = setup();
  clock.now = 3000;
  run('document.hidden = true; pauseDeviceTiming();');
  clock.now = 30000;
  run(`renderDeviceNetwork(selectedDevice());
    assert.equal(elements.connectionDuration.textContent, '当前在线 13 秒（待同步）');`);
});

test('sample expiry freezes extrapolation at the deadline', () => {
  const { clock, run } = setup();
  clock.now = 50000;
  run(`renderDeviceNetwork(selectedDevice());
    assert.equal(elements.systemUptime.textContent, '系统运行 55 秒（待更新）');
    assert.equal(elements.connectionDuration.textContent, '当前在线 1 分');`);
  clock.now = 70000;
  run(`renderDeviceNetwork(selectedDevice());
    assert.equal(elements.systemUptime.textContent, '系统运行 55 秒（待更新）');
    assert.equal(elements.deviceIp.textContent, '2001:db8::20');`);
});

test('offline hides current values, missing fields are unknown, zero uptime is valid', () => {
  const { run } = setup();
  run(`selectedDevice().status = 'offline'; renderDeviceMeta(selectedDevice());
    assert.equal(elements.deviceNetwork.hidden, true);
    assert.equal(elements.deviceIp.hidden, true);
    assert.equal(elements.deviceIpSeparator.hidden, true);
    assert.equal(elements.deviceMac.textContent, 'A4:83:E7:19:2C:5A');
    assert.equal(elements.deviceActivity.hidden, false);
    assert.equal(elements.deviceActivity.textContent, '从未上线');
    selectedDevice().status = 'online'; selectedDevice().clientStatus = null;
    renderDeviceMeta(selectedDevice());
    assert.equal(elements.deviceIp.hidden, false);
    assert.equal(elements.deviceIpSeparator.hidden, false);
    assert.equal(elements.deviceActivity.hidden, true);
    assert.equal(elements.deviceIp.textContent, '2001:db8::20');
    assert.equal(elements.systemUptime.textContent, '系统运行 未知');
    selectedDevice().connection.remoteIpAddress = null; renderDeviceNetwork(selectedDevice());
    assert.equal(elements.deviceIp.textContent, 'IP 未知');
    selectedDevice().clientStatus = { systemUptimeMs: 0, ageMs: 0, fresh: true, validForMs: 45000 };
    renderDeviceNetwork(selectedDevice());
    assert.equal(elements.systemUptime.textContent, '系统运行 0 秒');
    delete selectedDevice().connection; renderDeviceNetwork(selectedDevice());
    assert.equal(elements.deviceNetwork.hidden, true);`);
});

test('an expired server snapshot displays its original sample without growing', () => {
  const { clock, run } = setup();
  clock.now = 3000;
  run(`selectedDevice().clientStatus.fresh = false; selectedDevice().clientStatus.ageMs = 120000;
    selectedDevice().clientStatus.validForMs = 0; renderDeviceNetwork(selectedDevice());
    assert.equal(elements.systemUptime.textContent, '系统运行 10 秒（待更新）');`);
});

test('late HTTP snapshot cannot overwrite newer SSE state', async () => {
  const { context, run } = setup();
  let resolve;
  context.pendingSnapshot = new Promise(done => { resolve = done; });
  run('api = () => pendingSnapshot; state.events = { readyState: EventSource.OPEN };');
  const request = run('refreshDeviceSnapshot()');
  run('state.deviceRevision++; selectedDevice().name = "newer event";');
  resolve({ devices: [] });
  await request;
  run('assert.equal(selectedDevice().name, "newer event");');
});

test('fresh server snapshot calibrates page refresh and a new session resets connection only', () => {
  const { clock, run } = setup();
  clock.now = 2000;
  run(`const updated = { ...selectedDevice(), connection: { id: 'session-2', durationMs: 0 } };
    observeDevice(updated); state.devices = [updated]; renderDeviceNetwork(updated);
    assert.equal(elements.connectionDuration.textContent, '当前在线 0 秒');
    assert.equal(elements.systemUptime.textContent, '系统运行 11 秒');`);
});

function setupStartup() {
  const fixture = setup();
  let resolve, reject;
  fixture.context.initialSnapshot = new Promise((done, fail) => { resolve = done; reject = fail; });
  fixture.run(`
    state.devices = []; state.selectedId = null;
    elements.bootView = { hidden: false };
    elements.loginView = { hidden: true };
    elements.appView.hidden = true;
    elements.logout = { hidden: false };
    elements.tokenDialog = { open: false };
    document.querySelector = () => ({ textContent: '' });
    api = path => path === '/api/v1/server-info' ? Promise.resolve({ version: 'test' }) : initialSnapshot;
    let renderedStatus = null, streamOpened = false, startupError = '';
    render = () => {
      assert.equal(elements.bootView.hidden, false);
      assert.equal(elements.appView.hidden, true);
      renderedStatus = selectedDevice()?.status || 'empty';
    };
    openEvents = () => { streamOpened = true; };
    showToast = message => { startupError = message; };
  `);
  return { ...fixture, resolve, reject };
}

test('startup keeps the loading view until the offline snapshot is rendered', async () => {
  const { run, resolve } = setupStartup();
  const request = run('showApp()');
  run(`assert.equal(elements.bootView.hidden, false);
    assert.equal(elements.appView.hidden, true);
    assert.equal(renderedStatus, null);`);
  resolve({ devices: [{ id: 'device-1', status: 'offline', macAddress: 'A4:83:E7:19:2C:5A' }] });
  await request;
  run(`assert.equal(renderedStatus, 'offline');
    assert.equal(elements.bootView.hidden, true);
    assert.equal(elements.appView.hidden, false);
    assert.equal(streamOpened, true);`);
});

test('an empty startup snapshot renders before the app becomes visible', async () => {
  const { run, resolve } = setupStartup();
  const request = run('showApp()');
  resolve({ devices: [] });
  await request;
  run(`assert.equal(renderedStatus, 'empty');
    assert.equal(elements.bootView.hidden, true);
    assert.equal(elements.appView.hidden, false);`);
});

test('startup fetch failure renders a safe view and preserves the error message', async () => {
  const { run, reject } = setupStartup();
  const request = run('showApp()');
  reject(new Error('无法读取设备列表'));
  await request;
  run(`assert.equal(renderedStatus, 'empty');
    assert.equal(elements.bootView.hidden, true);
    assert.equal(elements.appView.hidden, false);
    assert.equal(streamOpened, false);
    assert.equal(startupError, '无法读取设备列表');`);
});
