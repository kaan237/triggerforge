# Phase 4 contract: virtual controller and drivers

Adds to `docs/contract.md` (phase 2) and `docs/contract-phase3.md` (phase 3). Nothing there
changes except where this file says so. Protocol facts live in `docs/protocol.md` and
`docs/protocol-emulation.md`; code is written from those notes, never from reference code.

Goal: games see a virtual wired controller (Xbox 360 or DualSense) that Tactile feeds from
the real Bluetooth DualSense. The real controller is hidden from games with HidHide. Audio
and isochronous transfers are **not** in this phase (phase 5b).

## Projects

| Project | Content | Owner |
|---|---|---|
| `src/Tactile.Emulation` | `Contracts.cs` (fixed), USB/IP server, virtual USB devices, Xbox 360 and DualSense (HID only) device models | emulation job |
| `src/Tactile.Drivers` | `Contracts.cs` (fixed), driver detection, install, VHCI attach/detach, HidHide control, recovery journal, `RecoveryRunner` | drivers job |
| `src/Tactile.Core/Emulation/` + small edits in `Tactile.Core`, `Tactile.Device.DualSense`, `Tactile.Host`, `ui/` | orchestration, profile model, remap, output merge, bridge, UI pages, `--restore` | core job |

References: `Tactile.Emulation` → `Tactile.Device.DualSense`. `Tactile.Drivers` → nothing
from the app (only BCL + Win32). `Tactile.Core` → both. `Tactile.Host` → all.
Tests: `tests/Tactile.Tests/Emulation/`, `tests/Tactile.Tests/Drivers/`,
`tests/Tactile.Tests/EmulationCore/` (one folder per job). The tests project gets project
references to both new projects (added once before the jobs start).

The `Contracts.cs` files are fixed: no job edits them. If a job needs a change, it writes
the reason in its final report and works around it.

## Safety rules (all jobs)

- No code is **run** that attaches a virtual device, hides a device, installs a driver,
  sends output to the controller or input to the PC. Unit tests use fakes and loopback
  sockets only. The lead runs hardware checks later, announced to KH.
- The USB/IP server listens on `127.0.0.1` only, never `0.0.0.0` or IPv6 any.
- Every change to the system (VHCI attach, HidHide entry, HidHide active flag) is written to
  the recovery journal **before** it is applied, and removed **after** it is undone.
- Restore undoes only what Tactile added. It never clears HidHide entries the user or another
  app made.
- All Win32 failures are reported as results/notices, never crash the app. Emulation failing
  leaves the rest of the app (profiles, lights, mapping) working.

## Emulation API (`src/Tactile.Emulation/Contracts.cs`, fixed)

```csharp
namespace Tactile.Emulation;

public enum VirtualKind { Xbox360, DualSense }

/// Real controller values the virtual DualSense answers with (USB form: report id first).
public sealed record DualSenseIdentity(byte[] Calibration, byte[] Pairing, byte[] Firmware);

/// Something the game sent to the virtual pad.
public abstract record PadFeedback(long Timestamp);          // Stopwatch ticks at arrival
public sealed record RumbleFeedback(long Timestamp, byte Large, byte Small) : PadFeedback(Timestamp);   // Xbox 360
public sealed record LedFeedback(long Timestamp, byte Pattern) : PadFeedback(Timestamp);                // Xbox 360 ring
public sealed record DualSenseOutputFeedback(long Timestamp, byte[] State) : PadFeedback(Timestamp);    // 47-byte common block from report 0x02

public interface IVirtualPad : IDisposable
{
    VirtualKind Kind { get; }
    string BusId { get; }                 // USB/IP bus id the VHCI imports, e.g. "1-1"
    bool Imported { get; }                // a client has imported it (OP_REQ_IMPORT done)
    void Submit(InputState state);        // next input report towards the game (latest wins)
    event Action<PadFeedback>? Feedback;  // raised on the server thread; handlers must be quick
}

public interface IUsbIpServer : IDisposable
{
    int Port { get; }                     // bound on 127.0.0.1, ephemeral unless given
    IVirtualPad Create(VirtualKind kind, DualSenseIdentity? identity);   // identity only for DualSense; null = built-in defaults
    void Remove(IVirtualPad pad);
}

public static class UsbIpServerFactory
{
    public static IUsbIpServer Start(int port = 0);   // implemented by the emulation job
}
```

