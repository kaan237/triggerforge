# Phase 5a contract: local audio → haptics, rumble → haptics, click feel, speaker

Adds to `docs/contract.md`, `docs/contract-phase3.md` and `docs/contract-phase4.md`. Nothing
there changes except where this file says so. Protocol facts live in `docs/protocol.md`; code
is written from those notes, never from other projects' code.

Goal: with the controller on Bluetooth (no drivers), the voice-coil actuators can follow
system or app audio, rumble can be felt as a soft haptic wave, buttons can give a short
click, and one app's audio can play from the controller speaker.

Everything here works on **Bluetooth only**. Over USB the haptics are an audio endpoint
(phase 5b territory); the UI shows a notice instead.

## Work split (done one after another, each reviewed before the next)

| Job | Content | Files |
|---|---|---|
| 1 | Audio capture (WASAPI loopback, process loopback, app list) and audio → haptic DSP | `src/Tactile.Audio/**` (not `Contracts.cs`), `tests/Tactile.Tests/Audio/**` |
| 2 | Haptic engine in core: one stream, mixes audio + rumble + clicks; profile model, validation, bridge, UI page | `src/Tactile.Core/Haptics/**`, small edits in `Tactile.Core`, `Tactile.Device.DualSense/DualSenseController.cs`, `Tactile.Host/Bridge.cs`, `ui/`; tests in `tests/Tactile.Tests/Haptics/**` |
| 3 | Speaker: libopus binding, Opus framing, combined report, speaker source in the engine and UI | see "Job 3" below (filled in before job 3 starts) |

`src/Tactile.Audio/Contracts.cs` is fixed. If a job needs a change, it says why in its
final report and works around it.

References: `Tactile.Audio` → nothing from the app (BCL + Win32 only). `Tactile.Core` →
`Tactile.Audio`. Tests → `Tactile.Audio`. (Already added.)

## Safety rules (every job)

- Nothing is **run** that sends output to the controller, input to the PC, or changes system
  audio settings. Unit tests use pure code and fakes. `dotnet build` and `dotnet test` are
  allowed; running `Tactile.Host` or `tools/` is not.
- Capture is read-only loopback. Never change the default device, volume, or mute.
- Every Win32/COM failure becomes an `AudioUnavailableException` / `Failed` event / notice
  with a Turkish message; the rest of the app keeps working.
- No code copied or closely paraphrased from other projects (GPL/AGPL sources especially).
  Microsoft's documented COM interfaces and GUIDs are fine.
- No new NuGet packages. Own `[ComImport]`/`LibraryImport` interop, in the style of
  `src/Tactile.Input.Windows/WindowsMicrophone.cs`.
- Code comments describe the code only. Do not commit.
- Build must stay warning-free (`TreatWarningsAsErrors`). Restore with
  `DOTNET_SYSTEM_NET_DISABLEIPV6=1` set (this machine hangs on IPv6).

## Job 1: `Tactile.Audio`

### Capture (`IAudioSystem`, `IAudioCapture`)

- `public static class AudioSystemFactory { public static IAudioSystem Create(); }`
- `SystemAudio`: `IMMDeviceEnumerator.GetDefaultAudioEndpoint(eRender, eConsole)` →
  `IAudioClient` shared mode, `AUDCLNT_STREAMFLAGS_LOOPBACK | EVENTCALLBACK` (event-driven;
  if event callback is not supported for loopback on this OS, a 5 ms polling loop is fine).
  If the default device changes or is removed, raise `Failed` once with a Turkish message.
- `AppAudio`: process loopback through `ActivateAudioInterfaceAsync` with
  `VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK` and `AUDIOCLIENT_ACTIVATION_PARAMS`
  (`PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE`). Needs Windows 10 2004+; older
  systems throw `AudioUnavailableException("Uygulama sesi yakalama bu Windows sürümünde yok.")`.
  Process not running → `AudioUnavailableException`. Target process exits → `Failed`.
- Output format is always **48 kHz stereo float**. Ask the engine for it
  (`AUTOCONVERTPCM | SRC_DEFAULT_QUALITY` on the shared-mode loopback client; process
  loopback accepts the requested format). If a device still delivers another rate or
  channel count, convert in code (mono → both channels, >2 channels → first two, linear
  resample to 48 kHz). Silent packets (`AUDCLNT_BUFFERFLAGS_SILENT`) are delivered as zeros.
