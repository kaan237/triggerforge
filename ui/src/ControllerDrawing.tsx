import type { Input, Output } from "./host";
import { rawAxis } from "./model";

// Top view, viewBox 600×420, symmetric about x = 300.
// Left half outline as cubic segments; the right half is mirrored.
type P = [number, number];
const start: P = [300, 64];
const half: [P, P, P][] = [
  [[266, 64], [230, 64], [196, 64]],
  [[150, 64], [114, 70], [96, 88]],
  [[76, 108], [64, 140], [56, 182]],
  [[44, 240], [30, 312], [36, 352]],
  [[41, 384], [66, 400], [94, 398]],
  [[122, 396], [138, 376], [152, 348]],
  [[168, 316], [186, 290], [216, 282]],
  [[246, 276], [272, 276], [300, 276]],
];

const mx = ([x, y]: P): string => `${600 - x} ${y}`;
const pt = ([x, y]: P): string => `${x} ${y}`;

const outline = (() => {
  let d = `M ${pt(start)}`;
  for (const [c1, c2, e] of half) d += ` C ${pt(c1)} ${pt(c2)} ${pt(e)}`;
  for (let i = half.length - 1; i >= 0; i--) {
    const [c1, c2] = half[i];
    const to = i > 0 ? half[i - 1][2] : start;
    d += ` C ${mx(c2)} ${mx(c1)} ${mx(to)}`;
  }
  return d + " Z";
})();

const pad = { x: 232, y: 70, w: 136, h: 112 };
const touchpad = "M 240 70 H 360 Q 368 70 368 78 V 160 Q 368 175 353 179 Q 300 190 247 179 Q 232 175 232 160 V 78 Q 232 70 240 70 Z";
const lightLeft = "M 225 78 V 160 Q 225 180 243 187";
const lightRight = "M 375 78 V 160 Q 375 180 357 187";

const dpadCenter: P = [152, 150];
const faceCenter: P = [448, 150];
const sticks = { left: [228, 232] as P, right: [372, 232] as P };

const dpadArm = (cx: number, cy: number) => `M ${cx - 8} ${cy - 11} V ${cy - 28} Q ${cx - 8} ${cy - 30} ${cx - 6} ${cy - 30} H ${cx + 6} Q ${cx + 8} ${cy - 30} ${cx + 8} ${cy - 28} V ${cy - 11} L ${cx} ${cy - 4} Z`;
const dpad = [
  { id: "Up", rot: 0 },
  { id: "Right", rot: 90 },
  { id: "Down", rot: 180 },
  { id: "Left", rot: 270 },
];

const face = [
  { id: "Triangle", dx: 0, dy: -26 },
  { id: "Circle", dx: 26, dy: 0 },
  { id: "Cross", dx: 0, dy: 26 },
  { id: "Square", dx: -26, dy: 0 },
];

function Glyph({ id }: { id: string }) {
  switch (id) {
    case "Triangle":
      return <path d="M 0 -5 L 5 3.5 H -5 Z" />;
    case "Circle":
      return <circle r="4.5" />;
    case "Cross":
      return <path d="M -4 -4 L 4 4 M 4 -4 L -4 4" />;
    default:
      return <rect x="-4" y="-4" width="8" height="8" />;
  }
}

type Pick = { cls: (id: string, base?: string) => string; click: (id: string) => (() => void) | undefined };

function Shoulder({ side, pressed, pull, pick }: { side: "L" | "R"; pressed: boolean; pull: number; pick: Pick }) {
  // Drawn for the left; the right side is mirrored with a transform.
  const h = 26 * pull;
  const trigger = side === "L" ? "L2" : "R2";
  const bumper = side === "L" ? "L1" : "R1";
  return (
    <g transform={side === "R" ? "translate(600 0) scale(-1 1)" : undefined}>
      <rect x="122" y="16" width="74" height="26" rx="6" className={pick.cls(trigger)} onClick={pick.click(trigger)} />
      {h > 0.5 && <rect x="122" y={42 - h} width="74" height={h} rx={Math.min(6, h / 2)} className="fill" pointerEvents="none" />}
      <path d="M 108 62 Q 112 50 126 50 H 198 Q 204 50 204 56 V 62" className={pick.cls(bumper, pressed ? "part on" : "part")} onClick={pick.click(bumper)} />
    </g>
  );
}

