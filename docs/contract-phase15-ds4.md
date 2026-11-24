# Phase 15.3 contract: physical DualShock 4 support

Adds to `docs/contract.md`, `docs/contract-phase8.md`, and `docs/contract-phase12.md`. Roadmap basis: `docs/yol-haritasi.md:80`. Target version: next minor/major bump on merge (`Directory.Build.props:11`, currently `1.3.0` on this copy).

Goal: add physical DualShock 4 (v1, v2, and official USB Wireless Adaptor) as a second controller family in the existing HID layer, converting DS4 input into `InputState`/`Buttons` so keyboard/mouse mapping, virtual pads (Xbox 360, DualSense HID-only, DualShock 4), lightbar, rumble, HidHide hiding, multi-controller slots, and the DSU motion server work out of the box, while features absent on DS4 (adaptive triggers, haptic audio, controller speaker, player LEDs, microphone LED) are silently skipped in output and hidden in the UI, and our own virtual USB/IP DualShock 4 is never mistaken for physical hardware.

## 1. Scope

### In scope

1. **DS4 discovery and link classification (`src/Tactile.Device.DualSense/DualSenseIds.cs:5,11-47`)**:
   - Extend `ControllerModel` (`DualSenseIds.cs:5`) to `{ DualSense, DualSenseEdge, DualShock4 }`.
   - Under `SonyVendorId` (`0x054C`, `DualSenseIds.cs:13`), recognize:
     - `0x05C4` (DS4 v1): `InputReportLength == 64` -> `Link.Usb`, `InputReportLength == 78` -> `Link.Bluetooth`.
     - `0x09CC` (DS4 v2): `InputReportLength == 64` -> `Link.Usb`, `InputReportLength == 78` -> `Link.Bluetooth`.
     - `0x0BA0` (DS4 USB Wireless Adaptor): `InputReportLength == 64` -> `Link.Usb` only (dongle exposes USB report layout).
   - Keep `DualSenseController` (`src/Tactile.Device.DualSense/DualSenseController.cs:27`) as the unified physical controller handle to avoid breaking existing callers and tests (`ControllerSlot`, `ControllerService`, `AppCore`, `Bridge`).

2. **DS4 feature reports, full-report activation, calibration, and MAC (`DualSenseController.cs:78-114,302-318`, `DeviceInfo.cs:7-75`)**:
   - **Bluetooth full-report activation**: on `Link.Bluetooth`, read feature report `0x05` (41 B, CRC-32 with `Crc32.FeatureSeed` = `0xA3`, `src/Tactile.Device.DualSense/Crc32.cs:8`) on open; this switches the DS4 from reduced `0x01` reports to full `0x11` (78 B) reports and provides IMU calibration.
   - **USB calibration**: on `Link.Usb`, read feature report `0x02` (37 B, no CRC).
   - **IMU calibration parsing (`ImuCalibration.ParseDualShock4`)**: DS4 stores gyro range pairs as `pitch+, yaw+, roll+, pitch-, yaw-, roll-` at offsets `7..18` (verified in `src/Tactile.Emulation/VirtualDualShock4.cs:79-89`), unlike DualSense's `pitch+, pitch-, yaw+, yaw-, roll+, roll-` (`src/Tactile.Device.DualSense/DeviceInfo.cs:45`). `ParseDualShock4` maps pairs `(7,13), (9,15), (11,17)`, validates CRC with `Crc32.FeatureSeed` when `Link.Bluetooth`, and falls back to `ImuCalibration.Identity` on short/invalid reads.
   - **MAC address**: on `Link.Usb`, read feature `0x12` (16 B) or fallback `0x81` (7 B) and parse bytes `1..6` reversed (verified in `VirtualDualShock4.cs:60-64`). On `Link.Bluetooth`, extract the 12-hex-digit BT address from `HidDeviceInfo.Path` when present; if unavailable, leave `MacAddress = null` (BT feature ID for DS4 MAC is unverified in this repo; `ControllerService`, `DsuServer`, `Overview`, and `Settings` already handle `null` MAC).
   - **Virtual identity isolation**: set `Features = UsbFeatureReports.None` (`src/Tactile.Device.DualSense/FeatureReport.cs:6`) for physical DS4 so `AppCore.RealOf` (`src/Tactile.Core/AppCore.cs:763-765`) and `EmulationSlots.UpdateSecondaries` (`src/Tactile.Core/Emulation/EmulationSlots.cs:132-135`) never pass DS4-formatted feature reports as DualSense `0x05`/`0x09`/`0x20` reports (`VirtualDualSense` and `VirtualDualShock4` fall back to `DefaultCalibration()`/`DefaultPairing(slot)`).

