import { L } from "../i18n";
import { useEffect, useRef } from "react";
import type { Brightness, Lightbar, MicLed, Profile } from "../host";
import { brightnessFactor, effectColor, effects, ledMask } from "../model";
import { useApp, useInput } from "../state";
import { DraftBar, PageHead, Section, Segmented, Slider, fixed } from "../ui";

// Runs the effect locally so the draft can be seen before it is saved.
function LightPreview({ light, battery, player }: { light: Lightbar; battery: number; player: number }) {
  const left = useRef<HTMLDivElement>(null);
  const right = useRef<HTMLDivElement>(null);
  const hex = useRef<HTMLSpanElement>(null);

  useEffect(() => {
    let raf = 0;
    const reduce = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    const frame = () => {
      const c = effectColor(light, reduce ? 0 : performance.now() / 1000, battery, player);
      const o = light.effect === "off" ? 1 : brightnessFactor[light.brightness];
      for (const el of [left.current, right.current]) {
        if (!el) continue;
        el.style.background = c === "#000000" ? "" : c;
        el.style.opacity = String(o);
      }
      if (hex.current) hex.current.textContent = light.effect === "off" ? L("off", "kapalı") : c;
      if (!reduce) raf = requestAnimationFrame(frame);
    };
    frame();
    return () => cancelAnimationFrame(raf);
  }, [light, battery, player]);

  return (
    <div className="light-preview">
      <div className="lp-pad">
        <div ref={left} className="lp-strip left" />
        <div className="lp-touch" />
        <div ref={right} className="lp-strip right" />
      </div>
      <span ref={hex} className="num faint lp-hex" />
    </div>
  );
}

const needsColor = (e: Lightbar["effect"]) => e === "static" || e === "breathe" || e === "strobe";
const needsSecond = (e: Lightbar["effect"]) => e === "breathe" || e === "strobe";
const needsSpeed = (e: Lightbar["effect"]) => e === "breathe" || e === "strobe" || e === "rainbow";

function ColorField({ label, value, onChange }: { label: string; value: string; onChange: (v: string) => void }) {
  return (
    <label className="color-field">
      <span>{label}</span>
      <input type="color" value={value.toLowerCase()} onChange={(e) => onChange(e.target.value.toUpperCase())} />
      <span className="num faint">{value.toUpperCase()}</span>
    </label>
  );
}

export function Light() {
  const { draft, edit } = useApp();
  const input = useInput();
  if (!draft) return <div className="page"><PageHead title={L("Light", "Işık")} /></div>;

  const l = draft.lightbar;
  const setL = (patch: Partial<Lightbar>) => edit((p) => ({ ...p, lightbar: { ...p.lightbar, ...patch } }));
  const setLeds = (patch: Partial<Profile["playerLeds"]>) => edit((p) => ({ ...p, playerLeds: { ...p.playerLeds, ...patch } }));
  const mask = ledMask(draft.playerLeds);

  return (
    <div className="page light-page">
      <PageHead title={L("Light", "Işık")}>
        <DraftBar />
      </PageHead>

      <div className="light-grid">
        <Section title={L("Light bar", "Işık çubuğu")}>
          <ul className="effect-list" role="radiogroup" aria-label={L("Effect", "Efekt")}>
            {effects.map((e) => (
              <li key={e.id}>
                <button role="radio" aria-checked={l.effect === e.id} className={l.effect === e.id ? "on" : ""} onClick={() => setL({ effect: e.id })}>
                  <span className="radio" aria-hidden />
                  <span className="effect-name">{e.label}</span>
                  <span className="faint">{e.hint}</span>
                </button>
              </li>
            ))}
          </ul>
        </Section>

        <div className="light-side">
          <Section title={L("Preview", "Önizleme")} aside={<span className="faint small">{L("unsaved preview", "taslak, kaydetmeden")}</span>}>
            <LightPreview light={l} battery={input?.battery ?? 80} player={draft.playerLeds.number} />
            <div className="light-controls">
              {needsColor(l.effect) && <ColorField label={needsSecond(l.effect) ? L("Color 1", "Renk 1") : L("Color", "Renk")} value={l.color} onChange={(color) => setL({ color })} />}
              {needsSecond(l.effect) && <ColorField label={L("Color 2", "Renk 2")} value={l.color2} onChange={(color2) => setL({ color2 })} />}
              {needsSpeed(l.effect) && <Slider label={L("Speed", "Hız")} min={0.1} max={5} step={0.1} value={l.speed} format={(v) => `${fixed(1)(v)}×`} onChange={(speed) => setL({ speed })} />}
              <div className="row-field">
                <span className="label">{L("Brightness", "Parlaklık")}</span>
                <Segmented<Brightness>
                  label={L("Brightness", "Parlaklık")}
                  value={l.brightness}
                  onChange={(brightness) => setL({ brightness })}
                  options={[
                    { id: "high", label: L("High", "Yüksek") },
                    { id: "medium", label: L("Medium", "Orta") },
                    { id: "low", label: L("Low", "Düşük") },
                  ]}
                />
              </div>
            </div>
          </Section>

          <Section title={L("Player LEDs", "Oyuncu LED'leri")}>
            <div className="row-field">
              <Segmented
                label={L("Player LED mode", "Oyuncu LED modu")}
                value={draft.playerLeds.mode}
                onChange={(mode) => setLeds({ mode })}
                options={[
                  { id: "off", label: L("Off", "Kapalı") },
                  { id: "number", label: L("Number", "Numara") },
                  { id: "custom", label: L("Custom", "Özel") },
                ]}
              />
              {draft.playerLeds.mode === "number" && (
                <Segmented
                  label={L("Player number", "Oyuncu numarası")}
                  value={draft.playerLeds.number}
                  onChange={(number) => setLeds({ number })}
                  options={[1, 2, 3, 4].map((n) => ({ id: n, label: String(n) }))}
                />
              )}
            </div>
            <div className="led-row">
              {[0, 1, 2, 3, 4].map((i) => {
                const on = (mask >> i) & 1;
                const custom = draft.playerLeds.mode === "custom";
                return (
                  <button
                    key={i}
                    className={on ? "led on" : "led"}
                    disabled={!custom}
                    aria-pressed={!!on}
                    aria-label={`LED ${i + 1}`}
                    onClick={() => setLeds({ mask: draft.playerLeds.mask ^ (1 << i) })}
                  />
                );
              })}
              <span className="faint small">{draft.playerLeds.mode === "custom" ? L("Click the LEDs", "LED'lere tıkla") : ""}</span>
            </div>
          </Section>

          <Section title={L("Microphone LED", "Mikrofon LED'i")}>
            <Segmented<MicLed>
              label={L("Microphone LED", "Mikrofon LED")}
              value={draft.micLed}
              onChange={(micLed) => edit((p) => ({ ...p, micLed }))}
              options={[
                { id: "off", label: L("Off", "Kapalı") },
                { id: "on", label: L("On", "Açık") },
                { id: "pulse", label: L("Pulse", "Nabız") },
              ]}
            />
          </Section>
        </div>
      </div>
    </div>
  );
}
