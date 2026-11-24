import { L } from "./i18n";
import { useState } from "react";
import { send, type Output } from "./host";

type Mode = "off" | "feedback" | "slope" | "weapon" | "vibration" | "bow" | "galloping" | "machine";

type Field = { key: string; label: string; min: number; max: number; value: number };

const modes: { id: Mode; label: string; fields: Field[] }[] = [
  { id: "off", get label() { return L("Off", "Kapalı"); }, fields: [] },
  {
    id: "feedback",
    get label() { return L("Resistance", "Direnç"); },
    fields: [
      { key: "start", get label() { return L("Start", "Başlangıç"); }, min: 0, max: 9, value: 2 },
      { key: "strength", get label() { return L("Strength", "Güç"); }, min: 1, max: 8, value: 5 },
    ],
  },
  {
    id: "slope",
    get label() { return L("Rising resistance", "Artan direnç"); },
    fields: [
      { key: "start", get label() { return L("Start", "Başlangıç"); }, min: 0, max: 8, value: 0 },
      { key: "end", get label() { return L("End", "Bitiş"); }, min: 1, max: 9, value: 8 },
      { key: "startStrength", get label() { return L("Start strength", "İlk güç"); }, min: 1, max: 8, value: 1 },
      { key: "endStrength", get label() { return L("End strength", "Son güç"); }, min: 1, max: 8, value: 8 },
    ],
  },
  {
    id: "weapon",
    get label() { return L("Weapon", "Silah"); },
    fields: [
      { key: "start", get label() { return L("Start", "Başlangıç"); }, min: 2, max: 7, value: 3 },
      { key: "end", get label() { return L("Break point", "Kırılma"); }, min: 3, max: 8, value: 6 },
      { key: "strength", get label() { return L("Strength", "Güç"); }, min: 1, max: 8, value: 8 },
    ],
  },
  {
    id: "vibration",
    get label() { return L("Vibration", "Titreşim"); },
    fields: [
      { key: "start", get label() { return L("Start", "Başlangıç"); }, min: 0, max: 9, value: 0 },
      { key: "amplitude", get label() { return L("Intensity", "Şiddet"); }, min: 1, max: 8, value: 6 },
      { key: "frequency", label: "Hz", min: 1, max: 120, value: 30 },
    ],
  },
  {
    id: "bow",
    get label() { return L("Bow", "Yay"); },
    fields: [
      { key: "start", get label() { return L("Start", "Başlangıç"); }, min: 0, max: 7, value: 1 },
      { key: "end", get label() { return L("End", "Bitiş"); }, min: 1, max: 8, value: 6 },
      { key: "strength", get label() { return L("Strength", "Güç"); }, min: 1, max: 8, value: 6 },
      { key: "snap", get label() { return L("Kickback", "Geri tepme"); }, min: 1, max: 8, value: 6 },
    ],
  },
  {
    id: "galloping",
    get label() { return L("Gallop", "Dörtnala"); },
    fields: [
      { key: "start", get label() { return L("Start", "Başlangıç"); }, min: 0, max: 8, value: 0 },
      { key: "end", get label() { return L("End", "Bitiş"); }, min: 1, max: 9, value: 9 },
      { key: "firstFoot", get label() { return L("First foot", "1. ayak"); }, min: 0, max: 6, value: 1 },
      { key: "secondFoot", get label() { return L("Second foot", "2. ayak"); }, min: 1, max: 7, value: 4 },
      { key: "frequency", label: "Hz", min: 1, max: 20, value: 3 },
    ],
  },
  {
    id: "machine",
    get label() { return L("Machine", "Makine"); },
    fields: [
      { key: "start", get label() { return L("Start", "Başlangıç"); }, min: 0, max: 8, value: 0 },
      { key: "end", get label() { return L("End", "Bitiş"); }, min: 1, max: 9, value: 9 },
      { key: "amplitudeA", get label() { return L("Intensity A", "Şiddet A"); }, min: 0, max: 7, value: 2 },
      { key: "amplitudeB", get label() { return L("Intensity B", "Şiddet B"); }, min: 0, max: 7, value: 6 },
      { key: "frequency", label: "Hz", min: 1, max: 60, value: 20 },
      { key: "period", get label() { return L("Period", "Periyot"); }, min: 1, max: 60, value: 8 },
    ],
  },
];

function Slider({ label, value, min, max, onChange, suffix }: { label: string; value: number; min: number; max: number; onChange: (v: number) => void; suffix?: string }) {
  return (
    <label className="slider">
      <span>{label}</span>
      <input type="range" min={min} max={max} value={value} onChange={(e) => onChange(Number(e.target.value))} />
      <span className="num">
        {value}
        {suffix}
      </span>
    </label>
  );
}

