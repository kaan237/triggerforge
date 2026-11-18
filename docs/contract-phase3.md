# Phase 3 contract: button mapping, keyboard and mouse

Adds to `docs/contract.md` (phase 2). Nothing there changes except where this file says so.
The game still sees the real controller in this phase; mapping only *adds* keyboard and
mouse input (controller-to-controller remapping is phase 4).

## Projects

| Project | Content | Owner |
|---|---|---|
| `src/Tactile.Input` | `Key` (scan-code enum), `MouseButton`, `KeyInfo`, `IInputInjector`, `IMicrophone`, `ILauncher`, `IElevationProbe`, `InputPlatform` (+ `InputPlatform.None`) | fixed: nobody edits it in this phase |
| `src/Tactile.Input.Windows` | Windows implementations of those interfaces | platform agent |
| `src/Tactile.Core/Mapping/` | mapping model, validation, engine, macros | core agent |

`Tactile.Core` references `Tactile.Input`. `Tactile.Host` references both. The core never
touches Windows input APIs directly; tests use fakes of the interfaces.

## Profile additions (still `version: 1`; missing fields take defaults)

```jsonc
{
  // … every phase 2 field …
  "mapping": {
    "enabled": false,                 // master switch for this profile; false = no keyboard/mouse output
    "layerButton": "ps",              // button name or "none"; held = second layer
    "buttons": { "cross": Binding },  // base layer; key = button name (below)
    "layer":   { "cross": Binding },  // while layerButton is held; a button missing here uses its base binding
    "touchpad": {
      "mode": "off",                  // off | mouse | zones
      "sensitivity": 1.0,             // 0.1-5
      "acceleration": 0.5,            // 0-2
      "scroll": true,                 // mouse mode: two fingers scroll
      "scrollSpeed": 1.0,             // 0.1-5
      "tapToClick": true,             // mouse mode: 1-finger tap = left click, 2-finger tap = right click
      "zoneLayout": "halves",         // halves (left, right) | quadrants (topLeft, topRight, bottomLeft, bottomRight)
      "zones": [Binding, Binding],    // zones mode: pad *click* inside that zone; length 2 or 4 to match the layout
      "swipes": { "up": Action, "down": Action, "left": Action, "right": Action }  // zones mode only
    },
    "gyroMouse": {
      "mode": "off",                  // off | always | whileHeld | toggle
      "button": "r1",                 // for whileHeld / toggle; the button keeps its own binding too
      "axis": "yaw",                  // yaw | roll: which rotation moves the cursor sideways
      "sensitivityX": 1.0,            // 0.1-10; 1.0 = 10 px per degree
      "sensitivityY": 1.0,
      "invertX": false, "invertY": false,
      "smoothing": 0.2,               // 0-0.9
      "deadzone": 0.5                 // deg/s, 0-5
    },
    "sticks": {
      "left":  StickMapping,
      "right": StickMapping
    }
  },
  "macros": [ Macro ]                 // at most 50
}
```

Button names (map keys, `layerButton`, `gyroMouse.button`): the `Buttons` enum members in
camelCase: `square cross circle triangle l1 r1 l2 r2 create options l3 r3 ps touchpad mic
up right down left fn1 fn2 paddleLeft paddleRight`. The `input` message keeps sending
PascalCase (`"Cross"`), as in phase 2; the UI converts.

### Binding

```jsonc
{
  "press":  Action,          // short press; the plain action when hold and double are null
  "hold":   Action | null,   // fires once held for holdMs
  "double": Action | null,   // second press within doubleMs of the first release
  "holdMs": 400,             // 150-2000
  "doubleMs": 250,           // 100-600
  "turbo": false,            // press action repeats while held
  "turboHz": 10,             // 1-30
  "toggle": false            // press action latches: first press down, second press up
}
```

Rules (the engine follows these exactly; tests cover each):

1. **Plain** (hold and double both null): a *held* action (`key`, `mouseButton`) goes down
   with the button and up with it. A *one-shot* action (everything else) fires on button down.
2. **Turbo** (plain only): the press action is tapped at `turboHz`, 50% duty, while held.
   **Toggle** (plain only): first press = down, next press = up; one-shot actions fire on
   every press. `turbo` and `toggle` are ignored when hold or double is set; when both
   are set, turbo wins. Validation keeps the flags as stored; the UI disables them.
3. **With hold and/or double**: nothing fires on button down.
   - Held past `holdMs` (hold set) → `hold` fires: a held action goes down then and up at
     release, a one-shot fires once.
   - Released before `holdMs` (or hold null): if double is set, wait up to `doubleMs` for
     the next press. A second press in time → `double` fires (held: down with that second
     press, up with its release; one-shot: fires). No second press → `press` fires as a **tap**.
   - With hold set and double null, a release before `holdMs` taps `press` at once.
4. A **tap** is down, then up 30 ms later.
5. The release always goes to whatever went down. Changing layer, letting go of the layer
   button or switching profile mid-press never leaves a key stuck.
