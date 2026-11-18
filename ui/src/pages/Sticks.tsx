import { useEffect, useRef } from "react";
import { CurvePlot } from "../CurvePlot";
import { send, type Input, type Profile, type StickConfig } from "../host";
import { clamp, deepEqual, processStick, rawAxis } from "../model";
import { useApp, useInput, type Notice } from "../state";
import { Check, DraftBar, PageHead, Section, Segmented, Slider, fixed, pct } from "../ui";

type Side = "left" | "right";
const R = 80;

function StickView({ cfg, raw, processed, trail }: { cfg: StickConfig; raw: [number, number]; processed: [number, number]; trail: [number, number][] }) {
  const inner = cfg.innerDeadzone * R;
  const outer = (1 - cfg.outerDeadzone) * R;
  return (
    <svg viewBox="-92 -92 184 184" className="stick-view" role="img" aria-label="Analog konumu">
      <circle r={R} className="rim" />
      <line x1={-R} x2={R} className="axis" />
      <line y1={-R} y2={R} className="axis" />
      {cfg.shape === "round" ? (
        <circle r={inner} className="dz" />
      ) : (
        <path d={`M ${-R} ${-inner} H ${R} V ${inner} H ${-R} Z M ${-inner} ${-R} V ${R} H ${inner} V ${-R} Z`} className="dz" />
      )}
      <circle r={outer} className="outer-dz" />
      {trail.length > 1 && <polyline points={trail.map(([x, y]) => `${x * R},${y * R}`).join(" ")} className="trail" />}
      <circle cx={raw[0] * R} cy={raw[1] * R} r="4.5" className="raw-dot" />
      <circle cx={processed[0] * R} cy={processed[1] * R} r="4.5" className="proc-dot" />
    </svg>
  );
}

function StickPanel({ side, cfg, input, dirty, onChange }: { side: Side; cfg: StickConfig; input: Input | null; dirty: boolean; onChange: (c: StickConfig) => void }) {
  const trail = useRef<[number, number][]>([]);
  const pos = input ? (side === "left" ? input.left : input.right) : ([128, 128] as [number, number]);
  const raw: [number, number] = [rawAxis(pos[0]), rawAxis(pos[1])];
  // After a save the host's numbers are the truth; while editing, preview the draft.
  const hostProcessed = side === "left" ? input?.leftProcessed : input?.rightProcessed;
  const processed = !dirty && hostProcessed ? hostProcessed : processStick(raw[0], raw[1], cfg);

  // last second of raw positions
  useEffect(() => {
    if (!input) return;
    const p = side === "left" ? input.left : input.right;
    trail.current.push([rawAxis(p[0]), rawAxis(p[1])]);
    if (trail.current.length > 30) trail.current.shift();
  }, [input, side]);

  const m = Math.hypot(raw[0], raw[1]);
  const span = 1 - cfg.innerDeadzone - cfg.outerDeadzone;
  const live = m <= cfg.innerDeadzone || span <= 0 ? 0 : clamp((m - cfg.innerDeadzone) / span, 0, 1);
  const set = (patch: Partial<StickConfig>) => onChange({ ...cfg, ...patch });

  return (
    <Section
      title={side === "left" ? "Sol analog" : "Sağ analog"}
      aside={
        <span className="num faint small">
          {fixed(2)(processed[0])} · {fixed(2)(processed[1])}
          {dirty && " · taslak"}
        </span>
      }
      className="stick-panel"
    >
      <div className="stick-visuals">
        <StickView cfg={cfg} raw={raw} processed={processed} trail={trail.current} />
        <CurvePlot shape={cfg} onChange={set} live={live} />
      </div>
      <div className="stick-controls">
        <Slider label="İç ölü bölge" min={0} max={0.5} step={0.01} value={cfg.innerDeadzone} format={pct} onChange={(innerDeadzone) => set({ innerDeadzone })} />
        <Slider label="Dış ölü bölge" min={0} max={0.5} step={0.01} value={cfg.outerDeadzone} format={pct} onChange={(outerDeadzone) => set({ outerDeadzone })} />
        <Slider label="Anti ölü bölge" min={0} max={0.5} step={0.01} value={cfg.antiDeadzone} format={pct} onChange={(antiDeadzone) => set({ antiDeadzone })} />
        <div className="row-field">
          <span className="label">Ölü bölge şekli</span>
          <Segmented
            label="Ölü bölge şekli"
            value={cfg.shape}
            onChange={(shape) => set({ shape })}
            options={[
              { id: "round", label: "Yuvarlak" },
              { id: "square", label: "Kare" },
            ]}
          />
        </div>
        <div className="row-field">
          <span className="label">Ters çevir</span>
          <Check label="X" checked={cfg.invertX} onChange={(invertX) => set({ invertX })} />
          <Check label="Y" checked={cfg.invertY} onChange={(invertY) => set({ invertY })} />
        </div>
      </div>
    </Section>
  );
}

