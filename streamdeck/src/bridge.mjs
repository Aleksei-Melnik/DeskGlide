import { EventEmitter } from 'node:events';
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { createConnection } from 'node:net';
import { randomUUID } from 'node:crypto';

export const requestTimeout = command => ['saveReplay', 'toggleRecording'].includes(command) ? 600000 : 12000;

export class Bridge extends EventEmitter {
  constructor(descriptor = join(process.env.LOCALAPPDATA ?? '', 'SdrCapture', 'streamdeck-bridge.json')) {
    super(); this.descriptor = descriptor; this.pending = new Map(); this.snapshot = null; this.stopped = false;
  }
  async connect() {
    if (this.stopped || this.socket || this.connecting) return;
    this.connecting = true;
    try {
      const text = await readFile(this.descriptor, 'utf8');
      if (text.length > 1024) throw new Error('Invalid DeskGlide discovery file');
      const info = JSON.parse(text);
      if (info.protocol !== 1 || !/^DeskGlide\.StreamDeck\.[A-Za-z0-9.-]+$/.test(info.pipe)) throw new Error('Incompatible DeskGlide bridge');
      if (this.stopped) return;
      const socket = createConnection(`\\\\.\\pipe\\${info.pipe}`); this.socket = socket;
      let data = '';
      socket.setEncoding('utf8');
      socket.on('data', chunk => {
        data += chunk;
        if (data.length > 65536) { socket.destroy(); return; }
        let pos;
        while ((pos = data.indexOf('\n')) >= 0) {
          const line = data.slice(0, pos); data = data.slice(pos + 1);
          try {
            const message = JSON.parse(line);
            if (message.type === 'status' && validSnapshot(message.state)) { this.snapshot = message.state; this.receivedAt = Date.now(); this.emit('status', this.snapshot); }
            else if (message.type === 'reply' && typeof message.reply?.id === 'string') {
              const request = this.pending.get(message.reply.id);
              if (request) { clearTimeout(request.timer); this.pending.delete(message.reply.id); request.resolve(message.reply); }
              if (validSnapshot(message.reply.state)) { this.snapshot = message.reply.state; this.receivedAt = Date.now(); this.emit('status', this.snapshot); }
            }
          } catch { socket.destroy(); }
        }
      });
      socket.on('error', () => {});
      socket.on('close', () => {
        if (this.socket !== socket) return;
        this.socket = null; this.snapshot = null;
        for (const request of this.pending.values()) { clearTimeout(request.timer); request.reject(new Error('DeskGlide disconnected. The command was not retried.')); }
        this.pending.clear(); this.emit('status', null); this.retry();
      });
    } catch { this.retry(); }
    finally { this.connecting = false; }
  }
  retry() { clearTimeout(this.retryTimer); if (!this.stopped) this.retryTimer = setTimeout(() => this.connect(), 1500); }
  request(command, target = 'local', peer = '', monitor = '') {
    if (!this.socket || !this.snapshot) return Promise.reject(new Error('Start DeskGlide 0.7.19 or later on this PC.'));
    if (this.pending.size >= 8) return Promise.reject(new Error('Too many pending commands.'));
    const id = randomUUID().replaceAll('-', '');
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this.pending.delete(id); reject(new Error('No confirmation received. The command was not retried.')); }, requestTimeout(command));
      this.pending.set(id, { resolve, reject, timer });
      this.socket.write(JSON.stringify({ id, command, target, peer, monitor }) + '\n');
    });
  }
  close() { this.stopped = true; clearTimeout(this.retryTimer); this.socket?.destroy(); }
}
export function validSnapshot(value) {
  return value && validState(value.local) && (!value.host || validState(value.host));
}
function validState(value) {
  return value && typeof value.name === 'string' && typeof value.version === 'string' && typeof value.replayEnabled === 'boolean'
    && Number.isFinite(value.bufferedSeconds) && Number.isFinite(value.durationSeconds) && Array.isArray(value.peers)
    && ['recording', 'recordingBusy', 'recordingSupported'].every(key => value[key] === undefined || typeof value[key] === 'boolean')
    && ['recordingError', 'recordingStatus'].every(key => value[key] == null || typeof value[key] === 'string' && value[key].length <= 4096);
}
