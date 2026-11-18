import { useEffect, useRef, useState, type PointerEvent } from "react";
import { CurvePlot } from "../CurvePlot";
import { send, type TriggerConfig, type TriggerMode, type TriggerResponse } from "../host";
import { clamp, deepEqual, isZoneMode, modeDef, presets, processTrigger, triggerModes, withMode, zoneFrequency, zoneProfile } from "../model";
import { useApp, useInput } from "../state";
import { DraftBar, PageHead, Slider, fixed } from "../ui";

type Side = "left" | "right";

// Graph geometry (viewBox units)
const X0 = 32;
const ZW = 60;
const TOP = 14;
const BASE = 182;
const UNIT = (BASE - TOP) / 8;
const TRACK_Y = 222;
const W = X0 + ZW * 10 + 8;
const H = 248;

const zoneAt = (x: number) => clamp(Math.floor((x - X0) / ZW), 0, 9);
const levelAt = (y: number) => clamp(Math.round((BASE - y) / UNIT), 0, 8);

function ZoneEditor({ config, onChange, pull }: { config: TriggerConfig; onChange: (c: TriggerConfig) => void; pull: number }) {
  const svg = useRef<SVGSVGElement>(null);
  const drag = useRef<{ target: "graph" | "start" | "end"; last: number } | null>(null);
  const def = modeDef(config.mode);
  const { level, vibe } = zoneProfile(config);
  const p = config.params;
  const hasRange = def.edit === "range" || def.edit === "start";
  const editable = def.edit === "zones" || def.edit === "start" || config.mode === "slope" || p.strength !== undefined;

  const local = (e: PointerEvent) => {
    const m = svg.current?.getScreenCTM();
    if (!m) return null;
    const q = new DOMPoint(e.clientX, e.clientY).matrixTransform(m.inverse());
    return { x: q.x, y: q.y };
  };

  const applyGraph = (zone: number, lv: number, from: number) => {
    if (def.edit === "zones") {
      const zones = [...(config.zones ?? Array(10).fill(0))];
      // fill the zones crossed since the last move so fast drags leave no gaps
      const a = Math.min(from, zone);
      const b = Math.max(from, zone);
      for (let i = a; i <= b; i++) zones[i] = lv;
      onChange({ ...config, zones });
    } else if (config.mode === "feedback") {
      onChange({ ...config, params: { ...p, start: zone, strength: Math.max(1, lv) } });
    } else if (config.mode === "vibration") {
      onChange({ ...config, params: { ...p, start: zone, amplitude: Math.max(1, lv) } });
    } else if (config.mode === "slope") {
      const nearStart = Math.abs(zone - p.start) <= Math.abs(zone - p.end);
      const next = nearStart
        ? { ...p, start: clamp(zone, 0, p.end - 1), startStrength: Math.max(1, lv) }
        : { ...p, end: clamp(zone, p.start + 1, 9), endStrength: Math.max(1, lv) };
      onChange({ ...config, params: next });
    } else if (p.strength !== undefined) {
      onChange({ ...config, params: { ...p, strength: Math.max(1, lv) } });
    }
  };

  const applyHandle = (which: "start" | "end", x: number) => {
    const startDef = def.params.find((f) => f.key === "start");
    const endDef = def.params.find((f) => f.key === "end");
    const edge = Math.round((x - X0) / ZW);
    if (which === "start" && startDef) {
      const hi = endDef ? Math.min(startDef.max, p.end - 1) : startDef.max;
      onChange({ ...config, params: { ...p, start: clamp(edge, startDef.min, hi) } });
    } else if (which === "end" && endDef) {
      onChange({ ...config, params: { ...p, end: clamp(edge - 1, Math.max(endDef.min, p.start + 1), endDef.max) } });
    }
  };

  const down = (e: PointerEvent<SVGSVGElement>) => {
    const q = local(e);
    if (!q) return;
    if (q.y > BASE + 22 && hasRange) {
      const sx = X0 + p.start * ZW;
      const ex = X0 + (p.end + 1) * ZW;
      const which = def.edit === "start" || Math.abs(q.x - sx) <= Math.abs(q.x - ex) ? "start" : "end";
      drag.current = { target: which, last: 0 };
      applyHandle(which, q.x);
    } else if (q.y <= BASE + 8 && editable) {
      const z = zoneAt(q.x);
      drag.current = { target: "graph", last: z };
      applyGraph(z, levelAt(q.y), z);
    } else return;
    e.currentTarget.setPointerCapture(e.pointerId);
  };

  const move = (e: PointerEvent<SVGSVGElement>) => {
    const d = drag.current;
    if (!d) return;
    const q = local(e);
    if (!q) return;
    if (d.target === "graph") {
      const z = zoneAt(q.x);
      applyGraph(z, levelAt(q.y), d.last);
      d.last = z;
    } else applyHandle(d.target, q.x);
  };

  const tops = level.map((lv, i) => `${X0 + i * ZW + ZW / 2},${BASE - lv * UNIT}`).join(" ");
  const liveX = X0 + clamp(pull, 0, 1) * ZW * 10;

  return (
    <svg
      ref={svg}
      viewBox={`0 0 ${W} ${hasRange ? H : BASE + 26}`}
      className={editable ? "zones editable" : "zones"}
      onPointerDown={down}
      onPointerMove={move}
      onPointerUp={() => (drag.current = null)}
      onPointerCancel={() => (drag.current = null)}
      role="img"
      aria-label="Tetik direnç grafiği"
    >
      <defs>
        <pattern id="vibe" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
          <rect width="6" height="6" className="vibe-bg" />
          <line x1="0" y1="0" x2="0" y2="6" className="vibe-line" />
        </pattern>
      </defs>
      {[0, 2, 4, 6, 8].map((lv) => (
        <g key={lv} className="grid">
          <line x1={X0} x2={X0 + ZW * 10} y1={BASE - lv * UNIT} y2={BASE - lv * UNIT} />
          <text x={X0 - 8} y={BASE - lv * UNIT + 4} textAnchor="end">
            {lv}
          </text>
        </g>
      ))}
      {level.map((lv, i) => (
        <g key={i}>
          <rect x={X0 + i * ZW + 4} y={TOP} width={ZW - 8} height={BASE - TOP} className="zone-col" />
          {lv > 0 && <rect x={X0 + i * ZW + 4} y={BASE - lv * UNIT} width={ZW - 8} height={lv * UNIT} className={vibe[i] ? "bar vibe" : "bar"} />}
          <text x={X0 + i * ZW + ZW / 2} y={BASE + 16} textAnchor="middle" className="axis-label">
            {i + 1}
          </text>
        </g>
      ))}
      {config.mode !== "off" && <polyline points={tops} className="profile-line" />}

      {hasRange && (
        <g className="track">
          <line x1={X0} x2={X0 + ZW * 10} y1={TRACK_Y} y2={TRACK_Y} className="track-line" />
          <line
            x1={X0 + p.start * ZW}
            x2={def.edit === "start" ? X0 + ZW * 10 : X0 + (p.end + 1) * ZW}
            y1={TRACK_Y}
            y2={TRACK_Y}
            className="track-band"
          />
          <rect x={X0 + p.start * ZW - 4} y={TRACK_Y - 11} width="8" height="22" rx="2" className="track-handle" />
          {def.edit === "range" && <rect x={X0 + (p.end + 1) * ZW - 4} y={TRACK_Y - 11} width="8" height="22" rx="2" className="track-handle" />}
        </g>
      )}

      <g className="pull">
        <line x1={liveX} x2={liveX} y1={TOP - 6} y2={BASE} />
        <rect x={liveX - 3} y={TOP - 9} width="6" height="6" rx="1" />
      </g>
    </svg>
  );
}

