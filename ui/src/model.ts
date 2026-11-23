import { L } from "./i18n";
import { isKey } from "./keys";
import type {
  Action,
  AppSettings,
  Binding,
  ButtonName,
  GyroMouse,
  Macro,
  MacroStep,
  Mapping,
  MappingLayer,
  ButtonChord,
  TriggerMapping,
  FlickStickConfig,
  StickMapping,
  TouchpadMapping,
  Curve,
  CurvePoint,
  EmulationConfig,
  EmulationMode,
  EmulationSettings,
  LightEffect,
  Lightbar,
  Profile,
  StickConfig,
  TelemetryRule,
  TriggerConfig,
  TriggerMode,
  TriggerResponse,
} from "./host";

// ---- defaults ----

export const emulationModes: EmulationMode[] = ["off", "xbox360", "dualSense", "dualShock4", "auto"];
export const defaultTrigger = (): TriggerConfig => ({ mode: "off", params: {}, zones: null, frequency: 0 });
export const defaultTelemetryRule = (): TelemetryRule => ({
  signal: "brake", from: 0, to: 1, exponent: 1, target: "leftTrigger",
  trigger: defaultTrigger(), param: null, min: 0, max: 0,
  colorFrom: { r: 0, g: 0, b: 0 }, colorTo: { r: 0, g: 0, b: 0 }, blinkAt: null,
});
export const defaultResponse = (): TriggerResponse => ({ deadzone: 0, max: 255, curve: "linear", exponent: 1, points: null });
export const defaultStick = (): StickConfig => ({
  innerDeadzone: 0.05,
  outerDeadzone: 0.02,
  shape: "round",
  curve: "linear",
  exponent: 1,
  points: null,
  antiDeadzone: 0,
  invertX: false,
  invertY: false,
});

export const defaultAction = (): Action => ({ kind: "none" });
export const defaultBinding = (): Binding => ({
  press: defaultAction(),
  hold: null,
  double: null,
  triple: null,
  cycle: null,
  holdMs: 400,
  doubleMs: 250,
  turbo: false,
  turboHz: 10,
  toggle: false,
});
export const defaultTouchpad = (): TouchpadMapping => ({
  mode: "off",
  sensitivity: 1,
  acceleration: 0.5,
  scroll: true,
  scrollSpeed: 1,
  tapToClick: true,
  zoneLayout: "halves",
  zones: [defaultBinding(), defaultBinding()],
  swipes: { up: defaultAction(), down: defaultAction(), left: defaultAction(), right: defaultAction() },
});
export const defaultGyroMouse = (): GyroMouse => ({
  mode: "off",
  button: "r1",
  axis: "yaw",
  sensitivityX: 1,
  sensitivityY: 1,
  invertX: false,
  invertY: false,
  smoothing: 0.2,
  deadzone: 0.5,
  useCalibration: false,
});
export const defaultStickMapping = (): StickMapping => ({
  mode: "off", keys: { up: "w", down: "s", left: "a", right: "d" }, threshold: 0.5, speed: 1,
  ringMode: "off", ringThreshold: 0.9, ringHysteresis: 0.05, ringAction: defaultAction(),
});
export const maxLayers = 4;
export const maxLayerNameLength = 32;
export const maxChords = 32;
export const defaultMappingLayer = (index = 0): MappingLayer => ({
  name: L(`Layer ${index + 1}`, `Katman ${index + 1}`),
  button: index === 0 ? "ps" : "none", mode: "hold", buttons: {},
});
export const defaultTriggerMapping = (): TriggerMapping => ({
  mode: "off", softThreshold: 0.25, fullThreshold: 0.95, hysteresis: 0.05, hipFireMs: 80,
  soft: defaultAction(), full: defaultAction(),
});
export const defaultFlickStick = (): FlickStickConfig => ({
  enabled: false, countsPer360: 6545, threshold: 0.9, flickTimeMs: 100,
  snap: "off", deadzoneAngle: 0, smoothing: 0.2,
});
export const withMappingLayers = (mapping: Mapping, layers: MappingLayer[]): Mapping => ({
  ...mapping, layers, layerButton: layers[0].button, layer: layers[0].buttons,
});
export function defaultMapping(): Mapping {
  const first = defaultMappingLayer();
  return {
    enabled: false,
    layerButton: first.button,
    buttons: {},
    layer: first.buttons,
    layers: [first],
    chordWindowMs: 50,
    chords: [],
    triggers: { left: defaultTriggerMapping(), right: defaultTriggerMapping() },
    flickStick: defaultFlickStick(),
    touchpad: defaultTouchpad(),
    gyroMouse: defaultGyroMouse(),
    sticks: { left: defaultStickMapping(), right: defaultStickMapping() },
  };
}
export const defaultMacro = (name: string, id = newId()): Macro => ({ id, name, repeat: "once", steps: [] });

export function newId(): string {
  return crypto.randomUUID().replace(/-/g, "");
}

export function defaultProfile(name = L("Default", "Varsayılan"), id = newId()): Profile {
  return {
    version: 1,
    id,
    name,
    games: [],
    lightbar: { effect: "static", color: "#3060FF", color2: "#000000", speed: 1, brightness: "high" },
    playerLeds: { mode: "number", number: 1, mask: 0 },
    micLed: "off",
    triggers: { left: defaultTrigger(), right: defaultTrigger() },
    triggerResponse: { left: defaultResponse(), right: defaultResponse() },
    sticks: { left: defaultStick(), right: defaultStick() },
    gyro: { sensitivity: 1, smoothing: 0.2, invertX: false, invertY: false },
    rumble: { strength: 1 },
    haptics: {
      gameAudio: "both", gameStrength: 1, gameLowPass: false,
      audio: "off", app: "", mode: "game", strength: 1, rumbleToHaptics: false, clicks: false, clickStrength: 0.6,
      speaker: "off", speakerApp: "", speakerVolume: 0.8, confirmPulses: false,
    },
    mapping: defaultMapping(),
    macros: [],
    emulation: defaultEmulation(),
    telemetry: { rules: [] },
  };
}

