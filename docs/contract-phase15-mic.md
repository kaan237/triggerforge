# Phase 15.1 contract: Bluetooth DualSense microphone via virtual USB/IP UAC1

Adds to docs/contract-phase5a.md, docs/contract-phase5b.md, and docs/contract-phase13.md. Base version on this branch is 1.3.0 (Directory.Build.props:11).

Goal: forward the primary Bluetooth DualSense / DualSense Edge microphone (78-byte 0x31 Opus frames) to Windows through the virtual DualSense UAC1 microphone endpoint (EP 0x82, 2 ch, 48 kHz, 16-bit S16LE, 192 B/ms), off by default, streamed only while a Windows app opens the virtual mic, with zero disk persistence and full coexistence with haptics/speaker streaming.

## 1. Scope

### In scope

1. **Bluetooth 0x31 microphone frame separation (src/Tactile.Device.DualSense/InputParser.cs, DualSenseController.cs)**:
   - Windows ReadFile delivers 78-byte BT reports via HidDevice.Read (src/Tactile.Device.Hid/HidDevice.cs:72, DualSenseIds.BluetoothInputReportLength = 78 at src/Tactile.Device.DualSense/DualSenseIds.cs:18).
   - In InputParser.TryParse (src/Tactile.Device.DualSense/InputParser.cs:19-54), on Link.Bluetooth when report.Length >= 78 and report[0] == 0x31, test `(report[1] & 0x02) != 0` **before** Crc32.Verify (line 44). If set, return new `ParseResult.MicAudio` with `state = null`: mic frames replace the 63-byte state block and their trailing bytes 74..77 are not verified to carry a valid CRC-32, so they must never be parsed as controller state or counted as `Stats.BadCrc`.
   - Add `InputParser.TryParseMic(ReadOnlySpan<byte> report, out byte sequence, out ReadOnlySpan<byte> opus71)`: validates `report.Length >= 78 && report[0] == 0x31 && (report[1] & 0x02) != 0`, extracts `sequence = report[2]` (8-bit counter) and `opus71 = report.Slice(3, 71)` (71-byte CBR 48 kHz mono 10 ms Opus frame).
   - DualSenseController.ReadLoop (src/Tactile.Device.DualSense/DualSenseController.cs:247-284) handles `ParseResult.MicAudio` by incrementing `Stats.MicFrames` and invoking `MicFrameReceived(this, sequence, opus71)` without touching `_latest` or `InputReceived`.

2. **Bluetooth mic stream activation & coexistence (OutputReport.cs, HapticReport.cs, DualSenseController.cs)**:
   - OutputOptions (src/Tactile.Device.DualSense/OutputReport.cs:4-8) adds optional trailing parameters `bool MicStream = false, byte MicGain = 0x40, bool MicMuted = false`. In `OutputReport.WriteState` (lines 53-102), when `MicStream` is true: set Flags A bit 6 (`0x40`, MicVolumeEnable), `block[6] = Math.Min(o.MicGain, (byte)0x40)` (0..0x40), Flags B bit 1 (`0x02`, PowerSaveEnable), and `block[9] = o.MicMuted ? (byte)0x10 : (byte)0x00` (bit 4 = mic mute). Existing `o.Rumble` behavior is preserved when haptics are inactive.
   - HapticReport (src/Tactile.Device.DualSense/HapticReport.cs:9-160) adds `public bool MicEnabled { get; set; }`. When true, bit 0 is set on the sub-packet `0x91` stream flags byte at offset 4: `BuildSpeaker` (`0x36`, line 101) `0xFE -> 0xFF`, `BuildSpeakerDouble` (`0x39`, line 143) `0x7E -> 0x7F`, `Build` (`0x32`, line 67) `0x66 -> 0x67`.
   - Add `HapticReport.BuildMicControl(bool enable)` building a standalone 142-byte `0x32` report with sub-packet `0x91` length 7 and offset 4 = `0xFF` (enable) or `0xFE` (disable), CRC-32 with `Crc32.OutputSeed`. Unverified on hardware: whether standalone `0x32` needs 4-byte (`0x67`/`0x66`) vs 7-byte (`0xFF`/`0xFE`) `0x91` or periodic keepalive without haptics; see Sections 7 and 8.
   - DualSenseController adds `SetMicStream(bool enabled, byte gain = 0x40, bool muted = false)` and `WriteMicControl(bool enable)`; HapticStreamer (src/Tactile.Device.DualSense/DualSenseController.cs:325-445) exposes `public bool MicEnabled { set => _report.MicEnabled = value; }`.
   - When `HapticStreamer` is running (5a or 5b haptics/speaker), it carries bit 0 seamlessly; when not running, `SetMicStream` + `WriteMicControl(true)` opens mic standalone without disabling motor rumble (`HapticsActive` stays false). Starting/stopping `HapticStreamer` while mic is active preserves `MicEnabled`.

