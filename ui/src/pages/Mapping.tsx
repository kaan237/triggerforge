import { L } from "../i18n";
import { useEffect, useRef, useState } from "react";
import { ControllerDrawing } from "../ControllerDrawing";
import { send, type Binding, type ButtonName, type ButtonChord, type Mapping as MappingConfig, type MappingLayer, type Profile, type TriggerMapping } from "../host";
import { ActionEditor, BindingEditor } from "../MappingEditors";
import { bindingSummary, buttonDefs, buttonLabel, desktopTemplate, fromInput, hasBindings, isEmptyBinding, layerModes, toInput, triggerStageModes } from "../mapping";
import { defaultAction, defaultBinding, defaultMappingLayer, maxChords, maxLayerNameLength, maxLayers, processTrigger, withMappingLayers } from "../model";
import { useApp, useInput } from "../state";
import { DraftBar, PageHead, Section, Segmented, Slider, Toggle, pct } from "../ui";
import { Macros } from "./Macros";
import { RadialMenus } from "./RadialMenus";
import { GyroMouseTab, StickMappingTab, TouchpadTab } from "./MappingTabs";

type Tab = "buttons" | "chords" | "triggers" | "touchpad" | "gyro" | "sticks" | "radial" | "macros";

const tabs: { id: Tab; label: string }[] = [
  { id: "buttons", get label() { return L("Mapping", "Tuşlar"); } },
  { id: "chords", get label() { return L("Chords", "Akorlar"); } },
  { id: "triggers", get label() { return L("Triggers", "Tetikler"); } },
  { id: "touchpad", get label() { return L("Touchpad", "Touchpad"); } },
  { id: "gyro", get label() { return L("Gyro mouse", "Gyro fare"); } },
  { id: "sticks", get label() { return L("Sticks", "Analoglar"); } },
  { id: "radial", get label() { return L("Radial menus", "Daire menüler"); } },
  { id: "macros", get label() { return L("Macros", "Makrolar"); } },
];

export type EditMapping = (fn: (m: MappingConfig) => MappingConfig) => void;

function StatusLine({ draft }: { draft: Profile }) {
  const { status, activeId, dirty, draftBase } = useApp();
  const s = status?.mapping;
  let text: string;
  let warn = false;
  let restart = false;
  if (draft.id !== activeId) text = L("Mappings take effect when this profile is active.", "Bu profil etkin değil. Atamalar profil etkinleşince çalışır.");
  else if (dirty && draftBase && draftBase.mapping.enabled !== draft.mapping.enabled) text = L("Save to apply.", "Kaydedince geçerli olur.");
  else if (!draft.mapping.enabled) text = L("Off.", "Kapalı.");
  else if (!status?.connected) text = L("No controller connected.", "Kontrolcü bağlı değil.");
  else if (s?.paused) text = s.pauseReason === "ui" ? L("Capturing a key.", "Tuş yakalanıyor.") : "";
  else if (s?.blockedBy) {
    text = L(`${s.blockedBy} is running as administrator, so it cannot receive mappings.`, `${s.blockedBy} yönetici olarak çalışıyor, atamalar ona ulaşmıyor.`);
    warn = true;
    restart = !status.elevated;
  } else if (s?.active) text = L("Active.", "Etkin.");
  else text = L("Waiting.", "Bekliyor.");
  const paused = s?.pauseReason === "shortcut" ? L("Paused (shortcut)", "Duraklatıldı (kısayol)")
    : s?.pauseReason === "fullscreen" ? L("Paused (fullscreen)", "Duraklatıldı (tam ekran)")
      : L("Paused", "Duraklatıldı");

  return (
    <div className="map-status">
      <span className={s?.active && !s.blockedBy && draft.id === activeId ? "dot live" : "dot"} aria-hidden />
      {text && <span className={warn ? "small warn" : "small muted-text"}>{text}</span>}
      {s?.paused && <span className="tag accent">{paused}</span>}
      <button className="button" disabled={!s} onClick={() => send({ type: "mapping.togglePause" })}>
        {s?.pauseReason === "shortcut" ? L("Resume", "Sürdür")
          : s?.pauseReason === "ui" ? L("Toggle pause", "Duraklatmayı değiştir") : L("Pause", "Duraklat")}
      </button>
      {restart && (
        <button className="button" onClick={() => send({ type: "app.restartElevated" })}>
          {L("Restart as administrator", "Yönetici olarak yeniden başlat")}
        </button>
      )}
    </div>
  );
}

