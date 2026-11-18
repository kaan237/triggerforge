# Test hardware

Fill in when the controller is connected. `tactile-capture list` prints link and report sizes.

| Item | Value |
|---|---|
| Controller | DualSense / DualSense Edge |
| Firmware | (from feature `0x20`, or PS5 / Sony PC updater) |
| Bluetooth adapter | (Device Manager → Bluetooth) |
| USB cable | USB-C data cable |
| OS | Windows 11 Pro 26200 |

## Measured

| What | USB | Bluetooth |
|---|---|---|
| Input reports per second | | |
| Reduced mode before feature `0x05`? | n/a | |
| Max output reports per second without drops | | |

## Capture

```
tactile-capture list
tactile-capture record captures/usb-buttons.bin 15
tactile-capture record captures/bt-buttons.bin 15
```

Captures go in `captures/` and are not committed (see `.gitignore`); keep a few small
ones under `tests/` once the parser tests need them.
