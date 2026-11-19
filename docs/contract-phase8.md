# Phase 8 contract: several controllers, DualSense Edge

Adds to `docs/contract.md` and the phase 3-7 contracts. Version becomes **0.9.0**.

Goal: up to **4 controllers** at once, each with its own profile, light, player LEDs, triggers,
UDP commands and (with emulation on) its own virtual controller; DualSense Edge gets profile
shortcuts on its Fn buttons and is shown as an Edge in the UI.

Nobody can test this on hardware yet (one DualSense, no Edge). Everything new is covered by
tests with fakes, and **one controller must behave exactly as today**: every existing test
passes unchanged in meaning.

## Model

- **Primary controller** = slot 0. It keeps every feature it has today: mapping to keyboard and
  mouse, macros, audio haptics, speaker, game audio, Forza telemetry, gyro, idle sleep, the
  live view. It is the controller whose MAC equals `settings.primaryController`, else the first
  one found.
- **Secondary controllers** = slots 1-3, in the order they connected. Each has:
  - its profile: `settings.controllerDefaults[mac]` if set, else the primary's active profile;
  - from that profile: triggers, trigger response, light bar, player LEDs, mic LED, rumble
    strength, stick settings for its virtual pad, emulation mode;
  - UDP layers addressed to its index (below);
  - its own virtual controller when emulation is on (job 3), HID only (no audio).
  - Not for secondaries: keyboard/mouse mapping, macros, audio haptics, speaker, telemetry
    rules, idle sleep, gyro calibration. The UI says so.
- **Player LEDs:** with 2+ controllers connected and `settings.autoPlayerLeds` (default true),
  slot n shows the console pattern for player n+1, as bits of the 5 player LEDs (bit 0 = left
  outer … bit 4 = right outer): player 1 `0x04` (center), 2 `0x0A` (inner two), 3 `0x15`
  (center + outer two), 4 `0x1B` (all but center), unless the profile or a UDP command sets
  player LEDs. With one controller nothing changes.
- A controller that disconnects frees its slot; the others keep their slots (no renumbering
  while connected). A new controller takes the lowest free secondary slot. If the primary
  disconnects, no other controller is promoted automatically (its profile and virtual pad stay
  as they are); `controller.setPrimary` changes it. The UI shows "Primary controller not
  connected" with a button per connected controller to make it primary.

## DualSense Edge

- Detected by PID `0DF2` (already). Input bits FnL, FnR, left/right rear (L4/R4) are already
  parsed (`Buttons.Fn1/Fn2/PaddleLeft/PaddleRight`) and mappable.
- **Fn shortcuts** (`settings.edgeShortcuts`, default true; per controller, any slot):
  - FnL + R1: next profile (in `Profiles.All` order), FnL + L1: previous profile;
  - FnR + Triangle / Circle / Cross / Square: profile 1 / 2 / 3 / 4 in that order;
  - for the primary this activates the profile like the tray does (manual choice until the
    foreground app changes); for a secondary it sets that controller's profile for this session
    only (not saved).
  - A shortcut fires on the press of the second button while the Fn button is held; the
    buttons still reach mapping and the virtual pad (no suppression in this release).
  - A notice "Profile: <name>" / "Profil: <ad>" with the controller number.
- Not in this release (no public, verifiable protocol facts): on-controller profiles, trigger
  stop levels (mechanical, not reported), stick module data. `docs/protocol.md` gets a short
  "Edge: not supported" list with the reason.

## Work split

| Job | Content | Files | When |
|---|---|---|---|
| 1 | Controller slots, secondary pipeline, primary choice, auto player LEDs, Edge shortcuts, UDP index, settings, bridge, tests | `src/Tactile.Core/**` except `Emulation/**`, `src/Tactile.Device.DualSense/**`, `src/Tactile.Host/**`, `tests/**` except `tests/Tactile.Tests/Emulation*/**` | with job 2 |
| 2 | UI: controllers list, primary button, per-controller profile, Edge badge and buttons, settings, `sim.ts` with two controllers (one Edge) | `ui/src/**` | with job 1 |
| 3 | One virtual controller per slot | `src/Tactile.Core/Emulation/**`, `src/Tactile.Emulation/**`, `src/Tactile.Drivers/**`, `tests/Tactile.Tests/Emulation*/**`, plus the small hook in `AppCore` named below | after job 1 |

