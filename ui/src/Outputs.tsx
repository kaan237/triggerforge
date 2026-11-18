import { useState } from "react";
import { send, type Output } from "./host";

type Mode = "off" | "feedback" | "slope" | "weapon" | "vibration" | "bow" | "galloping" | "machine";

type Field = { key: string; label: string; min: number; max: number; value: number };

const modes: { id: Mode; label: string; fields: Field[] }[] = [
  { id: "off", label: "Kapalı", fields: [] },
  {
    id: "feedback",
    label: "Direnç",
    fields: [
      { key: "start", label: "Başlangıç", min: 0, max: 9, value: 2 },
      { key: "strength", label: "Güç", min: 1, max: 8, value: 5 },
    ],
  },
  {
    id: "slope",
    label: "Artan direnç",
    fields: [
      { key: "start", label: "Başlangıç", min: 0, max: 8, value: 0 },
      { key: "end", label: "Bitiş", min: 1, max: 9, value: 8 },
      { key: "startStrength", label: "İlk güç", min: 1, max: 8, value: 1 },
      { key: "endStrength", label: "Son güç", min: 1, max: 8, value: 8 },
    ],
  },
  {
    id: "weapon",
    label: "Silah",
    fields: [
      { key: "start", label: "Başlangıç", min: 2, max: 7, value: 3 },
      { key: "end", label: "Kırılma", min: 3, max: 8, value: 6 },
      { key: "strength", label: "Güç", min: 1, max: 8, value: 8 },
    ],
  },
  {
    id: "vibration",
    label: "Titreşim",
    fields: [
      { key: "start", label: "Başlangıç", min: 0, max: 9, value: 0 },
      { key: "amplitude", label: "Şiddet", min: 1, max: 8, value: 6 },
      { key: "frequency", label: "Hz", min: 1, max: 120, value: 30 },
    ],
  },
  {
    id: "bow",
    label: "Yay",
    fields: [
      { key: "start", label: "Başlangıç", min: 0, max: 7, value: 1 },
      { key: "end", label: "Bitiş", min: 1, max: 8, value: 6 },
      { key: "strength", label: "Güç", min: 1, max: 8, value: 6 },
      { key: "snap", label: "Geri tepme", min: 1, max: 8, value: 6 },
    ],
  },
  {
    id: "galloping",
    label: "Dörtnala",
    fields: [
      { key: "start", label: "Başlangıç", min: 0, max: 8, value: 0 },
      { key: "end", label: "Bitiş", min: 1, max: 9, value: 9 },
      { key: "firstFoot", label: "1. ayak", min: 0, max: 6, value: 1 },
      { key: "secondFoot", label: "2. ayak", min: 1, max: 7, value: 4 },
      { key: "frequency", label: "Hz", min: 1, max: 20, value: 3 },
    ],
  },
  {
    id: "machine",
    label: "Makine",
    fields: [
      { key: "start", label: "Başlangıç", min: 0, max: 8, value: 0 },
      { key: "end", label: "Bitiş", min: 1, max: 9, value: 9 },
      { key: "amplitudeA", label: "Şiddet A", min: 0, max: 7, value: 2 },
      { key: "amplitudeB", label: "Şiddet B", min: 0, max: 7, value: 6 },
      { key: "frequency", label: "Hz", min: 1, max: 60, value: 20 },
      { key: "period", label: "Periyot", min: 1, max: 60, value: 8 },
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

export function Outputs({ output, link }: { output: Output | null; link?: string }) {
  const [haptic, setHaptic] = useState({ frequency: 160, amplitude: 70 });
  if (!output) return null;

  const leds = [0, 1, 2, 3, 4];

  return (
    <div className="outputs">
      <section className="panel">
        <h2>Işık</h2>
        <div className="light-row">
          <input
            type="color"
            value={output.lightbar}
            onChange={(e) => send({ type: "lightbar", color: e.target.value })}
            aria-label="Işık çubuğu rengi"
          />
          <span className="num faint">{output.lightbar}</span>
          <div className="segmented" role="group" aria-label="Parlaklık">
            {["Yüksek", "Orta", "Düşük"].map((l, i) => (
              <button key={l} className={output.brightness === i ? "on" : ""} onClick={() => send({ type: "brightness", level: i })}>
                {l}
              </button>
            ))}
          </div>
        </div>
        <div className="light-row">
          <span className="label">Oyuncu</span>
          <div className="player-leds">
            {leds.map((i) => {
              const on = (output.playerLeds >> i) & 1;
              return (
                <button
                  key={i}
                  className={on ? "led on" : "led"}
                  aria-pressed={!!on}
                  aria-label={`Oyuncu LED ${i + 1}`}
                  onClick={() => send({ type: "playerLeds", mask: output.playerLeds ^ (1 << i) })}
                />
              );
            })}
          </div>
          <span className="label">Mikrofon</span>
          <div className="segmented" role="group" aria-label="Mikrofon LED">
            {["Kapalı", "Açık", "Nabız"].map((l, i) => (
              <button key={l} className={output.micLed === i ? "on" : ""} onClick={() => send({ type: "micLed", mode: i })}>
                {l}
              </button>
            ))}
          </div>
        </div>
      </section>

      <section className="panel">
        <h2>Rumble</h2>
        <Slider label="Sol (ağır)" min={0} max={255} value={output.rumbleLeft} onChange={(v) => send({ type: "rumble", left: v, right: output.rumbleRight })} />
        <Slider label="Sağ (hafif)" min={0} max={255} value={output.rumbleRight} onChange={(v) => send({ type: "rumble", left: output.rumbleLeft, right: v })} />
      </section>

      <section className="panel triggers">
        <h2>Tetikler</h2>
        <div className="two">
          <TriggerEditor side="left" />
          <TriggerEditor side="right" />
        </div>
      </section>

      <section className="panel">
        <h2>Haptik test</h2>
        {link === "Usb" ? (
          <p className="muted-text">USB'de haptik ses kanalından gider; bu test Bluetooth içindir.</p>
        ) : (
          <>
            <Slider label="Frekans" min={20} max={600} value={haptic.frequency} suffix=" Hz" onChange={(v) => setHaptic({ ...haptic, frequency: v })} />
            <Slider label="Şiddet" min={5} max={100} value={haptic.amplitude} suffix=" %" onChange={(v) => setHaptic({ ...haptic, amplitude: v })} />
            <div className="button-row">
              {[
                ["Sol", true, false],
                ["Sağ", false, true],
                ["İkisi", true, true],
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
      </section>

      <button className="button ghost" onClick={() => send({ type: "reset" })}>
        Hepsini sıfırla
      </button>
    </div>
  );
}
