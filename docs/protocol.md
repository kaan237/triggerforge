# DualSense protocol notes

Working notes on how the controller talks over HID. Values were checked against
public driver sources and community research; the code in this repo is written
from these notes. Anything marked **unverified** must be confirmed on hardware
before code depends on it.

## Identity

| Device | VID | PID |
|---|---|---|
| DualSense | `054C` | `0CE6` |
| DualSense Edge | `054C` | `0DF2` |

## Checksums (Bluetooth only)

CRC-32, IEEE polynomial (reflected `0xEDB88320`), init `0xFFFFFFFF`, final XOR.
A one-byte seed is fed in before the report bytes:

| Direction | Seed |
|---|---|
| Input reports | `0xA1` |
| Output reports | `0xA2` |
| Feature reports | `0xA3` |

The CRC covers the report from its ID byte up to the last 4 bytes, and is stored
little-endian in those last 4 bytes. A bad CRC on output is dropped silently by
the controller.

## Input

| Link | Report ID | Size | State block starts at |
|---|---|---|---|
| USB | `0x01` | 64 | byte 1 |
| Bluetooth | `0x31` | 78 | byte 2 (byte 1 is a sequence/tag byte) |

Bluetooth starts in a reduced mode (short `0x01` reports). Reading feature report
`0x05` switches it to full `0x31` reports. Verified 2026-11-18: 7 `0x01` reports,
then only `0x31` after the feature read; all 13 646 `0x31` CRCs valid.

On Bluetooth, byte 1 of `0x31` is the sequence (high nibble, +0x10 per report) and
the state block's own counter (offset 6) stays constant. Observed rate over BT:
~800 reports/s (median interval 1.25 ms). Once switched, the controller stays in
full mode for the rest of the connection (second capture had no `0x01` at all).

Checked on hardware (BT, 2026-11-18): sticks 0-255 at offsets 0-3, face buttons and
hat at offset 7, L3/R3 at offset 8 bits 6-7, gyro/accel at 15-26, battery nibble at
52. Offsets 11-12 and 55-62 change every report (counters or timestamps, unused).
Also checked: L1/R1/L2/R2 at offset 8 bits 0-3, L2/R2 analog 0-255 at 4-5,
touchpad click at offset 9 bit 1, two touch points (X 0-1919, Y up to ~1000 seen).
Offset 40 changes with L2 travel (94/130 while pressed); offsets 41-42 read 9 with
no trigger effect set. Meaning not settled; revisit when effects are written.
Not yet seen: PS and mic buttons.

One report in 13 304 failed CRC in a normal session: drop reports that fail CRC.

State block (63 bytes, offsets relative to block start):

| Offset | Size | Field |
|---|---|---|
| 0 | 1 | Left stick X (0-255, 128 centre) |
| 1 | 1 | Left stick Y |
| 2 | 1 | Right stick X |
| 3 | 1 | Right stick Y |
| 4 | 1 | L2 analog |
| 5 | 1 | R2 analog |
| 6 | 1 | Sequence counter |
| 7 | 1 | D-pad hat (low nibble, 8 = released) + □ ✕ ○ △ (bits 4-7) |
| 8 | 1 | L1, R1, L2, R2, Create, Options, L3, R3 (bits 0-7) |
| 9 | 1 | PS (bit 0), touchpad click (bit 1), mic (bit 2); Edge: Fn/paddles in upper bits |
| 10 | 1 | reserved |
| 11 | 4 | reserved |
| 15 | 6 | Gyro pitch, yaw, roll (int16 LE) |
| 21 | 6 | Accel X, Y, Z (int16 LE) |
| 27 | 4 | Sensor timestamp (uint32 LE) |
| 31 | 1 | reserved |
| 32 | 4 | Touch point 1 |
| 36 | 4 | Touch point 2 |
| 40 | 12 | reserved (adaptive trigger status lives here, layout **unverified**) |
| 52 | 1 | Battery level (low nibble, 0-10) + charge state (high nibble) |
| 53 | 1 | Headphone (bit 0), mic jack (bit 1), mic muted (bit 2) |
| 54 | 1 | status, unused for now |
| 55 | 8 | reserved |

Touch point (4 bytes): byte 0 bit 7 set = no finger, bits 0-6 = touch id.
X is 12 bits (`b1 | (b2 & 0x0F) << 8`), Y is 12 bits (`(b2 >> 4) | b3 << 4`).
Pad size 1920 × 1080.

