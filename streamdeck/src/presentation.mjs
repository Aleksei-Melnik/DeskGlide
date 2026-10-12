export const actions = {
  'save-replay': { name: 'Save Replay', command: 'saveReplay', glyph: 'M34 26a22 22 0 1 0 12-3 M22 17v19h19 M42 47h18v19H42z' },
  'open-kvm': { name: 'Open KVM', command: 'openKvm', glyph: 'M20 24h48v32H20z M36 64h16 M44 56v8 M52 37l-9 9' },
  'lock-mouse': { name: 'Lock Mouse Transitions', command: 'toggleEdges', glyph: 'M30 38h28v28H30z M35 38V27a9 9 0 0 1 18 0v11 M44 48v8' },
  'toggle-replay': { name: 'Toggle Replay', command: 'toggleReplay', glyph: 'M26 25a25 25 0 1 0 13-5 M20 14v18h18 M42 39h11v18H42z' },
  'toggle-streaming': { name: 'Toggle Streaming', command: 'toggleStreaming', glyph: 'M20 25h48v32H20z M34 65h20 M44 57v8 M37 34l16 7-16 8z' }
};
export const uuid = 'com.yami.deskglide';
export const translate = (english, ru) => ru ? ({
  Offline: 'Нет связи', Ready: 'Готово', Saving: 'Сохраняю', Queued: 'Копирую', Saved: 'Сохранено', Error: 'Ошибка',
  Replay: 'Повтор', On: 'Вкл', Off: 'Выкл', KVM: 'KVM', Locked: 'Блок', Edges: 'Переходы', Streaming: 'Трансляция'
}[english] ?? english) : english;
export function stateFor(snapshot, settings = {}) { return settings.target === 'host' ? snapshot?.host : snapshot?.local; }
export function describe(kind, snapshot, settings = {}, busy = false, saved = false) {
  const state = stateFor(snapshot, settings), ru = settings.language === 'ru', t = x => translate(x, ru);
  if (!state) return { label: t('Offline'), color: '#626271', fill: 0, state: 0 };
  switch (kind) {
    case 'save-replay': {
      const fill = Math.max(0, Math.min(1, state.bufferedSeconds / Math.max(1, state.durationSeconds)));
      const time = `${Math.floor(state.bufferedSeconds / 60)}:${String(Math.floor(state.bufferedSeconds % 60)).padStart(2, '0')}`;
      if (saved) return { label: t('Saved'), color: '#38dfab', fill, state: 1 };
      if (state.saving || busy) return { label: t('Saving'), color: '#bd70ff', fill, state: 0 };
      if (state.saveState === 'queued') return { label: t('Queued'), color: '#e3b35d', fill, state: 0 };
      if (state.replayError || state.saveState === 'error') return { label: t('Error'), color: '#ff657d', fill, state: 0 };
      return { label: state.replayEnabled ? time : t('Off'), color: state.replayEnabled ? '#ff398e' : '#626271', fill, state: 0 };
    }
    case 'open-kvm': {
      const peer = state.peers.find(p => p.id === settings.peer);
      if (settings.peer && !peer) return { label: t('Offline'), color: '#626271', fill: 0, state: 0 };
      return { label: peer ? peer.name.slice(0, 12) : `KVM · ${state.peers.length}`, color: '#38d7e3', fill: 0, state: 0 };
    }
    case 'lock-mouse': return { label: t(state.seamless ? 'Edges' : 'Locked'), color: state.seamless ? '#38d7e3' : '#ff398e', fill: 0, state: state.seamless ? 0 : 1 };
    case 'toggle-replay': return { label: `${t('Replay')}\n${t(state.replayEnabled ? 'On' : 'Off')}`, color: state.replayEnabled ? '#ff398e' : '#626271', fill: 0, state: state.replayEnabled ? 1 : 0 };
    case 'toggle-streaming': return { label: `${t('Streaming')}\n${t(state.streaming ? 'On' : 'Off')}`, color: state.streaming ? '#38d7e3' : '#626271', fill: 0, state: state.streaming ? 1 : 0 };
  }
}
export function keyImage(kind, view) {
  return `<svg xmlns="http://www.w3.org/2000/svg" width="144" height="144" viewBox="0 0 88 88"><defs><linearGradient id="bg" x2="1" y2="1"><stop stop-color="#302237"/><stop offset="1" stop-color="#101119"/></linearGradient></defs><rect width="88" height="88" rx="10" fill="url(#bg)"/><path d="${actions[kind].glyph}" fill="none" stroke="${view.color}" stroke-width="3.5" stroke-linejoin="round" stroke-linecap="round"/><rect x="8" y="78" width="72" height="3" rx="1.5" fill="#35303f"/><rect x="8" y="78" width="${Math.max(.01, 72 * view.fill).toFixed(2)}" height="3" rx="1.5" fill="${view.color}"/></svg>`;
}