`InputState` is `Tactile.Device.DualSense.InputState`. For Xbox 360 the pad maps it itself
(sticks, triggers, buttons; PS → Guide, Create → Back, Options → Start, touchpad click unused).

## Drivers API (`src/Tactile.Drivers/Contracts.cs`, fixed)

```csharp
namespace Tactile.Drivers;

public enum DriverKind { UsbIp, HidHide }
public enum DriverState { Missing, Ready, Unsupported, Error }

public sealed record DriverStatus(DriverKind Kind, DriverState State, string? Version, string? Detail);

public sealed record InstallResult(bool Ok, bool RebootRequired, string Message);

public interface IDriverManager
{
    DriverStatus Status(DriverKind kind);
    /// Downloads the pinned official installer, checks SHA-256 and Authenticode signer,
    /// then starts it elevated (UAC). Never runs anything unverified.
    Task<InstallResult> InstallAsync(DriverKind kind, IProgress<string>? progress, CancellationToken ct);
}

public interface IVhci
{
    /// Asks the VHCI driver to import busId from 127.0.0.1:tcpPort. Returns the hub port.
    int Attach(int tcpPort, string busId);
    void Detach(int hubPort);
}

public interface IDeviceHider
{
    /// Hides the device (instance id of the real controller's HID or parent node) from every
    /// process except Tactile. Journal first.
    bool Hide(string deviceInstanceId);
    void Unhide(string deviceInstanceId);
}

/// Journal of system changes, %APPDATA%\Tactile\recovery.json.
public interface IRecovery
{
    bool UncleanShutdown { get; }         // journal existed at startup with entries
    void RestoreAll();                    // detach every journaled hub port, undo every journaled HidHide change
    void MarkCleanExit();
}

/// Device tree helpers (SetupAPI / cfgmgr32).
public interface IDeviceTree
{
    /// True if the HID device path belongs to a device under the usbip-win2 VHCI (our virtual pads).
    bool IsVirtual(string hidDevicePath);
    /// Device instance id for a HID device path (what HidHide's blacklist takes), or null.
    string? InstanceId(string hidDevicePath);
}

public sealed record DriverPlatform(IDriverManager Drivers, IVhci Vhci, IDeviceHider Hider, IRecovery Recovery, IDeviceTree Tree)
{
    public static DriverPlatform CreateWindows(string dataDir);   // implemented by the drivers job
}
```

`recovery.json` replaces the plan's `hidden.json` (it also holds attached VHCI ports).

Facts that shape the drivers code (details in `docs/protocol-emulation.md` §2, §5, §6, §8):

- usbip-win2 struct layouts change between versions with the same size. Supported:
  **exactly 0.9.8.1** (detect via the Inno uninstall key `DisplayVersion`). Anything else is
  `Unsupported`.
- The VHCI keeps retrying a dropped server for ~2 h. `RestoreAll()` therefore: for every
  imported device whose host is `127.0.0.1` and whose port is one we journaled, stop attach
  attempts and plug out. Never touch other USB/IP devices. Never use persistent devices.
- The USB/IP server binds a fresh ephemeral port each run, so stale retries hit nothing.
- HidHide allows one open handle system-wide: open, act, close every time. Hiding uses the
  persistent blacklist (the only one in released builds), so the journal is what makes it
  crash-safe. Our exe goes on the whitelist as a `\Device\HarddiskVolumeN\…` path; journal
  whether we added it and whether we turned cloaking on.
- Both installers ask for a reboot: `InstallResult.RebootRequired = true` and the UI says so.
  usbip-win2: `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`; HidHide: `/exenoui /qn /norestart`.
  Pinned URLs, sizes, SHA-256 in §8. Authenticode must be valid; the expected signer
  subject is checked if pinned (lead fills it after verifying the real files).

### Driver distribution

Tactile does not bundle or sign drivers. `InstallAsync` downloads the **official signed
installer** from the vendor's GitHub release, at a version and SHA-256 pinned in
`src/Tactile.Drivers/DriverManifest.cs`, verifies the Authenticode signature and expected
signer, then runs it with `runas` (the user accepts UAC). Supported version range is pinned
too; outside it `Status` returns `Unsupported` and emulation stays off.

## Profile additions (still `version: 1`; missing fields take defaults)