function GyroPanel({ draft, input, calibration, onChange }: { draft: Profile; input: Input | null; calibration?: Notice; onChange: (g: Profile["gyro"]) => void }) {
  const g = draft.gyro;
  const running = calibration && calibration.progress !== undefined && calibration.progress < 1;
  const f = (n: number) => (n >= 0 ? "+" : "−") + Math.abs(n).toFixed(1).replace(".", ",");
  return (
    <Section title="Gyro" className="gyro-panel">
      <div className="gyro-grid">
        <div className="gyro-controls">
          <Slider label="Hassasiyet" min={0.1} max={4} step={0.05} value={g.sensitivity} format={(v) => `${fixed(2)(v)}×`} onChange={(sensitivity) => onChange({ ...g, sensitivity })} />
          <Slider label="Yumuşatma" min={0} max={1} step={0.01} value={g.smoothing} format={pct} onChange={(smoothing) => onChange({ ...g, smoothing })} />
          <div className="row-field">
            <span className="label">Ters çevir</span>
            <Check label="X" checked={g.invertX} onChange={(invertX) => onChange({ ...g, invertX })} />
            <Check label="Y" checked={g.invertY} onChange={(invertY) => onChange({ ...g, invertY })} />
          </div>
        </div>
        <div className="gyro-cal">
          <dl className="kv compact">
            <dt>Gyro °/s</dt>
            <dd className="num">{input ? input.gyro.map(f).join("  ") : "—"}</dd>
          </dl>
          <div className="cal-row">
            <button className="button" disabled={!!running || !input} onClick={() => send({ type: "gyro.calibrate" })}>
              Kalibre et
            </button>
            {calibration ? (
              <span className="cal-status">
                {calibration.progress !== undefined && (
                  <span className="progress">
                    <span style={{ width: `${calibration.progress * 100}%` }} />
                  </span>
                )}
                <span className="small">{calibration.message}</span>
              </span>
            ) : (
              <span className="hint">Kontrolcüyü düz bir yüzeye koy, 3 saniye dokunma.</span>
            )}
          </div>
        </div>
      </div>
    </Section>
  );
}

export function Sticks() {
  const { draft, draftBase, edit, dirty, notices } = useApp();
  const input = useInput();
  if (!draft) return <div className="page"><PageHead title="Analoglar" /></div>;
  const calibration = [...notices].reverse().find((n) => n.kind === "gyroCalibration");

  return (
    <div className="page sticks-page">
      <PageHead title="Analoglar">
        <DraftBar />
      </PageHead>
      <div className="sticks-grid">
        {(["left", "right"] as Side[]).map((s) => (
          <StickPanel key={s} side={s} cfg={draft.sticks[s]} input={input} dirty={dirty && !deepEqual(draft.sticks[s], draftBase?.sticks[s])} onChange={(c) => edit((p) => ({ ...p, sticks: { ...p.sticks, [s]: c } }))} />
        ))}
      </div>
      <GyroPanel draft={draft} input={input} calibration={calibration} onChange={(gyro) => edit((p) => ({ ...p, gyro }))} />
    </div>
  );
}
