# Phase 13 contract: racing telemetry — EA SPORTS F1 UDP, ACC & AC shared memory, BeamNG OutGauge UDP, per-game templates

Adds to `docs/contract.md`, `docs/contract-phase6.md`, and the phase 7-12 contracts. Version becomes **1.4.0**.

Goal: expand the Phase 6 racing telemetry subsystem (`src/Tactile.Core/Integration/**`) from Forza-only to four additional racing telemetry sources — EA SPORTS F1 (`F1 24` / `F1 25` / `2026` UDP), Assetto Corsa Competizione (ACC Windows shared memory), Assetto Corsa (original AC Windows shared memory), and BeamNG.drive (OutGauge UDP) — feeding a unified, source-independent normalized signal record (`TelemetryValues`) and rule engine (`TelemetryRules`), with ready-made templates for each game and fake UDP test modes in `tools/integration-test/udptest.py`, while keeping all existing profiles, settings, and tests unbroken.

## 1. Scope

### In scope (6 implemented features)

1. **Unified normalized signal set & multi-source arbitration (`TelemetrySignal`, `TelemetryValues`, `TelemetrySource`)**:
   - Extends `TelemetrySignal` (`src/Tactile.Core/Integration/TelemetryRules.cs:7-11`) and `TelemetryValues` (`src/Tactile.Core/Integration/ForzaPacket.cs:7-32`) with two new normalized signals appended with default values so existing 16-argument positional callers remain source-compatible:
     - `RevLights` (`revLights`, `double` in `[0.0, 1.0]`, default `0`): shift/rev LED percentage (`m_revLightsPercent / 100.0` in F1; `DL_SHIFT` bit `0` or normalized `Rpm` in BeamNG; normalized `Rpm` in Forza, ACC, and AC).
     - `Drs` (`drs`, `int` `0` or `1`, default `0`): DRS active flag (`m_drs != 0` in F1 `CarTelemetryData`; `drs > 0.5f` at offset `200` in original AC; `0` in Forza, ACC, and BeamNG).
   - Preserves all 16 existing signals (`RaceOn`, `SpeedKmh`, `Rpm`, `Gas`, `Brake`, `Clutch`, `Handbrake`, `Gear`, `Steer`, `SlipFront`, `SlipRear`, `Surface`, `RumbleStrip`, `Boost`, `Abs`, `Traction`) and their normalization ranges in `Validation.NormalizeTelemetry` (`src/Tactile.Core/Storage/Validation.cs:133-141`).
   - **Source-independent rule evaluation**: `TelemetryRules.Evaluate` (`src/Tactile.Core/Integration/TelemetryRules.cs:73-121`) evaluates the active profile's `Profile.Telemetry.Rules` identically regardless of which game produced `TelemetryValues`.
   - **Multi-source arbitration ("last active frame wins")**:
     - Identifies the telemetry source via `public enum TelemetrySource { Forza, F1, Acc, Ac, BeamNg }` (serialized as `"forza" | "f1" | "acc" | "ac" | "beamNg"`).
     - Multiple sources may be enabled simultaneously in `IntegrationSettings`. Each enabled source maintains its own listener/reader state, packet counter, and latest `TelemetryValues`.
     - For driving the shared `IntegrationLayers.Telemetry` (`"integration.telemetry"`) output layer: whenever a source produces a valid frame with `Values.RaceOn == 1`, that source becomes the `ActiveSource` and its `TelemetryValues` drive the layer (**last received active frame wins**). A frame with `Values.RaceOn == 0` clears the layer if it comes from the current `ActiveSource` (or when no source has sent a `RaceOn == 1` frame within `PacketLifetime = 1 s`); an idle `RaceOn == 0` poll from a paused/background source never clobbers a different source that is actively streaming `RaceOn == 1` within the `1 s` window.
     - After `1 s` (`PacketLifetime`) without a valid `RaceOn == 1` frame from any enabled source, `ActiveSource` becomes `null` and the `IntegrationLayers.Telemetry` output layer is removed.
     - The Integrations UI status header/cards display which source is currently active (`status.activeSource`).

2. **EA SPORTS F1 UDP telemetry (`src/Tactile.Core/Integration/F1Telemetry.cs`)**:
   - Localhost-only UDP listener bound to `127.0.0.1` (`IPAddress.Loopback`), default port `20777` (`settings.integration.f1Port`, range `1024..65535`), disabled by default (`settings.integration.f1Enabled: false`). F1 sends UDP directly to `127.0.0.1:20777` on the same PC when in-game UDP Telemetry is enabled with Broadcast Mode off.
   - **Header & version validation (`PacketHeader`, 29 bytes, Little-Endian, packed)**:
     - Datagrams shorter than `29` bytes or from non-loopback endpoints are rejected (`Rejected++`).
     - Offset `0..1` (`uint16 LE`): `m_packetFormat`. Supported formats:
       - `2024` (F1 24, up to `22` cars).
       - `2025` (F1 25 standard format, up to `22` cars).
       - `2026` (F1 25 with 2026 Season Pack format, up to `24` cars).
     - Any other `m_packetFormat` value is counted as an unknown version (`Unsupported++`, not `Rejected`) and skipped without throwing.
     - Offset `5` (`uint8`): `m_packetVersion` (if `0`, `Rejected++`).
     - Offset `6` (`uint8`): `m_packetId` (`0..15`; if `> 15`, `Rejected++`).
     - Offset `7..14` (`uint64 LE`): `m_sessionUID` (when `m_sessionUID` changes, cached session values `maxRpm`, `idleRpm`, `boost`, `slipFront`, `slipRear`, and `gSurface` reset to `0`).
     - Offset `27` (`uint8`): `m_playerCarIndex`. Maximum car slots `maxCars = (m_packetFormat == 2026) ? 24 : 22`.
       - If `m_playerCarIndex == 255` (spectator / no player car), resets cached state, increments `Packets`, and emits a frame with `RaceOn = 0`.
       - If `m_playerCarIndex >= maxCars && m_playerCarIndex != 255`, rejects the packet (`Rejected++`).
   - **Packet size validation and stateful multi-packet merging (`F1TelemetrySession`)**:
     - **`m_packetId == 6` (`CarTelemetryData`)**:
       - Record size = `60` bytes/car + `3` trailing bytes. Total packet length must equal `29 + maxCars * 60 + 3` (**`1352` B** for `2024`/`2025`, **`1472` B** for `2026`); any other length increments `Rejected`.
       - Player car offset: `base = 29 + m_playerCarIndex * 60`.
       - Extracts:
         - `SpeedKmh`: `uint16 LE` `m_speed` at `base + 0`.
         - `Gas`: `Math.Clamp(FloatAt(packet, base + 2), 0, 1)` (`m_throttle`).
         - `Steer`: `Math.Clamp(FloatAt(packet, base + 6), -1, 1)` (`m_steer`).
         - `Brake`: `Math.Clamp(FloatAt(packet, base + 10), 0, 1)` (`m_brake`).
         - `Clutch`: `Math.Clamp(packet[base + 14] / 100.0, 0, 1)` (`m_clutch`, `0..100`).
         - `Gear`: `Math.Max(0, (int)unchecked((sbyte)packet[base + 15]))` (`m_gear`: `-1` = R, `0` = N, `1..8` = forward gears).
         - `m_engineRPM`: `uint16 LE` at `base + 16`.
         - `Drs`: `packet[base + 18] != 0 ? 1 : 0` (`m_drs`).
         - `RevLights`: `Math.Clamp(packet[base + 19] / 100.0, 0, 1)` (`m_revLightsPercent`, `0..100`).
         - `m_surfaceType[4]`: `uint8[4]` at `base + 56..59` (wheel order `RL, RR, FL, FR`). `RumbleStrip = (packet[base + 56] == 1 || packet[base + 57] == 1 || packet[base + 58] == 1 || packet[base + 59] == 1) ? 1 : 0` (surface code `1` = rumble strip). Off-track rough surfaces (`surfaceType > 1`) set a minimum `Surface` floor of `0.35`.
         - `Rpm`: if cached `maxRpm > idleRpm && maxRpm > 0` from `CarStatusData` (ID 7), `Math.Clamp((m_engineRPM - idleRpm) / (double)(maxRpm - idleRpm), 0, 1)`; else if `RevLights > 0`, `RevLights`; else if `m_engineRPM > 0`, `Math.Clamp((m_engineRPM - 3500.0) / 10000.0, 0, 1)`; else `0`.
         - `RaceOn`: `1` when `m_engineRPM > 0 || SpeedKmh > 0 || Gas > 0 || Brake > 0`, else `0`.
         - `Abs`: `Brake > 0.1 && _slipFront > 0.15 && SpeedKmh > 5 ? 1 : 0`.
         - `Traction`: `Gas > 0.1 && _slipRear > 0.15 && SpeedKmh > 5 ? 1 : 0`.
       - Emits an updated `TelemetryValues` frame on every valid ID 6 packet.
     - **`m_packetId == 7` (`CarStatusData`)**:
       - Record size = `55` bytes/car. Total packet length must equal `29 + maxCars * 55` (**`1239` B** for `2024`/`2025`, **`1349` B** for `2026`); any other length increments `Rejected`.
       - Player car offset: `base = 29 + m_playerCarIndex * 55`.
       - Caches `m_maxRPM` (`uint16 LE` at `base + 17`), `m_idleRPM` (`uint16 LE` at `base + 19`), and `Boost = Math.Clamp(FloatAt(packet, base + 37) / 4_000_000.0, 0, 1)` (`m_ersStoreEnergy` in `[0, 4e6]` J). Note: `m_tractionControl` (`base + 0`) and `m_antiLockBrakes` (`base + 1`) are menu assist settings, not live intervention flags, so live `Abs` and `Traction` are derived from wheel slip ratio.
     - **`m_packetId == 13` (`PacketMotionExData`)**:
       - Player-only packet. Total packet length must equal **`237` B** (`2024`/`2025`) or **`273` B** (`2026`); any other length increments `Rejected`.
       - Reads `m_wheelSlipRatio[4]` (`float[4]` at fixed packet offsets `93..108`) in F1 wheel order **`[0]=RL, [1]=RR, [2]=FL, [3]=FR`**:
         - `_slipRear = Math.Max(Math.Abs(FloatAt(packet, 93)), Math.Abs(FloatAt(packet, 97)))`.
         - `_slipFront = Math.Max(Math.Abs(FloatAt(packet, 101)), Math.Abs(FloatAt(packet, 105)))`.
     - **`m_packetId == 0` (`PacketMotionData`)**:
       - Total packet length must equal **`1349` B** (`2024`/`2025`, `60` B/car) or **`1325` B** (`2026`, `54` B/car); any other length increments `Rejected`.
       - For `2024`/`2025`, reads vertical G-force `m_gForceVertical` (`float` at `29 + m_playerCarIndex * 60 + 44`) and caches `_gSurface = Math.Clamp(Math.Abs(gVert) / 3.0, 0, 1)`.
     - **Other valid packet IDs (`1..5, 8..12, 14..15`)**:
       - Counted in `Packets++` without error and without altering the current `TelemetryValues` frame.

