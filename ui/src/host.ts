// Bridge to the C# host (WebView2). Outside the host (vite dev in a browser) a small
// simulator stands in so the UI can be worked on without a controller.

export type Status = {
  type: "status";
  connected: boolean;
  model?: string;
  link?: string;
  mac?: string;
  firmware?: string;
  version: string;
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

export type HostMessage = Status | Input | Output | { type: "error"; message: string };

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

if (webview) {
  webview.addEventListener("message", (e) => emit(e.data as HostMessage));
}

export function send(message: { type: string; [key: string]: unknown }) {
  if (webview) {
    webview.postMessage(message);
  } else {
    simulate(message);
  }
}

// ---- simulator ----

let simOutput: Output = {
  type: "output",
  lightbar: "#000040",
  brightness: 0,
  playerLeds: 0,
  micLed: 0,
  rumbleLeft: 0,
  rumbleRight: 0,
  leftTrigger: "05",
  rightTrigger: "05",
};
let simTimer: number | undefined;

function simulate(m: { type: string; [key: string]: unknown }) {
  switch (m.type) {
    case "hello":
      emit({ type: "status", connected: true, model: "DualSense", link: "Simulated", firmware: "—", version: "dev" });
      emit(simOutput);
      simTimer ??= window.setInterval(simTick, 33);
      return;
    case "lightbar":
      simOutput = { ...simOutput, lightbar: m.color as string };
      break;
    case "brightness":
      simOutput = { ...simOutput, brightness: m.level as number };
      break;
    case "playerLeds":
      simOutput = { ...simOutput, playerLeds: m.mask as number };
      break;
    case "micLed":
      simOutput = { ...simOutput, micLed: m.mode as number };
      break;
    case "rumble":
      simOutput = { ...simOutput, rumbleLeft: m.left as number, rumbleRight: m.right as number };
      break;
    case "trigger":
      simOutput = { ...simOutput, [m.side === "left" ? "leftTrigger" : "rightTrigger"]: String(m.mode) };
      break;
    case "reset":
      simOutput = { ...simOutput, lightbar: "#000040", playerLeds: 0, micLed: 0, rumbleLeft: 0, rumbleRight: 0, leftTrigger: "05", rightTrigger: "05" };
      break;
    default:
      return;
  }
  emit(simOutput);
}

let simReports = 0;
function simTick() {
  const t = performance.now() / 1000;
  const wave = (f: number, p = 0) => Math.sin(t * f + p);
  simReports += 26;
  const buttons: string[] = [];
  if (wave(1.3) > 0.85) buttons.push("Cross");
  if (wave(0.9, 2) > 0.9) buttons.push("R1");
  emit({
    type: "input",
    buttons,
    left: [128 + Math.round(wave(1.1) * 100), 128 + Math.round(wave(1.7) * 100)],
    right: [128 + Math.round(wave(0.6, 1) * 60), 128 + Math.round(wave(0.8, 1) * 60)],
    l2: Math.max(0, Math.round(wave(0.7) * 255)),
    r2: Math.max(0, Math.round(wave(0.5, 3) * 255)),
    gyro: [wave(2) * 40, wave(1.5) * 20, wave(1) * 10],
    accel: [wave(0.3) * 0.2, 0.98, wave(0.4) * 0.15],
    touch: wave(0.4) > 0.3 ? [{ id: 1, x: 960 + Math.round(wave(1.2) * 700), y: 540 + Math.round(wave(0.9) * 400) }] : [],
    battery: 85,
    charging: false,
    headphones: false,
    reports: simReports,
    badCrc: 0,
  });
}
