// Bridge to the C# host (WebView2). Outside the host (vite dev in a browser) a small
// simulator stands in so the UI can be worked on without a controller.
// Shapes follow docs/contract.md.

import { createSimulator } from "./sim";

// ---- profile ----

export type Curve = "linear" | "exponential" | "custom";
export type CurvePoint = [number, number];

export type TriggerMode =
  | "off"
  | "feedback"
  | "zones"
  | "slope"
  | "weapon"
  | "vibration"
  | "zonesVibration"
  | "bow"
  | "galloping"
  | "machine";

export type TriggerConfig = {
  mode: TriggerMode;
  params: Record<string, number>;
  zones: number[] | null;
  frequency: number;
};

export type TriggerResponse = {
  deadzone: number;
  max: number;
  curve: Curve;
  exponent: number;
  points: CurvePoint[] | null;
};

export type StickConfig = {
  innerDeadzone: number;
  outerDeadzone: number;
  shape: "round" | "square";
  curve: Curve;
  exponent: number;
  points: CurvePoint[] | null;
  antiDeadzone: number;
  invertX: boolean;
  invertY: boolean;
};

export type LightEffect = "off" | "static" | "breathe" | "strobe" | "rainbow" | "battery" | "playerColor";
export type Brightness = "high" | "medium" | "low";

export type Lightbar = {
  effect: LightEffect;
  color: string;
  color2: string;
  speed: number;
  brightness: Brightness;
};

export type PlayerLeds = { mode: "off" | "number" | "custom"; number: number; mask: number };
export type MicLed = "off" | "on" | "pulse";

export type Pair<T> = { left: T; right: T };

// ---- mapping (docs/contract-phase3.md) ----

export type ButtonName =
  | "square" | "cross" | "circle" | "triangle"
  | "l1" | "r1" | "l2" | "r2"
  | "create" | "options" | "l3" | "r3" | "ps" | "touchpad" | "mic"
  | "up" | "right" | "down" | "left"
  | "fn1" | "fn2" | "paddleLeft" | "paddleRight";

// camelCase member of Tactile.Input.Key ("a", "digit1", "leftCtrl"); the full list is in keys.ts.
export type KeyName = string;

export type ActionKind = "none" | "key" | "mouseButton" | "wheel" | "macro" | "profile" | "run" | "micMute" | "special";
export type MouseButtonName = "left" | "right" | "middle" | "x1" | "x2";
export type Direction = "up" | "down" | "left" | "right";
export type Special = "screenshot" | "gameBar" | "taskView" | "desktop" | "showApp";

// Flat: `kind` picks which fields are used; the host drops the rest.
export type Action = {
  kind: ActionKind;
  keys?: KeyName[];
  button?: MouseButtonName;
  direction?: Direction;
  amount?: number;
  macroId?: string;
  profileId?: string; // id | "next" | "previous"
  path?: string;
  args?: string;
  special?: Special;
};

export type Binding = {
  press: Action;
  hold: Action | null;
  double: Action | null;
  holdMs: number;
  doubleMs: number;
  turbo: boolean;
  turboHz: number;
  toggle: boolean;
};

export type TouchpadMapping = {
  mode: "off" | "mouse" | "zones";
  sensitivity: number;
  acceleration: number;
  scroll: boolean;
  scrollSpeed: number;
  tapToClick: boolean;
  zoneLayout: "halves" | "quadrants";
  zones: Binding[];
  swipes: Record<Direction, Action>;
};

export type GyroMouse = {
  mode: "off" | "always" | "whileHeld" | "toggle";
  button: ButtonName;
  axis: "yaw" | "roll";
  sensitivityX: number;
  sensitivityY: number;
  invertX: boolean;
  invertY: boolean;
  smoothing: number;
  deadzone: number;
};

export type StickMapping = {
  mode: "off" | "keys" | "mouse" | "scroll";
  keys: Record<Direction, KeyName>; // "none" allowed
  threshold: number;
  speed: number;
};

export type Mapping = {
  enabled: boolean;
  layerButton: ButtonName | "none";
  buttons: Partial<Record<ButtonName, Binding>>;
  layer: Partial<Record<ButtonName, Binding>>;
  touchpad: TouchpadMapping;
  gyroMouse: GyroMouse;
  sticks: Pair<StickMapping>;
};

export type MacroStep =
  | { op: "down" | "up" | "tap"; key: KeyName }
  | { op: "mouseDown" | "mouseUp" | "click"; button: MouseButtonName }
  | { op: "wheel"; direction: Direction; amount: number }
  | { op: "delay"; ms: number };

export type Macro = {
  id: string;
  name: string;
  repeat: "once" | "whileHeld" | "toggle";
  steps: MacroStep[];
};

export type MappingStatus = { active: boolean; paused: boolean; blockedBy: string | null };

// ---- virtual controller (docs/contract-phase4.md) ----

export type EmulationMode = "off" | "xbox360" | "dualSense";

export type EmulationConfig = {
  mode: EmulationMode;
  // source button -> target button or "none"; only listed buttons change
  remap: Partial<Record<ButtonName, ButtonName | "none">>;
  swapSticks: boolean;
  hideMappedButtons: boolean;
};

export type EmulationSettings = { enabled: boolean; hideController: boolean };

export type DriverKind = "usbIp" | "hidHide";
export type DriverState = "missing" | "ready" | "unsupported" | "error";
export type DriverStatus = { kind: DriverKind; state: DriverState; version: string | null; detail: string | null };

