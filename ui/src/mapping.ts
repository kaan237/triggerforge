import { L } from "./i18n";
// Labels, summaries and small helpers for the Tuşlar page.

import { useEffect } from "react";
import { send, subscribe, type Action, type ActionKind, type Binding, type ButtonName, type Direction, type FlickSnapMode, type GyroMouse, type LayerMode, type Macro, type Mapping, type MouseButtonName, type Profile, type Special, type StickRingMode, type TriggerStageMode } from "./host";
import { chordLabel } from "./keys";
import { defaultBinding, defaultMapping, defaultMappingLayer, withMappingLayers } from "./model";

export const buttonDefs: { id: ButtonName; label: string; group: string }[] = [
  { id: "cross", get label() { return L("Cross", "Çarpı"); }, get group() { return L("Face", "Yüz"); } },
  { id: "circle", get label() { return L("Circle", "Daire"); }, get group() { return L("Face", "Yüz"); } },
  { id: "square", get label() { return L("Square", "Kare"); }, get group() { return L("Face", "Yüz"); } },
  { id: "triangle", get label() { return L("Triangle", "Üçgen"); }, get group() { return L("Face", "Yüz"); } },
  { id: "up", get label() { return L("D-pad up", "Yön yukarı"); }, get group() { return L("Direction", "Yön"); } },
  { id: "down", get label() { return L("D-pad down", "Yön aşağı"); }, get group() { return L("Direction", "Yön"); } },
  { id: "left", get label() { return L("D-pad left", "Yön sol"); }, get group() { return L("Direction", "Yön"); } },
  { id: "right", get label() { return L("D-pad right", "Yön sağ"); }, get group() { return L("Direction", "Yön"); } },
  { id: "l1", label: "L1", get group() { return L("Shoulders", "Omuz"); } },
  { id: "r1", label: "R1", get group() { return L("Shoulders", "Omuz"); } },
  { id: "l2", label: "L2", get group() { return L("Shoulders", "Omuz"); } },
  { id: "r2", label: "R2", get group() { return L("Shoulders", "Omuz"); } },
  { id: "l3", label: "L3", get group() { return L("Stick", "Analog"); } },
  { id: "r3", label: "R3", get group() { return L("Stick", "Analog"); } },
  { id: "create", label: "Create", get group() { return L("Medium", "Orta"); } },
  { id: "options", label: "Options", get group() { return L("Medium", "Orta"); } },
  { id: "ps", label: "PS", get group() { return L("Medium", "Orta"); } },
  { id: "touchpad", get label() { return L("Touchpad click", "Touchpad tık"); }, get group() { return L("Medium", "Orta"); } },
  { id: "mic", get label() { return L("Microphone", "Mikrofon"); }, get group() { return L("Medium", "Orta"); } },
  { id: "fn1", label: "Fn1", group: "Edge" },
  { id: "fn2", label: "Fn2", group: "Edge" },
  { id: "paddleLeft", get label() { return L("Left rear", "Sol arka"); }, group: "Edge" },
  { id: "paddleRight", get label() { return L("Right rear", "Sağ arka"); }, group: "Edge" },
];

export const buttonLabel = (id: ButtonName | "none") => (id === "none" ? L("None", "Yok") : buttonDefs.find((b) => b.id === id)?.label ?? id);

// The input message sends PascalCase ("Cross", "PS"); profile keys are camelCase.
export const fromInput = (name: string): ButtonName | undefined => buttonDefs.find((b) => b.id.toLowerCase() === name.toLowerCase())?.id;
export const toInput = (id: ButtonName): string => (id === "ps" ? "PS" : id[0].toUpperCase() + id.slice(1));

export const gyroModes: { id: GyroMouse["mode"]; label: string }[] = [
  { id: "off", get label() { return L("Off", "Kapalı"); } },
  { id: "always", get label() { return L("Always on", "Hep açık"); } },
  { id: "whileHeld", get label() { return L("While held", "Basılıyken"); } },
  { id: "whileReleased", get label() { return L("While button not held", "Tuş basılı değilken"); } },
  { id: "toggle", get label() { return L("Toggle", "Aç/kapa"); } },
  { id: "whileTouchpad", get label() { return L("While touching touchpad", "Touchpad'e dokunulurken"); } },
];

export const actionKinds: { id: ActionKind; label: string }[] = [
  { id: "none", get label() { return L("None", "Yok"); } },
  { id: "key", get label() { return L("Keyboard key", "Klavye tuşu"); } },
  { id: "mouseButton", get label() { return L("Mouse button", "Fare tuşu"); } },
  { id: "wheel", get label() { return L("Mouse wheel", "Fare tekerleği"); } },
  { id: "macro", get label() { return L("Macro", "Makro"); } },
  { id: "profile", get label() { return L("Switch profile", "Profil değiştir"); } },
  { id: "run", get label() { return L("Launch program", "Program çalıştır"); } },
  { id: "micMute", get label() { return L("Toggle microphone mute", "Mikrofonu sustur"); } },
  { id: "special", get label() { return L("System", "Sistem"); } },
];

