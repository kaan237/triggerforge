import { useEffect, useRef, useState } from "react";
import logo from "./assets/logo-256.png";
import { send, type AppSettings } from "./host";
import { L, isDs4, modelName } from "./i18n";
import { Live } from "./Live";
import { linkLabel } from "./model";
import { DriverCard } from "./pages/Emulation";
import { useApp, useInput } from "./state";
import { Segmented } from "./ui";

function ControllerStep() {
  const { status } = useApp();
  if (status?.connected) {
    const link = status.link && (linkLabel[status.link] ?? status.link);
    return <p role="status"><span className="dot live" aria-hidden /> {modelName(status.model)}{link && ` · ${link}`}</p>;
  }
  return (
    <>
      <p role="status">{L("No controller connected", "Kontrolcü bağlı değil")}</p>
      <p>{L("Connect your controller with a USB cable.", "Kontrolcüyü USB kablosuyla bağlayın.")}</p>
      <p>{L("To pair over Bluetooth, hold PS + Create until the light bar flashes, then add the controller in Windows Bluetooth settings.", "Bluetooth ile eşleştirmek için ışık çubuğu yanıp sönene kadar PS + Create tuşlarına basılı tutun, ardından Windows Bluetooth ayarlarından kontrolcüyü ekleyin.")}</p>
    </>
  );
}

function DriversStep() {
  const { emulation } = useApp();
  useEffect(() => send({ type: "emulation.status" }), []);
  return (
    <>
      <p>{L("Use a virtual controller for games that only recognize Xbox controllers or a DualSense connected by USB.", "Yalnızca Xbox kontrolcülerini veya USB ile bağlı DualSense'i tanıyan oyunlar için sanal kontrolcü kullanabilirsiniz.")}</p>
      {!emulation && <p role="status">{L("Checking drivers…", "Sürücüler denetleniyor…")}</p>}
      {emulation?.error && <p className="warn" role="status">{emulation.error}</p>}
      <div className="driver-list">
        {emulation?.drivers.map((driver) => <DriverCard key={driver.kind} driver={driver} />)}
      </div>
    </>
  );
}

function TestStep() {
  const { status } = useApp();
  const input = useInput();
  const rumbleTimer = useRef<number | undefined>(undefined);
  const tested = useRef(false);

  useEffect(() => () => {
    window.clearTimeout(rumbleTimer.current);
    if (tested.current) send({ type: "reset" });
  }, []);

  const reset = () => {
    window.clearTimeout(rumbleTimer.current);
    tested.current = false;
    send({ type: "reset" });
  };
  const disabled = !status?.connected;

  return (
    <>
      <Live input={input} edge={status?.model === "DualSenseEdge"} ds4={isDs4(status)} />
      <div className="button-row setup-actions">
        <button className="button" disabled={disabled} onClick={() => {
          tested.current = true;
          send({ type: "lightbar", color: "#39C6FF" });
        }}>{L("Test light bar color", "Işık çubuğu rengini dene")}</button>
        <button className="button" disabled={disabled} onClick={() => {
          tested.current = true;
          window.clearTimeout(rumbleTimer.current);
          send({ type: "rumble", left: 96, right: 96 });
          rumbleTimer.current = window.setTimeout(() => send({ type: "rumble", left: 0, right: 0 }), 300);
        }}>{L("Test rumble", "Titreşimi dene")}</button>
        <button className="button" disabled={disabled} onClick={() => {
          tested.current = true;
          send({ type: "trigger.preview", side: "right", config: { mode: "feedback", params: { start: 2, strength: 3 }, zones: null, frequency: 0 } });
        }}>{L("Test R2 resistance", "R2 direncini dene")}</button>
        <button className="button ghost" onClick={reset}>{L("Reset", "Sıfırla")}</button>
      </div>
    </>
  );
}

export function SetupWizard({ settings }: { settings: AppSettings }) {
  const [step, setStep] = useState(0);
  const heading = useRef<HTMLHeadingElement>(null);
  const selectedLanguage = useRef(settings.language);
  const titles = [
    L("Welcome", "Hoş geldiniz"),
    L("Controller", "Kontrolcü"),
    L("Virtual controller (optional)", "Sanal kontrolcü (isteğe bağlı)"),
    L("Test", "Test"),
    L("Done", "Tamamlandı"),
  ];

  useEffect(() => heading.current?.focus(), [step]);

  const finish = () => send({ type: "settings.save", settings: { ...settings, language: selectedLanguage.current, firstRunDone: true } });
  const next = () => setStep(step + 1);
  return (
    <main className="setup-window" aria-labelledby="setup-heading">
      <div className="setup-panel panel">
        <header>
          {step === 0 && <img className="setup-logo" src={logo} width={96} height={96} alt="TriggerForge" />}
          <div className="setup-brand">TriggerForge</div>
          <p className="small muted-text">{L(`Step ${step + 1} of ${titles.length}`, `Adım ${step + 1} / ${titles.length}`)}</p>
          <h1 id="setup-heading" ref={heading} tabIndex={-1}>{titles[step]}</h1>
        </header>
        {step === 0 && (
          <>
            <p>{L("Set up and customize your DualSense on PC.", "DualSense kontrolcünüzü bilgisayarda kurun ve özelleştirin.")}</p>
            <Segmented
              label={L("Language", "Dil")}
              value={settings.language}
              options={[{ id: "en", label: "English" }, { id: "tr", label: "Türkçe" }]}
              onChange={(language) => {
                if (language === selectedLanguage.current && language === settings.language) return;
                selectedLanguage.current = language;
                send({ type: "settings.save", settings: { ...settings, language } });
              }}
            />
          </>
        )}
        {step === 1 && <ControllerStep />}
        {step === 2 && <DriversStep />}
        {step === 3 && <TestStep />}
        {step === 4 && (
          <ul>
            <li>{L("Triggers — customize L2 and R2 resistance.", "Tetikler — L2 ve R2 direncini özelleştirin.")}</li>
            <li>{L("Profiles — save settings for each game.", "Profiller — her oyun için ayarları kaydedin.")}</li>
            <li>{L("Integrations — connect supported games and apps.", "Entegrasyonlar — desteklenen oyunları ve uygulamaları bağlayın.")}</li>
          </ul>
        )}
        <footer className="button-row setup-actions setup-footer">
          <button className="button ghost" onClick={finish}>{L("Skip setup", "Kurulumu atla")}</button>
          <span className="spacer" />
          <button className="button" disabled={step === 0} onClick={() => setStep(step - 1)}>{L("Back", "Geri")}</button>
          {step === 2 && <button className="button ghost" onClick={next}>{L("Not now", "Şimdi değil")}</button>}
          {step === 4
            ? <button className="button primary" onClick={finish}>{L("Finish", "Bitir")}</button>
            : <button className="button primary" onClick={next}>{L("Next", "İleri")}</button>}
        </footer>
      </div>
    </main>
  );
}
