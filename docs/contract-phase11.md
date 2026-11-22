# Phase 11 contract: button engine v2 — chords, multi-shift layers, trigger stages, stick rings, flick stick

Adds to `docs/contract.md`, `docs/contract-phase3.md`, and the phase 4-10 contracts. Version becomes **1.2.0**.

Goal: upgrade the mapping engine with five advanced input mechanisms — simultaneous button chords (`2-3` buttons with window, conflict priority, and component suppression), up to `4` stacked shift layers (`hold` / `toggle` with automatic migration from legacy `layerButton` + `layer`), analog trigger soft/full pull stages with hysteresis, left/right stick inner/outer ring actions with hysteresis, and right-stick Flick Stick (`360°` mouse count calibration wizard working alongside Gyro Mouse) — while keeping every existing profile, setting, and test (`659+` tests) unbroken.

## 1. Scope

### In scope (5 implemented features)

1. **Button chords / simultaneous press (`mapping.chords`, `mapping.chordWindowMs`)**:
   - Up to `32` chords per profile. Each chord binds `2` or `3` distinct physical buttons (`buttons: ButtonName[]`) to an `ActionConfig` (`action`).
   - Time window `mapping.chordWindowMs` (default `50` ms, range `20..300` ms).
   - **Zero latency for non-chord buttons**: Physical buttons that do not appear in any configured chord bypass the chord buffer and fire `ButtonMachine.Down` immediately (`0` ms delay).
   - **Component suppression**: Buttons that participate in at least one configured chord enter a pending state on press for up to `chordWindowMs`. When all buttons of a chord become pressed within `chordWindowMs` of the first pressed component in that chord, their individual single-button bindings are suppressed and only the chord `action` fires.
   - **Conflict priority**: A `3`-button chord takes priority over any `2`-button subset chord. When a `2`-button chord `(A, B)` is matched and a `3`-button superset chord `(A, B, C)` also exists in `mapping.chords`, the engine waits until either `C` arrives within `chordWindowMs` (firing `A+B+C`) or the window expires / `A` or `B` is released (firing `A+B`). If no `3`-button superset exists, `A+B` fires immediately when the second button is pressed. Among chords of equal size, earlier index in `mapping.chords` wins.
   - **Release rule**: A held chord action stays down while all component buttons remain held and releases immediately when **any** component button is released. The remaining still-held component buttons stay consumed until physically released and do not late-fire their single-button bindings or re-trigger another chord without a fresh press. If a pending button times out or is released before completing any chord, its single-button `ButtonMachine` receives the delayed press (and release, if already let go).
2. **Multi-shift layers (`mapping.layers`, up to 4 layers + base)**:
   - Extends the single secondary layer into an ordered list `mapping.layers` of `1..4` shift layers above the base layer (`mapping.buttons`).
   - Each layer has `name` (`1..32` chars), `button` (`ButtonName | "none"`, including DualSense Edge `fn1`, `fn2`, `paddleLeft`, `paddleRight`), `mode` (`"hold" | "toggle"`, default `"hold"`), and `buttons` (`Partial<Record<ButtonName, Binding>>`).
   - **Activator protection**: Any button assigned as a layer's `button` (`!= "none"`) acts exclusively as that layer's activator; its own action bindings in base or shift layers and chord participation are ignored. Pressing/releasing a `hold` layer button or toggling a `toggle` layer button fires `_host.PulseConfirm` when `haptics.confirmPulses` is `true`.
   - **Layer inheritance (fallback)**: When resolving a button's `Binding`, active layers are scanned from highest index (`layers[Count - 1]`) down to lowest (`layers[0]`), falling back to the base layer (`mapping.buttons`) when a button is not bound in the active higher layer(s).
   - **Clean handover on layer change**: Whenever the active layer set changes (any layer activates or deactivates via `hold` or `toggle`), all currently held `ButtonMachine` outputs, active/pending chords, and pending taps are immediately released so no action from the previous layer can get stuck ("layer trap"). Still-held physical buttons do not fire the new layer's binding until released and pressed again.
   - **Automatic legacy migration**: Existing profiles with `layerButton` + `layer` and no `layers` array automatically migrate on load/normalize to `layers: [{ name: "Layer 1", button: layerButton, mode: "hold", buttons: layer }]`, while keeping `layerButton` and `layer` mirrored to `layers[0]` in memory and JSON for backwards compatibility.
3. **Analog trigger soft / full pull (`mapping.triggers.left`, `mapping.triggers.right`)**:
   - Evaluates the profile's shaped trigger travel (`TriggerResponseProcessor.Process(raw) / 255.0` in `[0.0, 1.0]`) against two thresholds: `softThreshold` (default `0.25`, range `0.05..0.90`) and `fullThreshold` (default `0.95`, range `0.15..1.00`, enforced `>= softThreshold + 0.05`), with release `hysteresis` (default `0.05`, range `0.01..0.20`).
   - Each trigger has `mode` (`"off" | "additive" | "exclusive" | "hipFire"`, default `"off"`), `soft` (`ActionConfig`), `full` (`ActionConfig`), and `hipFireMs` (default `80` ms, range `20..300` ms):
     - `"off"`: two-stage trigger mapping is inactive; `l2`/`r2` in `mapping.buttons`/`layers` behaves as a regular digital button.
     - `"additive"`: crossing `softThreshold` holds `soft` (until travel drops below `softThreshold - hysteresis`); crossing `fullThreshold` holds `full` alongside `soft` (until travel drops below `fullThreshold - hysteresis`).
     - `"exclusive"`: crossing `softThreshold` holds `soft`; crossing `fullThreshold` releases `soft` and holds `full` until travel drops below `fullThreshold - hysteresis` (returning below `fullThreshold - hysteresis` does not re-press `soft` until travel drops below `softThreshold - hysteresis` and re-enters).
     - `"hipFire"`: crossing `softThreshold` starts a `hipFireMs` timer. Reaching `fullThreshold` within `hipFireMs` skips `soft` completely and holds only `full`. If `hipFireMs` expires before `fullThreshold` is reached, `soft` fires and stays held (and pulling further to `fullThreshold` later in the same pull also holds `full` additively; releasing below `softThreshold - hysteresis` before `hipFireMs` without reaching `fullThreshold` taps `soft`).
   - When a trigger's `mode != "off"`, its two-stage state machine replaces the regular single-button `_machines[Buttons.L2 / R2]` lookup (unless `l2`/`r2` is used as a layer activator or chord component).
