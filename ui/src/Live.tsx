import type { Input } from "./host";

const face = [
  { id: "Triangle", label: "△", x: 0, y: -1 },
  { id: "Circle", label: "○", x: 1, y: 0 },
  { id: "Cross", label: "✕", x: 0, y: 1 },
  { id: "Square", label: "□", x: -1, y: 0 },
];

const dpad = [
  { id: "Up", x: 0, y: -1 },
  { id: "Right", x: 1, y: 0 },
  { id: "Down", x: 0, y: 1 },
  { id: "Left", x: -1, y: 0 },
];

const strip = ["L1", "R1", "Create", "Options", "PS", "Mic", "Touchpad", "L3", "R3"];

function Stick({ pos, pressed, label }: { pos: [number, number]; pressed: boolean; label: string }) {
  const x = ((pos[0] - 127.5) / 127.5) * 34;
  const y = ((pos[1] - 127.5) / 127.5) * 34;
  return (
    <figure className="stick">
      <svg viewBox="-44 -44 88 88" width="88" height="88" aria-hidden>
        <circle r="40" className="ring" />
        <line x1="-40" x2="40" className="axis" />
        <line y1="-40" y2="40" className="axis" />
        <circle cx={x} cy={y} r="7" className={pressed ? "knob on" : "knob"} />
      </svg>
      <figcaption>
        <span>{label}</span>
        <span className="num">
          {pos[0]} · {pos[1]}
        </span>
      </figcaption>
    </figure>
  );
}

function Trigger({ value, label }: { value: number; label: string }) {
  return (
    <div className="trigger-meter">
      <span>{label}</span>
      <div className="bar">
        <div className="fill" style={{ height: `${(value / 255) * 100}%` }} />
      </div>
      <span className="num">{value}</span>
    </div>
  );
}

export function Live({ input }: { input: Input | null }) {
  if (!input) {
    return <div className="panel live muted-text">Veri bekleniyor…</div>;
  }

  const down = new Set(input.buttons);
  const f = (n: number) => (n >= 0 ? " " : "") + n.toFixed(1);

  return (
    <div className="panel live">
      <div className="live-row">
        <Trigger value={input.l2} label="L2" />
        <Stick pos={input.left} pressed={down.has("L3")} label="Sol" />

        <svg viewBox="-30 -30 60 60" width="64" height="64" className="cluster" aria-label="Yön tuşları">
          {dpad.map((d) => (
            <rect key={d.id} x={d.x * 18 - 7} y={d.y * 18 - 7} width="14" height="14" rx="2" className={down.has(d.id) ? "on" : ""} />
          ))}
        </svg>

        <svg viewBox="-30 -30 60 60" width="64" height="64" className="cluster" aria-label="Ön tuşlar">
          {face.map((b) => (
            <g key={b.id} transform={`translate(${b.x * 18} ${b.y * 18})`}>
              <circle r="8" className={down.has(b.id) ? "on" : ""} />
              <text dy="3.5">{b.label}</text>
            </g>
          ))}
        </svg>

        <Stick pos={input.right} pressed={down.has("R3")} label="Sağ" />
        <Trigger value={input.r2} label="R2" />
      </div>

      <div className="chips">
        {strip.map((b) => (
          <span key={b} className={down.has(b) ? "chip on" : "chip"}>
            {b}
          </span>
        ))}
      </div>

      <div className="live-row bottom">
        <div className="touchpad" aria-label="Dokunmatik yüzey">
          {input.touch.map((t) => (
            <span key={t.id} className="finger" style={{ left: `${(t.x / 1920) * 100}%`, top: `${(t.y / 1080) * 100}%` }} />
          ))}
        </div>

        <dl className="readout">
          <dt>gyro °/s</dt>
          <dd className="num">
            {f(input.gyro[0])} {f(input.gyro[1])} {f(input.gyro[2])}
          </dd>
          <dt>ivme g</dt>
          <dd className="num">
            {f(input.accel[0])} {f(input.accel[1])} {f(input.accel[2])}
          </dd>
          <dt>rapor</dt>
          <dd className="num">
            {input.reports.toLocaleString("tr-TR")}
            {input.badCrc > 0 && <span className="faint"> · {input.badCrc} bozuk</span>}
          </dd>
        </dl>
      </div>
    </div>
  );
}
