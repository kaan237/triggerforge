# Phase 5b contract: virtual DualSense audio (game haptics and speaker over Bluetooth)

Adds to `docs/contract-phase4.md` and `docs/contract-phase5a.md`. Protocol facts live in
`docs/protocol-emulation.md` §10 (written in our own words); code is written from those
notes, never from other projects' code.

Goal: the virtual DualSense also exposes the real pad's USB audio function, so Windows shows
a "DualSense Wireless Controller" playback device. Games write 4-channel audio to it;
channels 3-4 become Bluetooth haptics and channels 1-2 the controller speaker, through the
5a stream (`HapticStreamer`, `0x32` / `0x36`).

## Work split (one after another, each reviewed before the next)

| Job | Content | Files |
|---|---|---|
| 1 | ISO transfers in the USB/IP server, ISO pacing, audio variant of the virtual DualSense (descriptors, SET_INTERFACE, UAC1 controls, HID on interface 3, mic silence), `IVirtualAudio` | `src/Tactile.Emulation/**` (not `Contracts*.cs`), `tests/Tactile.Tests/Emulation/**` |
| 2 | Game audio in the core: splitter (ch 3-4 → haptic payload, ch 1-2 → speaker), priority over 5a sources, settings, profile fields, emulation wiring, UI | `src/Tactile.Audio/GameAudio.cs` (new), `src/Tactile.Core/**`, `src/Tactile.Host/Bridge.cs`, `src/Tactile.Device.DualSense/HapticReport.cs` (buffer length only), `ui/`; tests in `tests/Tactile.Tests/GameAudio/**` |

`src/Tactile.Emulation/Contracts.cs` and `ContractsAudio.cs` are fixed.

## Safety rules (every job)

As in phase 4 and 5a: nothing is **run** that attaches a virtual device, hides a device,
sends output to the controller or input to the PC, or changes Windows audio settings
(default device, volume). Tests use fakes and loopback sockets. Only `dotnet build`,
`dotnet test`, `npm run build`. 127.0.0.1 only. No new packages. No copied code. No
commits. Warning-free. Turkish user text. Code comments describe the code only.

## Job 1: emulation

### ISO in `UsbIpConnection`

- Parse ISO SUBMITs per §10.1 (OUT: payload then descriptors; IN: descriptors only). Limits:
  1..128 packets, each `offset + length` within the buffer, `length` ≤ the endpoint's
  wMaxPacketSize; otherwise RET_SUBMIT with descriptors echoed, status −EINVAL, no payload.
- ISO URBs for an endpoint go to a per-endpoint FIFO. A single ISO scheduler thread per
  connection (`AboveNormal`, `timeBeginPeriod(1)` via `TimerResolution` while it runs) advances
  a 1 ms frame clock with catch-up (§10.2): each frame takes the next packet of the head URB
  of each active ISO endpoint; a URB is answered after its last packet.
  - OUT packet: hand the slice to the device (`AudioOut(ReadOnlySpan<byte>)`), mark
    `actual_length = length`, status 0.
  - IN packet: device fills it (`AudioIn(Span<byte>)` returns bytes written); always fill
    whole packets (zero padding) so the length rule of §10.1 holds.
  - RET_SUBMIT per §10.1 (`start_frame` echoed, `error_count`).
- UNLINK of a queued ISO URB: remove it, RET_UNLINK −ECONNRESET, no RET_SUBMIT.
- The clock is injectable for tests (e.g. a `Func<long>` of elapsed milliseconds and a way to
  step frames); the real one uses `Stopwatch`.
- Non-ISO behaviour (control, interrupt, HID pacing) stays exactly as it is.

### Audio variant of `VirtualDualSense`

- Constructor flag `audio`. Without it: today's HID-only pad, byte-for-byte unchanged.
- With it: `Speed` full (2); configuration = §3.1 bytes with §10.3 changes (ISO bInterval 1,
  interrupt bInterval 4, mic wMaxPacketSize 192 = `C0 00`); wTotalLength must match the
  bytes. HID interface number 3; the report descriptor request with `wIndex` 3 (and keep
  accepting the request the HID-only pad handles today). `IntervalMs` stays 4.