3. **Opus decoder & jitter/PLC/drift buffer (src/Tactile.Audio/Opus.cs, BluetoothMicBuffer.cs)**:
   - Extend `src/Tactile.Audio/Opus.cs` with `OpusDecoder : IDisposable` binding existing `third_party/opus/win-x64/opus.dll` (`LibraryImport("opus")` + `DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)`, matching `OpusEncoder` at lines 95-113; exports `opus_decoder_create`, `opus_decode`, `opus_decoder_destroy`, `opus_decoder_ctl` are present in the shipped DLL). Configured for 48 kHz, 1 channel, 480 samples/frame (10 ms).
   - `OpusDecoder.Decode(ReadOnlySpan<byte> packet, Span<short> mono480)` decodes a 71-byte packet; `DecodePlc(Span<short> mono480)` calls `opus_decode(decoder, null, 0, pcm, 480, 0)` for packet loss concealment. `DllNotFoundException` / negative create errors throw `AudioUnavailableException`.
   - `BluetoothMicBuffer : IDisposable` (lock-guarded SPSC ring, zero per-call heap allocation, matching `SpeakerFeed` at src/Tactile.Audio/SpeakerFeed.cs:8-79):
     - **Sequence-based PLC**: tracks 8-bit `lastSeq`. `delta = (byte)(seq - lastSeq)`: `0` = duplicate (drop); `1` = decode packet; `2..5` (1..4 lost frames, <= 40 ms) = run `delta - 1` `DecodePlc` calls then decode packet; `> 5` = resync (reset sequence, decode packet without burst flooding). Decode errors invoke `DecodePlc` (or clear 480 samples) and increment error counter without throwing.
     - **Jitter target 30-40 ms**: ring capacity 80 ms (3840 mono samples). Prime threshold = 30 ms (1440 samples = 3 frames) before reading begins.
     - **Clock drift management**:
       - *Overflow (BT clock faster than USB/IP 1 ms host)*: if buffered mono samples exceed 60 ms (2880 samples), drop oldest samples down to 35 ms (1680 samples) to bound latency without unbounded growth.
       - *Underflow (BT clock slower than host)*: if `< 48` mono samples remain on a 1 ms read, write available samples faded linearly to 0 over the tail, zero-fill remainder, set `_primed = false`, and return zeros until 30 ms re-accumulates.
     - **Mono -> 2 ch S16LE output**: `ReadStereoS16(Span<byte> dst, bool muted)` writes 48 stereo frames (192 B, or `dst.Length / 4` frames if shorter), duplicating each mono sample to L and R (`BinaryPrimitives.WriteInt16LittleEndian`). When `muted` is true, advances/drains buffer normally (preventing stale audio bursts on unmute) while writing strict all-zero bytes to `dst`.

