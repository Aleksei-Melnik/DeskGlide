import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { spawn } from 'node:child_process';
import { WebSocketServer } from 'ws';
import { uuid } from '../src/presentation.mjs';
const executable = process.env.DESKGLIDE_TEST_EXE;
const sleep = ms => new Promise(r => setTimeout(r, ms));
async function until(fn, label) { for (let i = 0; i < 100; i++) { if (await fn()) return; await sleep(100); } throw new Error(`Timed out: ${label}`); }
test('official SDK -> Windows pipe -> DeskGlide reply; disconnect and reconnect', { skip: !executable, timeout: 35000 }, async () => {
  const folder = await mkdtemp(join(tmpdir(), 'DeskGlide-SDK-'));
  const descriptor = join(folder, 'bridge.json'), transcript = join(folder, 'commands.jsonl');
  let server, plugin, socket; const messages = []; let errors = '';
  const websocket = new WebSocketServer({ port: 0 }); await new Promise(r => websocket.on('listening', r));
  websocket.on('connection', ws => { socket = ws; ws.on('message', x => messages.push(JSON.parse(x))); });
  const launch = () => spawn(resolve(executable), ['--streamdeck-fixture', descriptor, transcript], { windowsHide: true, stdio: 'ignore' });
  try {
    server = launch(); await until(async () => { try { return JSON.parse(await readFile(descriptor, 'utf8')).protocol === 1; } catch { return false; } }, 'private server');
    const info = { application: { language: 'en', platform: 'windows', platformVersion: '10.0', version: '7.4.2' }, devices: [{ id: 'deck', name: 'Test Stream Deck', type: 0, size: { columns: 5, rows: 3 } }], plugin: { uuid, version: '1.0.0.0' } };
    plugin = spawn(process.execPath, ['bin/plugin.js', '-port', String(websocket.address().port), '-pluginUUID', 'test-plugin', '-registerEvent', 'registerPlugin', '-info', JSON.stringify(info)],
      { cwd: resolve(`${uuid}.sdPlugin`), env: { ...process.env, DESKGLIDE_TEST_DESCRIPTOR: descriptor }, windowsHide: true });
    plugin.stderr.on('data', x => { errors += x; });
    await until(() => messages.some(m => m.event === 'registerPlugin'), 'official SDK registration');
    function event(type, kind, context, settings = {}) {
      socket.send(JSON.stringify({ event: type, action: `${uuid}.${kind}`, context, device: 'deck', payload: { settings, coordinates: { column: 0, row: 0 }, controller: 'Keypad', state: 0, isInMultiAction: false } }));
    }
    event('willAppear', 'save-replay', 'save');
    event('willAppear', 'open-kvm', 'kvm', { peer: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' });
    event('willAppear', 'switch-monitor', 'monitor', { monitor: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa|DISPLAY1' });
    await until(() => messages.some(m => m.event === 'setTitle' && m.context === 'save' && m.payload.title === '2:00'), 'live buffer title');
    event('keyDown', 'save-replay', 'save');
    event('keyDown', 'open-kvm', 'kvm', { peer: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' });
    event('keyDown', 'switch-monitor', 'monitor', { monitor: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa|DISPLAY1' });
    await until(async () => { try { const lines = (await readFile(transcript, 'utf8')).trim().split('\n').map(x => JSON.parse(x)); return lines.some(r => r.command === 'saveReplay') && lines.some(r => r.command === 'openKvm' && r.peer === 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'); } catch { return false; } }, 'typed commands');
    await until(() => messages.some(m => m.event === 'showOk' && m.context === 'kvm'), 'acknowledged action');
    await until(async () => { const lines = (await readFile(transcript, 'utf8')).trim().split('\n').map(x => JSON.parse(x)); return lines.some(r => r.command === 'switchMonitor' && r.monitor === 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa|DISPLAY1'); }, 'monitor command without hotkey emulation');
    server.kill(); await new Promise(r => server.once('exit', r));
    await until(() => messages.some(m => m.event === 'setTitle' && m.context === 'save' && m.payload.title === 'Offline'), 'disconnect feedback');
    const before = (await readFile(transcript, 'utf8')).trim().split('\n').length;
    event('keyDown', 'save-replay', 'save'); await until(() => messages.some(m => m.event === 'showAlert' && m.context === 'save'), 'offline error feedback');
    server = launch(); messages.length = 0;
    await until(() => messages.some(m => m.event === 'setTitle' && m.context === 'save' && m.payload.title === '2:00'), 'restart/reconnect');
    assert.equal((await readFile(transcript, 'utf8')).trim().split('\n').length, before, 'Disconnected command must never retry');
    assert.equal(errors, '');
  } finally {
    socket?.terminate(); plugin?.kill(); server?.kill(); websocket.close();
    await sleep(300); await rm(folder, { recursive: true, force: true });
  }
});