## Output

| Link | Report ID | Size | Layout |
|---|---|---|---|
| USB | `0x02` | 63 | ID, 47-byte state, 15 zero bytes |
| Bluetooth | `0x31` | 78 | ID, seq byte (`seq << 4`), tag `0x10`, 47-byte state, 24 zero bytes, CRC |

47-byte state block:

| Offset | Field |
|---|---|
| 0 | Flags A: compatible rumble (bit 0), rumble instead of haptics (bit 1), R2 effect (bit 2), L2 effect (bit 3), headphone vol (bit 4), speaker vol (bit 5), mic vol (bit 6), audio control (bit 7) |
| 1 | Flags B: mic LED (bit 0), power save / mute (bit 1), lightbar colour (bit 2), reset LEDs (bit 3), player LEDs (bit 4), haptic low-pass (bit 5), motor power (bit 6), audio control 2 (bit 7) |
| 2 | Right motor (high frequency) |
| 3 | Left motor (low frequency) |
| 4 | Headphone volume (0-0x7F) |
| 5 | Speaker volume (0-0xFF) |
| 6 | Mic volume (0-0x40) |
| 7 | Audio control (output path in bits 4-5) |
| 8 | Mic LED: 0 off, 1 on, 2 pulse |
| 9 | Power save control (bit 4 = mute mic) |
| 10-20 | R2 trigger effect (11 bytes) |
| 21-31 | L2 trigger effect (11 bytes) |
| 32-35 | reserved |
| 36 | reserved |
| 37 | Audio control 2 (speaker preamp gain bits 0-2) |
| 38 | Flags C: LED brightness (bit 0), lightbar setup / fade (bit 1), improved rumble emulation (bit 2) |
| 39-40 | reserved |
| 41 | Lightbar setup (bit 1 = fade out / release) |
| 42 | LED brightness (0 high, 1 mid, 2 low) |
| 43 | Player LEDs (5 bits, bit 5 = instant) |
| 44 | Lightbar red |
| 45 | Lightbar green |
| 46 | Lightbar blue |

## Trigger effects

Each trigger takes 11 bytes: byte 0 is the mode, bytes 1-10 are parameters.
The trigger travel is split into 10 zones (0 = top, 9 = fully pressed).

| Mode | Byte | Parameters |
|---|---|---|
| Off | `0x05` | none |
| Feedback | `0x21` | bytes 1-2: active zone mask (10 bits); bytes 3-6: 3-bit force per zone (force-1) |
| Weapon | `0x25` | bytes 1-2: mask with start and end zone bits; byte 3: force-1 |
| Vibration | `0x26` | bytes 1-2: zone mask; bytes 3-6: 3-bit amplitude per zone; byte 9: frequency (Hz) |
| Bow | `0x22` | start/end mask; byte 3-4: force and snap force (3 bits each) |
| Galloping | `0x23` | start/end mask; byte 3: two foot timings (3 bits each); byte 4: frequency |
| Machine | `0x27` | start/end mask; two amplitudes, frequency, period |
| Legacy | `0x01`, `0x02`, `0x06` | older simple modes, no range checks in firmware |

Bow, Galloping and Machine are not used by Sony's own SDK and may change with
firmware. Valid ranges: zone 0-9, force/amplitude 1-8 (stored as 0-7).

## Feature reports

| ID | Size | Content |
|---|---|---|
| `0x05` | 41 | IMU calibration: gyro bias (3 × int16), gyro plus/minus per axis, gyro speed plus/minus, accel plus/minus per axis |
| `0x09` | 20 | Pairing info, controller MAC at bytes 1-6 (reversed) |
| `0x20` | 64 | Firmware info: build date/time strings, hardware and firmware versions |

On Bluetooth, feature replies end with a CRC using seed `0xA3`. Windows reports a
547-byte feature length over BT; the real payload is shorter and the CRC sits
right after it (verified: `0x05` CRC at bytes 37-40, `0x20` at 60-63).

## Bluetooth haptics and speaker

Over USB, haptics are a 4-channel 48 kHz audio stream: channels 1-2 go to the
speaker, channels 3-4 drive the two voice-coil actuators. Over Bluetooth there is
no audio endpoint, so samples are packed into large output reports.

Timing: one packet every 512 samples at 48 kHz = 10.667 ms.