function Head({ draft, editMapping }: { draft: Profile; editMapping: EditMapping }) {
  const [confirm, setConfirm] = useState(false);
  const m = draft.mapping;
  const apply = () => {
    editMapping((cur) => desktopTemplate(cur));
    setConfirm(false);
  };
  return (
    <section className="panel map-head">
      <div className="map-head-main">
        <Toggle
          label={L("Keyboard and mouse mapping", "Klavye/fare ataması")}
          hint={L("The game still sees the controller. Mappings also send keyboard and mouse input.", "Oyun kontrolcüyü görmeye devam eder; atamalar ayrıca klavye ve fare girdisi üretir.")}
          checked={m.enabled}
          onChange={(enabled) => editMapping((cur) => ({ ...cur, enabled }))}
        />
        <StatusLine draft={draft} />
      </div>
      <div className="map-head-side">
        {confirm ? (
          <span className="confirm-inline">
            <span className="small warn">{L("Replace the current mappings?", "Mevcut tuş atamaları değişsin mi?")}</span>
            <button className="button ghost" onClick={() => setConfirm(false)}>
              {L("Cancel", "Vazgeç")}
            </button>
            <button className="button danger" onClick={apply}>
              {L("Replace", "Değiştir")}
            </button>
          </span>
        ) : (
          <button className="button" title={L("Desktop layout with mouse, arrow keys, scrolling, and shortcuts", "Fare, ok tuşları, kaydırma ve kısayollarla masaüstü düzeni")} onClick={() => (hasBindings(m) ? setConfirm(true) : apply())}>
            {L("Desktop preset", "Masaüstü şablonu")}
          </button>
        )}
      </div>
    </section>
  );
}

