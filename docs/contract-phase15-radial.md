# Phase 15.4 contract: radial menu

Adds to `docs/contract.md`, `docs/contract-phase3.md`, and `docs/contract-phase14.md`. Roadmap basis: `docs/yol-haritasi.md:81`. Profile and settings versions stay at `1` (`src/Tactile.Core/Model/Profile.cs:27`, `src/Tactile.Core/Model/AppSettings.cs:9`).

Goal: add profile-scoped radial menus (up to 4 per profile, 2–8 slices each) opened while holding a mapped button (`special: "radialMenu"` + menu ID), selecting a slice by tilting the configured stick (left or right), firing the selected slice's `ActionConfig` once (tap) on release or cancelling when centered, neutralizing only that stick across mapping, emulation, and DSU while open, and rendering on a click-through, no-activate, flat-graphite `OverlayWindow`.

## 1. Scope

### In scope

1. **Radial menu model & action (`src/Tactile.Core/Model/Mapping.cs`)**:
   - Add `RadialMenu` to `SpecialAction` (`src/Tactile.Core/Model/Mapping.cs:12`, serialized as `"radialMenu"` via `src/Tactile.Core/Storage/TactileJson.cs:76-77`).
   - Add `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? RadialMenuId { get; init; }` to `ActionConfig` (`src/Tactile.Core/Model/Mapping.cs:123-170`).
   - Add `RadialStick { Left, Right }`, `RadialSlice(Label, Action)`, `RadialMenu(Id, Name, Stick, Slices)`, and `IReadOnlyList<RadialMenu> RadialMenus` (default `[]`) on `MappingConfig` (`src/Tactile.Core/Model/Mapping.cs:37-59`). Because `Mapper.SameMapping` serializes `Mapping` (`src/Tactile.Core/Mapping/Mapper.cs:312-314`), radial edits automatically reconfigure the mapper.
2. **Hold-to-open, stick-select, release-to-tap state machine (`src/Tactile.Core/Mapping/RadialMenu.cs`, `ActionDispatcher.cs`, `Mapper.cs`)**:
   - `RadialMenuController`: pure, lock-guarded state machine (mirroring `src/Tactile.Core/Mapping/OnScreenKeyboard.cs:15-310`) publishing immutable `RadialMenuState(bool Visible, string MenuId, string Name, RadialStick Stick, IReadOnlyList<string> Labels, int? SelectedIndex)` via `Changed` only when visible state or selection changes.
   - **Open**: holding a button bound to `special: "radialMenu"` + valid `RadialMenuId` triggers `ActionDispatcher.Down` (`src/Tactile.Core/Mapping/ActionDispatcher.cs:76-113`), returning a `HeldAction` carrying `RadialMenuId` (`ActionDispatcher.cs:27-33`) and opening the menu. If another radial menu is already open, ignore the second open.
   - **Select**: read `Stick.Normalized` (`src/Tactile.Device.DualSense/InputState.cs:33-37`, `X` right positive, `Y` up positive). Magnitude `r = hypot(x, y)`: select when `r >= 0.35`, clear to `null` (center cancel) when `r < 0.25` (0.10 hysteresis). Angle clockwise from 12 o'clock: `a = atan2(x, y)` wrapped to `[0, 2π)`; slice `0` is centered at top, `index = floor(((a + π/N) mod 2π) / (2π/N))` for `N` slices (`2..8`).
   - **Commit**: normal button release (`ButtonMachine.Up`, `src/Tactile.Core/Mapping/ButtonMachine.cs:105-141` -> `ActionDispatcher.Up`, `ActionDispatcher.cs:115-137`) closes the menu and, if `SelectedIndex` is non-null and its `Action.Kind != ActionKind.None`, invokes `ActionDispatcher.Tap(slice.Action, now)` (`ActionDispatcher.cs:140-146`) once. If centered (`SelectedIndex == null`), cancels with no output.
   - **Cancel**: pause (UI/hotkey/shortcut/fullscreen), mapping disabled, profile switch, shift-layer change, OSK open, controller disconnect/primary swap, or dispose cancels the menu **before** `ButtonMachine.ReleaseAll` (`src/Tactile.Core/Mapping/Mapper.cs:529-572`) so `Up` never fires a slice.