4. **Virtual DualSense mic endpoint wiring (src/Tactile.Emulation/ContractsMic.cs, VirtualDualSense.Audio.cs)**:
   - Keep `src/Tactile.Emulation/ContractsAudio.cs` untouched (fixed in Phase 5b, line 1). Add `src/Tactile.Emulation/ContractsMic.cs` defining `MicAudioReader(Span<byte> stereoS16Le)` and `IVirtualMic` (`bool MicActive`, `bool MicMuted`, `ushort MicVolume`, `event Action<bool>? MicActiveChanged`, `void SetMicReader(MicAudioReader? reader)`).
   - `VirtualDualSense.WithAudio` (src/Tactile.Emulation/VirtualDualSense.Audio.cs:35-184) implements `IVirtualMic`:
     - `MicActive => Alternate(Mic) == 1` (interface 2, alt 1); `MicMuted => _mute[1] != 0`; `MicVolume => _volume[1]` (UAC1 unit 5, 0x0000..0x3000, lines 42-43, 134-183).
     - `SET_INTERFACE` (`0x0B`, lines 102-111), `SET_CONFIGURATION` / `ResetAlternates` (line 92), and `VirtualUsbDevice.Detach` (src/Tactile.Emulation/VirtualUsbDevice.cs:99-104) invoke `MicActiveChanged` whenever `MicActive` flips.
     - `AudioIn(Span<byte> data)` (lines 78-88): when `MicActive` is false returns 0; otherwise fills `length = Math.Min(data.Length, MicPacketSize)` (`192`): if `_mute[1] != 0` or reader is null, clears `data[..length]`; else calls reader on `data[..length]` and clears any unwritten tail so `IsoScheduler.TransferPacket` (src/Tactile.Emulation/IsoScheduler.cs:86-106) always satisfies the usbip-win2 ISO IN exact-length rule.

5. **On-demand core bridge, mute/LED, disconnect & default-device protection (Core + Host)**:
   - `BluetoothMicBridge` activates **only** when all hold: `settings.Emulation.BluetoothMic == true`, `settings.Emulation.Audio == true`, virtual DualSense with `IVirtualMic` is attached, `IVirtualMic.MicActive == true`, and primary slot (`Index == 0`) is a connected `Link.Bluetooth` controller (DualSense or DualSense Edge).
   - Closing the Windows recording app (`MicActive -> false`), disabling the setting, detaching/re-creating the pad, disconnecting, or swapping primary immediately clears `IVirtualMic.SetMicReader(null)`, clears the buffer, sends mic-off (`SetMicStream(false)`), and removes the `"mic.led"` output layer.
   - **Mute & Mic LED**: effective mute = `controllerMuteToggle || latestInput.MicMuted || virtualMic.MicMuted`. Pressing `Buttons.Mic` (rising edge in `AppCore.ProcessPrimaryInput`, src/Tactile.Core/AppCore.cs:797-884) toggles `controllerMuteToggle` only while Bluetooth mic is enabled/ready and `Buttons.Mic` is not bound to an active mapping action (so existing `ActionKind.MicMute` bindings at ActionDispatcher.cs:99-101, 234-244 keep working unchanged). While active and muted, set output override `"mic.led"` -> `MicLed.On` and emit strict zero PCM on EP 0x82; when unmuted or inactive, clear `"mic.led"` so profile/mapping `MicLed` resumes.
   - **Default recording device protection**: `EmulationService` already snapshots and restores both `AudioFlow.Render` and `AudioFlow.Capture` for 15 s via `IAudioDefaults` (`src/Tactile.Audio/AudioDefaults.cs:34-68`, `src/Tactile.Core/Emulation/EmulationService.cs:602-603,648-714`). Keep `KeepAudioDefaults` unchanged so Windows never silently hijacks the default mic when attaching the virtual DualSense.

### Out of scope

- Secondary controllers (`EmulationSlots.cs:112` keeps `Audio = false`), USB-connected primary controllers (already expose native USB UAC1 mic), and Xbox 360 / DualShock 4 virtual pads.
- 3.5 mm headset jack routing (`0x96`/`0xD6`), echo cancellation/DSP noise suppression, mic gain slider in UI (uses controller default gain `0x40` and Windows UAC1 unit 5 volume/mute), or recording/saving mic audio to disk.
- Rebuilding `third_party/opus/win-x64/opus.dll` or adding any NuGet packages.

## 2. Settings Schema & Compatibility

`AppSettings.CurrentVersion` (src/Tactile.Core/Model/AppSettings.cs:9) and `Profile.CurrentVersion` stay `1`. No migration bump.