4. **Stick outer / inner ring actions (`mapping.sticks.left`, `mapping.sticks.right`)**:
   - Extends `StickMapping` with `ringMode` (`"off" | "outer" | "inner"`, default `"off"`), `ringAction` (`ActionConfig`, default `none`), `ringThreshold` (default `0.90`, range `0.20..0.98`), and `ringHysteresis` (default `0.05`, range `0.01..0.20`).
   - Evaluates radial magnitude $r = \min(1.0, \sqrt{x^2 + y^2})$ of the shaped stick output `(x, y) = StickProcessor.Process(raw)` in parallel with the stick's primary `mode` (`off | keys | mouse | scroll`):
     - `"outer"`: presses/holds `ringAction` when $r \ge \text{ringThreshold}$ and releases when $r < \text{ringThreshold} - \text{ringHysteresis}$.
     - `"inner"`: presses/holds `ringAction` when the stick leaves the inner deadzone ($r > 0$) while below the outer ring ($r < \text{ringThreshold}$ when entering from center, or $r \le \text{ringThreshold} - \text{ringHysteresis}$ when returning from the outer ring), and releases when $r == 0$ (center deadzone) or $r \ge \text{ringThreshold}$.
5. **Flick Stick (right stick → horizontal mouse) & 360° calibration wizard (`mapping.flickStick`)**:
   - Converts physical right-stick angle $\theta = \text{atan2}(x, y) \times \frac{180}{\pi} \in [-180^\circ, +180^\circ]$ (where $0^\circ$ is forward/$+Y$ and $+90^\circ$ is right/$+X$) into horizontal relative mouse movement (`dx`, with `dy = 0`) through the shared `PointerOutput` sub-pixel accumulator.
   - Parameters: `enabled` (`bool`, default `false`), `countsPer360` (`int`, default `6545`, range `500..30000`), `threshold` (`double`, default `0.90`, range `0.50..0.98`, release hysteresis `0.05`), `flickTimeMs` (`int`, default `100` ms, range `30..300` ms), `snap` (`"off" | "four" | "eight"`, default `"off"`), `deadzoneAngle` (`double`, default `0.0` deg, range `0.0..45.0` deg), and `smoothing` (`double`, default `0.2`, range `0.0..0.9`).
   - **Flick + Sweep phases**:
     - When raw right-stick magnitude $r = \sqrt{x^2 + y^2}$ crosses `threshold` from below, an initial **Flick** begins toward angle $\theta_0$ (filtered by `deadzoneAngle` around $0^\circ$ and snapped to $90^\circ$ or $45^\circ$ increments when `snap` is `"four"` or `"eight"`). Total flick counts $\theta_{\text{flick}} \times \frac{\text{countsPer360}}{360.0}$ are emitted smoothly over `flickTimeMs` (completing even if the stick returns to center before `flickTimeMs` elapses, unless cancelled by `ReleaseAll()` or replaced by a new flick).
     - While the stick remains engaged ($r \ge \text{threshold} - 0.05$), subsequent frame-to-frame angle deltas $\Delta\theta = \text{WrapDegrees}(\theta_t - \theta_{t-1}) \in (-180^\circ, +180^\circ]$ drive continuous **Sweep** rotation (`_sweepRemainder += deltaTheta; step = _sweepRemainder * (1 - smoothing); _sweepRemainder -= step`), conserving 100% of total angle while smoothing quantization noise.
   - **Works with Gyro Mouse**: Flick Stick and `GyroMouse` feed the same `PointerOutput` simultaneously. Optional `mapping.gyroMouse.useCalibration` (`bool`, default `false`) switches `GyroMouse` from the fixed `10` px/deg scale to `countsPer360 / 360.0` counts/deg so `sensitivityX = 1.0` equals a real-world `1:1` turn sharing the same calibration. When `flickStick.enabled` is `true`, `mapping.sticks.right.mode` (`keys | mouse | scroll`) is bypassed while `sticks.right` ring actions continue to work.
   - **Virtual controller interplay**: when emulation is on and `flickStick.enabled` is `true`, the virtual pad's right stick is centered (the physical right stick drives only the mouse), the same way `hideMappedButtons` hides mapped buttons; Phase 10 `gyroStick` offset still applies on top. A stick whose `ringMode != "off"` keeps feeding the virtual pad (ring actions are additive).
   - **360° calibration wizard**: UI wizard on the Flick Stick / Gyro Mouse tabs lets the user adjust `countsPer360` and trigger a delayed test turn (`mapping.testTurn` bridge message: after a `2000` ms countdown so the user can focus the game window, injects exactly `countsPer360` horizontal mouse counts over `200` ms).

### Out of scope

