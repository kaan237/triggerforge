# Phase 12 contract: game compatibility — virtual DualShock 4, DSU motion server, per-game automatic virtual controller

Adds to `docs/contract.md`, `docs/contract-phase4.md`, `docs/contract-phase6.md`, and the phase 7-11 contracts. Version becomes **1.3.0**.

Goal: deliver three game-compatibility features — a virtual wired DualShock 4 (USB v2, `VID 0x054C / PID 0x09CC`, HID-only with full sticks, buttons, triggers, IMU, 2-point touchpad, battery, rumble + lightbar feedback, and SDL/Steam feature reports), a local DSU/Cemuhook UDP motion server (`127.0.0.1:26760`, up to 4 controller slots) for emulators, and per-game automatic virtual controller selection (`emulation.mode: "auto"` backed by an embedded native DualSense game list and user per-exe overrides) — while keeping every existing profile, setting, and test (`659+` tests) unbroken and without touching `src/Tactile.Core/Mapping/**`.

## 1. Scope

### In scope (3 implemented features)

1. **Virtual DualShock 4 (`emulation.mode: "dualShock4"`, USB v2 `PID 0x09CC`, HID-only)**:
   - Extends `VirtualKind` (`src/Tactile.Emulation/Contracts.cs:6`) and `EmulationMode` (`src/Tactile.Core/Model/Profile.cs:18`) with `DualShock4`.
   - Works on primary slot `0` (over both Bluetooth and USB physical DualSense connections, since the virtual device kind differs from the physical DualSense) and on secondary slots `1..3` (`src/Tactile.Core/Emulation/EmulationSlots.cs:112-189`). No USB audio interface (HID-only, 41-byte configuration descriptor).
   - **USB Descriptors**:
     - Device descriptor (18 B): `12 01 00 02 00 00 00 40 4C 05 CC 09 00 01 01 02 00 01` (`bcdUSB = 0x0200`, `bMaxPacketSize0 = 64`, `idVendor = 0x054C`, `idProduct = 0x09CC`, `bcdDevice = 0x0100`, `iManufacturer = 1` `"Sony Interactive Entertainment"`, `iProduct = 2` `"Wireless Controller"`, `iSerialNumber = 0` on slot 0 or `3` with `SecondaryPads.Serial(slot)` on slots `1..3`).
     - Configuration descriptor (41 B, 1 interface, HID class `03/00/00`):
       - Config (9 B): `09 02 29 00 01 01 00 C0 FA` (`wTotalLength = 41`, 1 interface, `bmAttributes = 0xC0`, 500 mA).
       - Interface 0 (9 B): `09 04 00 00 02 03 00 00 00` (2 endpoints, HID).
       - HID descriptor (9 B): `09 21 11 01 00 01 22 <lenLo> <lenHi>` (`bcdHID = 1.11`, Report descriptor `0x22`, exact byte length of the DS4 USB report descriptor).
       - Interrupt IN endpoint (7 B): `07 05 84 03 40 00 06` (EP `0x84` IN, 64 B, High-Speed `bInterval = 6` → 4 ms, matching `VirtualDualSense.cs:19`).
       - Interrupt OUT endpoint (7 B): `07 05 03 03 40 00 06` (EP `0x03` OUT, 64 B, `bInterval = 6`).
     - HID report descriptor: standard DS4 USB report descriptor declaring Input `0x01` (64 B), Output `0x05` (32 B), and the DS4 Feature reports (`0x02` 37 B, `0x04` 42 B, `0x08` 48 B, `0x10` 5 B, `0x11` 3 B, `0x12` 16 B, `0x13` 24 B, `0x14` 21 B, `0x15` 45 B, `0x80` 7 B, `0x81` 7 B, `0x82` 6 B, `0x83` 2 B, `0xA3` 49 B, etc.). Every feature report ID declared in the report descriptor must have an entry in `_features` with the exact declared byte length (including the report ID byte).
   - **Input report `0x01` (64 B on EP `0x84` IN)**:
     - Byte `0`: `0x01` (Report ID).
     - Bytes `1..4`: `LX, LY, RX, RY` (`uint8`, `0` = left/up, `128` = center, `255` = right/down).
     - Byte `5`: Bits `0..3` = D-Pad hat (`0..7` clockwise from up, `8` = neutral; opposing directions cancel); Bit 4 = `Square`, Bit 5 = `Cross`, Bit 6 = `Circle`, Bit 7 = `Triangle`.
     - Byte `6`: Bit 0 = `L1`, Bit 1 = `R1`, Bit 2 = `L2` (digital), Bit 3 = `R2` (digital), Bit 4 = `Share` (`Buttons.Create`), Bit 5 = `Options`, Bit 6 = `L3`, Bit 7 = `R3`.
     - Byte `7`: Bit 0 = `PS`, Bit 1 = `Touchpad`, Bits `2..7` = 6-bit frame counter (`(sequence & 0x3F) << 2`, incremented by 1 per report).
     - Bytes `8..9`: `L2, R2` analog (`0..255`).
     - Bytes `10..11`: `uint16 LE` IMU timestamp in `5.33 µs` ticks (`(ushort)(s.SensorTimestamp >> 4)`, converting DualSense `0.333 µs` ticks to DS4 `5.33 µs` ticks).
     - Byte `12`: battery temperature (`0x14`).
     - Bytes `13..18`: `GyroRaw` (`3 × int16 LE`: Pitch, Yaw, Roll).
     - Bytes `19..24`: `AccelRaw` (`3 × int16 LE`: X, Y, Z).
     - Bytes `25..29`: `0x00`.
     - Byte `30`: status/battery flags — Bits `0..3` = battery level (`s.Battery.Charge == ChargeState.Full ? 11 : Math.Clamp(s.Battery.Percent / 10, 0, 10)`), Bit 4 (`0x10`) = `1` (USB cable connected), Bit 5 (`0x20`) = `HeadphoneConnected`, Bit 6 (`0x40`) = `MicConnected`.
     - Bytes `31..32`: `0x00`.
     - Byte `33`: touch packet count = `0x01`.
     - Byte `34`: touch packet counter (`sequence`).
     - Bytes `35..38`: Touch 1 (`t[0] = (byte)((p.Active ? 0x00 : 0x80) | (p.Id & 0x7F))`, 12-bit `X` clamped to `0..1919`, 12-bit `Y` clamped to `0..941` for DS4 touchpad resolution).
     - Bytes `39..42`: Touch 2 (same packing; inactive = `0x80, 0x00, 0x00, 0x00`).
     - Bytes `43..60`: 2 historical 9-byte touch slots Marked inactive (`report[44] = 0x80; report[48] = 0x80; report[53] = 0x80; report[57] = 0x80`).
     - Bytes `61..63`: `0x00`.
   - **Output report `0x05` (32 B on EP `0x03` OUT or EP0 `SET_REPORT` Output ID `0x05`)**:
     - Accepts 32-byte reports with `data[0] == 0x05` (or 31-byte ID-less `SET_REPORT` payloads on Linux hosts; larger buffers up to 64 B starting with `0x05` use the first 32 B).
     - Flags byte (`block[0]`, report offset `1`): Bit 0 (`0x01`) = update rumble (`Right/Small = block[3]`, `Left/Large = block[4]`), Bit 1 (`0x02`) = update lightbar (`R = block[5], G = block[6], B = block[7]`).
     - Raises `DualShock4Feedback(long Timestamp, bool HasRumble, byte Large, byte Small, bool HasLightbar, Rgb Lightbar)` on the virtual pad, which `EmulationService.OnFeedback` overlays onto `_game` / `_gameFields` (`OutputFields.Rumble`, `OutputFields.Lightbar`) and forwards to the physical DualSense (`GameLayer` on slot 0, `secondary.SetFeedback` on slots `1..3`) with profile `Rumble.Strength` scaling.
   - **Feature reports (`GET_REPORT` Feature on EP0)**:
     - `0x02` (37 B, IMU calibration): copies bytes `1..34` from the real DualSense calibration (`identity?.Calibration`, which shares the exact 34-byte pitch/yaw/roll bias, `plus/minus`, `speed_plus/minus`, and accel X/Y/Z `plus/minus` layout) or `DefaultCalibration()` when `identity?.Calibration` is null; byte `0 = 0x02`, bytes `35..36 = 0`.
     - `0x12` (16 B, pairing/MAC): byte `0 = 0x12`, bytes `1..6` = controller Bluetooth MAC in reverse byte order (from `identity?.Pairing[1..7]` or `DefaultPairing(slot)`), bytes `7..9 = 0x08, 0x25, 0x00`, bytes `10..15` = host MAC.
     - `0x81` (7 B, USB device MAC): byte `0 = 0x81`, bytes `1..6` = controller Bluetooth MAC in reverse byte order (same 6 bytes as `0x12[1..6]`).
     - `0xA3` (49 B, firmware/hardware version): byte `0 = 0xA3`, bytes `1..16` = ASCII build date (`"Jun  2 2018"` padded with NUL), bytes `17..32` = ASCII build time (`"12:00:00"` padded with NUL), bytes `35..36` = `hw_version` (`0x0100` LE), bytes `41..44` = `fw_version` (`0x00000100` LE) so SDL (`SDL_hidapi_ps4.c`) and Steam Input identify the device as an official Sony DS4 with gyro and lightbar support.