export const layerModes: { id: LayerMode; label: string }[] = [
  { id: "hold", get label() { return L("Hold", "Basılı tut"); } },
  { id: "toggle", get label() { return L("Toggle", "Aç/kapa"); } },
];

export const triggerStageModes: { id: TriggerStageMode; label: string }[] = [
  { id: "off", get label() { return L("Off", "Kapalı"); } },
  { id: "additive", get label() { return L("Additive", "Birlikte"); } },
  { id: "exclusive", get label() { return L("Exclusive", "Tek kademe"); } },
  { id: "hipFire", get label() { return L("Hip fire", "Hızlı tam basış"); } },
];

export const stickRingModes: { id: StickRingMode; label: string }[] = [
  { id: "off", get label() { return L("Off", "Kapalı"); } },
  { id: "outer", get label() { return L("Outer", "Dış"); } },
  { id: "inner", get label() { return L("Inner", "İç"); } },
];

export const flickSnapModes: { id: FlickSnapMode; label: string }[] = [
  { id: "off", get label() { return L("Off", "Kapalı"); } },
  { id: "four", get label() { return L("Four directions", "Dört yön"); } },
  { id: "eight", get label() { return L("Eight directions", "Sekiz yön"); } },
];

export const mouseButtons: { id: MouseButtonName; label: string }[] = [
  { id: "left", get label() { return L("Left", "Sol"); } },
  { id: "right", get label() { return L("Right", "Sağ"); } },
  { id: "middle", get label() { return L("Medium", "Orta"); } },
  { id: "x1", get label() { return L("Back", "Geri"); } },
  { id: "x2", get label() { return L("Forward", "İleri"); } },
];
const clickLabel: Record<MouseButtonName, string> = { get left() { return L("Left click", "Sol tık"); }, get right() { return L("Right click", "Sağ tık"); }, get middle() { return L("Middle click", "Orta tık"); }, get x1() { return L("Back button", "Geri tuşu"); }, get x2() { return L("Forward button", "İleri tuşu"); } };
export const mouseLabel = (b?: MouseButtonName) => clickLabel[b ?? "left"];

export const directions: { id: Direction; label: string }[] = [
  { id: "up", get label() { return L("Up", "Yukarı"); } },
  { id: "down", get label() { return L("Down", "Aşağı"); } },
  { id: "left", get label() { return L("Left", "Sol"); } },
  { id: "right", get label() { return L("Right", "Sağ"); } },
];
export const directionLabel = (d?: Direction) => directions.find((x) => x.id === d)?.label ?? L("Down", "Aşağı");

export const specials: { id: Special; label: string; hint: string }[] = [
  { id: "screenshot", get label() { return L("Screenshot", "Ekran görüntüsü"); }, hint: "Win + Print Screen" },
  { id: "gameBar", get label() { return L("Game Bar", "Oyun çubuğu"); }, hint: "Win + G" },
  { id: "taskView", get label() { return L("Task View", "Görev görünümü"); }, hint: "Win + Tab" },
  { id: "desktop", get label() { return L("Show desktop", "Masaüstünü göster"); }, hint: "Win + D" },
  { id: "showApp", get label() { return L("Show TriggerForge", "Tactile'ı aç"); }, get hint() { return L("Brings this window to the front", "Bu pencereyi öne getirir"); } },
];

// A fresh action of that kind with its own fields filled.
export function actionOfKind(kind: ActionKind): Action {
  switch (kind) {
    case "key":
      return { kind, keys: [] };
    case "mouseButton":
      return { kind, button: "left" };
    case "wheel":
      return { kind, direction: "down", amount: 1 };
    case "macro":
      return { kind, macroId: "" };
    case "profile":
      return { kind, profileId: "next" };
    case "run":
      return { kind, path: "", args: "" };
    case "special":
      return { kind, special: "screenshot" };
    default:
      return { kind };
  }
}

export function actionSummary(a: Action | null | undefined, profiles: Profile[], macros: Macro[]): string {
  if (!a) return "—";
  switch (a.kind) {
    case "key":
      return chordLabel(a.keys);
    case "mouseButton":
      return mouseLabel(a.button);
    case "wheel":
      return L(`Scroll ${directionLabel(a.direction).toLowerCase()} ×${a.amount ?? 1}`, `Tekerlek ${directionLabel(a.direction).toLocaleLowerCase("tr")} ×${a.amount ?? 1}`);
    case "macro":
      return L(`Macro: ${macros.find((m) => m.id === a.macroId)?.name ?? "not selected"}`, `Makro: ${macros.find((m) => m.id === a.macroId)?.name ?? "seçilmedi"}`);
    case "profile":
      if (a.profileId === "next") return L("Next profile", "Sonraki profil");
      if (a.profileId === "previous") return L("Previous profile", "Önceki profil");
      return L(`Profile: ${profiles.find((p) => p.id === a.profileId)?.name ?? "unknown"}`, `Profil: ${profiles.find((p) => p.id === a.profileId)?.name ?? "bilinmiyor"}`);
    case "run":
      return a.path ? L(`Launch: ${a.path.split(/[\\/]/).pop()}`, `Çalıştır: ${a.path.split(/[\\/]/).pop()}`) : L("Launch: no program selected", "Çalıştır: seçilmedi");
    case "micMute":
      return L("Toggle microphone mute", "Mikrofonu sustur");
    case "special":
      return specials.find((s) => s.id === a.special)?.label ?? L("System", "Sistem");
    default:
      return "—";
  }
}

