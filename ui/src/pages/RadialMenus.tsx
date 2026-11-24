import { L } from "../i18n";
import { useState, type KeyboardEvent } from "react";
import type { Profile, RadialMenu, RadialSlice, RadialStick } from "../host";
import { ActionEditor } from "../MappingEditors";
import { defaultRadialMenu, defaultRadialSlice, maxRadialLabelLength, maxRadialMenus, maxRadialNameLength, maxRadialSlices, minRadialSlices, newId, removeRadialMenu } from "../model";
import { radialMenuName } from "../mapping";
import { Section, Segmented } from "../ui";
import type { EditMapping } from "./Mapping";

const stickOptions: { id: RadialStick; label: string }[] = [
  { id: "left", get label() { return L("Left stick", "Sol analog"); } },
  { id: "right", get label() { return L("Right stick", "Sağ analog"); } },
];

const point = (radius: number, angle: number) => `${(radius * Math.sin(angle)).toFixed(2)} ${(-radius * Math.cos(angle)).toFixed(2)}`;

// Slice 0 is centred at 12 o'clock and the rest follow clockwise, as the menu on screen does.
function wedgePath(index: number, count: number, inner: number, outer: number): string {
  const step = (Math.PI * 2) / count;
  const a0 = index * step - step / 2;
  const a1 = a0 + step;
  const large = step > Math.PI ? 1 : 0;
  return `M ${point(outer, a0)} A ${outer} ${outer} 0 ${large} 1 ${point(outer, a1)} L ${point(inner, a1)} A ${inner} ${inner} 0 ${large} 0 ${point(inner, a0)} Z`;
}

const clip = (text: string, max: number) => (text.length > max ? `${text.slice(0, max - 1)}…` : text);

function Preview({ menu, selected, onSelect }: { menu: RadialMenu; selected: number; onSelect: (i: number) => void }) {
  const count = menu.slices.length;
  // Roving tabindex: arrows pick the neighbour and move focus onto it (the wedges are the svg's first children).
  const go = (e: KeyboardEvent, next: number) => {
    e.preventDefault();
    onSelect(next);
    (e.currentTarget.parentElement?.children[next] as SVGGElement | undefined)?.focus();
  };
  const key = (e: KeyboardEvent, i: number) => {
    if (e.key === "Enter" || e.key === " ") {
      e.preventDefault();
      onSelect(i);
    } else if (e.key === "ArrowRight" || e.key === "ArrowDown") {
      go(e, (i + 1) % count);
    } else if (e.key === "ArrowLeft" || e.key === "ArrowUp") {
      go(e, (i + count - 1) % count);
    }
  };
  return (
    <svg className="radial-preview" viewBox="-100 -100 200 200" role="group" aria-label={L(`Preview of ${radialMenuName(menu)}, ${count} slices`, `${radialMenuName(menu)} önizlemesi, ${count} dilim`)}>
      {menu.slices.map((slice, i) => {
        const step = (Math.PI * 2) / count;
        const mid = i * step;
        const tx = 64 * Math.sin(mid);
        const ty = -64 * Math.cos(mid);
        const text = slice.label.trim() || String(i + 1);
        const cls = ["wedge", i === selected ? "on" : "", slice.action.kind === "none" ? "empty" : ""].join(" ");
        return (
          <g key={i} className={cls} tabIndex={i === selected ? 0 : -1} role="button" aria-pressed={i === selected} aria-label={L(`Slice ${i + 1}: ${text}`, `Dilim ${i + 1}: ${text}`)} onClick={() => onSelect(i)} onKeyDown={(e) => key(e, i)}>
            <path d={wedgePath(i, count, 26, 96)} />
            <text x={tx} y={ty} textAnchor="middle" dominantBaseline="central">{clip(text, count > 6 ? 5 : 8)}</text>
          </g>
        );
      })}
      <circle className="hub" r="22" />
      <text className="hub-text" textAnchor="middle" dominantBaseline="central">{L("Cancel", "İptal")}</text>
    </svg>
  );
}