3. **DS4 input parsing (`src/Tactile.Device.DualSense/Ds4InputParser.cs`)**:
   - **USB (`Link.Usb`)**: report ID `0x01`, length `>= 64` B, base offset `0` (`report[1..63]`).
   - **Bluetooth (`Link.Bluetooth`)**: report ID `0x01` -> `ParseResult.ReducedReport`; report ID `0x11`, length `>= 78` B, verify `Crc32.Verify(Crc32.InputSeed, report[..78])` (`Crc32.cs:6,28-37`) -> `ParseResult.BadCrc` on mismatch, base offset `+2` (USB offset `k` lives at BT byte `k + 2`).
   - **Field mapping to `InputState` (`InputState.cs:47-64`)** (offsets relative to USB report start `b = report[base..]`):
     - `b[1..4]`: `Left = Stick(b[1], b[2])`, `Right = Stick(b[3], b[4])`.
     - `b[5]`: low nibble `0..7` = D-pad hat (`8` = released), bits `4..7` = `Square, Cross, Circle, Triangle`.
     - `b[6]`: bits `0..7` = `L1, R1, L2, R2, Share -> Buttons.Create (1 << 8), Options, L3, R3` (`(Buttons)((uint)b[6] << 4)`).
     - `b[7]`: bit `0` = `Buttons.PS`, bit `1` = `Buttons.Touchpad`, bits `2..7` = 6-bit frame counter. (`Mic`, `Fn1`, `Fn2`, `PaddleLeft`, `PaddleRight` are always cleared.)
     - `b[8..9]`: `L2`, `R2` analog (`0..255`).
     - `b[10..11]`: `uint16 LE` IMU timestamp (`5.33 us` ticks). Unwrap monotonically per controller into 32-bit `0.333 us` ticks (`accum += (uint)unchecked((ushort)(raw16 - prev16)) << 4`) so `DsuServer.SlotState.NextMicros` (`src/Tactile.Core/Integration/DsuServer.cs:659-698`) and `VirtualDualShock4.WriteInput` (`VirtualDualShock4.cs:129`) never glitch on the ~350 ms 16-bit wrap.
     - `b[13..18]`: `GyroRaw` (`3 x int16 LE`), `b[19..24]`: `AccelRaw` (`3 x int16 LE`).
     - `b[30]`: low nibble `level = b[30] & 0x0F`, cable bit `cable = (b[30] & 0x10) != 0`, `HeadphoneConnected = (b[30] & 0x20) != 0`, `MicConnected = (b[30] & 0x40) != 0`, `MicMuted = false`. Charge/percent: `cable && level >= 11` -> `(100, Full)`; `cable` -> `(Math.Clamp(level * 10, 0, 100), Charging)`; `!cable` -> `(Math.Min(level * 10 + 5, 100), Discharging)` (matches `InputParser.cs:118` and `VirtualDualShock4.cs:134-135`).
     - `b[33]`: touch packet count `n`; `b[34..]`: `n` packets of 9 bytes (`1` counter + `4` B `Touch1` + `4` B `Touch2`, same 12-bit packing as `InputParser.ReadTouch` at `InputParser.cs:107-111`, range `1920 x 942`). Bounds-check `n` against available payload (`<= 3` USB, `<= 4` BT); when `n == 0`, both touches inactive; when `n >= 1`, take packet `n - 1` (newest sample; verify packet order on hardware in Section 7).

4. **DS4 output report (`src/Tactile.Device.DualSense/Ds4OutputReport.cs`)**:
   - **USB (`Link.Usb`, including `0BA0`)**: 32-byte report ID `0x05`: `[0] = 0x05`, `[1] = 0x07` (flags), `[4] = state.RumbleRight`, `[5] = state.RumbleLeft`, `[6..8] = scaled RGB(state.Lightbar, state.Brightness)`, rest `0`.
   - **Bluetooth (`Link.Bluetooth`)**: 78-byte report ID `0x11`: `[0] = 0x11`, `[1] = 0xC0` (`hw_control`), `[2] = 0x00`, `[3] = 0x07`, `[6] = state.RumbleRight`, `[7] = state.RumbleLeft`, `[8..10] = scaled RGB`, `[74..77] = Crc32.Stamp(Crc32.OutputSeed, report)` (`Crc32.cs:7,22-26`).
   - **Brightness scaling**: DS4 lacks a separate brightness register; scale RGB by `High = 1.0`, `Medium = 0.62`, `Low = 0.30` (matching `brightnessFactor` in `ui/src/model.ts:873`).
   - **Silent skip & deduplication**: `LeftTrigger`, `RightTrigger`, `PlayerLeds`, `MicLed`, and `SpeakerVolume` are omitted from DS4 reports; `Ds4OutputReport.SameEffect(a, b)` compares only `(Lightbar, Brightness, RumbleLeft, RumbleRight)` so changes to DualSense-only fields never cause redundant HID writes.