export function bindingSummary(b: Binding | undefined, profiles: Profile[], macros: Macro[]): string {
  if (!b) return "—";
  if (b.cycle?.length) return b.cycle.map((action) => actionSummary(action, profiles, macros)).join(" → ");
  const parts = [actionSummary(b.press, profiles, macros)];
  if (b.hold) parts.push(L(`hold: ${actionSummary(b.hold, profiles, macros)}`, `tut: ${actionSummary(b.hold, profiles, macros)}`));
  if (b.double) parts.push(L(`double: ${actionSummary(b.double, profiles, macros)}`, `çift: ${actionSummary(b.double, profiles, macros)}`));
  if (b.triple) parts.push(L(`triple: ${actionSummary(b.triple, profiles, macros)}`, `üçlü: ${actionSummary(b.triple, profiles, macros)}`));
  if (!b.hold && !b.double && !b.triple && b.turbo) parts.push(L("turbo", "turbo"));
  else if (!b.hold && !b.double && !b.triple && b.toggle) parts.push(L("latch", "kilit"));
  return parts.join(" · ");
}

export const isEmptyBinding = (b: Binding) => {
  const d = defaultBinding();
  return b.press.kind === "none" && !b.hold && !b.double && !b.triple && !b.cycle?.length
    && !b.turbo && !b.toggle && b.holdMs === d.holdMs && b.doubleMs === d.doubleMs && b.turboHz === d.turboHz;
};

// ---- mapping.pause while any capture field or the recorder has focus ----

let holders = 0;
function hold(delta: number) {
  const before = holders > 0;
  holders = Math.max(0, holders + delta);
  const after = holders > 0;
  if (before !== after) send({ type: "mapping.pause", paused: after });
}

export function usePause(on: boolean) {
  useEffect(() => {
    if (!on) return;
    hold(1);
    return () => hold(-1);
  }, [on]);
}

// ---- run.pick: one requester at a time ----

let pickTarget: ((path: string) => void) | null = null;
subscribe((m) => {
  if (m.type !== "runPicked") return;
  const target = pickTarget;
  pickTarget = null;
  if (target && m.path) target(m.path);
});

export function pickProgram(onPicked: (path: string) => void) {
  pickTarget = onPicked;
  send({ type: "run.pick" });
}

// ---- desktop template ----

const key = (...keys: string[]): Binding => ({ ...defaultBinding(), press: { kind: "key", keys } });
const click = (button: MouseButtonName): Binding => ({ ...defaultBinding(), press: { kind: "mouseButton", button } });
const one = (a: Action): Binding => ({ ...defaultBinding(), press: a });

export function desktopTemplate(current: Mapping): Mapping {
  const d = defaultMapping();
  const layer = {
    ...defaultMappingLayer(),
    buttons: {
      options: one({ kind: "special", special: "showApp" }),
      create: one({ kind: "special", special: "taskView" }),
      triangle: one({ kind: "special", special: "desktop" }),
      l1: key("leftCtrl", "leftShift", "tab"),
      r1: key("leftCtrl", "tab"),
    },
  };
  return withMappingLayers({
    ...current,
    enabled: true,
    buttons: {
      cross: click("left"),
      circle: click("right"),
      square: key("escape"),
      triangle: key("leftWin"),
      up: key("up"),
      down: key("down"),
      left: key("left"),
      right: key("right"),
      l1: key("leftAlt", "left"),
      r1: key("leftAlt", "right"),
      l2: one({ kind: "wheel", direction: "up", amount: 3 }),
      r2: one({ kind: "wheel", direction: "down", amount: 3 }),
      r3: click("middle"),
      options: key("enter"),
      create: one({ kind: "special", special: "screenshot" }),
      touchpad: click("left"),
      mic: one({ kind: "micMute" }),
    },
    touchpad: { ...d.touchpad, mode: "mouse", tapToClick: true, scroll: true },
    sticks: { left: { ...d.sticks.left, mode: "mouse" }, right: { ...d.sticks.right, mode: "scroll" } },
    gyroMouse: { ...current.gyroMouse, mode: "off" },
  }, [layer, ...current.layers.slice(1).map((other) => other.button === "ps" ? { ...other, button: "none" as const } : other)]);
}

export const hasBindings = (m: Mapping) =>
  [m.buttons, ...m.layers.map((layer) => layer.buttons)].some((buttons) => Object.keys(buttons).length > 0)
  || m.chords.length > 0 || m.flickStick.enabled
  || Object.values(m.triggers).some((trigger) => trigger.soft.kind !== "none" || trigger.full.kind !== "none")
  || Object.values(m.sticks).some((stick) => stick.ringAction.kind !== "none");