**Haptics only — report `0x32`, 142 bytes**

| Offset | Content |
|---|---|
| 0 | `0x32` |
| 1 | sequence (`seq << 4`) |
| 2-3 | sub-packet `0x11` header (`0x91`, length) |
| 4-7 | session flags, two buffer length bytes, packet counter |
| 8-9 | sub-packet `0x12` header (`0x92`, 64) |
| 10-73 | 32 stereo frames of signed 8-bit samples at 3 kHz |
| 138-141 | CRC |

The 3 kHz stream comes from the 48 kHz channels 3-4 by averaging each group of 16
samples.

**Opening the stream (verified on hardware 2026-11-18).** Haptic reports alone do
nothing; the controller ignores them until the session is opened:

1. Main `0x31` report with rumble flags cleared (Flags A bits 0-1, Flags C bit 2)
   and audio fields enabled: headphone/speaker volume, audio control = `0x30`
   (speaker path), audio control 2 = `0x02` (preamp gain).
2. One `0x32` "prime": byte 1 = `0x10`, sub-packet `0x10` header `0x90 0x3F`, then the
   same 47-byte state block (63-byte sub-packet, zero tail), CRC.
3. About 8 silent haptic reports at the normal cadence, then real samples.

Classic rumble is ignored while the stream is open; restore the rumble flags to
return to motor rumble.

**Combined reports**

| ID | Size | Carries |
|---|---|---|
| `0x35` | 334 | speaker audio only |
| `0x36` | 398 | state block + haptics + speaker audio |
| `0x39` | 547 | two haptic frames + two audio frames |

Speaker audio is Opus, 48 kHz stereo, 10 ms frames, constant bitrate padded to
200 bytes per frame. The 512-sample block is resampled to 480 before encoding.

Exact sub-packet flag values come from community reverse engineering and are not
documented by Sony; treat firmware updates as a risk.

**Speaker + haptics — report `0x36`, 398 bytes** (sources read 2026-11-19: dualsense-companion
`media.py` (MIT), LinuxAudio4Dualsense5 README (AGPL, facts only); not verified on our
hardware yet). Offsets from the report id:

| Offset | Content |
|---|---|
| 0 | `0x36` |
| 1 | sequence (`seq << 4`), same counter rules as `0x32` |
| 2-3 | sub-packet `0x11` header: `0x91`, length 7 |
| 4 | stream mask: `0xFE` = speaker (+ haptics), `0xFF` = also microphone |
| 5-9 | five buffer bytes, all `0x40` |
| 10 | packet counter (mod 256) |
| 11-12 | sub-packet `0x12` header: `0x92`, 64 |
| 13-76 | 32 haptic frames, s8 stereo, as in `0x32` |
| 77-78 | sub-packet `0x13` header: `0x93`, 200 (`0x96` instead routes to the headphone jack) |
| 79-278 | one Opus frame, exactly 200 bytes |
| 279-393 | zero |
| 394-397 | CRC32, seed `0xA2`, little-endian |

One source puts an extra `0x90` state sub-packet (63 bytes) between the config and the
haptics, shifting the rest by 65 bytes; the other (tested against a PS5 capture) has no state
block. We default to **no state block** and keep the other as a switch for hardware testing.

Opus for the speaker: 48 kHz, stereo (internal speaker is mono: same signal on L and R),
480-sample (10 ms) frames, CBR 160 kbit/s = 200 bytes per frame. Each 10.667 ms tick takes
512 captured samples and linearly resamples them to 480 before encoding, so one frame goes
out per tick and the controller plays it at its own pace. Pacing at exactly 10 ms instead
underruns/stutters.

Main report settings for the speaker (0x31 common block): audio-control and speaker-volume
enable flags, speaker volume (0-100 used in practice), audio path `0x30` (speaker), audio
control 2 = `0x02` (preamp). This is what the haptic prime already sends.

`0x35` (334 bytes) is named in one source as audio + haptics but no layout was found; we do
not use it.

**Two frames per report — `0x39`, 547 bytes** (dualsense-companion `media.py`, MIT, derived
from a PS5 capture; facts only). One report every 21.333 ms carries two ticks:

