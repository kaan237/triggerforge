import { useState } from "react";
import type { Binding, ButtonName, Direction, GyroMouse, Profile, StickMapping, TouchpadMapping } from "../host";
import { ActionEditor, BindingEditor, KeyField } from "../MappingEditors";
import { bindingSummary, buttonDefs, directions } from "../mapping";
import { clamp, defaultBinding, processStick, rawAxis } from "../model";
import { useApp, useInput } from "../state";
import { Check, Section, Segmented, Slider, Toggle, fixed, pct } from "../ui";
import type { EditMapping } from "./Mapping";

type TabProps = { draft: Profile; editMapping: EditMapping };

const times = (v: number) => `${fixed(1)(v)}×`;

// ---- touchpad ----

const zoneNames = { halves: ["Sol yarı", "Sağ yarı"], quadrants: ["Sol üst", "Sağ üst", "Sol alt", "Sağ alt"] } as const;

function PadDiagram({ tp, selected, onSelect }: { tp: TouchpadMapping; selected: number; onSelect: (i: number) => void }) {
  const input = useInput();
  const { profiles, draft } = useApp();
  const W = 192;
  const H = 108;
  const quad = tp.zoneLayout === "quadrants";
  const rects = quad
    ? [
        [0, 0],
        [W / 2, 0],
        [0, H / 2],
        [W / 2, H / 2],
      ].map(([x, y]) => ({ x, y, w: W / 2, h: H / 2 }))
    : [0, W / 2].map((x) => ({ x, y: 0, w: W / 2, h: H }));
  return (
    <svg viewBox={`-1 -1 ${W + 2} ${H + 2}`} className="pad-diagram" role="img" aria-label="Touchpad bölgeleri">
      {rects.map((r, i) => (
        <g key={i} onClick={() => onSelect(i)} className={selected === i ? "zone sel" : "zone"}>
          <rect x={r.x} y={r.y} width={r.w} height={r.h} />
          <text x={r.x + 6} y={r.y + 12} className="zone-name">
            {zoneNames[tp.zoneLayout][i]}
          </text>
          <text x={r.x + 6} y={r.y + 23} className="zone-sum">
            {truncate(bindingSummary(tp.zones[i], profiles, draft?.macros ?? []), quad ? 20 : 30)}
          </text>
        </g>
      ))}
      {input?.touch.map((t) => <circle key={t.id} cx={(t.x / 1920) * W} cy={(t.y / 1080) * H} r="3.5" className="finger" />)}
    </svg>
  );
}

const truncate = (s: string, n: number) => (s.length > n ? s.slice(0, n - 1) + "…" : s);