3. **ACC and AC Windows shared memory reader (`src/Tactile.Core/Integration/AccSharedMemory.cs`, `src/Tactile.Core/Integration/AcSharedMemory.cs`)**:
   - Enabled via `settings.integration.acEnabled` (`bool`, default `false`).
   - **Read-only Windows named memory-mapped files**:
     - Opens `Local\acpmf_physics`, `Local\acpmf_graphics`, and `Local\acpmf_static` strictly in read-only mode (`MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read)` and `CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read)`). Never creates or writes to shared memory.
   - **Silent wait & rate limiting (max 60 Hz)**:
     - Polled from `TelemetryListener` at most once every `16 ms` (`MinPollInterval = TimeSpan.FromMilliseconds(16)`, `<= 60 Hz`).
     - When the game is not running (`OpenExisting` throws `FileNotFoundException` or `IOException`), catches silently without notices or log spam, closes any partial handles, and backs off (`OpenRetryInterval = TimeSpan.FromMilliseconds(500)`) before attempting `OpenExisting` again.
   - **Freshness via `packetId` and handle release on freeze/exit**:
     - Reads `packetId` (`int32 LE` at `acpmf_physics` offset `0`) and `status` (`int32 LE` at `acpmf_graphics` offset `4`: `0 = AC_OFF`, `1 = AC_REPLAY`, `2 = AC_LIVE`, `3 = AC_PAUSE`).
     - Tracks `_lastPacketId` and `_lastPacketChangeTime`. `RaceOn` is `1` only while `status == 2` (`AC_LIVE`) and `now - _lastPacketChangeTime <= TimeSpan.FromMilliseconds(200)`.
     - If `packetId` does not change for `> 200 ms` (`StaleTimeout`) or `status != 2`, emits `RaceOn = 0` and immediately closes/disposes the open `MemoryMappedFile` and `MemoryMappedViewAccessor` handles so Windows can reclaim the shared memory object when the game exits.
   - **Common physics layout (`acpmf_physics` offsets `0..367`, shared by AC and ACC)**:
     - `0` (`int32`): `packetId`
     - `4` (`float`): `gas` (`0..1`) → `Gas`
     - `8` (`float`): `brake` (`0..1`) → `Brake`
     - `16` (`int32`): `gear` (`0` = R, `1` = N, `2` = 1st gear, …) → `Gear = Math.Max(0, gear - 1)`
     - `20` (`int32`): `rpms`
     - `24` (`float`): `steerAngle` → `Steer = Math.Clamp(steerAngle, -1, 1)`
     - `28` (`float`): `speedKmh` → `SpeedKmh = Math.Max(0, speedKmh)`
     - `44..55` (`float[3]`): `accG[3]` (X, Y/vertical, Z)
     - `56..71` (`float[4]`): `wheelSlip[4]` (wheel order `FL, FR, RL, RR`):
       - `SlipFront = Math.Max(Math.Abs(FloatAt(physics, 56)), Math.Abs(FloatAt(physics, 60)))`
       - `SlipRear = Math.Max(Math.Abs(FloatAt(physics, 64)), Math.Abs(FloatAt(physics, 68)))`
     - `184..199` (`float[4]`): `suspensionTravel[4]` (FL, FR, RL, RR)
     - `200` (`float`): `drs` (original AC only)
     - `204` (`float`): `tc`
     - `252` (`float`): `abs`
     - `276` (`float`): `turboBoost` → `Boost`
     - `364` (`float`): `clutch` (`0..1`) → `Clutch`
     - `acpmf_static` offset `452` (`int32`): `maxRpm`
   - **ACC-specific layout (`AccSharedMemory.cs`, `physics.Length >= 800`)**:
     - `588` (`int32`): `currentMaxRpm` (used as `maxRpm` when `> 0`, falling back to `acpmf_static:452`, then `8000`).
     - `672` (`int32`): `tcinAction` (`0` or `1`).
     - `676` (`int32`): `absInAction` (`0` or `1`).
     - `784` (`float`): `kerbVibration` (`0.0..1.0+`).
     - `788` (`float`): `slipVibrations` (`0.0..1.0+`).
     - `792` (`float`): `gVibrations` (`0.0..1.0+`).
     - `796` (`float`): `absVibrations` (`0.0..1.0+`).
     - Signal derivation in ACC:
       - `Rpm = Math.Clamp(rpms / (double)maxRpm, 0, 1)`, `RevLights = Rpm`, `Drs = 0`.
       - `Abs = (IntAt(physics, 676) != 0 || FloatAt(physics, 252) > 0 || FloatAt(physics, 796) > 0.05) ? 1 : 0`.
       - `Traction = (IntAt(physics, 672) != 0 || FloatAt(physics, 204) > 0) ? 1 : 0`.
       - `RumbleStrip = FloatAt(physics, 784) > 0.05 ? 1 : 0`.
       - `Surface = Math.Clamp(Math.Max(FloatAt(physics, 784), Math.Max(FloatAt(physics, 788), FloatAt(physics, 792))), 0, 1)`.
   - **Original AC layout (`AcSharedMemory.cs`, `physics.Length >= 368`, 712-byte `SPageFilePhysics`)**:
     - `maxRpm` from `acpmf_static:452` (fallback `8000`).
     - `Rpm = Math.Clamp(rpms / (double)maxRpm, 0, 1)`, `RevLights = Rpm`.
     - `Drs = physics.Length >= 204 && FloatAt(physics, 200) > 0.5 ? 1 : 0`.
     - `Abs = FloatAt(physics, 252) > 0 || (Brake > 0.1 && SlipFront > 1.0 && SpeedKmh > 5) ? 1 : 0`.
     - `Traction = FloatAt(physics, 204) > 0 || (Gas > 0.1 && SlipRear > 1.0 && SpeedKmh > 5) ? 1 : 0`.
     - `Surface`: derived from frame-to-frame `suspensionTravel[4]` (`184..199`) delta (`max(|travel[i] - prevTravel[i]|) * 100.0`) and vertical G oscillation (`Math.Abs(FloatAt(physics, 48) - 1.0) / 2.0`), clamped to `[0, 1]`.
     - `RumbleStrip = Surface >= 0.45 ? 1 : 0`.
   - **AC vs ACC detection**:
     - Selects ACC (`AcGame.Acc`) when `physics.Length >= 800` and either `acpmf_static` version string (`smVersion` at `0..29` or `acVersion` at `30..59`, UTF-16 LE) contains `"1.8"` / `"ACC"` or any ACC-only field (`588`, `672`, `676`, `780`, `784..796`) is non-zero; otherwise selects original AC (`AcGame.Ac`).