5. **Silent feature gating in Core & Emulation**:
   - **Haptics & speaker**: `HapticStreamer` constructor (`DualSenseController.cs:340-346`) rejects `DualShock4`; `ControllerService.ReconcileHapticsLocked` (`src/Tactile.Core/ControllerService.cs:229`) and `PlayHapticTone` (`ControllerService.cs:131-145`) reject `DualShock4`; `HapticEngine.Configure` (`src/Tactile.Core/Haptics/HapticEngine.cs:61-129`) takes `bool supported = true` and stops quietly with `Problem = null` when `!supported`; `AppCore.Tick` (`src/Tactile.Core/AppCore.cs:620-628,646-648`) passes `supported: connected.Details.Model != ControllerModel.DualShock4` and `speakerVolume = null`; `AppCore.PulseConfirm` (`AppCore.cs:1108-1114`) skips `_haptics.PulseConfirm()` and falls back to its existing 60 ms classic rumble pulse.
   - **Edge shortcuts**: `EdgeShortcuts.Read` (`src/Tactile.Core/Multi/EdgeShortcuts.cs:9-14`) already returns `null` for non-Edge models.
   - **Virtual controllers**: extend `RealController` (`src/Tactile.Core/Emulation/EmulationService.cs:44`) with `ControllerModel Model = ControllerModel.DualSense`. Gate the USB-DualSense skip (`EmulationService.cs:560,1047`) on `w.Controller.Model != ControllerModel.DualShock4` so a wired USB DS4 can emulate DualSense (or Auto -> DualSense), and force `Audio = false` on virtual DualSense when `controller.Model == ControllerModel.DualShock4` (`EmulationService.cs:514,590,602,1124`).

6. **HidHide & Virtual DS4 Exclusion (CRITICAL)**:
   - **Existing virtual-device filter (verified)**:
     - `AppCore` wires `drivers.Tree.IsVirtual` into `ControllerService` (`src/Tactile.Core/AppCore.cs:93`).
     - `ControllerService.Scan` filters `!IsVirtual(c.Hid.Path)` before `TryReadMac` or `Open` (`src/Tactile.Core/ControllerService.cs:311-312`).
     - `DeviceTree.IsVirtual` (`src/Tactile.Drivers/DeviceTree.cs:11-24`) resolves `InstanceId(hidDevicePath)` via SetupAPI/fallback (`DeviceTree.cs:26-75`), calls `CM_Locate_DevNode`, and walks up to 32 parents via `CM_Get_Parent` checking `DeviceTree.IsVhciNode` (`DeviceTree.cs:37-39`), which matches `ROOT\USBIP_WIN2` (`VhciInstancePrefix`, line `9`) or hardware ID `ROOT\USBIP_WIN2\UDE` (`VhciHardwareId`, line `8`).
     - Because `VirtualDualShock4` (`VID 054C / PID 09CC`, `VirtualDualShock4.cs:12`) attaches through the same `usbip-win2` VHCI (`EmulationService.cs:607`), its HID child is already under `ROOT\USBIP_WIN2\UDE` and rejected by `DeviceTree.IsVirtual`.
   - **Fail-safe hardening**: in `ControllerService.IsVirtual` (`ControllerService.cs:572-588`), an exception from `_isVirtual(path)` currently returns `false` ("treat the pad as real"). Change the catch block so when `_isVirtual` throws (or add an optional `Func<bool>? hasVirtualPad` check so when any virtual pad is active, a failed probe returns `true` = skip for this scan tick and retry next scan in 1.5 s) a transient PnP error can **never** open our own virtual `054C:09CC` pad as physical hardware.
   - **HidHide**: `EmulationService.SyncHide` (`EmulationService.cs:716-761`) calls `_platform.Tree.InstanceId(_padPath)` and `_platform.Hider.Hide(id)` (`src/Tactile.Drivers/DeviceHider.cs:12-55`), which is VID/PID-agnostic and journals every hidden physical DS4 instance ID for `--restore` (`src/Tactile.Drivers/Recovery.cs:21-39`). No edits to `src/Tactile.Drivers/**` are required.

### Out of scope