export function defaultEmulation(): EmulationConfig {
  return {
    mode: "off", remap: {}, swapSticks: false, hideMappedButtons: true,
    gyroStick: { ...defaultGyroMouse(), button: "l2", antiDeadzone: 0 },
  };
}

export function defaultSettings(defaultProfileId = ""): AppSettings {
  return {
    version: 1,
    startWithWindows: false,
    closeToTray: true,
    language: "en",
    autoProfile: true,
    autoProfileDelayMs: 800,
    defaultProfileId,
    controllerDefaults: {},
    primaryController: null,
    autoPlayerLeds: true,
    edgeShortcuts: true,
    pauseCombo: [],
    pauseHotkey: [],
    pauseOnFullscreen: false,
    backupCount: 7,
    maxControllers: 4,
    idleSleepMinutes: 15,
    lowBatteryWarnings: [20, 10],
    runAsAdminHint: false,
    updates: { autoCheck: true },
    firstRunDone: false,
    emulation: { enabled: true, hideController: true, audio: true, keepAudioDefaults: true, hapticBuffer: 64, speakerPacket: "single36", gameModes: {} },
    integration: {
      udpEnabled: false, udpPort: 6969, dsx: true, dsxPortFile: true,
      releaseAfterSeconds: 0, forzaEnabled: false, forzaPort: 5555,
      f1Enabled: false, f1Port: 20777, acEnabled: false, beamNgEnabled: false, beamNgPort: 4444,
      dsuEnabled: false, dsuPort: 26760,
    },
  };
}

// Missing fields take defaults (the contract allows partial objects).
export function normalizeProfile(p: Partial<Profile> & { id: string }): Profile {
  const d = defaultProfile(p.name ?? L("Profile", "Profil"), p.id);
  const h = p.haptics;
  const trig = (t?: Partial<TriggerConfig>): TriggerConfig => ({
    ...defaultTrigger(),
    ...t,
    params: { ...(t?.params ?? {}) },
    zones: t?.zones ?? null,
    frequency: t?.frequency ?? 0,
  });
  return {
    ...d,
    ...p,
    games: p.games ?? [],
    lightbar: { ...d.lightbar, ...p.lightbar },
    playerLeds: { ...d.playerLeds, ...p.playerLeds },
    triggers: { left: trig(p.triggers?.left), right: trig(p.triggers?.right) },
    triggerResponse: {
      left: { ...defaultResponse(), ...p.triggerResponse?.left },
      right: { ...defaultResponse(), ...p.triggerResponse?.right },
    },
    sticks: { left: { ...defaultStick(), ...p.sticks?.left }, right: { ...defaultStick(), ...p.sticks?.right } },
    gyro: { ...d.gyro, ...p.gyro },
    rumble: { ...d.rumble, ...p.rumble },
    haptics: {
      gameAudio: h?.gameAudio === "haptics" || h?.gameAudio === "speaker" || h?.gameAudio === "off" ? h.gameAudio : d.haptics.gameAudio,
      gameStrength: typeof h?.gameStrength === "number" && Number.isFinite(h.gameStrength) ? Math.min(2, Math.max(0, h.gameStrength)) : d.haptics.gameStrength,
      gameLowPass: h?.gameLowPass === true,
      audio: h?.audio === "system" || h?.audio === "app" ? h.audio : d.haptics.audio,
      app: typeof h?.app === "string" ? h.app.trim().slice(0, 260) : d.haptics.app,
      mode: h?.mode === "film" || h?.mode === "music" ? h.mode : d.haptics.mode,
      strength: typeof h?.strength === "number" && Number.isFinite(h.strength) ? Math.min(2, Math.max(0, h.strength)) : d.haptics.strength,
      rumbleToHaptics: h?.rumbleToHaptics === true,
      clicks: h?.clicks === true,
      confirmPulses: h?.confirmPulses === true,
      clickStrength: typeof h?.clickStrength === "number" && Number.isFinite(h.clickStrength) ? Math.min(1, Math.max(0, h.clickStrength)) : d.haptics.clickStrength,
      speaker: h?.speaker === "system" || h?.speaker === "app" ? h.speaker : d.haptics.speaker,
      speakerApp: typeof h?.speakerApp === "string" ? h.speakerApp.trim().slice(0, 260) : d.haptics.speakerApp,
      speakerVolume: typeof h?.speakerVolume === "number" && Number.isFinite(h.speakerVolume) ? Math.min(1, Math.max(0, h.speakerVolume)) : d.haptics.speakerVolume,
    },
    mapping: normalizeMapping(p.mapping),
    macros: (p.macros ?? []).map(normalizeMacro),
    emulation: {
      ...d.emulation,
      ...p.emulation,
      mode: emulationModes.includes(p.emulation?.mode as EmulationMode) ? p.emulation!.mode : d.emulation.mode,
      remap: { ...(p.emulation?.remap ?? {}) },
      gyroStick: {
        ...normalizeGyro(p.emulation?.gyroStick, d.emulation.gyroStick),
        antiDeadzone: bounded(p.emulation?.gyroStick?.antiDeadzone, 0, 0.5, d.emulation.gyroStick.antiDeadzone),
      },
    },
    telemetry: {
      rules: (p.telemetry?.rules ?? []).slice(0, 32).map((rule) => {
        const defaults = defaultTelemetryRule();
        return {
          ...defaults,
          ...rule,
          trigger: rule.trigger ? trig(rule.trigger) : null,
          colorFrom: rule.colorFrom ?? defaults.colorFrom,
          colorTo: rule.colorTo ?? defaults.colorTo,
        };
      }),
    },
  };
}