2. **DSU / Cemuhook motion server (`src/Tactile.Core/Integration/DsuServer.cs`)**:
   - Localhost-only UDP server bound to `127.0.0.1` (`IPAddress.Loopback`), default port `26760` (`settings.integration.dsuPort`, range `1024..65535`), disabled by default (`settings.integration.dsuEnabled: false`).
   - Follows the Phase 6 UDP listener pattern (`src/Tactile.Core/Integration/UdpApiServer.cs:18-24,354-385,413-442` and `src/Tactile.Core/Integration/TelemetryListener.cs:52-83`):
     - Sets `SioUdpConnReset` (`0x9800000C`) on Windows so an ICMP port-unreachable from a closed emulator socket never faults the receive loop.
     - Detects port conflicts if `dsuPort == udpPort` or `dsuPort == (forzaEnabled ? forzaPort : -1)` (or when `dsuPort` equals either configured port in `IntegrationSettings`), reporting `DsuListenerState.PortConflict` without binding.
     - On `SocketException` during bind, transitions to `DsuListenerState.PortBusy`, posts one localized notice (`"DSU port {port} is in use. Close other motion servers or choose another port."` / `"DSU portu {port} kullanımda. Diğer hareket sunucularını kapatın ya da portu değiştirin."`), and retries every `5` seconds (`RetryInterval`).
     - **Rate limit and malformed packet protection**: rolling 1-second window limit of `MaxPacketsPerSecond = 500` incoming client requests; datagrams from non-loopback addresses, shorter than `20` bytes or larger than `1024` bytes, with magic `!= "DSUC"`, version `!= 1001`, declared length `!= receivedBytes - 16`, or invalid IEEE 802.3 CRC-32 (`Crc32.Compute` in `src/Tactile.Device.DualSense/Crc32.cs:12` with bytes `8..11` zeroed) are dropped and increment `rejected`.
   - **Protocol messages (Little-Endian, version `1001` / `0x03E9`)**:
     - `0x100000` (Protocol Version): replies with a 22-byte `"DSUS"` packet (`length = 6`, message type `0x100000`, payload `uint16 LE = 1001`).
     - `0x100001` (Controller Info): reads `int32` slot count (`clamped/validated to 1..4`) at payload `0..3` and requested slot indices (`0..3`) at payload `4..`. For each valid slot index `0..3`, sends a 32-byte `"DSUS"` packet (`length = 16`, message type `0x100001`, 11-byte shared controller header + `0x00` terminal byte).
     - `0x100002` (Controller Data Subscription & Stream):
       - Parses the 8-byte client request (`regFlags` at `0`: `0` = all slots, `1` = slot `request[1]`, `2` = MAC `request[2..8]`).
       - Registers or refreshes the client's `IPEndPoint` in the subscriber table (capped at `MaxClients = 8`; oldest expired or evicted first). Subscriptions expire after `5` seconds (`ClientTimeout = TimeSpan.FromSeconds(5)`) without a `0x100002` keep-alive request.
       - On every input report from any connected controller slot (`0..3`), sends a 100-byte `"DSUS"` packet (`length = 84`, message type `0x100002`, 80-byte payload) to each active subscriber matching that slot/MAC:
         - Shared 11-byte header (`payload[0..10]`): `slot` (`0..3`), `state` (`2` = connected, `0` = disconnected), `model` (`2` = full gyro), `connection` (`1` = USB, `2` = Bluetooth), 6-byte MAC (`Controller.Details.MacAddress` bytes or synthetic `[0x02, 0x54, 0x41, 0x43, 0x54, (byte)(slot + 1)]`), `battery` (`ChargeState.Full` → `0xEF`, `ChargeState.Charging` → `0xEE`, otherwise `0x00..0x05` mapped from `Battery.Percent`).
         - `payload[11]` = `1` (active), `payload[12..15]` = `uint32 LE` per-slot packet counter.
         - `payload[16]` = Buttons 1 (`0x80` Left, `0x40` Down, `0x20` Right, `0x10` Up, `0x08` Options, `0x04` R3, `0x02` L3, `0x01` Share/Create).
         - `payload[17]` = Buttons 2 (`0x80` Square, `0x40` Cross, `0x20` Circle, `0x10` Triangle, `0x08` R1, `0x04` L1, `0x02` R2, `0x01` L2).
         - `payload[18]` = PS (`0` or `1`), `payload[19]` = Touchpad click (`0` or `1`).
         - `payload[20..23]` = `LX` (`s.Left.X`), `LY` (`(byte)(255 - s.Left.Y)`), `RX` (`s.Right.X`), `RY` (`(byte)(255 - s.Right.Y)`).
         - `payload[24..35]` = analog D-Pad (`24..27`: Left, Down, Right, Up as `0`/`255`), analog face buttons (`28..31`: Square, Cross, Circle, Triangle as `0`/`255`), analog bumpers (`32`: R1, `33`: L1 as `0`/`255`), analog triggers (`34`: `s.R2`, `35`: `s.L2`).
         - `payload[36..41]` = Touch 1 and `payload[42..47]` = Touch 2 (`+0`: `Active ? 1 : 0`, `+1`: `Id`, `+2..3`: `uint16 LE X`, `+4..5`: `uint16 LE Y`).
         - `payload[48..55]` = `uint64 LE` motion timestamp in **microseconds (`µs`)** (monotonic accumulation of 32-bit `s.SensorTimestamp` ticks converted from `0.333 µs` via `ticks / 3`, falling back to monotonic clock microseconds if `SensorTimestamp` is `0`).
         - `payload[56..67]` = `3 × float32 LE` accelerometer in **`g`** (`cal.AccelG(s.AccelRaw)` → `X, Y, Z`).
         - `payload[68..79]` = `3 × float32 LE` gyroscope in **`deg/s`** (`Pitch = gyro.X, Yaw = gyro.Y, Roll = gyro.Z`, using bias-corrected `GyroDegPerSec` on slot 0 and `cal.GyroDegPerSec(s.GyroRaw)` on slots `1..3`).