- **Action sets**: Only shift layers (`1..4`) over the base mapping are in scope; independent full action sets are excluded.
- **Radial menu / OSD overlay**: Phase 14 and Phase 15.4.
- **Flick Stick to virtual gamepad stick**: Flick Stick outputs relative mouse $\Delta X$ only.
- **Secondary controllers (slots 1-3)**: Keep Phase 8 rules (`docs/contract-phase8.md:26-27`); mapping runs on slot 0 (primary) only.

---

## 2. Profile and Settings Schema

`Profile.CurrentVersion` (`src/Tactile.Core/Model/Profile.cs:26`) and `AppSettings.CurrentVersion` (`src/Tactile.Core/Model/AppSettings.cs:7`) stay `1` (no destructive file format break; legacy profiles migrate in-place during `Validation.Normalize` and `normalizeMapping`).

### Migration and backwards-compatibility rule

1. **Legacy `layerButton` + `layer` → `layers` automatic migration**:
   - In C# (`src/Tactile.Core/Storage/MappingValidation.cs:172-252`) and TypeScript (`ui/src/model.ts:306-330`), when `mapping.layers` is `null` or empty (`Count == 0`):
     - Normalize `mapping.layerButton` (default `"ps"`) and `mapping.layer` (default `{}`) using the existing rules.
     - Populate `mapping.layers` with a single migrated layer:
       `[{ name: "Layer 1", button: normalizedLayerButton, mode: "hold", buttons: normalizedLayer }]`.
   - When `mapping.layers` has `1..4` elements, **`layers` is the single source of truth**. Normalize always overwrites `LayerButton = layers[0].Button` and `Layer = layers[0].Buttons` (legacy mirror, written to JSON for older app versions). No mutation detection: code that needs to change Layer 1 edits `layers[0]`; existing callers that only read `LayerButton`/`Layer` keep working. Existing tests that construct a `MappingConfig` with only `LayerButton`/`Layer` (no `layers`) go through the empty-`layers` migration above.
2. **Default values for missing Phase 11 fields**:
   - Profiles from Phases 2-10 without `chords`, `chordWindowMs`, `layers`, `triggers`, `flickStick`, `sticks.*.ringMode`, or `gyroMouse.useCalibration` deserialize cleanly to the defaults below with identical runtime behavior.
3. **Test requirement**:
   - Loading a Phase 3-10 profile JSON with `"layerButton": "l1"` and `"layer": { "cross": ... }` (and no `"layers"` key) must populate `mapping.layers[0]` with `button == "l1"`, `mode == LayerMode.Hold`, and `buttons["cross"]`, keep `mapping.layerButton == "l1"` and `mapping.layer["cross"]` identical, and execute Layer 1 while `L1` is held.

### Profile additions (`Profile.Mapping`)

```jsonc
{
  "mapping": {
    "enabled": false,
    "layerButton": "ps",               // mirrored from layers[0].button for legacy compatibility
    "buttons": { "cross": Binding },   // base layer
    "layer":   { "cross": Binding },   // mirrored from layers[0].buttons for legacy compatibility
    "layers": [                        // 1-4 shift layers; index 0 is lowest shift, index Count-1 is highest
      {
        "name": "Layer 1",             // string, 1-32 chars, default "Layer 1" ("Katman 1")
        "button": "ps",                // ButtonName | "none", default "ps" for layer 0, "none" for 1..3
        "mode": "hold",                // "hold" | "toggle", default "hold"
        "buttons": { "cross": Binding }
      }
    ],
    "chordWindowMs": 50,               // int, default 50, clamped to 20..300
    "chords": [                        // ButtonChord[], default [], max 32
      {
        "buttons": ["l1", "cross"],    // 2-3 distinct ButtonNames, normalized to canonical enum order
        "action": { "kind": "key", "keys": ["f5"] }
      }
    ],
    "triggers": {
      "left": {
        "mode": "off",                 // "off" | "additive" | "exclusive" | "hipFire", default "off"
        "softThreshold": 0.25,         // 0.05-0.90, default 0.25
        "fullThreshold": 0.95,         // 0.15-1.00, default 0.95 (>= softThreshold + 0.05)
        "hysteresis": 0.05,            // 0.01-0.20, default 0.05
        "hipFireMs": 80,               // 20-300, default 80
        "soft": { "kind": "none" },    // ActionConfig
        "full": { "kind": "none" }     // ActionConfig
      },
      "right": { /* same as left */ }
    },
    "touchpad": { /* unchanged */ },
    "gyroMouse": {
      // ... existing fields ...
      "useCalibration": false          // bool, default false; when true uses flickStick.countsPer360 / 360.0 px/deg
    },
    "sticks": {
      "left": {
        "mode": "off",                 // "off" | "keys" | "mouse" | "scroll"
        "keys": { "up": "w", "down": "s", "left": "a", "right": "d" },
        "threshold": 0.5,              // 0.1-0.9
        "speed": 1.0,                  // 0.1-5.0
        "ringMode": "off",             // "off" | "outer" | "inner", default "off"
        "ringThreshold": 0.90,         // 0.20-0.98, default 0.90
        "ringHysteresis": 0.05,        // 0.01-0.20, default 0.05
        "ringAction": { "kind": "none" }
      },
      "right": { /* same as left */ }
    },
    "flickStick": {
      "enabled": false,                // bool, default false
      "countsPer360": 6545,            // int, 500-30000, default 6545
      "threshold": 0.90,               // 0.50-0.98, default 0.90
      "flickTimeMs": 100,              // int, 30-300, default 100
      "snap": "off",                   // "off" | "four" | "eight", default "off"
      "deadzoneAngle": 0.0,            // deg, 0.0-45.0, default 0.0
      "smoothing": 0.2                 // 0.0-0.9, default 0.2
    }
  }
}
```