- **DS4 speaker / headphone jack audio over USB or BT**: excluded (no DS4 audio streaming).
- **DS4 lightbar hardware blink registers (flash on/off bytes 9-10)**: steady RGB driven by `LightbarEngine` at 30 Hz (same as DualSense).
- **Third-party DS4 clones (non-`054C` VIDs)**: excluded; Sony VID `0x054C` only.
- **Dedicated DS4 SVG controller drawing**: `ControllerDrawing.tsx` stays shared; only DS4-absent UI controls are hidden.
- **Modifying `src/Tactile.Core/Mapping/**`, `src/Tactile.Emulation/**`, or `src/Tactile.Drivers/**`**: untouched to keep scope minimal.

---

## 2. Profile and Settings Schema

`Profile.CurrentVersion` (`src/Tactile.Core/Model/Profile.cs:27`) and `AppSettings.CurrentVersion` (`src/Tactile.Core/Model/AppSettings.cs:9`) remain **`1`**. No new JSON properties, no migration needed.

- Existing profiles and settings deserialize identically via `Validation.Normalize` (`src/Tactile.Core/Storage/Validation.cs:14-112,198-258`).
- A profile assigned to a DS4 keeps its saved `triggers`, `playerLeds`, `micLed`, and `haptics` values untouched on disk so sharing or switching profiles between a DualSense and a DS4 never destroys DualSense settings.
- `settings.primaryController` and `settings.controllerDefaults[mac]` accept DS4 MACs using existing `Validation.NormalizeMac` (`Validation.cs:282-284`).

---

## 3. Bridge (`src/Tactile.Host/Bridge.cs` <-> `ui/src/host.ts`) Contract

No new message types or fields are required. `Bridge.PostStatus` (`src/Tactile.Host/Bridge.cs:700,715`) already serializes `Details.Model.ToString()`.

### Host -> UI messages

| Message `type` | Field / Shape | Description |
|---|---|---|
| `status` | `model?: "DualSense" \| "DualSenseEdge" \| "DualShock4"`, `controllers[].model: "DualSense" \| "DualSenseEdge" \| "DualShock4"` | Existing message (`Bridge.cs:692-728`, `ui/src/host.ts:400-426`). `"DualShock4"` is emitted whenever a physical DS4 occupies slot `0..3`. `firmware` is `null` when unavailable. |
| `input` | Existing `Input` shape (`Bridge.cs:730-768`, `ui/src/host.ts:428-446`) | DS4 Share arrives as `"Create"` in `buttons`; `"Mic"`, `"Fn1"`, `"Fn2"`, `"PaddleLeft"`, `"PaddleRight"` are never set; `touch` coordinates are in `0..1919 x 0..941`. |

### UI -> Host messages

Unchanged (`ui/src/host.ts:489-518`). Existing live DevTools commands (`trigger`, `trigger.preview`, `playerLeds`, `micLed`) remain accepted without error and are silently ignored by `Ds4OutputReport`; `haptic` returns a localized error notice via `ControllerService.PlayHapticTone`.

---

## 4. Work Split

Two C# jobs + one UI job with **strictly disjoint file sets**. Job C runs in parallel with Jobs A and B; Job B runs after Job A (consumes `ControllerModel.DualShock4`, `Ds4InputParser`, `Ds4OutputReport`, `ImuCalibration.ParseDualShock4`).

