# Phase 2 contract

Shared between the core (C#), the host bridge and the React UI. Change it only by
editing this file first.

## Files on disk

`%APPDATA%\Tactile\` (override with env `TACTILE_DATA_DIR`, used by tests):

| File | Content |
|---|---|
| `settings.json` | `AppSettings` |
| `profiles\<id>.json` | one `Profile` each |

Writes go to `<name>.tmp` then `File.Move(overwrite: true)`. A file that fails to
parse is renamed to `<name>.broken-<yyyyMMddHHmmss>` and skipped; the app keeps running
and reports it (`notice` kind `brokenFile`). If no profile exists, a "Varsayılan"
profile is created.

All JSON: camelCase, enums as camelCase strings, `version` field on every root
object (currently `1`). Unknown fields are ignored; missing fields take defaults.

## Profile (v1)

```jsonc
{
  "version": 1,
  "id": "3f2a…",                    // GUID "N" format, also the file name
  "name": "Ghost of Tsushima",
  "games": ["GhostOfTsushima.exe"],  // process file names, case-insensitive; empty = manual only
  "lightbar": {
    "effect": "static",              // off | static | breathe | strobe | rainbow | battery | playerColor
    "color": "#3060FF",
    "color2": "#000000",             // second colour for breathe/strobe
    "speed": 1.0,                    // 0.1-5, cycles per second scale
    "brightness": "high"             // high | medium | low
  },
  "playerLeds": { "mode": "number", "number": 1, "mask": 0 },  // mode: off | number | custom (mask 5 bits)
  "micLed": "off",                   // off | on | pulse
  "triggers": {
    "left":  { "mode": "off", "params": {}, "zones": null, "frequency": 0 },
    "right": { "mode": "weapon", "params": { "start": 3, "end": 6, "strength": 8 } }
  },
  "triggerResponse": {               // analog L2/R2 pressure shaping (applied in phase 4, shown now)
    "left":  { "deadzone": 0, "max": 255, "curve": "linear", "exponent": 1.0, "points": null },
    "right": { "deadzone": 0, "max": 255, "curve": "linear", "exponent": 1.0, "points": null }
  },
  "sticks": {
    "left":  { "innerDeadzone": 0.05, "outerDeadzone": 0.02, "shape": "round", "curve": "linear",
               "exponent": 1.0, "points": null, "antiDeadzone": 0.0, "invertX": false, "invertY": false },
    "right": { /* same */ }
  },
  "gyro": { "sensitivity": 1.0, "smoothing": 0.2, "invertX": false, "invertY": false },
  "rumble": { "strength": 1.0 }      // 0-2 multiplier
}
```

Trigger `mode` values and their `params` (ranges are the ones `TriggerEffect` enforces):

| mode | params |
|---|---|
| `off` | — |
| `feedback` | `start`, `strength` |
| `zones` | uses `zones`: 10 ints 0-8 |
| `slope` | `start`, `end`, `startStrength`, `endStrength` |
| `weapon` | `start`, `end`, `strength` |
| `vibration` | `start`, `amplitude`, `frequency` |
| `zonesVibration` | uses `zones` (0-8) and `frequency` |
| `bow` | `start`, `end`, `strength`, `snap` |
| `galloping` | `start`, `end`, `firstFoot`, `secondFoot`, `frequency` |
| `machine` | `start`, `end`, `amplitudeA`, `amplitudeB`, `frequency`, `period` |

Curve: `linear` | `exponential` (uses `exponent`, 0.3-4) | `custom` (uses `points`:
5 `[x, y]` pairs in 0-1, x ascending, first `[0,0]`, last `[1,1]`; piecewise linear).

## AppSettings (v1)

```jsonc
{
  "version": 1,
  "startWithWindows": false,
  "closeToTray": true,
  "language": "tr",                  // tr | en
  "autoProfile": true,
  "autoProfileDelayMs": 800,         // foreground must stay this long before switching
  "defaultProfileId": "…",
  "controllerDefaults": { "AA:BB:…": "<profileId>" },   // per controller MAC
  "idleSleepMinutes": 15,            // 0 = never; Bluetooth only
  "lowBatteryWarnings": [20, 10],
  "runAsAdminHint": false
}
```

## Core API (Tactile.Core) used by the host

```csharp
public sealed class ProfileStore            // load/save/delete/import/export, atomic writes
{
    public ProfileStore(string? dataDir = null);
    public IReadOnlyList<Profile> All { get; }
    public Profile? Get(string id);
    public Profile Save(Profile p);         // validates + clamps, returns stored copy
    public bool Delete(string id);          // refuses to delete the last profile
    public Profile Duplicate(string id, string newName);
    public Profile Import(string json);     // new id assigned
    public string Export(string id);
    public event Action? Changed;
    public IReadOnlyList<string> BrokenFiles { get; }
}

public sealed class SettingsStore { public SettingsStore(string? dataDir = null); public AppSettings Current { get; } public void Save(AppSettings s); }

public sealed class AppCore : IDisposable   // the one object the host creates
{
    public AppCore(string? dataDir = null, IForegroundSource? foreground = null);
    public ControllerService Controllers { get; }
    public ProfileStore Profiles { get; }
    public SettingsStore Settings { get; }
    public Profile ActiveProfile { get; }
    public string ActiveReason { get; }      // "manual" | "game:<exe>" | "controllerDefault" | "default"
    public void ActivateProfile(string id);  // manual pin until the foreground game changes
    public void PreviewTrigger(bool left, TriggerConfig? config);  // null = back to profile
    public void StartGyroCalibration();      // 3 s still; progress via event
    public StickProcessor LeftStick { get; } public StickProcessor RightStick { get; }
    public event Action? ActiveProfileChanged;
    public event Action<Notice>? Notice;     // lowBattery, brokenFile, idleSleep, gyroCalibration
}

public interface IForegroundSource { event Action<string /*exe file name*/>? Changed; string? Current { get; } }
```

`ControllerService` stays the low-level owner of the device. The engine computes the
`OutputState` from the active profile at 30 Hz (lightbar effects animate) and only
sends when it changed.

## Bridge messages

UI → host (`send({type, ...})`). Existing Phase 1 messages stay (`lightbar`, `rumble`,
`trigger`, `haptic`, `reset`, …) and act as live overrides on top of the profile.

| type | fields |
|---|---|
| `hello` | — host answers `status`, `output`, `profiles`, `settings` |
| `profile.save` | `profile` (full object) |
| `profile.delete` | `id` |
| `profile.duplicate` | `id`, `name` |
| `profile.activate` | `id` |
| `profile.import` | `json` (string) |
| `profile.export` | `id` → host answers `export` |
| `trigger.preview` | `side` (`left`/`right`), `config` (TriggerConfig) or `null` to stop |
| `settings.save` | `settings` (full object) |
| `gyro.calibrate` | — |
| `foreground.pick` | — host answers `foreground` with recent exe names (last 10 seen) |

Host → UI:

| type | fields |
|---|---|
| `status` | Phase 1 fields + `activeProfileId`, `activeReason` |
| `input` | Phase 1 fields + `leftProcessed: [x,y]`, `rightProcessed: [x,y]` (−1..1 after deadzone/curve) |
| `output` | Phase 1 fields |
| `profiles` | `list: Profile[]`, `activeId` |
| `settings` | `settings` |
| `export` | `id`, `json` |
| `foreground` | `recent: string[]` |
| `notice` | `kind` (`lowBattery` / `brokenFile` / `idleSleep` / `gyroCalibration` / `info`), `message`, optional `progress` 0-1 |
| `error` | `message` |
