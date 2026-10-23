import test from 'node:test';
import assert from 'node:assert/strict';
import { actions, describe, keyImage, stateFor, bufferSeconds } from '../src/presentation.mjs';
import { validSnapshot, requestTimeout } from '../src/bridge.mjs';
const local = { name: 'Gaming', version: '0.7.20', role: 'Host', replayEnabled: true, bufferedSeconds: 120, durationSeconds: 300,
  streaming: true, seamless: true, saveRevision: 0, saveState: 'idle', peers: [{ id: 'peer', name: 'Server' }], monitors: [{ id: 'left', name: 'Left', active: false }, { id: 'main', name: 'Main', active: true }] };

test('independent recording action exposes actual start, recording, saving and error states', () => {
  assert.equal(actions.recording?.command, 'toggleRecording');
  const ready = { ...local, recordingSupported: true, recording: false, recordingBusy: false };
  assert.equal(describe('recording', { local: ready })?.label, 'Record');
  assert.equal(describe('recording', { local: { ...ready, recording: true } })?.mode, 'recording');
  assert.equal(describe('recording', { local: { ...ready, recording: true, recordingBusy: true } })?.label, 'Saving');
  assert.equal(describe('recording', { local: { ...ready, recordingBusy: true } })?.label, 'Starting');
  assert.equal(describe('recording', { local: { ...ready, recordingError: 'encoder failed' } })?.mode, 'error');
  assert.equal(describe('recording', { local: { ...ready, recording: true } }, { language: 'ru' })?.label, 'Запись');
});

test('recording never advertises an older app or a disconnected configured host', () => {
  assert.equal(describe('recording', { local })?.label, 'Unavailable');
  assert.equal(describe('recording', { local: { ...local, recordingSupported: true, recording: true } }, { target: 'host' })?.label, 'Offline');
  const host = { ...local, name: 'Controller', recordingSupported: true, recording: true };
  assert.equal(describe('recording', { local, host }, { target: 'host' })?.mode, 'recording');
  const idle = keyImage('recording', describe('recording', { local: { ...host, recording: false } }));
  assert.notEqual(idle, keyImage('recording', describe('recording', { local: host })));
});

test('recording IPC accepts backward-compatible snapshots and rejects malformed recording fields', () => {
  assert.ok(validSnapshot({ local }));
  assert.ok(validSnapshot({ local: { ...local, recordingSupported: true, recording: true, recordingBusy: false, recordingError: null } }));
  for (const bad of [{ recording: 'yes' }, { recordingBusy: 1 }, { recordingSupported: 'true' }, { recordingError: {} }, { recordingStatus: 'x'.repeat(4097) }])
    assert.equal(validSnapshot({ local: { ...local, ...bad } }), false);
  assert.equal(requestTimeout('toggleRecording'), 600000);
  assert.equal(requestTimeout('saveReplay'), 600000);
  assert.equal(requestTimeout('status'), 12000);
});
test('buffer meter and actual delivered feedback remain distinct', () => {
  const s = { local };
  assert.equal(describe('save-replay', s).label, '2:00'); assert.equal(describe('save-replay', s).fill, .4);
  assert.equal(describe('save-replay', { local: { ...local, saveState: 'queued' } }).label, 'Queued');
  assert.equal(describe('save-replay', s, {}, false, true).label, 'Saved');
  assert.equal(describe('save-replay', { local: { ...local, replayError: 'encoder' } }).label, 'Error');
});
test('disconnected host never falls back to recording on this PC', () => {
  assert.equal(stateFor({ local }, { target: 'host' }), undefined);
  assert.equal(describe('save-replay', { local }, { target: 'host' }).label, 'Offline');
  assert.equal(describe('open-kvm', { local }, { peer: 'missing' }).label, 'Offline');
  assert.equal(describe('open-kvm', { local }, { peer: 'peer' }).label, 'Server');
});
test('toggles reflect live state and Russian translation', () => {
  assert.equal(describe('lock-mouse', { local: { ...local, seamless: false } }, { language: 'ru' }).label, 'Блок');
  assert.equal(describe('toggle-replay', { local }).state, 1);
  assert.equal(describe('toggle-streaming', { local: { ...local, streaming: false } }).state, 0);
  for (const kind of Object.keys(actions)) assert.match(keyImage(kind, describe(kind, null)), /^<svg[^]*<\/svg>$/);
});
test('minimal icons distinguish locked, unlocked, off and unavailable', () => {
  const unlocked = describe('lock-mouse', { local }), locked = describe('lock-mouse', { local: { ...local, seamless: false } });
  assert.notEqual(keyImage('lock-mouse', unlocked), keyImage('lock-mouse', locked));
  assert.notEqual(keyImage('toggle-replay', describe('toggle-replay', { local })), keyImage('toggle-replay', describe('toggle-replay', { local: { ...local, replayEnabled: false } })));
  assert.equal(describe('lock-mouse', { local: { ...local, role: 'Off' } }).mode, 'offline');
  assert.equal(describe('open-kvm', { local }).label, 'KVM');
  assert.equal(describe('switch-monitor', { local }, { monitor: 'main' }).mode, 'active');
  assert.equal(describe('switch-monitor', { local }, { monitor: 'gone' }).label, 'Offline');
});
test('replay timer interpolates only fresh recorder time, caps and resets', () => {
  const live = { ...local, bufferedSeconds: 120.8, bufferAdvancing: true };
  assert.equal(describe('save-replay', { local: live }, {}, false, false, .3).label, '2:01');
  assert.equal(bufferSeconds(live, 60), 122.05);
  assert.equal(bufferSeconds({ ...live, bufferedSeconds: 0 }, .5), 0);
  assert.equal(bufferSeconds({ ...live, bufferAdvancing: false }, .5), 120.8);
  assert.equal(bufferSeconds({ ...live, replayError: 'stalled' }, .5), 120.8);
  assert.equal(bufferSeconds({ ...live, bufferedSeconds: 299.8 }, .5), 300);
  assert.equal(describe('save-replay', { local: { ...live, bufferedSeconds: 2 } }).label, '0:02');
  assert.equal(describe('save-replay', { local: { ...live, bufferedSeconds: 3661, durationSeconds: 7200 } }).label, '1:01:01');
  assert.equal(describe('save-replay', { local: { ...live, saving: true } }, {}, false, true).label, 'Saving');
});
