import { L } from "../i18n";
import { useEffect, useState } from "react";
import { send, subscribe, type AppSettings } from "../host";
import { Live } from "../Live";
import { deepEqual } from "../model";
import { Outputs } from "../Outputs";
import { useApp, useInput } from "../state";
import { PageHead, Segmented, Toggle } from "../ui";

function NumberField({ label, value, min, max, step = 1, suffix, onChange, hint }: { label: string; value: number; min: number; max: number; step?: number; suffix?: string; hint?: string; onChange: (v: number) => void }) {
  return (
    <label className="setting-row">
      <span className="toggle-text">
        <span>{label}</span>
        {hint && <span className="hint">{hint}</span>}
      </span>
      <span className="number-field">
        <input
          type="number"
          min={min}
          max={max}
          step={step}
          value={value}
          onChange={(e) => {
            const v = Number(e.target.value);
            if (Number.isFinite(v)) onChange(Math.min(max, Math.max(min, v)));
          }}
        />
        {suffix && <span className="faint">{suffix}</span>}
      </span>
    </label>
  );
}

function DevTools() {
  const { output, status } = useApp();
  const input = useInput();
  return (
    <div className="dev-tools">
      <p className="hint">{L("These test controls temporarily override the profile. Select Reset all to restore it.", "Faz 1 test ekranı. Buradaki komutlar profilin üstüne geçici olarak yazılır; “Hepsini sıfırla” profile döner.")}</p>
      <div className="controller-page">
        <Live input={input} />
        <Outputs output={output} link={status?.link} />
      </div>
    </div>
  );
}