export function TouchpadTab({ draft, editMapping }: TabProps) {
  const tp = draft.mapping.touchpad;
  const [zone, setZone] = useState(0);
  const set = (patch: Partial<TouchpadMapping>) => editMapping((m) => ({ ...m, touchpad: { ...m.touchpad, ...patch } }));
  const setLayout = (zoneLayout: TouchpadMapping["zoneLayout"]) => {
    // halves → quadrants repeats left/right on the bottom row; quadrants → halves keeps the top row.
    const zones = zoneLayout === "quadrants" ? [0, 1, 0, 1].map((i) => ({ ...(tp.zones[i] ?? defaultBinding()) })) : tp.zones.slice(0, 2);
    set({ zoneLayout, zones });
    setZone(0);
  };
  const setZoneBinding = (i: number, b: Binding) => set({ zones: tp.zones.map((z, j) => (j === i ? b : z)) });
  const z = Math.min(zone, tp.zones.length - 1);

  return (
    <div className="tab-grid">
      <Section title="Touchpad" className="tab-main">
        <div className="row-field">
          <span className="label">Kullanım</span>
          <Segmented
            label="Touchpad kullanımı"
            value={tp.mode}
            onChange={(mode) => set({ mode })}
            options={[
              { id: "off", label: "Kapalı" },
              { id: "mouse", label: "Fare" },
              { id: "zones", label: "Bölgeler" },
            ]}
          />
        </div>
        <p className="hint mode-hint">
          {tp.mode === "off" && "Touchpad yalnız tıklama tuşu olarak çalışır (Tuşlar sekmesindeki Touchpad tık)."}
          {tp.mode === "mouse" && "Parmak imleci hareket ettirir. Pad tıklaması Tuşlar sekmesindeki Touchpad tık atamasını kullanır."}
          {tp.mode === "zones" && "Pad tıklaması parmağın olduğu bölgenin atamasını çalıştırır; Touchpad tık ataması yok sayılır."}
        </p>
        {tp.mode === "mouse" && (
          <div className="tab-controls">
            <Slider label="Hassasiyet" min={0.1} max={5} step={0.1} value={tp.sensitivity} format={times} onChange={(sensitivity) => set({ sensitivity })} />
            <Slider label="İvme" min={0} max={2} step={0.05} value={tp.acceleration} format={fixed(2)} onChange={(acceleration) => set({ acceleration })} />
            <Toggle label="Dokununca tıkla" hint="Tek parmak dokunuş sol tık, iki parmak sağ tık." checked={tp.tapToClick} onChange={(tapToClick) => set({ tapToClick })} />
            <Toggle label="İki parmakla kaydır" checked={tp.scroll} onChange={(scroll) => set({ scroll })} />
            {tp.scroll && <Slider label="Kaydırma hızı" min={0.1} max={5} step={0.1} value={tp.scrollSpeed} format={times} onChange={(scrollSpeed) => set({ scrollSpeed })} />}
          </div>
        )}
        {tp.mode === "zones" && (
          <div className="zones-edit">
            <div className="row-field">
              <span className="label">Bölgeler</span>
              <Segmented
                label="Bölge düzeni"
                value={tp.zoneLayout}
                onChange={setLayout}
                options={[
                  { id: "halves", label: "İki yarı" },
                  { id: "quadrants", label: "Dört çeyrek" },
                ]}
              />
            </div>
            <PadDiagram tp={tp} selected={z} onSelect={setZone} />
          </div>
        )}
      </Section>

      {tp.mode === "zones" && (
        <>
          <Section title={`Bölge: ${zoneNames[tp.zoneLayout][z]}`}>
            <BindingEditor binding={tp.zones[z]} onChange={(b) => setZoneBinding(z, b)} />
          </Section>
          <Section title="Kaydırma hareketleri" className="tab-wide">
            <p className="hint">Tek parmağı hızla padin dörtte birinden fazla kaydır. Kalkınca çalışır.</p>
            <div className="swipe-list">
              {directions.map((d) => (
                <div key={d.id} className="bind-row">
                  <span className="label">{d.label}</span>
                  <ActionEditor action={tp.swipes[d.id]} onChange={(a) => set({ swipes: { ...tp.swipes, [d.id]: a } })} />
                </div>
              ))}
            </div>
          </Section>
        </>
      )}
    </div>
  );
}

// ---- gyro mouse ----

