import { useRef, useState, type KeyboardEvent } from "react";
import type { Action, ActionKind, Binding, Direction, KeyName, Macro, MouseButtonName, Special } from "./host";
import { codeToKey, keyLabel, modifierKeys, searchKeys } from "./keys";
import { actionKinds, actionOfKind, directions, mouseButtons, pickProgram, specials, usePause } from "./mapping";
import { useApp } from "./state";
import { Check, Segmented, Slider } from "./ui";

// ---- key capture ----

function orderChord(keys: KeyName[]): KeyName[] {
  return [...keys.filter((k) => modifierKeys.has(k)), ...keys.filter((k) => !modifierKeys.has(k))];
}

// Records a chord from the physical keys (KeyboardEvent.code) pressed while focused.
// Keys the page cannot see (Win, Print Screen, media, F13-F24) come from the list.
export function KeyField({ value, onChange, max = 4, compact }: { value: KeyName[]; onChange: (keys: KeyName[]) => void; max?: number; compact?: boolean }) {
  const box = useRef<HTMLDivElement>(null);
  const held = useRef(new Set<KeyName>());
  const [focused, setFocused] = useState(false);
  const [recorded, setRecordedState] = useState<KeyName[]>([]);
  const rec = useRef<KeyName[]>([]);
  const setRecorded = (next: KeyName[] | ((r: KeyName[]) => KeyName[])) => {
    rec.current = typeof next === "function" ? next(rec.current) : next;
    setRecordedState(rec.current);
  };
  const [listOpen, setListOpen] = useState(false);
  usePause(focused);

  const commit = (keys: KeyName[]) => {
    onChange(orderChord(keys).slice(0, max));
    setRecorded([]);
    box.current?.blur();
  };

  const down = (e: KeyboardEvent) => {
    e.preventDefault();
    e.stopPropagation();
    if (e.repeat) return;
    const k = codeToKey(e.code);
    if (!k) return;
    const fresh = held.current.size === 0;
    held.current.add(k);
    // Modifier flags count too, in case the modifier's own keydown never reached the page.
    const flags: [boolean, KeyName, KeyName][] = [
      [e.ctrlKey, "leftCtrl", "rightCtrl"],
      [e.shiftKey, "leftShift", "rightShift"],
      [e.altKey, "leftAlt", "rightAlt"],
      [e.metaKey, "leftWin", "rightWin"],
    ];
    setRecorded((r) => {
      let next = fresh ? [] : [...r];
      if (!modifierKeys.has(k) && max > 1)
        for (const [on, left, right] of flags) if (on && !next.includes(left) && !next.includes(right) && next.length < max - 1) next = [...next, left];
      return next.includes(k) || next.length >= max ? next : [...next, k];
    });
  };

  const up = (e: KeyboardEvent) => {
    e.preventDefault();
    e.stopPropagation();
    const k = codeToKey(e.code);
    if (!k) return;
    const had = held.current.delete(k);
    if (held.current.size > 0) return;
    // Print Screen and a few others only ever send keyup.
    if (!had && rec.current.length === 0) return commit([k]);
    if (rec.current.length > 0) commit(rec.current);
  };

  const shown = focused ? recorded : value;
  const addFromList = (k: KeyName) => {
    if (max === 1) onChange([k]);
    else if (!value.includes(k) && value.length < max) onChange(orderChord([...value, k]));
  };

  return (
    <div className={compact ? "key-field compact" : "key-field"}>
      <div className="key-row">
        <div
          ref={box}
          className={focused ? "key-capture on" : "key-capture"}
          tabIndex={0}
          role="textbox"
          aria-label="Tuş yakala"
          onFocus={() => {
            held.current.clear();
            setRecorded([]);
            setFocused(true);
          }}
          onBlur={() => {
            held.current.clear();
            setFocused(false);
          }}
          onKeyDown={down}
          onKeyUp={up}
        >
          {shown.length > 0 ? (
            shown.map((k, i) => (
              <span key={k} className="key-chip-wrap">
                {i > 0 && <span className="faint">+</span>}
                <kbd className="key-chip">{keyLabel(k)}</kbd>
              </span>
            ))
          ) : (
            <span className="faint">{focused ? "Tuşa bas…" : "Tıkla, tuşa bas"}</span>
          )}
        </div>
        <button className={listOpen ? "button on" : "button"} onClick={() => setListOpen(!listOpen)} aria-expanded={listOpen}>
          Listeden
        </button>
        {value.length > 0 && !compact && (
          <button className="button ghost" onClick={() => onChange([])}>
            Temizle
          </button>
        )}
      </div>
      {listOpen && <KeyList selected={value} onPick={addFromList} onClose={() => setListOpen(false)} />}
    </div>
  );
}

