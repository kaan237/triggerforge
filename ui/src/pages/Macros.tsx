import { useRef, useState, type KeyboardEvent, type MouseEvent, type WheelEvent } from "react";
import type { Direction, KeyName, Macro, MacroStep, MouseButtonName, Profile } from "../host";
import { codeToKey, keyLabel, modifierKeys } from "../keys";
import { KeyField } from "../MappingEditors";
import { directions, mouseButtons, mouseLabel, directionLabel, usePause } from "../mapping";
import { clone, defaultMacro, newId } from "../model";
import { useApp } from "../state";
import { Section, Segmented } from "../ui";

const MAX_STEPS = 500;
const MAX_MACROS = 50;

type Op = MacroStep["op"];
const ops: { id: Op; label: string }[] = [
  { id: "tap", label: "Tuşa bas-bırak" },
  { id: "down", label: "Tuşu indir" },
  { id: "up", label: "Tuşu bırak" },
  { id: "click", label: "Fare tıkla" },
  { id: "mouseDown", label: "Fare indir" },
  { id: "mouseUp", label: "Fare bırak" },
  { id: "wheel", label: "Tekerlek" },
  { id: "delay", label: "Bekle" },
];
const keyOps = new Set<Op>(["tap", "down", "up"]);
const mouseOps = new Set<Op>(["click", "mouseDown", "mouseUp"]);

function withOp(s: MacroStep, op: Op): MacroStep {
  if (keyOps.has(op)) return { op: op as "tap", key: "key" in s ? s.key : "a" };
  if (mouseOps.has(op)) return { op: op as "click", button: "button" in s ? s.button : "left" };
  if (op === "wheel") return { op, direction: "down", amount: 1 };
  return { op: "delay", ms: "ms" in s ? s.ms : 50 };
}

export function stepSummary(s: MacroStep): string {
  switch (s.op) {
    case "tap":
      return keyLabel(s.key);
    case "down":
      return `${keyLabel(s.key)} indir`;
    case "up":
      return `${keyLabel(s.key)} bırak`;
    case "click":
      return mouseLabel(s.button);
    case "mouseDown":
      return `${mouseLabel(s.button)} indir`;
    case "mouseUp":
      return `${mouseLabel(s.button)} bırak`;
    case "wheel":
      return `Tekerlek ${directionLabel(s.direction).toLocaleLowerCase("tr")} ×${s.amount}`;
    case "delay":
      return `${s.ms} ms`;
  }
}

function StepRow({ step, index, count, selected, onSelect, onChange, onMove, onDelete }: {
  step: MacroStep;
  index: number;
  count: number;
  selected: boolean;
  onSelect: () => void;
  onChange: (s: MacroStep) => void;
  onMove: (d: -1 | 1) => void;
  onDelete: () => void;
}) {
  return (
    <li className={selected ? "step selected" : "step"}>
      <button className="step-no num" onClick={onSelect} aria-label={`Adım ${index + 1} seç`}>
        {index + 1}
      </button>
      <select value={step.op} aria-label="Adım türü" onChange={(e) => onChange(withOp(step, e.target.value as Op))}>
        {ops.map((o) => (
          <option key={o.id} value={o.id}>
            {o.label}
          </option>
        ))}
      </select>
      <div className="step-fields">
        {"key" in step && <KeyField compact max={1} value={[step.key]} onChange={(k) => k[0] && onChange({ ...step, key: k[0] })} />}
        {"button" in step && (
          <select value={step.button} aria-label="Fare tuşu" onChange={(e) => onChange({ ...step, button: e.target.value as MouseButtonName })}>
            {mouseButtons.map((b) => (
              <option key={b.id} value={b.id}>
                {b.label}
              </option>
            ))}
          </select>
        )}
        {step.op === "wheel" && (
          <>
            <select value={step.direction} aria-label="Yön" onChange={(e) => onChange({ ...step, direction: e.target.value as Direction })}>
              {directions.map((d) => (
                <option key={d.id} value={d.id}>
                  {d.label}
                </option>
              ))}
            </select>
            <label className="number-field">
              <input type="number" min={1} max={10} value={step.amount} onChange={(e) => onChange({ ...step, amount: Math.min(10, Math.max(1, Math.round(Number(e.target.value) || 1))) })} />
              <span className="faint">çentik</span>
            </label>
          </>
        )}
        {step.op === "delay" && (
          <label className="number-field">
            <input type="number" min={0} max={60000} step={10} value={step.ms} onChange={(e) => onChange({ ...step, ms: Math.min(60000, Math.max(0, Math.round(Number(e.target.value) || 0))) })} />
            <span className="faint">ms</span>
          </label>
        )}
      </div>
      <div className="step-tools">
        <button className="icon-btn" disabled={index === 0} aria-label="Yukarı taşı" onClick={() => onMove(-1)}>
          <span className="chev up" />
        </button>
        <button className="icon-btn" disabled={index === count - 1} aria-label="Aşağı taşı" onClick={() => onMove(1)}>
          <span className="chev down" />
        </button>
        <button className="icon-btn" aria-label="Adımı sil" onClick={onDelete}>
          <svg viewBox="0 0 10 10" width="10" height="10" aria-hidden>
            <path d="M 1.5 1.5 L 8.5 8.5 M 8.5 1.5 L 1.5 8.5" />
          </svg>
        </button>
      </div>
    </li>
  );
}