### Validation & security rules for new fields

- `Validation.MaxLayers = 4`, `Validation.MaxLayerNameLength = 32`, `Validation.MaxChords = 32`.
- **`MappingLayer` normalization**:
  - `Layers` capped at `MaxLayers = 4` (excess layers removed with an issue notice). If `Layers` is null or empty, 1 layer is created from `LayerButton` and `Layer`.
  - `Name`: trimmed; if empty, defaults to `$"Layer {i + 1}"` / `$"Katman {i + 1}"`; truncated to `32` chars.
  - `Button`: `"none"` (case-insensitive) or a valid `ButtonName`; unknown button names fall back to `"ps"` for layer 0 or `"none"` for layers 1..3 with an issue notice. If two layers specify the same non-`"none"` activator button, the later layer's `Button` is reset to `"none"` with an issue notice.
  - `Mode`: `Defined(layer.Mode, LayerMode.Hold)`.
  - `Buttons`: normalized via `ButtonMap`.
- **`ButtonChord` normalization**:
  - `ChordWindowMs`: `Math.Clamp(m.ChordWindowMs, 20, 300)` (when `0` from default/uninitialized struct, defaults to `50`).
  - Each chord's `Buttons` is parsed via `ButtonNames.TryParse`, deduplicated, and sorted in `ButtonNames.All` enum order.
  - A chord with `< 2` valid distinct buttons is dropped with an issue notice; a chord with `> 3` valid buttons is truncated to the first `3` with an issue notice.
  - `Action` is normalized via `ActionOf`. Chords whose normalized `Action.Kind == ActionKind.None` or whose exact canonical `Buttons` tuple duplicates an earlier chord in `Chords` are removed (duplicate combo raises an issue notice).
  - Total chords capped at `MaxChords = 32` (excess removed with an issue notice).
- **`TriggerMapping` normalization**:
  - `Mode`: `Defined(t.Mode, TriggerStageMode.Off)`.
  - `SoftThreshold`: `Clamp(t.SoftThreshold, 0.05, 0.90, 0.25)`.
  - `FullThreshold`: `Math.Max( softThreshold + 0.05, Clamp(t.FullThreshold, 0.15, 1.00, 0.95) )`.
  - `Hysteresis`: `Clamp(t.Hysteresis, 0.01, 0.20, 0.05)`.
  - `HipFireMs`: `Math.Clamp(t.HipFireMs, 20, 300)`.
  - `Soft` and `Full`: normalized via `ActionOf`.
- **`StickMapping` ring normalization**:
  - `RingMode`: `Defined(s.RingMode, StickRingMode.Off)`.
  - `RingThreshold`: `Clamp(s.RingThreshold, 0.20, 0.98, 0.90)`.
  - `RingHysteresis`: `Clamp(s.RingHysteresis, 0.01, 0.20, 0.05)`.
  - `RingAction`: normalized via `ActionOf`.
- **`FlickStickConfig` normalization**:
  - `CountsPer360`: `Math.Clamp(f.CountsPer360, 500, 30000)`.
  - `Threshold`: `Clamp(f.Threshold, 0.50, 0.98, 0.90)`.
  - `FlickTimeMs`: `Math.Clamp(f.FlickTimeMs, 30, 300)`.
  - `Snap`: `Defined(f.Snap, FlickSnapMode.Off)`.
  - `DeadzoneAngle`: `Clamp(f.DeadzoneAngle, 0.0, 45.0, 0.0)`.
  - `Smoothing`: `Clamp(f.Smoothing, 0.0, 0.9, 0.2)`.
- **`Validation.StripRunActions` (`src/Tactile.Core/Storage/MappingValidation.cs:17-55`)**:
  - MUST strip `ActionKind.Run` from:
    1. `m.Buttons` (base layer),
    2. all `m.Layers[i].Buttons` (and mirror `m.Layer = strippedLayers[0].Buttons` without double-counting `m.Layer` when `m.Layers.Count > 0`; if `m.Layers` is empty on an un-normalized profile, strip `m.Layer`),
    3. all `m.Chords[i].Action` (removing chords whose action was `Run`),
    4. `m.Triggers.Left.Soft`, `m.Triggers.Left.Full`, `m.Triggers.Right.Soft`, `m.Triggers.Right.Full`,
    5. `m.Sticks.Left.RingAction`, `m.Sticks.Right.RingAction`,
    6. `m.Touchpad.Zones` and `m.Touchpad.Swipes`,
    and include every stripped `run` action in `out int removed`.
- **`EmulationInput.MappedButtons` (`src/Tactile.Core/Emulation/EmulationInput.cs:86-125`)**:
  - Includes buttons bound across `m.Buttons` and all `m.Layers[i].Buttons` (plus `m.Layer` if `m.Layers` is empty), each layer's activator `button` when that layer has at least one bound button, all buttons in any `m.Chords` entry with `Action.Kind != ActionKind.None`, and `Buttons.L2` / `Buttons.R2` when `m.Triggers.Left` / `Right` has `Mode != TriggerStageMode.Off` and a non-`None` `Soft` or `Full` action.

---

## 3. Bridge (`src/Tactile.Host/Bridge.cs` ↔ `ui/src/host.ts`) Contract

Both Job A and Job B must implement these exact message shapes and property names:

### Host → UI messages