```jsonc
{
  "emulation": {
    "enabled": true,
    "hideController": true,
    "audio": true,
    "keepAudioDefaults": true,
    "bluetoothMic": false,     // NEW: bool, default false (privacy-first opt-in)
    "hapticBuffer": 64,
    "speakerPacket": "single36",
    "gameModes": {}
  }
}
```

- Legacy settings JSON lacking `bluetoothMic` deserializes to `false` via C# record default (`EmulationSettings`, src/Tactile.Core/Model/AppSettings.cs:58-96), preserved by `Validation.Normalize(AppSettings)` (src/Tactile.Core/Storage/Validation.cs:247-256), and normalized in `ui/src/model.ts:192,517-520` (`bluetoothMic: s.emulation?.bluetoothMic === true`).
- `EmulationSettings.Equals` and `GetHashCode` (src/Tactile.Core/Model/AppSettings.cs:74-80) must include `BluetoothMic`. Changing `BluetoothMic` does **not** re-plug the virtual pad (EP 0x82 descriptor is already present whenever `Audio == true`).

## 3. Bridge (src/Tactile.Host/Bridge.cs <-> ui/src/host.ts) Contract

| Direction | Message `type` | Field / Shape | Description |
|---|---|---|---|
| Host -> UI | `emulation` | `mic?: "off" \| "ready" \| "active" \| "muted"` | Added to `PostEmulation()` (src/Tactile.Host/Bridge.cs:513-533) and `EmulationStatus` (ui/src/host.ts:237-251).<ul><li>`off`: setting off, `audio` off, non-DualSense pad, or non-BT primary.</li><li>`ready`: setting on + BT DualSense/Edge + audio pad attached, no app reading EP 0x82.</li><li>`active`: an app opened EP 0x82 (`MicActive == true`), unmuted, streaming.</li><li>`muted`: `MicActive == true`, muted (mic button, hardware flag, or Windows mute); sending zeros.</li></ul> |
| UI -> Host | `settings.save` | Existing `settings.emulation.bluetoothMic: boolean` | Saved through existing `SaveSettings` path (src/Tactile.Host/Bridge.cs:231-233,555-575). No new UI -> Host message type required. |

## 4. Work Split

Strictly 2 C# jobs + 1 UI job with **disjoint file sets**. `AppCore.cs` and `EmulationService.cs` belong together in **Job A**.

| Job | Scope | Allowed files | Forbidden files | Order |
|---|---|---|---|---|
| **B** | `OpusDecoder`, `BluetoothMicBuffer` (PLC, jitter, drift, mute zeroing), `InputParser` mic split, `OutputReport`/`HapticReport`/`DualSenseController` mic flags & events, `IVirtualMic` + `VirtualDualSense.WithAudio` `AudioIn`, unit tests | `src/Tactile.Audio/Opus.cs`, `src/Tactile.Audio/BluetoothMicBuffer.cs` *(new)*, `src/Tactile.Device.DualSense/InputParser.cs`, `src/Tactile.Device.DualSense/OutputReport.cs`, `src/Tactile.Device.DualSense/HapticReport.cs`, `src/Tactile.Device.DualSense/DualSenseController.cs`, `src/Tactile.Emulation/ContractsMic.cs` *(new)*, `src/Tactile.Emulation/VirtualDualSense.Audio.cs`, `tests/Tactile.Tests/Audio/BluetoothMicBufferTests.cs` *(new)*, `tests/Tactile.Tests/Emulation/VirtualDualSenseMicTests.cs` *(new)*, `tests/Tactile.Tests/InputParserTests.cs`, `tests/Tactile.Tests/Speaker/SpeakerReportTests.cs` | `src/Tactile.Emulation/ContractsAudio.cs`, `src/Tactile.Core/**`, `src/Tactile.Host/**`, `ui/**`, `docs/**` | First (parallel with Job C) |
| **A** | `EmulationSettings.BluetoothMic`, `Validation`, `BluetoothMicBridge`, `ControllerService`, `EmulationService` (`EmulationMicStatus`, `VirtualMic`), `AppCore` wiring (on-demand open/close, mute/LED, disconnect, haptics coexistence), `Bridge.PostEmulation`, core/settings unit tests | `src/Tactile.Core/Model/AppSettings.cs`, `src/Tactile.Core/Storage/Validation.cs`, `src/Tactile.Core/Emulation/BluetoothMicBridge.cs` *(new)*, `src/Tactile.Core/Emulation/EmulationService.cs`, `src/Tactile.Core/ControllerService.cs`, `src/Tactile.Core/AppCore.cs`, `src/Tactile.Host/Bridge.cs`, `tests/Tactile.Tests/EmulationCore/BluetoothMicBridgeTests.cs` *(new)*, `tests/Tactile.Tests/EmulationCore/EmulationModelTests.cs` | All Job B files, `src/Tactile.Emulation/**`, `src/Tactile.Audio/**`, `src/Tactile.Device.DualSense/**`, `ui/**`, `docs/**` | After Job B |
| **C** | `EmulationSettings.bluetoothMic` + `EmulationStatus.mic` types & normalization, Virtual Controller page toggle (under Audio interface, disabled when audio off) + live mic status row + privacy hint, EN/TR `L(en, tr)`, `sim.ts` | `ui/src/host.ts`, `ui/src/model.ts`, `ui/src/pages/Emulation.tsx`, `ui/src/sim.ts` | `src/**`, `tests/**`, `docs/**` | Parallel with Jobs B & A |

