import { useEffect, useState } from "react";
import { inHost, type Profile, type Status } from "./host";
import { deepEqual } from "./model";
import { Notices } from "./Notices";
import { Emulation } from "./pages/Emulation";
import { Haptics } from "./pages/Haptics";
import { Light } from "./pages/Light";
import { Mapping } from "./pages/Mapping";
import { Overview } from "./pages/Overview";
import { Profiles } from "./pages/Profiles";
import { Settings } from "./pages/Settings";
import { Sticks } from "./pages/Sticks";
import { Triggers } from "./pages/Triggers";
import { AppProvider, useApp, useInput } from "./state";

// `keys`: profile fields edited on that page, for the unsaved mark in the rail.
const sections = [
  { id: "controller", label: "Kontrolcü", keys: [] },
  { id: "triggers", label: "Tetikler", keys: ["triggers", "triggerResponse"] },
  { id: "haptics", label: "Haptik", keys: ["haptics"] },
  { id: "light", label: "Işık", keys: ["lightbar", "playerLeds", "micLed"] },
  { id: "sticks", label: "Analoglar", keys: ["sticks", "gyro"] },
  { id: "mapping", label: "Tuşlar", keys: ["mapping", "macros"] },
  { id: "emulation", label: "Sanal kontrolcü", keys: ["emulation"] },
  { id: "profiles", label: "Profiller", keys: [] },
] as const satisfies readonly { id: string; label: string; keys: readonly (keyof Profile)[] }[];

type SectionId = (typeof sections)[number]["id"] | "settings";

const linkLabel: Record<string, string> = { Bluetooth: "Bluetooth", Usb: "USB", Simulated: "simülasyon" };

function mappingState(status: Status | null): string {
  const m = status?.mapping;
  if (!status?.connected) return "bekliyor";
  if (m?.paused) return "duraklatıldı";
  if (m?.blockedBy) return "engellendi";
  return m?.active ? "açık" : "bekliyor";
}

function StatusBar() {
  const { status, profiles, activeId } = useApp();
  const input = useInput();
  const connected = status?.connected ?? false;
  const active = profiles.find((p) => p.id === activeId);

  return (
    <header className="status">
      <span className={connected ? "dot live" : "dot"} aria-hidden />
      <span className="status-model">{connected ? status?.model ?? "DualSense" : "Kontrolcü bağlı değil"}</span>
      <span className="spacer" />
      {active && (
        <span className="metric">
          <span className="label">profil</span>
          <span className="value">{active.name}</span>
        </span>
      )}
      {active?.mapping.enabled && (
        <span className="metric" title={status?.mapping?.blockedBy ? `${status.mapping.blockedBy} yönetici olarak çalışıyor` : undefined}>
          <span className="label">atama</span>
          <span className={status?.mapping?.blockedBy ? "value warn" : "value"}>{mappingState(status)}</span>
        </span>
      )}
      <span className="metric">
        <span className="label">pil</span>
        <span className="value">{input ? `%${input.battery}${input.charging ? " şarjda" : ""}` : "—"}</span>
      </span>
      <span className="metric">
        <span className="label">bağlantı</span>
        <span className="value">{connected && status?.link ? linkLabel[status.link] ?? status.link : "—"}</span>
      </span>
    </header>
  );
}

function Shell() {
  const [section, setSection] = useState<SectionId>("controller");
  const { status, output, dirty, draft, draftBase } = useApp();
  const changed = (keys: readonly (keyof Profile)[]) => dirty && !!draft && !!draftBase && keys.some((k) => !deepEqual(draft[k], draftBase[k]));

  // The interface accent follows the controller lightbar.
  useEffect(() => {
    const color = status?.connected && output ? output.lightbar : null;
    document.documentElement.style.setProperty("--accent", color && color.toUpperCase() !== "#000000" ? lift(color) : "#9a9ca1");
  }, [output, status]);

  return (
    <div className="shell">
      <nav className="rail">
        <div className="wordmark">tactile</div>
        <ul>
          {sections.map((s) => (
            <li key={s.id}>
              <button className={section === s.id ? "active" : ""} onClick={() => setSection(s.id)}>
                {s.label}
                {changed(s.keys) && <span className="dirty-mark" title="Kaydedilmemiş değişiklik" />}
              </button>
            </li>
          ))}
        </ul>
        <div className="rail-foot">
          <button className={section === "settings" ? "active" : ""} onClick={() => setSection("settings")}>
            Ayarlar
          </button>
          <span className="version">{inHost ? `v${status?.version ?? "…"}` : "tarayıcı önizleme"}</span>
        </div>
      </nav>

      <main>
        <StatusBar />
        <section className="content" key={section}>
          {section === "controller" && <Overview />}
          {section === "triggers" && <Triggers />}
          {section === "haptics" && <Haptics />}
          {section === "light" && <Light />}
          {section === "sticks" && <Sticks />}
          {section === "mapping" && <Mapping />}
          {section === "emulation" && <Emulation />}
          {section === "profiles" && <Profiles />}
          {section === "settings" && <Settings />}
        </section>
      </main>
      <Notices />
    </div>
  );
}

export function App() {
  return (
    <AppProvider>
      <Shell />
    </AppProvider>
  );
}

// Dim lightbar colours (e.g. #000040) are unreadable as UI accents; raise lightness, keep hue.
function lift(hex: string): string {
  const n = parseInt(hex.slice(1), 16);
  let r = (n >> 16) & 255,
    g = (n >> 8) & 255,
    b = n & 255;
  const max = Math.max(r, g, b);
  if (max === 0) return "#9a9ca1";
  const k = Math.max(1, 200 / max);
  r = Math.min(255, Math.round(r * k + 40));
  g = Math.min(255, Math.round(g * k + 40));
  b = Math.min(255, Math.round(b * k + 40));
  return `rgb(${r} ${g} ${b})`;
}