3. **Single-stick neutralization & hold mask (`src/Tactile.Core/AppCore.cs`, `Mapper.cs`)**:
   - While a radial menu is open, replace **only** its configured stick (`Left` or `Right`) with `new Stick(128, 128)` before `Mapper.OnInput` (`AppCore.cs:999`), `EmulationService.OnInput` (`AppCore.cs:1011`), and `DsuServer.PublishInput` (`AppCore.cs:977`). All other inputs (other stick, buttons, triggers, gyro, touchpad) continue working normally.
   - On the opening frame, neutralize that stick inside `Mapper.OnInput` (`Mapper.cs:210-225`, releasing any held stick-keys/ring on that stick only) and before `EmulationService.OnInput` / `DsuServer.PublishInput` (matching the OSK opening-frame guard at `AppCore.cs:998-1005`).
   - On cancel while the trigger button is still physically held, suppress it via `_mapper.SuppressButtons` / `_radialHoldMask` (mirroring `_oskHoldMask` at `AppCore.cs:69,469-477,965-972`). OSK takes precedence: opening OSK cancels any open radial menu (`AppCore.cs:457-467`).
   - `EmulationInput.MappedButtons` (`src/Tactile.Core/Emulation/EmulationInput.cs:87-161`) already hides buttons bound to any non-`None` `ActionConfig`, so `HideMappedButtons` covers radial triggers with zero changes in `src/Tactile.Core/Emulation/**`.
4. **Overlay window (`src/Tactile.Host/OverlayWindow.cs`, `RadialMenuWindow.cs`, `TactileApp.cs`)**:
   - `RadialMenuWindow : OverlayWindow, IRadialMenuView` inherits click-through, no-activate, topmost flags (`OverlayWindow.cs:186-224`).
   - Centered on `ForegroundMonitor().WorkArea` (`OverlayWindow.cs:265-279`), DPI-scaled circular region (`CreateEllipticRgn` or `ShowUnactivated` with `cornerRadius = diameter / 2` at `OverlayWindow.cs:238-252`; base diameter `360 px` at 96 DPI, clamped to fit monitor).
   - Flat graphite palette (`OverlayWindow.cs:163-169`): `Surface`/`SurfaceRaised` unselected wedges, `LineStrong` dividers and rim, `Ink` fill + `Graphite` bold text on selected wedge (matching `src/Tactile.Host/OnScreenKeyboardWindow.cs:148-166`), inner center hub showing menu name or localized `"Cancel"`/`"İptal"`, wedge labels clipped via `OverlayLayout.Ellipsize` (`OverlayWindow.cs:99-123`).
   - Marshalled from input thread via `CoalescedRender<RadialMenuState>` (`src/Tactile.Host/OnScreenKeyboardWindow.cs:10-43`, wired in `src/Tactile.Host/TactileApp.cs:50-51,76,84,295-318,613-639`).
5. **UI editor (`ui/src/**`)**:
   - Add `"Radial menus"` / `"Daire menüler"` subtab in `ui/src/pages/Mapping.tsx:13-23,345-360` with `ui/src/pages/RadialMenus.tsx` (up to 4 menus, name, left/right stick, 2–8 slices with label + `ActionEditor` with `allowRadialMenu={false}`, static SVG preview).
   - Add `"radialMenu"` to `specials` (`ui/src/mapping.ts:104-111`) and menu picker in `ActionEditor` (`ui/src/MappingEditors.tsx:226-237`).

### Out of scope

