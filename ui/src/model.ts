import type {
  Action,
  AppSettings,
  Binding,
  ButtonName,
  GyroMouse,
  Macro,
  MacroStep,
  Mapping,
  StickMapping,
  TouchpadMapping,
  Curve,
  CurvePoint,
  EmulationConfig,
  LightEffect,
  Lightbar,
  Profile,
  StickConfig,
  TriggerConfig,
  TriggerMode,
  TriggerResponse,
} from "./host";

// ---- defaults ----

export const defaultTrigger = (): TriggerConfig => ({ mode: "off", params: {}, zones: null, frequency: 0 });
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
});
export const defaultStickMapping = (): StickMapping => ({ mode: "off", keys: { up: "w", down: "s", left: "a", right: "d" }, threshold: 0.5, speed: 1 });
export const defaultMapping = (): Mapping => ({
  enabled: false,
  layerButton: "ps",
  buttons: {},
  layer: {},
  touchpad: defaultTouchpad(),
  gyroMouse: defaultGyroMouse(),
  sticks: { left: defaultStickMapping(), right: defaultStickMapping() },
});
export const defaultMacro = (name: string, id = newId()): Macro => ({ id, name, repeat: "once", steps: [] });

export function newId(): string {
  return crypto.randomUUID().replace(/-/g, "");
}

export function defaultProfile(name = "Varsayılan", id = newId()): Profile {
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
      speaker: "off", speakerApp: "", speakerVolume: 0.8,
    },
    mapping: defaultMapping(),
    macros: [],
    emulation: defaultEmulation(),
  };
}

export function defaultEmulation(): EmulationConfig {
  return { mode: "off", remap: {}, swapSticks: false, hideMappedButtons: true };
}

export function defaultSettings(defaultProfileId = ""): AppSettings {
  return {
    version: 1,
    startWithWindows: false,
    closeToTray: true,
    language: "tr",
    autoProfile: true,
    autoProfileDelayMs: 800,
    defaultProfileId,
    controllerDefaults: {},
    idleSleepMinutes: 15,
    lowBatteryWarnings: [20, 10],
    runAsAdminHint: false,
    emulation: { enabled: true, hideController: true, audio: true, keepAudioDefaults: true, hapticBuffer: 64, speakerPacket: "single36" },
  };
}

// Missing fields take defaults (the contract allows partial objects).
export function normalizeProfile(p: Partial<Profile> & { id: string }): Profile {
  const d = defaultProfile(p.name ?? "Profil", p.id);
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
      clickStrength: typeof h?.clickStrength === "number" && Number.isFinite(h.clickStrength) ? Math.min(1, Math.max(0, h.clickStrength)) : d.haptics.clickStrength,
      speaker: h?.speaker === "system" || h?.speaker === "app" ? h.speaker : d.haptics.speaker,
      speakerApp: typeof h?.speakerApp === "string" ? h.speakerApp.trim().slice(0, 260) : d.haptics.speakerApp,
      speakerVolume: typeof h?.speakerVolume === "number" && Number.isFinite(h.speakerVolume) ? Math.min(1, Math.max(0, h.speakerVolume)) : d.haptics.speakerVolume,
    },
    mapping: normalizeMapping(p.mapping),
    macros: (p.macros ?? []).map(normalizeMacro),
    emulation: { ...defaultEmulation(), ...p.emulation, remap: { ...(p.emulation?.remap ?? {}) } },
  };
}

const normAction = (a?: Partial<Action> | null): Action => (a && a.kind ? { ...a, kind: a.kind } : defaultAction());

export function normalizeBinding(b?: Partial<Binding> | null): Binding {
  const d = defaultBinding();
  if (!b) return d;
  return { ...d, ...b, press: normAction(b.press), hold: b.hold ? normAction(b.hold) : null, double: b.double ? normAction(b.double) : null };
}

function normalizeBindings(m?: Partial<Record<ButtonName, Binding>>): Partial<Record<ButtonName, Binding>> {
  const out: Partial<Record<ButtonName, Binding>> = {};
  for (const [k, v] of Object.entries(m ?? {})) if (v) out[k as ButtonName] = normalizeBinding(v);
  return out;
}