function TriggerEditor({ side }: { side: "left" | "right" }) {
  const [mode, setMode] = useState<Mode>("off");
  const [values, setValues] = useState<Record<string, Record<string, number>>>(() =>
    Object.fromEntries(modes.map((m) => [m.id, Object.fromEntries(m.fields.map((f) => [f.key, f.value]))])),
  );

  const def = modes.find((m) => m.id === mode)!;
  const apply = (m: Mode, params: Record<string, number>) => send({ type: "trigger", side, mode: m, params });

  return (
    <div className="trigger-editor">
      <div className="row-head">
        <h3>{side === "left" ? "L2" : "R2"}</h3>
        <select
          value={mode}
          onChange={(e) => {
            const m = e.target.value as Mode;
            setMode(m);
            apply(m, values[m]);
          }}
        >
          {modes.map((m) => (
            <option key={m.id} value={m.id}>
              {m.label}
            </option>
          ))}
        </select>
      </div>
      {def.fields.map((f) => (
        <Slider
          key={f.key}
          label={f.label}
          min={f.min}
          max={f.max}
          value={values[mode][f.key]}
          onChange={(v) => {
            const next = { ...values, [mode]: { ...values[mode], [f.key]: v } };
            setValues(next);
            apply(mode, next[mode]);
          }}
        />
      ))}
    </div>
  );
}

export function Outputs({ output, link, ds4 = false }: { output: Output | null; link?: string; ds4?: boolean }) {
  const [haptic, setHaptic] = useState({ frequency: 160, amplitude: 70 });
  if (!output) return null;

  const leds = [0, 1, 2, 3, 4];

  return (
    <div className="outputs">
      <section className="panel">
        <h2>{L("Light", "Işık")}</h2>
        <div className="light-row">
          <input
            type="color"
            value={output.lightbar}
            onChange={(e) => send({ type: "lightbar", color: e.target.value })}
            aria-label={L("Light bar color", "Işık çubuğu rengi")}
          />
          <span className="num faint">{output.lightbar}</span>
          <div className="segmented" role="group" aria-label={L("Brightness", "Parlaklık")}>
            {[L("High", "Yüksek"), L("Medium", "Orta"), L("Low", "Düşük")].map((l, i) => (
              <button key={l} className={output.brightness === i ? "on" : ""} onClick={() => send({ type: "brightness", level: i })}>
                {l}
              </button>
            ))}
          </div>
        </div>
        {!ds4 && <div className="light-row">
          <span className="label">{L("Player", "Oyuncu")}</span>
          <div className="player-leds">
            {leds.map((i) => {
              const on = (output.playerLeds >> i) & 1;
              return (
                <button
                  key={i}
                  className={on ? "led on" : "led"}
                  aria-pressed={!!on}
                  aria-label={L(`Player LED ${i + 1}`, `Oyuncu LED ${i + 1}`)}
                  onClick={() => send({ type: "playerLeds", mask: output.playerLeds ^ (1 << i) })}
                />
              );
            })}
          </div>
          <span className="label">{L("Microphone", "Mikrofon")}</span>
          <div className="segmented" role="group" aria-label={L("Microphone LED", "Mikrofon LED")}>
            {[L("Off", "Kapalı"), L("On", "Açık"), L("Pulse", "Nabız")].map((l, i) => (
              <button key={l} className={output.micLed === i ? "on" : ""} onClick={() => send({ type: "micLed", mode: i })}>
                {l}
              </button>
            ))}
          </div>
        </div>}
      </section>

      <section className="panel">
        <h2>Rumble</h2>
        <Slider label={L("Left (heavy)", "Sol (ağır)")} min={0} max={255} value={output.rumbleLeft} onChange={(v) => send({ type: "rumble", left: v, right: output.rumbleRight })} />
        <Slider label={L("Right (light)", "Sağ (hafif)")} min={0} max={255} value={output.rumbleRight} onChange={(v) => send({ type: "rumble", left: output.rumbleLeft, right: v })} />
      </section>

      {!ds4 && <section className="panel triggers">
        <h2>{L("Triggers", "Tetikler")}</h2>
        <div className="two">
          <TriggerEditor side="left" />
          <TriggerEditor side="right" />
        </div>
      </section>}

      {!ds4 && <section className="panel">
        <h2>{L("Haptics test", "Haptik test")}</h2>
        {link === "Usb" ? (
          <p className="muted-text">{L("USB haptics use the audio channel. This test requires Bluetooth.", "USB'de haptik ses kanalından gider; bu test Bluetooth içindir.")}</p>
        ) : (
          <>
            <Slider label={L("Frequency", "Frekans")} min={20} max={600} value={haptic.frequency} suffix=" Hz" onChange={(v) => setHaptic({ ...haptic, frequency: v })} />
            <Slider label={L("Intensity", "Şiddet")} min={5} max={100} value={haptic.amplitude} suffix=" %" onChange={(v) => setHaptic({ ...haptic, amplitude: v })} />
            <div className="button-row">
              {[
                [L("Left", "Sol"), true, false],
                [L("Right", "Sağ"), false, true],
                [L("Both", "İkisi"), true, true],
              ].map(([label, left, right]) => (
                <button
                  key={String(label)}
                  className="button"
                  onClick={() => send({ type: "haptic", frequency: haptic.frequency, amplitude: haptic.amplitude / 100, left, right, ms: 600 })}
                >
                  {label}
                </button>
              ))}
            </div>
          </>
        )}
      </section>}

      <button className="button ghost" onClick={() => send({ type: "reset" })}>
        {L("Reset all", "Hepsini sıfırla")}
      </button>
    </div>
  );
}