export function Settings() {
  const { settings, profiles, status } = useApp();
  const [draft, setDraft] = useState<AppSettings | null>(settings);
  const [touched, setTouched] = useState(false);
  const [dev, setDev] = useState(false);
  const [logResult, setLogResult] = useState("");

  useEffect(() => subscribe((message) => {
    if (message.type === "logs.exported") {
      setLogResult(message.ok ? L(`Logs exported: ${message.path ?? ""}`, `Loglar dışa aktarıldı: ${message.path ?? ""}`) : message.error ?? "");
    }
  }), []);

  useEffect(() => {
    if (!touched) setDraft(settings);
  }, [settings, touched]);

  if (!draft || !settings) return <div className="page"><PageHead title={L("Settings", "Ayarlar")} /></div>;

  const dirty = touched && !deepEqual(draft, settings);
  const set = (patch: Partial<AppSettings>) => {
    setTouched(true);
    setDraft({ ...draft, ...patch });
  };
  const save = () => {
    send({ type: "settings.save", settings: draft });
    setTouched(false);
  };

  const mac = status?.connected ? status.mac : undefined;
  const macs = Array.from(new Set([...(mac ? [mac] : []), ...Object.keys(draft.controllerDefaults)]));
  const warnings = [draft.lowBatteryWarnings[0] ?? 20, draft.lowBatteryWarnings[1] ?? 10];

  return (
    <div className="page settings-page">
      <PageHead title={L("Settings", "Ayarlar")}>
        <div className="draft-bar">
          {dirty ? (
            <>
              <span className="unsaved">{L("Unsaved", "Kaydedilmedi")}</span>
              <button
                className="button ghost"
                onClick={() => {
                  setTouched(false);
                  setDraft(settings);
                }}
              >
                {L("Revert", "Geri al")}
              </button>
              <button className="button primary" onClick={save}>
                {L("Save", "Kaydet")}
              </button>
            </>
          ) : (
            <span className="faint saved">{L("Saved", "Kaydedildi")}</span>
          )}
        </div>
      </PageHead>

      <div className="settings-grid">
        <section className="settings-group">
          <h2>{L("Application", "Uygulama")}</h2>
          {!status?.portable && <Toggle label={L("Start with Windows", "Windows ile başlat")} checked={draft.startWithWindows} onChange={(startWithWindows) => set({ startWithWindows })} />}
          <Toggle label={L("Minimize to tray on close", "Kapatınca tepsiye küçült")} hint={L("Keeps profiles running after the window closes", "Pencere kapansa da profiller çalışmaya devam eder")} checked={draft.closeToTray} onChange={(closeToTray) => set({ closeToTray })} />
          <div className="setting-row">
            <span>{L("Language", "Dil")}</span>
            <Segmented
              label={L("Language", "Dil")}
              value={draft.language}
              onChange={(language) => set({ language })}
              options={[
                { id: "en", label: "English" },
                { id: "tr", label: "Türkçe" },
              ]}
            />
          </div>
          <Toggle label={L("Administrator reminder", "Yönetici izni uyarısı")} hint={L("Remind me if a profile is missed for a game running as administrator", "Yönetici olarak çalışan oyunlarda profil algılanmazsa hatırlat")} checked={draft.runAsAdminHint} onChange={(runAsAdminHint) => set({ runAsAdminHint })} />
          <div className="setting-row">
            <button className="button" onClick={() => {
              setLogResult("");
              send({ type: "logs.export" });
            }}>{L("Export logs", "Logları dışa aktar")}</button>
            <button className="button" onClick={() => send({ type: "logs.open" })}>{L("Open log folder", "Log klasörünü aç")}</button>
          </div>
          <div className="hint" role="status">{logResult}</div>
        </section>

        <section className="settings-group">
          <h2>{L("Profiles", "Profiller")}</h2>
          <Toggle label={L("Switch profiles by game", "Oyuna göre profil değiştir")} hint={L("Follows the foreground app", "Ön plandaki uygulamaya bakar")} checked={draft.autoProfile} onChange={(autoProfile) => set({ autoProfile })} />
          <NumberField label={L("Switch delay", "Geçiş gecikmesi")} hint={L("Wait before switching to the foreground game's profile", "Oyun bu kadar ön planda kalınca geçer")} value={draft.autoProfileDelayMs} min={0} max={5000} step={100} suffix="ms" onChange={(autoProfileDelayMs) => set({ autoProfileDelayMs })} />
          <label className="setting-row">
            <span>{L("Default profile", "Varsayılan profil")}</span>
            <select value={draft.defaultProfileId} onChange={(e) => set({ defaultProfileId: e.target.value })}>
              {profiles.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </select>
          </label>
          {macs.length > 0 && (
            <div className="controller-defaults">
              <span className="sub-label">{L("Per controller", "Kontrolcüye göre")}</span>
              {macs.map((m) => (
                <label key={m} className="setting-row">
                  <span className="toggle-text">
                    <span className="num">{m}</span>
                    {m === mac && <span className="hint">{L("connected controller", "bağlı kontrolcü")}</span>}
                  </span>
                  <select
                    value={draft.controllerDefaults[m] ?? ""}
                    onChange={(e) => {
                      const next = { ...draft.controllerDefaults };
                      if (e.target.value) next[m] = e.target.value;
                      else delete next[m];
                      set({ controllerDefaults: next });
                    }}
                  >
                    <option value="">{L("Global default", "Genel varsayılan")}</option>
                    {profiles.map((p) => (
                      <option key={p.id} value={p.id}>
                        {p.name}
                      </option>
                    ))}
                  </select>
                </label>
              ))}
            </div>
          )}
        </section>

        <section className="settings-group">
          <h2>{L("Strength", "Güç")}</h2>
          <NumberField label={L("Idle sleep", "Boşta uyku")} hint={L("Turns off the Bluetooth controller after inactivity. Set to 0 to disable.", "Bluetooth'ta bu süre dokunulmazsa kontrolcü kapanır; 0 kapalı")} value={draft.idleSleepMinutes} min={0} max={120} suffix={L("min", "dk")} onChange={(idleSleepMinutes) => set({ idleSleepMinutes })} />
          <NumberField label={L("First battery warning", "İlk pil uyarısı")} value={warnings[0]} min={1} max={90} suffix="%" onChange={(v) => set({ lowBatteryWarnings: [v, Math.min(warnings[1], v - 1)] })} />
          <NumberField label={L("Second battery warning", "İkinci pil uyarısı")} value={warnings[1]} min={0} max={89} suffix="%" onChange={(v) => set({ lowBatteryWarnings: [Math.max(warnings[0], v + 1), v] })} />
        </section>

        <section className="settings-group">
          <h2>{L("Virtual controller", "Sanal kontrolcü")}</h2>
          <div className="setting-row">
            <span className="toggle-text">
              <span>{L("Restore controller", "Kontrolcüyü geri getir")}</span>
              <span className="hint">{L("Removes the virtual controller, makes the physical controller visible, and turns off emulation", "Sanal kontrolcüyü kaldırır, gerçek kontrolcüyü yeniden görünür yapar ve sanal kontrolcüyü kapatır")}</span>
            </span>
            <button className="button" onClick={() => send({ type: "emulation.restore" })}>
              {L("Restore", "Geri getir")}
            </button>
          </div>
        </section>

        <section className="settings-group dev">
          <button className="disclosure" aria-expanded={dev} onClick={() => setDev(!dev)}>
            <span className="chev" aria-hidden />
            {L("Developer tools", "Geliştirici araçları")}
          </button>
          {dev && <DevTools />}
        </section>
      </div>
    </div>
  );
}