### Job-specific notes

- **Job B**: Do not modify `ContractsAudio.cs` or `SpeakerPairing.cs`. `InputParser.TryParse` must keep all existing `InputParserTests` fixtures passing unchanged. `HapticReport.Build` / `BuildSpeaker` / `BuildSpeakerDouble` must produce byte-identical output when `MicEnabled == false` so all existing `OutputTests` and `SpeakerReportTests` remain untouched.
- **Job A**: `EmulationService` sets `EmulationStatus.Mic` and triggers `Changed` when mic status changes (`EmulationService.cs:1043-1095`). `BluetoothMicBridge` must be injectable with a fake/in-memory `OpusDecoder` or buffer factory for deterministic tests, and must never throw to the HID reader or ISO scheduler threads. When `AudioUnavailableException` occurs (e.g., missing `opus.dll`), post a single localized `Notice(NoticeKinds.Emulation, ...)` and stay in `Off` without crashing.
- **Job C**: Every user-visible string must use `L(en, tr)` (`ui/src/i18n.ts`). On `ui/src/pages/Emulation.tsx:245-260`, place the toggle `L("Bluetooth microphone", "Bluetooth mikrofonu")` under `Audio interface` with hint `L("Streams the primary Bluetooth DualSense microphone to Windows only while an app is recording. Off by default; nothing is saved to disk.", "Birincil Bluetooth DualSense mikrofonunu yalnız bir uygulama kayıt yaparken Windows'a aktarır. Varsayılan kapalıdır; diske hiçbir şey yazılmaz.")`. In `LiveStatus` (lines 64-88), display `L("Microphone", "Mikrofon")`: `Off` / `Ready` / `Active` (with live dot) / `Muted`.

## 5. Acceptance Criteria and Required Tests

### Measurable acceptance criteria

1. `dotnet build` and `dotnet test` pass with **0 warnings and 0 errors** (`TreatWarningsAsErrors` enabled, `Directory.Build.props:6`); all existing tests pass unmodified.
2. `npm run build` in `ui/` succeeds with **0 TypeScript or Vite errors**.
3. Legacy settings JSON without `bluetoothMic` loads with `Emulation.BluetoothMic == false` in C# and `emulation.bluetoothMic === false` in UI.
4. BT 0x31 reports with `(report[1] & 0x02) != 0` (even with zeroed/invalid trailing bytes 74..77) return `ParseResult.MicAudio`, never update `InputState`, and never increment `Stats.BadCrc`.
5. `OpusDecoder` decodes valid 71-byte mono 48 kHz 10 ms Opus frames from `opus.dll` into 480 samples and conceals lost frames via PLC; `BluetoothMicBuffer` primes at 30 ms, trims overflow > 60 ms to ~35 ms, fades/zeros on underflow, and emits strict zeros when muted.
6. BT mic stream opens only when `BluetoothMic == true && Audio == true && MicActive == true && primary is BT DualSense/Edge`, closes immediately when `MicActive` returns to `false` or on disconnect/setting off, and coexists with `HapticStreamer` (`0x32`/`0x36`/`0x39`).