const normAction = (a?: Partial<Action> | null): Action => (a && a.kind ? { ...a, kind: a.kind } : defaultAction());

function optionalAction(a?: Action | null): Action | null {
  switch (a?.kind) {
    case "key":
      return Array.isArray(a.keys) && a.keys.some((key) => isKey(key)) ? a : null;
    case "macro":
      return typeof a.macroId === "string" && a.macroId.trim() ? a : null;
    case "profile":
      return typeof a.profileId === "string" && a.profileId.trim() ? a : null;
    case "run":
      return typeof a.path === "string" && a.path.trim() ? a : null;
    case "mouseButton":
    case "wheel":
    case "micMute":
    case "special":
      return a;
    default:
      return null;
  }
}

export function normalizeBinding(b?: Partial<Binding> | null): Binding {
  const d = defaultBinding();
  if (!b) return d;
  const cycle = Array.isArray(b.cycle)
    ? b.cycle.map(optionalAction).filter((action) => action !== null).slice(0, 8)
    : [];
  return {
    ...d, ...b,
    press: normAction(b.press),
    hold: b.hold ? normAction(b.hold) : null,
    double: b.double ? normAction(b.double) : null,
    triple: optionalAction(b.triple),
    cycle: cycle.length ? cycle : null,
    holdMs: Math.trunc(bounded(b.holdMs, 150, 2000, d.holdMs)),
    doubleMs: Math.trunc(bounded(b.doubleMs, 100, 600, d.doubleMs)),
  };
}

const bounded = (value: unknown, min: number, max: number, fallback: number) =>
  typeof value === "number" && Number.isFinite(value) ? clamp(value, min, max) : fallback;

const buttonNames: ButtonName[] = [
  "square", "cross", "circle", "triangle", "l1", "r1", "l2", "r2",
  "create", "options", "l3", "r3", "ps", "touchpad", "mic",
  "up", "right", "down", "left", "fn1", "fn2", "paddleLeft", "paddleRight",
];

function normalizeButton(value: unknown): ButtonName | undefined {
  if (typeof value !== "string") return undefined;
  const name = value.toLowerCase();
  return buttonNames.find((button) => button.toLowerCase() === name);
}

function normalizeGyro(g: Partial<GyroMouse> | undefined, d: GyroMouse): GyroMouse {
  const mode = g?.mode;
  return {
    mode: mode === "always" || mode === "whileHeld" || mode === "whileReleased" || mode === "toggle" || mode === "whileTouchpad" ? mode : d.mode,
    button: normalizeButton(g?.button) ?? d.button,
    axis: g?.axis === "roll" ? "roll" : d.axis,
    sensitivityX: bounded(g?.sensitivityX, 0.1, 10, d.sensitivityX),
    sensitivityY: bounded(g?.sensitivityY, 0.1, 10, d.sensitivityY),
    smoothing: bounded(g?.smoothing, 0, 0.9, d.smoothing),
    deadzone: bounded(g?.deadzone, 0, 5, d.deadzone),
    invertX: g?.invertX === true,
    invertY: g?.invertY === true,
    useCalibration: g?.useCalibration === true,
  };
}

function normalizeBindings(m?: Partial<Record<ButtonName, Binding>>): Partial<Record<ButtonName, Binding>> {
  const out: Partial<Record<ButtonName, Binding>> = {};
  for (const [key, binding] of Object.entries(m ?? {})) {
    const button = normalizeButton(key);
    if (button && binding && typeof binding === "object") out[button] = normalizeBinding(binding);
  }
  return out;
}

type MappingInput = Omit<Partial<Mapping>, "layerButton"> & { layerButton?: string };

function normalizeLayers(m: MappingInput): MappingLayer[] {
  const raw = Array.isArray(m.layers) && m.layers.length
    ? m.layers : [{ button: m.layerButton, buttons: m.layer, name: undefined, mode: undefined }];
  const used = new Set<ButtonName>();
  return raw.slice(0, maxLayers).map((layer, index) => {
    const d = defaultMappingLayer(index);
    const value = layer?.button;
    const name = typeof value === "string" ? value.trim().toLowerCase() : undefined;
    let button = name === "none" ? "none" : normalizeButton(name) ?? d.button;
    if (button !== "none") {
      if (used.has(button)) button = "none";
      else used.add(button);
    }
    return {
      name: (typeof layer?.name === "string" ? layer.name.trim() : "").slice(0, maxLayerNameLength) || d.name,
      button, mode: layer?.mode === "toggle" ? "toggle" : "hold", buttons: normalizeBindings(layer?.buttons),
    };
  });
}