4. **BeamNG.drive OutGauge UDP (`src/Tactile.Core/Integration/BeamNgOutGauge.cs`)**:
   - Localhost-only UDP listener bound to `127.0.0.1` (`IPAddress.Loopback`), default port `4444` (`settings.integration.beamNgPort`, range `1024..65535`), disabled by default (`settings.integration.beamNgEnabled: false`).
   - **Packet validation (`92` or `96` bytes, Little-Endian)**:
     - Accepts `92`-byte standard OutGauge packets and `96`-byte OutGauge packets (with trailing `int32 id`); any other length returns `false` (`Rejected++`).
     - Extracts:
       - Offset `10` (`uint8`): `gear` (`0` = R, `1` = N, `2` = 1st gear, …) → `Gear = Math.Max(0, packet[10] - 1)`.
       - Offset `12..15` (`float`): `speed` in **m/s** → `SpeedKmh = Math.Max(0, FloatAt(packet, 12) * 3.6)`.
       - Offset `16..19` (`float`): `rpm` (engine RPM). Normalized against adaptive ceiling `_maxRpm = Math.Clamp(Math.Max(_maxRpm, rawRpm), 4000.0, 20000.0)` (default initial ceiling `7000.0`, reset to `7000.0` when `rawRpm == 0 && SpeedKmh == 0`): `Rpm = Math.Clamp(rawRpm / _maxRpm, 0, 1)`.
       - Offset `20..23` (`float`): `turbo` (BAR) → `Boost = FloatAt(packet, 20)`.
       - Offset `44..47` (`uint32 LE`): `showLights` dashboard light bitmask:
         - Bit `0` (`1u << 0`, `DL_SHIFT`): shift light → `RevLights = (showLights & 1u) != 0 ? 1.0 : Rpm`.
         - Bit `2` (`1u << 2`, `DL_HANDBRAKE`): handbrake → `Handbrake = (showLights & (1u << 2)) != 0 ? 1.0 : 0.0`.
         - Bit `4` (`1u << 4`, `DL_TC`): traction control active → `Traction = (showLights & (1u << 4)) != 0 ? 1 : 0`.
         - Bit `10` (`1u << 10`, `DL_ABS`): ABS active → `Abs = (showLights & (1u << 10)) != 0 ? 1 : 0`.
       - Offset `48..51` (`float`): `throttle` (`0..1`) → `Gas = Math.Clamp(FloatAt(packet, 48), 0, 1)`.
       - Offset `52..55` (`float`): `brake` (`0..1`) → `Brake = Math.Clamp(FloatAt(packet, 52), 0, 1)`.
       - Offset `56..59` (`float`): `clutch` (`0..1`) → `Clutch = Math.Clamp(FloatAt(packet, 56), 0, 1)`.
       - `SlipFront = Abs`, `SlipRear = Traction`, `RaceOn = (rawRpm > 50 || SpeedKmh > 0.5 || Gas > 0.01 || Brake > 0.01) ? 1 : 0`.

5. **Per-game telemetry templates (`src/Tactile.Core/Integration/TelemetryTemplates.cs`)**:
   - Follows the 7-rule Forza template pattern (`Brake → LeftTrigger Feedback`, `Abs → LeftTrigger Vibration`, `Rpm → RightTrigger Feedback`, `Traction → RightTrigger Vibration`, `Rpm/RevLights → Lightbar green-to-red + blink`, `Rpm/RevLights → PlayerLeds 0..5`, `RumbleStrip → RumbleRight`):
     - `TelemetryTemplates.Forza` (`"forza"`, existing 7 rules unchanged).
     - `TelemetryTemplates.F1` (`"f1"`, uses `RevLights` for lightbar with `BlinkAt = 0.90` and player LEDs `From = 0.2`, `Brake` strength `2..6`, `Abs` frequency `40`, `Traction` frequency `45`, `RumbleStrip` rumble `140`).
     - `TelemetryTemplates.Acc` (`"acc"`, `Brake` strength `2..6`, `Abs` frequency `38`, `Traction` frequency `42`, `Rpm` lightbar + player LEDs, `RumbleStrip` rumble `130`).
     - `TelemetryTemplates.Ac` (`"ac"`, `Brake` strength `1..5`, `Abs` frequency `35`, `Traction` frequency `40`, `Rpm` lightbar + player LEDs, `RumbleStrip` rumble `120`).
     - `TelemetryTemplates.BeamNg` (`"beamNg"`, `Brake` strength `1..5`, `Abs` frequency `32`, `Traction` frequency `36`, `RevLights` lightbar + `Rpm` player LEDs, `RumbleStrip` rumble `120`).
   - Exposed via `TelemetryTemplates.TryGet(string? template, out TelemetryConfig config)` (accepting `"forza"`, `"f1"`, `"acc"`, `"ac"`, `"beamNg"` / `"beamng"`).

6. **Fake telemetry sender (`tools/integration-test/udptest.py`) & unit-test shared memory**:
   - Adds `f1` and `beamng` modes and `--f1-port` (default `20777`) / `--beamng-port` (default `4444`) options to `tools/integration-test/udptest.py`, sending 60 Hz loopback packets over an 8-second accelerate/brake/ABS/kerb cycle and ending with a stop packet (`raceOn = 0`).
   - Shared memory (ACC and AC) is tested in unit tests via in-memory byte buffers (`FakeSharedMemorySource` / span parsers), never touching real OS game memory.

