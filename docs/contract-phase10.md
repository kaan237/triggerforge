# Phase 10 contract: quick wins — pause, gyro ratchet & stick, cycle/triple, confirm pulses, backups

Adds to `docs/contract.md` and the phase 3-8 contracts. Version becomes **1.1.0**.

Goal: deliver six high-utility everyday features (pause shortcuts & fullscreen auto-pause, gyro mouse ratchet modes, gyro-to-virtual-right-stick, button cycle & triple press, haptic confirmation pulses, daily automatic profile backups with restore) while keeping every existing profile, setting and test (`620` tests) unbroken.

## 1. Scope

### In scope (6 implemented features + 1 evaluated and excluded)

1. **Pause mapping shortcut & fullscreen auto-pause**:
   - Controller button combo (`settings.pauseCombo`, 2-3 buttons) and global keyboard hotkey (`settings.pauseHotkey`, 1-4 keys) toggle user mapping pause on/off.
   - Tray menu item ("Pause mapping" / "Eşlemeyi duraklat") and tray tooltip suffix show pause state.
   - Optional automatic pause (`settings.pauseOnFullscreen`) while a fullscreen window (other than TriggerForge) is in front **and the active profile was not picked by a per-game rule** (i.e. it is the default/desktop profile). A game profile chosen by foreground exe keeps its mapping in fullscreen.
   - `RegisterHotKey` failure (combination taken by another app) posts a warning notice in both languages and leaves the hotkey unregistered; it never throws.
   - Pause combo buttons keep their normal mapped actions (no suppression in this phase); the UI hint recommends a combo that includes `ps` or Edge `fnL`/`fnR`.
   - Pausing for any reason (`ui`, `shortcut`, `fullscreen`) immediately releases all held keys, mouse buttons, running macros, latched toggles and turbo outputs (Phase 3 Rule 8).
2. **Gyro mouse ratchet modes**:
   - Extends `mapping.gyroMouse.mode` with `whileReleased` (gyro active by default, paused while `gyroMouse.button` is held) and `whileTouchpad` (gyro active while at least one finger touches the touchpad: `Touch1.Active || Touch2.Active`).
3. **Gyro → virtual right stick**:
   - New `emulation.gyroStick` on `Profile.Emulation` (primary controller slot 0 only, when `emulation.mode` is `xbox360` or `dualSense`).
   - Converts calibrated gyro angular velocity (`deg/s`) into a right-stick deflection offset `[-1, 1]` with deadzone, smoothing, sensitivity X/Y, anti-deadzone, axis choice (`yaw` or `roll`), inversion, and activation mode (`off | always | whileHeld | whileReleased | toggle | whileTouchpad`), then sums with the shaped physical right stick and clamps to `[-1, 1]`.
4. **Button cycle (`A → B → C`) and triple press**:
   - Extends `Binding` with `triple` (`ActionConfig?`) and `cycle` (`IReadOnlyList<ActionConfig>?`, up to 8 actions).
   - When `cycle` has 1+ actions, each press advances through `cycle[0]`, `cycle[1]`, …, wrapping around; index resets to `0` on `ReleaseAll()` (profile change, mapping pause/disable, controller disconnect).
   - When `triple` is set, the button state machine waits up to `doubleMs` after the second release before firing `double`, or fires `triple` on the third press within `doubleMs`.
5. **Haptic confirmation pulses**:
   - Per-profile boolean `haptics.confirmPulses` (default `false`).
   - Fires a short confirmation pulse on the primary controller when: (a) active profile changes, (b) mapping layer button is pressed or released, (c) a macro starts, or (d) user shortcut toggles mapping pause.
   - On Bluetooth when the haptic stream is active, queues a stereo confirmation pulse in `HapticMixer`; otherwise (USB, or Bluetooth without active stream) applies a `60 ms` light motor rumble pulse (`RumbleLeft = 64, RumbleRight = 160`) via a transient `"confirm"` output layer in `AppCore`.