3. **Per-game automatic virtual controller output (`emulation.mode: "auto"`)**:
   - Extends `EmulationMode` (`src/Tactile.Core/Model/Profile.cs:18`) with `Auto`.
   - Embeds a small, curated JSON resource `src/Tactile.Core/Emulation/dualsense-games.json` containing lowercase `.exe` file names of PC games with verified native DualSense support and a `"source"` attribution field citing PCGamingWiki (`https://www.pcgamingwiki.com/wiki/List_of_games_that_support_DualSense`).
   - Adds user-editable per-executable overrides `settings.emulation.gameModes` (`IReadOnlyDictionary<string, EmulationMode>`, mapping lowercase `.exe` file name → `dualSense | xbox360 | dualShock4 | off`, max `256` entries), editable in the UI (Settings / Virtual controller page) with a picker from `RecentForeground` (`foreground.pick`) or manual `.exe` entry.
   - **Resolution rule (`AutoEmulationResolver.Resolve`)**:
     - When a profile's `emulation.mode` is explicitly `Off`, `Xbox360`, `DualSense`, or `DualShock4`, that explicit mode is used directly (per-profile explicit choice always wins over `auto` table).
     - When a profile's `emulation.mode` is `Auto`:
       1. Look up the current settled foreground game executable in `settings.emulation.gameModes` (case-insensitive file name). If found, resolve to that override mode (`DualSense`, `Xbox360`, `DualShock4`, or `Off`).
       2. Otherwise, if the executable is in the embedded `dualsense-games.json` native list, resolve to `EmulationMode.DualSense`.
       3. Otherwise (unknown game or desktop), resolve to `EmulationMode.Xbox360`.
     - If the resolved mode is `DualSense` and the physical controller on slot 0 is connected over **USB**, `EmulationService.WantedKindLocked` returns `null` as today (`src/Tactile.Core/Emulation/EmulationService.cs:511-514`: the game already sees the physical wired DualSense without a virtual device).
   - **Switch stability & release-all safety**:
     - In `Auto` mode, the target virtual controller mode only re-evaluates when the settled foreground game executable (`_selector.SettledForeground` after `AutoProfileDelayMs`, or a newly matched game executable) changes, or when the active profile or `settings.emulation.gameModes` changes. Brief focus changes to system shells (`explorer.exe`) or TriggerForge itself do not flap the virtual pad while a game remains the last settled game executable, or follow `_selector.SettledForeground` after `AutoProfileDelayMs`.
     - Whenever the resolved virtual pad kind changes (`Xbox360` ↔ `DualSense` ↔ `DualShock4` ↔ `Off`) and the virtual pad is detached/reattached:
       1. Before detaching the old virtual pad in `EmulationService.StopLocked`, a neutral `InputState` (all buttons released, triggers `0`, sticks centered at `128, 128`) is submitted to the outgoing pad.
       2. `AppCore` releases all held mapped keys, mouse buttons, macros, toggles, and turbo outputs (`_mapper.ResetController()`) on virtual controller mode transition so no input can stay stuck across a hot-plug.

### Out of scope

- **Bluetooth DualShock 4 emulation**: Virtual DS4 is USB (`0x09CC`) over USB/IP only.
- **Virtual DS4 USB audio / headset / microphone interfaces**: Virtual DS4 is HID-only (41-byte config descriptor); DualSense USB audio remains exclusive to virtual `DualSense` on primary slot 0.
- **Xbox One / Switch Pro virtual controller output**: Excluded.
- **Physical DualShock 4 input support**: Phase 15.3.
- **Modification of `src/Tactile.Core/Mapping/**`**: Phase 11 is developed concurrently in `src/Tactile.Core/Mapping/**`; Phase 12 must not edit any file in `src/Tactile.Core/Mapping/**`.

---

## 2. Profile and Settings Schema

`Profile.CurrentVersion` (`src/Tactile.Core/Model/Profile.cs:26`) and `AppSettings.CurrentVersion` (`src/Tactile.Core/Model/AppSettings.cs:8`) stay `1`.

### Migration and backwards-compatibility rule

- No file version bump. Existing Phase 2-11 profile and settings JSON files without `"dualShock4"`, `"auto"`, `"gameModes"`, `"dsuEnabled"`, or `"dsuPort"` deserialize cleanly to defaults via C# record initializers and `LenientEnumConverterFactory` (`src/Tactile.Core/Storage/TactileJson.cs:34-62`), then pass through `Validation.Normalize` (`src/Tactile.Core/Storage/Validation.cs:14-112,198-316`).
- In the UI, `normalizeProfile` and `normalizeSettings` (`ui/src/model.ts:164-228,336-366`) fill missing fields with identical defaults.
- **Test requirement**: Loading an existing Phase 2-11 profile JSON and settings JSON without any Phase 12 fields must produce identical normalized objects and runtime behavior as before.

### Profile additions (`Profile.Emulation`)

```jsonc
{
  "emulation": {
    "mode": "off",                   // "off" | "xbox360" | "dualSense" | "dualShock4" | "auto"
    "remap": {},
    "swapSticks": false,
    "hideMappedButtons": true,
    "gyroStick": { /* Phase 10 GyroStickConfig, active for xbox360, dualSense, dualShock4, and auto */ }
  }
}
```

### Settings additions (`AppSettings`)

