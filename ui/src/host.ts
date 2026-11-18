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

export type NoticeKind = "lowBattery" | "brokenFile" | "idleSleep" | "gyroCalibration" | "info";

export type HostMessage =
  | Status
  | Input
  | Output
  | { type: "profiles"; list: Profile[]; activeId: string }
  | { type: "settings"; settings: AppSettings }
  | { type: "export"; id: string; json: string }
  | { type: "foreground"; recent: string[] }
  | { type: "notice"; kind: NoticeKind; message: string; progress?: number }
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
  | { type: "foreground.pick" };

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
