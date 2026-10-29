# DeskGlide for Stream Deck

Control DeskGlide with live buttons instead of simulated hotkeys. Free, Windows only.

## Install

1. Update DeskGlide to **0.7.20 or later** on each participating PC.
2. Install the [DeskGlide plugin from Elgato Marketplace](https://marketplace.elgato.com/product/deskglide-756e68ee-b961-40bb-b19c-cb0a39635679).
   **Settings → Stream Deck → Install plugin** opens the same Marketplace listing.
   For installation from a local package, open
   `com.yami.deskglide.streamDeckPlugin` from [GitHub Releases](https://github.com/Aleksei-Melnik/DeskGlide/releases/latest).
3. In Stream Deck, drag a **DeskGlide** action onto a key.

Requires **Stream Deck 7.1 or later**, Windows 10/11, and DeskGlide running in the same Windows user session.
Stream Deck supplies the Node runtime; users do not need to install Node or a developer SDK.
The published Marketplace plugin is version 1.1.1, with the six actions listed below.
The local package remains available for installation without Marketplace.

## Buttons

| Action | Behavior |
| --- | --- |
| Save Replay | Saves new buffered footage since the previous save. Shows buffered time and a fill meter. |
| Open KVM | Opens the computer chooser, or a selected connected computer/server. Runs on the local controller PC. |
| Switch Monitor | Moves the cursor and control to the center of the selected monitor in your layout. Works with mouse edge transitions locked. |
| Lock Mouse Transitions | Toggles movement across monitor edges. Turning it off returns control to the controller PC. |
| Toggle Replay | Enables/disables background recording. |
| Toggle Streaming | Enables/disables outgoing SDR video and audio over NDI. |

Buttons receive DeskGlide state once per second. The replay timer includes written
frames in the open segment and interpolates at most 1.25 seconds between advancing
snapshots. Off, stalled and disconnected recording does not keep counting indefinitely.
**Saving** means the clip is being exported;
**Queued** means it is safe in the local delivery queue and is waiting to reach the destination;
**Saved** appears only after DeskGlide confirms delivery to the selected folder.
An offline PC or failed command shows a warning. Commands are never automatically resent after a disconnect.

## Two PCs / two Stream Decks

Install the plugin on each PC that has a Stream Deck. For replay, streaming and mouse-transition buttons,
choose **This PC** or **Paired controller PC** in the action settings. The second option uses DeskGlide's
existing authenticated KVM connection. Pair the computers in DeskGlide first, and update both copies.
It does not send passwords or pairing codes to Stream Deck.

For **Open KVM**, choose **Computer chooser** or a connected computer, including a server excluded
from mouse transitions. This action opens a viewer on the controller PC where the plugin is running.
For **Switch Monitor**, choose the controller under **Run on**, then select a monitor
from its saved layout. Removed/excluded screens are not offered. Disconnected
selections are kept so you do not need to set up the key again after reconnection.
The plugin follows Stream Deck's language automatically (English or Russian).

## Troubleshooting

- **Offline:** start DeskGlide, update it, and check Settings → Stream Deck. A paired controller must also be online.
- **No buffer / recording error:** enable Instant replay and check DeskGlide's Status page.
- **Queued:** check the destination disk/network share. DeskGlide retains the exported clip locally and retries delivery.
- **Selected PC is disconnected:** reconnect it or choose another PC. The plugin preserves its configured selection.
- **Administrator / different user:** run Stream Deck and DeskGlide as the same Windows user. The bridge is private to that user.

The plugin works through a private Windows named pipe, with no additional network listener, keyboard emulation,
analytics or account sign-in. Network commands use the already paired DeskGlide connection.
Support: [GitHub issues](https://github.com/Aleksei-Melnik/DeskGlide/issues).

## Разовая настройка

Обновите DeskGlide на обоих ПК. Установите [плагин из Elgato Marketplace](https://marketplace.elgato.com/product/deskglide-756e68ee-b961-40bb-b19c-cb0a39635679).
Кнопка **Настройки → Stream Deck → Установить плагин** открывает ту же страницу Marketplace.
Перетащите действие DeskGlide на кнопку в Stream Deck. Для сохранения повтора с кнопки на стрим-ПК
выберите **Связанный управляющий ПК**. Компьютеры должны быть подключены друг к другу в DeskGlide.
«Сохранено» означает, что файл доставлен в выбранную папку; «Копирую» — что он пока находится
в локальной очереди доставки. Повторное нажатие после отключения связи не повторяется автоматически.