### Out of scope

- **EA WRC, DiRT Rally, iRacing, or BeamNG MotionSim (`BNG1`) UDP**: Excluded from Phase 13 to keep the minimum viable scope.
- **Non-loopback / LAN telemetry**: Listeners bind only to `127.0.0.1` and reject non-loopback packets.
- **Modifying `DsuServer.cs` or `src/Tactile.Emulation/**` or `src/Tactile.Core/Mapping/**`**: Phase 12 adds `DsuServer.cs` separately; Phase 13 creates its own files (`F1Telemetry.cs`, `AccSharedMemory.cs`, `AcSharedMemory.cs`, `BeamNgOutGauge.cs`) and keeps changes to shared files minimal.

---

## 2. Profile and Settings Schema

`Profile.CurrentVersion` (`src/Tactile.Core/Model/Profile.cs:26`) and `AppSettings.CurrentVersion` (`src/Tactile.Core/Model/AppSettings.cs:8`) stay `1`.

### Migration and backwards-compatibility rule

- No file version bump. Existing Phase 2-12 profile and settings JSON files deserialize cleanly to the defaults below via C# record initializers and `LenientEnumConverterFactory` (`src/Tactile.Core/Storage/TactileJson.cs:34-62`), then pass through `Validation.Normalize` (`src/Tactile.Core/Storage/Validation.cs:114-193,198-256`).
- In the UI, `normalizeProfile` and `normalizeSettings` (`ui/src/model.ts:190-194,249-260,513`) fill missing fields with identical defaults.
- **Test requirement**: Loading an existing Phase 2-12 profile JSON and settings JSON without any Phase 13 fields must produce identical normalized objects and runtime behavior as before.

### Profile additions (`Profile.Telemetry`)

```jsonc
{
  "telemetry": {
    "rules": [
      {
        "signal": "revLights",       // existing 16 signals | "revLights" | "drs"
        "from": 0.0,                 // 0..1 for revLights and drs
        "to": 1.0,
        "exponent": 1.0,
        "target": "lightbar",
        "trigger": null,
        "param": null,
        "min": 0,
        "max": 255,
        "colorFrom": { "r": 0, "g": 255, "b": 0 },
        "colorTo": { "r": 255, "g": 0, "b": 0 },
        "blinkAt": 0.9
      }
    ]
  }
}
```

### Settings additions (`AppSettings.Integration`)

```jsonc
{
  "integration": {
    "udpEnabled": false,
    "udpPort": 6969,
    "dsx": true,
    "dsxPortFile": true,
    "releaseAfterSeconds": 0,
    "forzaEnabled": false,
    "forzaPort": 5555,
    "f1Enabled": false,              // bool, default false (starts off)
    "f1Port": 20777,                 // int, default 20777, clamped to 1024..65535
    "acEnabled": false,              // bool, default false (starts off; covers both ACC and AC shared memory)
    "beamNgEnabled": false,          // bool, default false (starts off)
    "beamNgPort": 4444               // int, default 4444, clamped to 1024..65535
  }
}
```

### Validation & port-conflict rules for new fields

- `TelemetrySignal.RevLights` and `TelemetrySignal.Drs`: input range `(0, 1)` in `Validation.NormalizeTelemetry` (`src/Tactile.Core/Storage/Validation.cs:133-141`) and `signalRange` (`ui/src/pages/Integrations.tsx:148-158`).
- `IntegrationSettings.F1Port`: `Math.Clamp(integration.F1Port, 1024, 65535)` (default `20777`).
- `IntegrationSettings.BeamNgPort`: `Math.Clamp(integration.BeamNgPort, 1024, 65535)` (default `4444`).
- **Deterministic UDP port conflict priority (`UdpPort` > `ForzaPort` > `F1Port` > `BeamNgPort`)**:
  - `Forza` reports `ForzaListenerState.PortConflict` when `ForzaPort == UdpPort`.
  - `F1` reports `ForzaListenerState.PortConflict` when `F1Port == UdpPort || F1Port == ForzaPort`.
  - `BeamNg` reports `ForzaListenerState.PortConflict` when `BeamNgPort == UdpPort || BeamNgPort == ForzaPort || BeamNgPort == F1Port`.

---

## 3. Bridge (`src/Tactile.Host/Bridge.cs` ↔ `ui/src/host.ts`) Contract

Job A and Job C must implement these exact message shapes and property names:

### Host → UI messages

| Message `type` | Field / Shape | Description |
|---|---|---|
| `integration` | `{ type: "integration", udp: UdpStatus, forza: ForzaStatus, f1?: F1Status, ac?: AcStatus, beamNg?: BeamNgStatus, activeSource?: TelemetrySource \| null }` | Extends `PostIntegration` (`src/Tactile.Host/Bridge.cs:532-536`, `ui/src/host.ts:314`).<ul><li>`f1`: `{ state: "off" \| "listening" \| "receiving" \| "portBusy" \| "portConflict", port: number, format: number \| null, packets: number, rejected: number, unsupported: number, values: TelemetryValues \| null }`</li><li>`ac`: `{ state: "off" \| "listening" \| "receiving", game: "ac" \| "acc" \| null, packets: number, values: TelemetryValues \| null }`</li><li>`beamNg`: `{ state: "off" \| "listening" \| "receiving" \| "portBusy" \| "portConflict", port: number, packets: number, rejected: number, values: TelemetryValues \| null }`</li><li>`activeSource`: `"forza" \| "f1" \| "acc" \| "ac" \| "beamNg" \| null`</li></ul> |

### UI → Host messages

| Message `type` | Payload fields | Host behavior |
|---|---|---|
| `telemetry.applyTemplate` | `profileId: string, template: "forza" \| "f1" \| "acc" \| "ac" \| "beamNg"` | Extends `telemetry.applyTemplate` (`src/Tactile.Host/Bridge.cs:334-344`, `ui/src/host.ts:498`). Looks up the template via `TelemetryTemplates.TryGet(template, out var config)`, replaces `profile.Telemetry` on `profileId`, saves to `ProfileStore` (which broadcasts updated `profiles`), or throws `BadMessage("Telemetry template not found.", "Telemetri şablonu bulunamadı.")` if unknown. |

---

## 4. Work Split

Job A, Job B, and Job C have **strictly disjoint file sets**. All shared C# files (`TelemetryRules.cs`, `ForzaPacket.cs`, `IntegrationStatus.cs`, `TelemetryTemplates.cs`, `TelemetryListener.cs`, `AppSettings.cs`, `Validation.cs`, `AppCore.cs`, `Bridge.cs`) belong exclusively to **Job A**. **Job B** touches only `AccSharedMemory.cs`, `AcSharedMemory.cs`, and `AcSharedMemoryTests.cs`. **Job C** touches only `ui/src/**`.