### Required unit tests (hardware-free, fake reports & real `opus.dll` compute only)

- `tests/Tactile.Tests/InputParserTests.cs` *(Job B)*:
  - `Bluetooth_mic_report_is_classified_before_crc_and_never_parsed_as_state`: synthetic 78-byte `0x31` with `byte1 = 0x02` (and `0x32` with sequence nibble), `byte2 = 0x5A`, 71 payload bytes, bogus tail `74..77` -> `TryParse` returns `ParseResult.MicAudio` with `state == null`; `TryParseMic` returns `true`, `seq == 0x5A`, `opus71` exact slice; normal state `0x31` with bit 1 = 0 still requires valid CRC.
- `tests/Tactile.Tests/Speaker/SpeakerReportTests.cs` *(Job B)*:
  - `Mic_flag_sets_bit0_on_0x32_0x36_0x39_and_standalone_mic_control_has_valid_crc`: verifies `MicEnabled = true` sets offset 4 to `0x67` (`0x32`), `0xFF` (`0x36`), `0x7F` (`0x39`), `MicEnabled = false` preserves `0x66`/`0xFE`/`0x7E`, `BuildMicControl(true/false)` writes `0x91 0x07 0xFF/0xFE` with valid `Crc32.OutputSeed`, and `OutputReport.WriteState` sets Flags A `0x40` + offset 6 gain (clamped to `0x40`) + Flags B `0x02` + offset 9 mute bit.
- `tests/Tactile.Tests/Audio/BluetoothMicBufferTests.cs` *(Job B)*:
  - `Opus_decoder_roundtrips_mono_frame_and_conceals_lost_frames`: encodes 48 kHz mono 10 ms sine (or uses libopus 1-ch encoder in test helper) into <= 71-byte packet, decodes 480 samples with non-zero energy; verifies sequence gap `1 -> 3` invokes PLC once (produces 2 frames = 960 samples), duplicate sequence is dropped, and corrupt packet does not throw.
  - `Jitter_buffer_primes_at_30ms_trims_overflow_and_handles_underflow_and_mute`: verifies silence while `< 3` frames buffered, valid duplicated stereo S16LE (`L == R`, 192 B/ms) once 3 frames queued, overflow (> 60 ms pushed) trims oldest down to ~35 ms, underflow fades to 0 and re-primes, and `muted: true` outputs all zeros while draining buffer so unmute plays fresh audio.
- `tests/Tactile.Tests/Emulation/VirtualDualSenseMicTests.cs` *(Job B)*:
  - `Mic_active_tracks_interface2_alt_reset_detach_and_applies_uac1_mute`: verifies `MicActiveChanged` fires on `SET_INTERFACE(2, 1)` -> `true`, `SET_INTERFACE(2, 0)` / `SET_CONFIGURATION` / `Detach()` -> `false`; `AudioIn` delegates to `MicAudioReader`, pads partial reads to 192 B, and outputs strict zeros when UAC1 unit 5 mute (`0x21 0x01 0x0100 0x0500`) is 1.
- `tests/Tactile.Tests/EmulationCore/BluetoothMicBridgeTests.cs` *(Job A)*:
  - `Opens_only_when_enabled_bt_primary_and_mic_active_and_closes_on_idle_or_disconnect`: uses fake controller, fake `IVirtualMic`, and synthetic `0x31` mic frames; verifies off by default, stays closed when `MicActive == false` or primary is USB, opens on `MicActive == true`, closes on `MicActive == false` / setting off / disconnect / primary swap, and coexists across `HapticStreamer` start/stop.
  - `Mute_button_and_hardware_mute_zero_samples_and_drive_mic_led`: verifies `Buttons.Mic` press edge toggles mute + sets `MicLed.On` + zeros EP 0x82 output, respects `InputState.MicMuted` and UAC1 `MicMuted`, and does not hijack `Buttons.Mic` when bound in profile mapping.