function ParamSliders({ config, onChange }: { config: TriggerConfig; onChange: (c: TriggerConfig) => void }) {
  const def = modeDef(config.mode);
  const fields = def.params.filter((f) => f.key !== "start" && f.key !== "end");
  const set = (key: string, v: number) => {
    const params = { ...config.params, [key]: v };
    if (config.mode === "galloping") {
      if (key === "firstFoot" && v >= params.secondFoot) params.secondFoot = Math.min(7, v + 1);
      if (key === "secondFoot" && v <= params.firstFoot) params.firstFoot = Math.max(0, v - 1);
    }
    onChange({ ...config, params });
  };
  return (
    <div className="param-grid">
      {fields.map((f) => (
        <Slider key={f.key} label={f.label} min={f.min} max={f.max} value={config.params[f.key] ?? f.def} onChange={(v) => set(f.key, v)} format={f.key === "frequency" ? (v) => `${v} Hz` : undefined} />
      ))}
      {config.mode === "zonesVibration" && (
        <Slider label={zoneFrequency.label} min={zoneFrequency.min} max={zoneFrequency.max} value={config.frequency} format={(v) => `${v} Hz`} onChange={(frequency) => onChange({ ...config, frequency })} />
      )}
    </div>
  );
}

function hint(config: TriggerConfig): string {
  const def = modeDef(config.mode);
  if (def.edit === "zones") return "Grafikte tıkla ya da sürükle: her bölgenin gücü 0–8.";
  if (config.mode === "feedback" || config.mode === "vibration") return "Grafiğe tıkla: başlangıç ve güç. Alttaki tutamak başlangıcı kaydırır.";
  if (config.mode === "slope") return "Grafikte iki ucu sürükle. Alttaki tutamaklar aralığı belirler.";
  if (config.mode === "off") return "Tetik serbest, direnç yok.";
  return "Alttaki tutamaklarla aralığı seç, gücü kaydırıcılardan ayarla.";
}