| Job | Scope | Allowed files | Forbidden files | When |
|---|---|---|---|---|
| **A** | DS4 discovery (`05C4`/`09CC`/`0BA0`), `Ds4InputParser` (USB `0x01`/BT `0x11`, CRC, u16 timestamp unwrap), `Ds4OutputReport` (USB `0x05`/BT `0x11`, CRC, brightness scaling, dedup), `ImuCalibration.ParseDualShock4`, `DualSenseController` DS4 open/read/write/guard, protocol unit tests | `src/Tactile.Device.DualSense/DualSenseIds.cs`, `src/Tactile.Device.DualSense/DeviceInfo.cs`, `src/Tactile.Device.DualSense/DualSenseController.cs`, `src/Tactile.Device.DualSense/Ds4InputParser.cs` *(new)*, `src/Tactile.Device.DualSense/Ds4OutputReport.cs` *(new)*, `tests/Tactile.Tests/Ds4ProtocolTests.cs` *(new)* | `src/Tactile.Core/**`, `src/Tactile.Host/**`, `src/Tactile.Drivers/**`, `src/Tactile.Emulation/**`, `src/Tactile.Device.Hid/**`, `ui/**`, `docs/**` | First (parallel with Job C) |
| **B** | Core/Emulation/Host DS4 gating (`ControllerService` haptics & fail-safe virtual exclusion, `HapticEngine` unsupported silent stop, `EmulationService`/`EmulationSlots` `RealController.Model` + USB DS4 emulation + no virtual audio, `AppCore` wiring, `TactileApp` tray label), core/multi/emulation unit tests | `src/Tactile.Core/ControllerService.cs`, `src/Tactile.Core/Haptics/HapticEngine.cs`, `src/Tactile.Core/Emulation/EmulationService.cs`, `src/Tactile.Core/Emulation/EmulationSlots.cs`, `src/Tactile.Core/AppCore.cs`, `src/Tactile.Host/TactileApp.cs`, `tests/Tactile.Tests/Multi/DualShock4CoreTests.cs` *(new)* | `src/Tactile.Device.DualSense/**`, `src/Tactile.Device.Hid/**`, `src/Tactile.Core/Mapping/**`, `src/Tactile.Emulation/**`, `src/Tactile.Drivers/**`, `src/Tactile.Host/Bridge.cs`, `ui/**`, `docs/**` | After Job A (parallel with Job C) |
| **C** | React/TypeScript UI: `"DualShock4"` type & display name, hide DS4-absent UI (haptics tab, adaptive trigger effects, player/mic LEDs, virtual audio section, DevTools DS4-absent controls), Share label & `942` touchpad height, `sim.ts` DS4 simulation, EN/TR strings | `ui/src/host.ts`, `ui/src/i18n.ts`, `ui/src/App.tsx`, `ui/src/pages/Overview.tsx`, `ui/src/pages/Triggers.tsx`, `ui/src/pages/Light.tsx`, `ui/src/pages/Emulation.tsx`, `ui/src/pages/Settings.tsx`, `ui/src/pages/MappingTabs.tsx`, `ui/src/Live.tsx`, `ui/src/Outputs.tsx`, `ui/src/sim.ts` | `src/**`, `tests/**`, `docs/**` | Parallel with Jobs A & B |

### Job A details (`src/Tactile.Device.DualSense/**`)

- `DualSenseIds.cs:5,11-47`: add `DualShock4` to `ControllerModel`, constants `DualShock4V1ProductId = 0x05C4`, `DualShock4V2ProductId = 0x09CC`, `DualShock4DongleProductId = 0x0BA0`, and `IsDualShock4(ControllerModel)` helper; restrict `0x0BA0` to `InputReportLength == UsbInputReportLength`.
- `DeviceInfo.cs:27-64`: add `ImuCalibration.ParseDualShock4(ReadOnlySpan<byte> feature, Link link)` handling `0x02` (USB, `>= 37` B) and `0x05` (BT, `>= 41` B with `Crc32.Verify(Crc32.FeatureSeed, feature[..41])`) with DS4 gyro pair offsets `(7,13), (9,15), (11,17)`.
- `Ds4InputParser.cs` *(new)*: pure `TryParse(ReadOnlySpan<byte> report, Link link, ref Ds4Timestamp timestamp, out InputState? state)` + `Ds4Timestamp` struct unwrapping u16 `5.33 us` ticks to u32 `0.333 us` ticks.
- `Ds4OutputReport.cs` *(new)*: `BuildUsb(OutputState)` (32 B, ID `0x05`, flag `0x07`, motors `4-5`, scaled RGB `6-8`), `BuildBluetooth(OutputState)` (78 B, ID `0x11`, `0xC0`, `0x00`, flag `0x07`, motors `6-7`, scaled RGB `8-10`, CRC `0xA2`), `ScaleBrightness(Rgb, LedBrightness)`, and `SameEffect(OutputState, OutputState?)`.
- `DualSenseController.cs`: branch `Open`/`ReadMac`/`ReadLoop`/`Send` on `ControllerModel.DualShock4`; keep `Features = UsbFeatureReports.None` for DS4; reject `DualShock4` in `HapticStreamer` ctor (`340-346`).

### Job B details (`src/Tactile.Core/**`, `src/Tactile.Host/TactileApp.cs`)

- `ControllerService.cs`: reject `DualShock4` in `PlayHapticTone` (`131-145`) and `ReconcileHapticsLocked` (`229`); add optional `Func<bool>? hasVirtualPad` (or fail-closed on exception in `IsVirtual`, `572-588`, when a virtual pad is active so a transient PnP query error skips opening rather than treating a virtual `09CC` pad as real).
- `HapticEngine.cs:61-129`: add `bool supported = true` parameter to `Configure`; when `!supported`, stop captures and publish `Streaming = false, Source = "off", Problem = null`.
- `EmulationService.cs:44,514,560,590,602,1047,1124` & `EmulationSlots.cs:135`: add `ControllerModel Model = ControllerModel.DualSense` to `RealController`; allow USB DS4 to attach virtual DualSense (HID-only, `audio: false`) while preserving existing USB DualSense skip.
- `AppCore.cs:93,620-628,646-648,765,1108`: pass `Model` in `RealOf`, pass `supported: !isDs4` to `_haptics.Configure`, keep `speakerVolume = null` for DS4, and fall back to rumble in `PulseConfirm`.
- `TactileApp.cs:374`: format `ControllerModel.DualShock4` as `"DualShock 4"` (and `DualSenseEdge` as `"DualSense Edge"`) in tray tooltip/status.