const mouseOf = (b: number): MouseButtonName | null => (["left", "middle", "right", "x1", "x2"] as const)[b] ?? null;

// Captures keys and mouse buttons inside the box with their timings.
function Recorder({ hasSteps, onDone }: { hasSteps: boolean; onDone: (steps: MacroStep[], mode: "replace" | "append") => void }) {
  const box = useRef<HTMLDivElement>(null);
  const [recording, setRecording] = useState(false);
  const [ask, setAsk] = useState(false);
  const [count, setCount] = useState(0);
  const mode = useRef<"replace" | "append">("replace");
  const live = useRef(false);
  const steps = useRef<MacroStep[]>([]);
  const last = useRef<number | null>(null);
  const keys = useRef(new Set<KeyName>());
  const buttons = useRef(new Set<MouseButtonName>());
  const synthetic = useRef(new Set<KeyName>());
  usePause(recording);

  const start = (m: "replace" | "append") => {
    mode.current = m;
    steps.current = [];
    last.current = null;
    keys.current.clear();
    buttons.current.clear();
    synthetic.current.clear();
    setCount(0);
    setAsk(false);
    live.current = true;
    setRecording(true);
    window.setTimeout(() => box.current?.focus(), 0);
  };

  const stop = () => {
    if (!live.current) return;
    live.current = false;
    setRecording(false);
    // Leave nothing held at the end of the macro.
    const out = [...steps.current];
    keys.current.forEach((key) => out.push({ op: "up", key }));
    buttons.current.forEach((button) => out.push({ op: "mouseUp", button }));
    if (out.length > 0) onDone(out.slice(0, MAX_STEPS), mode.current);
  };

  const push = (s: MacroStep) => {
    if (steps.current.length >= MAX_STEPS - 1) return;
    const now = performance.now();
    if (last.current !== null) {
      const gap = now - last.current;
      if (gap >= 10) steps.current.push({ op: "delay", ms: Math.min(60000, Math.round(gap / 10) * 10) });
    }
    last.current = now;
    steps.current.push(s);
    setCount(steps.current.length);
  };

  const key = (e: KeyboardEvent, down: boolean) => {
    e.preventDefault();
    e.stopPropagation();
    if (e.repeat) return;
    const k = codeToKey(e.code);
    if (!k) return;
    // A modifier seen only as a flag (its own keydown never reached the page) is wrapped around the key.
    const flagged = (["leftCtrl", "leftShift", "leftAlt", "leftWin"] as const).filter(
      (m, i) => [e.ctrlKey, e.shiftKey, e.altKey, e.metaKey][i] && !keys.current.has(m) && !keys.current.has(m.replace("left", "right")) && m !== k,
    );
    if (down) {
      for (const m of modifierKeys.has(k) ? [] : flagged) {
        synthetic.current.add(m);
        keys.current.add(m);
        push({ op: "down", key: m });
      }
      keys.current.add(k);
    } else if (!keys.current.delete(k)) return;
    push({ op: down ? "down" : "up", key: k });
    if (!down && !modifierKeys.has(k)) {
      for (const m of synthetic.current) {
        keys.current.delete(m);
        push({ op: "up", key: m });
      }
      synthetic.current.clear();
    }
  };

  const mouse = (e: MouseEvent, down: boolean) => {
    e.preventDefault();
    const b = mouseOf(e.button);
    if (!b) return;
    if (down) buttons.current.add(b);
    else if (!buttons.current.delete(b)) return;
    push({ op: down ? "mouseDown" : "mouseUp", button: b });
  };

  const wheel = (e: WheelEvent) => {
    if (e.deltaY === 0 && e.deltaX === 0) return;
    const direction: Direction = Math.abs(e.deltaY) >= Math.abs(e.deltaX) ? (e.deltaY > 0 ? "down" : "up") : e.deltaX > 0 ? "right" : "left";
    push({ op: "wheel", direction, amount: 1 });
  };

  return (
    <div className="recorder">
      <div className="recorder-bar">
        {recording ? (
          <button className="button danger" onMouseDown={(e) => e.preventDefault()} onClick={stop}>
            Durdur
          </button>
        ) : ask ? (
          <>
            <span className="small warn">Mevcut adımlar ne olsun?</span>
            <button className="button ghost" onClick={() => setAsk(false)}>
              Vazgeç
            </button>
            <button className="button" onClick={() => start("append")}>
              Sona ekle
            </button>
            <button className="button danger" onClick={() => start("replace")}>
              Değiştir
            </button>
          </>
        ) : (
          <button className="button" onClick={() => (hasSteps ? setAsk(true) : start("replace"))}>
            <span className="rec-dot" aria-hidden />
            Kaydet
          </button>
        )}
        <span className="hint">Kutunun içinde tuşlara bas ve tıkla; aradaki süreler bekleme olur.</span>
      </div>
      {recording && (
        <div
          ref={box}
          tabIndex={0}
          className="record-box"
          onKeyDown={(e) => key(e, true)}
          onKeyUp={(e) => key(e, false)}
          onMouseDown={(e) => mouse(e, true)}
          onMouseUp={(e) => mouse(e, false)}
          onAuxClick={(e) => e.preventDefault()}
          onContextMenu={(e) => e.preventDefault()}
          onWheel={wheel}
          onBlur={stop}
        >
          <span className="rec-dot on" aria-hidden />
          <span>Kaydediliyor</span>
          <span className="faint num">{count} olay</span>
          <span className="faint small">Kutudan çıkınca kayıt durur.</span>
        </div>
      )}
    </div>
  );
}