function normalizeMappingAction(value?: Action | null): Action {
  const action = optionalAction(value);
  switch (action?.kind) {
    case "key":
      return { kind: "key", keys: [...new Set(action.keys?.filter(isKey))].slice(0, 4) };
    case "mouseButton": {
      const button = action.button;
      return { kind: "mouseButton", button: button === "right" || button === "middle" || button === "x1" || button === "x2" ? button : "left" };
    }
    case "wheel": {
      const direction = action.direction;
      return {
        kind: "wheel", direction: direction === "up" || direction === "left" || direction === "right" ? direction : "down",
        amount: Math.trunc(bounded(action.amount, 1, 10, 1)),
      };
    }
    case "macro":
      return { kind: "macro", macroId: action.macroId?.trim() };
    case "profile":
      return { kind: "profile", profileId: action.profileId?.trim() };
    case "run": {
      const path = action.path?.trim() ?? "";
      if (path.length > 260 || !/^(?:[a-z]:[\\/]|[\\/]{2})/i.test(path)) return defaultAction();
      return { kind: "run", path, args: typeof action.args === "string" ? action.args.slice(0, 512) : "" };
    }
    case "micMute":
      return { kind: "micMute" };
    case "special": {
      const special = action.special;
      return { kind: "special", special: special === "gameBar" || special === "taskView" || special === "desktop" || special === "showApp" ? special : "screenshot" };
    }
    default:
      return defaultAction();
  }
}

function normalizeChords(raw: Mapping["chords"] | undefined): ButtonChord[] {
  const chords: ButtonChord[] = [];
  const seen = new Set<string>();
  if (!Array.isArray(raw)) return chords;
  for (const chord of raw) {
    if (!Array.isArray(chord?.buttons)) continue;
    const selected = new Set(chord.buttons.map(normalizeButton));
    const buttons = buttonNames.filter((b) => selected.has(b)).slice(0, 3);
    const action = normalizeMappingAction(chord.action);
    const key = buttons.join(",");
    if (buttons.length < 2 || action.kind === "none" || seen.has(key)) continue;
    chords.push({ buttons, action });
    seen.add(key);
    if (chords.length === maxChords) break;
  }
  return chords;
}

function normalizeTriggerMapping(t?: Partial<TriggerMapping>): TriggerMapping {
  const d = defaultTriggerMapping();
  const softThreshold = bounded(t?.softThreshold, 0.05, 0.9, d.softThreshold);
  const mode = t?.mode;
  return {
    mode: mode === "additive" || mode === "exclusive" || mode === "hipFire" ? mode : d.mode,
    softThreshold,
    fullThreshold: Math.max(softThreshold + 0.05, bounded(t?.fullThreshold, 0.15, 1, d.fullThreshold)),
    hysteresis: bounded(t?.hysteresis, 0.01, 0.2, d.hysteresis),
    hipFireMs: Math.trunc(bounded(t?.hipFireMs, 20, 300, d.hipFireMs)),
    soft: normalizeMappingAction(t?.soft),
    full: normalizeMappingAction(t?.full),
  };
}

function normalizeFlickStick(f?: Partial<FlickStickConfig>): FlickStickConfig {
  const d = defaultFlickStick();
  return {
    enabled: f?.enabled === true,
    countsPer360: Math.trunc(bounded(f?.countsPer360, 500, 30000, d.countsPer360)),
    threshold: bounded(f?.threshold, 0.5, 0.98, d.threshold),
    flickTimeMs: Math.trunc(bounded(f?.flickTimeMs, 30, 300, d.flickTimeMs)),
    snap: f?.snap === "four" || f?.snap === "eight" ? f.snap : d.snap,
    deadzoneAngle: bounded(f?.deadzoneAngle, 0, 45, d.deadzoneAngle),
    smoothing: bounded(f?.smoothing, 0, 0.9, d.smoothing),
  };
}

export function normalizeMapping(m?: MappingInput): Mapping {
  const d = defaultMapping();
  if (!m) return d;
  const t: Partial<TouchpadMapping> = m.touchpad ?? {};
  const layout = t.zoneLayout === "quadrants" ? "quadrants" : "halves";
  const count = layout === "quadrants" ? 4 : 2;
  const zones = Array.from({ length: count }, (_, i) => normalizeBinding(t.zones?.[i]));
  const sw: Partial<TouchpadMapping["swipes"]> = t.swipes ?? {};
  const stick = (s?: Partial<StickMapping>): StickMapping => {
    const base = defaultStickMapping();
    return {
      ...base, ...s, keys: { ...base.keys, ...s?.keys },
      ringMode: s?.ringMode === "inner" || s?.ringMode === "outer" ? s.ringMode : base.ringMode,
      ringThreshold: bounded(s?.ringThreshold, 0.2, 0.98, base.ringThreshold),
      ringHysteresis: bounded(s?.ringHysteresis, 0.01, 0.2, base.ringHysteresis),
      ringAction: normalizeMappingAction(s?.ringAction),
    };
  };
  return withMappingLayers({
    ...d,
    ...m,
    layerButton: d.layerButton,
    buttons: normalizeBindings(m.buttons),
    chordWindowMs: Math.trunc(bounded(m.chordWindowMs === 0 ? undefined : m.chordWindowMs, 20, 300, d.chordWindowMs)),
    chords: normalizeChords(m.chords),
    triggers: { left: normalizeTriggerMapping(m.triggers?.left), right: normalizeTriggerMapping(m.triggers?.right) },
    flickStick: normalizeFlickStick(m.flickStick),
    touchpad: {
      ...d.touchpad,
      ...t,
      zoneLayout: layout,
      zones,
      swipes: { up: normAction(sw.up), down: normAction(sw.down), left: normAction(sw.left), right: normAction(sw.right) },
    },
    gyroMouse: normalizeGyro(m.gyroMouse, d.gyroMouse),
    sticks: { left: stick(m.sticks?.left), right: stick(m.sticks?.right) },
  }, normalizeLayers(m));
}