### Job C details (`ui/src/**`)

- `host.ts:403` & `i18n.ts:21-23`: add `"DualShock4"` to `ControllerStatus["model"]` and map `"DualShock4"` -> `"DualShock 4"` in `modelName`.
- `App.tsx:21-33,78-150`: hide `"haptics"` rail item when primary is connected and `status.model === "DualShock4"`; redirect `section` to `"controller"` if currently on `"haptics"`.
- `Triggers.tsx:279-330`: when primary is `"DualShock4"`, hide adaptive trigger bar/graph/test/presets (`280-300,311-335`) while keeping L2/R2 `Analog response` (`302-308`) and a short EN/TR note that adaptive trigger effects are DualSense-only.
- `Light.tsx:112-163`, `Overview.tsx:135-146`, `Outputs.tsx:141,167-233`: hide Player LEDs, Microphone LED, adaptive trigger summary/controls, and haptics test when primary is `"DualShock4"`.
- `Emulation.tsx:245-260,353,370,376-388`: when primary is `"DualShock4"`, keep `"dualSense"` enabled over USB, adjust USB note, and hide `"Audio and haptics"` section.
- `Live.tsx:18,72,118`, `MappingTabs.tsx:47`, `Settings.tsx:46-47`: show `"Share"` instead of `"Create"`, hide `"Mic"` chip, and divide touchpad `t.y` by `942` when primary is `"DualShock4"`.
- `sim.ts:117-122,709-714`: make the third toggleable simulated controller a `"DualShock4"` (so primary swap and secondary DS4 cards are previewable in both EN and TR).

---

## 5. Acceptance Criteria and Test List

### Measurable acceptance criteria

1. `dotnet build` and `dotnet test` pass with **0 warnings and 0 errors**; all existing tests pass without changing their assertions, plus all new Phase 15.3 unit tests.
2. `npm run build` in `ui/` succeeds with **0 TypeScript or Vite errors**.
3. `DualSenseIds.FindControllers` classifies VID `0x054C` with PIDs `0x05C4` (USB/BT), `0x09CC` (USB/BT), and `0x0BA0` (USB only) as `ControllerModel.DualShock4` without affecting `0x0CE6`/`0x0DF2`.
4. `Ds4InputParser` parses USB `0x01` (64 B) and BT `0x11` (78 B, `Crc32.InputSeed = 0xA1`, USB offset `+2`) into `InputState` with accurate buttons (Share -> `Create`), sticks, triggers, gyro/accel, unwrapped u32 `SensorTimestamp`, battery/headset flags, and 2-point `1920 x 942` touch, rejecting bad CRC and flagging BT `0x01` as `ReducedReport`.
5. `Ds4OutputReport` builds valid USB `0x05` (32 B) and BT `0x11` (78 B, `0xC0`, flag `0x07`, CRC `0xA2`) reports with rumble and brightness-scaled RGB, while silently ignoring trigger effects, player LEDs, mic LED, and speaker volume and suppressing no-op HID writes.
6. Physical DS4 works end-to-end with mapping, virtual pads (Xbox 360, DualSense HID-only over USB/BT, DualShock 4), HidHide hiding, multi-controller slots, and `DsuServer`, never starts `HapticStreamer`, and **our own virtual DS4 (`054C:09CC` under `ROOT\USBIP_WIN2\UDE`) is never opened as a physical controller**.
7. Existing profiles and settings JSONs (`version: 1`) load unchanged with zero schema or migration regressions.

### Required unit tests (`tests/Tactile.Tests/`)

