let socket, context, actionId, settings = {}, snapshot;
const el = id => document.getElementById(id);
window.connectElgatoStreamDeckSocket = function(port, uuid, event, info, actionInfo) {
  const app = JSON.parse(info), action = JSON.parse(actionInfo);
  context = uuid; actionId = action.action; settings = { language: app.application.language === 'ru' ? 'ru' : 'en', ...action.payload.settings };
  socket = new WebSocket(`ws://127.0.0.1:${port}`);
  socket.onopen = () => { send(event, undefined, { uuid }); send('sendToPlugin', { type: 'status' }); };
  socket.onmessage = ev => {
    const data = JSON.parse(ev.data);
    if (data.event === 'sendToPropertyInspector') {
      if (data.payload.type === 'status') { snapshot = data.payload.state; update(); }
      if (data.payload.type === 'error') el('error').textContent = data.payload.message;
    } else if (data.event === 'didReceiveSettings') { settings = data.payload.settings; update(); }
  };
  socket.onclose = () => { snapshot = null; update(); };
  update(); document.querySelector('main').hidden = false;
};
function send(event, payload, extra = {}) {
  if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ event, context, action: actionId, ...(payload === undefined ? {} : { payload }), ...extra }));
}
function update() {
  const ru = settings.language === 'ru', kvm = actionId?.endsWith('.open-kvm');
  el('language').value = settings.language ?? 'en'; el('languageLabel').textContent = ru ? 'Язык' : 'Language';
  el('targetRow').hidden = kvm; el('targetLabel').textContent = ru ? 'Выполнять на' : 'Run on';
  el('target').options[0].text = ru ? 'Этот ПК' : 'This PC'; el('target').options[1].text = ru ? 'Связанный управляющий ПК' : 'Paired controller PC';
  el('target').value = settings.target ?? 'local'; el('peerRow').hidden = !kvm;
  el('peerLabel').textContent = ru ? 'Открыть' : 'Open';
  const peerSelect = el('peer'); peerSelect.replaceChildren(new Option(ru ? 'Выбор компьютера' : 'Computer chooser', ''));
  for (const peer of snapshot?.local?.peers ?? []) peerSelect.add(new Option(peer.name, peer.id));
  if (settings.peer && ![...peerSelect.options].some(o => o.value === settings.peer)) peerSelect.add(new Option(ru ? 'ПК отключён' : 'Disconnected PC', settings.peer));
  peerSelect.value = settings.peer ?? '';
  const state = settings.target === 'host' ? snapshot?.host : snapshot?.local;
  el('connection').textContent = state ? `${state.name} · ${state.version}` : (ru ? 'Нет связи' : 'Offline');
  el('details').textContent = state ? (ru ? 'Связь установлена. Настройки сохраняются автоматически.' : 'Connected. Settings save automatically.')
    : (ru ? 'Запустите DeskGlide 0.7.19 или новее. Для управляющего ПК сначала подключите компьютеры в DeskGlide.' : 'Start DeskGlide 0.7.19 or later. To use the controller PC, pair the computers in DeskGlide first.');
  el('help').textContent = ru ? 'Настройка и загрузка DeskGlide' : 'Setup & download DeskGlide';
}
for (const id of ['language', 'target', 'peer']) el(id).addEventListener('change', () => {
  settings[id] = el(id).value; if (actionId?.endsWith('.open-kvm')) settings.target = 'local';
  el('error').textContent = ''; send('setSettings', settings); send('sendToPlugin', { type: 'status' }); update();
});
el('help').addEventListener('click', ev => { ev.preventDefault(); send('openUrl', { url: el('help').href }); });