| Job | Scope | Allowed files | Forbidden files | When |
|---|---|---|---|---|
| **B** | ACC & AC shared memory parsers, read-only MMF poller (60 Hz max, `packetId` freshness, silent wait when game closed), shared memory unit tests with fake memory blocks | `src/Tactile.Core/Integration/AccSharedMemory.cs`, `src/Tactile.Core/Integration/AcSharedMemory.cs`, `tests/Tactile.Tests/Integration/AcSharedMemoryTests.cs` | All other files in `src/**`, `tests/**`, `ui/**`, `tools/**`, `docs/**` | First (or parallel with Job C; uses existing 16-arg `TelemetryValues` positional constructor and exposes `AcTelemetryFrame(bool IsAcc, TelemetryValues Values, double RevLights = 0, int Drs = 0)`) |
| **A** | F1 UDP & BeamNG OutGauge UDP parsers, multi-source `TelemetryListener` wiring (Forza + F1 + ACC/AC + BeamNG), `RevLights` & `Drs` signals, templates, `AppSettings`, `Validation`, `Bridge`, `udptest.py` (`f1` & `beamng`), C# unit tests | `src/Tactile.Core/Integration/F1Telemetry.cs`, `src/Tactile.Core/Integration/BeamNgOutGauge.cs`, `src/Tactile.Core/Integration/ForzaPacket.cs`, `src/Tactile.Core/Integration/TelemetryRules.cs`, `src/Tactile.Core/Integration/IntegrationStatus.cs`, `src/Tactile.Core/Integration/TelemetryTemplates.cs`, `src/Tactile.Core/Integration/TelemetryListener.cs`, `src/Tactile.Core/Model/AppSettings.cs`, `src/Tactile.Core/Storage/Validation.cs`, `src/Tactile.Core/AppCore.cs`, `src/Tactile.Host/Bridge.cs`, `tools/integration-test/udptest.py`, `tests/Tactile.Tests/Integration/F1TelemetryTests.cs`, `tests/Tactile.Tests/Integration/BeamNgOutGaugeTests.cs`, `tests/Tactile.Tests/Integration/TelemetryListenerTests.cs`, `tests/Tactile.Tests/Integration/TelemetryRulesTests.cs`, `tests/Tactile.Tests/Integration/IntegrationCoreTests.cs` | `src/Tactile.Core/Integration/AccSharedMemory.cs`, `src/Tactile.Core/Integration/AcSharedMemory.cs`, `tests/Tactile.Tests/Integration/AcSharedMemoryTests.cs`, `src/Tactile.Core/Mapping/**`, `src/Tactile.Emulation/**`, `ui/**`, `docs/**` | After Job B (consumes `AccSharedMemory` & `AcSharedMemory`; needs them to compile), in parallel with Job C |
| **C** | React/TypeScript UI (F1, ACC/AC, BeamNG connection cards, active source badge, live telemetry values, template picker for all 5 games), EN/TR strings, `sim.ts` | `ui/src/**` | `src/**`, `tests/**`, `tools/**`, `docs/**` | Parallel with Jobs A & B |

### Job B (`AccSharedMemory.cs`, `AcSharedMemory.cs`, `AcSharedMemoryTests.cs`)

- **Files to create**:
  - `src/Tactile.Core/Integration/AcSharedMemory.cs`:
    - `public enum AcGame { Ac, Acc }`
    - `internal readonly record struct AcTelemetryFrame(bool IsAcc, TelemetryValues Values, double RevLights = 0, int Drs = 0);`
    - `internal interface ISharedMemorySource : IDisposable { bool TryRead(Span<byte> physics, out int physicsLength, Span<byte> graphics, out int graphicsLength, Span<byte> statics, out int staticsLength); void Close(); }`
    - `AcSharedMemory` class:
      - Constants `PhysicsMapName = @"Local\acpmf_physics"`, `GraphicsMapName = @"Local\acpmf_graphics"`, `StaticMapName = @"Local\acpmf_static"`, `MinPhysicsSize = 368`, `AcPhysicsSize = 712`, `MinGraphicsSize = 8`, `MinStaticSize = 456`, `AcStatusLive = 2`.
      - Pure span parser `public static bool TryParse(ReadOnlySpan<byte> physics, ReadOnlySpan<byte> graphics, ReadOnlySpan<byte> statics, ref AcSuspensionState state, out int packetId, out int status, out AcTelemetryFrame frame)` parsing original AC telemetry (`packetId`, `gas`, `brake`, `clutch`, `gear`, `rpms`, `maxRpm` from `statics[452]`, `steerAngle`, `speedKmh`, `wheelSlip[4]`, `tc` at `204`, `abs` at `252`, `turboBoost` at `276`, `drs` at `200`, and `Surface`/`RumbleStrip` from `suspensionTravel[4]` at `184..199` delta and vertical G at `48`). Sanitizes `NaN`/`Infinity` floats to `0`.
  - `src/Tactile.Core/Integration/AccSharedMemory.cs`:
    - `AccSharedMemory` class:
      - Constant `AccMinPhysicsSize = 800`, `MinPollInterval = TimeSpan.FromMilliseconds(16)` (60 Hz cap), `StaleTimeout = TimeSpan.FromMilliseconds(200)`, `OpenRetryInterval = TimeSpan.FromMilliseconds(500)`.
      - Pure span parser `public static bool TryParse(ReadOnlySpan<byte> physics, ReadOnlySpan<byte> graphics, ReadOnlySpan<byte> statics, out int packetId, out int status, out AcTelemetryFrame frame)` parsing ACC telemetry (including `currentMaxRpm` at `588` with fallback to `statics[452]`, `tcinAction` at `672`, `absInAction` at `676`, `kerbVibration` at `784`, `slipVibrations` at `788`, `gVibrations` at `792`, `absVibrations` at `796`).
      - Auto-detector `public static bool IsAccBuffer(ReadOnlySpan<byte> physics, ReadOnlySpan<byte> statics)` returning `true` when `physics.Length >= AccMinPhysicsSize` and either `statics` UTF-16 version string (`0..59`) indicates ACC/`1.8` or any ACC-only offset (`588`, `672`, `676`, `780`, `784..796`) is non-zero.
      - Reader/poller `internal sealed class AccSharedMemoryReader(Func<TimeSpan> now, ISharedMemorySource? source = null) : IDisposable`:
        - Default `WindowsSharedMemorySource` implementation opening `Local\acpmf_physics`, `Local\acpmf_graphics`, and `Local\acpmf_static` with `MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read)` and `CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read)`.
        - `public bool TryPoll(out AcTelemetryFrame frame, out bool changed)`: enforces `>= 16 ms` between polls; returns `false` silently when the game is closed (retrying `OpenExisting` at most every `500 ms`); tracks `packetId` changes (`changed = true` when a new `packetId` arrives); sets `RaceOn = 1` only when `status == 2` (`AC_LIVE`) and `now() - _lastPacketChange <= StaleTimeout (200 ms)`; when `packetId` is stale for `> 200 ms` or `status != 2`, emits `RaceOn = 0` and calls `_source.Close()` so MMF handles are not leaked after game exit.
  - `tests/Tactile.Tests/Integration/AcSharedMemoryTests.cs`:
    - Unit tests using an in-memory `FakeSharedMemorySource` (no real OS named MMFs opened).
- **Prohibitions**: Do not edit any file outside the three files listed above. Never open or write to real OS shared memory in tests.

### Job A (F1 + BeamNG UDP + Shared Integration/Host Files + Tests + `udptest.py`)

