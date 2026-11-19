import { useEffect, useState } from "react";
import { send, subscribe, type AudioApp, type HapticsConfig } from "../host";
import { useApp } from "../state";
import { DraftBar, PageHead, Section, Segmented, Slider, Toggle, pct } from "../ui";

const modes: { id: HapticsConfig["mode"]; label: string; hint: string }[] = [
  { id: "game", label: "Oyun", hint: "Darbe ve patlama gibi ani sesleri belirgin tutar." },
  { id: "film", label: "Film", hint: "Konuşmadan gelen titreşimi azaltır." },
  { id: "music", label: "Müzik", hint: "Bas ve davul vuruşlarını öne çıkarır." },
];

export function Haptics() {
  const { draft, edit, haptics, status } = useApp();
  const [picking, setPicking] = useState<"audio" | "speaker" | null>(null);
  const [apps, setApps] = useState<AudioApp[] | null>(null);
  useEffect(() => subscribe((m) => {
    if (m.type === "audioApps") setApps(m.apps);
  }), []);

  const config = draft?.haptics;
  const set = (patch: Partial<HapticsConfig>) => edit((p) => ({ ...p, haptics: { ...p.haptics, ...patch } }));
  const source = haptics?.source === "system" ? "Sistem sesi"
    : haptics?.source.startsWith("app:") ? haptics.source.slice(4) || "Uygulama" : null;

  const sourceFields = (
    field: "audio" | "speaker",
    label: string,
    value: HapticsConfig["audio"],
    app: string,
    setSource: (value: HapticsConfig["audio"]) => void,
    setApp: (app: string) => void,
  ) => (
    <>
      <div className="row-field">
        <Segmented<HapticsConfig["audio"]>
          label={label}
          value={value}
          onChange={setSource}
          options={[
            { id: "off", label: "Kapalı" },
            { id: "system", label: "Sistem sesi" },
            { id: "app", label: "Uygulama" },
          ]}
        />
      </div>
      {value === "app" && (
        <>
          <div className="row-field">
            <label className="field-inline">
              Uygulama dosyası
              <input type="text" value={app} maxLength={260} placeholder="ornek.exe" spellCheck={false}
                onChange={(e) => setApp(e.target.value)} />
            </label>
            <button className="button" onClick={() => {
              setPicking(field);
              setApps(null);
              send({ type: "audio.apps" });
            }}>Çalan uygulamalar</button>
          </div>
          {picking === field && (
            <div className="recent">
              {apps === null ? <p className="hint">Liste alınıyor…</p>
                : apps.length === 0 ? <p className="hint">Şu anda ses çalan uygulama yok.</p>
                : <ul>
                  {apps.map((a) => (
                    <li key={a.pid}>
                      <button onClick={() => {
                        setApp(a.name);
                        setPicking(null);
                      }} title={a.name}>{a.display}</button>
                    </li>
                  ))}
                </ul>}
            </div>
          )}
        </>
      )}
    </>
  );
  return (
    <div className="page">
      <PageHead title="Haptik"><DraftBar /></PageHead>
      <div className="haptics-sections">
        <p className="hint" role="status">
          {haptics ? haptics.streaming ? "Haptik akışı açık" : "Haptik akışı kapalı" : "Haptik durumu bekleniyor…"}
          {source && <> · {source}</>}
          {status?.link === "Usb"
            ? <> · Haptik akışı şimdilik yalnız Bluetooth'ta çalışır.</>
            : haptics?.problem && <> · {haptics.problem}</>}
        </p>
        {config && (
          <>
            {draft.emulation.mode === "dualSense" && (
              <Section title="Oyun haptiği">
                <div className="row-field">
                  <Segmented
                    label="Oyun sesi çıkışı"
                    value={config.gameAudio}
                    onChange={(gameAudio) => set({ gameAudio })}
                    options={[
                      { id: "both", label: "Hepsi" },
                      { id: "haptics", label: "Yalnız haptik" },
                      { id: "speaker", label: "Yalnız hoparlör" },
                      { id: "off", label: "Kapalı" },
                    ]}
                  />
                </div>
                {(config.gameAudio === "both" || config.gameAudio === "haptics") && (
                  <>
                    <Slider label="Oyun haptiği şiddeti" min={0} max={2} step={0.01} value={config.gameStrength}
                      onChange={(gameStrength) => set({ gameStrength })} format={pct} />
                    <Toggle label="Yumuşatma (düşük geçiren)" checked={config.gameLowPass}
                      onChange={(gameLowPass) => set({ gameLowPass })} />
                  </>
                )}
                <p className="hint">Oyun kendi haptiğini gönderirken sesten haptik geri planda kalır.</p>
              </Section>
            )}
            <Section title="Sesten haptik">
              {sourceFields("audio", "Ses kaynağı", config.audio, config.app, (audio) => set({ audio }), (app) => set({ app }))}
              <ul className="effect-list" role="radiogroup" aria-label="Haptik modu">
                {modes.map((mode) => (
                  <li key={mode.id}>
                    <button role="radio" aria-checked={config.mode === mode.id} className={config.mode === mode.id ? "on" : ""}
                      onClick={() => set({ mode: mode.id })}>
                      <span className="radio" aria-hidden />
                      <span className="effect-name">{mode.label}</span>
                      <span className="faint">{mode.hint}</span>
                    </button>
                  </li>
                ))}
              </ul>
              <Slider label="Ses şiddeti" min={0} max={2} step={0.01} value={config.strength} format={pct}
                onChange={(strength) => set({ strength })} />
            </Section>
            <Section title="Kontrolcü hoparlörü">
              {sourceFields("speaker", "Hoparlör kaynağı", config.speaker, config.speakerApp,
                (speaker) => set({ speaker }), (speakerApp) => set({ speakerApp }))}
              <Slider label="Hoparlör ses düzeyi" min={0} max={1} step={0.01} value={config.speakerVolume} format={pct}
                onChange={(speakerVolume) => set({ speakerVolume })} />
              <p className="hint">Sistem sesi, bildirimler dâhil tüm sesleri kontrolcü hoparlörüne gönderir.</p>
            </Section>
            <Section title="Rumble">
              <Toggle label="Rumble'ı haptik dalga olarak ver"
                hint="Ses ve tık hissi kapalıyken de oyun titreşimleri için haptik akışı açık tutar."
                checked={config.rumbleToHaptics} onChange={(rumbleToHaptics) => set({ rumbleToHaptics })} />
            </Section>
            <Section title="Tık hissi">
              <Toggle label="Tuş ve tetik tık hissi" checked={config.clicks} onChange={(clicks) => set({ clicks })} />
              <Slider label="Tık şiddeti" min={0} max={1} step={0.01} value={config.clickStrength} format={pct}
                onChange={(clickStrength) => set({ clickStrength })} />
            </Section>
          </>
        )}
      </div>
    </div>
  );
}