- Capture thread: dedicated, `AboveNormal` priority, MMCSS "Pro Audio" via
  `AvSetMmThreadCharacteristics` when available. Dispose stops within 500 ms and never
  throws.
- `ListApps()`: `IAudioSessionManager2` → session enumerator on the default render device,
  sessions in state active, skip the system sounds session and our own process, one entry
  per process name (lowest pid), `DisplayName` = session display name or the process file
  name without `.exe`. Failure → empty list.

### DSP: `public sealed class AudioHaptics`

Turns the captured 48 kHz stereo stream into the 3 kHz s8 stereo payloads `HapticReport`
expects (32 frames, 64 values, interleaved L/R; see `docs/protocol.md`).

```csharp
public sealed class AudioHaptics
{
    public AudioHaptics(HapticMode mode);
    public HapticMode Mode { get; set; }        // takes effect on the next block
    public float Gain { get; set; } = 1f;       // 0-2, clamped
    public void Push(ReadOnlySpan<float> stereo48k);   // capture thread; any length
    public bool Read(Span<sbyte> payload);      // haptic thread; 64 values; false = no data (payload set to silence)
    public int BufferedBlocks { get; }          // whole 512-frame blocks waiting
    public void Clear();
}
```

- Thread-safe for one writer and one reader (lock or SPSC ring, no allocation per call).
- Latency cap: keep at most 4 blocks (≈43 ms); older audio is dropped when the writer gets
  ahead. Reader with no full block returns false and silence (never blocks).
- Per block: filter at 48 kHz, then decimate 16:1 to 3 kHz (average of 16, like
  `HapticReport.Downsample`), then gain, soft clip (`tanh`-like), quantise to s8 -127..127.
- Modes (one shared filter chain, different settings; exact numbers are the job's choice
  within these ranges, documented as constants):
  - `Game`: band 20-400 Hz, light compression, noise gate around -45 dBFS. Sharp hits stay sharp.
  - `Film`: low-pass ~120-150 Hz, slower attack/release envelope, stronger compression so
    dialogue does not buzz; gate around -40 dBFS.
  - `Music`: low-pass ~150-200 Hz plus a transient boost (kick/bass onsets), gate around -50 dBFS.
- Stereo is kept (left actuator from left channel, right from right).
- Filters are simple IIR (biquad) with state kept across blocks; changing `Mode` must not
  click (reset or crossfade is fine).

### Tests (`tests/Tactile.Tests/Audio/`)

Pure DSP only (no devices): silence → silence; full-scale 60 Hz sine → strong output on the
same side only; 2 kHz sine → near zero in `Film` and `Music`; gate holds very quiet input at
0; gain 0 → silence; latency cap drops old blocks; `Read` with no data returns false;
odd-sized pushes add up correctly; mono/multichannel/resample conversion helpers if they are
separate units.

## Job 2: haptic engine in core

### Profile (`Profile.Haptics`, new)

```jsonc
"haptics": {
  "audio": "off",            // off | system | app
  "app": "",                 // exe name for "app", e.g. "Spotify.exe"
  "mode": "game",            // game | film | music
  "strength": 1.0,           // 0-2
  "rumbleToHaptics": false,  // true: rumble is always felt as a haptic wave (stream stays open)
  "clicks": false,           // short click on button presses and trigger click points
  "clickStrength": 0.6       // 0-1
}
```

C#: `public sealed record HapticsConfig` on `Profile` with `public HapticsConfig Haptics { get; init; } = new();`,
enum `AudioHapticSource { Off, System, App }`, reuse `Tactile.Audio.HapticMode`.
Validation (`Storage/Validation.cs`): clamp numbers, unknown enum → default, `app` trimmed,
at most 260 chars. Old profiles without the field load with defaults (no version bump).

### Engine (`src/Tactile.Core/Haptics/HapticEngine.cs`)

- Owned by `AppCore`, created with an `IAudioSystem` (injectable; tests use a fake).
- **Needs a stream** when the active profile has any of: `audio != off`, `clicks`,
  `rumbleToHaptics`. While it needs one and a Bluetooth controller is connected, exactly one
  `HapticStreamer` runs; otherwise none. Reconnect / profile switch / USB link are handled.