export function normalizeMacro(m: Partial<Macro>): Macro {
  return { ...defaultMacro(m.name ?? L("Macro", "Makro"), m.id ?? newId()), ...m, steps: (m.steps ?? []).map((s) => ({ ...s }) as MacroStep) };
}

export const maxGameModeOverrides = 256;

// Lower-case .exe file name, as the host stores it; undefined when the text is not a usable name.
export function normalizeExeName(value: unknown): string | undefined {
  if (typeof value !== "string") return undefined;
  const name = value.trim().split(/[\\/]/).pop()?.trim().toLowerCase() ?? "";
  if (!name || name === ".exe" || /[<>:"|?*\u0000-\u001f]/.test(name)) return undefined;
  return name.endsWith(".exe") ? name : `${name}.exe`;
}

function normalizeGameModes(value: unknown): EmulationSettings["gameModes"] {
  const out: EmulationSettings["gameModes"] = {};
  if (!value || typeof value !== "object") return out;
  for (const [key, mode] of Object.entries(value)) {
    const name = normalizeExeName(key);
    if (name && (mode === "off" || mode === "xbox360" || mode === "dualSense" || mode === "dualShock4")) out[name] = mode;
    if (Object.keys(out).length >= maxGameModeOverrides) break;
  }
  return out;
}

export function normalizeSettings(s: Partial<AppSettings>): AppSettings {
  const d = defaultSettings();
  const emulation = { ...d.emulation, ...s.emulation, gameModes: normalizeGameModes(s.emulation?.gameModes) };
  if (emulation.speakerPacket !== "single36") emulation.speakerPacket = d.emulation.speakerPacket;
  const dsuPort = typeof s.integration?.dsuPort === "number" && Number.isFinite(s.integration.dsuPort) && s.integration.dsuPort > 0
    ? Math.min(65535, Math.max(1024, Math.trunc(s.integration.dsuPort))) : d.integration.dsuPort;
  const port = (value: unknown, fallback: number) =>
    typeof value === "number" && Number.isFinite(value) ? Math.min(65535, Math.max(1024, Math.trunc(value))) : fallback;
  const primaryController = s.primaryController?.trim().replaceAll("-", ":").toUpperCase();
  const combo = Array.isArray(s.pauseCombo)
    ? [...new Set(s.pauseCombo.map(normalizeButton).filter((button) => button !== undefined))].slice(0, 3)
    : [];
  const hotkey = Array.isArray(s.pauseHotkey)
    ? [...new Set(s.pauseHotkey.filter((key) => isKey(key)))].slice(0, 4)
    : [];
  return {
    ...d, ...s,
    language: s.language === "tr" ? "tr" : "en",
    controllerDefaults: { ...(s.controllerDefaults ?? {}) },
    primaryController: primaryController || null,
    autoPlayerLeds: s.autoPlayerLeds !== false,
    edgeShortcuts: s.edgeShortcuts !== false,
    pauseCombo: combo.length >= 2 ? combo : [],
    pauseHotkey: hotkey,
    pauseOnFullscreen: s.pauseOnFullscreen === true,
    backupCount: Math.trunc(bounded(s.backupCount, 0, 30, d.backupCount)),
    maxControllers: typeof s.maxControllers === "number" && Number.isFinite(s.maxControllers)
      ? Math.min(4, Math.max(1, Math.trunc(s.maxControllers)))
      : d.maxControllers,
    emulation,
    integration: { ...d.integration, ...s.integration, dsuEnabled: s.integration?.dsuEnabled === true, dsuPort,
      f1Enabled: s.integration?.f1Enabled === true, f1Port: port(s.integration?.f1Port, d.integration.f1Port),
      acEnabled: s.integration?.acEnabled === true,
      beamNgEnabled: s.integration?.beamNgEnabled === true, beamNgPort: port(s.integration?.beamNgPort, d.integration.beamNgPort),
    },
    updates: { autoCheck: s.updates?.autoCheck !== false },
    firstRunDone: s.firstRunDone === true,
  };
}

export function clone<T>(v: T): T {
  return JSON.parse(JSON.stringify(v)) as T;
}

// Key-order independent; undefined and missing are the same.
export function deepEqual(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (typeof a !== "object" || typeof b !== "object" || a === null || b === null) return false;
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  const ka = Object.keys(a as object).filter((k) => (a as Record<string, unknown>)[k] !== undefined);
  const kb = Object.keys(b as object).filter((k) => (b as Record<string, unknown>)[k] !== undefined);
  if (ka.length !== kb.length) return false;
  return ka.every((k) => deepEqual((a as Record<string, unknown>)[k], (b as Record<string, unknown>)[k]));
}

// ---- labels ----

export const linkLabel: Record<string, string> = { Bluetooth: "Bluetooth", Usb: "USB", get Simulated() { return L("simulation", "simülasyon"); } };

export const reasonLabel = (reason?: string): string => {
  if (!reason) return "—";
  if (reason === "manual") return L("Selected manually", "Elle seçildi");
  if (reason.startsWith("game:")) return L(`Game: ${reason.slice(5)}`, `Oyun: ${reason.slice(5)}`);
  if (reason === "controllerDefault") return L("Controller default", "Kontrolcü varsayılanı");
  return L("Default", "Varsayılan");
};

export const effects: { id: LightEffect; label: string; hint: string }[] = [
  { id: "off", get label() { return L("Off", "Kapalı"); }, get hint() { return L("Turns off the light bar", "Işık çubuğu söner"); } },
  { id: "static", get label() { return L("Static", "Sabit"); }, get hint() { return L("One steady color", "Tek renk, hareket yok"); } },
  { id: "breathe", get label() { return L("Breathe", "Nefes"); }, get hint() { return L("Fades between two colors", "İki renk arasında yavaş geçiş"); } },
  { id: "strobe", get label() { return L("Flash", "Yanıp sönme"); }, get hint() { return L("Switches sharply between two colors", "İki renk arasında keskin geçiş"); } },
  { id: "rainbow", get label() { return L("Rainbow", "Gökkuşağı"); }, get hint() { return L("Cycles through colors", "Renk tekerinde döner"); } },
  { id: "battery", get label() { return L("Battery", "Pil"); }, get hint() { return L("Green when full, red when low", "Doluysa yeşil, azaldıkça kırmızı"); } },
  { id: "playerColor", get label() { return L("Player color", "Oyuncu rengi"); }, get hint() { return L("Color based on the player number", "Oyuncu numarasının rengi"); } },
];

export const effectLabel = (e: LightEffect) => effects.find((x) => x.id === e)?.label ?? e;

export const brightnessLabel = { get high() { return L("High", "Yüksek"); }, get medium() { return L("Medium", "Orta"); }, get low() { return L("Low", "Düşük"); } } as const;

// ---- trigger modes ----

export type ParamDef = { key: string; label: string; min: number; max: number; def: number };

type ModeDef = {
  id: TriggerMode;
  label: string;
  // zones: strength per zone is edited on the graph
  // range: start/end handles on the track; start: a single handle
  edit: "none" | "zones" | "range" | "start";
  params: ParamDef[];
};

export const triggerModes: ModeDef[] = [
  { id: "off", get label() { return L("Off", "Kapalı"); }, edit: "none", params: [] },
  {
    id: "feedback",
    get label() { return L("Resistance", "Direnç"); },
    edit: "start",
    params: [
      { key: "start", get label() { return L("Start", "Başlangıç"); }, min: 0, max: 9, def: 2 },
      { key: "strength", get label() { return L("Strength", "Güç"); }, min: 1, max: 8, def: 5 },
    ],
  },
  { id: "zones", get label() { return L("Zone resistance", "Bölgesel direnç"); }, edit: "zones", params: [] },
  {
    id: "slope",
    get label() { return L("Rising resistance", "Artan direnç"); },
    edit: "range",
    params: [
      { key: "start", get label() { return L("Start", "Başlangıç"); }, min: 0, max: 8, def: 0 },
      { key: "end", get label() { return L("End", "Bitiş"); }, min: 1, max: 9, def: 8 },
      { key: "startStrength", get label() { return L("Start strength", "İlk güç"); }, min: 1, max: 8, def: 1 },
      { key: "endStrength", get label() { return L("End strength", "Son güç"); }, min: 1, max: 8, def: 8 },
    ],
  },
  {
    id: "weapon",
    get label() { return L("Weapon", "Silah"); },
    edit: "range",
    params: [
      { key: "start", get label() { return L("Start", "Başlangıç"); }, min: 2, max: 7, def: 3 },
      { key: "end", get label() { return L("Break point", "Kırılma"); }, min: 3, max: 8, def: 6 },
      { key: "strength", get label() { return L("Strength", "Güç"); }, min: 1, max: 8, def: 8 },
    ],
  },
  {
    id: "vibration",
    get label() { return L("Vibration", "Titreşim"); },
    edit: "start",
    params: [
      { key: "start", get label() { return L("Start", "Başlangıç"); }, min: 0, max: 9, def: 0 },
      { key: "amplitude", get label() { return L("Intensity", "Şiddet"); }, min: 1, max: 8, def: 6 },
      { key: "frequency", get label() { return L("Frequency", "Frekans"); }, min: 1, max: 120, def: 30 },
    ],
  },
  { id: "zonesVibration", get label() { return L("Zone vibration", "Bölgesel titreşim"); }, edit: "zones", params: [] },
  {
    id: "bow",
    get label() { return L("Bow", "Yay"); },
    edit: "range",
    params: [
      { key: "start", get label() { return L("Start", "Başlangıç"); }, min: 0, max: 7, def: 1 },
      { key: "end", get label() { return L("End", "Bitiş"); }, min: 1, max: 8, def: 6 },
      { key: "strength", get label() { return L("Tension", "Gerginlik"); }, min: 1, max: 8, def: 6 },
      { key: "snap", get label() { return L("Kickback", "Geri tepme"); }, min: 1, max: 8, def: 6 },
    ],
  },
  {
    id: "galloping",
    get label() { return L("Gallop", "Dörtnala"); },
    edit: "range",
    params: [
      { key: "start", get label() { return L("Start", "Başlangıç"); }, min: 0, max: 8, def: 0 },
      { key: "end", get label() { return L("End", "Bitiş"); }, min: 1, max: 9, def: 9 },
      { key: "firstFoot", get label() { return L("First foot", "1. ayak"); }, min: 0, max: 6, def: 1 },
      { key: "secondFoot", get label() { return L("Second foot", "2. ayak"); }, min: 1, max: 7, def: 4 },
      { key: "frequency", get label() { return L("Frequency", "Frekans"); }, min: 1, max: 20, def: 3 },
    ],
  },
  {
    id: "machine",
    get label() { return L("Machine", "Makine"); },
    edit: "range",
    params: [
      { key: "start", get label() { return L("Start", "Başlangıç"); }, min: 0, max: 8, def: 0 },
      { key: "end", get label() { return L("End", "Bitiş"); }, min: 1, max: 9, def: 9 },
      { key: "amplitudeA", get label() { return L("Intensity A", "Şiddet A"); }, min: 0, max: 7, def: 2 },
      { key: "amplitudeB", get label() { return L("Intensity B", "Şiddet B"); }, min: 0, max: 7, def: 6 },
      { key: "frequency", get label() { return L("Frequency", "Frekans"); }, min: 1, max: 60, def: 20 },
      { key: "period", get label() { return L("Period", "Periyot"); }, min: 1, max: 60, def: 8 },
    ],
  },
];

export const zoneFrequency: ParamDef = { key: "frequency", get label() { return L("Frequency", "Frekans"); }, min: 1, max: 120, def: 30 };

export const modeDef = (m: TriggerMode) => triggerModes.find((d) => d.id === m) ?? triggerModes[0];
export const isZoneMode = (m: TriggerMode) => m === "zones" || m === "zonesVibration";

export const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));

