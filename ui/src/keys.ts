// Key names (members of Tactile.Input.Key in camelCase), their labels, and the
// KeyboardEvent.code → name mapping from docs/contract-phase3.md.

import type { KeyName } from "./host";

export type KeyDef = { name: KeyName; label: string; group: string; capture: boolean };

const letters = "abcdefghijklmnopqrstuvwxyz".split("");
const groups: { group: string; keys: [KeyName, string][]; capture?: boolean }[] = [
  { group: "Harfler", keys: letters.map((l) => [l, l.toUpperCase()]) },
  { group: "Rakamlar", keys: "1234567890".split("").map((d) => [`digit${d}`, d]) },
  { group: "Değiştiriciler", keys: [["leftCtrl", "Ctrl"], ["rightCtrl", "Sağ Ctrl"], ["leftShift", "Shift"], ["rightShift", "Sağ Shift"], ["leftAlt", "Alt"], ["rightAlt", "Sağ Alt"], ["leftWin", "Win"], ["rightWin", "Sağ Win"]] },
  {
    group: "Düzen",
    keys: [
      ["escape", "Esc"], ["tab", "Tab"], ["capsLock", "Caps Lock"], ["space", "Boşluk"], ["enter", "Enter"], ["backspace", "Geri sil"],
      ["insert", "Insert"], ["delete", "Delete"], ["home", "Home"], ["end", "End"], ["pageUp", "Page Up"], ["pageDown", "Page Down"],
      ["up", "Yukarı ok"], ["down", "Aşağı ok"], ["left", "Sol ok"], ["right", "Sağ ok"],
      ["printScreen", "Print Screen"], ["scrollLock", "Scroll Lock"], ["menu", "Menü"],
    ],
  },
  { group: "İşlev", keys: Array.from({ length: 24 }, (_, i) => [`f${i + 1}`, `F${i + 1}`] as [KeyName, string]) },
  {
    group: "Noktalama",
    keys: [
      ["minus", "-"], ["equals", "="], ["leftBracket", "["], ["rightBracket", "]"], ["backslash", "\\"], ["semicolon", ";"],
      ["quote", "'"], ["backquote", "`"], ["comma", ","], ["period", "."], ["slash", "/"], ["intlBackslash", "< (ISO)"],
    ],
  },
  {
    group: "Numpad",
    keys: [
      ...Array.from({ length: 10 }, (_, i) => [`numpad${i}`, `Num ${i}`] as [KeyName, string]),
      ["numpadAdd", "Num +"], ["numpadSubtract", "Num -"], ["numpadMultiply", "Num *"], ["numpadDivide", "Num /"],
      ["numpadDecimal", "Num ,"], ["numpadEnter", "Num Enter"], ["numLock", "Num Lock"],
    ],
  },
  {
    group: "Medya",
    keys: [
      ["mediaPlayPause", "Oynat/Duraklat"], ["mediaNext", "Sonraki parça"], ["mediaPrevious", "Önceki parça"], ["mediaStop", "Durdur"],
      ["volumeUp", "Ses aç"], ["volumeDown", "Ses kıs"], ["volumeMute", "Sesi kapat"],
    ],
  },
];

// Keys a browser page usually never sees (the OS takes them first).
const hardToCapture = new Set(["leftWin", "rightWin", "printScreen", "mediaPlayPause", "mediaNext", "mediaPrevious", "mediaStop", "volumeUp", "volumeDown", "volumeMute"]);

export const keyDefs: KeyDef[] = groups.flatMap((g) =>
  g.keys.map(([name, label]) => ({ name, label, group: g.group, capture: !hardToCapture.has(name) && !/^f(1[3-9]|2\d)$/.test(name) })),
);
const byName = new Map(keyDefs.map((k) => [k.name, k]));

export const isKey = (name: string) => byName.has(name);
export const keyLabel = (name: KeyName) => (name === "none" ? "—" : byName.get(name)?.label ?? name);
export const chordLabel = (keys: KeyName[] | undefined) => (keys && keys.length ? keys.map(keyLabel).join(" + ") : "—");

export const modifierKeys = new Set(["leftCtrl", "rightCtrl", "leftShift", "rightShift", "leftAlt", "rightAlt", "leftWin", "rightWin"]);

const named: Record<string, KeyName> = {
  Escape: "escape", Enter: "enter", Space: "space", Tab: "tab", Backspace: "backspace", CapsLock: "capsLock",
  Minus: "minus", Equal: "equals", BracketLeft: "leftBracket", BracketRight: "rightBracket", Backslash: "backslash",
  Semicolon: "semicolon", Quote: "quote", Backquote: "backquote", Comma: "comma", Period: "period", Slash: "slash",
  IntlBackslash: "intlBackslash", NumpadEnter: "numpadEnter", NumpadAdd: "numpadAdd", NumpadSubtract: "numpadSubtract",
  NumpadMultiply: "numpadMultiply", NumpadDivide: "numpadDivide", NumpadDecimal: "numpadDecimal", NumpadComma: "numpadDecimal",
  NumLock: "numLock", ScrollLock: "scrollLock", PrintScreen: "printScreen", Insert: "insert", Delete: "delete",
  Home: "home", End: "end", PageUp: "pageUp", PageDown: "pageDown", ContextMenu: "menu", OSLeft: "leftWin", OSRight: "rightWin",
  AudioVolumeUp: "volumeUp", AudioVolumeDown: "volumeDown", AudioVolumeMute: "volumeMute", MediaPlayPause: "mediaPlayPause",
  MediaTrackNext: "mediaNext", MediaTrackPrevious: "mediaPrevious", MediaStop: "mediaStop",
};
const modifierCode: Record<string, string> = { Control: "Ctrl", Shift: "Shift", Alt: "Alt", Meta: "Win" };

export function codeToKey(code: string): KeyName | null {
  let name: string | undefined;
  let m: RegExpExecArray | null;
  if ((m = /^Key([A-Z])$/.exec(code))) name = m[1].toLowerCase();
  else if ((m = /^Digit(\d)$/.exec(code))) name = `digit${m[1]}`;
  else if ((m = /^Numpad(\d)$/.exec(code))) name = `numpad${m[1]}`;
  else if (/^F\d{1,2}$/.test(code)) name = code.toLowerCase();
  else if ((m = /^(Control|Shift|Alt|Meta)(Left|Right)$/.exec(code))) name = `${m[2].toLowerCase()}${modifierCode[m[1]]}`;
  else if ((m = /^Arrow(Up|Down|Left|Right)$/.exec(code))) name = m[1].toLowerCase();
  else name = named[code];
  return name && byName.has(name) ? name : null;
}

export function searchKeys(query: string): KeyDef[] {
  const q = query.trim().toLocaleLowerCase("tr");
  if (!q) return keyDefs;
  return keyDefs.filter((k) => k.label.toLocaleLowerCase("tr").includes(q) || k.name.toLowerCase().includes(q) || k.group.toLocaleLowerCase("tr").includes(q));
}
