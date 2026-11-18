# Tactile

DualSense control for Windows: adaptive triggers, haptics, lightbar and per-game
profiles over Bluetooth, without a cable.

Early development. Nothing usable yet.

## Build

Requirements: .NET 10 SDK, Node 24, WebView2 runtime (ships with Windows 11).

```
npm --prefix ui install
npm --prefix ui run build
dotnet build Tactile.slnx
dotnet test Tactile.slnx
```

Run the UI with hot reload inside the app:

```
npm --prefix ui run dev
set TACTILE_UI_DEV=http://localhost:5173
src\Tactile.Host\bin\Debug\net10.0-windows\Tactile.exe
```

## Layout

| Path | What |
|---|---|
| `src/Tactile.Device.Hid` | Windows HID access (hid.dll, SetupAPI) |
| `src/Tactile.Device.DualSense` | Report formats, CRC, capture files |
| `src/Tactile.Core` | Profiles and effects (later phases) |
| `src/Tactile.Host` | Window, WebView2, tray |
| `ui/` | React interface |
| `tools/Tactile.Tools.Capture` | `tactile-capture`: dumps raw reports for tests |
| `docs/protocol.md` | Protocol notes |

## License

All rights reserved. See `LICENSE`.