// Resistance per zone (0-8) for drawing, and whether that zone vibrates.
export function zoneProfile(c: TriggerConfig): { level: number[]; vibe: boolean[] } {
  const p = c.params;
  const level = Array<number>(10).fill(0);
  const vibe = Array<boolean>(10).fill(false);
  const s = p.start ?? 0;
  const e = p.end ?? 9;
  for (let i = 0; i < 10; i++) {
    switch (c.mode) {
      case "feedback":
        level[i] = i >= s ? p.strength ?? 0 : 0;
        break;
      case "zones":
      case "zonesVibration":
        level[i] = c.zones?.[i] ?? 0;
        vibe[i] = c.mode === "zonesVibration" && level[i] > 0;
        break;
      case "slope":
        if (i >= s) {
          const t = e > s ? clamp((i - s) / (e - s), 0, 1) : 1;
          level[i] = Math.round((p.startStrength ?? 1) + ((p.endStrength ?? 8) - (p.startStrength ?? 1)) * t);
        }
        break;
      case "weapon":
        level[i] = i >= s && i <= e ? p.strength ?? 0 : 0;
        break;
      case "vibration":
        level[i] = i >= s ? p.amplitude ?? 0 : 0;
        vibe[i] = i >= s;
        break;
      case "bow":
        if (i >= s && i <= e) level[i] = Math.max(1, Math.round(((i - s + 1) / (e - s + 1)) * (p.strength ?? 0)));
        break;
      case "galloping":
        if (i >= s && i <= e) {
          level[i] = clamp((p.secondFoot ?? 4) + 1, 1, 8);
          vibe[i] = true;
        }
        break;
      case "machine":
        if (i >= s && i <= e) {
          level[i] = clamp(Math.max(p.amplitudeA ?? 0, p.amplitudeB ?? 0) + 1, 1, 8);
          vibe[i] = true;
        }
        break;
    }
  }
  return { level, vibe };
}

