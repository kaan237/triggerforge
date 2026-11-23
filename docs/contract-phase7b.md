# Phase 7b contract: signed updates, first-run wizard

Adds to `docs/contract.md` and `docs/contract-phase7a.md`. Decisions: `PLAN.md`, "Faz 7
kararları". Version becomes **0.8.0**.

Goal: TriggerForge checks a signed manifest on GitHub Releases, downloads and verifies the new
installer and runs it; a first-run wizard walks a new user through language, controller,
drivers and a short test.

## Work split (jobs A and B run at the same time; their files never overlap)

| Job | Content | Files |
|---|---|---|
| A | Updater (check, verify, download, install), settings fields, bridge messages, tests | `src/Tactile.Core/Updates/**` (`UpdateKey.cs` exists, do not change its values), `src/Tactile.Core/Model/AppSettings.cs`, `src/Tactile.Core/Storage/Validation.cs`, `src/Tactile.Core/AppCore.cs` (only if needed), `src/Tactile.Host/**`; tests in `tests/Tactile.Tests/Updates/**` |
| B | Wizard UI, Settings "Updates" section, `host.ts` types, `sim.ts` answers | `ui/src/**` only |
| C | Release tool, manifest signing, installer relaunch, publish/release scripts | `tools/Tactile.Tools.Release/**`, `build/**`, `installer/**` |

## Safety rules (every job)

Nothing is **run** that sends output to the controller, input to the PC, attaches or hides a
device, installs a driver or an update, or touches the network. Tests use a fake
`HttpMessageHandler` and temp folders; no real HTTP. The app itself is not started. Only
`dotnet build`, `dotnet test`, `npm run build`. No new packages. No copied code. No commits.
Warning-free. English and Turkish for every user text (`Text.Pick` / `L`), English default.
Code comments describe the code only. No tool or assistant names anywhere.

## Manifest (written by job C, read by job A)

`manifest.json` (UTF-8, ≤ 64 KB) and `manifest.json.sig` (base64 of a 64-byte ECDSA P-256 /
SHA-256 signature, IEEE P1363 `r||s`, over the exact bytes of `manifest.json`).

```jsonc
{
  "schema": 1,
  "version": "0.8.0",
  "released": "2026-11-19",
  "notes": { "en": "…", "tr": "…" },
  "installer": { "name": "TriggerForge-0.8.0-setup.exe", "url": "https://github.com/kaan237/triggerforge/releases/download/v0.8.0/TriggerForge-0.8.0-setup.exe", "size": 42000000, "sha256": "<64 lowercase hex>" },
  "portable":  { "name": "TriggerForge-0.8.0-portable.zip", "url": "…", "size": 51000000, "sha256": "…" }
}
```

Both files are fetched from `UpdateKey.ManifestUrl` and `ManifestUrl + ".sig"`.

## Job A: updater

### Fetching (`Updates/UpdateClient.cs`)

- One `HttpClient` with `AllowAutoRedirect = false`; redirects (301/302/303/307/308, at most 5)
  are followed by hand. **Every** URL, the first and each `Location`, must be `https`, port
  443, and its host exactly one of `github.com`, `objects.githubusercontent.com`,
  `release-assets.githubusercontent.com`. Anything else → error `untrustedHost`, nothing more
  is fetched.
- Size caps: manifest 64 KB, signature 1 KB, installer and zip 300 MB. A body larger than
  the cap (by `Content-Length` or while reading) → error `tooLarge`. Timeouts: 15 s for
  manifest/sig, 10 min for the installer.
- User-Agent `TriggerForge/<version>`. No cookies, no credentials, no query strings added.

### Verification (`Updates/UpdateManifest.cs`)

1. Signature: `ECDsa.ImportSubjectPublicKeyInfo(UpdateKey.ManifestPublicKey)`,
   `VerifyData(bytes, sig, SHA256, IeeeP1363FixedFieldConcatenation)`. Bad or missing → error
   `badSignature`. Nothing from an unsigned manifest is shown or used.
2. Parse (strict: `schema == 1`, version parses as `System.Version`, every field present,
   `size` 1..300 MB, `sha256` 64 hex). Asset `url` must start with
   `UpdateKey.AssetPrefix + "v" + version + "/"` and end with its `name`; `name` must be
   `TriggerForge-<version>-setup.exe` / `-portable.zip`. Otherwise `badManifest`.
3. Newer only: manifest version > the running assembly version (compare major.minor.build).
   Equal or lower → state `upToDate`. Never a downgrade.
4. After download: size equals, SHA-256 equals (constant-time compare) → else `badFile`, file
   deleted. Then the installer's Authenticode signer certificate thumbprint must equal
   `UpdateKey.InstallerSignerThumbprint` (read with `X509Certificate.CreateFromSignedFile`;
   the hash check already proves integrity). Mismatch → `badSigner`, file deleted.

### Install

- Download to `<local data>\updates\<version>\<name>` (`AppPaths` local root, i.e. next to
  logs; portable: `data\updates`), written to `.partial` then renamed. Old version folders
  are deleted when a check starts.