function MacroEditor({ macro, onChange, onDuplicate, onDelete, canDuplicate }: { macro: Macro; onChange: (m: Macro) => void; onDuplicate: () => void; onDelete: () => void; canDuplicate: boolean }) {
  const [sel, setSel] = useState<number | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const steps = macro.steps;
  const setSteps = (next: MacroStep[]) => onChange({ ...macro, steps: next.slice(0, MAX_STEPS) });
  const insert = (s: MacroStep) => {
    const at = sel !== null && sel < steps.length ? sel + 1 : steps.length;
    setSteps([...steps.slice(0, at), s, ...steps.slice(at)]);
    setSel(at);
  };
  const move = (i: number, d: -1 | 1) => {
    const j = i + d;
    if (j < 0 || j >= steps.length) return;
    const next = [...steps];
    [next[i], next[j]] = [next[j], next[i]];
    setSteps(next);
    if (sel === i) setSel(j);
  };
  const total = steps.reduce((t, s) => t + (s.op === "delay" ? s.ms : s.op === "tap" || s.op === "click" ? 30 : 0), 0);
  const full = steps.length >= MAX_STEPS;

  return (
    <div className="macro-detail">
      <div className="detail-head">
        <input className="name-input" type="text" value={macro.name} maxLength={64} aria-label="Makro adı" onChange={(e) => onChange({ ...macro, name: e.target.value })} />
        <span className="faint small num">
          {steps.length} adım · {total >= 1000 ? `${(total / 1000).toFixed(1).replace(".", ",")} sn` : `${total} ms`}
        </span>
      </div>
      <div className="detail-actions">
        <Segmented
          label="Tekrar"
          value={macro.repeat}
          onChange={(repeat) => onChange({ ...macro, repeat })}
          options={[
            { id: "once", label: "Bir kez" },
            { id: "whileHeld", label: "Basılıyken tekrarla" },
            { id: "toggle", label: "Aç/kapa" },
          ]}
        />
        <span className="spacer" />
        <button className="button" disabled={!canDuplicate} onClick={onDuplicate}>
          Çoğalt
        </button>
        {confirmDelete ? (
          <>
            <span className="small warn">Silinsin mi? Kullanan atamalar boşalır.</span>
            <button className="button ghost" onClick={() => setConfirmDelete(false)}>
              Vazgeç
            </button>
            <button className="button danger" onClick={onDelete}>
              Sil
            </button>
          </>
        ) : (
          <button className="button ghost" onClick={() => setConfirmDelete(true)}>
            Sil
          </button>
        )}
      </div>

      <section className="detail-section">
        <h2>Kayıt</h2>
        <Recorder hasSteps={steps.length > 0} onDone={(rec, mode) => setSteps(mode === "append" ? [...steps, ...rec] : rec)} />
      </section>

      <section className="detail-section">
        <div className="panel-head">
          <h2>Adımlar</h2>
          {steps.length > 0 && (
            <button className="button ghost" onClick={() => setSteps([])}>
              Hepsini sil
            </button>
          )}
        </div>
        {steps.length > 0 ? (
          <ol className="step-list">
            {steps.map((s, i) => (
              <StepRow
                key={i}
                step={s}
                index={i}
                count={steps.length}
                selected={sel === i}
                onSelect={() => setSel(sel === i ? null : i)}
                onChange={(next) => setSteps(steps.map((x, j) => (j === i ? next : x)))}
                onMove={(d) => move(i, d)}
                onDelete={() => {
                  setSteps(steps.filter((_, j) => j !== i));
                  if (sel !== null && sel >= i) setSel(sel > 0 ? sel - 1 : null);
                }}
              />
            ))}
          </ol>
        ) : (
          <p className="hint">Adım yok. Kaydet ya da aşağıdan ekle.</p>
        )}
        <div className="button-row">
          <span className="faint small">{sel !== null ? `${sel + 1}. adımdan sonra ekle:` : "Sona ekle:"}</span>
          <button className="button" disabled={full} onClick={() => insert({ op: "tap", key: "a" })}>
            Tuş
          </button>
          <button className="button" disabled={full} onClick={() => insert({ op: "click", button: "left" })}>
            Fare
          </button>
          <button className="button" disabled={full} onClick={() => insert({ op: "wheel", direction: "down", amount: 1 })}>
            Tekerlek
          </button>
          <button className="button" disabled={full} onClick={() => insert({ op: "delay", ms: 50 })}>
            Bekleme
          </button>
        </div>
        <p className="hint step-note">
          {macro.repeat === "once" && "Sonuna kadar bir kez çalışır; çalışırken tekrar basmak bir şey yapmaz."}
          {macro.repeat === "whileHeld" && "Tuş basılı kaldıkça baştan çalışır; bırakınca o adımdan sonra durur."}
          {macro.repeat === "toggle" && "İlk basış döngüyü başlatır, ikinci basış durdurur."}
        </p>
      </section>
    </div>
  );
}