export function normalizeMapping(m?: Partial<Mapping>): Mapping {
  const d = defaultMapping();
  if (!m) return d;
  const t: Partial<TouchpadMapping> = m.touchpad ?? {};
  const layout = t.zoneLayout === "quadrants" ? "quadrants" : "halves";
  const count = layout === "quadrants" ? 4 : 2;
  const zones = Array.from({ length: count }, (_, i) => normalizeBinding(t.zones?.[i]));
  const sw: Partial<TouchpadMapping["swipes"]> = t.swipes ?? {};
  const stick = (s?: Partial<StickMapping>): StickMapping => ({ ...defaultStickMapping(), ...s, keys: { ...defaultStickMapping().keys, ...s?.keys } });
  return {
    ...d,
    ...m,
    buttons: normalizeBindings(m.buttons),
    layer: normalizeBindings(m.layer),
    touchpad: {
      ...d.touchpad,
      ...t,
      zoneLayout: layout,
      zones,
      swipes: { up: normAction(sw.up), down: normAction(sw.down), left: normAction(sw.left), right: normAction(sw.right) },
    },
    gyroMouse: { ...d.gyroMouse, ...m.gyroMouse },
    sticks: { left: stick(m.sticks?.left), right: stick(m.sticks?.right) },
  };
}

export function normalizeMacro(m: Partial<Macro>): Macro {
  return { ...defaultMacro(m.name ?? "Makro", m.id ?? newId()), ...m, steps: (m.steps ?? []).map((s) => ({ ...s }) as MacroStep) };
}

export function normalizeSettings(s: Partial<AppSettings>): AppSettings {
  const d = defaultSettings();
  const emulation = { ...d.emulation, ...s.emulation };
  if (emulation.speakerPacket !== "single36") emulation.speakerPacket = d.emulation.speakerPacket;
  return { ...d, ...s, controllerDefaults: { ...(s.controllerDefaults ?? {}) }, emulation };
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

export const reasonLabel = (reason?: string): string => {
  if (!reason) return "—";
  if (reason === "manual") return "Elle seçildi";
  if (reason.startsWith("game:")) return `Oyun: ${reason.slice(5)}`;
  if (reason === "controllerDefault") return "Kontrolcü varsayılanı";
  return "Varsayılan";
};

export const effects: { id: LightEffect; label: string; hint: string }[] = [
  { id: "off", label: "Kapalı", hint: "Işık çubuğu söner" },
  { id: "static", label: "Sabit", hint: "Tek renk, hareket yok" },
  { id: "breathe", label: "Nefes", hint: "İki renk arasında yavaş geçiş" },
  { id: "strobe", label: "Yanıp sönme", hint: "İki renk arasında keskin geçiş" },
  { id: "rainbow", label: "Gökkuşağı", hint: "Renk tekerinde döner" },
  { id: "battery", label: "Pil", hint: "Doluysa yeşil, azaldıkça kırmızı" },
  { id: "playerColor", label: "Oyuncu rengi", hint: "Oyuncu numarasının rengi" },
];

export const effectLabel = (e: LightEffect) => effects.find((x) => x.id === e)?.label ?? e;

export const brightnessLabel = { high: "Yüksek", medium: "Orta", low: "Düşük" } as const;

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
  { id: "off", label: "Kapalı", edit: "none", params: [] },
  {
    id: "feedback",
    label: "Direnç",
    edit: "start",
    params: [
      { key: "start", label: "Başlangıç", min: 0, max: 9, def: 2 },
      { key: "strength", label: "Güç", min: 1, max: 8, def: 5 },
    ],
  },
  { id: "zones", label: "Bölgesel direnç", edit: "zones", params: [] },
  {
    id: "slope",
    label: "Artan direnç",
    edit: "range",
    params: [
      { key: "start", label: "Başlangıç", min: 0, max: 8, def: 0 },
      { key: "end", label: "Bitiş", min: 1, max: 9, def: 8 },
      { key: "startStrength", label: "İlk güç", min: 1, max: 8, def: 1 },
      { key: "endStrength", label: "Son güç", min: 1, max: 8, def: 8 },
    ],
  },
  {
    id: "weapon",
    label: "Silah",
    edit: "range",
    params: [
      { key: "start", label: "Başlangıç", min: 2, max: 7, def: 3 },
      { key: "end", label: "Kırılma", min: 3, max: 8, def: 6 },
      { key: "strength", label: "Güç", min: 1, max: 8, def: 8 },
    ],
  },
  {
    id: "vibration",
    label: "Titreşim",
    edit: "start",
    params: [
      { key: "start", label: "Başlangıç", min: 0, max: 9, def: 0 },
      { key: "amplitude", label: "Şiddet", min: 1, max: 8, def: 6 },
      { key: "frequency", label: "Frekans", min: 1, max: 120, def: 30 },
    ],
  },
  { id: "zonesVibration", label: "Bölgesel titreşim", edit: "zones", params: [] },
  {
    id: "bow",
    label: "Yay",
    edit: "range",
    params: [
      { key: "start", label: "Başlangıç", min: 0, max: 7, def: 1 },
      { key: "end", label: "Bitiş", min: 1, max: 8, def: 6 },
      { key: "strength", label: "Gerginlik", min: 1, max: 8, def: 6 },
      { key: "snap", label: "Geri tepme", min: 1, max: 8, def: 6 },
    ],
  },
  {
    id: "galloping",
    label: "Dörtnala",
    edit: "range",
    params: [
      { key: "start", label: "Başlangıç", min: 0, max: 8, def: 0 },
      { key: "end", label: "Bitiş", min: 1, max: 9, def: 9 },
      { key: "firstFoot", label: "1. ayak", min: 0, max: 6, def: 1 },
      { key: "secondFoot", label: "2. ayak", min: 1, max: 7, def: 4 },
      { key: "frequency", label: "Frekans", min: 1, max: 20, def: 3 },
    ],
  },
  {
    id: "machine",
    label: "Makine",
    edit: "range",
    params: [
      { key: "start", label: "Başlangıç", min: 0, max: 8, def: 0 },
      { key: "end", label: "Bitiş", min: 1, max: 9, def: 9 },
      { key: "amplitudeA", label: "Şiddet A", min: 0, max: 7, def: 2 },
      { key: "amplitudeB", label: "Şiddet B", min: 0, max: 7, def: 6 },
      { key: "frequency", label: "Frekans", min: 1, max: 60, def: 20 },
      { key: "period", label: "Periyot", min: 1, max: 60, def: 8 },
    ],
  },
];