```jsonc
{
  "emulation": {
    "enabled": true,
    "hideController": true,
    "audio": true,
    "keepAudioDefaults": true,
    "hapticBuffer": 64,
    "speakerPacket": "single36",
    "gameModes": {                   // Record<string, "xbox360" | "dualSense" | "dualShock4" | "off">, default {}
      "eldenring.exe": "xbox360",
      "bloodborne-shadps4.exe": "dualShock4"
    }
  },
  "integration": {
    "udpEnabled": false,
    "udpPort": 6969,
    "dsx": true,
    "dsxPortFile": true,
    "releaseAfterSeconds": 0,
    "forzaEnabled": false,
    "forzaPort": 5555,
    "dsuEnabled": false,             // bool, default false (starts off)
    "dsuPort": 26760                 // int, default 26760, clamped to 1024..65535
  }
}
```

### Embedded native DualSense games resource (`src/Tactile.Core/Emulation/dualsense-games.json`)

> Plan review note: the executable names below are a starting draft and some are unverified. The loader trims and lower-cases every entry, drops duplicates and blanks, and a unit test asserts the shipped file is already in that canonical form (no leading spaces, all lower-case, sorted). Only add a name when its exe file name is confirmed (store page, PCGamingWiki "Executable" field or user report); a wrong or missing name only falls back to `xbox360`, and users fix it through `gameModes` overrides.

```jsonc
{
  "source": "PCGamingWiki (https://www.pcgamingwiki.com/wiki/List_of_games_that_support_DualSense)",
  "updated": "2026-11-22",
  "executables": [
    " AlanWake2.exe",
    "acmirage.exe",
    "acshadows.exe",
    "apex_legends.exe",
    "avatarfop.exe",
    "blackmythwukong.exe",
    "cod.exe",
    "cyberpunk2077.exe",
    "deathloop.exe",
    "ds.exe",
    "dyinglightgame_x64_rwdi.exe",
    "f1_23.exe",
    "f1_24.exe",
    "farcry6.exe",
    "ff7rebirth.exe",
    "ffxvi.exe",
    "forspoken.exe",
    "genshinimpact.exe",
    "ghostoftsushima.exe",
    "ghostwire.exe",
    "gow.exe",
    "gowragnarok.exe",
    "helldivers2.exe",
    "hfw.exe",
    "hitman3.exe",
    "hogwartslegacy.exe",
    "horizonzerodawnremastered.exe",
    "metroexodus.exe",
    "monsterhunterwilds.exe",
    "nfsunbound.exe",
    "overwatch.exe",
    "plaguetalerequiem_x64.exe",
    "ratchetandclank.exe",
    "re4.exe",
    "re8.exe",
    "returnal-win64-shipping.exe",
    "ackt.exe",
    "sackboy-win64-shipping.exe",
    "silenthill2-win64-shipping.exe",
    "spider-man.exe",
    "spider-man2.exe",
    "milesmorales.exe",
    "starwarsjedifallenorder.exe",
    "starwarsjedisurvivor.exe",
    "starwarsoutlaws.exe",
    "stellarblade.exe",
    "thelastofus-i.exe",
    "tlou-i.exe",
    "tlou-i-l.exe",
    "tlou-ii.exe",
    "tlou-ii-l.exe",
    "u4.exe",
    "u4-l.exe",
    "tll.exe",
    "tll-l.exe",
    "untildawn-win64-shipping.exe",
    "warframe.x64.exe",
    "witcher3.exe"
  ]
}
```

### Validation & security rules for new fields

- `EmulationConfig.Mode`: `Defined(e.Mode, EmulationMode.Off)` accepts `Off`, `Xbox360`, `DualSense`, `DualShock4`, and `Auto`.
- `EmulationSettings.GameModes`:
  - Keys are trimmed, reduced to file name via `Path.GetFileName(key.Trim())`, lowercased (`ToLowerInvariant()`), and rejected if empty, lacking a `.exe` suffix (normalized to include `.exe` if missing or must be a valid file name without path separators), or containing invalid path characters.
  - Values must be one of `EmulationMode.Xbox360`, `EmulationMode.DualSense`, `EmulationMode.DualShock4`, or `EmulationMode.Off` (`EmulationMode.Auto` or undefined enum values are dropped).
  - Capped at `MaxGameModeOverrides = 256` entries.
- `IntegrationSettings.DsuEnabled`: boolean, default `false`.
- `IntegrationSettings.DsuPort`: `Math.Clamp(integration.DsuPort <= 0 ? 26760 : integration.DsuPort, 1024, 65535)`.

---

## 3. Bridge (`src/Tactile.Host/Bridge.cs` ↔ `ui/src/host.ts`) Contract

Both Job B and Job C must implement these exact message shapes and property names:

### Host → UI messages

| Message `type` | Field / Shape | Description |
|---|---|---|
| `emulation` | `mode: EmulationMode` (`"off" \| "xbox360" \| "dualSense" \| "dualShock4"`), `configuredMode?: EmulationMode` (`"off" \| "xbox360" \| "dualSense" \| "dualShock4" \| "auto"`), `autoGame?: string \| null`, `slots: { index: number, mode: EmulationMode, attached: boolean, hidden: boolean, error?: string \| null }[]` | Existing message (`src/Tactile.Host/Bridge.cs:508-525`, `ui/src/host.ts:231-243`). `mode` reports the currently active/resolved pad kind (`"off" \| "xbox360" \| "dualSense" \| "dualShock4"`). When the profile's `emulation.mode` is `"auto"`, `configuredMode` is `"auto"` and `autoGame` is the settled foreground `.exe` name (or `null` on desktop). |
| `integration` | `{ type: "integration", udp: UdpStatus, forza: ForzaStatus, dsu: DsuStatus }` | Extends `PostIntegration` (`src/Tactile.Host/Bridge.cs:527-531`, `ui/src/host.ts:314`) with `dsu: { state: "off" \| "listening" \| "portBusy" \| "portConflict", port: number, clients: number, packets: number, rejected: number }`. |

### UI → Host messages

No new message `type` is required; profile `emulation.mode` (`"dualShock4"` / `"auto"`), `settings.emulation.gameModes`, and `settings.integration.dsuEnabled` / `dsuPort` save through the existing `profile.save` and `settings.save` messages (`src/Tactile.Host/Bridge.cs:135-144,231-233`), and `integration.status` / `emulation.status` refresh the status snapshots.

---

## 4. Work Split

Job A, Job B, and Job C have **strictly disjoint file sets**. Shared core/host model files (`Profile.cs`, `AppSettings.cs`, `Validation.cs`, `AppCore.cs`, `Bridge.cs`) are edited **only in Job B**. No job may touch `src/Tactile.Core/Mapping/**`. Job C (`ui/src/**`) can run in parallel with Jobs A and B.

