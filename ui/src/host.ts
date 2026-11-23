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
export type Special = "screenshot" | "gameBar" | "taskView" | "desktop" | "showApp" | "onScreenKeyboard";

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
  triple: Action | null;
  cycle: Action[] | null;
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
  mode: "off" | "always" | "whileHeld" | "whileReleased" | "toggle" | "whileTouchpad";
  button: ButtonName;
  axis: "yaw" | "roll";
  sensitivityX: number;
  sensitivityY: number;
  invertX: boolean;
  invertY: boolean;
  smoothing: number;
  deadzone: number;
  useCalibration: boolean;
};

export type StickRingMode = "off" | "outer" | "inner";

export type StickMapping = {
  mode: "off" | "keys" | "mouse" | "scroll";
  keys: Record<Direction, KeyName>; // "none" allowed
  threshold: number;
  speed: number;
  ringMode: StickRingMode;
  ringThreshold: number;
  ringHysteresis: number;
  ringAction: Action;
};

export type LayerMode = "hold" | "toggle";
export type MappingLayer = {
  name: string;
  button: ButtonName | "none";
  mode: LayerMode;
  buttons: Partial<Record<ButtonName, Binding>>;
};

export type ButtonChord = { buttons: ButtonName[]; action: Action };
export type TriggerStageMode = "off" | "additive" | "exclusive" | "hipFire";
export type TriggerMapping = {
  mode: TriggerStageMode;
  softThreshold: number;
  fullThreshold: number;
  hysteresis: number;
  hipFireMs: number;
  soft: Action;
  full: Action;
};

export type FlickSnapMode = "off" | "four" | "eight";
export type FlickStickConfig = {
  enabled: boolean;
  countsPer360: number;
  threshold: number;
  flickTimeMs: number;
  snap: FlickSnapMode;
  deadzoneAngle: number;
  smoothing: number;
};