// Switching mode keeps what the user had drawn where it makes sense.
export function withMode(c: TriggerConfig, mode: TriggerMode): TriggerConfig {
  if (mode === c.mode) return c;
  const def = modeDef(mode);
  const params: Record<string, number> = {};
  for (const f of def.params) params[f.key] = c.params[f.key] !== undefined ? clamp(c.params[f.key], f.min, f.max) : f.def;
  if (params.end !== undefined && params.start !== undefined && params.end <= params.start) params.end = params.start + 1;
  if (isZoneMode(mode)) {
    const zones = c.zones ?? zoneProfile(c).level;
    return { mode, params: {}, zones: zones.some((z) => z > 0) ? [...zones] : [0, 0, 2, 3, 4, 5, 6, 7, 8, 8], frequency: mode === "zonesVibration" ? c.frequency || zoneFrequency.def : 0 };
  }
  return { mode, params, zones: null, frequency: 0 };
}

export function triggerSummary(c: TriggerConfig): string {
  const def = modeDef(c.mode);
  const p = c.params;
  if (c.mode === "off") return L("Off", "Kapalı");
  if (def.edit === "range") return L(`${def.label} (zones ${p.start + 1}–${p.end + 1})`, `${def.label} (bölge ${p.start + 1}–${p.end + 1})`);
  if (def.edit === "start") return L(`${def.label} (zones ${p.start + 1}–10)`, `${def.label} (bölge ${p.start + 1}–10)`);
  return def.label;
}