function ButtonsTab({ draft, editMapping }: { draft: Profile; editMapping: EditMapping }) {
  const { profiles, output, status } = useApp();
  const input = useInput();
  const [selected, setSelected] = useState<ButtonName>("cross");
  const [layer, setLayer] = useState(-1);
  const m = draft.mapping;
  const index = Math.min(layer, m.layers.length - 1);
  const shift = m.layers[index];
  const down = new Set((input?.buttons ?? []).map(fromInput));
  const activators = new Set(m.layers.map((entry) => entry.button));
  const summary = (binding?: Binding) => bindingSummary(binding, profiles, draft.macros, draft.mapping.radialMenus);
  const baseName = L("Base", "Temel");
  const nameOf = (entry: MappingLayer, i: number) => entry.name.trim() || L(`Layer ${i + 1}`, `Katman ${i + 1}`);
  const fallback = (button: ButtonName) => {
    for (let i = index - 1; i >= 0; i--) {
      const lower = m.layers[i];
      if (lower.button !== "none" && lower.buttons[button]) {
        return { name: nameOf(lower, i), binding: lower.buttons[button], shift: true };
      }
    }
    return { name: baseName, binding: m.buttons[button], shift: false };
  };

  const setShift = (patch: Partial<MappingLayer>) =>
    editMapping((cur) => withMappingLayers(cur, cur.layers.map((entry, i) => i === index ? { ...entry, ...patch } : entry)));
  const setBinding = (binding: Binding | null) =>
    editMapping((cur) => {
      const buttons = { ...(cur.layers[index]?.buttons ?? cur.buttons) };
      if (!binding || (!shift && isEmptyBinding(binding))) delete buttons[selected];
      else buttons[selected] = binding;
      if (!shift) return { ...cur, buttons };
      return withMappingLayers(cur, cur.layers.map((entry, i) => i === index ? { ...entry, buttons } : entry));
    });
  const addLayer = () => {
    editMapping((cur) => withMappingLayers(cur, [...cur.layers, defaultMappingLayer(cur.layers.length)]));
    setLayer(m.layers.length);
  };
  const removeLayer = () => {
    editMapping((cur) => withMappingLayers(cur, cur.layers.filter((_, i) => i !== index)));
    setLayer(index - 1);
  };
  const own = (shift?.buttons ?? m.buttons)[selected];
  const inherited = fallback(selected);
  const groups = [...new Set(buttonDefs.map((b) => b.group))];

  return (
    <div className="keys-grid">
      <div className="keys-left">
        <div className="drawing pick-drawing">
          <ControllerDrawing input={status?.connected ? input : null} output={status?.connected ? output : null} selected={toInput(selected)} onPick={(id) => setSelected(fromInput(id) ?? selected)} />
        </div>
        <div className="layer-pick">
          <label className="row-field">
            <span className="label">{L("Layer", "Katman")}</span>
            <select value={index} onChange={(e) => setLayer(Number(e.target.value))}>
              <option value={-1}>{baseName}</option>
              {m.layers.map((entry, i) => <option key={i} value={i}>{nameOf(entry, i)}</option>)}
            </select>
          </label>
          <div className="button-row">
            <button className="button" disabled={m.layers.length >= maxLayers} onClick={addLayer}>{L("Add layer", "Katman ekle")}</button>
            {shift && <button className="button ghost" disabled={m.layers.length === 1} onClick={removeLayer}>{L("Remove layer", "Katmanı kaldır")}</button>}
          </div>
          {shift && (
            <>
              <label className="row-field">
                <span className="label">{L("Name", "Ad")}</span>
                <input value={shift.name} maxLength={maxLayerNameLength} onChange={(e) => setShift({ name: e.target.value })} />
              </label>
              <label className="row-field">
                <span className="label">{L("Activator", "Etkinleştirme tuşu")}</span>
                <select value={shift.button} onChange={(e) => setShift({ button: fromInput(e.target.value) ?? "none" })}>
                  <option value="none">{L("None", "Yok")}</option>
                  {buttonDefs.map((b) => <option key={b.id} value={b.id} disabled={b.id !== shift.button && activators.has(b.id)}>{b.label}</option>)}
                </select>
              </label>
              <Segmented label={L("Layer mode", "Katman modu")} value={shift.mode} options={layerModes} onChange={(mode) => setShift({ mode })} />
            </>
          )}
        </div>
        <ul className="button-list">
          {groups.map((g) =>
            buttonDefs.filter((b) => b.group === g).map((b, i) => {
              const ownBinding = (shift?.buttons ?? m.buttons)[b.id];
              const text = summary(ownBinding ?? fallback(b.id).binding);
              const cls = [selected === b.id ? "selected" : "", down.has(b.id) ? "live" : "", i === 0 ? "group-start" : ""].join(" ");
              return (
                <li key={b.id}>
                  <button className={cls} onClick={() => setSelected(b.id)}>
                    <span className="bl-name">{b.label}</span>
                    <span className={!ownBinding || text === "—" ? "bl-sum faint" : "bl-sum"}>{activators.has(b.id) ? L("layer activator", "katman tuşu") : text}</span>
                  </button>
                </li>
              );
            }),
          )}
        </ul>
      </div>

      <section className="panel bind-panel">
        <div className="panel-head">
          <h2>{buttonLabel(selected)}{shift && <span className="tag">{nameOf(shift, index)}</span>}</h2>
          {own && <button className="button ghost" onClick={() => setBinding(null)}>
            {shift ? L("Use lower mapping", "Alt katman atamasını kullan") : L("Clear", "Temizle")}
          </button>}
        </div>
        {activators.has(selected) ? (
          <p className="hint">{L("This button activates a layer. Its own bindings and chords are ignored.", "Bu tuş katmanı etkinleştirir. Kendi atamaları ve akorları yok sayılır.")}</p>
        ) : !shift || own ? (
          <BindingEditor key={`${index}:${selected}`} allowRadialMenu binding={own ?? defaultBinding()} onChange={setBinding} />
        ) : (
          <div className="inherit">
            <p className="hint">{inherited.shift
              ? L(`Falls back to ${inherited.name} when active.`, `Etkinse ${inherited.name} kullanılır.`)
              : L(`Falls back to ${inherited.name}.`, `${inherited.name} katmanındaki atama kullanılır.`)} <span className="muted-text">{summary(inherited.binding)}</span>
            </p>
            <button className="button" onClick={() => setBinding(defaultBinding())}>{L("Add layer mapping", "Katmanda ayrı ata")}</button>
          </div>
        )}
      </section>
    </div>
  );
}