| Job | Scope | Allowed files | Forbidden files | When |
|---|---|---|---|---|
| **A** | Virtual DualShock 4 USB device (`0x09CC`), descriptors, `0x01` input, `0x05` output, `0x02`/`0x12`/`0x81`/`0xA3` features, `Contracts.cs` & `UsbIpServer.cs`, DS4 unit tests | `src/Tactile.Emulation/**`, `tests/Tactile.Tests/Emulation/**` | `src/Tactile.Core/**`, `src/Tactile.Host/**`, `ui/**`, `docs/**` | First (or parallel with Job C; Job B consumes `VirtualKind.DualShock4` and `DualShock4Feedback` from `Contracts.cs`) |
| **B** | DSU/Cemuhook UDP server, `EmulationMode.Auto` & `DualShock4` wiring in `EmulationService`, embedded `dualsense-games.json`, `Profile.cs`, `AppSettings.cs`, `Validation.cs`, `AppCore.cs`, `Bridge.cs`, DSU & Auto unit tests | `src/Tactile.Core/Integration/**`, `src/Tactile.Core/Emulation/**`, `src/Tactile.Core/Model/Profile.cs`, `src/Tactile.Core/Model/AppSettings.cs`, `src/Tactile.Core/Storage/Validation.cs`, `src/Tactile.Core/AppCore.cs`, `src/Tactile.Core/Tactile.Core.csproj`, `src/Tactile.Host/Bridge.cs`, `tests/Tactile.Tests/Integration/**`, `tests/Tactile.Tests/EmulationCore/**`, `tests/Tactile.Tests/StoreTests.cs` | `src/Tactile.Emulation/**`, `src/Tactile.Core/Mapping/**`, `tests/Tactile.Tests/Emulation/**`, `ui/**`, `docs/**` | Parallel with Job C (with `Contracts.cs` from Job A) |
| **C** | React/TypeScript UI (`DualShock 4` & `Auto` mode selector, per-game override editor, DSU server card in Integrations), EN/TR strings, `sim.ts` | `ui/src/**` | `src/**`, `tests/**`, `docs/**` | Parallel with Jobs A & B |

### Job A (`src/Tactile.Emulation/**` + `tests/Tactile.Tests/Emulation/**`)

- **Files to create**:
  - `src/Tactile.Emulation/VirtualDualShock4.cs`: subclass of `VirtualUsbDevice` (`src/Tactile.Emulation/VirtualUsbDevice.cs:17`) implementing:
    - `Kind => VirtualKind.DualShock4`, `Speed => SpeedHigh`, `InEndpoint => 4`, `OutEndpoint => 3`, `IntervalMs => 4`.
    - `Strings => ["Sony Interactive Entertainment", "Wireless Controller"]` (plus `SecondaryPads.Serial(slot)` at index 3 when `slot != 0`, setting `DeviceDescriptor[16] = 3`).
    - 18-byte `DeviceDescriptor` (`VID 0x054C`, `PID 0x09CC`, `bcdDevice 0x0100`), 41-byte HID-only `ConfigDescriptor`, and DS4 USB `ReportDescriptor`.
    - `WriteInput(InputState s, byte sequence, Span<byte> report)` writing the 64-byte report `0x01` (sticks, D-Pad hat + face buttons, shoulder/system buttons, PS/Touchpad + 6-bit counter, analog L2/R2, `5.33 µs` timestamp `(ushort)(s.SensorTimestamp >> 4)`, raw gyro & accel, battery + USB flag `0x10`, and 2-point touchpad with Y clamped to `941` and inactive historical slots `0x80`).
    - `Out(ReadOnlySpan<byte> data, long timestamp)` and EP0 `SET_REPORT` (`reportType == 2 && id == 0x05`) parsing 32-byte DS4 output report `0x05` (flags at offset 1: bit `0x01` rumble `Small = offset 4, Large = offset 5`, bit `0x02` lightbar `R, G, B = offsets 6..8`) and raising `DualShock4Feedback`.
    - `ClassOrVendor` answering `GET_REPORT` Feature for `0x02` (37 B IMU calibration from `identity?.Calibration[1..35]` or `DefaultCalibration()`), `0x12` (16 B pairing MAC from `identity?.Pairing` or `DefaultPairing(slot)`), `0x81` (7 B reversed device MAC), `0xA3` (49 B firmware/hardware info with `hw_version = 0x0100`, `fw_version = 0x00000100`), and zero-filled stubs of exact descriptor length for all other declared DS4 feature IDs.
- **Files to edit**:
  - `src/Tactile.Emulation/Contracts.cs:6,14-22`: add `DualShock4` to `VirtualKind` (`public enum VirtualKind { Xbox360, DualSense, DualShock4 }`) and add `public sealed record DualShock4Feedback(long Timestamp, bool HasRumble, byte Large, byte Small, bool HasLightbar, Rgb Lightbar) : PadFeedback(Timestamp);`.
  - `src/Tactile.Emulation/UsbIpServer.cs:56-62`: add `VirtualKind.DualShock4 => new VirtualDualShock4(this, busNum, identity, slot),` to the `Create` switch.
  - `tests/Tactile.Tests/Emulation/VirtualPadTests.cs` & `tests/Tactile.Tests/Emulation/UsbIpServerTests.cs`: add unit tests for `VirtualDualShock4` descriptors, `WriteInput` byte layout, `0x05` output report (interrupt OUT and EP0 `SET_REPORT`), `0x02`/`0x12`/`0x81`/`0xA3` feature report sizes and values, secondary slot serial number, and full USB/IP loopback import + URB exchange.
- **Prohibitions**: Do not edit any file outside `src/Tactile.Emulation/**` and `tests/Tactile.Tests/Emulation/**`.

### Job B (`src/Tactile.Core/**`, `src/Tactile.Host/Bridge.cs`, `tests/Tactile.Tests/**` except `Emulation/**`)

- **Files to create**:
  - `src/Tactile.Core/Integration/DsuServer.cs`: UDP server on `127.0.0.1:dsuPort` implementing DSU protocol version `1001` (`0x100000`, `0x100001`, `0x100002`), 4-slot controller info and motion/input streaming, IEEE 802.3 CRC-32 validation via `Crc32.Compute`, 5-second client subscription timeout (`MaxClients = 8`), 500 pkt/s rolling rate limit, port-conflict detection against `UdpPort` and `ForzaPort`, and 5-second retry on `PortBusy`.
  - `src/Tactile.Core/Emulation/AutoEmulationResolver.cs`: loads the embedded `dualsense-games.json` resource once into a `FrozenSet<string>` (or `HashSet<string>(StringComparer.OrdinalIgnoreCase)`), exposes `IReadOnlySet<string> BuiltInDualSenseGames`, and resolves `(EmulationMode configuredMode, string? settledExe, IReadOnlyDictionary<string, EmulationMode>? gameModes)` to a concrete `EmulationMode` (`Off`, `Xbox360`, `DualSense`, or `DualShock4`).
  - `src/Tactile.Core/Emulation/dualsense-games.json`: embedded resource JSON listing native DualSense PC game executables with PCGamingWiki source attribution.
  - `tests/Tactile.Tests/Integration/DsuServerTests.cs`: unit tests for DSU server packets, CRC-32, 4 slots, gyro (`deg/s`) and accel (`g`) conversion, microsecond timestamp, client timeout, rate limiting, malformed datagram rejection, and port busy / port conflict states.
