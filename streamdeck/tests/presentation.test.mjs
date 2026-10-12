import test from 'node:test';
import assert from 'node:assert/strict';
import { actions, describe, keyImage, stateFor } from '../src/presentation.mjs';
const local = { name: 'Gaming', version: '0.7.19', replayEnabled: true, bufferedSeconds: 120, durationSeconds: 300,
  streaming: true, seamless: true, saveRevision: 0, saveState: 'idle', peers: [{ id: 'peer', name: 'Server' }] };
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