export function Macros({ draft }: { draft: Profile }) {
  const { edit } = useApp();
  const [selectedId, setSelectedId] = useState<string | null>(draft.macros[0]?.id ?? null);
  const macros = draft.macros;
  const selected = macros.find((m) => m.id === selectedId) ?? macros[0];
  const setMacros = (fn: (list: Macro[]) => Macro[]) => edit((p) => ({ ...p, macros: fn(p.macros) }));
  const full = macros.length >= MAX_MACROS;

  const add = () => {
    let n = macros.length + 1;
    while (macros.some((m) => m.name === `Makro ${n}`)) n++;
    const m = defaultMacro(`Makro ${n}`);
    setMacros((list) => [...list, m]);
    setSelectedId(m.id);
  };

  return (
    <div className="profiles-grid macros-grid">
      <div>
        {macros.length > 0 && (
          <ul className="profile-list">
            {macros.map((m) => (
              <li key={m.id}>
                <button className={m.id === selected?.id ? "selected" : ""} onClick={() => setSelectedId(m.id)}>
                  <span className="pl-name">{m.name || "Adsız"}</span>
                  <span className="pl-games faint">
                    {m.steps.length} adım · {{ once: "bir kez", whileHeld: "basılıyken", toggle: "aç/kapa" }[m.repeat]}
                  </span>
                </button>
              </li>
            ))}
          </ul>
        )}
        <div className="button-row">
          <button className="button" disabled={full} onClick={add}>
            Yeni makro
          </button>
          {full && <span className="hint">En çok {MAX_MACROS} makro.</span>}
        </div>
      </div>
      {selected ? (
        <MacroEditor
          key={selected.id}
          macro={selected}
          canDuplicate={!full}
          onChange={(m) => setMacros((list) => list.map((x) => (x.id === m.id ? m : x)))}
          onDuplicate={() => {
            const copy = { ...clone(selected), id: newId(), name: `${selected.name} (kopya)`.slice(0, 64) };
            setMacros((list) => [...list, copy]);
            setSelectedId(copy.id);
          }}
          onDelete={() => {
            setMacros((list) => list.filter((x) => x.id !== selected.id));
            setSelectedId(null);
          }}
        />
      ) : (
        <Section title="Makrolar">
          <p className="hint">Makro, sırayla basılan tuşlar ve beklemelerdir. Bir tuşa “Makro” eylemiyle atanır.</p>
        </Section>
      )}
    </div>
  );
}