- **Files to edit**:
  - `src/Tactile.Core/Tactile.Core.csproj`: embed `Emulation\dualsense-games.json` as an `<EmbeddedResource>`.
  - `src/Tactile.Core/Model/Profile.cs:18`: extend `EmulationMode` to `public enum EmulationMode { Off, Xbox360, DualSense, DualShock4, Auto }`.
  - `src/Tactile.Core/Model/AppSettings.cs:44-66`: add `bool DsuEnabled` (default `false`) and `int DsuPort` (default `26760`) to `IntegrationSettings`; add `IReadOnlyDictionary<string, EmulationMode> GameModes` (default empty dictionary) to `EmulationSettings`.
  - `src/Tactile.Core/Storage/Validation.cs:240-255,262-316`: normalize `Integration.DsuEnabled`, `Integration.DsuPort` (`1024..65535`, default `26760` when `<= 0`), and `Emulation.GameModes` (clean `.exe` file names, valid non-`Auto` `EmulationMode` values, capped at `256`).
  - `src/Tactile.Core/Integration/IntegrationStatus.cs:5-18`: add `public enum DsuListenerState { Off, Listening, PortBusy, PortConflict }`, `public sealed record DsuStatus(DsuListenerState State, int Port, int Clients, long Packets, long Rejected)`, and extend `IntegrationStatus` to `public sealed record IntegrationStatus(UdpStatus Udp, ForzaStatus Forza, DsuStatus? Dsu = null)`.
  - `src/Tactile.Core/Emulation/EmulationService.cs:11-35,195-228,493-538,738-788,801-855,954-973,1014-1018`:
    - Accept `string? foregroundExe = null` in `Update(...)` / `UpdateWanted(...)`, resolving `EmulationMode.Auto` via `AutoEmulationResolver.Resolve` while remembering the last settled game executable so focus shifts to non-game windows do not flap the virtual controller.
    - Map `EmulationMode.DualShock4` to `VirtualKind.DualShock4` in `WantedKindLocked`, `Start`, and `BuildStatus` (and expose `ConfiguredMode` and `AutoGame` on `EmulationStatus`).
    - In `StopLocked()`, submit a neutral `InputState` (`Buttons.None`, triggers `0`, sticks `128, 128`) to the pad before detaching, and clear both `XboxLayer` and `GameLayer`.
    - In `OnFeedback`, handle `DualShock4Feedback`: update `_gameFields` (`OutputFields.Rumble` when `HasRumble`, `OutputFields.Lightbar` when `HasLightbar`) and `_game` (`RumbleLeft = Large, RumbleRight = Small`, `Lightbar`), applying `GameOutput.Overlay` onto `GameLayer` (or `secondary.SetFeedback` for secondary slots).
  - `src/Tactile.Core/Emulation/EmulationSlots.cs:145-151,181-186`: pass settled foreground exe to secondary `UpdateWanted` (if a secondary profile uses `Auto`) and report `EmulationMode.DualShock4` in `BuildSlots` when `slot._kind == VirtualKind.DualShock4`.
  - `src/Tactile.Core/AppCore.cs:79-85,153-169,233,498-510,557-571,635-638,799-801,883-915`:
    - Create and dispose `DsuServer _dsu`; feed primary and secondary controller inputs (`InputState`, calibrated `gyroDegPerSec`, `accelG`, slot metadata) to `_dsu.PublishInput(...)`; configure `_dsu` in `ConfigureIntegration()` (handling port conflicts between `UdpPort`, `ForzaPort`, and `DsuPort`).
    - Pass `_selector.SettledForeground` into `_emulation?.Update(...)`; when the resolved virtual pad mode changes across an auto/profile transition, call `_mapper.ResetController()` so no mapped key stays held.
  - `src/Tactile.Host/Bridge.cs:508-531`: include `configuredMode` and `autoGame` in `PostEmulation()` and `status.Dsu` in `PostIntegration()`.
  - `tests/Tactile.Tests/EmulationCore/EmulationServiceTests.cs` & `tests/Tactile.Tests/StoreTests.cs`: add tests for `EmulationMode.DualShock4` (including over USB physical DualSense and `DualShock4Feedback` rumble + lightbar overlay), `EmulationMode.Auto` resolution (built-in DualSense games, fallback to Xbox 360, user `gameModes` overrides, no flapping, neutral input on mode switch), and settings/profile normalization.
- **Prohibitions**: Do not edit any file under `src/Tactile.Emulation/**`, `src/Tactile.Core/Mapping/**`, `tests/Tactile.Tests/Emulation/**`, or `ui/**`.

### Job C (`ui/src/**`)

- **Files to edit**:
  - `ui/src/host.ts:212,225,231-243,264-272,314`:
    - Extend `EmulationMode` to `"off" | "xbox360" | "dualSense" | "dualShock4" | "auto"`.
    - Add `gameModes: Record<string, Exclude<EmulationMode, "auto">>` to `EmulationSettings`.
    - Add `configuredMode?: EmulationMode` and `autoGame?: string | null` to `EmulationStatus`.
    - Add `dsuEnabled: boolean` and `dsuPort: number` to `IntegrationSettings`.
    - Add `DsuStatus` (`{ state: "off" | "listening" | "portBusy" | "portConflict"; port: number; clients: number; packets: number; rejected: number }`) and `dsu?: DsuStatus` to `IntegrationStatus`.
  - `ui/src/model.ts:155-160,206-214,336-366`:
    - Update `defaultSettings` with `emulation.gameModes: {}` and `integration.dsuEnabled: false, dsuPort: 26760`.
    - Update `normalizeProfile` to accept `"dualShock4"` and `"auto"` in `emulation.mode`.
    - Update `normalizeSettings` to normalize `emulation.gameModes` and clamp `integration.dsuPort` (`1024..65535`).
  - `ui/src/pages/Emulation.tsx:20,54-70,80-131,230-274`:
    - Add `"dualShock4"` (`"DualShock 4"`) and `"auto"` (`L("Auto (by game)", "Otomatik (oyuna göre)")`) to `modeLabel` and the profile mode `Segmented` control (note: `dualShock4` and `auto` are enabled on both USB and Bluetooth; only explicit `dualSense` is disabled when on USB).
    - When `configuredMode === "auto"` (or draft `c.mode === "auto"`), show a status/hint line indicating which mode was chosen for the current foreground game (e.g. `L("Auto picks DualSense for supported games and Xbox 360 for everything else.", "Otomatik kip desteklenen oyunlarda DualSense, diğerlerinde Xbox 360 seçer.")` + live resolved mode badge in `LiveStatus`).
    - Add a **Per-game virtual controller overrides** (`L("Per-game overrides (Auto mode)", "Oyuna göre özel seçimler (Otomatik kip)")`) card in `SystemPanel` (or below remapping) allowing the user to add/remove `.exe` → `DualSense | Xbox 360 | DualShock 4 | Off` overrides, with a button to pick from `foreground.pick` (`recent` executables) or type an `.exe` name.
  - `ui/src/pages/Integrations.tsx:61-127`:
    - Add a **DSU / Cemuhook (Emulators)** (`L("Motion server (DSU / Cemuhook)", "Hareket sunucusu (DSU / Cemuhook)")`) card in `Connections`:
      - Toggle `dsuEnabled` (`L("DSU motion server", "DSU hareket sunucusu")`) with hint `L("Streams gyro, accelerometer, touchpad, and buttons to Cemu, Dolphin, Ryujinx, and Yuzu on 127.0.0.1.", "Gyro, ivmeölçer, touchpad ve tuşları 127.0.0.1 üzerinden Cemu, Dolphin, Ryujinx ve Yuzu'ya aktarır.")`.
      - `PortField` for `dsuPort` (default `26760`, warning if `dsuPort === s.udpPort || dsuPort === s.forzaPort`).
      - Status line showing `Off` / `Listening on :26760` / `Port in use` / `Port conflict`, connected client count (`L("{n} clients", "{n} istemci")`), and sent packet count.
  - `ui/src/sim.ts:321-338,361-392`:
    - Simulate `dsu` status in `sendIntegration()` (`listening` when `dsuEnabled`, `portConflict` if port matches `udpPort` or `forzaPort`, sample `clients: 1`, incrementing `packets`).
    - Simulate `"dualShock4"` and `"auto"` in `emulationStatus()` (resolving `"auto"` against `GhostOfTsushima.exe` → `"dualSense"`, `ForzaHorizon5.exe` → `"xbox360"`, and respecting `settings.emulation.gameModes`).
