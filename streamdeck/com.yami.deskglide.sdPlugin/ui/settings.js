let socket, context, actionId, settings = {}, snapshot, language = 'en';
const el = id => document.getElementById(id);
window.connectElgatoStreamDeckSocket = function(port, uuid, event, info, actionInfo) {
  const app = JSON.parse(info), action = JSON.parse(actionInfo);
  context = uuid; actionId = action.action; language = app.application.language === 'ru' ? 'ru' : 'en'; settings = { ...action.payload.settings }; delete settings.language;
  if (actionId.endsWith('.open-kvm')) settings.target = 'local';
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
  const ru = language === 'ru', kvm = actionId?.endsWith('.open-kvm'), monitor = actionId?.endsWith('.switch-monitor'), recording = actionId?.endsWith('.recording');
  el('targetRow').hidden = kvm; el('targetLabel').textContent = ru ? 'Выполнять на' : 'Run on';
  el('target').options[0].text = ru ? 'Этот ПК' : 'This PC'; el('target').options[1].text = ru ? 'Связанный управляющий ПК' : 'Paired controller PC';
  el('target').value = settings.target ?? 'local'; el('peerRow').hidden = !kvm;
  el('peerLabel').textContent = ru ? 'Открыть' : 'Open';
  const state = settings.target === 'host' ? snapshot?.host : snapshot?.local;
  options('peer', [[ru ? 'Выбор компьютера' : 'Computer chooser', ''], ...(snapshot?.local?.peers ?? []).map(p => [p.name, p.id])], settings.peer, ru ? 'ПК отключён' : 'Disconnected PC');
  el('monitorRow').hidden = !monitor; el('monitorLabel').textContent = ru ? 'Монитор' : 'Monitor';
  options('monitor', [[ru ? 'Выберите монитор' : 'Choose a monitor', ''], ...(state?.monitors ?? []).map(m => [`${m.name} · ${m.width} × ${m.height}`, m.id])], settings.monitor, ru ? 'Монитор отключён' : 'Disconnected monitor');
  el('connection').textContent = state ? state.name : (ru ? 'Нет связи' : 'Offline');
  el('statusDot').classList.toggle('online', !!state);
  const details = !state ? (ru ? 'Запустите DeskGlide. Для удалённого управления сначала подключите компьютеры.' : 'Start DeskGlide. Pair your computers first for remote control.')
    : kvm && state.role !== 'Host' ? (ru ? 'Окно KVM открывается на управляющем ПК.' : 'Open KVM is available on the controller PC.')
    : monitor ? (ru ? 'Курсор переходит в центр выбранного монитора. Работает и при заблокированных переходах.' : 'Move the cursor to this monitor. Works with edge switching locked.')
    : recording && !state.recordingSupported ? (ru ? 'Обновите DeskGlide на выбранном ПК для управления видеозаписью.' : 'Update DeskGlide on the selected PC to use Video Recording.')
    : recording ? (ru ? 'Начинает или останавливает отдельную видеозапись. Компьютер записи, папка и формат берутся из настроек «Видеозапись» в DeskGlide на выбранном ПК.' : 'Starts or stops an independent video recording. The encoding computer, folder and format come from Settings → Video recording in DeskGlide on the selected PC.')
    : settings.target === 'host' ? (ru ? 'Команды отправляются на связанный управляющий ПК.' : 'Commands go to your paired controller PC.') : '';
  el('details').textContent = details; el('details').hidden = !details;
  if (recording) el('error').textContent = state?.recordingError ?? '';
  el('help').textContent = ru ? 'Инструкция ↗' : 'Setup guide ↗';
}
function options(id, values, selected = '', offline) {
  if (selected && !values.some(v => v[1] === selected)) values.push([offline, selected]);
  const select = el(id), signature = JSON.stringify(values);
  if (select.dataset.options !== signature) { select.replaceChildren(...values.map(([text, value]) => new Option(text, value))); select.dataset.options = signature; }
  select.value = selected;
}
for (const id of ['target', 'peer', 'monitor']) el(id).addEventListener('change', () => {
  settings[id] = el(id).value; if (actionId?.endsWith('.open-kvm')) settings.target = 'local';
  el('error').textContent = ''; send('setSettings', settings); send('sendToPlugin', { type: 'status' }); update();
});
el('help').addEventListener('click', ev => { ev.preventDefault(); send('openUrl', { url: el('help').href }); });