```jsonc
{
  // … phase 2 and 3 fields …
  "emulation": {
    "mode": "off",              // off | xbox360 | dualSense
    "remap": { "cross": "circle" },   // source button → target button name or "none"; only buttons listed change
    "swapSticks": false,
    "hideMappedButtons": true   // buttons bound in mapping (phase 3) are released on the virtual pad
  }
}
```

Remap keys/targets use the phase 3 button names. `l2`/`r2` as source: its analog value
follows the target if the target is `l2`/`r2`, otherwise the analog goes to 0 and the target
is pressed while the source's digital bit is down. Remap is applied before `Submit`.

## AppSettings additions

```jsonc
{
  "emulation": {
    "enabled": true,            // master switch; false = never attach or hide anything
    "hideController": true      // use HidHide while a virtual pad is attached
  }
}
```

## Core behaviour (`src/Tactile.Core/Emulation/`)

- `EmulationService` owns the pad lifecycle: wanted mode = active profile's mode, if
  settings allow, both drivers `Ready`, and a controller is connected. On change: create pad,
  attach, hide the real controller; on stop: unhide, detach, remove. Mode switch between
  profiles reattaches. Attach failures become a notice and the mode is treated as off until
  the next profile change or a "retry" message.
- Startup: if `IRecovery.UncleanShutdown`, call `RestoreAll()` before anything else and
  queue a notice. Clean exit: stop, then `MarkCleanExit()`.
- Our own device scan must ignore virtual pads: `ControllerService` gets an optional
  `Func<string, bool> isVirtual` (from `IDeviceTree.IsVirtual`) and skips matching paths.
- If the real controller is on **USB**, DualSense mode is not offered (it already is a wired
  DualSense); Xbox 360 mode still works.
- Virtual DualSense output report `0x02` arrives as 48 bytes (descriptor size) or 63; accept
  both, use bytes 1..47.
- Input: every `InputState` from the real controller → remap → hide mapped buttons → `Submit`.
- Xbox 360 feedback: rumble → output override layer `"emulation"` (RumbleLeft = Large,
  RumbleRight = Small). LED ignored.
- DualSense feedback: `OutputReport.ApplyState(block, current)` (new, in
  `Tactile.Device.DualSense`) applies only the fields whose valid flags are set. The game's
  accumulated state is an override layer `"game"` on top of the profile: once the game has set
  triggers, lightbar, player LEDs, mic LED or rumble, those fields follow the game until the
  pad is removed. Sending must not wait for the 30 Hz tick.
- Latency: `Stopwatch` ticks from `PadFeedback.Timestamp` to the return of the controller
  write. Keep p50/p95 over a rolling window; send in `emulation` status every second.
- Steam Input: if `steam.exe` runs and a pad is attached, queue one notice per session
  ("Steam Input bu oyunda açıksa çift kontrolcü görünür…").

## Host

- `Tactile.exe --restore`: runs `RestoreAll()` and sets `emulation.enabled = false`, shows a
  short message, exits. Works while another copy runs (it does not take the single-instance
  mutex). Tray menu and settings page get "Kontrolcüyü geri getir" doing the same in-process.
- A Start menu shortcut "Tactile — Kontrolcüyü geri getir" (`--restore`) is created on first
  run if missing.

## Bridge additions

Requests:

| type | fields |
|---|---|
| `emulation.status` | — host answers `emulation` |
| `driver.install` | `kind` (`usbIp` / `hidHide`) |
| `emulation.retry` | — |
| `emulation.restore` | — same as `--restore` in-process |

Events:

| type | fields |
|---|---|
| `emulation` | `enabled`, `mode` (current, `off`/`xbox360`/`dualSense`), `attached`, `hidden`, `drivers: [{kind, state, version, detail}]`, `latency: {p50Ms, p95Ms, samples}`, `error` (string or null) |
| `driver.progress` | `kind`, `message`, `done` (bool), `ok` (bool) |

Profile and settings changes go through the existing `profile.save` / `settings.save`.

## UI (`ui/`)

- New page "Sanal kontrolcü": driver cards (state, version, install button, USB hub warning:
  "Kurulum sırasında USB 3 hub'ları kısa süre yeniden başlar; fare ve klavye bir an kopabilir."),
  master switch, hide switch, live status (attached, hidden, latency), "Kontrolcüyü geri getir".
- Profile editor: mode selector (Kapalı / Xbox 360 / DualSense) with an anti-cheat note for
  online games, remap editor (source → target list), swap sticks, hide mapped buttons.
- Browser preview (`sim.ts`) simulates the new messages.
