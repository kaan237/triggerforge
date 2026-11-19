# Phase 7a contract: TriggerForge name, English + Turkish, logs, installer, portable

Adds to `docs/contract.md` and the phase 3-6 contracts. Decisions are in `PLAN.md`, section
"Faz 7 kararları".

Goal: the app ships as **TriggerForge** with an installer, an uninstaller that leaves the
controller visible and the PC clean, a portable zip, English and Turkish text (English by
default), a local log with an export button, signed binaries and a third-party notices file.

## Work split (one after another, each reviewed before the next)

| Job | Content | Files |
|---|---|---|
| 1 | Name, data folder migration, portable mode, `--restore --quiet`, log + export | `Directory.Build.props`, `src/Tactile.Host/**`, `src/Tactile.Core/Storage/**`, `src/Tactile.Core/Logging/**` (new), `src/Tactile.Core/AppCore.cs`; tests in `tests/Tactile.Tests/Hosting/**` |
| 2 | English + Turkish everywhere, English default | every `src/**` file with user text, `ui/src/**` |
| 3 | Publish script, signing, Inno Setup script, uninstall check, third-party notices, README | `build/**` (new), `installer/**` (new), `tools/uninstall-check.ps1` (new), `THIRD-PARTY-NOTICES.txt` (new), `README.md` |

C# project, namespace and test names stay `Tactile.*`. Only what the user sees changes.

## Safety rules (every job)

Nothing is **run** that sends output to the controller, input to the PC, attaches or hides a
device, installs or removes a driver, or writes the real registry Run key or Start menu. The
app itself is not started. Tests use temporary folders and fakes. Only `dotnet build`,
`dotnet test`, `npm run build`, and in job 3 `dotnet publish` into `artifacts/`. No new NuGet
or npm packages. No copied code (license texts are copied verbatim, that is required). No
commits. Warning-free. Code comments describe the code only. No tool or assistant names in code, comments or docs.

## Job 1: name, data, portable, log

### Name

- `Directory.Build.props`: `Product` = `TriggerForge`, `Version` = `0.7.0`, `Company` and
  copyright stay `KH`.
- `Tactile.Host.csproj`: `AssemblyName` = `TriggerForge` (exe becomes `TriggerForge.exe`).
- Window title, tray text, balloon title, MessageBox captions: `TriggerForge`.
- Named objects: `Local\TriggerForge.Instance`, `Local\TriggerForge.Show`,
  `Local\TriggerForge.Restore`, `Local\TriggerForge.Restored`.
- Run key value `TriggerForge`. `Autostart.Apply` also deletes an old `Tactile` value.
- Start menu shortcut `TriggerForge — Restore controller.lnk` (job 2 makes the name follow the
  language only at creation time; it is not renamed later). The old Tactile shortcut is
  deleted if present.
- Thread names, DSX answer `Status` text: English, `TriggerForge: …`.

### Data folders and migration

- Settings, profiles, recovery journal: `%APPDATA%\TriggerForge`. WebView2 data:
  `%LOCALAPPDATA%\TriggerForge\WebView2`. Logs: `%LOCALAPPDATA%\TriggerForge\logs`.
- `TACTILE_DATA_DIR` keeps working (tests and tools use it).
- Migration (`Storage/DataMigration.cs`), run first thing in `Main` and in `--restore`, before
  anything reads settings or the recovery journal: if `%APPDATA%\TriggerForge` does not exist
  and `%APPDATA%\Tactile` does, copy the whole folder (files and subfolders) into a temporary
  sibling, then rename it to `TriggerForge`. The old folder is **never** deleted or changed.
  A failure leaves no half folder (temporary folder removed) and the app starts with fresh
  data plus a log line; it never blocks startup. The old WebView2 folder is not copied.
- Tests: copy with nested files, target exists → nothing happens, failure mid-copy → no target.

### Portable mode

- Active when a file `portable.txt` sits next to the exe.
- Data folder `<exe dir>\data`, logs `<exe dir>\data\logs`, WebView2 `<exe dir>\data\WebView2`.
- No Start menu shortcut, no Run key (the "start with Windows" toggle is hidden in the UI;
  the bridge reports `portable: true` in `hello`). Emulation default off for a fresh portable
  data folder (the user can still turn it on if the drivers are installed).