| Message `type` | Field / Shape | Description |
|---|---|---|
| `profiles` | `list: Profile[], activeId: string` | Existing message; each `Profile.mapping` now includes `layers`, `chordWindowMs`, `chords`, `triggers`, `flickStick`, `gyroMouse.useCalibration`, and `sticks.left/right` ring fields (`ringMode`, `ringThreshold`, `ringHysteresis`, `ringAction`). |

### UI → Host messages

| Message `type` | Payload fields | Host behavior |
|---|---|---|
| `mapping.testTurn` | `counts: number` (`500..30000`), `delayMs?: number` (`0..5000`, default `2000`) | Validates `appSource && _ready`, clamps `counts` to `500..30000` and `delayMs` to `0..5000`, and calls `_core.StartTestTurn(counts, delayMs)`. After `delayMs` elapses on the mapper clock, smoothly injects `counts` horizontal mouse counts (`dx = counts, dy = 0`) over `200 ms`, then posts an `info` notice: `"Sent 360° test turn (<counts> counts)."` / `"360° deneme dönüşü gönderildi (<counts> sayım)."`. Cancelled immediately if mapping is paused, profile changes, or controller disconnects. |

---

## 4. Work Split

Job A and Job B run in parallel with **strictly disjoint file sets**. `ButtonMachine` and `Mapper` are modified **only** in Job A (no parallel split inside the mapping engine).

| Job | Scope | Allowed files | Forbidden files | When |
|---|---|---|---|---|
| **A** | C# Mapping Engine v2 (`Mapper`, `ButtonMachine`, Chords, Layers, Triggers, Rings, FlickStick), Model, Validation, Bridge, Unit Tests | `src/**`, `tests/**` | `ui/**`, `docs/**` | Parallel with Job B |
| **B** | React/TypeScript UI Editors (Layers, Chords, Trigger Stages, Stick Rings, Flick Stick + 360° Wizard), EN/TR strings, browser preview (`sim.ts`) | `ui/src/**` | `src/**`, `tests/**`, `docs/**` | Parallel with Job A |
| **C** | Post-merge verification & small integration fixes (only if needed) | `src/Tactile.Host/**`, `ui/src/**` | `third_party/**`, `secrets/**` | After Jobs A & B finish |

### Job A (`src/` + `tests/`)

- **Files to create**:
  - `src/Tactile.Core/Mapping/ChordMachine.cs`: pending button buffer (`_pending`), active chord tracker (`_active`), and consumed-button mask (`_consumed`) implementing `2-3` button simultaneous press within `chordWindowMs`, `3`-button over `2`-button conflict priority, component suppression, release cleanup, and timeout flushing to per-button `ButtonMachine`s.
  - `src/Tactile.Core/Mapping/TriggerMapper.cs`: two-stage analog trigger state machine for one trigger (`L2` or `R2`) using `TriggerResponseProcessor` + `TriggerMapping` (`Off`, `Additive`, `Exclusive`, `HipFire` with `softThreshold`, `fullThreshold`, `hysteresis`, `hipFireMs`) and `ActionDispatcher`.
  - `src/Tactile.Core/Mapping/FlickStick.cs`: right-stick flick + sweep state machine and `200 ms` test-turn generator writing horizontal deltas to `PointerOutput`.
- **Files to edit**:
  - `src/Tactile.Core/Model/Mapping.cs:18-43,140-181`: add `LayerMode` (`Hold`, `Toggle`), `TriggerStageMode` (`Off`, `Additive`, `Exclusive`, `HipFire`), `StickRingMode` (`Off`, `Outer`, `Inner`), `FlickSnapMode` (`Off`, `Four`, `Eight`), `MappingLayer`, `ButtonChord`, `TriggerMapping`, `TriggerMappingPair`, `FlickStickConfig`; add `Layers`, `ChordWindowMs`, `Chords`, `Triggers`, `FlickStick` to `MappingConfig`; add `UseCalibration` to `GyroMouseConfig`; add `RingMode`, `RingThreshold`, `RingHysteresis`, `RingAction` to `StickMapping`.
  - `src/Tactile.Core/Storage/MappingValidation.cs:11-84,172-276,421-440`: add `MaxLayers = 4`, `MaxLayerNameLength = 32`, `MaxChords = 32`; implement legacy `layerButton` + `layer` → `Layers[0]` migration and mirroring; normalize `Layers`, `ChordWindowMs`, `Chords`, `Triggers`, `StickMapping` ring fields, `GyroMouse.UseCalibration`, and `FlickStick`; update `StripRunActions` to strip and count `run` actions in all layers, chords, trigger stages, and stick rings without double-counting `Layer` and `Layers[0]`.
  - `src/Tactile.Core/Mapping/StickMapper.cs:15-75`: add `ActionDispatcher` dependency and ring state (`_ringActive`, `_ringHeld`); evaluate `RingMode.Outer` and `RingMode.Inner` with `RingThreshold` and `RingHysteresis` on processed radial magnitude $r = \sqrt{x^2 + y^2}$; release `_ringHeld` in `ReleaseAll()`; allow bypassing primary stick `Mode` when `flickStick.Enabled` is `true` on the right stick while still evaluating the right stick's ring action.
  - `src/Tactile.Core/Mapping/GyroMouse.cs:18-76`: accept `int countsPer360` (default `3600`); when `_config.UseCalibration` is `true`, scale degrees by `countsPer360 / 360.0` instead of `PixelsPerDegree`.
  - `src/Tactile.Core/Mapping/Mapper.cs:31-62,79-175,207-315`:
    - Manage `1..4` shift layers (`Hold` and `Toggle` states); on any active layer change, release held `ButtonMachine`s, chords, touchpad zone machines, and pending taps (`ReleaseTaps()`), and post `_host.PulseConfirm`.
    - Route non-activator buttons through `ChordMachine` when `Chords.Count > 0` (buttons not in any chord go straight to `_machines[b]`).
    - Skip `_machines[Buttons.L2]` / `_machines[Buttons.R2]` when `Triggers.Left.Mode != Off` / `Triggers.Right.Mode != Off` (unless `L2`/`R2` is a layer activator or chord member) and drive `TriggerMapper` from `s.L2` / `s.R2` and `UpdateTimers(now)`.
    - Drive `FlickStick` from `s.Right` and `UpdateTimers(now)` when `FlickStick.Enabled` is `true` (and for `StartTestTurn(int counts, int delayMs, TimeSpan now)` even when mapping is tested from UI).
    - Release trigger stages, stick rings, chord actions, flick stick, and toggled shift layers in `ReleaseAllLocked()`.
  - `src/Tactile.Core/Emulation/EmulationInput.cs:86-125`: update `MappedButtons(Profile p)` to include all layers, layer activators, chord buttons, and active two-stage trigger buttons (`L2`/`R2`).
  - `src/Tactile.Core/AppCore.cs:350-375`: expose `StartTestTurn(int counts, int delayMs)` forwarding to `_mapper.StartTestTurn(counts, delayMs, Now())`.
  - `src/Tactile.Host/Bridge.cs:80-83,264-273`: handle `mapping.testTurn` (`counts`, `delayMs`).
  - `tests/Tactile.Tests/LayerTests.cs:39-56`: update the two mid-press layer transition tests to verify the Phase 11 rule that changing layers mid-press immediately releases the held action from the old layer.