function ChordsTab({ draft, editMapping }: { draft: Profile; editMapping: EditMapping }) {
  const { chords, chordWindowMs, layers } = draft.mapping;
  const activators = new Set(layers.map((entry) => entry.button));
  const available = buttonDefs.filter((b) => !activators.has(b.id));
  const combo = (buttons: ButtonName[]) => [...buttons].sort().join(",");
  const existing = new Set(chords.map((chord) => combo(chord.buttons)));
  const newPair = available.flatMap((first, i) => available.slice(i + 1).map((second) => [first.id, second.id]))
    .find((buttons) => !existing.has(combo(buttons)));
  const set = (index: number, patch: Partial<ButtonChord>) =>
    editMapping((m) => ({ ...m, chords: m.chords.map((chord, i) => i === index ? { ...chord, ...patch } : chord) }));

  return (
    <div className="tab-grid">
      <Section title={L("Chords", "Akorlar")} className="tab-wide">
        <Slider label={L("Chord window", "Akor süresi")} min={20} max={300} value={chordWindowMs} format={(ms) => L(`${ms} ms`, `${ms} ms`)} onChange={(value) => editMapping((m) => ({ ...m, chordWindowMs: value }))} />
        {chords.map((chord, index) => {
          const duplicate = chords.slice(0, index).some((other) => combo(other.buttons) === combo(chord.buttons));
          return (
            <div key={index} className="inherit">
              <div className="button-group">
                {[0, 1, 2].map((slot) => (
                  <label key={slot} className="field-inline">
                    {L(`Button ${slot + 1}`, `Tuş ${slot + 1}`)}
                    <select value={chord.buttons[slot] ?? ""} onChange={(e) => {
                      const button = fromInput(e.target.value);
                      const buttons = [...chord.buttons];
                      if (button) buttons[slot] = button;
                      else buttons.splice(slot, 1);
                      set(index, { buttons });
                    }}>
                      {slot === 2 && <option value="">{L("None", "Yok")}</option>}
                      {buttonDefs.map((b) => <option key={b.id} value={b.id} disabled={b.id !== chord.buttons[slot] && (activators.has(b.id) || chord.buttons.includes(b.id))}>{b.label}</option>)}
                    </select>
                  </label>
                ))}
                <button className="button ghost" onClick={() => editMapping((m) => ({ ...m, chords: m.chords.filter((_, i) => i !== index) }))}>{L("Remove chord", "Akoru kaldır")}</button>
              </div>
              <ActionEditor action={chord.action} allowRadialMenu={false} onChange={(action) => set(index, { action })} />
              {chord.action.kind === "none" && <p className="hint">{L("Choose an action to save this chord.", "Bu akoru kaydetmek için bir eylem seç.")}</p>}
              {duplicate && <p className="hint warn">{L("This combination already exists above.", "Bu tuş birleşimi yukarıda zaten var.")}</p>}
              {chord.buttons.some((button) => activators.has(button)) && <p className="hint warn">{L("A layer activator prevents this chord from firing.", "Katman tuşu bu akorun çalışmasını engeller.")}</p>}
            </div>
          );
        })}
        <button className="button" disabled={chords.length >= maxChords || !newPair} onClick={() => {
          if (newPair) editMapping((m) => ({ ...m, chords: [...m.chords, { buttons: newPair, action: defaultAction() }] }));
        }}>{L("Add chord", "Akor ekle")}</button>
      </Section>
    </div>
  );
}

function TriggersTab({ draft, editMapping }: { draft: Profile; editMapping: EditMapping }) {
  const input = useInput();
  const { status } = useApp();
  return (
    <div className="sticks-grid">
      {(["left", "right"] as const).map((side) => {
        const cfg = draft.mapping.triggers[side];
        const raw = status?.connected ? input?.[side === "left" ? "l2" : "r2"] ?? 0 : 0;
        const travel = Math.round(processTrigger(raw, draft.triggerResponse[side]) * 255) / 255;
        const set = (patch: Partial<TriggerMapping>) =>
          editMapping((m) => ({ ...m, triggers: { ...m.triggers, [side]: { ...m.triggers[side], ...patch } } }));
        const minimumFull = (soft: number) => Math.max(0.15, Math.round((soft + 0.05) * 100) / 100);
        const title = side === "left" ? L("Left trigger (L2)", "Sol tetik (L2)") : L("Right trigger (R2)", "Sağ tetik (R2)");
        return (
          <Section key={side} title={title}>
            <Segmented label={L("Trigger mode", "Tetik modu")} value={cfg.mode} options={triggerStageModes} onChange={(mode) => set({ mode })} />
            <svg width="100%" height="20" viewBox="0 0 100 12" preserveAspectRatio="none" role="img" aria-label={L(`Shaped travel ${pct(travel)}, soft ${pct(cfg.softThreshold)}, full ${pct(cfg.fullThreshold)}`, `İşlenmiş basış ${pct(travel)}, hafif ${pct(cfg.softThreshold)}, tam ${pct(cfg.fullThreshold)}`)}>
              <rect x="0" y="3" width="100" height="6" rx="1" fill="var(--line)" />
              <rect x="0" y="3" width={travel * 100} height="6" rx="1" fill="var(--accent)" />
              {[cfg.softThreshold, cfg.fullThreshold].map((threshold) => <rect key={threshold} x={Math.min(threshold * 100, 99.4)} y="0" width="0.6" height="12" fill="var(--text)" />)}
            </svg>
            <Slider label={L("Soft threshold", "Hafif basış eşiği")} min={0.05} max={0.9} step={0.01} value={cfg.softThreshold} format={pct} onChange={(softThreshold) => set({ softThreshold, fullThreshold: Math.max(cfg.fullThreshold, minimumFull(softThreshold)) })} />
            <Slider label={L("Full threshold", "Tam basış eşiği")} min={minimumFull(cfg.softThreshold)} max={1} step={0.01} value={cfg.fullThreshold} format={pct} onChange={(fullThreshold) => set({ fullThreshold })} />
            <Slider label={L("Release hysteresis", "Bırakma farkı")} min={0.01} max={0.2} step={0.01} value={cfg.hysteresis} format={pct} onChange={(hysteresis) => set({ hysteresis })} />
            {cfg.mode === "hipFire" && <Slider label={L("Hip fire window", "Hızlı tam basış süresi")} min={20} max={300} value={cfg.hipFireMs} format={(ms) => L(`${ms} ms`, `${ms} ms`)} onChange={(hipFireMs) => set({ hipFireMs })} />}
            <div className="bind-row"><span className="label">{L("Soft pull", "Hafif basış")}</span><ActionEditor action={cfg.soft} allowRadialMenu={false} onChange={(soft) => set({ soft })} /></div>
            <div className="bind-row"><span className="label">{L("Full pull", "Tam basış")}</span><ActionEditor action={cfg.full} allowRadialMenu={false} onChange={(full) => set({ full })} /></div>
          </Section>
        );
      })}
    </div>
  );
}