function KeyList({ selected, onPick, onClose }: { selected: KeyName[]; onPick: (k: KeyName) => void; onClose: () => void }) {
  const [q, setQ] = useState("");
  const found = searchKeys(q);
  const groups = [...new Set(found.map((k) => k.group))];
  return (
    <div className="key-list">
      <div className="key-list-head">
        <input type="text" value={q} autoFocus placeholder="Tuş ara: win, f13, ses…" onChange={(e) => setQ(e.target.value)} onKeyDown={(e) => e.key === "Escape" && onClose()} />
        <span className="hint">Konumlar ABD klavye düzenine göre.</span>
        <button className="button ghost" onClick={onClose}>
          Kapat
        </button>
      </div>
      <div className="key-list-body">
        {groups.map((g) => (
          <div key={g} className="key-group">
            <span className="sub-label">{g}</span>
            <div className="key-grid">
              {found
                .filter((k) => k.group === g)
                .map((k) => (
                  <button key={k.name} className={selected.includes(k.name) ? "on" : ""} title={k.capture ? k.name : `${k.name} · yalnız listeden`} onClick={() => onPick(k.name)}>
                    {k.label}
                  </button>
                ))}
            </div>
          </div>
        ))}
        {found.length === 0 && <p className="hint">Eşleşen tuş yok.</p>}
      </div>
    </div>
  );
}

// ---- action ----

export function ActionEditor({ action, onChange, kinds }: { action: Action; onChange: (a: Action) => void; kinds?: ActionKind[] }) {
  const { profiles, draft } = useApp();
  const macros: Macro[] = draft?.macros ?? [];
  const set = (patch: Partial<Action>) => onChange({ ...action, ...patch });
  const options = kinds ? actionKinds.filter((k) => kinds.includes(k.id)) : actionKinds;

  return (
    <div className="action-editor">
      <select value={action.kind} aria-label="Eylem türü" onChange={(e) => onChange(actionOfKind(e.target.value as ActionKind))}>
        {options.map((k) => (
          <option key={k.id} value={k.id}>
            {k.label}
          </option>
        ))}
      </select>
      <div className="action-fields">
        {action.kind === "key" && <KeyField value={action.keys ?? []} onChange={(keys) => set({ keys })} />}
        {action.kind === "mouseButton" && (
          <Segmented label="Fare tuşu" value={action.button ?? "left"} options={mouseButtons} onChange={(button: MouseButtonName) => set({ button })} />
        )}
        {action.kind === "wheel" && (
          <>
            <Segmented label="Yön" value={action.direction ?? "down"} options={directions} onChange={(direction: Direction) => set({ direction })} />
            <label className="number-field">
              <input type="number" min={1} max={10} value={action.amount ?? 1} onChange={(e) => set({ amount: Math.min(10, Math.max(1, Math.round(Number(e.target.value) || 1))) })} />
              <span className="faint">çentik</span>
            </label>
          </>
        )}
        {action.kind === "macro" &&
          (macros.length > 0 ? (
            <select value={action.macroId ?? ""} aria-label="Makro" onChange={(e) => set({ macroId: e.target.value })}>
              <option value="" disabled>
                Makro seç
              </option>
              {macros.map((m) => (
                <option key={m.id} value={m.id}>
                  {m.name}
                </option>
              ))}
            </select>
          ) : (
            <span className="hint">Bu profilde makro yok. Makrolar sekmesinden ekle.</span>
          ))}
        {action.kind === "profile" && (
          <select value={action.profileId ?? "next"} aria-label="Profil" onChange={(e) => set({ profileId: e.target.value })}>
            <option value="next">Sonraki profil</option>
            <option value="previous">Önceki profil</option>
            {profiles.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name}
              </option>
            ))}
          </select>
        )}
        {action.kind === "run" && (
          <div className="run-fields">
            <div className="key-row">
              <input type="text" className="grow" value={action.path ?? ""} maxLength={260} placeholder="C:\…\program.exe" spellCheck={false} onChange={(e) => set({ path: e.target.value })} />
              <button className="button" onClick={() => pickProgram((path) => set({ path }))}>
                Seç…
              </button>
            </div>
            <input type="text" value={action.args ?? ""} maxLength={512} placeholder="Argümanlar (isteğe bağlı)" spellCheck={false} onChange={(e) => set({ args: e.target.value })} />
          </div>
        )}
        {action.kind === "micMute" && <span className="hint">Windows varsayılan mikrofonunu susturur ya da açar. Mikrofon ışığı durumu gösterir.</span>}
        {action.kind === "special" && (
          <>
            <select value={action.special ?? "screenshot"} aria-label="Sistem eylemi" onChange={(e) => set({ special: e.target.value as Special })}>
              {specials.map((s) => (
                <option key={s.id} value={s.id}>
                  {s.label}
                </option>
              ))}
            </select>
            <span className="hint">{specials.find((s) => s.id === action.special)?.hint}</span>
          </>
        )}
      </div>
    </div>
  );
}