6. **Layer button**: while `layerButton` is not `none`, that button's own bindings are
   ignored; it only selects the layer. A button pressed while the layer is held uses its
   `layer` binding (or its base binding if it has none) until it is released, even if the
   layer button is let go first.
7. Several bindings may hold the same key; the key goes up only when the last one releases
   (reference count per key and per mouse button).
8. **Release everything** (all keys, mouse buttons, running macros, latched toggles, turbo)
   when: the active profile changes, the active profile is saved with a different `mapping`
   or `macros`, mapping is disabled or paused, the controller disconnects, and on dispose.

### Action

One flat object; `kind` picks which fields are used, validation drops the rest.

| kind | fields | held / one-shot |
|---|---|---|
| `none` | — | — |
| `key` | `keys`: 1-4 `Key` names, pressed in order, released in reverse (a chord like `["leftCtrl","c"]`) | held |
| `mouseButton` | `button`: `left` \| `right` \| `middle` \| `x1` \| `x2` | held |
| `wheel` | `direction`: `up` \| `down` \| `left` \| `right`, `amount`: notches 1-10 | one-shot |
| `macro` | `macroId` (a macro in the same profile; unknown → `none` on save) | see Macro |
| `profile` | `profileId`: an id, `"next"` or `"previous"` (order of `ProfileStore.All`) | one-shot |
| `run` | `path` (absolute, ≤ 260 chars), `args` (≤ 512 chars) | one-shot |
| `micMute` | — toggles the Windows default recording device; the mic LED follows (on = muted) | one-shot |
| `special` | `special`: `screenshot` (Win+PrintScreen) \| `gameBar` (Win+G) \| `taskView` (Win+Tab) \| `desktop` (Win+D) \| `showApp` (brings the Tactile window up) | one-shot |

`Key` names: the members of `Tactile.Input.Key` in camelCase (`a`, `digit1`, `f1`,
`escape`, `enter`, `space`, `leftCtrl`, `rightAlt`, `leftWin`, `up`, `numpad0`,
`numpadEnter`, `minus`, `equals`, `leftBracket`, `backquote`, `intlBackslash`,
`printScreen`, `mediaPlayPause`, `volumeUp`, …). Positions are US-layout physical keys.
For the UI, `KeyboardEvent.code` maps as: `KeyA`→`a`, `Digit1`→`digit1`, `F1`→`f1`,
`Escape`→`escape`, `ControlLeft`→`leftCtrl`, `ShiftRight`→`rightShift`, `AltLeft`→`leftAlt`,
`MetaLeft`→`leftWin`, `ArrowUp`→`up`, `Equal`→`equals`, `BracketLeft`→`leftBracket`,
`IntlBackslash`→`intlBackslash`, `NumpadEnter`→`numpadEnter`, `Numpad0`→`numpad0`,
`Backquote`→`backquote`, `ContextMenu`→`menu`, and the rest by the same pattern.

### Profile import and `run`

`ProfileStore.Import` replaces every `run` action in the imported profile with `none`
(a shared profile must not be able to start programs) and reports how many:
`public Profile Import(string json, out int removedRunActions)`. The one-argument overload
stays and discards the count.

### StickMapping

```jsonc
{
  "mode": "off",          // off | keys | mouse | scroll
  "keys": { "up": "w", "down": "s", "left": "a", "right": "d" },   // keys mode; "none" allowed per direction
  "threshold": 0.5,       // keys mode, 0.1-0.9, on the processed (deadzone/curve) value per axis; diagonals press two keys
  "speed": 1.0            // 0.1-5; mouse: full tilt = 1200 px/s × speed; scroll: full tilt = 15 notches/s × speed
}
```

Default for both sticks: `mode: off`, keys `w/s/a/d`. Uses the profile's stick processing
(phase 2 `StickProcessor`). Keys-mode keys are held keys and follow rules 5, 7 and 8.

### Touchpad details

The pad reports X 0-1919, Y 0-1079. Mouse mode: full pad width = 900 px at sensitivity 1
without acceleration; acceleration raises the gain with finger speed. Moves keep the
sub-pixel remainder. Two fingers: vertical and horizontal scroll (smooth deltas, not whole
notches). Tap: a touch shorter than 200 ms that moved less than 3% of the pad width. In
mouse mode the physical pad click goes to `buttons.touchpad` as normal; in zones mode the
click goes to the zone binding under finger 1 and `buttons.touchpad` is ignored. A swipe
(zones mode) is one finger moving more than 25% of the pad's width or height in under
500 ms; it fires on lift and cancels a zone press that started during it.

### Gyro mouse details

Gyro in deg/s from `AppCore.GyroDegPerSec` (calibration and drift removed). Pitch = gyro X
→ cursor Y, yaw = gyro Y or roll = gyro Z → cursor X. Signs will be checked on hardware;
keep them in one place. Smoothing is an exponential average; below the deadzone the
output is 0.

### Macro

