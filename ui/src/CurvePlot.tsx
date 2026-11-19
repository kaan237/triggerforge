import { L } from "./i18n";
import { useRef, type PointerEvent } from "react";
import type { Curve, CurvePoint } from "./host";
import { applyCurve, clamp, sampleCurve } from "./model";
import { Segmented, Slider, fixed } from "./ui";

type Shape = { curve: Curve; exponent: number; points: CurvePoint[] | null };

const S = 160; // plot size inside the viewBox
const M = 8; // margin

const toX = (v: number) => M + v * S;
const toY = (v: number) => M + (1 - v) * S;

// Response curve with draggable points in custom mode. `live` is the current input
// (0-1, after deadzone, before the curve).
export function CurvePlot({
  shape,
  onChange,
  live,
}: {
  shape: Shape;
  onChange: (s: Partial<Shape>) => void;
  live?: number | null;
}) {
  const svg = useRef<SVGSVGElement>(null);
  const drag = useRef<number | null>(null);

  const path = Array.from({ length: 41 }, (_, i) => {
    const x = i / 40;
    return `${i ? "L" : "M"} ${toX(x).toFixed(1)} ${toY(applyCurve(x, shape.curve, shape.exponent, shape.points)).toFixed(1)}`;
  }).join(" ");

  const local = (e: PointerEvent) => {
    const m = svg.current?.getScreenCTM();
    if (!m) return null;
    const p = new DOMPoint(e.clientX, e.clientY).matrixTransform(m.inverse());
    return { x: clamp((p.x - M) / S, 0, 1), y: clamp(1 - (p.y - M) / S, 0, 1) };
  };

  const move = (e: PointerEvent) => {
    const i = drag.current;
    if (i === null || !shape.points) return;
    const p = local(e);
    if (!p) return;
    const pts = shape.points.map((q) => [...q] as CurvePoint);
    const lo = pts[i - 1][0] + 0.02;
    const hi = pts[i + 1][0] - 0.02;
    pts[i] = [round(clamp(p.x, lo, hi)), round(p.y)];
    onChange({ points: pts });
  };

  const liveY = live != null ? applyCurve(live, shape.curve, shape.exponent, shape.points) : null;

  return (
    <div className="curve">
      <svg
        ref={svg}
        viewBox={`0 0 ${S + M * 2} ${S + M * 2}`}
        className="curve-plot"
        onPointerMove={move}
        onPointerUp={() => (drag.current = null)}
        onPointerCancel={() => (drag.current = null)}
      >
        <rect x={M} y={M} width={S} height={S} className="plot-frame" />
        {[0.25, 0.5, 0.75].map((g) => (
          <g key={g} className="grid">
            <line x1={toX(g)} x2={toX(g)} y1={M} y2={M + S} />
            <line y1={toY(g)} y2={toY(g)} x1={M} x2={M + S} />
          </g>
        ))}
        <line x1={toX(0)} y1={toY(0)} x2={toX(1)} y2={toY(1)} className="diag" />
        <path d={path} className="curve-line" />
        {live != null && liveY != null && (
          <g className="live-mark">
            <line x1={toX(live)} x2={toX(live)} y1={M} y2={M + S} />
            <circle cx={toX(live)} cy={toY(liveY)} r="3.5" />
          </g>
        )}
        {shape.curve === "custom" &&
          shape.points?.map((q, i) => (
            <circle
              key={i}
              cx={toX(q[0])}
              cy={toY(q[1])}
              r={i === 0 || i === 4 ? 3 : 5}
              className={i === 0 || i === 4 ? "handle fixed" : "handle"}
              onPointerDown={(e) => {
                if (i === 0 || i === 4) return;
                (e.currentTarget.ownerSVGElement ?? e.currentTarget).setPointerCapture(e.pointerId);
                drag.current = i;
              }}
            />
          ))}
      </svg>
      <div className="curve-controls">
        <Segmented
          label={L("Curve", "Eğri")}
          value={shape.curve}
          options={[
            { id: "linear", label: L("Linear", "Doğrusal") },
            { id: "exponential", label: L("Exponential", "Üstel") },
            { id: "custom", label: L("Custom", "Özel") },
          ]}
          onChange={(curve) => onChange(curve === "custom" && !shape.points ? { curve, points: sampleCurve(shape.curve, shape.exponent, null) } : { curve })}
        />
        {shape.curve === "exponential" && <Slider label={L("Exponent", "Üs")} min={0.3} max={4} step={0.05} value={shape.exponent} format={fixed(2)} onChange={(exponent) => onChange({ exponent })} />}
        {shape.curve === "custom" && <p className="hint">{L("Drag the three middle points.", "Ortadaki üç noktayı sürükle.")}</p>}
      </div>
    </div>
  );
}

const round = (v: number) => Math.round(v * 100) / 100;