- `tests/Tactile.Tests/Ds4ProtocolTests.cs` *(new, Job A)*:
  - `Parses_usb_and_bt_ds4_input_reports_and_verifies_crc`: verifies all fields of USB `0x01` (64 B) and BT `0x11` (78 B, USB offset `+2`, CRC `0xA1`), Share -> `Buttons.Create`, hat, sticks, L2/R2, gyro/accel, battery/charging/headphone/mic flags, 2-point touch (`1920 x 942`), BT `0x01` -> `ReducedReport`, and corrupted CRC -> `BadCrc`.
  - `Unwraps_16bit_sensor_timestamp_across_wrap_for_dsu`: feeds `0xFFF0` then `0x0010` and verifies monotonic 32-bit `0.333 us` tick delta (`0x20 << 4 = 512`).
  - `Parses_ds4_calibration_gyro_pair_order_and_rejects_bad_bt_crc`: verifies USB `0x02` (37 B) and BT `0x05` (41 B, CRC `0xA3`) gyro order `(pitch+, yaw+, roll+, pitch-, yaw-, roll-)` and `Identity` fallback on bad CRC.
  - `Builds_usb_and_bt_ds4_output_reports_and_deduplicates_unsupported_fields`: verifies USB `0x05` (32 B) and BT `0x11` (78 B, `0xC0`, `0x07`, motors `6-7`, RGB `8-10`, CRC `0xA2`), brightness scaling, and `SameEffect` returning `true` when only triggers/player LEDs/mic LED/speaker volume change.
  - `HapticStreamer_rejects_dualshock4`: asserts `InvalidOperationException` when constructed with a `ControllerModel.DualShock4` fake.
- `tests/Tactile.Tests/Multi/DualShock4CoreTests.cs` *(new, Job B)*:
  - `Virtual_ds4_09cc_path_is_never_opened_as_physical_controller`: registers a physical DS4 and a virtual `054C:09CC` candidate whose `isVirtual` returns `true` (and a probe-throwing candidate while emulation is active); verifies only the physical DS4 opens.
  - `Physical_ds4_drives_mapping_dsu_and_hides_via_hidhide_without_haptics`: verifies primary DS4 input reaches `Mapper`, `DsuServer`, and `EmulationService`, hides physical DS4 instance ID via fake `IDeviceHider`, attaches virtual DualSense HID-only (`audio == false`) even over USB, and keeps `HapticEngine` stopped with `Problem == null`.
  - `PlayHapticTone_and_PulseConfirm_behave_safely_on_ds4`: verifies `PlayHapticTone` returns localized error without starting streaming, and `PulseConfirm` falls back to classic rumble (`RumbleLeft = 64, RumbleRight = 160`).
  - `Secondary_ds4_receives_lightbar_and_rumble_and_ignores_edge_shortcuts`: verifies DS4 in slot 1 gets profile lightbar/rumble and game feedback while `EdgeShortcuts` never fire.

---

## 6. Safety Rules (Every Job)

- **No real hardware or OS side effects**: never run code that opens real HID devices, sends output to real controllers, injects keyboard/mouse input, attaches USB/IP devices, modifies HidHide, or launches the app executable. All tests use fakes and `ManualTime`.
- **Virtual pad self-open prevention**: never bypass `DeviceTree.IsVirtual` (`src/Tactile.Drivers/DeviceTree.cs:11-24`) in `ControllerService.Scan`.
- **Allowed commands**: only `dotnet build`, `dotnet test`, and `npm run build` (in `ui/`).
- **No commits & no new packages**: do not touch git history or add NuGet/npm packages.
- **Localization**: every new user-visible string must have both English and Turkish (`Text.Pick` / `L(en, tr)`).

---

## 7. Hardware Test Checklist (KH — End of Phase, ~15 min)

1. **USB DS4 (v1 `05C4`, v2 `09CC`, or dongle `0BA0`)**: plug in -> Overview shows `"DualShock 4"` and `"USB"`, sticks/buttons/D-pad/Share/Options/PS/touchpad click + 2-finger dot (`1920 x 942` full height) + gyro/accel move live, and Haptics tab / adaptive trigger effects / Player LEDs / Mic LED are hidden.
2. **Bluetooth DS4**: pair and connect over BT -> verify full `0x11` reports activate immediately after open (no stuck `ReducedReport`, CRC OK), battery % and charging state match, lightbar color/brightness and rumble work, and idle sleep disconnects cleanly.
3. **Mapping & DSU**: enable keyboard/mouse mapping (including touchpad mouse + gyro mouse) -> verify Share, touchpad, and gyro work; enable DSU server -> verify Cemu/Dolphin receives DS4 gyro/accel smoothly without 350 ms timestamp stutters.
4. **Virtual controller + HidHide + no self-capture**: set profile emulation to `DualShock 4`, `Xbox 360`, and `DualSense` (over both USB and BT) -> verify HidHide hides the physical DS4 (`joy.cpl` shows only 1 controller), game rumble + lightbar feedback reach the physical DS4, and **TriggerForge never lists its own virtual DS4 as a second physical controller**.
5. **Mixed multi-controller**: connect one DualSense + one DS4 simultaneously -> verify each slot uses its own model behavior, swapping primary updates hidden/visible UI sections cleanly, and unplugging restores everything.

