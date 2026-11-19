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