- Installed copy: `install` runs the verified installer with `UseShellExecute = true` and
  **no** `runas` (setup elevates itself and so keeps the signed-in user's token for the
  relaunch; started elevated it relaunched the app as administrator, found in the 2026-11-19
  install test), arguments `/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS
  /RELAUNCH` (`/RELAUNCH` left out when the app itself runs elevated), then the app exits
  normally. If the user declines setup's UAC prompt, the app stays closed and is started
  again from Start.
- Portable copy: never downloads or installs. State `available` carries `portable: true`; the
  UI offers to open `UpdateKey.ReleasesPage` (the only URL the app ever opens for updates).

### Schedule and settings

- `AppSettings.Updates` (new record `UpdateSettings { bool AutoCheck = true }`) and
  `AppSettings.FirstRunDone` (bool, default false). Validation keeps both.
- With `AutoCheck`, check once 60 s after start and then every 24 h while running. A manual
  check works whatever the setting. Only one check or download at a time.
- Every failure is logged in English with its error code; the UI gets the code and a short
  localized message. Offline / DNS / timeout → error `network`.

### Bridge

App → UI `update.status`:
```jsonc
{ "type": "update.status",
  "state": "idle" | "checking" | "upToDate" | "available" | "downloading" | "ready" | "error",
  "current": "0.8.0", "latest": "0.9.0" | null, "notes": "<in the current language>" | null,
  "released": "2026-12-01" | null, "progress": 0.0-1.0 | null, "portable": false,
  "error": "network" | "untrustedHost" | "tooLarge" | "badSignature" | "badManifest" | "badFile" | "badSigner" | null,
  "message": "<localized error text>" | null, "lastCheck": "<ISO UTC>" | null }
```
Sent on `hello`, on every state change, at most 5 per second while downloading.

UI → App: `update.check` (check now), `update.download` (when `available` and not portable;
on success state `ready`), `update.install` (when `ready`), `update.openPage` (opens the
releases page). Any message in the wrong state is ignored with a log line.

`hello` status also gets `"version": "0.8.0"` (already present) and `"firstRunDone"` comes with
settings.

## Job B: UI

### Settings → "Updates" / "Güncellemeler"

Toggle "Check for updates automatically" / "Güncellemeleri otomatik denetle"; current version;
last check time; button "Check now" / "Şimdi denetle"; when `available`: version, release
date, notes, button "Download" / "İndir" (portable: "Open download page" / "İndirme sayfasını
aç"); while downloading a progress bar; when `ready`: "Install and restart" / "Kur ve yeniden
başlat"; error text with a "Try again" button. A small badge on the Settings nav item when an
update is `available` or `ready`.

### First-run wizard

Shown full-window when `settings.firstRunDone` is false (also in the browser preview). Steps,
with Back / Next and "Skip setup" / "Kurulumu atla" on every step:

1. **Welcome** — app name, one-line description, language picker (English, Türkçe; applies
   at once by saving the language).
2. **Controller** — live state from `status`: connected model and link, or how to connect
   (USB cable, or Bluetooth pairing: hold PS + Create until the light bar flashes, then add
   it in Windows Bluetooth settings).
3. **Virtual controller (optional)** — what it is for (games that only see Xbox / DualSense
   over USB), current driver states from `emulation.status`, the existing install buttons
   (`driver.install`), progress from the existing driver messages, a restart warning when an
   install reports it. "Not now" goes on.
4. **Test** — the existing live input view (`Live`) and three buttons that use existing
   messages: light bar color, a short rumble, an R2 resistance preview; "Reset" restores the
   profile.
5. **Done** — what to look at next (Triggers, Profiles, Integrations); "Finish" saves
   `firstRunDone: true`.

Skip also saves `firstRunDone: true`. Settings gets "Run setup again" / "Kurulumu yeniden
başlat" which saves `firstRunDone: false`. `sim.ts` starts with `firstRunDone: false`,
answers all update messages (fake states with a short fake download) and keeps working as
today.

## Job C: release

- `tools/Tactile.Tools.Release`: `keygen` (done, key in the current user's Windows key store),
  `pubkey`, `backup <file> <password>`, `manifest <version> <dir> <notes-en> <notes-tr>`.
- `installer/TriggerForge.iss`: `[Run]` starts the app after a silent update when `/RELAUNCH`
  was passed; normal setups keep the "Launch TriggerForge" checkbox.
- `build/release.ps1`: runs `publish.ps1`, writes and signs the manifest, creates the GitHub
  release `v<version>` on `kaan237/triggerforge` with the installer, the zip, `manifest.json`
  and `manifest.json.sig`.

## Acceptance

- Build warning-free, all tests pass, UI build clean.
- Tests (job A): good manifest accepted; one flipped byte in manifest or signature →
  `badSignature`; redirect to another host → `untrustedHost`; `http://` → `untrustedHost`;
  oversized body → `tooLarge`; wrong asset URL or name → `badManifest`; older or equal
  version → `upToDate`; wrong file hash → `badFile` and the file is gone; portable never
  downloads.
- UI (job B): wizard steps in both languages in the browser preview; Settings update section
  in every state via `sim.ts`.
- Release (job C): the published `latest/download/manifest.json` verifies with the pinned key,
  and a 0.7.x → 0.8.0 style check against the live release reports `upToDate` for 0.8.0.
