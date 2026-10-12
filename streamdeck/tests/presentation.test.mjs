import test from 'node:test';
import assert from 'node:assert/strict';
import { actions, describe, keyImage, stateFor, bufferSeconds } from '../src/presentation.mjs';
const local = { name: 'Gaming', version: '0.7.20', role: 'Host', replayEnabled: true, bufferedSeconds: 120, durationSeconds: 300,
  streaming: true, seamless: true, saveRevision: 0, saveState: 'idle', peers: [{ id: 'peer', name: 'Server' }], monitors: [{ id: 'left', name: 'Left', active: false }, { id: 'main', name: 'Main', active: true }] };
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