- Source of the streamer: a mixer called once per 10.667 ms block that sums
  1. audio: `AudioHaptics.Read` (× `strength`),
  2. rumble: from the **composited** output's `RumbleLeft/RumbleRight` (left = large/low
     motor → left actuator, right = small/high motor → right actuator). Synthesis: low motor
     ≈ 45-60 Hz sine, high motor ≈ 150-200 Hz sine, amplitude from the motor value with a
     short smoothing ramp (no clicks on change), × profile `rumble.strength` already applied
     upstream. While a stream is open, motor rumble is ignored by the controller, so rumble
     is **always** converted while streaming; `rumbleToHaptics` only decides whether rumble
     alone opens the stream.
  3. clicks: on a press edge of face buttons, D-pad, L1/R1, L3/R3, Options, Create, PS,
     touchpad click, and when L2/R2 cross 50% going down, queue a ~8-15 ms decaying burst
     (~250 Hz) on the matching side (left-side controls → left actuator, right-side →
     right, centre → both) × `clickStrength`. Clicks from many presses add up, clamped.
  Sum, then clamp to s8. The mixer never allocates per block.
- Audio capture lifecycle: start the capture for the profile target when needed, restart
  when the target changes, stop when not needed. `AppAudio` whose process is not running:
  retry every 3 s quietly; post one notice (`kind` `info`) the first time it fails.
  `Failed` → stop, notice, retry as above.
- Status (for the UI): `HapticStatus(bool Streaming, string Source, string? Problem)`
  where `Source` is `"off" | "system" | "app:<name>"` and `Problem` is a Turkish sentence
  (e.g. "Bluetooth gerekli.", "Spotify.exe çalışmıyor.").
- `ControllerService`: add a way for the engine to own the stream
  (e.g. `SetHapticSource(Func<int, sbyte[]>? source)`), kept across reconnects. The existing
  test tone (`PlayHapticTone`) still works: while a tone plays it takes over, afterwards the
  engine source resumes.
- `HapticStreamer` may be changed to accept a fill-in-place source
  (`Action<int, Span<sbyte>>`) so the mixer does not allocate; keep `Tone` working.

### Bridge

UI → host:

| type | fields | reply |
|---|---|---|
| `audio.apps` | — | `audioApps` with `apps: [{ pid, name, display }]` |

Host → UI:

| type | fields |
|---|---|
| `haptics` | `streaming` bool, `source` string, `problem` string or null; sent on change and after `hello` |

Profile save/load carries `haptics` like every other profile field.

### UI

New page `ui/src/pages/Haptics.tsx`, rail entry "Haptik" (after Tetikler). Uses the
existing components and styles (`ui.tsx`, `styles.css`; design rules in PLAN.md 2b, no
gradients/glass/emoji). Sections:

- **Sesten haptik**: source segmented control (Kapalı / Sistem sesi / Uygulama); app picker
  (button "Çalan uygulamalar" → `audio.apps`, list to choose; free text also allowed);
  mode (Oyun / Film / Müzik) with one-line descriptions; strength slider 0-200 %.
- **Rumble**: switch "Rumble'ı haptik dalga olarak ver" with a one-line explanation.
- **Tık hissi**: switch + strength slider.
- Status line from the `haptics` message; when the controller is on USB show
  "Haptik akışı şimdilik yalnız Bluetooth'ta çalışır."
- `sim.ts` (browser preview without host): answer `audio.apps` with two fake apps and send a
  `haptics` status, so the page can be checked in a normal browser.

### Tests (`tests/Tactile.Tests/Haptics/`)

Fake audio system and fake controller sink: stream needed/not needed per profile; rumble
synthesis amplitude follows motor values, left/right mapping, ramp has no jump larger than a
set step; click on press edge only (not while held), trigger crossing; mixer clamps; app
target retry and single notice; profile JSON round-trip and validation clamps; old profile
without `haptics` loads.

## Job 3: speaker

Protocol: `docs/protocol.md` "Speaker + haptics — report `0x36`". Library: libopus 1.6.1,
built from the official Xiph release by `third_party/opus/build-opus.sh` into
`third_party/opus/win-x64/opus.dll` (static CRT, x64, BSD-3; licence text in
`third_party/opus/COPYING`). Do not rebuild or replace the DLL.

### Files

