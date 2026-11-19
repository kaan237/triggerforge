# Phase 6 contract: game integration (UDP API, DSX mods, Forza telemetry, rules)

Adds to `docs/contract.md` and the phase 3-5 contracts. Wire formats live in
`docs/protocol-integrations.md` (our own words); code is written from those notes, never from
other projects' code.

Goal: other programs (our own JSON format and existing DSX mods) can set triggers, lights and
rumble over localhost UDP, and Forza telemetry drives effects through rules the user edits
per profile.

## Work split (one after another, each reviewed before the next)

| Job | Content | Files |
|---|---|---|
| 1 | Integration settings, layer order, UDP API server (native + DSX), DSX port file, status, bridge | `src/Tactile.Core/Integration/**` (new), `src/Tactile.Core/AppCore.cs`, `src/Tactile.Core/Model/AppSettings.cs`, `src/Tactile.Core/Storage/Validation.cs`, `src/Tactile.Host/**`; tests in `tests/Tactile.Tests/Integration/**` |
| 2 | Forza parser and listener, telemetry signals, rule engine, profile field, Forza template, status, bridge | same folders; `src/Tactile.Core/Model/Profile.cs` |
| 3 | UI page **Entegrasyonlar** and `sim.ts` | `ui/**` only |

## Safety rules (every job)

Nothing is **run** that sends output to the controller, input to the PC, attaches or hides a
device. The app itself is not started. Tests use fakes and loopback sockets on port 0
(ephemeral), never the default ports, and never write the real DSX port file (the path is
injectable). Sockets bind to `127.0.0.1` only, never `0.0.0.0` / `::`. Only `dotnet build`,
`dotnet test`, `npm run build`. No new packages. No copied code. No commits. Warning-free.
Turkish user text. Code comments describe the code only.

## Shared rules

### Layer order (`AppCore.Build`)

Layers are applied in this order: `integration.udp`, then `integration.telemetry`, then all
other layers in the order first added (game feedback, Xbox rumble, speaker, mic, …), then
`preview.*`. So: profile < UDP API < telemetry rules < the game's own feedback < live preview
in the UI. Keys are constants in `Tactile.Core.Integration.IntegrationLayers`.

### Threads

Socket threads never touch the controller directly. They compute a layer function and call
`AppCore.SetOutputLayerNow(key, layer)` (already exists; internal). At most 100 layer updates
per second per source (newer state replaces an unsent older one). Any exception inside a
receive loop is caught and counted; the loop keeps running. Dispose closes sockets and waits
for the threads (≤ 1 s).

### Settings (`AppSettings.Integration`, new record `IntegrationSettings`)

```jsonc
"integration": {
  "udpEnabled": false,        // master switch for the UDP API
  "udpPort": 6969,            // 1024-65535
  "dsx": true,                // accept the DSX instruction format on the same socket
  "dsxPortFile": true,        // write the port to the DSX port file once the socket is bound
  "releaseAfterSeconds": 0,   // 0 = keep the UDP state until reset; 1-3600 = drop it after silence
  "forzaEnabled": false,
  "forzaPort": 5555           // 1024-65535, must differ from udpPort; UI warns for 5200-5300
}
```

Validation clamps ranges. If `forzaPort == udpPort`, the Forza listener stays off and its
status says `portConflict`. A settings change restarts only the socket whose fields changed.

## Job 1: UDP API

### Server (`Integration/UdpApiServer.cs`)

- Binds `127.0.0.1:udpPort` while `udpEnabled`. Bind failure (port in use) → status
  `portBusy`, one notice "UDP portu {port} kullanımda. DSX açıksa kapatın ya da portu
  değiştirin.", retry every 5 s while enabled; never throws to the caller.
- Datagrams over 16 KB, more than 32 instructions or commands, or more than 500 datagrams per
  second (the extra ones) are dropped and counted as `rejected`.
- Parse with `System.Text.Json` (`JsonDocument`, max depth 8). A packet is **native** if it has
  `"tactile": 1`, **DSX** if it has `"instructions"` and `dsx` is on; anything else is rejected.
- Every accepted command updates one state record (`UdpState`: optional left trigger, right
  trigger, lightbar, player LEDs, mic LED, rumble left/right) and the `integration.udp` layer
  overlays the fields that are set. Fields not set leave the profile's values.
- State is cleared (layer removed) by: a reset command, `udpEnabled` false, `releaseAfterSeconds`
  of silence when > 0, the bridge message `integration.release`, dispose.
