import { useEffect, useState } from "react";
import { send, type AppSettings } from "../host";
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
      <p className="hint">Faz 1 test ekranı. Buradaki komutlar profilin üstüne geçici olarak yazılır; “Hepsini sıfırla” profile döner.</p>
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

  useEffect(() => {
    if (!touched) setDraft(settings);
  }, [settings, touched]);

  if (!draft || !settings) return <div className="page"><PageHead title="Ayarlar" /></div>;

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
      <PageHead title="Ayarlar">
        <div className="draft-bar">
          {dirty ? (
            <>
              <span className="unsaved">Kaydedilmedi</span>
              <button
                className="button ghost"
                onClick={() => {
                  setTouched(false);
                  setDraft(settings);
                }}
              >
                Geri al
              </button>
              <button className="button primary" onClick={save}>
                Kaydet
              </button>
            </>
          ) : (
            <span className="faint saved">Kaydedildi</span>
          )}
        </div>
      </PageHead>

      <div className="settings-grid">
        <section className="settings-group">
          <h2>Uygulama</h2>
          <Toggle label="Windows ile başlat" checked={draft.startWithWindows} onChange={(startWithWindows) => set({ startWithWindows })} />
          <Toggle label="Kapatınca tepsiye küçült" hint="Pencere kapansa da profiller çalışmaya devam eder" checked={draft.closeToTray} onChange={(closeToTray) => set({ closeToTray })} />
          <div className="setting-row">
            <span>Dil</span>
            <Segmented
              label="Dil"
              value={draft.language}
              onChange={(language) => set({ language })}
              options={[
                { id: "tr", label: "Türkçe" },
                { id: "en", label: "English" },
              ]}
            />
          </div>
          <Toggle label="Yönetici izni uyarısı" hint="Yönetici olarak çalışan oyunlarda profil algılanmazsa hatırlat" checked={draft.runAsAdminHint} onChange={(runAsAdminHint) => set({ runAsAdminHint })} />
        </section>

        <section className="settings-group">
          <h2>Profiller</h2>
          <Toggle label="Oyuna göre profil değiştir" hint="Ön plandaki uygulamaya bakar" checked={draft.autoProfile} onChange={(autoProfile) => set({ autoProfile })} />
          <NumberField label="Geçiş gecikmesi" hint="Oyun bu kadar ön planda kalınca geçer" value={draft.autoProfileDelayMs} min={0} max={5000} step={100} suffix="ms" onChange={(autoProfileDelayMs) => set({ autoProfileDelayMs })} />
          <label className="setting-row">
            <span>Varsayılan profil</span>
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
              <span className="sub-label">Kontrolcüye göre</span>
              {macs.map((m) => (
                <label key={m} className="setting-row">
                  <span className="toggle-text">
                    <span className="num">{m}</span>
                    {m === mac && <span className="hint">bağlı kontrolcü</span>}
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
                    <option value="">Genel varsayılan</option>
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
          <h2>Güç</h2>
          <NumberField label="Boşta uyku" hint="Bluetooth'ta bu süre dokunulmazsa kontrolcü kapanır; 0 kapalı" value={draft.idleSleepMinutes} min={0} max={120} suffix="dk" onChange={(idleSleepMinutes) => set({ idleSleepMinutes })} />
          <NumberField label="İlk pil uyarısı" value={warnings[0]} min={1} max={90} suffix="%" onChange={(v) => set({ lowBatteryWarnings: [v, Math.min(warnings[1], v - 1)] })} />
          <NumberField label="İkinci pil uyarısı" value={warnings[1]} min={0} max={89} suffix="%" onChange={(v) => set({ lowBatteryWarnings: [Math.max(warnings[0], v + 1), v] })} />
        </section>

        <section className="settings-group">
          <h2>Sanal kontrolcü</h2>
          <div className="setting-row">
            <span className="toggle-text">
              <span>Kontrolcüyü geri getir</span>
              <span className="hint">Sanal kontrolcüyü kaldırır, gerçek kontrolcüyü yeniden görünür yapar ve sanal kontrolcüyü kapatır</span>
            </span>
            <button className="button" onClick={() => send({ type: "emulation.restore" })}>
              Geri getir
            </button>
          </div>
        </section>

        <section className="settings-group dev">
          <button className="disclosure" aria-expanded={dev} onClick={() => setDev(!dev)}>
            <span className="chev" aria-hidden />
            Geliştirici araçları
          </button>
          {dev && <DevTools />}
        </section>
      </div>
    </div>
  );
}