function SliceRow({ slice, index, count, selected, onSelect, onChange, onMove, onRemove }: {
  slice: RadialSlice; index: number; count: number; selected: boolean;
  onSelect: () => void; onChange: (s: RadialSlice) => void; onMove: (d: -1 | 1) => void; onRemove: () => void;
}) {
  return (
    <li className={selected ? "radial-slice selected" : "radial-slice"} onFocus={onSelect}>
      <button className="step-no num" onClick={onSelect} aria-label={L(`Select slice ${index + 1}`, `Dilim ${index + 1} seç`)}>{index + 1}</button>
      <div className="radial-slice-fields">
        <input type="text" value={slice.label} maxLength={maxRadialLabelLength} placeholder={L(`Label (empty shows ${index + 1})`, `Etiket (boşsa ${index + 1})`)} aria-label={L(`Slice ${index + 1} label`, `Dilim ${index + 1} etiketi`)} onChange={(e) => onChange({ ...slice, label: e.target.value })} />
        <ActionEditor action={slice.action} allowRadialMenu={false} onChange={(action) => onChange({ ...slice, action })} />
      </div>
      <div className="step-tools">
        <button className="icon-btn" disabled={index === 0} aria-label={L("Move up", "Yukarı taşı")} onClick={() => onMove(-1)}>
          <span className="chev up" />
        </button>
        <button className="icon-btn" disabled={index === count - 1} aria-label={L("Move down", "Aşağı taşı")} onClick={() => onMove(1)}>
          <span className="chev down" />
        </button>
        <button className="icon-btn" disabled={count <= minRadialSlices} aria-label={L("Remove slice", "Dilimi kaldır")} onClick={onRemove}>
          <svg viewBox="0 0 10 10" width="10" height="10" aria-hidden>
            <path d="M 1.5 1.5 L 8.5 8.5 M 8.5 1.5 L 1.5 8.5" />
          </svg>
        </button>
      </div>
    </li>
  );
}

function MenuEditor({ menu, onChange, onDelete }: { menu: RadialMenu; onChange: (m: RadialMenu) => void; onDelete: () => void }) {
  const [sel, setSel] = useState(0);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const slices = menu.slices;
  const current = Math.min(sel, slices.length - 1);
  const setSlices = (next: RadialSlice[]) => onChange({ ...menu, slices: next.slice(0, maxRadialSlices) });
  const move = (i: number, d: -1 | 1) => {
    const j = i + d;
    if (j < 0 || j >= slices.length) return;
    const next = [...slices];
    [next[i], next[j]] = [next[j], next[i]];
    setSlices(next);
    if (current === i) setSel(j);
  };

  return (
    <div className="macro-detail">
      <div className="detail-head">
        <input className="name-input" type="text" value={menu.name} maxLength={maxRadialNameLength} aria-label={L("Menu name", "Menü adı")} onChange={(e) => onChange({ ...menu, name: e.target.value })} />
        <span className="faint small num">{slices.length} {L("slices", "dilim")}</span>
      </div>
      <div className="detail-actions">
        <Segmented label={L("Stick that selects", "Seçen analog")} value={menu.stick} options={stickOptions} onChange={(stick) => onChange({ ...menu, stick })} />
        <span className="spacer" />
        {confirmDelete ? (
          <>
            <span className="small warn">{L("Delete this menu? Buttons that open it will be cleared.", "Silinsin mi? Bu menüyü açan tuşlar boşalır.")}</span>
            <button className="button ghost" onClick={() => setConfirmDelete(false)}>{L("Cancel", "Vazgeç")}</button>
            <button className="button danger" onClick={onDelete}>{L("Delete", "Sil")}</button>
          </>
        ) : (
          <button className="button ghost" onClick={() => setConfirmDelete(true)}>{L("Delete", "Sil")}</button>
        )}
      </div>

      <section className="detail-section">
        <div className="radial-layout">
          <Preview menu={menu} selected={current} onSelect={setSel} />
          <div className="radial-notes">
            <p className="hint">{L(
              `Hold the button that opens this menu and tilt the ${menu.stick} stick toward a slice. Slice 1 is at the top, the rest follow clockwise. Release to run it, or release at the centre to cancel.`,
              `Menüyü açan tuşu basılı tut ve ${menu.stick === "left" ? "sol" : "sağ"} analogu bir dilime yatır. 1. dilim en üsttedir, diğerleri saat yönünde sıralanır. Bırakınca çalışır; ortadayken bırakırsan iptal olur.`)}</p>
            <p className="hint">{L(
              "Slices are taps: each action runs once for a short moment. A macro set to repeat while held runs as a single tap. Only the selecting stick is paused while the menu is open.",
              "Dilimler tek dokunuştur: her eylem kısa bir an bir kez çalışır. Basılıyken tekrarla ayarlı makro tek dokunuş olarak çalışır. Menü açıkken yalnız seçimi yapan analog devre dışı kalır.")}</p>
          </div>
        </div>
      </section>

      <section className="detail-section">
        <div className="panel-head">
          <h2>{L("Slices", "Dilimler")}</h2>
          <span className="faint small">{L(`${minRadialSlices} to ${maxRadialSlices}`, `${minRadialSlices} ile ${maxRadialSlices} arası`)}</span>
        </div>
        <ol className="step-list">
          {slices.map((slice, i) => (
            <SliceRow
              key={i}
              slice={slice}
              index={i}
              count={slices.length}
              selected={current === i}
              onSelect={() => setSel(i)}
              onChange={(next) => setSlices(slices.map((x, j) => (j === i ? next : x)))}
              onMove={(d) => move(i, d)}
              onRemove={() => {
                setSlices(slices.filter((_, j) => j !== i));
                if (current >= i && current > 0) setSel(current - 1);
              }}
            />
          ))}
        </ol>
        <div className="button-row">
          <button className="button" disabled={slices.length >= maxRadialSlices} onClick={() => { setSlices([...slices, defaultRadialSlice()]); setSel(slices.length); }}>
            {L("Add slice", "Dilim ekle")}
          </button>
          {slices.length >= maxRadialSlices && <span className="hint">{L("Maximum", "En çok")} {maxRadialSlices}.</span>}
        </div>
      </section>
    </div>
  );
}