| Where | What |
|---|---|
| `src/Tactile.Audio/Opus.cs` | `OpusEncoder` P/Invoke wrapper (`LibraryImport("opus")`) |
| `src/Tactile.Audio/SpeakerFeed.cs` | 48 kHz capture → one 200-byte Opus frame per tick |
| `src/Tactile.Audio/Tactile.Audio.csproj` | copy `opus.dll` to output, and `COPYING` as `licenses/opus.txt` |
| `src/Tactile.Device.DualSense/HapticReport.cs` | `BuildSpeaker` for `0x36` |
| `src/Tactile.Device.DualSense/DualSenseController.cs` | `HapticStreamer` sends `0x36` when a speaker frame is present |
| `src/Tactile.Core/Haptics/**`, `Model`, `Storage/Validation.cs`, `Host/Bridge.cs`, `ui/` | speaker source in the engine, profile, UI |
| `tests/Tactile.Tests/Speaker/**` | tests |

### `OpusEncoder`

```csharp
public sealed class OpusEncoder : IDisposable
{
    public const int SampleRate = 48000, Channels = 2, FrameSamples = 480, FrameBytes = 200;
    public OpusEncoder();                                    // throws AudioUnavailableException if opus.dll is missing
    public void Encode(ReadOnlySpan<float> stereo480, Span<byte> frame200);   // always fills exactly 200 bytes
}
```

Settings: application `OPUS_APPLICATION_RESTRICTED_LOWDELAY`, bitrate 160000, VBR off
(hard CBR), complexity 10, signal music (raised from 5 after the 2026-11-19 hardware test). If a packet comes out shorter than 200 bytes, pad it to 200 with
`opus_packet_pad`. Negative return codes become `AudioUnavailableException`.

### `SpeakerFeed`

Same threading and latency rules as `AudioHaptics` (one writer, one reader, at most 4 blocks,
no allocation per call).

```csharp
public sealed class SpeakerFeed : IDisposable
{
    public SpeakerFeed();                             // owns an OpusEncoder
    public float Gain { get; set; } = 1f;             // 0-1
    public void Push(ReadOnlySpan<float> stereo48k);
    public bool Read(Span<byte> frame200);            // false = no audio; frame left untouched
    public void Clear();
}
```

Per tick: take 512 stereo frames, mix to mono (internal speaker is mono) and put the same
signal on both channels, linear resample 512 → 480, gain, encode. No full block → false (the
streamer then sends `0x32` for that tick, or an encoded silence frame if the speaker is on,
so the controller's speaker buffer does not underrun: the engine decides).

### `0x36` builder

`public byte[] BuildSpeaker(ReadOnlySpan<sbyte> haptics64, ReadOnlySpan<byte> opus200)` on
`HapticReport`: layout exactly as in `docs/protocol.md`, sharing `_sequence` and `_counter`
with `Build`, CRC with `Crc32.OutputSeed`. `public bool IncludeStateBlock { get; set; }`
(default false) inserts the 63-byte `0x90` state sub-packet variant (state from the last prime)
for hardware testing.

### Streamer and engine

- The streamer's per-tick source gets a second, optional output: a 200-byte speaker frame.
  When present, the tick sends `0x36`; otherwise `0x32`. One report per tick, never both.
- Profile gets:

```jsonc
"haptics": {
  // job 2 fields …
  "speaker": "off",          // off | system | app
  "speakerApp": "",          // exe name for "app"
  "speakerVolume": 0.8       // 0-1 → OutputState.SpeakerVolume 0-100
}
```

- Speaker on also needs the stream. Audio capture is shared: if haptics and speaker use the
  same target, one capture feeds both; different targets → two captures.
- While the speaker is on, an output layer sets `SpeakerVolume`; when off, the layer is removed.
- While the speaker is on and no audio has arrived, send encoded silence (not `0x32`), so
  playback does not stutter on resume.

### UI

On the "Haptik" page, section **Kontrolcü hoparlörü**: source (Kapalı / Sistem sesi /
Uygulama) with the same app picker, volume slider 0-100 %, and a one-line note that
"Sistem sesi" sends everything including notifications. `sim.ts` unchanged except the new
fields.

### Tests (`tests/Tactile.Tests/Speaker/`)

`OpusEncoder` with the real DLL (pure computation, no device): silence and a 1 kHz sine both
give exactly 200 bytes, valid TOC byte (48 kHz, 10 ms config); `SpeakerFeed` resample length
and mono mix; `0x36` layout (ids, lengths, offsets 77-78 = `0x93 200`, CRC checks with
`Crc32`), sequence shared with `0x32`; profile round-trip and validation; engine sends speaker
frames only when the profile asks.