export type Mapping = {
  enabled: boolean;
  layerButton: ButtonName | "none";
  buttons: Partial<Record<ButtonName, Binding>>;
  layer: Partial<Record<ButtonName, Binding>>;
  layers: MappingLayer[];
  chordWindowMs: number;
  chords: ButtonChord[];
  triggers: Pair<TriggerMapping>;
  flickStick: FlickStickConfig;
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

export type MappingStatus = { active: boolean; paused: boolean; pauseReason: "ui" | "shortcut" | "fullscreen" | null; blockedBy: string | null };

// ---- virtual controller (docs/contract-phase4.md) ----

export type EmulationMode = "off" | "xbox360" | "dualSense" | "dualShock4" | "auto";
// What is actually attached: "auto" always resolves to one of these.
export type ActiveEmulationMode = Exclude<EmulationMode, "auto">;
export type GyroStickConfig = GyroMouse & { antiDeadzone: number };

export type EmulationConfig = {
  mode: EmulationMode;
  // source button -> target button or "none"; only listed buttons change
  remap: Partial<Record<ButtonName, ButtonName | "none">>;
  swapSticks: boolean;
  hideMappedButtons: boolean;
  gyroStick: GyroStickConfig;
};

export type SpeakerPacket = "double39" | "single36";
export type EmulationSettings = {
  enabled: boolean; hideController: boolean; audio: boolean; keepAudioDefaults: boolean; hapticBuffer: number; speakerPacket: SpeakerPacket;
  // lower-case .exe file name -> virtual controller used when a profile is on "auto"
  gameModes: Record<string, Exclude<EmulationMode, "auto">>;
};

export type DriverKind = "usbIp" | "hidHide";
export type DriverState = "missing" | "ready" | "unsupported" | "error";
export type DriverStatus = { kind: DriverKind; state: DriverState; version: string | null; detail: string | null };

export type EmulationStatus = {
  type: "emulation";
  audio: "off" | "ready" | "playing";
  available?: boolean;
  enabled: boolean;
  mode: ActiveEmulationMode;
  configuredMode?: EmulationMode;
  autoGame?: string | null;
  attached: boolean;
  hidden: boolean;
  drivers: DriverStatus[];
  latency: { p50Ms: number; p95Ms: number; samples: number };
  error: string | null;
  slots: { index: number; mode: ActiveEmulationMode; attached: boolean; hidden: boolean; error?: string | null }[];
};

export type DriverProgress = { type: "driver.progress"; kind: DriverKind; message: string; done: boolean; ok: boolean };

export type HapticsConfig = {
  gameAudio: "both" | "haptics" | "speaker" | "off";
  gameStrength: number;
  gameLowPass: boolean;
  audio: "off" | "system" | "app";
  app: string;
  mode: "game" | "film" | "music";
  strength: number;
  rumbleToHaptics: boolean;
  clicks: boolean;
  clickStrength: number;
  confirmPulses: boolean;
  speaker: HapticsConfig["audio"];
  speakerApp: string;
  speakerVolume: number;
};

export type IntegrationSettings = {
  udpEnabled: boolean;
  udpPort: number;
  dsx: boolean;
  dsxPortFile: boolean;
  releaseAfterSeconds: number;
  forzaEnabled: boolean;
  forzaPort: number;
  f1Enabled: boolean;
  f1Port: number;
  acEnabled: boolean;
  beamNgEnabled: boolean;
  beamNgPort: number;
  dsuEnabled: boolean;
  dsuPort: number;
};

export type TelemetrySignal =
  | "raceOn" | "speedKmh" | "rpm" | "gas" | "brake" | "clutch" | "handbrake" | "gear"
  | "steer" | "slipFront" | "slipRear" | "surface" | "rumbleStrip" | "boost" | "abs" | "traction"
  | "revLights" | "drs";
export type TelemetryTarget = "leftTrigger" | "rightTrigger" | "lightbar" | "playerLeds" | "rumbleLeft" | "rumbleRight";
export type TelemetryColor = { r: number; g: number; b: number };
export type TelemetryValues = Record<TelemetrySignal, number>;
export type TelemetryRule = {
  signal: TelemetrySignal;
  from: number;
  to: number;
  exponent: number;
  target: TelemetryTarget;
  trigger: TriggerConfig | null;
  param: string | null;
  min: number;
  max: number;
  colorFrom: TelemetryColor | null;
  colorTo: TelemetryColor | null;
  blinkAt: number | null;
};
export type TelemetryConfig = { rules: TelemetryRule[] };

export type UdpStatus = {
  state: "off" | "listening" | "portBusy";
  port: number;
  packets: number;
  rejected: number;
  unsupported: number;
  lastPacketAgoMs: number | null;
  holding: boolean;
  format: "native" | "dsx" | null;
};
export type ForzaStatus = {
  state: "off" | "listening" | "receiving" | "portBusy" | "portConflict";
  port: number;
  format: "sled" | "fm7" | "fm" | "fh" | null;
  packets: number;
  rejected: number;
  values: TelemetryValues | null;
};
export type TelemetrySource = "forza" | "f1" | "acc" | "ac" | "beamNg";
export type TelemetryTemplate = TelemetrySource;
export type F1Status = {
  state: "off" | "listening" | "receiving" | "portBusy" | "portConflict";
  port: number;
  format: number | null;
  packets: number;
  rejected: number;
  unsupported: number;
  values: TelemetryValues | null;
};
export type AcStatus = {
  state: "off" | "listening" | "receiving";
  game: "ac" | "acc" | null;
  packets: number;
  values: TelemetryValues | null;
};
export type BeamNgStatus = {
  state: "off" | "listening" | "receiving" | "portBusy" | "portConflict";
  port: number;
  packets: number;
  rejected: number;
  values: TelemetryValues | null;
};
export type DsuStatus = {
  state: "off" | "listening" | "portBusy" | "portConflict";
  port: number;
  clients: number;
  packets: number;
  rejected: number;
};
export type IntegrationStatus = { type: "integration"; udp: UdpStatus; forza: ForzaStatus; dsu?: DsuStatus;
  f1?: F1Status; ac?: AcStatus; beamNg?: BeamNgStatus; activeSource?: TelemetrySource | null;
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
  telemetry: TelemetryConfig;
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
  primaryController: string | null;
  autoPlayerLeds: boolean;
  edgeShortcuts: boolean;
  pauseCombo: ButtonName[];
  pauseHotkey: KeyName[];
  pauseOnFullscreen: boolean;
  backupCount: number;
  maxControllers: number;
  idleSleepMinutes: number;
  lowBatteryWarnings: number[];
  runAsAdminHint: boolean;
  emulation: EmulationSettings;
  integration: IntegrationSettings;
  updates: UpdateSettings;
  osd: OsdSettings;
  firstRunDone: boolean;
};

export type OsdCorner = "topLeft" | "topRight" | "bottomLeft" | "bottomRight";
export type OsdSize = "small" | "medium" | "large";
export type OsdSettings = { enabled: boolean; corner: OsdCorner; durationSeconds: number; size: OsdSize; opacity: number };

export type UpdateSettings = { autoCheck: boolean };

// ---- host → UI ----

export type UpdateState = "idle" | "checking" | "upToDate" | "available" | "downloading" | "ready" | "error";
export type UpdateError = "network" | "untrustedHost" | "tooLarge" | "badSignature" | "badManifest" | "badFile" | "badSigner";

export type UpdateStatus = {
  type: "update.status";
  state: UpdateState;
  current: string;
  latest: string | null;
  notes: string | null;
  released: string | null;
  progress: number | null;
  portable: boolean;
  error: UpdateError | null;
  message: string | null;
  lastCheck: string | null;
};

// ---- community catalog (docs/contract-phase14.md) ----

export type CatalogController = "dualsense" | "edge" | "any";
export type CatalogError = "network" | "untrustedHost" | "tooLarge" | "badSignature" | "badManifest" | "badFile" | "outdated" | "storage" | "failed";
export type CatalogProfileSummary = {
  leftTrigger: TriggerMode;
  rightTrigger: TriggerMode;
  emulation: EmulationMode;
  mappingEnabled: boolean;
  layerCount: number;
  chordCount: number;
  macroCount: number;
  gyroMouse: GyroMouse["mode"];
  gyroStick: GyroMouse["mode"];
  flickStick: boolean;
  audioHaptics: boolean;
  telemetryRules: number;
};
// Every text field comes from a third party: render as text only.
export type CatalogEntry = {
  id: string;
  name: string;
  games: string[];
  controller: CatalogController;
  tags: string[];
  author: string;
  version: string;
  size: number;
  description: { en: string; tr: string };
  summary: CatalogProfileSummary;
};
export type CatalogStatus = {
  type: "catalog.status";
  state: "idle" | "loading" | "ready" | "importing" | "error";
  updated: string | null;
  profiles: CatalogEntry[];
  importingId: string | null;
  error: CatalogError | null;
  message: string | null;
};
export type CatalogPreview = {
  type: "catalog.preview";
  id: string;
  summary: CatalogProfileSummary;
  description: string;
  games: string[];
  author: string;
  version: string;
  controller: CatalogController;
  tags: string[];
};
export type ExportShare = { type: "exportShare"; id: string; fileName: string; json: string; entryJson: string; removedRunActions: number };

export type ControllerStatus = {
  index: number;
  mac: string | null;
  model: "DualSense" | "DualSenseEdge";
  link: "Bluetooth" | "Usb";
  battery: number | null;
  charging: boolean;
  primary: boolean;
  profileId: string;
  profileName: string;
};

export type Status = {
  type: "status";
  connected: boolean;
  controllers: ControllerStatus[];
  model?: string;
  link?: string;
  mac?: string;
  firmware?: string;
  version: string;
  activeProfileId?: string;
  activeReason?: string; // manual | game:<exe> | controllerDefault | default
  mapping?: MappingStatus;
  elevated?: boolean;
  portable?: boolean;
};

export type Input = {
  type: "input";
  mac?: string | null;
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

export type ProfileBackup = { id: string; date: string; profileCount: number };

export type HostMessage =
  | Status
  | Input
  | Output
  | { type: "profiles"; list: Profile[]; activeId: string }
  | { type: "backups"; list: ProfileBackup[] }
  | { type: "settings"; settings: AppSettings }
  | { type: "export"; id: string; json: string }
  | { type: "logs.exported"; ok: boolean; path?: string; error?: string }
  | { type: "foreground"; recent: string[] }
  | { type: "runPicked"; path: string | null }
  | { type: "notice"; kind: NoticeKind; message: string; progress?: number }
  | EmulationStatus
  | IntegrationStatus
  | HapticStatus
  | { type: "audioApps"; apps: AudioApp[] }
  | DriverProgress
  | UpdateStatus
  | CatalogStatus
  | CatalogPreview
  | ExportShare
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
  | { type: "controller.setPrimary"; mac: string }
  | { type: "controller.setProfile"; index: number; mac: string; profileId: string }
  | { type: "update.check" | "update.download" | "update.install" | "update.openPage" }
  | { type: "catalog.list"; refresh?: boolean }
  | { type: "catalog.preview" | "catalog.import"; id: string }
  | { type: "catalog.openRepo" }
  | { type: "profile.exportShare"; id: string }
  | { type: "logs.export" | "logs.open" }
  | { type: "gyro.calibrate" }
  | { type: "foreground.pick" }
  | { type: "audio.apps" }
  | { type: "mapping.pause"; paused: boolean }
  | { type: "mapping.togglePause" }
  | { type: "mapping.testTurn"; counts: number; delayMs?: number }
  | { type: "backups.list" }
  | { type: "backups.restore"; id: string }
  | { type: "run.pick" }
  | { type: "app.restartElevated" }
  | { type: "emulation.status" }
  | { type: "integration.status" | "integration.release" }
  | { type: "telemetry.applyTemplate"; profileId: string; template: TelemetryTemplate }
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