## Safety rules (every job)

Nothing is **run** that sends output to a controller, input to the PC, attaches or hides a
device, installs a driver, or touches the network. The app itself is not started. Tests use
fakes. Only `dotnet build`, `dotnet test`, `npm run build`. No new packages. No copied code.
No commits. Warning-free. English and Turkish for every user text. Code comments describe the
code only. No tool or assistant names anywhere.

## Job 1: core

### Settings (`AppSettings`)

```jsonc
"primaryController": null,   // MAC (upper case, colon separated) or null = first found
"autoPlayerLeds": true,
"edgeShortcuts": true,
"maxControllers": 4          // 1-4; 1 = exactly today's behaviour (only one controller opened)
```
`maxControllers: 1` is also the rollback switch: with it no secondary code runs at all (no
`SecondaryControllers` instance, no extra scans), which a test checks.
Validation clamps and normalizes the MAC like `controllerDefaults` keys.

### Controllers (`ControllerService`)

- Keeps today's public surface for the primary (`Controller`, `Output`, `SetOutput`,
  `Input`, `ConnectionChanged`, haptics, speaker) so existing callers keep working.
- New: `Slots` (read-only list of up to 4 `ControllerSlot { int Index; DualSenseController
  Controller; string? Mac; }`), events `SlotConnected(slot)`, `SlotDisconnected(index)`,
  `SlotInput(index, InputState)`, and `SendSecondary(index, OutputState)`.
- Scanning: every scan looks at all DualSense HID paths (skipping our virtual pads and paths
  already open), opens new ones up to `maxControllers`, and puts the primary-MAC controller in
  slot 0 when the primary slot is free. Opening one controller never blocks the others; a
  failing open is retried on the next scan. No extra threads per controller beyond what
  `DualSenseController` already uses.

### Secondary pipeline (`Core/Multi/SecondaryControllers.cs`)

- Per secondary slot: chosen profile, its own output layers (profile → `integration.udp:<n>`
  → game feedback from its virtual pad → nothing else), built with the same `OutputState`
  builders the primary uses, sent through `SendSecondary`. At most 100 sends per second per
  controller; unchanged state is not resent.
- Profile changes (saved, deleted, default changed) re-apply to every secondary.
- Input of a secondary goes only to: Edge shortcuts, the virtual pad of its slot (job 3), the
  `status.controllers` battery/link data. Never to mapping, macros or the foreground logic.

### UDP index

- DSX instructions: the first parameter (controller index) now selects the slot (0 = primary,
  1-3 = secondaries by slot). An index with no controller is ignored and counted as
  `unsupported`.
- Native format: optional `"controller": 0-3` (default 0) on each command and on `reset`.
- Answers: `connected`/`battery` refer to the addressed controller (native) or controller 0
  (DSX, unchanged).
- The UDP state record becomes one per slot; "release after silence" and `reset` work per slot
  (`reset` without `controller` resets all).

### Edge shortcuts (`Core/Multi/EdgeShortcuts.cs`)

Pure state machine per controller: `(previous buttons, current buttons) → action?`, tested
for every combination above, for holding (no repeat), and for non-Edge controllers (never
fires, even if bits are set).

### Bridge

- `status.controllers`: `[{ "index": 0, "mac": "…", "model": "DualSense" | "DualSenseEdge",
  "link": "Bluetooth" | "Usb", "battery": 0-100 | null, "charging": bool, "primary": true,
  "profileId": "…", "profileName": "…" }]` (sorted by index; sent with every status).