```jsonc
{
  "id": "…",                 // GUID "N"
  "name": "Combo 1",         // ≤ 64 chars
  "repeat": "once",          // once | whileHeld | toggle
  "steps": [                 // ≤ 500
    { "op": "down",  "key": "leftShift" },
    { "op": "tap",   "key": "a" },          // tap = down, 30 ms, up
    { "op": "up",    "key": "leftShift" },
    { "op": "mouseDown", "button": "left" }, { "op": "mouseUp", "button": "left" },
    { "op": "click", "button": "right" },
    { "op": "wheel", "direction": "down", "amount": 1 },
    { "op": "delay", "ms": 50 }             // 0-60000
  ]
}
```

- `once`: runs to the end; pressing again while it runs does nothing.
- `whileHeld`: starts on press, loops until the button is released, then stops after the
  current step.
- `toggle`: first press starts looping, next press stops.
- Stopping (for any reason in rule 8 too) releases every key and mouse button the macro holds.
- A loop round with no delay at all gets 10 ms added between rounds so it cannot spin.
- Macros run off the input thread.
- **Recording happens in the UI**: the web page captures key and mouse events inside a
  recording box with their timings and builds the steps. The host has no global keyboard hook.

## Core API additions

```csharp
namespace Tactile.Core;

public sealed class AppCore
{
    // New optional parameter; null = InputPlatform.None (no output).
    public AppCore(string? dataDir = null, IForegroundSource? foreground = null, InputPlatform? platform = null);
    // The internal test constructor gets the same InputPlatform parameter.

    /// While true the mapper releases everything and ignores input (UI key capture, macro recording).
    public bool MappingPaused { get; set; }
    public MappingStatus Mapping { get; }
    public event Action? MappingChanged;          // Mapping changed (active / paused / blockedBy)
    public event Action? ShowAppRequested;        // special: showApp
}

/// Active = enabled in the active profile, not paused, controller connected.
/// BlockedBy = exe name of an elevated foreground window that drops our input, else null.
public sealed record MappingStatus(bool Active, bool Paused, string? BlockedBy);
```

- The mapper runs on every input report (low latency) and on the 30 Hz tick (timers while
  input is quiet). Time comes from the core's `TimeProvider`, so tests drive it.
- Slow actions (`run`, `profile`, `micMute`, `showApp`, starting macros) go to one worker
  thread, never the HID reader thread.
- Mouse moves from touchpad, gyro and sticks are summed and sent at most once per 4 ms.
- Elevation: when the foreground app changes and mapping is active, ask
  `IElevationProbe.ForegroundBlocksInput()`; if true set `BlockedBy` and raise one notice
  (kind `elevated`) per exe name per run.
- Runtime failures (run failed, macro missing, no microphone) raise notice kind `mapping`.

## Windows platform (`Tactile.Input.Windows`)

- `WindowsInputInjector`: `SendInput`. Keys go as scan codes (`KEYEVENTF_SCANCODE`, plus
  `KEYEVENTF_EXTENDEDKEY` for 0xE0xx), except media keys and `printScreen`, which go as
  virtual keys. Mouse moves are relative (`MOUSEEVENTF_MOVE`); wheel uses
  `MOUSEEVENTF_WHEEL` / `MOUSEEVENTF_HWHEEL`.
- `WindowsMicrophone`: Core Audio (`IMMDeviceEnumerator` → default capture endpoint →
  `IAudioEndpointVolume` mute), COM interop written by hand.
- `WindowsLauncher`: `Process.Start` with `UseShellExecute = true`; missing file or a
  start failure becomes `InvalidOperationException` with a Turkish message.
- `WindowsElevationProbe`: foreground window → process id → token elevation, compared with
  this process's own elevation. Errors count as "not blocked".
- `WindowsInputPlatform.Create()` returns the `InputPlatform` with all four.

## Bridge additions

UI → host:

| type | fields |
|---|---|
| `mapping.pause` | `paused: bool` (UI sends true while a key-capture field or the macro recorder is focused) |
| `run.pick` | — host shows an Open File dialog (`.exe`) and answers `runPicked` |
| `app.restartElevated` | — host restarts itself as administrator (UAC prompt); on cancel, notice `info` |

Host → UI:

| type | fields |
|---|---|
| `status` | + `mapping: { active, paused, blockedBy }`, + `elevated: bool` (this process) |
| `runPicked` | `path: string \| null` (null = cancelled) |
| `notice` | + kinds `elevated`, `mapping` |

- `Bridge.Detach` (page reloads or navigates) also sets `MappingPaused = false`.
- The `profile.import` notice says how many `run` actions were removed when > 0.
- `status` is re-sent when `MappingChanged` fires.
- `ShowAppRequested` shows and activates the main window.
- Restart elevated: start `Tactile.exe --after <pid>` with verb `runas`, then exit. A
  process started with `--after <pid>` waits up to 5 s for that process to exit before it
  takes the single-instance mutex. Same for the tray flag: pass `--tray` along if the
  current run had it.