- **Prohibitions**: Do not edit any file under `src/` or `tests/`. Every user-visible string must have both English and Turkish via `L(en, tr)` (`ui/src/i18n.ts:16-18`).

---

## 5. Acceptance Criteria and Test List

### Measurable acceptance criteria

1. `dotnet build` and `dotnet test` pass with **0 warnings and 0 errors**; all existing **659+ tests** pass without changing their assertions, plus all new Phase 12 unit tests.
2. `npm run build` in `ui/` succeeds with **0 TypeScript or Vite errors**.
3. Legacy profile and settings JSONs from Phases 2-11 deserialize with `version: 1`, `dsuEnabled: false`, `dsuPort: 26760`, and `gameModes: {}`.
4. Virtual DualShock 4 (`VirtualDualShock4`) exposes `VID 0x054C / PID 0x09CC`, 41-byte HID-only configuration descriptor, valid DS4 report descriptor whose declared Feature report sizes match `_features` byte-for-byte, 64-byte input report `0x01`, 32-byte output report `0x05` (forwarding rumble and lightbar RGB to the physical DualSense), and non-zero `0x02`, `0x12`, `0x81`, and `0xA3` feature reports.
5. `DsuServer` binds only to `127.0.0.1`, starts disabled by default, answers `0x100000`, `0x100001`, and `0x100002` with valid IEEE 802.3 CRC-32, streams all 4 slots with `g` accelerometer and `deg/s` gyroscope floats, expires silent clients after `5 s`, enforces the `500 pkt/s` rate limit, and survives arbitrary malformed packets without throwing.
6. `EmulationMode.Auto` resolves executables in `settings.emulation.gameModes` first, then the embedded `dualsense-games.json` (`DualSense`), and defaults to `Xbox360`; switching virtual pad modes submits a neutral `InputState` before detaching and releases all mapped keys/actions.
7. Zero files under `src/Tactile.Core/Mapping/**` are modified.

### Required unit tests (`tests/Tactile.Tests/`)

- `tests/Tactile.Tests/Emulation/VirtualPadTests.cs` & `tests/Tactile.Tests/Emulation/UsbIpServerTests.cs` *(Job A)*:
  - `DualShock4_descriptors_and_feature_reports_match_declared_sizes`: verifies 18-byte device descriptor (`054C:09CC`), 41-byte config descriptor, report descriptor length in HID descriptor (`Config[25..27]`), and verifies that every feature report ID (`0x02` 37 B, `0x12` 16 B, `0x81` 7 B, `0xA3` 49 B, etc.) returns the exact byte length declared in the report descriptor, while unknown feature IDs stall (`EPipe`).
  - `DualShock4_default_and_identity_calibration_and_version_are_valid`: verifies `0x02` has non-zero plus/minus calibration divisors (no divide-by-zero in SDL/Steam), `0x12` and `0x81` contain the reversed controller MAC, and `0xA3` reports non-zero `hw_version` (`0x0100`) and `fw_version` (`0x00000100`).
  - `DualShock4_input_report_packs_buttons_sticks_triggers_imu_touch_and_battery`: verifies all byte offsets `0..63` of report `0x01`, including 6-bit sequence counter at `report[7] >> 2`, `5.33 µs` timestamp at `report[10..12]`, gyro/accel at `13..24`, USB cable bit `0x10` + battery level at `30`, Touch 1/2 12-bit coordinates with `Y <= 941` at `35..42`, and inactive markers `0x80` in `43..60`.
  - `DualShock4_output_report_raises_rumble_and_lightbar_feedback`: tests interrupt OUT and EP0 `SET_REPORT` `0x05` (32-byte and 31-byte forms) with rumble-only (`0x01`), lightbar-only (`0x02`), and combined (`0x03`) flags, and verifies malformed/wrong-ID reports are ignored.
  - `Full_dualshock4_session_over_loopback`: imports a virtual DS4 over loopback USB/IP, reads descriptors and feature `0x02`, submits input `0x01`, sends output `0x05`, and verifies `DualShock4Feedback`.
- `tests/Tactile.Tests/Integration/DsuServerTests.cs` *(new, Job B)*:
  - `Answers_version_and_controller_info_for_four_slots_with_valid_crc`: sends `0x100000` and `0x100001` for slots `0..3` (with slot 0 and slot 1 connected, slots 2-3 disconnected), verifies `"DSUS"` header, protocol `1001`, IEEE 802.3 CRC-32, connection type, MAC, and battery byte.
  - `Streams_motion_and_input_to_subscribed_clients_and_expires_after_5s`: subscribes via `0x100002` (all-slots, slot-filtered, and MAC-filtered), publishes an `InputState` with known sticks, buttons, triggers, touchpad points, `SensorTimestamp`, `AccelG = (0, 1, 0)`, and `GyroDegPerSec = (90, -45, 15)`, verifies the 100-byte packet payload offsets `0..79` (including float32 `g` and `deg/s` values and monotonic `µs` timestamp), advances fake clock by `5001 ms`, and verifies no further packets are sent until re-subscribed.
  - `Rejects_malformed_bad_crc_and_rate_limited_packets_without_stopping`: sends short (< 20 B), oversized (> 1024 B), wrong magic, wrong version, bad CRC-32, and > 500 packets/sec bursts; verifies `Status.Rejected` increments and the server continues answering valid requests.
  - `Port_busy_and_port_conflict_with_udp_or_forza_are_reported`: verifies `DsuListenerState.PortConflict` when `DsuPort == UdpPort` or `ForzaPort`, and `DsuListenerState.PortBusy` + 5 s retry when another socket holds the port.
