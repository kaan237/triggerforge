import { L, isDs4, modelName } from "./i18n";
import { useEffect, useState } from "react";
import logo from "./assets/logo-64.png";
import { inHost, type Profile, type Status } from "./host";
import { deepEqual, linkLabel } from "./model";
import { Notices } from "./Notices";
import { Emulation } from "./pages/Emulation";
import { Haptics } from "./pages/Haptics";
import { Integrations } from "./pages/Integrations";
import { Light } from "./pages/Light";
import { Mapping } from "./pages/Mapping";
import { Overview } from "./pages/Overview";
import { Profiles } from "./pages/Profiles";
import { Settings } from "./pages/Settings";
import { Sticks } from "./pages/Sticks";
import { Triggers } from "./pages/Triggers";
import { AppProvider, useApp, useInput } from "./state";
import { SetupWizard } from "./SetupWizard";

// `keys`: profile fields edited on that page, for the unsaved mark in the rail.
const sections = [
  { id: "controller", get label() { return L("Overview", "Kontrolcü"); }, keys: [] },
  { id: "triggers", get label() { return L("Triggers", "Tetikler"); }, keys: ["triggers", "triggerResponse"] },
  { id: "haptics", get label() { return L("Haptics", "Haptik"); }, keys: ["haptics"] },
  { id: "light", get label() { return L("Light", "Işık"); }, keys: ["lightbar", "playerLeds", "micLed"] },
  { id: "sticks", get label() { return L("Sticks", "Analoglar"); }, keys: ["sticks", "gyro"] },
  { id: "mapping", get label() { return L("Mapping", "Tuşlar"); }, keys: ["mapping", "macros"] },
  { id: "emulation", get label() { return L("Virtual controller", "Sanal kontrolcü"); }, keys: ["emulation"] },
  { id: "integrations", get label() { return L("Integrations", "Entegrasyonlar"); }, keys: [] },
  { id: "profiles", get label() { return L("Profiles", "Profiller"); }, keys: [] },
] as const satisfies readonly { id: string; label: string; keys: readonly (keyof Profile)[] }[];

type SectionId = (typeof sections)[number]["id"] | "settings";

function mappingState(status: Status | null): string {
  const m = status?.mapping;
  if (!status?.connected) return L("waiting", "bekliyor");
  if (m?.paused) return L("paused", "duraklatıldı");
  if (m?.blockedBy) return L("blocked", "engellendi");
  return m?.active ? L("on", "açık") : L("waiting", "bekliyor");
}

function StatusBar() {
  const { status, profiles, activeId } = useApp();
  const input = useInput();
  const connected = status?.connected ?? false;
  const active = profiles.find((p) => p.id === activeId);

  return (
    <header className="status">
      <span className={connected ? "dot live" : "dot"} aria-hidden />
      <span className="status-model">{connected ? modelName(status?.model) : L("No controller connected", "Kontrolcü bağlı değil")}</span>
      <span className="spacer" />
      {active && (
        <span className="metric">
          <span className="label">{L("profile", "profil")}</span>
          <span className="value">{active.name}</span>
        </span>
      )}
      {active?.mapping.enabled && (
        <span className="metric" title={status?.mapping?.blockedBy ? L(`${status.mapping.blockedBy} is running as administrator`, `${status.mapping.blockedBy} yönetici olarak çalışıyor`) : undefined}>
          <span className="label">{L("mapping", "atama")}</span>
          <span className={status?.mapping?.blockedBy ? "value warn" : "value"}>{mappingState(status)}</span>
        </span>
      )}
      <span className="metric">
        <span className="label">{L("battery", "pil")}</span>
        <span className="value">{input ? L(`${input.battery}%${input.charging ? " charging" : ""}`, `%${input.battery}${input.charging ? " şarjda" : ""}`) : "—"}</span>
      </span>
      <span className="metric">
        <span className="label">{L("connection", "bağlantı")}</span>
        <span className="value">{connected && status?.link ? linkLabel[status.link] ?? status.link : "—"}</span>
      </span>
    </header>
  );
}

function Shell() {
  const [section, setSection] = useState<SectionId>("controller");
  const { status, output, dirty, draft, draftBase, profiles, settings, update, telemetryDrafts } = useApp();
  const language = settings?.language ?? "en";
  const changed = (keys: readonly (keyof Profile)[]) => dirty && !!draft && !!draftBase && keys.some((k) => !deepEqual(draft[k], draftBase[k]));
  const telemetryChanged = profiles.some((p) => {
    const rules = telemetryDrafts.get(p.id);
    return rules !== undefined && !deepEqual(rules, p.telemetry.rules);
  });

  // The interface accent follows the controller lightbar.
  useEffect(() => {
    const color = status?.connected && output ? output.lightbar : null;
    document.documentElement.style.setProperty("--accent", color && color.toUpperCase() !== "#000000" ? lift(color) : "#9a9ca1");
  }, [output, status]);

  const ds4 = isDs4(status);
  useEffect(() => {
    if (ds4 && section === "haptics") setSection("controller");
  }, [ds4, section]);

  if (!settings) return null;
  if (!settings.firstRunDone) return <><SetupWizard settings={settings} /><Notices /></>;

  const updateReady = update?.state === "available" || update?.state === "ready";
  return (
    <div className="shell">
      <nav className="rail">
        <div className="wordmark">
          <img className="brand-icon" src={logo} width={24} height={24} alt="" aria-hidden />
          TriggerForge
        </div>
        <ul>
          {sections.filter((s) => !(ds4 && s.id === "haptics")).map((s) => (
            <li key={s.id}>
              <button className={section === s.id ? "active" : ""} onClick={() => setSection(s.id)}>
                {s.label}
                {(s.id === "integrations" ? telemetryChanged : changed(s.keys)) && <span className="dirty-mark" title={L("Unsaved changes", "Kaydedilmemiş değişiklik")} />}
              </button>
            </li>
          ))}
        </ul>
        <div className="rail-foot">
          <button className={section === "settings" ? "active" : ""} onClick={() => setSection("settings")}>
            {L("Settings", "Ayarlar")}
            {updateReady && <span className="tag accent update-badge">{L("Update", "Güncelleme")}</span>}
          </button>
          <span className="version">{inHost ? `v${status?.version ?? "…"}` : L("browser preview", "tarayıcı önizleme")}</span>
        </div>
      </nav>

      <main>
        <StatusBar />
        <section className="content" key={section}>
          <PageView key={language} section={section} />
        </section>
      </main>
      <Notices />
    </div>
  );
}

function PageView({ section }: { section: SectionId }) {
  return (
    <>
      {section === "controller" && <Overview />}
      {section === "triggers" && <Triggers />}
      {section === "haptics" && <Haptics />}
      {section === "light" && <Light />}
      {section === "sticks" && <Sticks />}
      {section === "mapping" && <Mapping />}
      {section === "emulation" && <Emulation />}
      {section === "integrations" && <Integrations />}
      {section === "profiles" && <Profiles />}
      {section === "settings" && <Settings />}
    </>
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