- `tests/Tactile.Tests/EmulationCore/EmulationModelTests.cs` *(Job A)*:
  - `Bluetooth_mic_defaults_false_and_roundtrips_with_legacy_json`: verifies `{}` -> `BluetoothMic == false` and `{"emulation":{"bluetoothMic":true}}` round-trips cleanly.

## 6. Safety and Privacy Rules

- **Privacy by default**: `EmulationSettings.BluetoothMic` defaults to `false`. No BT mic enable report is sent and no mic frames are decoded unless both the setting and `IVirtualMic.MicActive` are `true`.
- **No disk persistence**: Opus payloads and decoded PCM samples live strictly in bounded RAM buffers; never write mic audio to files, logs (`Log.cs`), or captures (`CaptureFile.cs`).
- **No hardware or OS audio mutations during tests**: Tests never open physical HID devices, attach VHCI devices, or call real Windows `IPolicyConfig`/`IMMDeviceEnumerator` (`AudioDefaults` stays mocked).
- **Allowed commands**: Only `dotnet build`, `dotnet test`, and `npm run build` (in `ui/`). No `git commit`/`git push`.

## 7. Hardware Test Checklist (End of Phase, ~15 min)

1. **Default-off & default-device safety**: Connect DualSense over BT, enable DualSense virtual pad with `Audio` on and `Bluetooth microphone` OFF -> verify `KeepAudioDefaults` prevents Windows from switching default recording device, virtual mic status shows `Off`, and Windows Sound Recorder receives silence (no `0x31` mic frames).
2. **On-demand standalone recording (no haptics/speaker)**: Enable `Bluetooth microphone` -> status shows `Ready`. Open Windows Sound Recorder / Discord mic test targeting "DualSense Wireless Controller" -> status turns `Active`, speech is intelligible without robotic pitch/stutter, and controller motor rumble still works. Close recording app -> status returns to `Ready` within 1 s.
3. **Mute button & LED**: While recording, press physical Mic button -> orange mic LED turns ON, status shows `Muted`, recording waveform drops flat to zero. Press again -> LED turns OFF, speech resumes with no stale buffered burst. Also verify Windows Sound Control Panel mic mute zeroes output.
4. **Coexistence with haptics & speaker**: Enable audio haptics or game haptics + controller speaker (`0x36` and `0x39`) while recording -> verify both speaker/haptics and mic continue without dropouts or corrupted inputs/CRC spikes.
5. **Disconnect & reconnect**: Turn off/disconnect BT DualSense mid-recording -> app stays stable, recording receives silence cleanly, no crash; reconnect DualSense -> mic resumes cleanly on next recording open. Repeat quick check on DualSense Edge if available.

## 8. Risks and Rollback

- **Risk — Standalone `0x32` mic-enable sub-packet layout & keepalive (unverified on hardware)**: Current `HapticReport.Build` (`0x32`) uses `0x91` len=4 (`0x66`), whereas `0x36` uses `0x91` len=7 (`0xFE`). Whether standalone BT mic opens reliably from a single `0x32` (`0x91` len=7 `0xFF`) without periodic 10.667 ms reports or without muting classic rumble is unverified until Hardware Check #2.
  - *Mitigation*: Isolate standalone `0x32` construction in `HapticReport.BuildMicControl` and `ControllerService.SetMicStream`; if hardware requires periodic keepalive or `0x32` len=4 `0x67`, only that single helper changes (fallback: run silent `HapticStreamer` with `MicEnabled = true` while `MicActive`).
- **Risk — Firmware `InputState.MicMuted` bit behavior over BT (unverified)**: Over BT, pressing the physical mic button may not flip `s[53] & 0x04` autonomously.
  - *Mitigation*: Host-side `Buttons.Mic` rising-edge toggle is ORed with `InputState.MicMuted` and UAC1 `IVirtualMic.MicMuted`, ensuring mute always zeroes audio and lights `MicLed.On`.