- Standard requests: SET_INTERFACE / GET_INTERFACE / SET_CONFIGURATION per §10.4; class
  requests routed by `wIndex` low byte (0-2 audio, 3 HID). UAC1 feature-unit controls exactly
  as the §10.4 table; endpoint frequency requests stall.
- `IVirtualAudio`: `SpeakerActive` = interface 1 alt 1; `Audio` raised once per OUT packet
  with the packet's samples as `short` (S16LE, 4 ch; a partial last frame is dropped).
  Switching interface 1 to alt 0 sets `SpeakerActive` false.
- Mic (EP `0x82`): while interface 2 is alt 1, every IN packet is 192 zero bytes (or the
  packet `length` if smaller). No real microphone data in this phase.
- `UsbIpServer.CreateDualSense(identity, audio)` (internal, called by `ContractsAudio.cs`);
  `Create(VirtualKind.DualSense, …)` keeps creating the HID-only pad.

### Tests (`tests/Tactile.Tests/Emulation/`)

Codec: ISO SUBMIT parse (OUT/IN), RET_SUBMIT bytes for OUT and IN (lengths, echoed
descriptors, start_frame), invalid packet → −EINVAL. Scheduler with a fake clock: an
N-packet URB is answered only after N frames; two endpoints interleave; catch-up after a
late tick; idle time not replayed; UNLINK. Device: audio config bytes (length 227 header
field = actual length, mic `C0 00`, intervals), HID report descriptor at wIndex 3,
SET_INTERFACE alt handling and `SpeakerActive`, every §10.4 control value, mic silence
packet. Loopback socket end-to-end: import the audio pad, send an ISO OUT URB of 4 packets
× 384 bytes, receive `Audio` 4 times and a correct RET_SUBMIT. HID-only pad tests unchanged
and passing.

## Job 2: core, settings, UI

### `Tactile.Audio/GameAudio.cs` (pure, testable)

```csharp
public sealed class GameAudio
{
    public float HapticStrength { get; set; } = 1f;   // 0-2
    public bool LowPass { get; set; }                  // gentle low-pass on ch 3-4 before decimation
    public void Push(ReadOnlySpan<short> frames4ch);   // ISO thread
    public bool ReadHaptics(Span<sbyte> payload64);    // haptic thread; false = no block (silence written)
    public bool HapticsLive { get; }                   // a non-zero haptic payload was produced in the last 100 ms
    public event Action<ReadOnlySpan<float>>? Speaker; // ch 1-2 as stereo float 48 kHz, raised from Push
    public void Clear();
}
```

Haptics per §10.5: 512-frame blocks of ch 3-4, 16-frame average, `/256 × strength`, clamp
to s8; at most 3 queued blocks (drop the oldest beyond). One writer, one reader, no
allocation per call.

### Core

- `AppSettings.Emulation` gets `Audio` (bool, default true: create the audio pad) and
  `HapticBuffer` (int 16-128, default 64 → `HapticReport.BufferLength` of the stream).
- `HapticsConfig` gets `gameAudio` (`both` | `haptics` | `speaker` | `off`, default `both`),
  `gameStrength` (0-2, default 1), `gameLowPass` (bool, default false). Validation clamps.
- `EmulationService` creates the DualSense pad with `CreateDualSense(identity, settings.Audio)`;
  a change of `Audio` re-creates the pad. The pad's `IVirtualAudio` (or null) is handed to
  the haptic engine through the core.
- `HapticEngine`:
  - The stream is also needed while `IVirtualAudio.SpeakerActive` and `gameAudio != off`.
  - Haptics per block: if `gameAudio` includes haptics and `GameAudio.HapticsLive`, the game
    payload replaces the 5a audio-derived payload; otherwise the 5a payload. Rumble and clicks
    are still mixed on top as today.
  - Speaker: if `gameAudio` includes speaker and the speaker interface is active, game ch 1-2
    feed the `SpeakerFeed` (this wins over the 5a speaker source); otherwise the 5a source.
    Speaker frames while the game speaker is active but silent: encoded silence (as 5a).
- Status: emulation status gains `audio` (`off` | `ready` | `playing`) for the UI.

### UI