- **Prohibitions**: Do not edit any file under `ui/`. Do not run the host executable or touch real HID/Win32 injection in tests.

### Job B (`ui/src/`)

- **Files to edit**:
  - `ui/src/host.ts:111-151,429-458`: add `LayerMode`, `MappingLayer`, `ButtonChord`, `TriggerStageMode`, `TriggerMapping`, `StickRingMode`, `FlickSnapMode`, `FlickStickConfig`; extend `GyroMouse` (`useCalibration`), `StickMapping` (`ringMode`, `ringThreshold`, `ringHysteresis`, `ringAction`), `Mapping` (`layers`, `chordWindowMs`, `chords`, `triggers`, `flickStick`), and `UiMessage` (`{ type: "mapping.testTurn"; counts: number; delayMs?: number }`).
  - `ui/src/model.ts:61-94,285-330`: add `defaultMappingLayer`, `defaultTriggerMapping`, `defaultFlickStick`, updated `defaultStickMapping`, `defaultGyroMouse`, `defaultMapping`, and `normalizeMapping` implementing the exact legacy `layerButton` + `layer` → `layers[0]` migration and parameter bounds from Section 2.
  - `ui/src/mapping.ts:41-60,187-231`: update `desktopTemplate` and `hasBindings` to keep `layers[0]` and `layerButton`/`layer` synchronized; add localized option lists for layer modes, trigger stage modes, stick ring modes, and flick snap modes.
  - `ui/src/pages/Mapping.tsx:13-22,65-256`:
    - Update `ButtonsTab` layer selector to support up to `4` shift layers (`Base`, `Layer 1..4` with add/remove layer buttons, per-layer `name`, activator `button` picker, and `"hold" | "toggle"` mode selector), showing which lower layer a button inherits from when unbound in the selected layer, and keeping `layerButton`/`layer` synced with `layers[0]`.
    - Add **Chords** (`"chords"` / `"Akorlar"`) and **Triggers** (`"triggers"` / `"Tetikler"`) subtabs (or sections):
      - Chords editor: `chordWindowMs` slider (`20..300` ms) and list of up to `32` chords (`2-3` button pickers + `ActionEditor` + add/remove).
      - Trigger stages editor: Left (`L2`) and Right (`R2`) cards with `mode` (`off | additive | exclusive | hipFire`), `softThreshold`, `fullThreshold`, `hysteresis`, `hipFireMs` (when `hipFire`), live shaped trigger bar preview with threshold markers, and `ActionEditor`s for `soft` and `full`.
  - `ui/src/pages/MappingTabs.tsx:139-305`:
    - In `GyroMouseTab`: add `useCalibration` toggle (`"Use 360° calibration (1× = 1:1 turn)"` / `"360° kalibrasyonunu kullan (1× = 1:1 dönüş)"`) showing the shared `flickStick.countsPer360` value.
    - In `StickMappingTab`:
      - Add Outer / Inner Ring section to both Left and Right sticks (`ringMode`: `off | outer | inner`, `ringThreshold`, `ringHysteresis`, `ringAction` via `ActionEditor`, and visual ring circle on `StickPreview`).
      - Add **Flick Stick & 360° Calibration Wizard** section on the Right stick (`enabled`, `countsPer360` slider/input `500..30000` with `-100 / -10 / +10 / +100` buttons and `"Test 360° turn (2s)"` / `"360° dönüşü dene (2 sn)"` button sending `mapping.testTurn`, `threshold`, `flickTimeMs`, `snap` `off | four | eight`, `deadzoneAngle`, `smoothing`).
  - `ui/src/sim.ts:577-604,724-745`: update `profile.import` `run`-action stripping in simulator to cover `layers`, `chords`, `triggers`, and `sticks.*.ringAction`; handle `mapping.testTurn` by posting the localized info notice after `delayMs`.