- **Files to create**:
  - `src/Tactile.Core/Integration/F1Telemetry.cs`:
    - `internal enum F1PacketResult { Ignored, Updated, Rejected, Unsupported }`
    - `internal sealed class F1TelemetrySession`: stateful per-session parser/merger handling `PacketHeader` (`29` B), `m_packetFormat` (`2024`, `2025`, `2026`), `m_sessionUID` cache reset, `m_playerCarIndex` bounds (`0..21` or `0..23`, `255` = spectator/off), `CarTelemetryData` (`ID 6`, `1352` / `1472` B), `CarStatusData` (`ID 7`, `1239` / `1349` B), `PacketMotionExData` (`ID 13`, `237` / `273` B, wheel order `RL, RR, FL, FR` at offsets `93..108`), and `PacketMotionData` (`ID 0`, `1349` / `1325` B).
  - `src/Tactile.Core/Integration/BeamNgOutGauge.cs`:
    - `internal sealed class BeamNgOutGauge`: parser for `92`-byte and `96`-byte Little-Endian OutGauge packets (`gear` at `10`, `speed` m/s at `12`, `rpm` at `16` with adaptive ceiling, `turbo` at `20`, `showLights` at `44` with `DL_SHIFT`/`DL_HANDBRAKE`/`DL_TC`/`DL_ABS`, `throttle` at `48`, `brake` at `52`, `clutch` at `56`).
  - `tests/Tactile.Tests/Integration/F1TelemetryTests.cs`: unit tests for F1 packet formats `2024`, `2025`, and `2026`, multi-packet merging (`ID 6 + ID 7 + ID 13`), wheel slip order (`RL, RR, FL, FR`), `RevLights` and `Drs`, session UID reset, `playerCarIndex == 255`, unknown format counting (`Unsupported`), and malformed packet rejection (`Rejected`).
  - `tests/Tactile.Tests/Integration/BeamNgOutGaugeTests.cs`: unit tests for `92`-byte and `96`-byte OutGauge packets, `m/s` to `km/h`, gear normalization (`0=R, 1=N, 2=1st`), `showLights` flags (`DL_SHIFT`, `DL_HANDBRAKE`, `DL_TC`, `DL_ABS`), adaptive `maxRpm`, stop packet (`RaceOn = 0`), non-finite floats, and wrong packet lengths.
- **Files to edit**:
  - `src/Tactile.Core/Integration/TelemetryRules.cs:7-11`: append `RevLights, Drs` to `TelemetrySignal`.
  - `src/Tactile.Core/Integration/ForzaPacket.cs:7-32,56-96`: add optional trailing parameters `double RevLights = 0, int Drs = 0` to `TelemetryValues`, handle `TelemetrySignal.RevLights` and `TelemetrySignal.Drs` in `TelemetryValues.Get`, and pass `RevLights: rpm, Drs: 0` in `ForzaPacket.TryParse`.
  - `src/Tactile.Core/Integration/IntegrationStatus.cs:5-18`: add `TelemetrySource`, `F1Status`, `AcStatus`, `BeamNgStatus`, and optional parameters `(F1Status? F1 = null, AcStatus? Ac = null, BeamNgStatus? BeamNg = null, TelemetrySource? ActiveSource = null)` on `IntegrationStatus`.
  - `src/Tactile.Core/Integration/TelemetryTemplates.cs:3-42`: add `F1`, `Acc`, `Ac`, `BeamNg` templates and `TryGet(string? name, out TelemetryConfig config)`.
  - `src/Tactile.Core/Integration/TelemetryListener.cs:7-309`:
    - Manage the three UDP telemetry sockets (`Forza`, `F1`, `BeamNg`) on `IPAddress.Loopback` and the shared memory poller (`AccSharedMemoryReader`, accepting an optional `ISharedMemorySource?` in the constructor for unit tests).
    - Convert `AcTelemetryFrame` to `TelemetryValues` via `frame.Values with { RevLights = frame.RevLights, Drs = frame.Drs }`.
    - Implement port conflict detection (`UdpPort` > `ForzaPort` > `F1Port` > `BeamNgPort`), 5-second `PortBusy` retry with localized one-time notice per port, and multi-source arbitration ("last active frame wins") driving `IntegrationLayers.Telemetry`.
    - Expose `Status` (`ForzaStatus`), `F1Status`, `AcStatus`, `BeamNgStatus`, and `ActiveSource`.
  - `src/Tactile.Core/Model/AppSettings.cs:44-53`: add `F1Enabled`, `F1Port = 20777`, `AcEnabled`, `BeamNgEnabled`, `BeamNgPort = 4444` to `IntegrationSettings`.
  - `src/Tactile.Core/Storage/Validation.cs:240-245`: clamp `F1Port` and `BeamNgPort` to `1024..65535`.
  - `src/Tactile.Core/AppCore.cs:233,914-926`: include `_telemetry.F1Status`, `_telemetry.AcStatus`, `_telemetry.BeamNgStatus`, and `_telemetry.ActiveSource` in `AppCore.Integration`; release any telemetry UDP socket colliding with `settings.UdpPort` when `UdpEnabled` is true in `ConfigureIntegration()`.
  - `src/Tactile.Host/Bridge.cs:334-344,532-536`: accept `"forza" | "f1" | "acc" | "ac" | "beamNg"` in `telemetry.applyTemplate` via `TelemetryTemplates.TryGet`; include `status.F1`, `status.Ac`, `status.BeamNg`, and `status.ActiveSource` in `PostIntegration()`.
  - `tools/integration-test/udptest.py:1-98`: add `--f1-port` (default `20777`), `--beamng-port` (default `4444`), and `f1` and `beamng` sender modes.
  - `tests/Tactile.Tests/Integration/TelemetryListenerTests.cs`, `tests/Tactile.Tests/Integration/TelemetryRulesTests.cs`, `tests/Tactile.Tests/Integration/IntegrationCoreTests.cs`: add tests for multi-source arbitration, F1/BeamNG/AC listener integration, port conflict matrix, `RevLights`/`Drs` rules, and the 4 new templates.
- **Prohibitions**: Do not edit `AccSharedMemory.cs`, `AcSharedMemory.cs`, `AcSharedMemoryTests.cs`, `src/Tactile.Core/Mapping/**`, `src/Tactile.Emulation/**`, or `ui/**`.

### Job C (`ui/src/**`)

- **Files to edit**:
  - `ui/src/host.ts:264-280,306-315,498`:
    - Extend `IntegrationSettings` with `f1Enabled: boolean`, `f1Port: number`, `acEnabled: boolean`, `beamNgEnabled: boolean`, `beamNgPort: number`.
    - Extend `TelemetrySignal` with `"revLights" | "drs"`.
    - Add `TelemetrySource = "forza" | "f1" | "acc" | "ac" | "beamNg"`, `TelemetryTemplate = "forza" | "f1" | "acc" | "ac" | "beamNg"`, `F1Status`, `AcStatus`, `BeamNgStatus`, and extend `IntegrationStatus` with `f1?: F1Status`, `ac?: AcStatus`, `beamNg?: BeamNgStatus`, `activeSource?: TelemetrySource | null`.
    - Update `UiMessage` `telemetry.applyTemplate` `template` type to `TelemetryTemplate`.
  - `ui/src/model.ts:36-47,190-194,513`:
    - Add defaults `f1Enabled: false, f1Port: 20777, acEnabled: false, beamNgEnabled: false, beamNgPort: 4444` to `defaultSettings().integration`.
    - Normalize and clamp `f1Port` and `beamNgPort` (`1024..65535`) in `normalizeSettings`.
    - Add `revLights: 0, drs: 0` where `TelemetryValues` defaults are constructed.
  - `ui/src/pages/Integrations.tsx:12-32,61-127,263-348`:
    - Add `revLights` (`L("Rev LEDs", "Devir LED'i")`) and `drs` (`"DRS"`) to `signals`.
    - In `Connections`, display the active telemetry source badge when `status?.activeSource` is non-null (`L("Active source", "Etkin kaynak")`: `"Forza"`, `"EA SPORTS F1"`, `"Assetto Corsa Competizione"`, `"Assetto Corsa"`, `"BeamNG.drive"`), and add cards/sections for:
      - **EA SPORTS F1**: toggle `f1Enabled` (`L("F1 UDP telemetry", "F1 UDP telemetrisi")`), `PortField` for `f1Port` (default `20777`), hint `L("In F1 Settings > Telemetry Settings, enable UDP Telemetry on port 20777 and keep UDP Broadcast Mode off.", "Oyunda Ayarlar > Telemetri Ayarları: UDP Telemetrisi açık, port 20777, UDP Broadcast kapalı olsun.")`, port conflict warning if `f1Port === s.udpPort || f1Port === s.forzaPort`, status line (`Off` / `Waiting` / `Receiving data (2024/2025/2026)` / `Port in use` / `Port conflict`), and live values when receiving.
      - **Assetto Corsa / ACC**: toggle `acEnabled` (`L("Assetto Corsa / ACC shared memory", "Assetto Corsa / ACC paylaşımlı bellek")`), hint `L("Reads telemetry automatically from Assetto Corsa and Assetto Corsa Competizione while driving. No in-game port setup needed.", "Assetto Corsa ve Assetto Corsa Competizione sürüş sırasında otomatik okunur; oyun içi port ayarı gerekmez.")`, status line (`Off` / `Waiting` / `Receiving data (ACC)` or `(AC)`), and live values when receiving.
      - **BeamNG.drive**: toggle `beamNgEnabled` (`L("BeamNG.drive OutGauge", "BeamNG.drive OutGauge")`), `PortField` for `beamNgPort` (default `4444`), hint `L("In BeamNG Options > Other > OutGauge support, set IP to 127.0.0.1 and port to 4444.", "BeamNG Ayarlar > Diğer > OutGauge desteği: IP 127.0.0.1, port 4444.")`, port conflict warning if `beamNgPort` matches `udpPort`, `forzaPort`, or `f1Port`, status line, and live values when receiving.
    - In `Rules`, replace the single `"Apply Forza template"` button with a template selector (`"forza" | "f1" | "acc" | "ac" | "beamNg"`) and `"Apply template"` / `"Şablonu uygula"` button (with the confirmation prompt `"This will replace the profile's rules."` / `"Bu profilin kuralları değiştirilecek."`).
  - `ui/src/sim.ts:77-115,331-348,645-652`:
    - Populate `revLights` and `drs` in `sampleTelemetry`.
    - Simulate `f1`, `ac`, `beamNg`, and `activeSource` in `sendIntegration()` (including port conflict checks between `udpPort`, `forzaPort`, `f1Port`, and `beamNgPort`).
    - Handle all 5 template names (`"forza"`, `"f1"`, `"acc"`, `"ac"`, `"beamNg"`) in `case "telemetry.applyTemplate"`.
