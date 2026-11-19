import type { AppSettings } from "./host";

export type Language = AppSettings["language"];

let current: Language = "en";

export function setLanguage(language: Language) {
  current = language;
  document.documentElement.lang = language;
}

export function locale(): string {
  return L("en-US", "tr-TR");
}

export function L(en: string, tr: string): string {
  return current === "tr" ? tr : en;
}

/** Display name for a controller model id from the host ("DualSenseEdge" → "DualSense Edge"). */
export function modelName(model: string | null | undefined): string {
  return model === "DualSenseEdge" ? "DualSense Edge" : model ?? "DualSense";
}