export function RadialMenus({ draft, editMapping }: { draft: Profile; editMapping: EditMapping }) {
  const menus = draft.mapping.radialMenus;
  const [selectedId, setSelectedId] = useState<string | null>(menus[0]?.id ?? null);
  const selected = menus.find((m) => m.id === selectedId) ?? menus[0];
  const full = menus.length >= maxRadialMenus;

  const add = () => {
    let n = menus.length + 1;
    while (menus.some((m) => m.name === L(`Menu ${n}`, `Menü ${n}`))) n++;
    const menu = defaultRadialMenu(L(`Menu ${n}`, `Menü ${n}`), newId());
    editMapping((m) => ({ ...m, radialMenus: [...m.radialMenus, menu].slice(0, maxRadialMenus) }));
    setSelectedId(menu.id);
  };
  const update = (menu: RadialMenu) =>
    editMapping((m) => ({ ...m, radialMenus: m.radialMenus.map((x) => (x.id === menu.id ? menu : x)) }));

  return (
    <div className="profiles-grid macros-grid">
      <div>
        {menus.length > 0 && (
          <ul className="profile-list">
            {menus.map((m) => (
              <li key={m.id}>
                <button className={m.id === selected?.id ? "selected" : ""} onClick={() => setSelectedId(m.id)}>
                  <span className="pl-name">{radialMenuName(m)}</span>
                  <span className="pl-games faint">
                    {m.slices.length} {L("slices ·", "dilim ·")} {m.stick === "left" ? L("left stick", "sol analog") : L("right stick", "sağ analog")}
                  </span>
                </button>
              </li>
            ))}
          </ul>
        )}
        <div className="button-row">
          <button className="button" disabled={full} onClick={add}>{L("New radial menu", "Yeni daire menü")}</button>
          {full && <span className="hint">{L("Maximum", "En çok")} {maxRadialMenus} {L("menus.", "menü.")}</span>}
        </div>
      </div>
      {selected ? (
        <MenuEditor
          key={selected.id}
          menu={selected}
          onChange={update}
          onDelete={() => {
            editMapping((m) => removeRadialMenu(m, selected.id));
            setSelectedId(null);
          }}
        />
      ) : (
        <Section title={L("Radial menus", "Daire menüler")}>
          <p className="hint">{L("A radial menu is a wheel of up to 8 actions. Create one here, then choose System > Radial menu as the press action of a button on the Mapping tab.", "Daire menü, en çok 8 eylemli bir çarktır. Burada oluştur, sonra Tuşlar sekmesinde bir tuşun basış eylemi olarak Sistem > Daire menü seçeneğini seç.")}</p>
        </Section>
      )}
    </div>
  );
}