export type EmulationStatus = {
  type: "emulation";
  available?: boolean;
  enabled: boolean;
  mode: EmulationMode;
  attached: boolean;
  hidden: boolean;
  drivers: DriverStatus[];
  latency: { p50Ms: number; p95Ms: number; samples: number };
  error: string | null;
};

export type DriverProgress = { type: "driver.progress"; kind: DriverKind; message: string; done: boolean; ok: boolean };

export type HapticsConfig = {
  audio: "off" | "system" | "app";
  app: string;
  mode: "game" | "film" | "music";
  strength: number;
  rumbleToHaptics: boolean;
  clicks: boolean;
  clickStrength: number;
  speaker: HapticsConfig["audio"];
  speakerApp: string;
  speakerVolume: number;
};

export type Profile = {
  version: number;
  id: string;
  name: string;
  games: string[];
  lightbar: Lightbar;
  playerLeds: PlayerLeds;
  micLed: MicLed;
  triggers: Pair<TriggerConfig>;
  triggerResponse: Pair<TriggerResponse>;
  sticks: Pair<StickConfig>;
  gyro: { sensitivity: number; smoothing: number; invertX: boolean; invertY: boolean };
  rumble: { strength: number };
  haptics: HapticsConfig;
  mapping: Mapping;
  macros: Macro[];
  emulation: EmulationConfig;
};

export type AppSettings = {
  version: number;
  startWithWindows: boolean;
  closeToTray: boolean;
  language: "tr" | "en";
  autoProfile: boolean;
  autoProfileDelayMs: number;
  defaultProfileId: string;
  controllerDefaults: Record<string, string>;
  idleSleepMinutes: number;
  lowBatteryWarnings: number[];
  runAsAdminHint: boolean;
  emulation: EmulationSettings;
};

// ---- host → UI ----

export type Status = {
  type: "status";
  connected: boolean;
  model?: string;
  link?: string;
  mac?: string;
  firmware?: string;
  version: string;
  activeProfileId?: string;
  activeReason?: string; // manual | game:<exe> | controllerDefault | default
  mapping?: MappingStatus;
  elevated?: boolean;
};

export type Input = {
  type: "input";
  buttons: string[];
  left: [number, number];
  right: [number, number];
  l2: number;
  r2: number;
  gyro: [number, number, number];
  accel: [number, number, number];
  touch: { id: number; x: number; y: number }[];
  battery: number;
  charging: boolean;
  headphones: boolean;
  reports: number;
  badCrc: number;
  leftProcessed?: [number, number];
  rightProcessed?: [number, number];
};

export type Output = {
  type: "output";
  lightbar: string;
  brightness: number;
  playerLeds: number;
  micLed: number;
  rumbleLeft: number;
  rumbleRight: number;
  leftTrigger: string;
  rightTrigger: string;
};

export type NoticeKind = "lowBattery" | "brokenFile" | "idleSleep" | "gyroCalibration" | "info" | "elevated" | "mapping" | "emulation";

export type AudioApp = { pid: number; name: string; display: string };
export type HapticStatus = { type: "haptics"; streaming: boolean; source: "off" | "system" | `app:${string}`; problem: string | null };

export type HostMessage =
  | Status
  | Input
  | Output
  | { type: "profiles"; list: Profile[]; activeId: string }
  | { type: "settings"; settings: AppSettings }
  | { type: "export"; id: string; json: string }
  | { type: "foreground"; recent: string[] }
  | { type: "runPicked"; path: string | null }
  | { type: "notice"; kind: NoticeKind; message: string; progress?: number }
  | EmulationStatus
  | HapticStatus
  | { type: "audioApps"; apps: AudioApp[] }
  | DriverProgress
  | { type: "error"; message: string };

// ---- UI → host (Phase 2 additions; Phase 1 test messages stay loose) ----

export type UiMessage =
  | { type: "hello" }
  | { type: "profile.save"; profile: Profile }
  | { type: "profile.delete"; id: string }
  | { type: "profile.duplicate"; id: string; name: string }
  | { type: "profile.activate"; id: string }
  | { type: "profile.import"; json: string }
  | { type: "profile.export"; id: string }
  | { type: "trigger.preview"; side: "left" | "right"; config: TriggerConfig | null }
  | { type: "settings.save"; settings: AppSettings }
  | { type: "gyro.calibrate" }
  | { type: "foreground.pick" }
  | { type: "audio.apps" }
  | { type: "mapping.pause"; paused: boolean }
  | { type: "run.pick" }
  | { type: "app.restartElevated" }
  | { type: "emulation.status" }
  | { type: "driver.install"; kind: DriverKind }
  | { type: "emulation.retry" }
  | { type: "emulation.restore" };

type LooseMessage = { type: string; [key: string]: unknown };

type WebView = {
  postMessage(message: unknown): void;
  addEventListener(type: "message", listener: (e: { data: unknown }) => void): void;
  removeEventListener(type: "message", listener: (e: { data: unknown }) => void): void;
};

const webview: WebView | undefined = (window as unknown as { chrome?: { webview?: WebView } }).chrome?.webview;

export const inHost = webview !== undefined;

const listeners = new Set<(m: HostMessage) => void>();

export function subscribe(handler: (message: HostMessage) => void): () => void {
  listeners.add(handler);
  return () => listeners.delete(handler);
}

function emit(m: HostMessage) {
  listeners.forEach((l) => l(m));
}

const simulate = webview ? null : createSimulator(emit);

if (webview) {
  webview.addEventListener("message", (e) => emit(e.data as HostMessage));
}

export function send(message: UiMessage | LooseMessage) {
  if (webview) webview.postMessage(message);
  else simulate?.(message as LooseMessage);
}