---

## 8. Risks and Rollback

- **Risk — Self-capture loop (`054C:09CC` virtual DS4 opened as physical controller)**:
  - *Mitigation*: `DeviceTree.IsVirtual` (`src/Tactile.Drivers/DeviceTree.cs:11-24`) checks `ROOT\USBIP_WIN2\UDE` parent ancestry before opening any candidate (`ControllerService.cs:312`), hardened to fail-closed (skip tick) if the device-tree query throws while a virtual pad is active, covered by unit test and hardware checklist item 4.
- **Risk — Unverified DS4 edge cases (BT MAC feature ID, multi-packet touch order when `b[33] > 1`, v1/v2 full-charge battery nibble, `0BA0` dongle behaviour)**:
  - *Mitigation*: BT MAC uses HID-path extraction with `null` fallback; `ImuCalibration.ParseDualShock4` falls back to `Identity`; touch packet count is bounds-checked; hardware checklist explicitly verifies BT MAC, battery, touchpad multi-packet, and `0BA0` dongle.
- **Risk — Touchpad quadrant vertical split (`TouchpadMapper.PadHeight = 1080` vs DS4 `942`)**:
  - *Mitigation*: `src/Tactile.Core/Mapping/TouchpadMapper.cs:10` stays untouched to avoid expanding scope; bottom quadrant threshold sits ~6% lower on DS4 (UI finger preview uses `942`), acceptable for minimal scope and trivially adjustable later if hardware testing shows friction.
- **Rollback**: removing the three DS4 PIDs (`0x05C4`, `0x09CC`, `0x0BA0`) from `DualSenseIds.FindControllers` disables all physical DS4 code paths immediately with zero profile/settings schema impact.

---

## 9. Review decisions (binding; they override the text above)

- D1. **Calibration order.** Wired USB feature `0x02` is axis-paired: `pitch+, pitch-, yaw+, yaw-, roll+, roll-` (offsets 7..18, the DualSense order). Bluetooth `0x05` **and** the `0BA0` dongle's `0x02` are grouped: `pitch+, yaw+, roll+, pitch-, yaw-, roll-`. `ParseDualShock4(feature, grouped)` takes the layout as a parameter. A zero range on an axis, or a bias/scale far from nominal (`|bias| > 1024` or scale off by more than 50 %), falls back to identity for that sensor.
- D2. **Dongle.** The dongle always sends `0x01`. `report[31] & 0x04` set means no controller is paired: such reports are dropped (never turned into input), and calibration is re-read (up to 5 tries, 2 ms apart) when the bit clears. An all-zero calibration means "not ready yet".
- D3. **Battery.** Cable bit set: `0..9` → charging, `level * 10 + 5`; `10` → charging 100; `11` → full 100; `12..15` → 0 with the existing "unknown/not charging" state if there is one, else `Discharging`. No cable: `0..9` → `level * 10 + 5`, `>= 10` → 100, discharging.
- D4. **Bluetooth input.** Accept report ids `0x11..0x19` of at least 78 bytes when `report[1] & 0x80` is set; CRC over the first 78 bytes.
- D5. **Bluetooth activation.** Read feature `0x05`; also send one `0x11` output report (`0xC0`, flags `0x07`) at open.
- D6. `VirtualDualShock4` already uses the paired USB order since 1.4.0.
- D7. Profiles keep all DualSense-only values; nothing is rewritten because a DS4 is connected.
- D8. **Bluetooth length.** Windows reports the largest input report (547 bytes) for a Bluetooth DS4, so any length of 78 or more is Bluetooth for `05C4`/`09CC`.
- D9. **Gyro bias.** The firmware applies the bias; the field only centres the range (`span = |plus-bias| + |minus-bias|`, sample bias 0). This replaces the bias subtraction implied above.
- D10. **Fallback scale.** Missing or implausible calibration uses the DS4 nominal scale (1/16 deg/s, 1/8192 g per count), not `Identity`.
- D11. **Dongle release.** The first unpaired report after input publishes one neutral state; every re-pair restarts the clock and re-reads calibration.
- D12. **Bluetooth MAC.** Read from the HID serial string, with the path as fallback.
- D13. `IDeviceTree.IsVirtual` throws when the device cannot be looked up.
- D14. Implausible calibration falls back per axis, not per sensor: the remaining axes keep their calibrated values.
- D15. A dongle opened without a paired pad has no MAC address for that session; the slot keeps the address it was accepted with.