- **Prohibitions**: Do not edit any file under `src/`, `tests/`, or `tools/`. Every user-visible string must have both English and Turkish via `L(en, tr)` (`ui/src/i18n.ts:16-18`).

---

## 5. Acceptance Criteria and Test List

### Measurable acceptance criteria

1. `dotnet build` and `dotnet test` pass with **0 warnings and 0 errors**; all existing tests pass without changing their assertions, plus all new Phase 13 unit tests.
2. `npm run build` in `ui/` succeeds with **0 TypeScript or Vite errors**.
3. Legacy profile and settings JSONs from Phases 2-12 deserialize with `version: 1`, `f1Enabled: false`, `f1Port: 20777`, `acEnabled: false`, `beamNgEnabled: false`, and `beamNgPort: 4444`.
4. `F1TelemetrySession` parses `2024` (22 cars), `2025` (22 cars), and `2026` (24 cars) packets, merges `ID 6` (`CarTelemetry`), `ID 7` (`CarStatus`), `ID 13` (`MotionEx` with `RL, RR, FL, FR` wheel order), and `ID 0` (`Motion`), increments `Unsupported` and skips unknown `m_packetFormat` values, and increments `Rejected` on truncated/oversized packets or invalid `m_playerCarIndex`.
5. `AccSharedMemory` and `AcSharedMemory` open Windows shared memory strictly with `MemoryMappedFileRights.Read` / `MemoryMappedFileAccess.Read`, never poll faster than `60 Hz` (`16 ms`), wait silently when the game is closed, detect staleness when `packetId` stops advancing for `> 200 ms` or `status != 2`, close handles on staleness, and accurately parse both ACC (`>= 800` B with `kerbVibration`, `absInAction`, `tcinAction`) and original AC (`712` B with `drs`, `abs`, `tc`, suspension travel kerb detection).
6. `BeamNgOutGauge` parses `92`-byte and `96`-byte packets, converts `speed` from `m/s` to `km/h`, maps `gear` (`0=R, 1=N, 2=1st`), extracts `DL_SHIFT`, `DL_HANDBRAKE`, `DL_TC`, and `DL_ABS` from `showLights`, and normalizes `rpm`.
7. When two telemetry sources are enabled simultaneously, the most recently received active frame (`RaceOn == 1`) drives `IntegrationLayers.Telemetry` and updates `IntegrationStatus.ActiveSource`.
8. `TelemetryTemplates.TryGet` provides valid 7-rule templates for `"forza"`, `"f1"`, `"acc"`, `"ac"`, and `"beamNg"` that pass `Validation.Normalize` with zero issues.

### Required unit tests (`tests/Tactile.Tests/`)

- `tests/Tactile.Tests/Integration/AcSharedMemoryTests.cs` *(new, Job B)*:
  - `Acc_parser_extracts_signals_assists_and_kerb_vibrations`: builds fake `acpmf_physics` (`800` B), `acpmf_graphics` (`status = 2`), and `acpmf_static` (`456` B) buffers; verifies `SpeedKmh`, `Rpm` (using `currentMaxRpm` at `588` and fallback to `static:452`), `Gas`, `Brake`, `Clutch`, `Gear` (`0=R, 1=N, 2=1st`), `Steer`, `SlipFront` (`FL/FR`), `SlipRear` (`RL/RR`), `Boost`, `Abs` (`absInAction` at `676` and `absVibrations` at `796`), `Traction` (`tcinAction` at `672`), `RumbleStrip` (`kerbVibration` at `784`), and `Surface`.
  - `Ac_original_parser_extracts_drs_assists_and_suspension_kerb`: builds a 712-byte original AC physics buffer with `drs = 1.0f` at offset `200`, `tc > 0` at `204`, `abs > 0` at `252`, and a frame-to-frame `suspensionTravel` jump at `184..199`; verifies `IsAcc == false`, `Drs == 1`, `Abs == 1`, `Traction == 1`, `RumbleStrip == 1`, and `Surface > 0`.
  - `Poller_waits_silently_when_closed_throttles_to_60hz_and_closes_handles_when_packet_id_freezes`: uses `FakeSharedMemorySource` with a manual clock; verifies `TryPoll` returns `false` without throwing when closed, skips polls `< 16 ms` apart, reports `RaceOn == 1` while `packetId` advances and `status == 2`, and transitions to `RaceOn == 0` while calling `Close()` on the source when `packetId` does not advance for `> 200 ms` or `status` changes to `AC_PAUSE` (`3`).
- `tests/Tactile.Tests/Integration/F1TelemetryTests.cs` *(new, Job A)*:
  - `Parses_f1_2024_2025_and_2026_formats_and_merges_status_and_motion_ex`: tests `2024` (`1352` B telemetry, `1239` B status, `237` B motion Ex) and `2026` (`1472` B telemetry, `1349` B status, `273` B motion Ex, player index `23`); verifies `SpeedKmh`, `Gas`, `Brake`, `Clutch`, `Gear`, `Steer`, `Drs`, `RevLights`, `Rpm` (before and after `CarStatusData` arrives), `Boost` (ERS ratio), `RumbleStrip` (`m_surfaceType == 1`), `SlipRear` (`[0]=RL, [1]=RR`), `SlipFront` (`[2]=FL, [3]=FR`), `Abs`, and `Traction`.
  - `Counts_unknown_packet_formats_as_unsupported_and_rejects_bad_sizes_or_car_indices`: verifies `m_packetFormat = 2023` or `2027` increments `Unsupported` (not `Rejected`), truncated/wrong-size `ID 6`/`7`/`13` or `m_playerCarIndex = 22` (in 22-car format) increments `Rejected`, `m_playerCarIndex = 255` emits `RaceOn = 0`, and changing `m_sessionUID` clears cached `maxRpm` and slip ratios.