function ResponseEditor({ value, onChange, raw }: { value: TriggerResponse; onChange: (r: TriggerResponse) => void; raw: number }) {
  const t = raw <= value.deadzone ? 0 : clamp((raw - value.deadzone) / Math.max(1, value.max - value.deadzone), 0, 1);
  const out = processTrigger(raw, value);
  return (
    <div className="response">
      <CurvePlot shape={value} onChange={(s) => onChange({ ...value, ...s })} live={t} />
      <div className="response-side">
        <Slider label="Ölü bölge" min={0} max={200} value={value.deadzone} onChange={(deadzone) => onChange({ ...value, deadzone, max: Math.max(value.max, deadzone + 1) })} />
        <Slider label="Tam basış" min={56} max={255} value={value.max} onChange={(max) => onChange({ ...value, max: Math.max(max, value.deadzone + 1) })} />
        <dl className="kv compact">
          <dt>Ham</dt>
          <dd className="num">{raw}</dd>
          <dt>Çıkış</dt>
          <dd className="num">{fixed(2)(out)}</dd>
        </dl>
        <p className="hint">Basınç eğrisi sanal kontrolcüyle (Faz 4) uygulanacak; şimdilik profile kaydedilir.</p>
      </div>
    </div>
  );
}

export function Triggers() {
  const { draft, edit } = useApp();
  const input = useInput();
  const [side, setSide] = useState<Side>("right");
  const [trying, setTrying] = useState(false);

  const cfg = draft?.triggers[side];

  useEffect(() => {
    if (trying && cfg) send({ type: "trigger.preview", side, config: cfg });
  }, [trying, side, cfg]);

  useEffect(() => {
    if (!trying) return;
    return () => send({ type: "trigger.preview", side, config: null });
  }, [trying, side]);

  if (!draft || !cfg) return <div className="page"><PageHead title="Tetikler" /></div>;

  const setCfg = (c: TriggerConfig) => edit((p) => ({ ...p, triggers: { ...p.triggers, [side]: c } }));
  const setResp = (r: TriggerResponse) => edit((p) => ({ ...p, triggerResponse: { ...p.triggerResponse, [side]: r } }));
  const raw = input ? (side === "left" ? input.l2 : input.r2) : 0;
  const def = modeDef(cfg.mode);

  return (
    <div className="page triggers-page">
      <PageHead title="Tetikler">
        <DraftBar />
      </PageHead>

      <div className="triggers-grid">
        <div className="trigger-main">
          <div className="trigger-bar">
            <div className="side-tabs" role="tablist">
              {(["left", "right"] as Side[]).map((s) => (
                <button key={s} role="tab" aria-selected={side === s} className={side === s ? "on" : ""} onClick={() => setSide(s)}>
                  <span className="side-name">{s === "left" ? "L2" : "R2"}</span>
                  <span className="side-mode">{modeDef(draft.triggers[s].mode).label}</span>
                </button>
              ))}
            </div>
            <label className="field-inline">
              <span>Mod</span>
              <select value={cfg.mode} onChange={(e) => setCfg(withMode(cfg, e.target.value as TriggerMode))}>
                {triggerModes.map((m) => (
                  <option key={m.id} value={m.id}>
                    {m.label}
                  </option>
                ))}
              </select>
            </label>
            <span className="spacer" />
            <button className={trying ? "button try on" : "button try"} aria-pressed={trying} onClick={() => setTrying(!trying)}>
              <span className="try-dot" aria-hidden />
              {trying ? "Deneniyor" : "Dene"}
            </button>
          </div>

          <section className="panel graph-panel">
            <div className="panel-head">
              <h2>Direnç</h2>
              <span className="graph-meta num">
                {def.edit === "range" && `aralık ${cfg.params.start + 1}–${cfg.params.end + 1}`}
                {def.edit === "start" && `başlangıç ${cfg.params.start + 1}`}
                <span className="faint"> · basınç {raw}</span>
              </span>
            </div>
            <ZoneEditor config={cfg} onChange={setCfg} pull={raw / 255} />
            <p className="hint">{hint(cfg)}</p>
            {(def.params.some((f) => f.key !== "start" && f.key !== "end") || cfg.mode === "zonesVibration") && <ParamSliders config={cfg} onChange={setCfg} />}
          </section>

          <section className="panel">
            <div className="panel-head">
              <h2>Analog tepki</h2>
              <span className="faint small">{side === "left" ? "L2" : "R2"} basıncı nasıl okunur</span>
            </div>
            <ResponseEditor value={draft.triggerResponse[side]} onChange={setResp} raw={raw} />
          </section>
        </div>

        <aside className="presets">
          <h2>Hazır ayarlar</h2>
          <ul>
            {presets.map((pr) => {
              const current = deepEqual(pr.config, cfg);
              return (
                <li key={pr.name}>
                  <button className={current ? "on" : ""} onClick={() => setCfg(JSON.parse(JSON.stringify(pr.config)))}>
                    <span>{pr.name}</span>
                    <span className="faint">{modeDef(pr.config.mode).label}</span>
                  </button>
                </li>
              );
            })}
          </ul>
          <p className="hint">Seçilen ayar {side === "left" ? "L2" : "R2"} editörünü doldurur; kaydetmeden profile yazılmaz.</p>
          {isZoneMode(cfg.mode) && (
            <button className="button ghost small-btn" onClick={() => setCfg({ ...cfg, zones: Array(10).fill(0) })}>
              Bölgeleri sıfırla
            </button>
          )}
        </aside>
      </div>
    </div>
  );
}