- UI → app: `controller.setPrimary { "mac": "…" }` (saves `primaryController`; takes effect
  at once if that controller is connected: slots are swapped, the old primary becomes a
  secondary), `controller.setProfile { "index": 1-3, "profileId": "…" }` (session only).
- Existing `status` fields for the primary stay as they are.

### Tests

Fakes for two to four controllers: slot assignment, reconnect keeps others' slots, primary MAC
wins slot 0, `maxControllers: 1` opens one, secondary profile choice, auto player LED patterns
(and profile/UDP override), DSX index routing, native `controller` field, Edge shortcuts,
`setPrimary` swap, secondary input never reaches the mapper.

## Job 2: UI

- **Overview**: a row of controller cards when 2+ are connected (number, model with an "Edge"
  badge, link, battery, profile name, "Primary" tag, "Make primary" / "Birincil yap" button on
  the others). One controller: the page looks as today, plus the Edge badge when it is one.
- **Live view**: on an Edge, show FnL, FnR, L4, R4 states.
- **Settings → Controllers / Kontrolcüler**: max controllers (1-4), auto player LEDs, Edge
  shortcuts (with a short list of the combinations), and the existing per-controller default
  profile list for every connected controller. A note that secondary controllers get profile,
  light, triggers and virtual controller but not mapping, macros, haptics or telemetry.
- **Per-controller session profile**: on a secondary card a profile picker (`controller.setProfile`).
- `host.ts` types, `sim.ts`: two controllers (primary DualSense on Bluetooth, secondary Edge on
  USB), a third can be toggled from the developer tools, `setPrimary` swaps them, Edge buttons
  move in the live view.

## Job 3: emulation per slot (after job 1)

- One emulation instance per slot sharing the driver layer and the USB/IP server. Facts checked
  before writing this: the server keeps a list of pads and answers imports by bus id
  (`UsbIpServer.cs`, `_pads.Find(p => p.BusId == busId)`); each pad needs its own bus id,
  serial and VHCI port. HidHide hides by device instance id, so one entry per physical
  controller; the allow list (our exe) is shared. Slot 0 keeps everything it has (audio,
  DualSense or Xbox mode, latency stats). Slots 1-3: Xbox 360 or DualSense mode from their
  profile, **HID only** (no audio interface), their own HidHide entry for their physical
  controller, their own recovery journal entries (`--restore` undoes all).
- Game feedback (rumble, DualSense output reports) from a slot's virtual pad goes back to that
  slot's controller through the secondary pipeline's "game feedback" layer.
- AppCore hook: job 1 exposes `SecondaryControllers` events; job 3 adds the per-slot emulation
  objects and wires them in one place in `AppCore` (`// Emulation per slot` block).
- `emulation.status` gets `slots: [{ index, mode, attached, hidden, error? }]`; the existing
  top-level fields stay for slot 0. UI (Virtual controller page) lists the slots (small UI change
  allowed in `ui/src/pages/Emulation.tsx` and `host.ts`/`sim.ts` for this field only).
- Tests: two slots attach two virtual pads with distinct serials and ports; a failure in slot
  1 does not touch slot 0; restore undoes every slot; slot 1 never creates an audio interface.

## Plan audit (2026-11-19)

First verdict REVİZYON GEREKLİ, fixed in this file: player LED patterns were wrong for player 3
and 4 (now the console bit patterns); no rollback switch (now `maxControllers: 1` runs no
secondary code); primary-disconnected state had no UI; the USB/IP multi-device and HidHide
facts for job 3 were unchecked (checked and written above). Edge USB output length was checked
and needs no change (`DualSenseController.Write` pads to the device's output report length).
Verdict after fixes: ONAYLANABİLİR.

## Acceptance

- Build warning-free, all tests pass (old and new), UI build clean.
- With one controller every existing behaviour and test is unchanged.
- Browser preview: two controllers in both languages, primary swap, Edge badge and buttons,
  settings section, emulation slots list.
- Hardware (later, KH): two DualSense at once each with its own profile and light; an Edge's
  Fn shortcuts; two virtual pads in a local multiplayer game.
