// Labels, summaries and small helpers for the Tuşlar page.

import { useEffect } from "react";
import { send, subscribe, type Action, type ActionKind, type Binding, type ButtonName, type Direction, type Macro, type Mapping, type MouseButtonName, type Profile, type Special } from "./host";
import { chordLabel } from "./keys";
import { defaultBinding, defaultMapping } from "./model";

export const buttonDefs: { id: ButtonName; label: string; group: string }[] = [
  { id: "cross", label: "Çarpı", group: "Yüz" },
  { id: "circle", label: "Daire", group: "Yüz" },
  { id: "square", label: "Kare", group: "Yüz" },
  { id: "triangle", label: "Üçgen", group: "Yüz" },
  { id: "up", label: "Yön yukarı", group: "Yön" },
  { id: "down", label: "Yön aşağı", group: "Yön" },
  { id: "left", label: "Yön sol", group: "Yön" },
  { id: "right", label: "Yön sağ", group: "Yön" },
  { id: "l1", label: "L1", group: "Omuz" },
  { id: "r1", label: "R1", group: "Omuz" },
  { id: "l2", label: "L2", group: "Omuz" },
  { id: "r2", label: "R2", group: "Omuz" },
  { id: "l3", label: "L3", group: "Analog" },
  { id: "r3", label: "R3", group: "Analog" },
  { id: "create", label: "Create", group: "Orta" },
  { id: "options", label: "Options", group: "Orta" },
  { id: "ps", label: "PS", group: "Orta" },
  { id: "touchpad", label: "Touchpad tık", group: "Orta" },
  { id: "mic", label: "Mikrofon", group: "Orta" },
  { id: "fn1", label: "Fn1", group: "Edge" },
  { id: "fn2", label: "Fn2", group: "Edge" },
  { id: "paddleLeft", label: "Sol arka", group: "Edge" },
  { id: "paddleRight", label: "Sağ arka", group: "Edge" },
];

export const buttonLabel = (id: ButtonName | "none") => (id === "none" ? "Yok" : buttonDefs.find((b) => b.id === id)?.label ?? id);

// The input message sends PascalCase ("Cross", "PS"); profile keys are camelCase.
export const fromInput = (name: string): ButtonName | undefined => buttonDefs.find((b) => b.id.toLowerCase() === name.toLowerCase())?.id;
export const toInput = (id: ButtonName): string => (id === "ps" ? "PS" : id[0].toUpperCase() + id.slice(1));

export const actionKinds: { id: ActionKind; label: string }[] = [
  { id: "none", label: "Yok" },
  { id: "key", label: "Klavye tuşu" },
  { id: "mouseButton", label: "Fare tuşu" },
  { id: "wheel", label: "Fare tekerleği" },
  { id: "macro", label: "Makro" },
  { id: "profile", label: "Profil değiştir" },
  { id: "run", label: "Program çalıştır" },
  { id: "micMute", label: "Mikrofonu sustur" },
  { id: "special", label: "Sistem" },
];

export const mouseButtons: { id: MouseButtonName; label: string }[] = [
  { id: "left", label: "Sol" },
  { id: "right", label: "Sağ" },
  { id: "middle", label: "Orta" },
  { id: "x1", label: "Geri" },
  { id: "x2", label: "İleri" },
];
const clickLabel: Record<MouseButtonName, string> = { left: "Sol tık", right: "Sağ tık", middle: "Orta tık", x1: "Geri tuşu", x2: "İleri tuşu" };
export const mouseLabel = (b?: MouseButtonName) => clickLabel[b ?? "left"];

export const directions: { id: Direction; label: string }[] = [
  { id: "up", label: "Yukarı" },
  { id: "down", label: "Aşağı" },
  { id: "left", label: "Sol" },
  { id: "right", label: "Sağ" },
];
export const directionLabel = (d?: Direction) => directions.find((x) => x.id === d)?.label ?? "Aşağı";

export const specials: { id: Special; label: string; hint: string }[] = [
  { id: "screenshot", label: "Ekran görüntüsü", hint: "Win + Print Screen" },
  { id: "gameBar", label: "Oyun çubuğu", hint: "Win + G" },
  { id: "taskView", label: "Görev görünümü", hint: "Win + Tab" },
  { id: "desktop", label: "Masaüstünü göster", hint: "Win + D" },
  { id: "showApp", label: "Tactile'ı aç", hint: "Bu pencereyi öne getirir" },
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
      return `Tekerlek ${directionLabel(a.direction).toLocaleLowerCase("tr")} ×${a.amount ?? 1}`;
    case "macro":
      return `Makro: ${macros.find((m) => m.id === a.macroId)?.name ?? "seçilmedi"}`;
    case "profile":
      if (a.profileId === "next") return "Sonraki profil";
      if (a.profileId === "previous") return "Önceki profil";
      return `Profil: ${profiles.find((p) => p.id === a.profileId)?.name ?? "bilinmiyor"}`;
    case "run":
      return a.path ? `Çalıştır: ${a.path.split(/[\\/]/).pop()}` : "Çalıştır: seçilmedi";
    case "micMute":
      return "Mikrofonu sustur";
    case "special":
      return specials.find((s) => s.id === a.special)?.label ?? "Sistem";
    default:
      return "—";
  }
}

export function bindingSummary(b: Binding | undefined, profiles: Profile[], macros: Macro[]): string {
  if (!b) return "—";
  const parts = [actionSummary(b.press, profiles, macros)];
  if (b.hold) parts.push(`tut: ${actionSummary(b.hold, profiles, macros)}`);
  if (b.double) parts.push(`çift: ${actionSummary(b.double, profiles, macros)}`);
  if (!b.hold && !b.double && b.turbo) parts.push("turbo");
  else if (!b.hold && !b.double && b.toggle) parts.push("kilit");
  return parts.join(" · ");
}

export const isEmptyBinding = (b: Binding) => {
  const d = defaultBinding();
  return b.press.kind === "none" && !b.hold && !b.double && !b.turbo && !b.toggle && b.holdMs === d.holdMs && b.doubleMs === d.doubleMs && b.turboHz === d.turboHz;
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
  return {
    ...current,
    enabled: true,
    layerButton: "ps",
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
    layer: {
      options: one({ kind: "special", special: "showApp" }),
      create: one({ kind: "special", special: "taskView" }),
      triangle: one({ kind: "special", special: "desktop" }),
      l1: key("leftCtrl", "leftShift", "tab"),
      r1: key("leftCtrl", "tab"),
    },
    touchpad: { ...d.touchpad, mode: "mouse", tapToClick: true, scroll: true },
    sticks: { left: { ...d.sticks.left, mode: "mouse" }, right: { ...d.sticks.right, mode: "scroll" } },
    gyroMouse: { ...current.gyroMouse, mode: "off" },
  };
}

export const hasBindings = (m: Mapping) => Object.keys(m.buttons).length > 0 || Object.keys(m.layer).length > 0;