| Offset | Content |
|---|---|
| 0 | `0x39` |
| 1 | sequence (`seq << 4`) |
| 2-3 | sub-packet `0x11` header: `0x91`, length 6 |
| 4 | flags `0x7E` (bit 0 = microphone, `0x7F`) |
| 5-8 | `0x51 0x1F 0x62 0x3C` (last byte = speaker buffer depth; the PS5 sends `0x28`, `0x3C` measured cleaner) |
| 9 | packet counter (mod 256) |
| 10-11 | sub-packet `0x15` header: `0xD5` (`0xD6` = headphone jack), 200 |
| 12-211 | Opus frame 1 (200 bytes) |
| 212-411 | Opus frame 2 (200 bytes) |
| 412-413 | sub-packet `0x12` header: `0xD2`, 64 |
| 414-477 | haptic block 1 (32 frames s8 stereo) |
| 478-541 | haptic block 2 |
| 542 | zero |
| 543-546 | CRC32, seed `0xA2`, little-endian |

Hardware note (2026-11-19, ours): with `0x36` every 10.667 ms the speaker gives about one
audible dropout every 2-3 s; the source above measured ~20 dropouts / 5 s for `0x36` against
~3 / 5 s for `0x39` on Bluetooth stalls. `0x39` (547 bytes) carries two Opus frames and two haptic blocks every 21.3 ms
(`0xD5` speaker, `0xD2` haptics sub-ids); one source reports it survives Bluetooth stalls
better than `0x36`. Kept as a later option.

## Bluetooth microphone (unverified on hardware)

Experimental and off by default. The layout below comes from community notes and has not been
checked against a real controller yet; treat every field as a hypothesis until the hardware test passes.

- Enabling: main `0x31` report with Flags A bit 6 (mic volume enable), byte 6 = gain (`0x00`-`0x40`),
  Flags B bit 1 (power save enable); bit 0 of the `0x91` stream flags in the `0x32`/`0x36`/`0x39` reports,
  or a standalone `0x32` report with `0x91` length 7 and flags `0xFF` (on) / `0xFE` (off). Whether the
  standalone form works without a running haptic stream is unknown.
- Input: a `0x31` report with bit 1 of byte 1 set replaces the state block with a sequence byte
  (byte 2) and one 71-byte 10 ms Opus frame (48 kHz, mono) in bytes 3-73. The trailing CRC is not
  assumed valid. The app only takes such a report as a microphone frame while it has asked for the
  stream; otherwise the bit is ignored and the report is checked as a state report.

## Virtual device (for games)

To make games see a wired controller while the real one is on Bluetooth, the app
presents a virtual USB DualSense:

- A USB/IP server runs inside the app on `127.0.0.1`.
- The signed usbip-win2 VHCI driver is told to attach it (`PLUGIN_HARDWARE`
  IOCTL); detach with `PLUGOUT_HARDWARE`.
- The virtual device mirrors the real HID descriptors, answers feature reports
  with the real controller's values, and exposes a USB Audio Class 1 function
  (4-channel 48 kHz out, 2-channel 48 kHz mic).
- HidHide hides the physical controller from games; our process is whitelisted.

## Bluetooth link control

Disconnect a controller (idle sleep): `IOCTL_BTH_DISCONNECT_DEVICE`
(`0x0041000C`) on a radio handle with the 6-byte address.

## DualSense Edge

| Fact | Value | Source |
|---|---|---|
| Identity | VID `054C`, PID `0DF2` | Linux `hid-playstation.c`; DualSenseClient `ControllerFactory.cs` |
| Extra buttons | input byte 9: FnL `0x10`, FnR `0x20`, left rear (L4) `0x40`, right rear (R4) `0x80` | DualSenseClient `DualSense/Input/InputState.cs` (byte 9 properties) |
| USB output report `0x02` | 64 bytes on the wire (63 + ID); our writer pads every report to the device's output report length, so no Edge case is needed | DualSenseClient `DualSense/Output/OutputReport.cs`; our `DualSenseController.Write` |
| Player LED patterns (console) | player 1 `0x04`, 2 `0x0A`, 3 `0x15`, 4 `0x1B` (5 LEDs, bit 0 = left outer) | Linux `hid-playstation.c` player id table |

**Not supported (phase 8), no public verifiable protocol facts:** profiles stored on the
controller, trigger stop levels (mechanical switches, not reported in input), the replaceable
stick module's data. The Edge's Fn buttons on PC arrive as plain buttons; TriggerForge uses
them for its own profile shortcuts (`docs/contract-phase8.md`).
