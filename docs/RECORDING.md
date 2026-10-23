# Recording and Instant Replay

DeskGlide uses one SDR desktop capture for streaming, replay and continuous
recording. HDR conversion is the same Rec.709 limited-range path used by NDI.
Recordings are SDR MP4 files with AAC audio; this release does not add HDR video.

## Choose where to encode

On **Instant replay** or **Video recording**, choose the encoding computer and
encoding method. **GPU** probes the actual FFmpeg NVENC, AMF or Quick Sync path;
**CPU x264** uses software H.264. GPU never silently falls back to CPU. CPU mode
supports H.264 only. Codec choices appear after a successful capability check for
the selected source size, output size, frame rate and encoder profile.

Quality mode uses the existing quality targets. VBR and CBR expose a bitrate limit
in Mbps; zero selects the automatic resolution/FPS limit. Performance, Balanced
and Quality select encoder presets, and the keyframe interval is configurable.
CPU presets are ultrafast, veryfast and medium respectively. CPU recording can
compete with the game for processor time; choose a suitable profile or a second PC.
The two-frame capability probe establishes format support, not sustained speed.

Profiles, storage folders and audio for ordinary recording are independent of
replay. The tray menu and default **Ctrl+Alt+F9** start/stop ordinary recording;
**Ctrl+Alt+F10** saves replay. Both shortcuts can be changed. The Stream Deck
**Recording** action also starts/stops recording and displays its state. Its
Local/Host selection follows the other plugin actions; the selected DeskGlide
profile decides which computer encodes. Update the Stream Deck plugin to 1.1.0.

When video/audio encoding settings match, replay and ordinary recording share
one encoder. Distinct profiles use separate encoders fed by the same capture.
Disabling replay does not stop ordinary recording. Format changes apply to a new
replay encoder while the current recording keeps its original profile. Stop an
ordinary recording before changing DeskGlide's capture monitor.

## Use a paired encoding PC

1. Update DeskGlide on both PCs and connect them through KVM pairing.
2. On the receiving PC, open **Video recording**, enable **Allow paired PCs to
   encode on this PC**, and choose its replay/recording folders.
3. On the gaming PC, choose the paired encoding PC on the relevant recording page.
   Check its codec capabilities with the intended resolution/FPS before use.
4. Start replay or recording. DeskGlide enables its existing NDI sender as needed
   and requests incoming-stream encoding through the paired TLS control channel.

The receiving PC owns destination paths; a request cannot supply an arbitrary
file path, executable or FFmpeg arguments. The gaming PC supplies its application
name for game subfolders. The video/audio transport is existing NDI High Bandwidth
on the LAN, separate from authenticated control. It is not raw RGB streaming or a
zero-copy game-to-encoder pipeline. NDI runtime setup on each PC is still needed.

Remote recording uses audio carried by the gaming PC's NDI stream, not the
receiver's desktop sound or microphone. Select the streaming audio source on the
gaming PC; if none is selected, recording uses its default playback mix. NDI audio
volume applies. The receiver's corresponding replay/recording audio mode can
disable audio; incoming audio is one stereo mix, not three isolated remote tracks.
Only local encoding provides the independently selected local/KVM audio tracks.

NDI sends 60 or 120 FPS as required by active remote profiles. Resolution, FPS,
network traffic and capture still cost resources on the gaming PC. A 5-Gbps NIC
link alone does not prove sufficient throughput or absence of game FPS impact.
Actual capability probes, source/encoded rates, queue drops, repeats, timing and
resource diagnostics are shown under **Status**. System CPU includes encoder
processes and other apps; the separate DeskGlide-process CPU value excludes
FFmpeg. GPU/encoder utilization and summed NIC traffic are system measurements.
Unavailable GPU counters remain unavailable, rather than showing a false zero.

## Interruptions and saving

Recording pins completed segments independently of the replay window. MP4 export
copies encoded packets and queues atomic delivery to the configured destination.
An unavailable destination retains a local completed file and delivery job.
Unfinished recording journals protect their segments and are retried on startup.
An active ownership lock prevents another recorder from cleaning those files.

Video-source loss pauses input and finalizes the current part; when the source
returns, the same logical recording continues in a new file. Cached audio drains
to the last video frame before closing. A controller connection that stays absent
for 30 seconds ends its session; ordinary recording is not blindly restarted after
an uncertain command. Replay can restore its desired state after reconnecting.
Status heartbeats continue while an export runs. Empty failed starts can be stopped
without claiming a saved video. Windows termination can still lose an open,
unfinished segment; completed protected segments remain recoverable.

## Validation boundaries

The regression suite exercises real FFmpeg CPU files, shared replay/recording,
failed-export recovery, paired TLS request validation/deduplication and native NDI
loopback with synthetic timestamped video/audio. Existing SDR color, frame cadence,
KVM and signed-update checks remain in CI. These checks do not establish hour-long
game stability, physical two-PC LAN performance, RTX 3080 hardware results or
equality with NVIDIA App. Capability checks determine support on the actual PC.