- `tests/Tactile.Tests/EmulationCore/EmulationServiceTests.cs` & `tests/Tactile.Tests/StoreTests.cs` *(Job B)*:
  - `DualShock4_mode_attaches_over_bluetooth_and_usb_and_overlays_rumble_and_lightbar`: verifies `EmulationMode.DualShock4` attaches `VirtualKind.DualShock4` on both Bluetooth and USB physical controllers, and `DualShock4Feedback` updates `GameLayer` rumble (scaled by `Rumble.Strength`) and lightbar RGB.
  - `Auto_mode_resolves_built_in_dualsense_games_user_overrides_and_xbox_fallback`: verifies `AutoEmulationResolver` picks `DualSense` for `"ghostoftsushima.exe"` / `"returnal-win64-shipping.exe"`, `Xbox360` for unknown `"eldenring.exe"`, and respects `settings.emulation.gameModes` overrides (`"eldenring.exe" -> DualShock4`, `"ghostoftsushima.exe" -> Xbox360`, `" Returnal-Win64-Shipping.exe " -> Off`).
  - `Auto_mode_reattaches_only_when_settled_game_changes_and_submits_neutral_input_first`: verifies switching from a DualSense game to an Xbox 360 game submits a neutral `InputState` (buttons `None`, triggers `0`, sticks `128, 128`) before detaching the old pad and reattaching the new pad, and does not flap when foreground briefly reports null/explorer while the settled game is unchanged.
  - `Backwards_compatibility_and_validation_for_phase12_settings_and_profiles`: verifies legacy JSON loading, `dsuPort` clamping (`1024..65535`), and `gameModes` key/value normalization in `StoreTests.cs`.

---

## 6. Safety Rules (Every Job)

- **No hardware or OS injection**: Never run code that sends output to a real controller, injects keyboard/mouse input (`SendInput`), attaches/hides real devices via `usbip-win2` or `HidHide`, or installs drivers. The application executable is never started during development. All tests use fake `DriverPlatform`, fake `InputPlatform`, fake `IForegroundSource`, `ManualTime`, and ephemeral loopback ports (`127.0.0.1:0`).
- **Loopback only**: Both `UsbIpServer` and `DsuServer` must bind exclusively to `IPAddress.Loopback` (`127.0.0.1`), never `IPAddress.Any` (`0.0.0.0`) or `IPAddress.IPv6Any` (`::`), and `DsuServer` must reject any packet whose `RemoteEndPoint` is not a loopback address.
- **Allowed commands**: Only `dotnet build`, `dotnet test`, and `npm run build` (in `ui/`).
- **No commits**: Do not run `git commit`, `git push`, or modify git history.
- **Stuck-input prevention**: Any virtual pad mode switch (`Xbox360` ↔ `DualSense` ↔ `DualShock4` ↔ `Off`), detach, profile switch, or controller disconnect must submit a neutral virtual pad state before removal and release all held mapped keys, mouse buttons, macros, toggles, and turbo outputs.

---

## 7. Hardware Test Checklist (KH — End of Phase, ~15 min)

1. **Virtual DualShock 4 in `joy.cpl` and Steam/SDL**: Set the active profile's Virtual Controller mode to `DualShock 4` → verify Windows `joy.cpl` shows `"Wireless Controller"` with working axes, D-Pad, and 14 buttons; open Steam controller test (or an SDL2/SDL3 game) and verify gyro, touchpad cursor/click, rumble, and lightbar color changes sent by the game appear on the physical DualSense.
2. **Virtual DualShock 4 in a DS4-native game**: Launch a game that supports DualShock 4 over USB (e.g. a Sony/Unity/Ubisoft title or shadPS4) with `Hide physical controller` on → verify PlayStation button prompts appear and only one controller is seen by the game.
3. **DSU / Cemuhook motion server in Cemu or Dolphin**: Enable `DSU motion server` (`127.0.0.1:26760`) on the Integrations page → open Cemu (`Input Settings → DSUController`) or Dolphin (`Controller Settings → Alternative Input Sources → DSU Client`), verify the controller slot appears with battery status, and tilt the DualSense in the motion preview to verify pitch, yaw, roll, and accelerometer axes move in the right directions.
4. **Automatic virtual controller switching (`Auto` mode)**: Set the default profile's Virtual Controller mode to `Auto (by game)` → launch a native DualSense game from the list (e.g. *Ghost of Tsushima* or *Returnal*) while on Bluetooth and verify TriggerForge attaches a virtual `DualSense` with game haptics/triggers; close it and launch a non-DualSense game (e.g. *Forza Horizon 5* or *Elden Ring*) while holding a button → verify held inputs release cleanly and TriggerForge switches the virtual pad to `Xbox 360`.
5. **Per-game override in `Auto` mode**: In Settings / Virtual Controller, add a per-game override setting that game's `.exe` to `DualShock 4` (or `Off`), focus the game again → verify `Auto` mode switches to the overridden virtual controller type immediately.

---

## 8. Risks and Rollback

- **Risk — Windows `hidclass` rejecting `GET_REPORT` Feature replies if report descriptor and `_features` lengths diverge**:
  - *Mitigation*: `VirtualDualShock4` defines `FeatureSizes` directly from the report descriptor's `85 <id> ... 95 <count> B1 02` items (size = `count + 1` including the report ID byte) and a dedicated unit test parses the report descriptor bytes to assert every declared Feature ID exists in `_features` with the exact length.
- **Risk — DSU gyro/accelerometer or stick Y axis inversion in Cemu/Dolphin**:
  - *Mitigation*: DSU stick `LY`/`RY` invert DualSense top-left-origin `0..255` (`255 - Y` so up = `255` per DSU spec), accelerometer outputs calibrated `g` (`+Y` up / `+Z`gravity per `ImuCalibration.AccelG`), and gyro outputs calibrated `deg/s` (`Pitch, Yaw, Roll`). KH hardware checklist item 3 verifies axis signs directly in Dolphin/Cemu motion preview.
- **Risk — Virtual controller hot-plug flapping during `Alt+Tab` in `Auto` mode**:
  - *Mitigation*: `Auto` mode tracks the last settled game executable (`_selector.SettledForeground` after `AutoProfileDelayMs`) and only switches the virtual pad when a new foreground executable settles or the user edits the profile / `gameModes` overrides, and submits a neutral `InputState` + `_mapper.ResetController()` before every detach.
- **Risk — Merge conflicts with concurrent Phase 11 work**:
  - *Mitigation*: Phase 12 touches zero files in `src/Tactile.Core/Mapping/**` and does not modify `EmulationInput.MappedButtons`.
- **Rollback switches**:
  - Setting `integration.dsuEnabled: false` (the default) completely stops the DSU socket and worker thread.
  - Choosing an explicit `emulation.mode` (`off`, `xbox360`, or `dualSense`) bypasses `AutoEmulationResolver` and `VirtualDualShock4`, restoring exact Phase 8-10 virtual controller behavior.