- **Risk — Clock drift between controller 48 kHz oscillator and USB/IP 1 ms host timer**: Unbounded drift causes growing latency or periodic clicks.
  - *Mitigation*: Bounded 80 ms ring, 30 ms prime, overflow trim to 35 ms when > 60 ms, and fade-to-zero underflow recovery, verified deterministically in `BluetoothMicBufferTests`.
- **Risk — State parser regression corrupting gameplay inputs**: If a `0x31` mic frame reaches `InputParser.ParseState`, sticks/buttons would spike randomly.
  - *Mitigation*: `(report[1] & 0x02) != 0` is checked before CRC and state parsing in `InputParser.TryParse`, guarded by dedicated unit tests.
- **Rollback**: Setting `settings.emulation.bluetoothMic = false` (the default) completely disables BT mic control reports, decoding, and `"mic.led"` overrides; EP 0x82 falls back to Phase 5b silence packets with zero impact on HID, haptics, or speaker.

---

## 9. Review decisions (binding; they override the text above)

Line numbers above predate 1.6.0 (base version 1.6.0): locate code by symbol. The whole feature is off by default and labelled experimental in the UI until it passes the hardware checklist.

- M1. **Closing.** Every way out (recording stops, setting off, primary swap, disconnect, pad stopped, app exit, `DualSenseController.Dispose`) sends the mic-off control: `WriteMicControl(false)`, or `MicEnabled = false` on the running `HapticStreamer`. The main report without mic bits goes first and the off control is the last mic-related report; a failed off is retried by the bridge worker while the controller stays connected. `Dispose` clears `_micStream` and closes the mic before the neutral report. A failed write never throws out of the closing path.
- M2. **Events.** `MicActiveChanged` is raised inside try/catch. Its handler only records the wanted state and signals; opening, closing and HID writes run on `BluetoothMicBridge`'s own worker (or the core tick in tests), never on a USB/IP or HID reader thread and never with `_opLock`, `_stateLock`, `_gate` or the USB/IP write lock held.
- M3. **Mute button.** While the virtual mic is ready or active, a rising edge of the physical mic button on the raw primary input always toggles the controller mute, whatever the mapping, layer, pause or on-screen keyboard does. A `micMute` mapping still runs as well. Silence wins: muted when the button toggle, the report bit or the Windows mute says so. The toggle is cleared when the mic session ends.
- M4. **Virtual endpoint.** `AudioIn` always drains the reader and clears the packet when muted, so no stale burst follows an unmute. It applies `MicVolume` as `AudioOut` applies the speaker volume. Mute and volume reads are thread-safe. No `MicMutedChanged` event: the bridge reads `MicMuted` on its tick.
- M5. **Controller service.** `ControllerService.SetMicStream(bool)` and `event PrimaryMicFrame` exist; frames come only from the current primary and are dropped for any other controller. `HapticReport.MicEnabled` is volatile. Stopping a haptic stream while the mic is wanted sends the standalone mic-on control again. A DualShock 4 or a USB link is refused.
- M6. **Parser safety.** `0x31` is taken as a mic frame only while that controller's mic stream is on; otherwise the bit is ignored and the report goes through the CRC as before, so the feature cannot change input handling when it is off. The frame layout is unverified on hardware; `docs/protocol.md` says so.
- M7. **Privacy of samples.** `Clear` and `Dispose` zero the ring and reset the primed state under the ring lock; reads after `Dispose` return silence. Every recording session uses a new decoder. Nothing is written to disk or logged.
- M8. **LED and status.** `mic.led` outranks `game`, `mapping.mic` and `live.micLed` while muted or active, and survives `ClearOverrides`. `EmulationStatus.SameState` includes `Mic`. Secondary slots always run with `BluetoothMic = false`. `IntegrationLayers.cs` and `EmulationSlots.cs` belong to Job A.
- M9. **UI.** The toggle is shown only while the audio interface is on; `Toggle` gets no new prop. Job C may not touch `ui.tsx`. The label carries "(experimental)" / "(deneysel)".
- M10. **Job A may edit Job B files** for the fixes above, because Job B is finished.