function Stick({ at, raw, processed, pressed, id, pick }: { at: P; raw: [number, number]; processed?: [number, number]; pressed: boolean; id: string; pick: Pick }) {
  const [cx, cy] = at;
  const r = 15;
  return (
    <g onClick={pick.click(id)} className={pick.click(id) ? "hit" : undefined}>
      <circle cx={cx} cy={cy} r="27" className={pick.cls(id, "part stick-rim")} />
      <circle cx={cx} cy={cy} r="19" className={pressed ? "part on" : "part"} />
      <line x1={cx - 19} x2={cx + 19} y1={cy} y2={cy} className="axis" />
      <line x1={cx} x2={cx} y1={cy - 19} y2={cy + 19} className="axis" />
      <circle cx={cx + rawAxis(raw[0]) * r} cy={cy + rawAxis(raw[1]) * r} r="3.5" className="raw-dot" />
      {processed && <circle cx={cx + processed[0] * r} cy={cy + processed[1] * r} r="3.5" className="proc-dot" />}
    </g>
  );
}

const centred: [number, number] = [128, 128];

// `onPick` makes the buttons clickable (input names: "Cross", "L1", "PS"); `selected` outlines one.
export function ControllerDrawing({ input, output, selected, onPick }: { input: Input | null; output: Output | null; selected?: string; onPick?: (id: string) => void }) {
  const down = new Set(input?.buttons ?? []);
  const pick: Pick = {
    cls: (id, base = "part") => base + (onPick ? " hit" : "") + (selected === id ? " sel" : ""),
    click: (id) => (onPick ? () => onPick(id) : undefined),
  };
  const on = (id: string) => pick.cls(id, down.has(id) ? "part on" : "part");
  const light = output && output.lightbar.toUpperCase() !== "#000000" ? output.lightbar : null;
  const lightOpacity = output ? [1, 0.7, 0.45][output.brightness] ?? 1 : 1;

  return (
    <svg viewBox="0 0 600 420" className={onPick ? "ds pickable" : "ds"} role="img" aria-label="DualSense canlı görünüm">
      <Shoulder side="L" pressed={down.has("L1")} pull={(input?.l2 ?? 0) / 255} pick={pick} />
      <Shoulder side="R" pressed={down.has("R1")} pull={(input?.r2 ?? 0) / 255} pick={pick} />
      <text x="114" y="34" className="tag-text" textAnchor="end">L2</text>
      <text x="486" y="34" className="tag-text">R2</text>
      <text x="100" y="56" className="tag-text" textAnchor="end">L1</text>
      <text x="500" y="56" className="tag-text">R1</text>

      <path d={outline} className="body" />

      <path d={touchpad} className={pick.cls("Touchpad", down.has("Touchpad") ? "part pad on-soft" : "part pad")} onClick={pick.click("Touchpad")} />
      {input?.touch.map((t) => (
        <circle key={t.id} cx={pad.x + (t.x / 1920) * pad.w} cy={pad.y + (t.y / 1080) * pad.h} r="5" className="finger-dot" pointerEvents="none" />
      ))}

      <path d={lightLeft} className="lightbar" style={light ? { stroke: light, opacity: lightOpacity } : undefined} />
      <path d={lightRight} className="lightbar" style={light ? { stroke: light, opacity: lightOpacity } : undefined} />

      <rect x="206" y="80" width="7" height="17" rx="3.5" className={on("Create")} onClick={pick.click("Create")} />
      <rect x="387" y="80" width="7" height="17" rx="3.5" className={on("Options")} onClick={pick.click("Options")} />

      <circle cx="300" cy="208" r="9" className={on("PS")} onClick={pick.click("PS")} />
      <rect x="290" y="226" width="20" height="6" rx="3" className={on("Mic")} onClick={pick.click("Mic")} style={output && output.micLed > 0 ? { stroke: "#e0806b" } : undefined} />

      <g>
        {dpad.map((d) => (
          <path key={d.id} d={dpadArm(dpadCenter[0], dpadCenter[1])} transform={`rotate(${d.rot} ${dpadCenter[0]} ${dpadCenter[1]})`} className={on(d.id)} onClick={pick.click(d.id)} />
        ))}
      </g>

      {face.map((b) => (
        <g key={b.id} transform={`translate(${faceCenter[0] + b.dx} ${faceCenter[1] + b.dy})`} onClick={pick.click(b.id)}>
          <circle r="11" className={on(b.id)} />
          <g className={down.has(b.id) ? "glyph on" : "glyph"} pointerEvents="none">
            <Glyph id={b.id} />
          </g>
        </g>
      ))}

      <Stick at={sticks.left} raw={input?.left ?? centred} processed={input?.leftProcessed} pressed={down.has("L3")} id="L3" pick={pick} />
      <Stick at={sticks.right} raw={input?.right ?? centred} processed={input?.rightProcessed} pressed={down.has("R3")} id="R3" pick={pick} />

      <g className="player-row">
        {[0, 1, 2, 3, 4].map((i) => (
          <rect key={i} x={281 + i * 8} y="192" width="5" height="2" rx="1" className={output && (output.playerLeds >> i) & 1 ? "led-on" : "led-off"} />
        ))}
      </g>
    </svg>
  );
}
