export { actions, keyImage } from './icons.mjs';
export const uuid = 'com.yami.deskglide';
export const translate = (english, ru) => ru ? ({
  Offline: 'Нет связи', Ready: 'Готово', Saving: 'Сохраняю', Queued: 'Копирую', Saved: 'Сохранено', Error: 'Ошибка',
  Replay: 'Повтор', Off: 'Выкл', Locked: 'Блок', Unlocked: 'Открыто', Stream: 'Экран', Monitor: 'Монитор', Choose: 'Выберите', Unavailable: 'Недоступно'
}[english] ?? english) : english;
export function stateFor(snapshot, settings = {}) { return settings.target === 'host' ? snapshot?.host : snapshot?.local; }
export function bufferSeconds(state, elapsed = 0) {
  const seconds = Number.isFinite(state.bufferedSeconds) ? Math.max(0, state.bufferedSeconds) : 0;
  // Only interpolate a fresh, advancing recorder snapshot. Never run a timer
  // through a stalled encoder, disconnected PC or an empty buffer.
  const advance = state.replayEnabled && state.bufferAdvancing && !state.replayError && seconds > 0 ? Math.min(1.25, Math.max(0, elapsed)) : 0;
  return Math.min(Math.max(0, state.durationSeconds), seconds + advance);
}
export function describe(kind, snapshot, settings = {}, busy = false, saved = false, elapsed = 0) {
  const state = stateFor(snapshot, settings), ru = settings.language === 'ru', t = x => translate(x, ru);
  const view = (label, mode = 'ready', fill = 0) => ({ label, mode, fill, state: ['on', 'locked', 'saved', 'active'].includes(mode) ? 1 : 0 });
  if (!state) return view(t('Offline'), 'offline');
  switch (kind) {
    case 'save-replay': {
      const seconds = bufferSeconds(state, elapsed), fill = seconds / Math.max(1, state.durationSeconds);
      if (state.saving || busy) return view(t('Saving'), 'busy', fill);
      if (state.replayError || state.saveState === 'error') return view(t('Error'), 'error', fill);
      if (state.saveState === 'queued') return view(t('Queued'), 'queued', fill);
      if (saved) return view(t('Saved'), 'saved', fill);
      const whole = Math.floor(seconds + 1e-6), minutes = Math.floor(whole / 60), rest = String(whole % 60).padStart(2, '0');
      const time = minutes >= 60 ? `${Math.floor(minutes / 60)}:${String(minutes % 60).padStart(2, '0')}:${rest}` : `${minutes}:${rest}`;
      return view(state.replayEnabled ? time : t('Off'), state.replayEnabled ? 'on' : 'off', fill);
    }
    case 'open-kvm': {
      if (state.role !== 'Host') return view(t('Unavailable'), 'offline');
      const peer = state.peers.find(p => p.id === settings.peer);
      if (settings.peer && !peer) return view(t('Offline'), 'offline');
      return view(peer ? peer.name.slice(0, 14) : 'KVM');
    }
    case 'switch-monitor': {
      if (state.role !== 'Host' || !state.monitors) return view(t('Unavailable'), 'offline');
      const monitor = state.monitors.find(m => m.id === settings.monitor);
      if (!monitor) return view(t(settings.monitor ? 'Offline' : 'Choose'), 'offline');
      const parts = monitor.name.split(' · '), name = parts.length > 1 ? `${parts[0].slice(0, 10)}\n${parts.at(-1).replace(/^.*\\/, '').slice(0, 12)}` : monitor.name.slice(0, 14);
      return view(name, monitor.active ? 'active' : 'ready');
    }
    case 'lock-mouse': return state.role !== 'Host' ? view(t('Unavailable'), 'offline') : view(t(state.seamless ? 'Unlocked' : 'Locked'), state.seamless ? 'unlocked' : 'locked');
    case 'toggle-replay': return view(t('Replay'), state.replayEnabled ? 'on' : 'off');
    case 'toggle-streaming': return view(t('Stream'), state.streaming ? 'on' : 'off');
  }
}
