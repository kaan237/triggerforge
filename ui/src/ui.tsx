import type { ReactNode } from "react";
import { useApp } from "./state";

export function Slider({
  label,
  value,
  min,
  max,
  step = 1,
  onChange,
  format,
}: {
  label: string;
  value: number;
  min: number;
  max: number;
  step?: number;
  onChange: (v: number) => void;
  format?: (v: number) => string;
}) {
  return (
    <label className="slider">
      <span>{label}</span>
      <input type="range" min={min} max={max} step={step} value={value} onChange={(e) => onChange(Number(e.target.value))} />
      <span className="num">{format ? format(value) : value}</span>
    </label>
  );
}

export function Segmented<T extends string | number>({
  value,
  options,
  onChange,
  label,
}: {
  value: T;
  options: { id: T; label: string }[];
  onChange: (v: T) => void;
  label?: string;
}) {
  return (
    <div className="segmented" role="radiogroup" aria-label={label}>
      {options.map((o) => (
        <button key={String(o.id)} role="radio" aria-checked={o.id === value} className={o.id === value ? "on" : ""} onClick={() => onChange(o.id)}>
          {o.label}
        </button>
      ))}
    </div>
  );
}

export function Toggle({ checked, onChange, label, hint }: { checked: boolean; onChange: (v: boolean) => void; label: string; hint?: string }) {
  return (
    <label className="toggle">
      <span className="toggle-text">
        <span>{label}</span>
        {hint && <span className="hint">{hint}</span>}
      </span>
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      <span className="switch" aria-hidden />
    </label>
  );
}

export function Check({ checked, onChange, label, disabled }: { checked: boolean; onChange: (v: boolean) => void; label: string; disabled?: boolean }) {
  return (
    <label className={disabled ? "check disabled" : "check"}>
      <input type="checkbox" checked={checked} disabled={disabled} onChange={(e) => onChange(e.target.checked)} />
      <span className="box" aria-hidden />
      <span>{label}</span>
    </label>
  );
}

export function PageHead({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <header className="page-head">
      <h1>{title}</h1>
      <div className="page-head-side">{children}</div>
    </header>
  );
}

// Which profile the page edits, whether it is saved, and the save/revert actions.
export function DraftBar() {
  const { draft, dirty, saveDraft, revertDraft, activeId } = useApp();
  if (!draft) return null;
  return (
    <div className="draft-bar">
      <span className="draft-name">
        <span className="faint">Profil</span> {draft.name}
        {draft.id !== activeId && <span className="tag">etkin değil</span>}
      </span>
      {dirty ? (
        <>
          <span className="unsaved">Kaydedilmedi</span>
          <button className="button ghost" onClick={revertDraft}>
            Geri al
          </button>
          <button className="button primary" onClick={saveDraft}>
            Kaydet
          </button>
        </>
      ) : (
        <span className="faint saved">Kaydedildi</span>
      )}
    </div>
  );
}

export function Section({ title, aside, children, className }: { title: string; aside?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <section className={`panel ${className ?? ""}`}>
      <div className="panel-head">
        <h2>{title}</h2>
        {aside}
      </div>
      {children}
    </section>
  );
}

export const pct = (v: number) => `${Math.round(v * 100)}%`;
export const fixed = (d: number) => (v: number) => v.toFixed(d).replace(".", ",");