- **Prohibitions**: Do not edit any file under `src/` or `tests/`. Every user-visible string must have both English and Turkish via `L(en, tr)` (`ui/src/i18n.ts:16-18`).

### Job C (after Job A & B, only if needed)

- Verify `Bridge.cs` ↔ `host.ts` message and schema compatibility and fix any mismatch discovered during `dotnet test` + `npm run build` + browser simulation check.

---

## 5. Acceptance Criteria and Test List

### Measurable acceptance criteria

1. `dotnet build` and `dotnet test` pass with **0 warnings and 0 errors**; all existing **659+ tests** pass (with `tests/Tactile.Tests/LayerTests.cs:39-56` updated for the Phase 11 mid-press layer-release rule), plus all new Phase 11 unit tests.
2. `npm run build` in `ui/` succeeds with **0 TypeScript or Vite errors**.
3. Legacy profile JSONs from Phases 2-10 with `"layerButton"` and `"layer"` (and no `"layers"` property) automatically migrate to `layers[0]` (`mode: "hold"`, `button: layerButton`, `buttons: layer`) while keeping `layerButton` and `layer` mirrored.
4. Any layer activation/deactivation (`hold` or `toggle`), pause transition (`ui`, `shortcut`, `fullscreen`), profile switch, or controller disconnect immediately releases all held keys, mouse buttons, chords, trigger stages, stick ring actions, macros, toggles, and turbo outputs.
5. `Validation.StripRunActions` removes `ActionKind.Run` from `layers`, `chords`, `triggers` (`soft`/`full`), and `sticks` (`ringAction`) on profile import and counts every removed entry in `removedRunActions`.

### Required unit tests (`tests/Tactile.Tests/`)

- `tests/Tactile.Tests/ChordTests.cs` *(new)*:
  - Non-chord buttons fire immediately on press with `0` ms delay even when other chords are configured.
  - `2`-button chord (`L1 + Cross`) pressed within `chordWindowMs` fires chord action, suppresses both individual bindings, and releases chord action as soon as either button is released without firing the remaining held button's single binding.
  - Pressing component buttons further apart than `chordWindowMs` (or releasing the first button before the second arrives) flushes the first button's normal single/double/hold binding.
  - `3`-button chord (`L1 + R1 + Cross`) wins over `2`-button subset chord (`L1 + R1`) when the 3rd button arrives within `chordWindowMs`; if the 3rd button does not arrive before `chordWindowMs`, `L1 + R1` fires on timeout.
- `tests/Tactile.Tests/LayerTests.cs`:
  - Legacy `layerButton` + `layer` profile migrates to `Layers[0]` and works identically.
  - Up to `4` stacked layers with `Hold` and `Toggle` modes: higher active layer overrides lower active layer; unbound button in Layer 3 falls back to active Layer 2 → Layer 1 → Base (`Buttons`).
  - DualSense Edge buttons (`fn1`, `fn2`, `paddleLeft`, `paddleRight`) work as layer activators.
  - Activating or deactivating any layer (`Hold` press/release or `Toggle` press) immediately releases any key held from the previous layer state and does not fire the new layer's binding until the physical button is pressed again.
  - `ReleaseAll()` resets toggled shift layers to inactive.
- `tests/Tactile.Tests/TriggerMappingTests.cs` *(new)*:
  - `Additive` mode holds `soft` at `softThreshold`, adds `full` at `fullThreshold`, and respects `hysteresis` on release.
  - `Exclusive` mode releases `soft` upon reaching `fullThreshold` and holds only `full`.
  - `HipFire` mode skips `soft` when `fullThreshold` is reached within `hipFireMs`, fires `soft` when held in the soft zone past `hipFireMs`, and taps `soft` on a quick soft pull-and-release shorter than `hipFireMs`.
  - Active trigger stage mode suppresses the regular digital `Buttons.L2`/`R2` single-button binding.
- `tests/Tactile.Tests/GyroStickMappingTests.cs`:
  - Stick `RingMode.Outer` and `RingMode.Inner` press and release `ringAction` at `ringThreshold` and `ringThreshold - ringHysteresis` independently of `StickMapping.Mode` (`Off`, `Keys`, `Mouse`).
  - `FlickStick`: initial flick to $+90^\circ$, $-90^\circ$, and $180^\circ$ emits exact `countsPer360 * (angle / 360)` horizontal mouse counts over `flickTimeMs` (including when the stick returns to center mid-flick); `deadzoneAngle` suppresses forward flicks within $\pm\text{deadzoneAngle}$; `snap` (`Four` and `Eight`) snaps flick angles; continuous rim sweep emits proportional $\Delta X$ with smoothing; works simultaneously with `GyroMouse` (including `gyroMouse.useCalibration: true` using `countsPer360 / 360.0` counts/deg) and `StartTestTurn`.
- `tests/Tactile.Tests/MappingValidationTests.cs` & `tests/Tactile.Tests/StoreTests.cs`:
  - Migration test: Phase 3-10 JSON with `layerButton` + `layer` and no `layers` normalizes `layers[0]` and round-trips cleanly.
  - Clamping and capping of `layers` (max `4`, duplicate activator buttons cleared), `chords` (max `32`, `2-3` distinct buttons, deduplication), `triggers` (`fullThreshold >= softThreshold + 0.05`), `sticks` ring bounds, and `flickStick` bounds.
  - `StripRunActions` removes and counts `run` actions in `layers`, `chords`, `triggers.left/right.soft/full`, and `sticks.left/right.ringAction`.
