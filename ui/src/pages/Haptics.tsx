import { L } from "../i18n";
import { useEffect, useState } from "react";
import { send, subscribe, type AudioApp, type HapticsConfig, type SpeakerPacket } from "../host";
import { useApp } from "../state";
import { DraftBar, PageHead, Section, Segmented, Slider, Toggle, pct } from "../ui";

const modes: { id: HapticsConfig["mode"]; label: string; hint: string }[] = [
  { id: "game", get label() { return L("Game", "Oyun"); }, get hint() { return L("Keeps sharp sounds such as impacts and explosions distinct.", "Darbe ve patlama gibi ani sesleri belirgin tutar."); } },
  { id: "film", get label() { return L("Movie", "Film"); }, get hint() { return L("Reduces vibration from dialogue.", "Konuşmadan gelen titreşimi azaltır."); } },
  { id: "music", get label() { return L("Music", "Müzik"); }, get hint() { return L("Emphasizes bass and drum hits.", "Bas ve davul vuruşlarını öne çıkarır."); } },
];

export function Haptics() {
  const { draft, edit, haptics, settings, status } = useApp();
  const [picking, setPicking] = useState<"audio" | "speaker" | null>(null);
  const [apps, setApps] = useState<AudioApp[] | null>(null);
  useEffect(() => subscribe((m) => {
    if (m.type === "audioApps") setApps(m.apps);
  }), []);

  const config = draft?.haptics;
  const set = (patch: Partial<HapticsConfig>) => edit((p) => ({ ...p, haptics: { ...p.haptics, ...patch } }));
  const source = haptics?.source === "system" ? L("System audio", "Sistem sesi")
    : haptics?.source.startsWith("app:") ? haptics.source.slice(4) || L("App", "Uygulama") : null;

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
            { id: "off", label: L("Off", "Kapalı") },
            { id: "system", label: L("System audio", "Sistem sesi") },
            { id: "app", label: L("Application", L("App", "Uygulama")) },
          ]}
        />
      </div>
      {value === "app" && (
        <>
          <div className="row-field">
            <label className="field-inline">
              {L("App file", "Uygulama dosyası")}
              <input type="text" value={app} maxLength={260} placeholder={L("example.exe", "ornek.exe")} spellCheck={false}
                onChange={(e) => setApp(e.target.value)} />
            </label>
            <button className="button" onClick={() => {
              setPicking(field);
              setApps(null);
              send({ type: "audio.apps" });
            }}>{L("Apps playing audio", "Çalan uygulamalar")}</button>
          </div>
          {picking === field && (
            <div className="recent">
              {apps === null ? <p className="hint">{L("Loading list…", "Liste alınıyor…")}</p>
                : apps.length === 0 ? <p className="hint">{L("No apps are playing audio.", "Şu anda ses çalan uygulama yok.")}</p>
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
      <PageHead title={L("Haptics", "Haptik")}><DraftBar /></PageHead>
      <div className="haptics-sections">
        <p className="hint" role="status">
          {haptics ? haptics.streaming ? L("Haptics stream on", "Haptik akışı açık") : L("Haptics stream off", "Haptik akışı kapalı") : L("Waiting for haptics status…", "Haptik durumu bekleniyor…")}
          {source && <> · {source}</>}
          {status?.link === "Usb"
            ? <> {L("· Haptics streaming currently requires Bluetooth.", "· Haptik akışı şimdilik yalnız Bluetooth'ta çalışır.")}</>
            : haptics?.problem && <> · {haptics.problem}</>}
        </p>
        {config && (
          <>
            {(draft.emulation.mode === "dualSense" || draft.emulation.mode === "auto") && (
              <Section title={L("Game haptics", "Oyun haptiği")}>
                <div className="row-field">
                  <Segmented
                    label={L("Game audio output", "Oyun sesi çıkışı")}
                    value={config.gameAudio}
                    onChange={(gameAudio) => set({ gameAudio })}
                    options={[
                      { id: "both", label: L("Both", "Hepsi") },
                      { id: "haptics", label: L("Haptics only", "Yalnız haptik") },
                      { id: "speaker", label: L("Speaker only", "Yalnız hoparlör") },
                      { id: "off", label: L("Off", "Kapalı") },
                    ]}
                  />
                </div>
                {(config.gameAudio === "both" || config.gameAudio === "haptics") && (
                  <>
                    <Slider label={L("Game haptics intensity", "Oyun haptiği şiddeti")} min={0} max={2} step={0.01} value={config.gameStrength}
                      onChange={(gameStrength) => set({ gameStrength })} format={pct} />
                    <Toggle label={L("Smoothing (low-pass)", "Yumuşatma (düşük geçiren)")} checked={config.gameLowPass}
                      onChange={(gameLowPass) => set({ gameLowPass })} />
                  </>
                )}
                <p className="hint">{L("Game haptics take priority over audio haptics.", "Oyun kendi haptiğini gönderirken sesten haptik geri planda kalır.")}</p>
              </Section>
            )}
            <Section title={L("Audio haptics", "Sesten haptik")}>
              {sourceFields("audio", L("Audio source", "Ses kaynağı"), config.audio, config.app, (audio) => set({ audio }), (app) => set({ app }))}
              <ul className="effect-list" role="radiogroup" aria-label={L("Haptics mode", "Haptik modu")}>
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
              <Slider label={L("Audio intensity", "Ses şiddeti")} min={0} max={2} step={0.01} value={config.strength} format={pct}
                onChange={(strength) => set({ strength })} />
            </Section>
            <Section title={L("Controller speaker", "Kontrolcü hoparlörü")}>
              {sourceFields("speaker", L("Speaker source", "Hoparlör kaynağı"), config.speaker, config.speakerApp,
                (speaker) => set({ speaker }), (speakerApp) => set({ speakerApp }))}
              <Slider label={L("Speaker volume", "Hoparlör ses düzeyi")} min={0} max={1} step={0.01} value={config.speakerVolume} format={pct}
                onChange={(speakerVolume) => set({ speakerVolume })} />
              {settings && (
                <>
                  <div className="row-field">
                    <Segmented<SpeakerPacket>
                      label={L("Packet", "Paket")}
                      value={settings.emulation.speakerPacket}
                      onChange={(speakerPacket) => send({ type: "settings.save", settings: {
                        ...settings, emulation: { ...settings.emulation, speakerPacket },
                      } })}
                      options={[
                        { id: "single36", label: L("Single frame (recommended)", "Tek kare (önerilen)") },
                        { id: "double39", label: L("Double frame (experimental)", "Çift kare (deneysel)") },
                      ]}
                    />
                  </div>
                  <p className="hint">{L("Double frames may produce no sound on some controllers. Switch back to single frames if needed.", "Çift kare bazı kontrolcülerde hiç ses vermiyor; ses gelmezse tek kareye dön.")}</p>
                </>
              )}
              <p className="hint">{L("System audio sends all sounds, including notifications, to the controller speaker.", "Sistem sesi, bildirimler dâhil tüm sesleri kontrolcü hoparlörüne gönderir.")}</p>
            </Section>
            <Section title="Rumble">
              <Toggle label={L("Play rumble through haptics", "Rumble'ı haptik dalga olarak ver")}
                hint={L("Keeps haptics streaming for game rumble with audio and click feedback off.", "Ses ve tık hissi kapalıyken de oyun titreşimleri için haptik akışı açık tutar.")}
                checked={config.rumbleToHaptics} onChange={(rumbleToHaptics) => set({ rumbleToHaptics })} />
            </Section>
            <Section title={L("Click feedback", "Tık hissi")}>
              <Toggle label={L("Button and trigger click feedback", "Tuş ve tetik tık hissi")} checked={config.clicks} onChange={(clicks) => set({ clicks })} />
              <Toggle label={L("Confirmation pulses (profile, layer, macro)", "Onay darbeleri (profil, katman, makro)")} checked={config.confirmPulses} onChange={(confirmPulses) => set({ confirmPulses })} />
              <Slider label={L("Click intensity", "Tık şiddeti")} min={0} max={1} step={0.01} value={config.clickStrength} format={pct}
                onChange={(clickStrength) => set({ clickStrength })} />
            </Section>
          </>
        )}
      </div>
    </div>
  );
}