export function GyroMouseTab({ draft, editMapping }: TabProps) {
  const g = draft.mapping.gyroMouse;
  const input = useInput();
  const set = (patch: Partial<GyroMouse>) => editMapping((m) => ({ ...m, gyroMouse: { ...m.gyroMouse, ...patch } }));
  const needsButton = g.mode === "whileHeld" || g.mode === "toggle";
  const f = (n: number) => (n >= 0 ? "+" : "−") + Math.abs(n).toFixed(1).replace(".", ",");

  return (
    <div className="tab-grid">
      <Section title="Gyro fare" className="tab-main">
        <div className="row-field">
          <span className="label">Çalışma</span>
          <Segmented
            label="Gyro fare"
            value={g.mode}
            onChange={(mode) => set({ mode })}
            options={[
              { id: "off", label: "Kapalı" },
              { id: "always", label: "Hep açık" },
              { id: "whileHeld", label: "Basılıyken" },
              { id: "toggle", label: "Aç/kapa" },
            ]}
          />
        </div>
        <div className="row-field">
          <span className="label">Tuş</span>
          <select value={g.button} disabled={!needsButton} onChange={(e) => set({ button: e.target.value as ButtonName })}>
            {buttonDefs.map((b) => (
              <option key={b.id} value={b.id}>
                {b.label}
              </option>
            ))}
          </select>
          <span className="hint">{needsButton ? "Tuş kendi atamasını da çalıştırır." : "Basılıyken ya da aç/kapa için."}</span>
        </div>
        <div className="row-field">
          <span className="label">Yatay eksen</span>
          <Segmented
            label="Yatay eksen"
            value={g.axis}
            onChange={(axis) => set({ axis })}
            options={[
              { id: "yaw", label: "Sağa-sola çevir" },
              { id: "roll", label: "Yana yatır" },
            ]}
          />
        </div>
        <div className="tab-controls">
          <Slider label="Yatay hız" min={0.1} max={10} step={0.1} value={g.sensitivityX} format={times} onChange={(sensitivityX) => set({ sensitivityX })} />
          <Slider label="Dikey hız" min={0.1} max={10} step={0.1} value={g.sensitivityY} format={times} onChange={(sensitivityY) => set({ sensitivityY })} />
          <Slider label="Yumuşatma" min={0} max={0.9} step={0.01} value={g.smoothing} format={pct} onChange={(smoothing) => set({ smoothing })} />
          <Slider label="Ölü bölge" min={0} max={5} step={0.1} value={g.deadzone} format={(v) => `${fixed(1)(v)}°/s`} onChange={(deadzone) => set({ deadzone })} />
          <div className="row-field">
            <span className="label">Ters çevir</span>
            <Check label="X" checked={g.invertX} onChange={(invertX) => set({ invertX })} />
            <Check label="Y" checked={g.invertY} onChange={(invertY) => set({ invertY })} />
          </div>
        </div>
      </Section>
      <Section title="Canlı">
        <dl className="kv compact">
          <dt>Gyro °/s</dt>
          <dd className="num">{input ? input.gyro.map(f).join("  ") : "—"}</dd>
        </dl>
        <p className="hint mode-hint">1× hız derece başına 10 piksel. Kayma varsa Analoglar sayfasından gyroyu kalibre et.</p>
      </Section>
    </div>
  );
}

// ---- sticks ----

const keyPresets: { name: string; keys: Record<Direction, string> }[] = [
  { name: "WASD", keys: { up: "w", down: "s", left: "a", right: "d" } },
  { name: "Ok tuşları", keys: { up: "up", down: "down", left: "left", right: "right" } },
  { name: "Numpad", keys: { up: "numpad8", down: "numpad2", left: "numpad4", right: "numpad6" } },
];

function StickPreview({ side, cfg }: { side: "left" | "right"; cfg: StickMapping }) {
  const { draft } = useApp();
  const input = useInput();
  const R = 50;
  const pos = input ? (side === "left" ? input.left : input.right) : ([128, 128] as [number, number]);
  const hostProcessed = side === "left" ? input?.leftProcessed : input?.rightProcessed;
  const [x, y] = hostProcessed ?? (draft ? processStick(rawAxis(pos[0]), rawAxis(pos[1]), draft.sticks[side]) : [0, 0]);
  const t = cfg.threshold * R;
  const keys = cfg.mode === "keys";
  const lit = (d: Direction) => keys && ((d === "up" && y < -cfg.threshold) || (d === "down" && y > cfg.threshold) || (d === "left" && x < -cfg.threshold) || (d === "right" && x > cfg.threshold));
  return (
    <svg viewBox="-60 -60 120 120" className="stick-preview" role="img" aria-label="Analog eşiği">
      <circle r={R} className="rim" />
      {keys && <rect x={-t} y={-t} width={t * 2} height={t * 2} className="thr" />}
      {keys &&
        (["up", "down", "left", "right"] as Direction[]).map((d) => {
          const [cx, cy] = { up: [0, -R - 5], down: [0, R + 5], left: [-R - 5, 0], right: [R + 5, 0] }[d];
          return <circle key={d} cx={cx} cy={cy} r="3" className={lit(d) ? "dir on" : "dir"} />;
        })}
      <circle cx={clamp(x, -1, 1) * R} cy={clamp(y, -1, 1) * R} r="4" className="proc-dot" />
    </svg>
  );
}