- `tests/Tactile.Tests/EmulationCore/EmulationInputTests.cs`:
  - `EmulationInput.MappedButtons` includes buttons from all `4` layers, layer activators, chords, and active two-stage triggers (`L2`/`R2`).
  - With `flickStick.enabled`, the virtual pad right stick is centered (plus any Phase 10 `gyroStick` offset); with flick off it is unchanged.

---

## 6. Safety Rules (Every Job)

- **No hardware or OS injection**: Agents must NEVER run code that sends output to a controller, injects keyboard/mouse input (`SendInput`), registers real global hotkeys during tests, attaches/hides devices, installs drivers, or touches the network. The application executable is never started by agents. All tests use fake `InputPlatform`, fake `IForegroundSource`, and `ManualTime`.
- **Allowed commands**: Only `dotnet build`, `dotnet test`, and `npm run build` (in `ui/`).
- **No commits**: Do not run `git commit`, `git push`, or modify git history.
- **No AI attribution**: No model, assistant, or tool names anywhere in code, comments, or documentation.
- **Stuck-key prevention**: Any layer transition (`hold` or `toggle`), pause transition (`ui`, `shortcut`, `fullscreen`), profile switch, or controller disconnect must release all held virtual keys, mouse buttons, chord actions, trigger stage actions, stick ring actions, macros, toggles, and turbo outputs, and cancel any pending test turn.

---

## 7. Hardware Test Checklist (KH — End of Phase, ~15 min)

1. **Legacy profile migration**: Open TriggerForge with existing Phase 10 profiles → verify existing `PS` layer bindings appear as "Layer 1 (hold PS)" in the Mapping tab and work without reconfiguration.
2. **Chords (2-button & 3-button priority)**: Bind `L1` = `1`, `R1` = `2`, chord `L1 + R1` = `3`, and chord `L1 + R1 + Cross` = `4` (`chordWindowMs: 60` ms) → open a text editor, press `L1` alone (`1`), press `L1 + R1` together (`3`, no `1` or `2`), and press `L1 + R1 + Cross` together (`4`, no `3`).
3. **Multi-shift layers (Hold & Toggle + Fallback)**: Configure Layer 1 (`Hold L1`, `Cross` = `A`, `Circle` = `B`) and Layer 2 (`Toggle Fn1` or `R1`, `Cross` = `C`, `Circle` unbound) → hold `Cross` in base, toggle Layer 2 on → verify base key releases immediately, pressing `Cross` outputs `C`, and pressing `Circle` while holding `L1` falls back to Layer 1 (`B`).
4. **Analog trigger soft / full pull**: Set Right Trigger (`R2`) to `hipFire` (`softThreshold: 0.25`, `fullThreshold: 0.90`, `soft` = Right Click, `full` = Left Click) → pull `R2` slowly to aim (Right Click holds) then pull fully; pull `R2` rapidly to 100% and verify only Left Click fires (soft pull skipped).
5. **Stick outer & inner ring**: Set Left Stick to `keys` (`WASD`) with `ringMode: "outer"`, `ringThreshold: 0.90`, `ringAction` = `LeftShift` → tilt stick halfway (walks with `W`), push to full rim (holds `LeftShift + W` to sprint).
6. **Flick Stick + 360° calibration wizard + Gyro Mouse**: Enable `FlickStick` on Right Stick and `GyroMouse` (`whileTouchpad` or `always`, `useCalibration: true`) → open an FPS game, run the 360° calibration wizard ("Test 360° turn (2s)") and adjust `countsPer360` until the camera lands on the exact starting landmark → pull Right Stick down for an instant 180° flick, rotate along the rim to sweep 360°, and tilt the controller for fine gyro aiming simultaneously.

---

## 8. Risks and Rollback

- **Risk — Legacy `layerButton` + `layer` profiles losing their shift layer or double-counting in `StripRunActions`**:
  - *Mitigation*: `Validation.NormalizeMapping` and `normalizeMapping` automatically migrate `layerButton` + `layer` into `layers[0]` when `layers` is empty and keep `layerButton`/`layer` mirrored to `layers[0]`. `StripRunActions` strips `layers` once and mirrors `Layer = strippedLayers[0].Buttons` so `removedRunActions` never double-counts Layer 1. Daily automatic backups from Phase 10 (`<DataDir>\backups\yyyy-MM-dd\`) also snapshot all profiles before the first save.
- **Risk — Chord window adding input latency to everyday single-button presses**:
  - *Mitigation*: `ChordMachine` computes the bitmask of buttons that appear in `mapping.chords`. Any button outside that bitmask bypasses `ChordMachine` completely (`0` ms latency).
- **Risk — "Layer trap" or stuck keys when switching layers while buttons are held**:
  - *Mitigation*: Layer activator buttons cannot be overridden by bindings inside any layer, and any change in the active layer bitmask immediately calls `ReleaseAll()` on all `ButtonMachine`s, active chords, and pending taps.
- **Risk — Double-firing L2/R2 when two-stage trigger mapping is enabled**:
  - *Mitigation*: When `mapping.triggers.left.mode != "off"` (or `right.mode != "off"`), `Mapper.ProcessButtons` skips `_machines[Buttons.L2]` (or `Buttons.R2`) so only `TriggerMapper` drives that trigger.
- **Rollback switches**:
  - Leaving `chords: []`, `layers` at 1 layer, `triggers.left.mode: "off"`, `triggers.right.mode: "off"`, `sticks.left.ringMode: "off"`, `sticks.right.ringMode: "off"`, `flickStick.enabled: false`, and `gyroMouse.useCalibration: false` disables every new Phase 11 runtime path and preserves Phase 10 behavior.
