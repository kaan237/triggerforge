# Integration protocols (phase 6)

Facts written in our own words from public documentation and public examples. No code was
copied. Sources are listed at the end.

## 1. DSX UDP format (used by existing DSX mods)

### Transport

- UDP, one JSON object per datagram, sent to `127.0.0.1`.
- The mods do not hard-code a port. They read it as plain text from
  `C:\Temp\DualSenseX\DualSenseX_PortNumber.txt` (just the number) and send to that port.
- After each packet the client waits for one JSON answer from the same port. Clients are
  told to use a timeout, so a missing answer must not break them.

### Request

```jsonc
{ "instructions": [ { "type": <int>, "parameters": [ <number | bool>, ... ] }, ... ] }
```

- Enum values travel as integers (the examples serialize C# enums with the default JSON
  settings). Booleans travel as `true` / `false`.
- The first parameter of every instruction is the controller index (0 = first pad).
- Mods often send one instruction per packet; some send several in one packet.

### Instruction types

| Value | Name | Parameters after the controller index |
|---|---|---|
| 0 | Invalid | — |
| 1 | TriggerUpdate | trigger, mode, mode parameters (below) |
| 2 | RGBUpdate | red, green, blue (0-255); some senders add a brightness value |
| 3 | PlayerLED | five booleans, leftmost LED first |
| 4 | TriggerThreshold | trigger, threshold 0-255 (when the trigger counts as "pressed" in DSX's own emulation) |
| 5 | MicLED | mode: 0 = on, 1 = pulse, 2 = off |
| 6 | PlayerLEDNewRevision | 0 = player 1 … 4 = player 5 (all on), 5 = all off |
| 7 | ResetToUserSettings | none: drop everything the mod set and go back to the user's own settings |

Trigger: 0 = invalid, 1 = left, 2 = right.

Player LED layouts for `PlayerLEDNewRevision` (five LEDs, left to right): player 1 = middle only;
player 2 = second and fourth; player 3 = first, middle, fifth; player 4 = all but the middle;
player 5 = all five.

### Trigger modes (`TriggerUpdate`)

| Value | Name | Extra parameters (ranges as the example states them) |
|---|---|---|
| 0 | Normal | — (no effect) |
| 1 | GameCube | — (hard stop near the end of travel) |
| 2 | VerySoft | — |
| 3 | Soft | — |
| 4 | Hard | — |
| 5 | VeryHard | — |
| 6 | Hardest | — |
| 7 | Rigid | — |
| 8 | VibrateTrigger | frequency 0-255 |
| 9 | Choppy | — |
| 10 | Medium | — |
| 11 | VibrateTriggerPulse | — |
| 12 | CustomTriggerValue | custom mode (below), then 7 raw values 0-255 |
| 13 | Resistance | start 0-9, force 0-8 |
| 14 | Bow | start 0-8, end 0-8, force 0-8, snap force 0-8 |
| 15 | Galloping | start 0-8, end 0-9, first foot 0-6, second foot 0-7, frequency 0-255 |
| 16 | SemiAutomaticGun | start 2-7, end 0-8, force 0-8 |
| 17 | AutomaticGun | start 0-9, strength 0-8, frequency 0-255 |
| 18 | Machine | start 0-9, end 0-9, strength A 0-7, strength B 0-7, frequency 0-255, period 0-2 |

The example's comments for AutomaticGun and Machine are swapped; the parameter lists above
follow the example calls, which agree with the parameter counts.

Custom mode values: 0 off; 1-4 rigid (plain, A, B, AB); 5-8 pulse (plain, A, B, AB); 9-12
vibrate-resistance (plain, A, B, AB); 13-16 vibrate-pulse (plain, A, B, AB). These name the
controller's older simple trigger modes (`docs/protocol.md`, "Legacy" row). The meaning of the
A/B variants is not in our notes.

### Answer

```jsonc
{ "Status": "<text>", "TimeReceived": "<local time as text>", "isControllerConnected": <bool>, "BatteryLevel": <int 0-100> }
```

### Not confirmed

- The public examples are the old GitHub version (v1.4.9 and the "v2.0" example). The paid
  Steam version (v3) may accept more modes or fields. Unknown types and modes must be ignored,
  not treated as errors.
- DSX's own default port; irrelevant for us, because the mods read the port file.

## 2. Forza "Data Out" telemetry

### Transport

- One-way UDP from the game to a user-set IP and port, one packet per rendered frame, only
  while the player is driving (nothing in menus, pauses, replays, after a race).
- In-game: Forza Horizon 6: SETTINGS > HUD AND GAMEPLAY: Data Out (on/off), Data Out IP Address,
  Data Out IP Port. Forza Motorsport (2023): SETTINGS > GAMEPLAY & HUD > "UDP RACE TELEMETRY",
  same fields plus a format choice "Sled" or "Dash". 127.0.0.1 is allowed.
- Forza Horizon 6 binds its own socket somewhere in ports 5200-5300; listeners must use a
  port outside that range.
- Little endian (PC). Types: S = signed integer, U = unsigned, F = float; number = bits.

### Format by packet size

| Size | Game / format |
|---|---|
| 232 | "Sled" (Forza Motorsport 7 and Forza Motorsport): no player inputs, no speed field |
| 311 | Forza Motorsport 7 "Dash" |
| 331 | Forza Motorsport (2023) "Dash": FM7 dash + tire wear ×4 (F32) + track id (S32) |
| 324 | Forza Horizon 4, 5 and 6: sled, then 12 extra bytes, then the dash fields, then 1 unused byte |
| 323 | Reported by the community for some Forza Horizon 5 builds; same offsets as 324 |

### Sled part (offsets 0-231, all formats)

| Offset | Type | Field |
|---|---|---|
| 0 | S32 | IsRaceOn (1 = driving, 0 = menus / stopped) |
| 4 | U32 | TimestampMS (may wrap) |
| 8 | F32 | EngineMaxRpm |
| 12 | F32 | EngineIdleRpm |
| 16 | F32 | CurrentEngineRpm |
| 20 / 24 / 28 | F32 | Acceleration X / Y / Z (car space, m/s²) |
| 32 / 36 / 40 | F32 | Velocity X / Y / Z (car space, m/s) |
| 44 / 48 / 52 | F32 | Angular velocity X / Y / Z (rad/s) |
| 56 / 60 / 64 | F32 | Yaw / Pitch / Roll (rad) |
| 68-80 | F32 ×4 | Normalized suspension travel FL, FR, RL, RR (0 stretched, 1 compressed) |
| 84-96 | F32 ×4 | Tire slip ratio (0 = full grip, \|x\| > 1 = grip lost) |
| 100-112 | F32 ×4 | Wheel rotation speed (rad/s) |
| 116-128 | S32 ×4 | Wheel on rumble strip (1/0) |
| 132-144 | S32 ×4 | Wheel in puddle (1/0) |
| 148-160 | F32 ×4 | Surface rumble (the game's own controller-rumble value) |
| 164-176 | F32 ×4 | Tire slip angle (same scale as slip ratio) |
| 180-192 | F32 ×4 | Tire combined slip (same scale) |
| 196-208 | F32 ×4 | Suspension travel in meters |
| 212 | S32 | CarOrdinal |
| 216 | S32 | CarClass (0 = D … 7 = X) |
| 220 | S32 | CarPerformanceIndex (100-999) |
| 224 | S32 | DrivetrainType (0 FWD, 1 RWD, 2 AWD) |
| 228 | S32 | NumCylinders |

Wheel order in every ×4 group: front left, front right, rear left, rear right.

### Horizon extra (offsets 232-243, 323/324-byte packets only)

U32 CarGroup (232), F32 SmashableVelDiff (236, m/s), F32 SmashableMass (240, kg).

### Dash part

`D` = 232 for the Motorsport dash formats (311, 331) and 244 for the Horizon formats (323, 324).

| Offset | Type | Field |
|---|---|---|
| D+0 / +4 / +8 | F32 | Position X / Y / Z (m) |
| D+12 | F32 | Speed (m/s) |
| D+16 | F32 | Power (W) |
| D+20 | F32 | Torque (N·m) |
| D+24-36 | F32 ×4 | Tire temperature |
| D+40 | F32 | Boost (psi above atmospheric) |
| D+44 | F32 | Fuel (0-1) |
| D+48 | F32 | Distance traveled (m) |
| D+52 / +56 / +60 | F32 | Best / last / current lap (s) |
| D+64 | F32 | Current race time (s) |
| D+68 | U16 | Lap number |
| D+70 | U8 | Race position |
| D+71 | U8 | Accel (0-255) |
| D+72 | U8 | Brake (0-255) |
| D+73 | U8 | Clutch (0-255) |
| D+74 | U8 | Handbrake (0-255) |
| D+75 | U8 | Gear |
| D+76 | S8 | Steer (−127 left … 127 right) |
| D+77 | S8 | Normalized driving line |
| D+78 | S8 | Normalized AI brake difference |

So for Horizon: Speed 256, Accel 315, Brake 316, Gear 319, Steer 320. For FM7/FM dash:
Speed 244, Accel 303, Brake 304, Gear 307, Steer 308.

### Not confirmed

- Gear numbering (which value is reverse / neutral) is not in the official text.
- The 323-byte Forza Horizon 5 variant is a community report only.

## Sources

- Paliverse/DualSenseX on GitHub: README and the two UDP example projects
  (`UDPExample.zip`, `UDP Example (C#) for v2.0/DSX_UDP_Example.zip`), read 2026-11-19.
- Forza Support, "Forza Horizon 6 Data Out Documentation" and "Forza Motorsport Data Out
  Documentation", support.forza.net, read 2026-11-19.