export function Mapping() {
  const { draft, edit } = useApp();
  const [tab, setTab] = useState<Tab>("buttons");
  const [countdown, setCountdown] = useState(0);
  const turnTimer = useRef<number | undefined>(undefined);
  useEffect(() => () => window.clearInterval(turnTimer.current), []);
  if (!draft) return <div className="page"><PageHead title={L("Mapping", "Tuşlar")} /></div>;
  const editMapping: EditMapping = (fn) => edit((p) => ({ ...p, mapping: fn(p.mapping) }));
  const testTurn = () => {
    if (turnTimer.current !== undefined) return;
    const delayMs = 2000;
    const deadline = performance.now() + delayMs;
    send({ type: "mapping.testTurn", counts: draft.mapping.flickStick.countsPer360, delayMs });
    setCountdown(2);
    turnTimer.current = window.setInterval(() => {
      const remaining = Math.max(0, Math.ceil((deadline - performance.now()) / 1000));
      setCountdown(remaining);
      if (remaining === 0) {
        window.clearInterval(turnTimer.current);
        turnTimer.current = undefined;
      }
    }, 100);
  };

  return (
    <div className="page mapping-page">
      <PageHead title={L("Mapping", "Tuşlar")}>
        <DraftBar />
      </PageHead>
      <Head draft={draft} editMapping={editMapping} />
      <div className="subtabs" role="tablist">
        {tabs.map((t) => (
          <button key={t.id} role="tab" aria-selected={tab === t.id} className={tab === t.id ? "on" : ""} onClick={() => setTab(t.id)}>
            {t.label}
            {t.id === "macros" && draft.macros.length > 0 && <span className="faint num"> {draft.macros.length}</span>}
            {t.id === "radial" && draft.mapping.radialMenus.length > 0 && <span className="faint num"> {draft.mapping.radialMenus.length}</span>}
          </button>
        ))}
      </div>
      {tab === "buttons" && <ButtonsTab draft={draft} editMapping={editMapping} />}
      {tab === "chords" && <ChordsTab draft={draft} editMapping={editMapping} />}
      {tab === "triggers" && <TriggersTab draft={draft} editMapping={editMapping} />}
      {tab === "touchpad" && <TouchpadTab draft={draft} editMapping={editMapping} />}
      {tab === "gyro" && <GyroMouseTab draft={draft} editMapping={editMapping} countdown={countdown} onTestTurn={testTurn} />}
      {tab === "sticks" && <StickMappingTab draft={draft} editMapping={editMapping} countdown={countdown} onTestTurn={testTurn} />}
      {tab === "radial" && <RadialMenus draft={draft} editMapping={editMapping} />}
      {tab === "macros" && <Macros draft={draft} />}
    </div>
  );
}