export const presets: { name: string; config: TriggerConfig }[] = [
  { get name() { return L("Off", "Kapalı"); }, config: defaultTrigger() },
  { get name() { return L("Pistol", "Tabanca"); }, config: { mode: "weapon", params: { start: 3, end: 5, strength: 6 }, zones: null, frequency: 0 } },
  { get name() { return L("Rifle", "Tüfek"); }, config: { mode: "weapon", params: { start: 4, end: 7, strength: 8 }, zones: null, frequency: 0 } },
  { get name() { return L("Bow", "Yay"); }, config: { mode: "bow", params: { start: 1, end: 6, strength: 6, snap: 4 }, zones: null, frequency: 0 } },
  { get name() { return L("Brake", "Fren"); }, config: { mode: "slope", params: { start: 1, end: 9, startStrength: 2, endStrength: 8 }, zones: null, frequency: 0 } },
  { get name() { return L("Accelerator", "Gaz pedalı"); }, config: { mode: "zones", params: {}, zones: [1, 1, 2, 2, 2, 3, 3, 3, 4, 4], frequency: 0 } },
  { get name() { return L("Locked trigger", "Kilitli tetik"); }, config: { mode: "feedback", params: { start: 0, strength: 8 }, zones: null, frequency: 0 } },
  { get name() { return L("Engine vibration", "Motor titreşimi"); }, config: { mode: "zonesVibration", params: {}, zones: [2, 2, 3, 3, 4, 5, 5, 6, 7, 8], frequency: 40 } },
  { get name() { return L("Gallop", "Dörtnala"); }, config: { mode: "galloping", params: { start: 0, end: 9, firstFoot: 2, secondFoot: 5, frequency: 3 }, zones: null, frequency: 0 } },
];

// ---- shaping (mirrors what the core does; used for the draft preview and the simulator) ----

export function applyCurve(v: number, curve: Curve, exponent: number, points: CurvePoint[] | null): number {
  v = clamp(v, 0, 1);
  if (curve === "exponential") return Math.pow(v, exponent);
  if (curve === "custom" && points && points.length >= 2) {
    for (let i = 1; i < points.length; i++) {
      const [x0, y0] = points[i - 1];
      const [x1, y1] = points[i];
      if (v <= x1) return x1 > x0 ? y0 + ((v - x0) / (x1 - x0)) * (y1 - y0) : y1;
    }
    return points[points.length - 1][1];
  }
  return v;
}

export function sampleCurve(curve: Curve, exponent: number, points: CurvePoint[] | null): CurvePoint[] {
  return [0, 0.25, 0.5, 0.75, 1].map((x) => [x, Math.round(applyCurve(x, curve, exponent, points) * 100) / 100]);
}

function shapeAxis(m: number, c: StickConfig): number {
  const span = 1 - c.innerDeadzone - c.outerDeadzone;
  if (m <= c.innerDeadzone || span <= 0) return 0;
  const t = clamp((m - c.innerDeadzone) / span, 0, 1);
  const y = applyCurve(t, c.curve, c.exponent, c.points);
  return c.antiDeadzone + (1 - c.antiDeadzone) * y;
}

// x, y in -1..1
export function processStick(x: number, y: number, c: StickConfig): [number, number] {
  if (c.invertX) x = -x;
  if (c.invertY) y = -y;
  if (c.shape === "square") return [Math.sign(x) * shapeAxis(Math.abs(x), c), Math.sign(y) * shapeAxis(Math.abs(y), c)];
  const m = Math.hypot(x, y);
  if (m === 0) return [0, 0];
  const k = shapeAxis(Math.min(1, m), c) / m;
  return [clamp(x * k, -1, 1), clamp(y * k, -1, 1)];
}

export const rawAxis = (v: number) => clamp((v - 127.5) / 127.5, -1, 1);

// 0-255 pressure → 0-1 after deadzone, max and curve
export function processTrigger(v: number, r: TriggerResponse): number {
  if (v <= r.deadzone) return 0;
  const t = clamp((v - r.deadzone) / Math.max(1, r.max - r.deadzone), 0, 1);
  return applyCurve(t, r.curve, r.exponent, r.points);
}

// ---- lightbar effects (preview + simulator) ----

const playerColors = ["#2050FF", "#FF2828", "#20D040", "#FF40B0"];

export function hexToRgb(hex: string): [number, number, number] {
  const n = parseInt(hex.replace("#", "").padEnd(6, "0").slice(0, 6), 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

export function rgbToHex(r: number, g: number, b: number): string {
  return "#" + [r, g, b].map((v) => Math.round(clamp(v, 0, 255)).toString(16).padStart(2, "0")).join("").toUpperCase();
}

function mix(a: string, b: string, t: number): string {
  const x = hexToRgb(a);
  const y = hexToRgb(b);
  return rgbToHex(x[0] + (y[0] - x[0]) * t, x[1] + (y[1] - x[1]) * t, x[2] + (y[2] - x[2]) * t);
}

function hue(h: number): string {
  const f = (n: number) => {
    const k = (n + h / 60) % 6;
    return 255 * (1 - Math.max(0, Math.min(k, 4 - k, 1)));
  };
  return rgbToHex(f(5), f(3), f(1));
}

export function effectColor(l: Lightbar, t: number, battery: number, player: number): string {
  switch (l.effect) {
    case "off":
      return "#000000";
    case "breathe":
      return mix(l.color2, l.color, (Math.sin(t * Math.PI * 2 * l.speed * 0.5) + 1) / 2);
    case "strobe":
      return (t * l.speed) % 1 < 0.5 ? l.color : l.color2;
    case "rainbow":
      return hue((t * l.speed * 72) % 360);
    case "battery":
      return mix("#FF2010", "#20E040", clamp(battery / 100, 0, 1));
    case "playerColor":
      return playerColors[clamp(player, 1, 4) - 1];
    default:
      return l.color;
  }
}

export const brightnessFactor = { high: 1, medium: 0.62, low: 0.3 } as const;

// DualSense player number patterns on the five LEDs.
export const playerMasks = [0, 0b00100, 0b01010, 0b10101, 0b11011];

export function ledMask(p: Profile["playerLeds"]): number {
  if (p.mode === "off") return 0;
  if (p.mode === "number") return playerMasks[clamp(p.number, 1, 4)];
  return p.mask & 0b11111;
}