- No migration in portable mode.

### `--restore --quiet`

`--restore` works as today. With `--quiet` no MessageBox is shown; the exit code is 0 on
success and 1 on failure, and the result is logged. The uninstaller uses this.

### Log (`src/Tactile.Core/Logging/Log.cs`)

- Static `Log.Info/Warn/Error(string message, Exception? e = null)`; thread-safe; never throws.
- File `triggerforge.log`, UTF-8, one line per entry:
  `2026-11-19T16:20:00.123Z ERROR message | ExceptionType: message` and the stack trace on
  following indented lines.
- Rotation: at 1 MB the file is renamed to `triggerforge.1.log` (older `.1` → `.2`, `.2`
  deleted). At most 3 files.
- Masking: every line replaces the user profile path with `%USERPROFILE%` and the user name
  with `<user>` (case-insensitive) before it is written. MAC addresses keep only the last two
  bytes (`**:**:**:**:AB:CD`).
- Logged: app start (version, Windows version, portable yes/no), migration result, unhandled
  exceptions (`AppDomain.UnhandledException`, `Application.ThreadException`,
  `TaskScheduler.UnobservedTaskException`), every notice of kind `emulation`, `mapping`,
  `brokenFile`, driver install start/result, `--restore` result, exceptions already caught
  in the host (startup, WebView2, elevation).
- No telemetry: nothing leaves the PC.

### Export

- Bridge message `logs.export` → save dialog (default name `TriggerForge-logs-<yyyyMMdd-HHmm>.zip`)
  → zip with the log files, `settings.json` and a `system.txt` (app version, Windows version,
  portable, driver states as the drivers layer reports them). Profiles are not included.
  The same masking runs on `system.txt`. Answer `logs.exported` with `{ ok, path? , error? }`.
- Bridge message `logs.open` opens the logs folder in Explorer.
- UI: in Settings, section "Uygulama/Application": two buttons "Logları dışa aktar" and
  "Log klasörünü aç" (job 2 translates). `sim.ts` answers both.
- Tests: rotation, masking (profile path, user name, MAC), zip contents.

## Job 2: English and Turkish

- `Language` default becomes `En` in `AppSettings`, `Validation` and the UI default. Existing
  settings files keep their stored value.
- C#: `src/Tactile.Core/Text.cs` with `Text.Current` (set from settings at load and on every
  save) and `Text.Pick(string en, string tr)`. Every user-visible string goes through it:
  notices, MessageBoxes, tray texts, balloons, shortcut description, driver progress, status
  texts the UI shows, validation messages. Log lines stay English only.
- UI: `ui/src/i18n.ts` with `L(en: string, tr: string): string` reading the current language,
  set by `AppProvider` from settings. `<html lang>` follows. `App.tsx` remounts the page view
  (`key={language}`) when the language changes so every text updates; shared drafts live in
  the app state and survive.
- Every visible UI string, `title`/`aria-label`/placeholder included, and `sim.ts` texts.
  Turkish text stays exactly as today; English text is natural, short UI English (not a word
  by word translation). Page names: Overview, Triggers, Light, Sticks, Mapping, Macros,
  Haptics, Virtual controller, Integrations, Profiles, Settings.
- The language picker shows "English" first, then "Türkçe". Changing it applies at once after
  save, including tray texts.
- Tests: default `En`; `Text.Pick` follows `Current`; one notice in both languages.

## Job 3: packaging

### Publish (`build/publish.ps1`)

1. `npm ci` (only if `ui/node_modules` is missing) and `npm run build` in `ui`.
2. `dotnet publish src/Tactile.Host -c Release -r win-x64 --self-contained true
   -p:PublishSingleFile=false -o artifacts/app` (folder publish: WebView2 loader and
   `opus.dll` stay files).
3. Sign every `TriggerForge.exe` and `Tactile.*.dll` in `artifacts/app` with the code signing
   certificate in `Cert:\CurrentUser\My` whose thumbprint is passed as `-Thumbprint`
   (default `25C26195A82AC4EC3A5C57CF6B7D652939154655`) using `Set-AuthenticodeSignature
   -HashAlgorithm SHA256 -TimestampServer http://timestamp.digicert.com`. A missing
   certificate stops the script with a clear message. `-NoSign` skips signing.