- The answer goes back to the sender's endpoint (always loopback).

### Native format

```jsonc
{ "tactile": 1, "commands": [ { "cmd": "...", ... } ] }
```

| `cmd` | Fields | Effect |
|---|---|---|
| `trigger` | `side`: `left`/`right`, `config`: TriggerConfig as in profiles (or `null` = clear that side) | `Triggers.ToEffect`; invalid config → error, nothing changed |
| `lightbar` | `r`, `g`, `b` 0-255 | |
| `playerLeds` | `mask` 0-31 (bit 0 = leftmost) | |
| `micLed` | `mode`: `off`/`on`/`pulse` | |
| `rumble` | `left`, `right` 0-255 | |
| `profile` | `id` | `AppCore.ActivateProfile`; unknown id → error |
| `reset` | — | clear the UDP state |
| `status` | — | only the answer |

Answer: `{ "ok": true|false, "error": "<text or null>", "connected": bool, "link": "usb"|"bluetooth"|null, "battery": 0-100|null }`.
Commands run in order; the first error stops the rest and is reported.

### DSX format (`Integration/DsxTranslator.cs`, pure, testable)

Instruction and enum numbers: `docs/protocol-integrations.md` §1. The controller index is
ignored (one pad; phase 8 adds more). Parameters may be numbers, booleans (true = 1) or numeric
strings; anything else makes that instruction ignored. Out-of-range numbers are **clamped** to
the ranges our `TriggerEffect` builders accept (mods are sloppy), never rejected. Unknown
instruction types and trigger modes are ignored and counted (`unsupported`).

| DSX | Ours |
|---|---|
| Normal | `Off` |
| GameCube | `Feedback(8, 8)` |
| VerySoft / Soft / Medium / Hard / VeryHard / Hardest / Rigid | `Feedback(0, s)` with s = 1 / 2 / 4 / 5 / 6 / 7 / 8 |
| VibrateTrigger f | `Vibration(0, 8, f)` |
| Choppy | `Feedback(zones)` with zones alternating 0, 6, 0, 6, … starting at zone 0 |
| VibrateTriggerPulse | `Vibration(0, 4, 8)` |
| CustomTriggerValue m, v1-v7 | m 0 → `Off`; rigid family → raw bytes `[0x01, v1..v7, 0, 0, 0]`; pulse family → `[0x02, …]`; vibrate families → `[0x06, …]` (`TriggerEffect.FromBytes`); A/B variants treated as their plain mode |
| Resistance s, f | `Feedback(s, f)` |
| Bow s, e, f, snap | `Bow` |
| Galloping s, e, a, b, f | `Galloping` |
| SemiAutomaticGun s, e, f | `Weapon(s, max(e, s+1), f)` |
| AutomaticGun s, a, f | `Vibration(s, a, f)` |
| Machine s, e, a, b, f, p | `Machine` |
| RGBUpdate r, g, b [, brightness] | lightbar (brightness ignored) |
| PlayerLED b1-b5 | player LED mask |
| PlayerLEDNewRevision n | mask per the layout table; 5 = 0 |
| MicLED 0/1/2 | on / pulse / off |
| TriggerThreshold | accepted, no effect |
| ResetToUserSettings | reset |

DSX answer: `{"Status":"Tactile: komutlar alındı","TimeReceived":"<DateTime.Now as text>","isControllerConnected":bool,"BatteryLevel":int}` (battery 0 when unknown).

### DSX port file (`Integration/DsxPortFile.cs`)

When the socket binds with `dsx` and `dsxPortFile` on, write the port number (digits only, no
newline) to `C:\Temp\DualSenseX\DualSenseX_PortNumber.txt`, creating the folder. Atomic write
(temp file + replace). Failure → notice, never a crash. Never write it when the bind failed
(then DSX itself probably owns the port and the file). The path comes from a constructor
parameter so tests use a temp folder.

### Status and bridge

`AppCore.Integration` exposes `IntegrationStatus` (immutable record) with
`Udp { State: off|listening|portBusy, Port, Packets, Rejected, Unsupported, LastPacketAgoMs (null = none), Holding (layer active), Format (native|dsx|null of the last packet) }`
and (job 2) `Forza`. Bridge: host → UI message `integration` (status fields camelCase), sent on
`hello`, on `integration.status`, and every second with the emulation timer. UI → host:
`integration.status`, `integration.release`.