6. **Bluetooth RSSI (Signal Strength) — OUT OF SCOPE**:
   - **Reason**: DualSense connects to Windows as a **Bluetooth Classic (BR/EDR)** HID device (`src/Tactile.Device.DualSense/BluetoothLink.cs:9-38`, `src/Tactile.Device.DualSense/DualSenseIds.cs:18`). Windows user-mode exposes no Win32 or WinRT API for live RSSI on a connected Classic Bluetooth HID device (`BLUETOOTH_DEVICE_INFO` in `bthprops.cpl` has no RSSI field; `BluetoothLEDevice` is BLE-only; `System.Devices.Aep.SignalStrength` only reports during inquiry/BLE advertisement; HCI `Read_RSSI` is not accessible from user-mode without custom kernel filtering).
7. **Daily automatic profile backups & restore**:
   - Once per calendar day (local date `yyyy-MM-dd`) on startup and before the first profile save/delete of that day, copies all `profiles\*.json` into `<DataDir>\backups\yyyy-MM-dd\`.
   - Keeps the newest `settings.backupCount` (default `7`, range `0..30`; `0` disables automatic backups) daily folders and prunes older ones.
   - UI can list available backups and restore a selected day's snapshot into `profiles\`, reloading `ProfileStore` atomically without stripping `run` actions (local backup, not external untrusted import).
   - Restore is non-destructive: before replacing, the current `profiles\*.json` are copied to `<DataDir>\backups\pre-restore-yyyyMMdd-HHmmss\` (kept: newest 3, pruned separately from daily folders, shown in the list). Restore replaces the profile set with the snapshot's files (profiles not in the snapshot are removed from `profiles\` but survive in the pre-restore copy). Each file is written atomically (temp + move); a failure mid-way restores the pre-restore copy and posts an error.
   - `id` must match `^\d{4}-\d{2}-\d{2}$` or `^pre-restore-\d{8}-\d{6}$` and resolve inside `<DataDir>\backups\`; anything else is rejected (no path traversal). Pruning only touches folders matching these patterns.
   - The active profile id after restore: kept if it exists in the snapshot, otherwise falls back to the default profile.

### Out of scope

- **Bluetooth RSSI**: Excluded for the Windows BR/EDR platform limitation stated above.
- **Secondary controllers (slots 1-3)**: Keep Phase 8 rules (`docs/contract-phase8.md:26-27`); mapping, macros, haptics and `gyroStick` run on slot 0 (primary) only.
- **Chords, multi-shift layers, trigger soft/full pull, stick ring actions, flick stick**: Phase 11.
- **On-screen display (OSD) notification overlay**: Phase 14.

---

## 2. Profile and Settings Schema

`Profile.CurrentVersion` (`src/Tactile.Core/Model/Profile.cs:26`) and `AppSettings.CurrentVersion` (`src/Tactile.Core/Model/AppSettings.cs:7`) stay `1`.

### Migration and backwards-compatibility rule

- No file version bump. Old JSON files without the new fields deserialize to the defaults below via C# record initializers and `LenientEnumConverterFactory` (`src/Tactile.Core/Storage/TactileJson.cs:34-62`), then pass through `Validation.Normalize` (`src/Tactile.Core/Storage/Validation.cs:13-110,196-248`).
- In the UI, `normalizeProfile`, `normalizeMapping`, `normalizeBinding` and `normalizeSettings` (`ui/src/model.ts:154-275`) fill missing fields with the exact same defaults.
- **Test requirement**: Loading an existing Phase 2-8 profile JSON and settings JSON without any Phase 10 fields must produce identical normalized objects and runtime behavior as before.

### Profile additions (`Profile`)

```jsonc
{
  "haptics": {
    // ... existing fields ...
    "confirmPulses": false           // bool, default false
  },
  "mapping": {
    // ... existing fields ...
    "buttons": {
      "cross": {
        "press":  { "kind": "none" },
        "hold":   null,
        "double": null,
        "triple": null,              // Action | null, default null
        "cycle":  null,              // Action[] | null, default null; 1-8 actions when active
        "holdMs": 400,               // 150-2000
        "doubleMs": 250,             // 100-600 (also used for the 2nd-release -> 3rd-press window)
        "turbo": false,
        "turboHz": 10,
        "toggle": false
      }
    },
    "gyroMouse": {
      "mode": "off",                 // off | always | whileHeld | whileReleased | toggle | whileTouchpad
      "button": "r1",
      "axis": "yaw",
      "sensitivityX": 1.0,           // 0.1-10
      "sensitivityY": 1.0,           // 0.1-10
      "invertX": false,
      "invertY": false,
      "smoothing": 0.2,              // 0-0.9
      "deadzone": 0.5                // deg/s, 0-5
    }
  },
  "emulation": {
    "mode": "off",                   // off | xbox360 | dualSense
    "remap": {},
    "swapSticks": false,
    "hideMappedButtons": true,
    "gyroStick": {
      "mode": "off",                 // off | always | whileHeld | whileReleased | toggle | whileTouchpad
      "button": "l2",                // ButtonName, default "l2"
      "axis": "yaw",                 // yaw | roll, default "yaw"
      "sensitivityX": 1.0,           // 0.1-10, default 1.0 (1.0 = full deflection at 120 deg/s)
      "sensitivityY": 1.0,           // 0.1-10, default 1.0
      "deadzone": 0.5,               // deg/s, 0-5, default 0.5
      "antiDeadzone": 0.0,           // 0-0.5, default 0.0 (minimum stick magnitude once outside deadzone)
      "smoothing": 0.2,              // 0-0.9, default 0.2
      "invertX": false,              // bool, default false
      "invertY": false               // bool, default false
    }
  }
}
```

### Settings additions (`AppSettings`)

```jsonc
{
  // ... existing fields ...
  "pauseCombo": [],                  // ButtonName[], default []; valid: empty or 2-3 distinct buttons
  "pauseHotkey": [],                 // KeyName[], default []; valid: empty or 1-4 distinct keys
  "pauseOnFullscreen": false,        // bool, default false
  "backupCount": 7                   // int, default 7, clamped to 0..30 (0 = disabled)
}
```

### Validation & security rules for new fields

- `Binding.Triple`: normalized with `Optional(...)` (`src/Tactile.Core/Storage/MappingValidation.cs:290-299`); `kind: "none"` becomes `null`.
- `Binding.Cycle`: `null` or empty array normalizes to `null`. Non-empty array is filtered to non-`none` valid actions and capped at `MaxCycleActions = 8` (excess actions removed with an issue notice).
- **Precedence in `ButtonMachine` (`src/Tactile.Core/Mapping/ButtonMachine.cs:25-153`)**:
  - If `cycle` has 1+ actions, `hold`, `double`, `triple`, `turbo` and `toggle` are ignored: each press holds (or fires one-shot) `cycle[_cycleIndex]` and advances `_cycleIndex = (_cycleIndex + 1) % cycle.Count`; releasing releases the action that went down. `ReleaseAll()` resets `_cycleIndex = 0`.
  - If `triple` is set (and `cycle` is null): `turbo` and `toggle` are ignored. First release before `holdMs` enters `WaitDouble`. Second press within `doubleMs`: if `triple` is null, `double` fires immediately as today; if `triple` is set, enters `PendingSecond`. If held past `holdMs` (when `hold` is set), `hold` fires; if released before `holdMs`, enters `WaitTriple` for up to `doubleMs`. A third press within `doubleMs` fires `triple` (held or one-shot); timeout after `WaitTriple` taps `double` (or taps `press` if `double` is null).
- **`Validation.StripRunActions` (`src/Tactile.Core/Storage/MappingValidation.cs:16-54`)**: MUST also strip `ActionKind.Run` inside `Binding.Triple` and `Binding.Cycle` (removing `run` entries from `cycle`, or setting `cycle` to `null` if none remain) and count them in `removed`.
- **`EmulationInput.IsBound` (`src/Tactile.Core/Emulation/EmulationInput.cs:113-114`)**: returns `true` when `Triple is { Kind: not ActionKind.None }` or `Cycle is { Count: > 0 }`.
- `AppSettings.PauseCombo`: normalized to lower-camelCase valid `ButtonName`s, deduplicated; if count is `< 2`, cleared to `[]`; if count is `> 3`, truncated to `3`.
- `AppSettings.PauseHotkey`: normalized to valid `Key` enum values (`!= Key.None`), deduplicated, capped at `4`.
- `AppSettings.BackupCount`: `Math.Clamp(s.BackupCount, 0, 30)`.

---

## 3. Bridge (`src/Tactile.Host/Bridge.cs` ↔ `ui/src/host.ts`) Contract

Both Job A and Job B must implement these exact message shapes and property names:

### Host → UI messages

| Message `type` | Field / Shape | Description |
|---|---|---|
| `status` | `mapping: { active: boolean, paused: boolean, pauseReason: "ui" \| "shortcut" \| "fullscreen" \| null, blockedBy: string \| null }` | Replaces `status.mapping` (`src/Tactile.Host/Bridge.cs:678`, `ui/src/host.ts:163`). `paused` is `true` when any pause source is active. Priority of `pauseReason`: `"ui"` > `"shortcut"` > `"fullscreen"` > `null`. |
| `backups` | `{ type: "backups", list: { id: string, date: string, profileCount: number }[] }` | Sorted newest `id` (`"yyyy-MM-dd"`) first. Sent on `hello`, on `backups.list`, and after `backups.restore`. |

### UI → Host messages

| Message `type` | Payload fields | Host behavior |
|---|---|---|
| `mapping.pause` | `paused: boolean` | Existing message (`src/Tactile.Host/Bridge.cs:258-260`). Sets UI key-capture pause (`_core.UiMappingPaused = paused`). Reset to `false` on `Bridge.Detach()`. |
| `mapping.togglePause` | *(none)* | Flips user shortcut pause (`_core.ToggleUserPause()`), posts updated `status` and updates tray. |
| `backups.list` | *(none)* | Scans `<DataDir>\backups\`, posts `backups`. |
| `backups.restore` | `id: string` (`"yyyy-MM-dd"`) | Restores profiles from `<DataDir>\backups\<id>\`, reloads `ProfileStore`, posts `profiles`, `status`, `backups`, and an `info` notice: `"Restored N profiles from <id>."` / `"<id> yedeğinden N profil geri yüklendi."`. |

---

## 4. Work Split

Job A and Job B run in parallel with **strictly disjoint file sets**.

| Job | Scope | Allowed files | Forbidden files | When |
|---|---|---|---|---|
| **A** | C# Core, Host, Windows Input, Bridge, Unit Tests | `src/**`, `tests/**` | `ui/**`, `docs/**` | Parallel with Job B |
| **B** | React/TypeScript UI, EN/TR strings, browser preview (`sim.ts`) | `ui/src/**` | `src/**`, `tests/**`, `docs/**` | Parallel with Job A |
| **C** | Post-merge verification & small integration fixes (only if needed) | `src/Tactile.Host/**`, `ui/src/**` | `third_party/**`, `secrets/**` | After Jobs A & B finish |

### Job A (`src/` + `tests/`)

- **Files to create**:
  - `src/Tactile.Core/Mapping/PauseShortcut.cs`: edge detector for `PauseCombo` (fires once when all combo buttons become held simultaneously; requires releasing at least one combo button before firing again) and keyboard hotkey matcher.
  - `src/Tactile.Core/Emulation/GyroStick.cs`: state machine & filter converting `Vector3 gyroDegPerSec` + `InputState` (`Buttons`, `Touch1`, `Touch2`) into `(float X, float Y)` stick offset using `GyroStickConfig`.
  - `src/Tactile.Core/Storage/ProfileBackup.cs`: `EnsureDailyBackup(string dataDir, string profilesDir, int backupCount, DateOnly today)`, `ListBackups(string dataDir)`, `RestoreBackup(string dataDir, string profilesDir, string id)`.
- **Files to edit**:
  - `src/Tactile.Core/Model/AppSettings.cs:5-32`: add `PauseCombo`, `PauseHotkey`, `PauseOnFullscreen`, `BackupCount`.
  - `src/Tactile.Core/Model/Profile.cs:52-60,160-175`: add `GyroStickConfig GyroStick` to `EmulationConfig` and `bool ConfirmPulses` to `HapticsConfig`.
  - `src/Tactile.Core/Model/Mapping.cs:18,45-58`: add `WhileReleased`, `WhileTouchpad` to `GyroMouseMode`; add `Triple` and `Cycle` to `Binding`.
  - `src/Tactile.Core/Storage/Validation.cs:92-108,196-248,254-296`: normalize `ConfirmPulses`, `GyroStick`, `PauseCombo`, `PauseHotkey`, `PauseOnFullscreen`, `BackupCount`.
  - `src/Tactile.Core/Storage/MappingValidation.cs:16-54,70-79,199-240,273-299`: validate `Triple`, `Cycle`, new `GyroMouseMode` values, and strip `run` actions in `Triple` and `Cycle`.
  - `src/Tactile.Core/Mapping/ButtonMachine.cs:10-170`: implement `Cycle` and `Triple` (`PendingSecond`, `WaitTriple`) state transitions and reset `_cycleIndex` in `ReleaseAll()`.
  - `src/Tactile.Core/Mapping/Mapper.cs:9-12,137-138,234-286`: add `string? PauseReason` to `MappingStatus`; pass touchpad touch state (`s.Touch1.Active || s.Touch2.Active`) into `GyroEnabled`; notify `IMappingHost` on layer button press/release for confirm pulse.
  - `src/Tactile.Core/Mapping/ActionDispatcher.cs:6-17,165-186`: notify `IMappingHost` when a macro starts so confirm pulse triggers.
  - `src/Tactile.Core/Emulation/EmulationInput.cs:14-73,113-114`: update `IsBound` for `Triple`/`Cycle`; add helper or parameter to blend `GyroStick` offset with physical right stick after `SwapSticks` (so gyro always drives the virtual pad's right stick) and clamp to `[-1, 1]`.
  - `src/Tactile.Core/Emulation/EmulationService.cs:200-207,285-303`: pass primary calibrated `gyroDegPerSec` and `now` into `OnInput` and apply `GyroStick` for slot 0.
  - `src/Tactile.Core/Haptics/HapticMixer.cs:36-112` & `src/Tactile.Core/Haptics/HapticEngine.cs:57-58,101-107,206`: add `PulseConfirm()` to queue a double-tap confirmation waveform on Bluetooth when streaming; in `AppCore`, fall back to a `60 ms` `"confirm"` rumble layer when Bluetooth haptic streaming is not active.
  - `src/Tactile.Core/Foreground.cs:6-13,68-135`: add `bool IsFullscreen { get; }` to `IForegroundSource` (default `false` in fakes; in `WindowsForegroundSource`, check foreground `hwnd` against `MonitorFromWindow` + `GetMonitorInfoW` monitor rectangle, excluding explorer desktop/shell windows and our own process).
  - `src/Tactile.Core/Storage/ProfileStore.cs:16-77,138-183`: integrate `ProfileBackup` (`EnsureDailyBackup` on startup and before save/delete; `RestoreBackup(string id)` that reloads `_profiles` and fires `Changed`).
  - `src/Tactile.Core/AppCore.cs:74,318-326,521-532,719-760,910-950`: separate `_uiPaused`, `_userPaused`, `_fullscreenPaused`; compute `PauseReason` (`"ui" | "shortcut" | "fullscreen" | null`); check `PauseCombo` on primary input; trigger confirm pulses on profile change, layer change, macro start, and user pause toggle.
  - `src/Tactile.Host/Bridge.cs:54-60,98-117,258-261,678`: handle `mapping.togglePause`, `backups.list`, `backups.restore`; include `pauseReason` in `status.mapping`; post `backups` on `hello`.
  - `src/Tactile.Host/Tray.cs:18-84` & `src/Tactile.Host/TactileApp.cs:54-68,264,355-360`: add tray menu item to toggle mapping pause, update tray status tooltip when paused, and register/unregister Win32 global hotkey (`RegisterHotKey` / `UnregisterHotKey` on `MainWindow.Handle` for `Settings.Current.PauseHotkey`).
- **Prohibitions**: Do not edit any file under `ui/`. Do not run the host executable or touch real HID/Win32 injection in tests.

### Job B (`ui/`)

- **Files to edit**:
  - `ui/src/host.ts:98-132,163-176,200-214,288-308,396-443`: update `Binding`, `GyroMouse`, `MappingStatus`, `EmulationConfig` (`GyroStickConfig`), `HapticsConfig`, `AppSettings`, `HostMessage` (`backups`) and `UiMessage` (`mapping.togglePause`, `backups.list`, `backups.restore`).
  - `ui/src/model.ts:48-151,178-275`: update `defaultBinding`, `defaultEmulation`, `defaultProfile`, `defaultSettings` and `normalize*` functions with the exact bounds from Section 2.
  - `ui/src/mapping.ts:127-140` & `ui/src/MappingEditors.tsx:245-296`: add Triple press editor and Cycle (`A → B → C` list up to 8 actions with add/remove/reorder) to `BindingEditor`; update `bindingSummary`.
  - `ui/src/pages/Mapping.tsx:26-55`: show `pauseReason` badge (`"Paused (shortcut)"` / `"Duraklatıldı (kısayol)"`, `"Paused (fullscreen)"` / `"Duraklatıldı (tam ekran)"`) and a Pause/Resume button sending `mapping.togglePause`.
  - `ui/src/pages/MappingTabs.tsx:139-207`: add `"whileReleased"` ("While button not held" / "Tuş basılı değilken") and `"whileTouchpad"` ("While touching touchpad" / "Touchpad'e dokunulurken") to `GyroMouseTab` (hide button picker when mode is `"whileTouchpad"`).
  - `ui/src/pages/Emulation.tsx:230-276`: add Gyro → Right Stick card (`mode`, `button`, `axis`, `sensitivityX`, `sensitivityY`, `deadzone`, `antiDeadzone`, `smoothing`, `invertX`, `invertY`).
  - `ui/src/pages/Haptics.tsx:167-172`: add `confirmPulses` toggle ("Confirmation pulses (profile, layer, macro)" / "Onay darbeleri (profil, katman, makro)").
  - `ui/src/pages/Settings.tsx:119-165`: add Pause shortcut section (`pauseCombo` 2-3 controller button picker, `pauseHotkey` key capture, `pauseOnFullscreen` toggle) and `backupCount` (0-30) setting.
  - `ui/src/pages/Profiles.tsx:154-236`: add Daily Backups section listing `{ id, date, profileCount }` from `backups` message with a "Restore" / "Geri yükle" button (`backups.restore`).
  - `ui/src/sim.ts:481-714`: simulate `mapping.togglePause`, `pauseReason`, `backups` list (2 sample daily backups) and `backups.restore`.
- **Prohibitions**: Do not edit any file under `src/` or `tests/`. Every user-visible string must have both English and Turkish via `L(en, tr)` (`ui/src/i18n.ts:16-18`).

### Job C (after Job A & B, only if needed)

- Verify `Bridge.cs` ↔ `host.ts` message compatibility and fix any mismatch discovered during `dotnet test` + `npm run build` + browser simulation check.

---

## 5. Acceptance Criteria and Test List

### Measurable acceptance criteria

1. `dotnet build` and `dotnet test` pass with **0 warnings and 0 errors**; all existing **620 tests** pass without changing their assertions, plus all new Phase 10 unit tests.
2. `npm run build` in `ui/` succeeds with **0 TypeScript or Vite errors**.
3. Legacy profile and settings JSONs from Phases 2-8 deserialize with `version: 1` and new fields set to defaults.
4. When mapping is paused via `UiMappingPaused`, `UserMappingPaused`, or `FullscreenPaused`, `_mapper.SetActive(false)` runs immediately and releases every held key, mouse button, macro, toggle, and turbo output.
5. `Validation.StripRunActions` removes `ActionKind.Run` from `Binding.Triple` and `Binding.Cycle` on profile import and includes them in `removedRunActions`.

### Required unit tests (`tests/Tactile.Tests/`)

- `tests/Tactile.Tests/BindingTests.cs`:
  - `Triple` press fires on 3rd press within `doubleMs`; 2 presses followed by `doubleMs` timeout taps `double` (or `press` if `double` is null); holding 2nd press past `holdMs` fires `hold`.
  - `Cycle` advances `A → B → C → A` across presses, releases the held action on button up, ignores `hold`/`double`/`turbo`/`toggle`, and resets index to `0` on `ReleaseAll()`.
- `tests/Tactile.Tests/GyroStickMappingTests.cs`:
  - `GyroMouseMode.WhileReleased` moves cursor while button is up and stops (and resets filter) while button is down.
  - `GyroMouseMode.WhileTouchpad` moves cursor only while `Touch1.Active` or `Touch2.Active` is true.
- `tests/Tactile.Tests/EmulationCore/EmulationInputTests.cs`:
  - `GyroStick` deadzone, anti-deadzone boost, sensitivity X/Y, smoothing, yaw/roll axis, invert X/Y, activation modes (`WhileHeld`, `WhileReleased`, `Toggle`, `WhileTouchpad`), and clamping when summed with physical right stick and `SwapSticks`.
  - `EmulationInput.MappedButtons` includes buttons bound only via `Triple` or `Cycle`.
- `tests/Tactile.Tests/MappingAppCoreTests.cs`:
  - `PauseCombo` (2-3 buttons) toggles user pause on edge without repeating while held.
  - `UiMappingPaused`, `UserMappingPaused` and `FullscreenPaused` remain independent (`Bridge.Detach` clearing UI pause does not unpause a user shortcut or fullscreen pause) and report the right `PauseReason` priority (`"ui"` > `"shortcut"` > `"fullscreen"`).
  - Pausing mid-press releases held keys immediately.
  - `ConfirmPulses` triggers pulse on profile switch, layer press/release, macro start, and pause toggle when enabled, and stays silent when `confirmPulses: false`.
- `tests/Tactile.Tests/Haptics/HapticMixerTests.cs`:
  - `PulseConfirm` injects non-zero confirmation samples into the next mixed blocks.
- `tests/Tactile.Tests/StoreTests.cs`:
  - Backwards-compatibility test loading a Phase 8 profile JSON and settings JSON without Phase 10 fields.
  - `StripRunActions` removes `run` actions inside `triple` and `cycle`.
  - `ProfileBackup` creates `<DataDir>\backups\yyyy-MM-dd\`, skips duplicate backup on the same date, prunes folders beyond `BackupCount`, and restores profiles while firing `ProfileStore.Changed`.
  - Restore writes a `pre-restore-*` copy first; rejects ids like `..\x`, `2026-13-99\..\..`, absolute paths; pruning leaves non-matching folders alone.
- `tests/Tactile.Tests/MappingAppCoreTests.cs` (additional): fullscreen auto-pause does not pause when the active profile came from a per-game rule.

---

## 6. Safety Rules (Every Job)

- **No hardware or OS injection**: Never run code that sends output to a controller, injects keyboard/mouse input (`SendInput`), registers real global hotkeys during tests, attaches/hides devices, installs drivers, or touches the network. The application executable is never started during development. All tests use fake `InputPlatform`, fake `IForegroundSource`, and `ManualTime`.
- **Allowed commands**: Only `dotnet build`, `dotnet test`, and `npm run build` (in `ui/`).
- **No commits**: Do not run `git commit`, `git push`, or modify git history.
- **Stuck-key prevention**: Any pause transition (`ui`, `shortcut`, `fullscreen`), profile switch, or controller disconnect must release all held virtual keys, mouse buttons, macros, toggles, and turbo outputs.

---

## 7. Hardware Test Checklist (KH — End of Phase, ~10 min)

1. **Controller pause combo**: Set `L1 + R1 + Options` as pause combo in Settings; hold a mapped key on desktop, press the combo → verify the key releases immediately, tray tooltip shows paused, and pressing the combo again resumes mapping.
2. **Global keyboard hotkey**: Set `Ctrl + Shift + P` as pause hotkey; press it while TriggerForge is minimized to tray → verify mapping pauses and resumes.
3. **Fullscreen auto-pause**: Enable "Pause in fullscreen apps", open a fullscreen game or press `F11` in a browser → verify desktop mapping pauses automatically and resumes on `Alt+Tab` back to desktop.
4. **Gyro ratchet (`whileReleased` & `whileTouchpad`)**: Set gyro mouse to `whileReleased` with `R1` → verify cursor moves with controller tilt and freezes while `R1` is held for repositioning. Switch to `whileTouchpad` → verify cursor moves only while a finger rests on the touchpad.
5. **Gyro → virtual right stick**: Enable Xbox 360 emulation and `gyroStick` (`whileHeld` on `L2`, `antiDeadzone: 0.12`) → open `joy.cpl` or a game, hold `L2`, tilt controller → verify smooth right-stick camera movement combined with physical right stick.
6. **Triple press & Cycle (`A → B → C`)**: Bind `Square` to cycle `1 → 2 → 3` and `Triangle` to single=`A`, double=`B`, triple=`C` → verify output in a text editor and verify switching profiles resets the cycle to `1`.
7. **Haptic confirmation pulses (BT & USB)**: Enable `confirmPulses` on the active profile → test over Bluetooth and over USB: switch profile, hold/release the layer button (`PS`), trigger a macro, and toggle pause → verify a distinct short tactile pulse each time.
8. **Daily profile backup & restore**: Modify a profile, check `%AppData%\TriggerForge\backups\<yyyy-MM-dd>\` exists, delete or alter a profile in the UI, then click "Restore" on today's backup in the Profiles page → verify the previous profile state is restored immediately.

---

## 8. Risks and Rollback

- **Risk — Pause sources overwriting each other**: `Bridge.Detach()` (`src/Tactile.Host/Bridge.cs:58`) sets `MappingPaused = false` on page reload.
  - *Mitigation*: Store `_uiPaused`, `_userPaused`, and `_fullscreenPaused` as three separate booleans in `AppCore`; `Paused` is `_uiPaused || _userPaused || _fullscreenPaused`.
- **Risk — Fullscreen false positives on Windows desktop/shell**: `Progman` or `WorkerW` covers the monitor rectangle.
  - *Mitigation*: `WindowsForegroundSource.IsFullscreen` ignores windows with class names `"Progman"`, `"WorkerW"`, `"Shell_TrayWnd"`, minimized/invisible windows, and our own process ID.
- **Risk — Double press latency when triple press is unused**:
  - *Mitigation*: When `binding.Triple is null`, `ButtonMachine` keeps the exact existing transition (2nd press in `WaitDouble` fires `Double` immediately on press down, zero extra delay).
- **Risk — `StripRunActions` bypass via `triple` or `cycle`**:
  - *Mitigation*: `Validation.StripRunActions` explicitly traverses `Binding.Triple` and every element of `Binding.Cycle`, covered by a dedicated unit test in `StoreTests.cs`.
- **Rollback switches**:
  - Setting `pauseCombo: []`, `pauseHotkey: []`, `pauseOnFullscreen: false`, `backupCount: 0` in `AppSettings`, and leaving `gyroStick.mode: "off"`, `confirmPulses: false`, `triple: null`, `cycle: null` in profiles disables every Phase 10 code path and restores exact Phase 8 behavior.
