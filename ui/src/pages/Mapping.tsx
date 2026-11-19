import { L } from "../i18n";
import { useState } from "react";
import { ControllerDrawing } from "../ControllerDrawing";
import { send, type Binding, type ButtonName, type Mapping as MappingConfig, type Profile } from "../host";
import { BindingEditor } from "../MappingEditors";
import { bindingSummary, buttonDefs, buttonLabel, desktopTemplate, fromInput, hasBindings, isEmptyBinding, toInput } from "../mapping";
import { defaultBinding } from "../model";
import { useApp, useInput } from "../state";
import { DraftBar, PageHead, Segmented, Toggle } from "../ui";
import { Macros } from "./Macros";
import { GyroMouseTab, StickMappingTab, TouchpadTab } from "./MappingTabs";

type Tab = "buttons" | "touchpad" | "gyro" | "sticks" | "macros";
type Layer = "base" | "layer";

const tabs: { id: Tab; label: string }[] = [
  { id: "buttons", get label() { return L("Mapping", "Tuşlar"); } },
  { id: "touchpad", label: "Touchpad" },
  { id: "gyro", get label() { return L("Gyro mouse", "Gyro fare"); } },
  { id: "sticks", get label() { return L("Sticks", "Analoglar"); } },
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
  else if (s?.paused) text = L("Paused while capturing a key.", "Duraklatıldı. Tuş yakalanırken atamalar durur.");
  else if (s?.blockedBy) {
    text = L(`${s.blockedBy} is running as administrator, so it cannot receive mappings.`, `${s.blockedBy} yönetici olarak çalışıyor, atamalar ona ulaşmıyor.`);
    warn = true;
    restart = !status.elevated;
  } else if (s?.active) text = "Aktif.";
  else text = "Bekliyor.";

  return (
    <div className="map-status">
      <span className={s?.active && !s.blockedBy && draft.id === activeId ? "dot live" : "dot"} aria-hidden />
      <span className={warn ? "small warn" : "small muted-text"}>{text}</span>
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
        <label className="field-inline">
          {L("Layer button", "Katman tuşu")}
          <select value={m.layerButton} onChange={(e) => editMapping((cur) => ({ ...cur, layerButton: e.target.value as ButtonName | "none" }))}>
            <option value="none">{L("None", "Yok")}</option>
            {buttonDefs.map((b) => (
              <option key={b.id} value={b.id}>
                {b.label}
              </option>
            ))}
          </select>
        </label>
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
  const [layer, setLayer] = useState<Layer>("base");
  const m = draft.mapping;
  const down = new Set((input?.buttons ?? []).map(fromInput));
  const summary = (b?: Binding) => bindingSummary(b, profiles, draft.macros);

  const base = m.buttons[selected];
  const own = m.layer[selected];
  const isLayerButton = m.layerButton === selected;

  const setBase = (b: Binding) =>
    editMapping((cur) => {
      const buttons = { ...cur.buttons };
      if (isEmptyBinding(b)) delete buttons[selected];
      else buttons[selected] = b;
      return { ...cur, buttons };
    });
  const setLayerBinding = (b: Binding | null) =>
    editMapping((cur) => {
      const next = { ...cur.layer };
      if (b) next[selected] = b;
      else delete next[selected];
      return { ...cur, layer: next };
    });

  const groups = [...new Set(buttonDefs.map((b) => b.group))];
  const noLayer = m.layerButton === "none";

  return (
    <div className="keys-grid">
      <div className="keys-left">
        <div className="drawing pick-drawing">
          <ControllerDrawing input={status?.connected ? input : null} output={status?.connected ? output : null} selected={toInput(selected)} onPick={(id) => setSelected(fromInput(id) ?? selected)} />
        </div>
        <div className="layer-pick">
          <Segmented
            label={L("Layer", "Katman")}
            value={layer}
            onChange={setLayer}
            options={[
              { id: "base", label: L("Base", "Temel") },
              { id: "layer", label: noLayer ? L("Layer", "Katman") : L(`Layer (hold ${buttonLabel(m.layerButton)})`, `Katman (${buttonLabel(m.layerButton)} basılı)`) },
            ]}
          />
        </div>
        <ul className="button-list">
          {groups.map((g) =>
            buttonDefs
              .filter((b) => b.group === g)
              .map((b, i) => {
                const layerOwn = m.layer[b.id];
                const text = layer === "layer" && layerOwn ? summary(layerOwn) : summary(m.buttons[b.id]);
                const inherited = layer === "layer" && !layerOwn;
                const cls = [selected === b.id ? "selected" : "", down.has(b.id) ? "live" : "", i === 0 ? "group-start" : ""].join(" ");
                return (
                  <li key={b.id}>
                    <button className={cls} onClick={() => setSelected(b.id)}>
                      <span className="bl-name">{b.label}</span>
                      <span className={inherited || text === "—" ? "bl-sum faint" : "bl-sum"}>{m.layerButton === b.id ? L("layer button", "katman tuşu") : text}</span>
                    </button>
                  </li>
                );
              }),
          )}
        </ul>
      </div>

      <section className="panel bind-panel">
        <div className="panel-head">
          <h2>
            {buttonLabel(selected)}
            {layer === "layer" && <span className="tag">{L("layer", "katman")}</span>}
          </h2>
          {layer === "base" ? (
            base && (
              <button className="button ghost" onClick={() => setBase(defaultBinding())}>
                {L("Clear", "Temizle")}
              </button>
            )
          ) : (
            own && (
              <button className="button ghost" onClick={() => setLayerBinding(null)}>
                {L("Use base mapping", "Temel atamaya dön")}
              </button>
            )
          )}
        </div>
        {isLayerButton ? (
          <p className="hint">{L("Holding this button activates the other buttons' layer mappings. Its own mapping is ignored. Change the layer button at the top of the page.", "Bu tuş katman tuşu. Basılıyken diğer tuşlar katman atamasını kullanır; kendi ataması yok sayılır. Katman tuşunu sayfanın üstünden değiştirebilirsin.")}</p>
        ) : layer === "base" ? (
          <BindingEditor binding={base ?? defaultBinding()} onChange={setBase} />
        ) : noLayer ? (
          <p className="hint">{L("No layer button selected. Choose one at the top of the page.", "Katman tuşu seçilmedi. Sayfanın üstünden bir tuş seç.")}</p>
        ) : own ? (
          <BindingEditor binding={own} onChange={(b) => setLayerBinding(b)} />
        ) : (
          <div className="inherit">
            <p className="hint">
              {L("This layer uses the base mapping:", "Katmanda ayrı atama yok; temel atama kullanılır:")} <span className="muted-text">{summary(base)}</span>
            </p>
            <div className="button-row">
              <button className="button" onClick={() => setLayerBinding(defaultBinding())}>
                {L("Add layer mapping", "Katmanda ayrı ata")}
              </button>
            </div>
          </div>
        )}
      </section>
    </div>
  );
}

export function Mapping() {
  const { draft, edit } = useApp();
  const [tab, setTab] = useState<Tab>("buttons");
  if (!draft) return <div className="page"><PageHead title={L("Mapping", "Tuşlar")} /></div>;
  const editMapping: EditMapping = (fn) => edit((p) => ({ ...p, mapping: fn(p.mapping) }));

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
          </button>
        ))}
      </div>
      {tab === "buttons" && <ButtonsTab draft={draft} editMapping={editMapping} />}
      {tab === "touchpad" && <TouchpadTab draft={draft} editMapping={editMapping} />}
      {tab === "gyro" && <GyroMouseTab draft={draft} editMapping={editMapping} />}
      {tab === "sticks" && <StickMappingTab draft={draft} editMapping={editMapping} />}
      {tab === "macros" && <Macros draft={draft} />}
    </div>
  );
}
