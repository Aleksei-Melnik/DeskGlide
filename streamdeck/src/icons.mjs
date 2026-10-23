export const actions = {
  'save-replay': { name: 'Save Replay', command: 'saveReplay', glyph: 'M44 22v23m-8-8 8 8 8-8M28 48v8h32v-8' },
  recording: { name: 'Video Recording', command: 'toggleRecording', glyph: 'M60 39a16 16 0 1 1-32 0a16 16 0 1 1 32 0', stop: 'M32 27h24v24H32z', error: 'M44 23v20M44 52v1M28 62h32' },
  'open-kvm': { name: 'Open KVM', command: 'openKvm', glyph: 'M25 22h38v27H25zM36 57h16M44 49v8M38 30l12 6-12 6z' },
  'switch-monitor': { name: 'Switch Monitor', command: 'switchMonitor', glyph: 'M23 23h35v26H23zM33 57h15M40 49v8M50 36h17m-6-6 6 6-6 6' },
  'lock-mouse': { name: 'Lock Mouse Transitions', command: 'toggleEdges', glyph: 'M31 35h26v23H31zM36 35v-9a8 8 0 0 1 16 0v9M44 44v5', unlocked: 'M31 35h26v23H31zM36 35v-9a8 8 0 0 1 16 0M44 44v5' },
  'toggle-replay': { name: 'Toggle Replay', command: 'toggleReplay', glyph: 'M63 38a19 19 0 1 1-7-15M57 16v12H45M40 30l12 8-12 8z', off: 'M63 38a19 19 0 1 1-7-15M57 16v12H45M38 32h12v12H38z' },
  'toggle-streaming': { name: 'Toggle Streaming', command: 'toggleStreaming', glyph: 'M25 22h38v27H25zM36 57h16M44 49v8M39 29l12 7-12 7z', off: 'M25 22h38v27H25zM36 57h16M44 49v8M35 29l18 14' }
};
export function keyImage(kind, view) {
  const mode = view.mode ?? 'ready', spec = actions[kind];
  const dim = ['offline', 'off'].includes(mode), stroke = dim ? '#70727b' : '#f0eff5';
  const color = ({ on: '#31d1db', active: '#31d1db', unlocked: '#31d1db', locked: '#f52b86', saved: '#49d9a0', busy: '#b887ef', queued: '#e1ae62', recording: '#ff4b62', saving: '#e1ae62', error: '#ff657d' })[mode];
  const glyph = kind === 'recording' && mode === 'error' ? spec.error : kind === 'recording' && ['recording', 'saving'].includes(mode) ? spec.stop : kind === 'lock-mouse' && mode === 'unlocked' ? spec.unlocked : mode === 'off' && spec.off ? spec.off : spec.glyph;
  const meter = kind === 'save-replay' ? `<rect x="14" y="82" width="60" height="2" rx="1" fill="#25262e"/><rect x="14" y="82" width="${(60 * Math.max(0, Math.min(1, view.fill || 0))).toFixed(2)}" height="2" rx="1" fill="${color ?? stroke}"/>` : '';
  const dot = color ? `<circle cx="72" cy="14" r="3" fill="${color}"/>` : '';
  const check = mode === 'saved' ? 'M30 39l10 10 20-22' : glyph;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="144" height="144" viewBox="0 0 88 88"><rect width="88" height="88" fill="#0d0e13"/><path d="${check}" fill="none" stroke="${stroke}" stroke-width="2.6" stroke-linejoin="round" stroke-linecap="round"/>${dot}${meter}</svg>`;
}