// ---- binding ----

export function BindingEditor({ binding, onChange }: { binding: Binding; onChange: (b: Binding) => void }) {
  const set = (patch: Partial<Binding>) => onChange({ ...binding, ...patch });
  const plain = !binding.hold && !binding.double;

  return (
    <div className="binding">
      <div className="bind-row">
        <span className="label">Basınca</span>
        <ActionEditor action={binding.press} onChange={(press) => set({ press })} />
      </div>

      <div className="bind-row">
        <Check label="Basılı tutunca" checked={!!binding.hold} onChange={(on) => set({ hold: on ? actionOfKind("key") : null })} />
        {binding.hold && (
          <div className="bind-sub">
            <ActionEditor action={binding.hold} onChange={(hold) => set({ hold })} />
            <Slider label="Tutma süresi" min={150} max={2000} step={10} value={binding.holdMs} format={(v) => `${v} ms`} onChange={(holdMs) => set({ holdMs })} />
          </div>
        )}
      </div>

      <div className="bind-row">
        <Check label="Çift basınca" checked={!!binding.double} onChange={(on) => set({ double: on ? actionOfKind("key") : null })} />
        {binding.double && (
          <div className="bind-sub">
            <ActionEditor action={binding.double} onChange={(double) => set({ double })} />
            <Slider label="Aralık" min={100} max={600} step={10} value={binding.doubleMs} format={(v) => `${v} ms`} onChange={(doubleMs) => set({ doubleMs })} />
          </div>
        )}
      </div>

      <div className="bind-row">
        <div className="bind-flags">
          <Check label="Turbo" disabled={!plain} checked={binding.turbo} onChange={(turbo) => set({ turbo })} />
          <Check label="Kilitle" disabled={!plain} checked={binding.toggle} onChange={(toggle) => set({ toggle })} />
        </div>
        {plain && binding.turbo && (
          <div className="bind-sub">
            <Slider label="Turbo hızı" min={1} max={30} step={1} value={binding.turboHz} format={(v) => `${v} Hz`} onChange={(turboHz) => set({ turboHz })} />
          </div>
        )}
        <p className="hint">
          {plain
            ? binding.turbo && binding.toggle
              ? "İkisi birden açıksa turbo geçerli."
              : "Turbo: basılıyken tekrarlar. Kilitle: ilk basış basılı tutar, ikincisi bırakır."
            : "Turbo ve kilit yalnız tutma ve çift basma kapalıyken çalışır."}
        </p>
      </div>
    </div>
  );
}