4. Portable zip: copy `artifacts/app` + an empty `portable.txt` + `THIRD-PARTY-NOTICES.txt`
   to `artifacts/TriggerForge-<version>-portable.zip`.
5. If `ISCC.exe` is found (Inno Setup 6 default paths or PATH), build the installer and sign
   it; otherwise print that Inno Setup is needed and stop after the zip.
6. Print SHA-256 of the zip and the installer.

Version comes from `Directory.Build.props`. `artifacts/` is in `.gitignore`.

### Installer (`installer/TriggerForge.iss`, Inno Setup 6)

- Per machine, `{autopf}\TriggerForge`, admin required, x64 only, Windows 10 1809+.
  Fixed `AppId` GUID (generate once). `AppMutex=Local\TriggerForge.Instance` (setup asks to
  close the running app). Uninstaller signed through `SignTool` (calls the same PowerShell
  signing).
- English and Turkish setup languages (setup picks from Windows, user can change).
- Tasks: desktop icon (off). No start-with-Windows task: setup runs elevated, so HKCU could be
  an administrator's account; the app's own setting writes the Run value (review fix).
- Start menu: `TriggerForge`, `TriggerForge — Restore controller` (`--restore`).
- Installs `THIRD-PARTY-NOTICES.txt` and `LICENSE` next to the exe. Checks for WebView2 Runtime
  (registry `EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}`); if missing, shows a
  message with the Microsoft download page and continues.
- Drivers are **not** bundled or installed by setup; the app's existing install button does it
  (pinned version, SHA-256, signer check).
- Uninstall order: stop the app, run `TriggerForge.exe --restore --quiet` (wait ≤ 20 s), remove
  the Run value, the app's Start menu shortcuts; ask "Also remove settings and profiles?"
  (default No) → delete `%APPDATA%\TriggerForge` and `%LOCALAPPDATA%\TriggerForge` for the
  current user; ask "Also remove the drivers usbip-win2 and HidHide?" (default No) → run each
  driver's own uninstaller from its uninstall registry entry, silently, if present. Silent
  uninstall (`/SILENT`, `/VERYSILENT`) answers No to both.

### Uninstall check (`tools/uninstall-check.ps1`)

Read-only report, exit code 0 when clean: install folder, Run values `TriggerForge` and
`Tactile`, Start menu shortcuts, `%APPDATA%`/`%LOCALAPPDATA%` folders (reported, not an error
unless the user chose removal via `-ExpectNoData`), HidHide hidden device list and app list
(via the HidHide CLI if installed; any entry containing a DualSense instance id or our exe is
an error), leftover virtual devices (USB/IP attached ports, if usbip-win2 is installed).

### Third-party notices (`THIRD-PARTY-NOTICES.txt`)

Verbatim license texts with name, version and source URL: .NET runtime (from the SDK's
`LICENSE.txt` and `ThirdPartyNotices.txt` reference), Microsoft.Web.WebView2 1.0.4314.35
(package `LICENSE.txt`, `NOTICE.txt`), Opus 1.6.1 (`third_party/opus/COPYING`), React and
React DOM 19.3.1 and their bundled runtime dependencies (each `LICENSE` from `ui/node_modules`).
A short section says usbip-win2 and HidHide are not shipped; they are downloaded from their
official releases when the user asks and keep their own licenses.

### README

English first, Turkish below (`## Türkçe`): what TriggerForge is ("DualSense PC control
center"), features, requirements, install / portable / uninstall, restore the controller,
logs, building from source (`build/publish.ps1`). No claims that are not in the app.

## Acceptance

- `dotnet build` warning-free, `dotnet test` all pass, `npm run build` clean.
- Job 1: an existing `%APPDATA%\Tactile` is copied once, old folder untouched; portable data
  stays next to the exe; log rotation and masking tests pass; export zip has the four parts.
- Job 2: a fresh settings file starts in English; switching to Türkçe and back changes every
  page, notices and the tray; no Turkish left in English mode (checked page by page in the
  browser pane with `sim.ts`).
- Job 3: `build/publish.ps1 -NoSign` produces `artifacts/app` and the portable zip; with the
  certificate, `Get-AuthenticodeSignature` shows signer `CN=Wiaus` on the exe; with Inno Setup
  installed, the installer builds and is signed.