export const zoneFrequency: ParamDef = { key: "frequency", label: "Frekans", min: 1, max: 120, def: 30 };

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
  if (c.mode === "off") return "Kapalı";
  if (def.edit === "range") return `${def.label} ${p.start}–${p.end}`;
  if (def.edit === "start") return `${def.label} ${p.start}'den`;
  return def.label;
}

export const presets: { name: string; config: TriggerConfig }[] = [
  { name: "Kapalı", config: defaultTrigger() },
  { name: "Tabanca", config: { mode: "weapon", params: { start: 3, end: 5, strength: 6 }, zones: null, frequency: 0 } },
  { name: "Tüfek", config: { mode: "weapon", params: { start: 4, end: 7, strength: 8 }, zones: null, frequency: 0 } },
  { name: "Yay", config: { mode: "bow", params: { start: 1, end: 6, strength: 6, snap: 4 }, zones: null, frequency: 0 } },
  { name: "Fren", config: { mode: "slope", params: { start: 1, end: 9, startStrength: 2, endStrength: 8 }, zones: null, frequency: 0 } },
  { name: "Gaz pedalı", config: { mode: "zones", params: {}, zones: [1, 1, 2, 2, 2, 3, 3, 3, 4, 4], frequency: 0 } },
  { name: "Kilitli tetik", config: { mode: "feedback", params: { start: 0, strength: 8 }, zones: null, frequency: 0 } },
  { name: "Motor titreşimi", config: { mode: "zonesVibration", params: {}, zones: [2, 2, 3, 3, 4, 5, 5, 6, 7, 8], frequency: 40 } },
  { name: "Dörtnala", config: { mode: "galloping", params: { start: 0, end: 9, firstFoot: 2, secondFoot: 5, frequency: 3 }, zones: null, frequency: 0 } },
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

function hexToRgb(hex: string): [number, number, number] {
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