- Nested/sub-radial menus (a slice's action cannot be `special: "radialMenu"`).
- Per-slice icons, custom colors, themes, animations, or mouse-click selection inside the wheel.
- Touchpad/gyro selection, hold-mode slice actions (slices always tap), or secondary controllers (slots 1–3 stay mapping-free per `docs/contract-phase14.md:133`).
- Changes to `src/Tactile.Host/Bridge.cs`, `src/Tactile.Emulation/**`, `src/Tactile.Drivers/**`, `src/Tactile.Device.*/**`, or `CatalogProfileSummary` (`src/Tactile.Core/Catalog/CatalogManifest.cs:10-46`).

---

## 2. Profile Schema and Validation

`Profile.CurrentVersion` (`src/Tactile.Core/Model/Profile.cs:27`) remains `1`. Old profiles without `radialMenus` deserialize with `RadialMenus = []` and identical runtime behavior.

### JSON additions (`profile.mapping`)

- `mapping.radialMenus`: array of `0..4` objects: `{ id: string (32-char lowercase hex), name: string (1..32), stick: "left" | "right", slices: [{ label: string (0..24), action: ActionConfig }] (2..8) }`.
- `ActionConfig` when `kind == "special"` and `special == "radialMenu"`: `{ kind: "special", special: "radialMenu", radialMenuId: "<id>" }`. Omitted (`null`) for all other actions.

### Validation & `StripRunActions` (`src/Tactile.Core/Storage/MappingValidation.cs`)

- Constants (`MappingValidation.cs:9-17`): `MaxRadialMenus = 4`, `MinRadialSlices = 2`, `MaxRadialSlices = 8`, `MaxRadialMenuNameLength = 32`, `MaxRadialSliceLabelLength = 24`.
- `NormalizeMapping` (`MappingValidation.cs:198-285`): normalize `m.RadialMenus` first to collect surviving `radialMenuIds` (mirroring `NormalizeMacros` at `src/Tactile.Core/Storage/Validation.cs:40-41`):
  - Truncate menus beyond `4` with localized EN/TR issue; deduplicate/generate `Id` via `IsId`/`NewId`; trim/default `Name` (`<= 32` chars); validate `Stick` (`Defined(..., RadialStick.Left)`).
  - Clamp `Slices` to `2..8` (truncate `> 8`, pad `< 2` with empty `{ Label = "", Action = ActionConfig.None }`, emit localized issue); trim `Label` (`<= 24` chars); normalize each slice's `Action` with `allowRadialMenu: false` (nested `radialMenu` becomes `ActionConfig.None` + issue).
- `ActionOf` (`MappingValidation.cs:463-554`): for `ActionKind.Special` + `SpecialAction.RadialMenu`, require `allowRadialMenu && radialMenuIds.Contains(a.RadialMenuId)` else replace with `ActionConfig.None` + localized issue (mirroring `ActionKind.Macro` at `MappingValidation.cs:500-507`). Clear `RadialMenuId` on all other specials.
- `Validation.StripRunActions` (`MappingValidation.cs:20-81`): strip `ActionKind.Run` inside every `m.RadialMenus[].Slices[].Action` and increment `removed`. Automatically covers local import (`src/Tactile.Core/Storage/ProfileStore.cs:223`), share export (`src/Tactile.Core/Catalog/CatalogShareExporter.cs:27`), and catalog signing (`src/Tactile.Core/Catalog/CatalogSigner.cs:159`; raw JSON scan `CatalogProfileFile.ContainsRunAction` at `CatalogProfileFile.cs:14-60` already catches nested `"kind": "run"`).
- Mirror schema clamping in `ui/src/model.ts:273-291,373-407,455-492`.

---

## 3. Bridge and Core-to-Host Interface

- **Host <-> UI Bridge (`src/Tactile.Host/Bridge.cs` <-> `ui/src/host.ts`)**: **no new message types**. Existing `profiles`, `profile.save`, `profile.import`, `profile.export`, and `profile.exportShare` (`Bridge.cs:149-242`) carry `mapping.radialMenus` and `action.radialMenuId`.
- **Core -> Host (`AppCore` -> `TactileApp` -> `RadialMenuWindow`)**:
  - `public sealed record RadialMenuState(bool Visible, string MenuId, string Name, RadialStick Stick, IReadOnlyList<string> Labels, int? SelectedIndex)` (`src/Tactile.Core/Mapping/RadialMenu.cs`).
  - `AppCore` exposes `event Action<RadialMenuState>? RadialMenuChanged`, `bool RadialMenuOpen`, `RadialMenuState RadialMenuSnapshot`, and `void CancelRadialMenu()` (matching `OnScreenKeyboardChanged`/`CloseOnScreenKeyboard` at `src/Tactile.Core/AppCore.cs:407-455`).
  - `internal interface IRadialMenuView : IDisposable { void Render(RadialMenuState state); void Hide(); }` (`src/Tactile.Host/RadialMenuWindow.cs`).

---

## 4. Work Split (Disjoint File Sets)

Job C runs in parallel with Jobs A and B; Job B runs after Job A.

| Job | Scope | Allowed files | Forbidden files | When |
|---|---|---|---|---|
| **A (Core)** | Model (`RadialMenu`, `RadialSlice`, `RadialStick`, `SpecialAction.RadialMenu`, `ActionConfig.RadialMenuId`), `MappingValidation` + `StripRunActions`, `RadialMenuController` state machine & angle math, `ActionDispatcher` open/commit/cancel + tap clock, `Mapper` & `AppCore` single-stick neutralization/cancellation/events, core unit tests | `src/Tactile.Core/Model/Mapping.cs`, `src/Tactile.Core/Storage/MappingValidation.cs`, `src/Tactile.Core/Mapping/RadialMenu.cs` *(new)*, `src/Tactile.Core/Mapping/ActionDispatcher.cs`, `src/Tactile.Core/Mapping/Mapper.cs`, `src/Tactile.Core/AppCore.cs`, `tests/Tactile.Tests/Mapping/RadialMenuTests.cs` *(new)* | `src/Tactile.Host/**`, `src/Tactile.Emulation/**`, `src/Tactile.Drivers/**`, `src/Tactile.Device.*/**`, `src/Tactile.Input*/**`, `src/Tactile.Core/Catalog/**`, `src/Tactile.Core/Mapping/ButtonMachine.cs`, `ui/**`, `docs/**` | First (parallel with C) |
| **B (Host)** | `OverlayLayout` circular radial geometry & centering, `OverlayWindow` elliptic region support, `RadialMenuWindow` (`IRadialMenuView`, flat graphite wedges/labels/highlight), `TactileApp` `CoalescedRender<RadialMenuState>` wiring & disposal, host layout/overlay tests | `src/Tactile.Host/OverlayWindow.cs`, `src/Tactile.Host/RadialMenuWindow.cs` *(new)*, `src/Tactile.Host/TactileApp.cs`, `tests/Tactile.Tests/Host/RadialMenuOverlayTests.cs` *(new)* | `src/Tactile.Core/**`, `src/Tactile.Host/Bridge.cs`, `src/Tactile.Host/MainWindow.cs`, `src/Tactile.Host/OsdOverlay.cs`, `src/Tactile.Host/OnScreenKeyboardWindow.cs`, `ui/**`, `docs/**` | After A (parallel with C) |
| **C (UI)** | TypeScript types, normalization, `"radialMenu"` special option + menu selector in `ActionEditor` (with `allowRadialMenu={false}` in slices), `"Radial menus"` subtab & editor (`RadialMenus.tsx`), EN/TR strings | `ui/src/host.ts`, `ui/src/model.ts`, `ui/src/mapping.ts`, `ui/src/MappingEditors.tsx`, `ui/src/pages/Mapping.tsx`, `ui/src/pages/RadialMenus.tsx` *(new)*, `ui/src/styles.css` | `src/**`, `tests/**`, `docs/**` | Parallel with A & B |

- **Job A implementation note**: update a cached `TimeSpan _now` in `ActionDispatcher.SetTime(now)` (called at the start of `Mapper.OnInput` and `Mapper.Tick`) so `ActionDispatcher.Up` can call `Tap(slice.Action, _now)` on release without changing `ButtonMachine.cs` or other mappers. Call radial cancel at the top of `Mapper.ReleaseAllLocked` (`Mapper.cs:529`) before `_machines` release.

---

## 5. Acceptance Criteria and Mandatory Tests

### Acceptance criteria

1. `dotnet build` and `dotnet test` pass with **0 warnings and 0 errors**; all existing tests pass unmodified.
2. `npm run build` in `ui/` passes with **0 TypeScript/Vite errors**.
3. Holding a button bound to `special: "radialMenu"` opens the menu, tilting the configured stick highlights the matching wedge (`2..8` slices, top = slice 0 clockwise), releasing fires the selected slice once (tap), and releasing while centered cancels with zero output.
4. While open, only the menu's stick is neutralized to `(128, 128)` for `Mapper`, `EmulationService`, and `DsuServer`; the other stick, buttons, triggers, gyro, and touchpad remain active.
5. Pause, profile switch, layer switch, OSK open, and disconnect cancel an open menu without firing any slice.
6. `Validation.StripRunActions` strips `run` actions inside radial slices for both local import and community catalog export/signing; nested `radialMenu` in slices is rejected; legacy `version: 1` profiles without `radialMenus` load unchanged.
7. `RadialMenuWindow` has `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST`, `ShowWithoutActivation == true`, never takes focus, and uses flat graphite palette.

### Mandatory unit tests

- `tests/Tactile.Tests/Mapping/RadialMenuTests.cs` *(new, Job A)*:
  - `Selects_correct_slice_for_2_to_8_slices_and_respects_deadzone_hysteresis`: verifies slice `0` at 12 o'clock clockwise for `N = 2, 4, 8`, enter `>= 0.35`, hold `0.30`, exit `< 0.25`.
  - `Hold_select_and_release_taps_slice_action_and_center_cancels`: holds trigger button, tilts stick to slice, releases -> verifies `Tap` (down + up after 30 ms); repeats with centered stick -> zero injected input.
  - `Neutralizes_only_configured_stick_across_mapper_emulation_and_dsu`: while left-stick radial menu is open, verifies left stick is `(128, 128)` in `Mapper`, `EmulationService`, and `DsuServer`, while right stick, triggers, gyro, and other buttons still pass through (and vice versa for right stick).
  - `Pause_profile_switch_disconnect_and_osk_cancel_without_firing`: opens menu, tilts onto a slice, triggers each cancel path -> verifies `Visible == false`, zero slice output, and held trigger button suppressed until released.
  - `Validation_limits_menus_slices_blocks_nesting_and_strips_run_actions`: verifies max 4 menus, 2–8 slice clamping/padding, nested `radialMenu` stripped, unknown `radialMenuId` cleared, `StripRunActions` removes slice `run` actions (`removed` count matches), and legacy profile JSON loads identically.
- `tests/Tactile.Tests/Host/RadialMenuOverlayTests.cs` *(new, Job B)*:
  - `Radial_layout_centers_on_work_area_scales_with_dpi_and_clamps_small_monitors`: verifies pure `OverlayLayout` radial bounds and wedge label positions at 96/144/192 DPI and small `640x480` work areas.
  - `Radial_window_preserves_passive_overlay_flags_and_coalesces_renders`: verifies `ExStyle`, `WithoutActivation`, `WM_MOUSEACTIVATE` -> `MA_NOACTIVATE`, `WM_NCHITTEST` -> `HTTRANSPARENT`, and `CoalescedRender<RadialMenuState>` dropping intermediate updates.

---

## 6. Safety Rules (Every Job)

- No real hardware, OS input injection, HID/USBIP/HidHide calls, or launching `TriggerForge.exe` in tests; use fakes and `ManualTime`.
- Anti-cheat boundary: `OverlayWindow` only (standard DWM layered window); **no DLL injection, no DirectX/Vulkan/OpenGL hooks, no game-process access**.
- Allowed commands only: `dotnet build`, `dotnet test`, `npm run build` (in `ui/`). No git commits, no new NuGet/npm packages.
- Every new user-visible string requires both English and Turkish (`Text.Pick` / `L(en, tr)`).

---

## 7. Hardware Test Checklist (End of Phase, ~10 min)

1. **Create & bind**: in Mapping -> Radial menus, create a 4-slice menu (left stick) with key/macro/special actions and bind `L1` -> System -> Radial menu -> select menu; save.
2. **Hold, select, release**: in a borderless window (Notepad or game), hold `L1` -> flat graphite wheel appears centered without stealing focus; tilt left stick across slices -> highlight follows smoothly; release on a slice -> action taps once; release at center -> cancels cleanly.
3. **Single-stick isolation**: while holding `L1` with virtual pad or DSU active, verify left stick stays neutral in game/tester while right stick camera and face buttons still work.
4. **Cancel paths**: open menu and tilt onto a slice, then trigger pause hotkey, switch profile, open OSK, or unplug controller -> wheel closes immediately without firing the slice.
5. **Import safety**: export a profile with a `run` action in a radial slice via `Export for sharing` and re-import -> verify `run` action is stripped and notice reports removal.

---

## 8. Risks and Rollback

- **Risk — Slice firing on cancel (`ButtonMachine.ReleaseAll` invokes `ActionDispatcher.Up`)**: mitigated by cancelling `RadialMenuController` at the top of `Mapper.ReleaseAllLocked` before releasing button machines and verifying all 4 cancel paths in unit tests.
- **Risk — Frame-0 stick leak on opening frame into `DsuServer` or `EmulationService`**: mitigated by neutralizing the claimed stick immediately on open inside `Mapper.OnInput` and before `EmulationService.OnInput` / `DsuServer.PublishInput` in `AppCore.ProcessPrimaryInput`. (Note: if `DsuServer.PublishInput` stays at `AppCore.cs:977` before `Mapper.OnInput`, frame 0 is un-neutralized for 1 report (~2–4 ms); moving the DSU publish call after post-mapper neutralization eliminates even that 1-frame leak.)
- **Risk — Exclusive fullscreen visibility**: DWM layered overlays may not appear over true exclusive fullscreen games (same documented limitation as `docs/contract-phase14.md:77`).
- **Rollback**: remove `SpecialAction.RadialMenu` dispatch in `ActionDispatcher` and hide the `"Radial menus"` tab; existing `version: 1` profiles remain valid with zero migration impact.

---

## 9. Review decisions (binding; they override the text above)

Line numbers above predate 1.5.0: locate code by symbol.

- R1. **Ownership and locks.** `AppCore` owns the `RadialMenuController` the way it owns the on-screen keyboard. State changes are queued and raised through the same ordered-event drain as the keyboard, never while `Mapper._gate`, `_mappingLock` or `_primaryInputLock` is held. A cancel that comes from the mapping side never takes `_primaryInputLock`: it sets an atomic flag, and the next primary input frame applies the button hold mask. If the pad disconnects first, the flag is cleared with the rest of the input state.
- R2. **Where `radialMenu` is allowed.** Only as the plain press action of a button binding (base layer or shift layer). Everywhere else (stick ring and stick directions, swipes, touch zones, trigger stages, hold/double/triple, chords, cycle steps, slices, Edge shortcuts) validation replaces it with `None` and reports it. Validation also turns off turbo and toggle on a binding whose press is `radialMenu`, and removes hold/double/triple there, so press never becomes a delayed tap. `ActionDispatcher.Tap` ignores `radialMenu`. `Down` returns `null` when the id is unknown or another menu is open, so that button's `Up` can never close or fire the open menu.
- R3. **Stick release.** Opening cancels a running flick/sweep and stick-held keys of that stick; the flick stick gets the neutral stick too. After the menu closes (commit or cancel, closing frame included) that stick stays neutral for mapping, flick, emulation and DSU until it returns to the centre (`r < 0.25`) once. Disconnect clears it.
- R4. **DSU order.** `PublishDsu` moves after `_mapper.OnInput` and the neutralization, so the opening frame is neutral too. The keyboard's DSU behaviour stays as it is.
- R5. **Files.** Job C may also edit `ui/src/pages/MappingTabs.tsx`; `normalizeBinding` in `ui/src/model.ts` drops references to deleted menus. Job B covers `TactileApp.Exit` and `DisposeOverlays`.
- R6. **Window.** `CreateEllipticRgn`, the same fixed alpha as the on-screen keyboard window. A slice with an empty label shows its number (1-8); an unassigned slice is dimmed and does nothing.
- R7. **Slices tap.** A macro set to repeat while held runs as a tap (30 ms); the editor hint says that slices are taps.
- R8. `ActionDispatcher.SetTime(now)` is called after `Mapper` has resolved its own clock.