- **Sanal kontrolcü** page: switch "Ses arayüzü (oyun haptiği ve hoparlörü)" with a hint
  that changing it re-plugs the virtual pad; slider "Haptik tamponu" 16-128 with "küçük =
  daha az gecikme, büyük = daha az kesinti"; audio status line.
- **Haptik** page: section **Oyun haptiği** (only meaningful in DualSense mode): segmented
  Hepsi / Yalnız haptik / Yalnız hoparlör / Kapalı, strength 0-200 %, switch "Yumuşatma
  (düşük geçiren)". One line: "Oyun kendi haptiğini gönderirken sesten haptik geri planda kalır."
- `sim.ts` sends the new fields so the pages can be checked in a browser.

### Tests (`tests/Tactile.Tests/GameAudio/`)

`GameAudio`: decimation and scaling numbers, strength, clamp, queue cap, `HapticsLive`
timing, speaker event channel split. Engine with fakes: game haptics replace 5a haptics
only while live; speaker priority; stream needed when the speaker interface is active.
Settings/profile JSON round-trip and validation. Existing tests keep passing.

## Hardware check (after both jobs, by the lead with KH)

1. Windows shows a "DualSense Wireless Controller" playback device after attach; note
   whether Windows makes it the default device (it must not take over silently; if it
   does, record it and decide).
2. A test program plays 4-channel audio to that device: tone on ch 3-4 → felt on the
   actuators; music on ch 1-2 → heard from the controller speaker.
3. If KH runs a haptic game: game haptics felt over Bluetooth.

## Job 3: keep the user's default audio devices, jitter buffers on the haptic paths

Found in the hardware check (2026-11-19): when the audio pad attaches, Windows makes
"DualSense Wireless Controller" the default playback **and** recording device (all PC sound
goes to the controller, voice apps record the silent virtual mic). And the speaker crackled
until `SpeakerFeed` got a 2-block jitter buffer; the haptic paths have the same gap.

### Default devices (`src/Tactile.Audio/AudioDefaults.cs`, new)

- Snapshot the default endpoint ids for render and capture, roles console, multimedia and
  communications (`IMMDeviceEnumerator.GetDefaultAudioEndpoint`).
- Restore with the policy-config COM object Windows uses for its own sound settings
  (undocumented but stable since Windows 7, used by common audio tools): CLSID
  `870af99c-171d-4f9e-af0d-e63df40c2bc9`, interface `f8679f50-850a-41cf-9c72-430f290290c8`,
  method order: GetMixFormat, GetDeviceFormat, ResetDeviceFormat, SetDeviceFormat,
  GetProcessingPeriod, SetProcessingPeriod, GetShareMode, SetShareMode, GetPropertyValue,
  SetPropertyValue, **SetDefaultEndpoint(string deviceId, ERole role)**, SetEndpointVisibility.
  Declare only up to SetDefaultEndpoint (vtable order matters). Failure → notice, never a crash.
- Core: `AppSettings.Emulation.KeepAudioDefaults` (bool, default true). `EmulationService`
  takes a snapshot right before attaching a DualSense pad **with audio**; for 15 s after the
  attach it checks every 500 ms; for each role whose default changed to an endpoint whose
  friendly name contains "DualSense", it restores the snapshot id once. After the 15 s window,
  or once restored, it never touches defaults again (a user who picks the DualSense by hand
  later keeps it). Injectable (`IAudioDefaults`) so tests use a fake; no real device changes
  in tests.
- UI, **Sanal kontrolcü** page under the audio switch: toggle "Varsayılan ses cihazlarımı koru"
  with hint "Sanal DualSense takılınca Windows sesi ve mikrofonu ona geçirmesin."

### Jitter buffers

- `AudioHaptics` (5a) and `GameAudio` (5b): like `SpeakerFeed`, start reading only once 2 whole
  blocks are queued, and after an underrun wait again for 2 blocks (return silence / false
  meanwhile). Keep the existing caps (AudioHaptics 4, GameAudio 3 blocks). `HapticsLive` /
  live window logic unchanged.
- Update the existing tests that assumed one block is enough; add priming tests for both.

### Rules

As for jobs 1-2. Tests must not call the real policy-config object or change any Windows
default device.
