import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { send, subscribe, type AppSettings, type DriverKind, type DriverProgress, type EmulationStatus, type Input, type NoticeKind, type Output, type Profile, type Status } from "./host";
import { clone, deepEqual, normalizeProfile, normalizeSettings } from "./model";

export type Notice = { id: number; kind: NoticeKind | "error"; message: string; progress?: number };

type AppState = {
  status: Status | null;
  output: Output | null;
  profiles: Profile[];
  activeId: string;
  settings: AppSettings | null;
  emulation: EmulationStatus | null;
  // Last install message per driver; cleared when a new install starts.
  installs: Partial<Record<DriverKind, DriverProgress>>;
  recent: string[] | null;
  notices: Notice[];
  dismiss: (id: number) => void;

  // The profile being edited on Tetikler / Işık / Analoglar. Shared so tab switches keep edits.
  draft: Profile | null;
  draftBase: Profile | null;
  dirty: boolean;
  edit: (fn: (p: Profile) => Profile) => void;
  saveDraft: () => void;
  revertDraft: () => void;
};

const Ctx = createContext<AppState | null>(null);

export function useApp(): AppState {
  const v = useContext(Ctx);
  if (!v) throw new Error("useApp outside AppProvider");
  return v;
}

// Input arrives at ~30 Hz; only components that draw it subscribe.
export function useInput(): Input | null {
  const [input, setInput] = useState<Input | null>(null);
  useEffect(
    () =>
      subscribe((m) => {
        if (m.type === "input") setInput(m);
        else if (m.type === "status" && !m.connected) setInput(null);
      }),
    [],
  );
  return input;
}

let noticeSeq = 0;

export function AppProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<Status | null>(null);
  const [output, setOutput] = useState<Output | null>(null);
  const [profiles, setProfiles] = useState<Profile[]>([]);
  const [activeId, setActiveId] = useState("");
  const [settings, setSettings] = useState<AppSettings | null>(null);
  const [emulation, setEmulation] = useState<EmulationStatus | null>(null);
  const [installs, setInstalls] = useState<Partial<Record<DriverKind, DriverProgress>>>({});
  const [recent, setRecent] = useState<string[] | null>(null);
  const [notices, setNotices] = useState<Notice[]>([]);
  const [draft, setDraft] = useState<Profile | null>(null);
  const touched = useRef(false);
  const profilesRef = useRef<Profile[]>([]);

  const push = useCallback((kind: Notice["kind"], message: string, progress?: number) => {
    setNotices((list) => {
      // Progress notices of one kind update in place.
      const same = progress !== undefined ? list.find((n) => n.kind === kind && n.progress !== undefined) : undefined;
      if (same) return list.map((n) => (n === same ? { ...n, message, progress } : n));
      return [...list.slice(-3), { id: ++noticeSeq, kind, message, progress }];
    });
  }, []);

  const dismiss = useCallback((id: number) => setNotices((list) => list.filter((n) => n.id !== id)), []);

  useEffect(() => {
    const off = subscribe((m) => {
      switch (m.type) {
        case "status":
          setStatus(m);
          if (m.activeProfileId) setActiveId(m.activeProfileId);
          break;
        case "output":
          setOutput(m);
          break;
        case "profiles": {
          const list = m.list.map((p) => normalizeProfile(p));
          profilesRef.current = list;
          setProfiles(list);
          setActiveId(m.activeId);
          break;
        }
        case "settings":
          setSettings(normalizeSettings(m.settings));
          break;
        case "foreground":
          setRecent(m.recent);
          break;
        case "emulation":
          setEmulation(m);
          break;
        case "driver.progress":
          setInstalls((all) => ({ ...all, [m.kind]: m }));
          break;
        case "export":
          download(m.json, profilesRef.current.find((p) => p.id === m.id)?.name ?? m.id);
          break;
        case "notice":
          push(m.kind, m.message, m.progress);
          break;
        case "error":
          push("error", m.message);
          break;
      }
    });
    send({ type: "hello" });
    return off;
  }, [push]);

  // Follow the stored active profile unless the user has unsaved edits.
  useEffect(() => {
    setDraft((d) => {
      const active = profiles.find((p) => p.id === activeId) ?? null;
      if (!d || !touched.current) return active ? clone(active) : null;
      const base = profiles.find((p) => p.id === d.id);
      if (!base) {
        touched.current = false;
        return active ? clone(active) : null;
      }
      return d;
    });
  }, [profiles, activeId]);

  const draftBase = draft ? profiles.find((p) => p.id === draft.id) ?? null : null;
  const dirty = !!draft && !!draftBase && touched.current && !deepEqual(draft, draftBase);

  const edit = useCallback((fn: (p: Profile) => Profile) => {
    touched.current = true;
    setDraft((d) => (d ? fn(d) : d));
  }, []);

  const saveDraft = useCallback(() => {
    if (!draft) return;
    touched.current = false;
    send({ type: "profile.save", profile: draft });
  }, [draft]);

  const revertDraft = useCallback(() => {
    touched.current = false;
    const active = profilesRef.current.find((p) => p.id === activeId);
    setDraft(active ? clone(active) : null);
  }, [activeId]);

  const value = useMemo<AppState>(
    () => ({ status, output, profiles, activeId, settings, emulation, installs, recent, notices, dismiss, draft, draftBase, dirty, edit, saveDraft, revertDraft }),
    [status, output, profiles, activeId, settings, emulation, installs, recent, notices, dismiss, draft, draftBase, dirty, edit, saveDraft, revertDraft],
  );

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

function download(json: string, name: string) {
  const safe = name.replace(/[\\/:*?"<>|]+/g, "").trim() || "profil";
  const url = URL.createObjectURL(new Blob([json], { type: "application/json" }));
  const a = document.createElement("a");
  a.href = url;
  a.download = `${safe}.json`;
  a.click();
  window.setTimeout(() => URL.revokeObjectURL(url), 1000);
}