### Tests (`tests/Tactile.Tests/Integration/`)

DSX translator: every mode and instruction row above, clamping, bool and string parameters,
unknown type/mode ignored, a packet with several instructions. Native parser: each command,
invalid config error, unknown cmd error, order and stop-on-first-error. Server over loopback
(port 0): native packet → layer function sets the right fields and answer JSON; DSX packet →
DSX answer shape; garbage, empty, 20 KB, deep JSON, wrong types → no crash, `rejected` counts,
server still answers the next good packet; rate limit; port busy (bind a socket first) →
`portBusy` then `listening` after release and retry; reset and `releaseAfterSeconds` with a
fake clock. Port file: written after bind, not written when busy, temp path. Layer order in
`AppCore.Build`: preview beats game layer beats telemetry beats UDP beats profile. Settings JSON
round-trip and clamping.

## Job 2: Forza telemetry and rules

### Parser (`Integration/ForzaPacket.cs`, pure)

`static bool TryParse(ReadOnlySpan<byte> packet, out TelemetryFrame frame)`: sizes and offsets
per `docs/protocol-integrations.md` §2 (232, 311, 323, 324, 331; anything else → false).
NaN/infinite floats read as 0. `TelemetryFrame` holds the signals below, already normalized,
plus `Format` (`sled`|`fm7`|`fm`|`fh`).

### Signals (`TelemetrySignal` enum, JSON camelCase names)

| Signal | Value |
|---|---|
| `raceOn` | 0/1 |
| `speedKmh` | dash: Speed × 3.6; sled: velocity length × 3.6 |
| `rpm` | (current − idle) / (max − idle), clamped 0-1; 0 when max ≤ idle |
| `gas`, `brake`, `clutch`, `handbrake` | 0-1 (byte / 255); sled: 0 |
| `gear` | raw gear byte; sled: 0 |
| `steer` | −1..1 |
| `slipFront`, `slipRear` | max of the pair's \|combined slip\| |
| `surface` | max \|surface rumble\| of the four wheels |
| `rumbleStrip` | 1 if any wheel is on a rumble strip |
| `boost` | psi; sled: 0 |
| `abs` | 1 when brake > 0.1, slipFront > 1 and speedKmh > 5, else 0 |
| `traction` | 1 when gas > 0.1, slipRear > 1 and speedKmh > 5, else 0 |

### Listener (`Integration/TelemetryListener.cs`)

Binds `127.0.0.1:forzaPort` while `forzaEnabled` (same busy/retry/notice rules as job 1, own
notice text). Each valid packet → evaluate the active profile's rules → `integration.telemetry`
layer. Layer removed when `raceOn` is 0, after 1 s without a valid packet, when the active profile
has no rules, when disabled, on dispose. Status
`Forza { State: off|listening|receiving|portBusy|portConflict, Port, Format, Packets, Rejected, Values (last frame's signals, null when none) }`;
`receiving` = a valid packet in the last 1 s.

### Rules (`Profile.Telemetry`, new record `TelemetryConfig { Rules }`, max 32)

```jsonc
{
  "signal": "brake",            // TelemetrySignal
  "from": 0.0, "to": 1.0,        // input range; value < from → rule inactive
  "exponent": 1.0,               // 0.2-5; t = clamp((v - from) / (to - from), 0, 1) ^ exponent
  "target": "leftTrigger",       // leftTrigger | rightTrigger | lightbar | playerLeds | rumbleLeft | rumbleRight
  "trigger": { TriggerConfig },  // trigger targets: the effect template
  "param": "strength",           // trigger targets: template parameter driven by t (null = constant template)
  "min": 1, "max": 6,            // output range: trigger param, rumble 0-255, playerLeds 0-5 lit from the left
  "colorFrom": {"r":0,"g":255,"b":0}, "colorTo": {"r":255,"g":0,"b":0},   // lightbar: lerp by t
  "blinkAt": 0.95                // lightbar, optional: t ≥ blinkAt → 8 Hz on/off blink
}
```

- Evaluation (`Integration/TelemetryRules.cs`, pure): for each target, the **last active** rule in
  the list wins; inactive rules leave the target to earlier rules or the profile. Driven trigger
  params are rounded and clamped by the template's own validation (`Triggers.TryToEffect`; a
  failing rule is skipped). If `to <= from`, t = 1 when v ≥ from.