- `tests/Tactile.Tests/Integration/BeamNgOutGaugeTests.cs` *(new, Job A)*:
  - `Parses_92_and_96_byte_outgauge_packets_and_dashboard_lights`: verifies `SpeedKmh` (`m/s * 3.6`), `Gear` (`0 -> 0`, `1 -> 0`, `2 -> 1`, `5 -> 4`), `Gas`, `Brake`, `Clutch`, `Boost`, `showLights` bits (`DL_SHIFT -> RevLights = 1`, `DL_HANDBRAKE -> Handbrake = 1`, `DL_TC -> Traction = 1`, `DL_ABS -> Abs = 1`), adaptive `maxRpm`, and all-zero stop packet (`RaceOn = 0`).
  - `Rejects_other_sizes_and_sanitizes_nonfinite_floats`: verifies lengths `0`, `91`, `93`, `95`, `97` return `false` and `NaN`/`Infinity` floats become `0`.
- `tests/Tactile.Tests/Integration/TelemetryListenerTests.cs`, `TelemetryRulesTests.cs`, `IntegrationCoreTests.cs` *(Job A)*:
  - `Multi_source_last_active_frame_wins_and_reports_active_source`: enables Forza, F1, BeamNG (on ephemeral loopback ports `0`), and AC shared memory (`FakeSharedMemorySource`); sends an F1 frame (`ActiveSource == TelemetrySource.F1`), then a BeamNG frame (`ActiveSource == TelemetrySource.BeamNg`), then an ACC frame (`ActiveSource == TelemetrySource.Acc`), verifying the output layer and `ActiveSource` switch to the latest active sender and clear after `1 s` of silence.
  - `Port_conflicts_follow_priority_order_udp_forza_f1_beamng`: verifies conflict detection across `UdpPort`, `ForzaPort`, `F1Port`, and `BeamNgPort`.
  - `All_five_game_templates_validate_and_drive_expected_targets`: verifies `TelemetryTemplates.Forza`, `F1`, `Acc`, `Ac`, and `BeamNg` normalize with zero issues and drive `LeftTrigger`, `RightTrigger`, `Lightbar`, `PlayerLeds`, and `RumbleRight` (including `RevLights` and `Drs` signals).

---

## 6. Safety Rules (Every Job)

- **No external network access**: Every UDP listener (`Forza`, `F1`, `BeamNg`) must bind exclusively to `IPAddress.Loopback` (`127.0.0.1`), never `IPAddress.Any` (`0.0.0.0`) or `IPAddress.IPv6Any` (`::`), and must drop any packet whose `RemoteEndPoint` is not a loopback address.
- **No writing to game memory**: `AccSharedMemory` / `AcSharedMemory` must open Windows memory-mapped files strictly with `MemoryMappedFileRights.Read` and `MemoryMappedFileAccess.Read`. Never call `CreateOrOpen` or open with write permissions.
- **No hardware or OS injection**: Agents must NEVER run code that sends output to a real controller, injects keyboard/mouse input, attaches/hides devices, or opens real OS shared memory in tests. The application executable and `udptest.py` are never started by agents. All unit tests use ephemeral loopback ports (`127.0.0.1:0`), `FakeSharedMemorySource`, and `ManualTime`.
- **Allowed commands**: Only `dotnet build`, `dotnet test`, and `npm run build` (in `ui/`).
- **No commits**: Do not run `git commit`, `git push`, or modify git history.
- **No AI attribution**: No model, assistant, or tool names anywhere in code, comments, or documentation.

---

## 7. Hardware Test Checklist (KH — End of Phase, ~15 min)

1. **Fake F1 UDP stream (`udptest.py f1`)**: In TriggerForge → Integrations, enable **F1 UDP telemetry** (port `20777`), select the active profile, and apply the **F1** template. Run `py -3 tools/integration-test/udptest.py f1 16` → verify the F1 card shows `"Receiving data (2024)"` and `"Active source: EA SPORTS F1"`, `R2` resists with RPM and vibrates on traction loss, `L2` resists on braking and vibrates on ABS, lightbar + player LEDs track rev LEDs and blink at redline, right rumble fires on kerb, and all effects release when the script stops.
2. **Fake BeamNG OutGauge stream (`udptest.py beamng`)**: Enable **BeamNG.drive OutGauge** (port `4444`) and apply the **BeamNG** template. Run `py -3 tools/integration-test/udptest.py beamng 16` → verify `"Active source: BeamNG.drive"`, live speed/RPM/gear values update, trigger resistance/ABS/TC vibration and shift light blink work, and effects release on stop.
3. **Multi-source handoff**: Keep both F1 and BeamNG enabled; start `udptest.py f1 10` and then `udptest.py beamng 10` → verify `Active source` switches smoothly to the latest sender without stuck trigger or rumble effects.
4. **Real game check (if F1 24/25, ACC, AC, or BeamNG.drive is installed)**:
   - *F1 24/25*: Enable UDP Telemetry in game settings (`127.0.0.1:20777`, Broadcast Off) → drive over kerbs and heavy braking zones.
   - *ACC / AC*: Enable **Assetto Corsa / ACC shared memory** in TriggerForge and apply the **ACC** or **AC** template → enter a driving session and verify live detection (`(ACC)` or `(AC)`), kerb rumble, ABS/TC trigger vibration, and automatic release when pausing to the menu.
   - *BeamNG.drive*: Enable OutGauge (`127.0.0.1:4444`) in BeamNG Options → Other → verify braking, ABS, and shift light feedback.

---

## 8. Risks and Rollback

- **Risk — F1 wheel order (`RL, RR, FL, FR`) vs Forza/AC wheel order (`FL, FR, RL, RR`)**:
  - *Mitigation*: `F1TelemetrySession` explicitly maps indices `[0, 1]` of `m_wheelSlipRatio` (`MotionExData` offsets `93, 97`) to `SlipRear` and indices `[2, 3]` (offsets `101, 105`) to `SlipFront`, verified by a dedicated unit test with asymmetric front/rear slip values.
- **Risk — F1 25 (2026 Season Pack) 24-car packet sizes breaking 22-car size checks**:
  - *Mitigation*: `F1TelemetrySession` computes `maxCars = (packetFormat == 2026) ? 24 : 22` from `m_packetFormat` and validates `CarTelemetryData` (`1352` vs `1472` B), `CarStatusData` (`1239` vs `1349` B), `MotionData` (`1349` vs `1325` B), and `MotionExData` (`237` vs `273` B) dynamically per format.
- **Risk — Windows shared memory handle leak keeping stale AC/ACC buffers alive after the game closes**:
  - *Mitigation*: `AccSharedMemoryReader` checks `acpmf_graphics.status == 2` (`AC_LIVE`) and `acpmf_physics.packetId` advancement; when `packetId` does not change for `> 200 ms`, it sets `RaceOn = 0` and immediately disposes its `MemoryMappedViewAccessor` and `MemoryMappedFile` handles.
- **Risk — Merge conflicts with Phase 12 (`DsuServer.cs`)**:
  - *Mitigation*: All new Phase 13 parsers live in their own files (`F1Telemetry.cs`, `AccSharedMemory.cs`, `AcSharedMemory.cs`, `BeamNgOutGauge.cs`) and `IntegrationStatus` uses optional trailing parameters so `DsuStatus` and Phase 13 status fields merge cleanly.
- **Rollback switches**:
  - Setting `f1Enabled: false`, `acEnabled: false`, and `beamNgEnabled: false` in `AppSettings.Integration` (all `false` by default) disables every new socket and shared memory poller, leaving only the Phase 6 Forza listener path active.