function StickMappingPanel({ side, cfg, onChange }: { side: "left" | "right"; cfg: StickMapping; onChange: (c: StickMapping) => void }) {
  const set = (patch: Partial<StickMapping>) => onChange({ ...cfg, ...patch });
  const preset = keyPresets.find((p) => directions.every((d) => p.keys[d.id] === cfg.keys[d.id]));
  return (
    <Section title={side === "left" ? "Sol analog" : "Sağ analog"}>
      <div className="row-field">
        <Segmented
          label="Analog kullanımı"
          value={cfg.mode}
          onChange={(mode) => set({ mode })}
          options={[
            { id: "off", label: "Kapalı" },
            { id: "keys", label: "Tuşlar" },
            { id: "mouse", label: "Fare" },
            { id: "scroll", label: "Kaydırma" },
          ]}
        />
      </div>
      <div className="stick-map">
        <StickPreview side={side} cfg={cfg} />
        <div className="stick-map-controls">
          {cfg.mode === "off" && <p className="hint">Analog yalnız oyuna gider.</p>}
          {cfg.mode === "keys" && (
            <>
              <div className="row-field">
                <span className="label">Hazır</span>
                <div className="button-group">
                  {keyPresets.map((p) => (
                    <button key={p.name} className={preset === p ? "button on" : "button"} onClick={() => set({ keys: { ...p.keys } })}>
                      {p.name}
                    </button>
                  ))}
                </div>
              </div>
              {directions.map((d) => (
                <div key={d.id} className="row-field dir-key">
                  <span className="label">{d.label}</span>
                  <KeyField compact max={1} value={cfg.keys[d.id] === "none" ? [] : [cfg.keys[d.id]]} onChange={(k) => set({ keys: { ...cfg.keys, [d.id]: k[0] ?? "none" } })} />
                  {cfg.keys[d.id] !== "none" && (
                    <button className="button ghost" onClick={() => set({ keys: { ...cfg.keys, [d.id]: "none" } })}>
                      Yok
                    </button>
                  )}
                </div>
              ))}
              <Slider label="Eşik" min={0.1} max={0.9} step={0.05} value={cfg.threshold} format={pct} onChange={(threshold) => set({ threshold })} />
              <p className="hint">Analog ayarları (ölü bölge, eğri) uygulandıktan sonraki değer. Çaprazda iki tuş birden basılır.</p>
            </>
          )}
          {(cfg.mode === "mouse" || cfg.mode === "scroll") && (
            <>
              <Slider label="Hız" min={0.1} max={5} step={0.1} value={cfg.speed} format={times} onChange={(speed) => set({ speed })} />
              <p className="hint">{cfg.mode === "mouse" ? "Tam eğimde saniyede 1200 piksel × hız." : "Tam eğimde saniyede 15 çentik × hız."}</p>
            </>
          )}
        </div>
      </div>
    </Section>
  );
}

export function StickMappingTab({ draft, editMapping }: TabProps) {
  return (
    <div className="sticks-grid">
      {(["left", "right"] as const).map((s) => (
        <StickMappingPanel key={s} side={s} cfg={draft.mapping.sticks[s]} onChange={(c) => editMapping((m) => ({ ...m, sticks: { ...m.sticks, [s]: c } }))} />
      ))}
    </div>
  );
}
