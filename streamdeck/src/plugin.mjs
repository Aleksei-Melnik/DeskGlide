import streamDeck, { SingletonAction, action } from '@elgato/streamdeck';
import { Bridge } from './bridge.mjs';
import { actions, uuid, describe, keyImage, stateFor } from './presentation.mjs';

const bridge = new Bridge(process.env.DESKGLIDE_TEST_DESCRIPTOR || undefined);
class DeskAction extends SingletonAction {
  constructor(kind) { super(); this.kind = kind; this.visible = new Map(); }
  onWillAppear(ev) {
    if (!ev.action.isKey()) return;
    const settings = { ...ev.payload.settings, language: streamDeck.info.application.language === 'ru' ? 'ru' : 'en' };
    if (this.kind === 'open-kvm') settings.target = 'local';
    this.visible.set(ev.action.id, { action: ev.action, settings, busy: false }); this.renderAll();
  }
  onWillDisappear(ev) { this.visible.delete(ev.action.id); }
  onDidReceiveSettings(ev) { const entry = this.visible.get(ev.action.id); if (entry) { entry.settings = { ...ev.payload.settings, language: streamDeck.info.application.language === 'ru' ? 'ru' : 'en' }; if (this.kind === 'open-kvm') entry.settings.target = 'local'; entry.last = null; this.renderAll(); } }
  onPropertyInspectorDidAppear() { this.sendInspector(); }
  onSendToPlugin() { this.sendInspector(); }
  sendInspector() {
    return streamDeck.ui.sendToPropertyInspector({ type: 'status', state: bridge.snapshot,
      help: 'https://github.com/Aleksei-Melnik/DeskGlide/blob/main/docs/STREAM-DECK.md' }).catch(() => {});
  }
  async onKeyDown(ev) {
    const entry = this.visible.get(ev.action.id);
    if (!entry || entry.busy) return;
    entry.busy = true; await this.renderAll();
    try {
      const settings = entry.settings;
      const reply = await bridge.request(actions[this.kind].command, this.kind === 'open-kvm' ? 'local' : settings.target ?? 'local', this.kind === 'open-kvm' ? settings.peer ?? '' : '', this.kind === 'switch-monitor' ? settings.monitor ?? '' : '');
      if (!reply.ok) throw new Error(reply.message || reply.code);
      if (this.kind !== 'save-replay') await ev.action.showOk();
      else if (stateFor(reply.state, settings)?.saveState === 'saved') { entry.savedUntil = Date.now() + 6000; await ev.action.showOk(); }
    } catch (e) {
      streamDeck.logger.warn(`${this.kind}: ${e.message}`); await ev.action.showAlert();
      streamDeck.ui.sendToPropertyInspector({ type: 'error', message: e.message }).catch(() => {});
    } finally { entry.busy = false; entry.last = null; await this.renderAll(); }
  }
  async renderAll() {
    if (this.rendering) { this.renderAgain = true; return; }
    this.rendering = true;
    try { do {
    this.renderAgain = false;
    for (const entry of this.visible.values()) {
      const current = stateFor(bridge.snapshot, entry.settings);
      if (entry.revision !== undefined && current?.saveRevision > entry.revision && this.kind === 'save-replay') {
        entry.savedUntil = Date.now() + 6000; entry.action.showOk().catch(() => {});
      }
      if (current) entry.revision = current.saveRevision; else entry.revision = undefined;
      const view = describe(this.kind, bridge.snapshot, entry.settings, entry.busy, entry.savedUntil > Date.now(), (Date.now() - (bridge.receivedAt ?? Date.now())) / 1000);
      const signature = JSON.stringify(view); if (entry.last === signature) continue; entry.last = signature;
      try {
        await entry.action.setState(view.state);
        await entry.action.setTitle(view.label, { fontSize: 10, titleAlignment: 'bottom' });
        await entry.action.setImage(keyImage(this.kind, view));
      } catch { /* Context disappeared or Stream Deck is shutting down. */ }
    }
    } while (this.renderAgain); } finally { this.rendering = false; }
  }
}
const registered = Object.keys(actions).map(kind => {
  const Registered = action({ UUID: `${uuid}.${kind}` })(DeskAction);
  const instance = new Registered(kind); streamDeck.actions.registerAction(instance); return instance;
});
bridge.on('status', () => { for (const a of registered) { a.renderAll(); a.sendInspector(); } });
setInterval(() => { for (const a of registered) a.renderAll(); }, 250);
streamDeck.connect().then(() => bridge.connect());
process.on('SIGTERM', () => { bridge.close(); process.exit(0); });