- Validation in `Validation.Normalize`: unknown enum → rule dropped; ranges clamped; more than 32
  → extra dropped; missing colours default to black.
- Template (`Integration/TelemetryTemplates.cs`): `Forza` rules, in order:
  1. leftTrigger: brake 0→1, feedback start 1, param strength 1→5.
  2. leftTrigger: abs from 0.5, vibration start 1 amplitude 6 frequency 35, constant.
  3. rightTrigger: rpm 0→1, feedback start 2, param strength 1→4.
  4. rightTrigger: traction from 0.5, vibration start 2 amplitude 5 frequency 40, constant.
  5. lightbar: rpm 0→1, green → red, blinkAt 0.95.
  6. playerLeds: rpm 0.4→1, 0→5.
  7. rumbleRight: rumbleStrip from 0.5, 0→120 (constant 120 when active).
- Bridge UI → host: `telemetry.applyTemplate` `{ profileId, template: "forza" }` replaces that
  profile's rules with the template and saves it (answer: the usual `profiles` message).
  `integration` status gains `forza`.

### Tests

Parser: build packets of each size with known values at the documented offsets; check every
signal, sled speed from velocity, NaN handling, wrong size → false. Rules: from/to/exponent
maths, last active wins, inactive fall-through, trigger param driving and invalid template
skipped, lightbar lerp and blink timing (fake clock), player LED bar, rumble. Listener over
loopback: valid packets → layer, `raceOn` 0 → layer removed, 1 s silence → removed (fake clock),
garbage → `rejected`, port conflict. Template applies and validates. Profile JSON round-trip.

## Job 3: UI

New page **Entegrasyonlar** in the side menu (after **Sanal kontrolcü**).

- **UDP API** card: switch "UDP API" with hint "Başka programlar ve DSX modları tetik, ışık ve
  titreşimi değiştirebilir. Yalnız bu bilgisayardan gelen komutlar kabul edilir."; port field;
  switch "DSX modlarıyla uyumlu"; switch "DSX port dosyasını yaz" with hint "Modlar portu
  C:\Temp\DualSenseX içindeki dosyadan okur."; select "Sessiz kalınca bırak" (Hiçbir zaman / 10 sn
  / 30 sn / 60 sn); status line (Kapalı / Dinliyor :port / Port kullanımda), packet count, last
  format, "Son komut x sn önce"; button "Efektleri bırak" (enabled while holding).
- **Forza** card: switch "Forza telemetrisi"; port field with hint "Oyunda Ayarlar > HUD ve
  Oynanış > Data Out: IP 127.0.0.1, port bu sayı. 5200-5300 arası kullanılmasın." (warning text
  when the value is in 5200-5300 or equals the UDP port); status (Kapalı / Bekliyor / Veri geliyor
  (format) / Port kullanımda / Port çakışıyor); live values when receiving: hız, devir %, gaz %,
  fren %, vites, ön/arka kayma.
- **Telemetri kuralları** card, for the active profile (shows its name, with a profile picker):
  rule list (signal, range, target, effect summary), add / remove / reorder (up/down), edit form
  per rule (signal select, from/to, exponent, target select, trigger template via the existing
  trigger editor component where possible, param select from the template's params, min/max,
  colours, blinkAt), button "Forza şablonunu uygula" (confirm: "Bu profilin kuralları
  değiştirilecek.") → `telemetry.applyTemplate`. Save via `profile.save`. One line: "Kurallarda
  sonraki kural kazanır. Oyun kendi tetik efektini gönderirse oyununki geçerli olur."
- Turkish labels for the signals: Yarış sürüyor, Hız (km/s), Devir, Gaz, Fren, Debriyaj, El
  freni, Vites, Direksiyon, Ön kayma, Arka kayma, Yol titreşimi, Kasis, Turbo, ABS, Patinaj.
- `sim.ts`: sends an `integration` message (UDP listening, Forza receiving with changing values)
  and handles `telemetry.applyTemplate`, `integration.release`, so the page can be checked in a
  browser.

## Hardware check (after all jobs, KH)

1. UDP: a small test sender (lead's scratchpad, not in repo) sends native and DSX packets →
   triggers/LED change; garbage packets → app keeps running.
2. A DSX mod (e.g. a Forza or GTA DSX mod KH picks) runs unchanged against Tactile.
3. Forza (KH's copy): Data Out to 127.0.0.1:5555 → brake stiffens L2, ABS vibrates, rev LEDs.
