# Test hardware

Fill in when the controller is connected. `tactile-capture list` prints link and report sizes.

| Item | Value |
|---|---|
| Controller | DualSense / DualSense Edge |
| Firmware | build "Jul  4 2025 10:10:32" (feature `0x20`) |
| Bluetooth adapter | (Device Manager → Bluetooth) |
| USB cable | USB-C data cable |
| OS | Windows 11 Pro 26200 |

## Measured

| What | USB | Bluetooth |
|---|---|---|
| Input reports per second | | ~800 (median 1.25 ms) |
| Reduced mode before feature `0x05`? | n/a | yes, 7 short reports then full |
| Max output reports per second without drops | | not measured yet |
| Output checked by hand | | lightbar, player LEDs, mic LED, both rumble motors, weapon, vibration, slope, galloping, haptics L/R |

## Capture

```
tactile-capture list
tactile-capture record captures/usb-buttons.bin 15
tactile-capture record captures/bt-buttons.bin 15
```

Captures go in `captures/` and are not committed (see `.gitignore`); keep a few small
ones under `tests/` once the parser tests need them.
