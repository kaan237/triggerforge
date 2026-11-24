import { L, isDs4 } from "../i18n";
import { useState } from "react";
import type { Binding, ButtonName, Direction, FlickStickConfig, GyroMouse, Profile, StickMapping, TouchpadMapping } from "../host";
import { ActionEditor, BindingEditor, KeyField } from "../MappingEditors";
import { bindingSummary, buttonDefs, directions, flickSnapModes, gyroModes, stickRingModes } from "../mapping";
import { clamp, defaultBinding, processStick, rawAxis } from "../model";
import { useApp, useInput } from "../state";
import { Check, Section, Segmented, Slider, Toggle, fixed, pct } from "../ui";
import type { EditMapping } from "./Mapping";

type TabProps = { draft: Profile; editMapping: EditMapping };
type CalibrationProps = TabProps & { countdown: number; onTestTurn: () => void };

const times = (v: number) => `${fixed(1)(v)}×`;

// ---- touchpad ----

const zoneNames = { get halves() { return [L("Left half", "Sol yarı"), L("Right half", "Sağ yarı")]; }, get quadrants() { return [L("Top left", "Sol üst"), L("Top right", "Sağ üst"), L("Bottom left", "Sol alt"), L("Bottom right", "Sağ alt")]; } } as const;

function PadDiagram({ tp, selected, onSelect }: { tp: TouchpadMapping; selected: number; onSelect: (i: number) => void }) {
  const input = useInput();
  const { profiles, draft, status } = useApp();
  const padHeight = isDs4(status) ? 942 : 1080;
  const W = 192;
  const H = 108;
  const quad = tp.zoneLayout === "quadrants";
  const rects = quad
    ? [
        [0, 0],
        [W / 2, 0],
        [0, H / 2],
        [W / 2, H / 2],
      ].map(([x, y]) => ({ x, y, w: W / 2, h: H / 2 }))
    : [0, W / 2].map((x) => ({ x, y: 0, w: W / 2, h: H }));
  return (
    <svg viewBox={`-1 -1 ${W + 2} ${H + 2}`} className="pad-diagram" role="img" aria-label={L("Touchpad zones", "Touchpad bölgeleri")}>
      {rects.map((r, i) => (
        <g key={i} onClick={() => onSelect(i)} className={selected === i ? "zone sel" : "zone"}>
          <rect x={r.x} y={r.y} width={r.w} height={r.h} />
          <text x={r.x + 6} y={r.y + 12} className="zone-name">
            {zoneNames[tp.zoneLayout][i]}
          </text>
          <text x={r.x + 6} y={r.y + 23} className="zone-sum">
            {truncate(bindingSummary(tp.zones[i], profiles, draft?.macros ?? []), quad ? 20 : 30)}
          </text>
        </g>
      ))}
      {input?.touch.map((t) => <circle key={t.id} cx={(t.x / 1920) * W} cy={(t.y / padHeight) * H} r="3.5" className="finger" />)}
    </svg>
  );
}

const truncate = (s: string, n: number) => (s.length > n ? s.slice(0, n - 1) + "…" : s);

export function TouchpadTab({ draft, editMapping }: TabProps) {
  const tp = draft.mapping.touchpad;
  const [zone, setZone] = useState(0);
  const set = (patch: Partial<TouchpadMapping>) => editMapping((m) => ({ ...m, touchpad: { ...m.touchpad, ...patch } }));
  const setLayout = (zoneLayout: TouchpadMapping["zoneLayout"]) => {
    // halves → quadrants repeats left/right on the bottom row; quadrants → halves keeps the top row.
    const zones = zoneLayout === "quadrants" ? [0, 1, 0, 1].map((i) => ({ ...(tp.zones[i] ?? defaultBinding()) })) : tp.zones.slice(0, 2);
    set({ zoneLayout, zones });
    setZone(0);
  };
  const setZoneBinding = (i: number, b: Binding) => set({ zones: tp.zones.map((z, j) => (j === i ? b : z)) });
  const z = Math.min(zone, tp.zones.length - 1);

  return (
    <div className="tab-grid">
      <Section title={L("Touchpad", "Touchpad")} className="tab-main">
        <div className="row-field">
          <span className="label">{L("Mode", "Kullanım")}</span>
          <Segmented
            label={L("Touchpad mode", "Touchpad kullanımı")}
            value={tp.mode}
            onChange={(mode) => set({ mode })}
            options={[
              { id: "off", label: L("Off", "Kapalı") },
              { id: "mouse", label: L("Mouse", "Fare") },
              { id: "zones", label: L("Zones", "Bölgeler") },
            ]}
          />
        </div>
        <p className="hint mode-hint">
          {tp.mode === "off" && L("The touchpad acts as a click button using the Touchpad click mapping.", "Touchpad yalnız tıklama tuşu olarak çalışır (Tuşlar sekmesindeki Touchpad tık).")}
          {tp.mode === "mouse" && L("Sliding moves the pointer. Clicking uses the Touchpad click mapping.", "Parmak imleci hareket ettirir. Pad tıklaması Tuşlar sekmesindeki Touchpad tık atamasını kullanır.")}
          {tp.mode === "zones" && L("Clicking uses the mapping for the zone under your finger.", "Pad tıklaması parmağın olduğu bölgenin atamasını çalıştırır; Touchpad tık ataması yok sayılır.")}
        </p>
        {tp.mode === "mouse" && (
          <div className="tab-controls">
            <Slider label={L("Sensitivity", "Hassasiyet")} min={0.1} max={5} step={0.1} value={tp.sensitivity} format={times} onChange={(sensitivity) => set({ sensitivity })} />
            <Slider label={L("Acceleration", "İvme")} min={0} max={2} step={0.05} value={tp.acceleration} format={fixed(2)} onChange={(acceleration) => set({ acceleration })} />
            <Toggle label={L("Tap to click", "Dokununca tıkla")} hint={L("Tap with one finger to left-click or two to right-click.", "Tek parmak dokunuş sol tık, iki parmak sağ tık.")} checked={tp.tapToClick} onChange={(tapToClick) => set({ tapToClick })} />
            <Toggle label={L("Two-finger scrolling", "İki parmakla kaydır")} checked={tp.scroll} onChange={(scroll) => set({ scroll })} />
            {tp.scroll && <Slider label={L("Scroll speed", "Kaydırma hızı")} min={0.1} max={5} step={0.1} value={tp.scrollSpeed} format={times} onChange={(scrollSpeed) => set({ scrollSpeed })} />}
          </div>
        )}
        {tp.mode === "zones" && (
          <div className="zones-edit">
            <div className="row-field">
              <span className="label">{L("Zones", "Bölgeler")}</span>
              <Segmented
                label={L("Zone layout", "Bölge düzeni")}
                value={tp.zoneLayout}
                onChange={setLayout}
                options={[
                  { id: "halves", label: L("Two halves", "İki yarı") },
                  { id: "quadrants", label: L("Four quadrants", "Dört çeyrek") },
                ]}
              />
            </div>
            <PadDiagram tp={tp} selected={z} onSelect={setZone} />
          </div>
        )}
      </Section>

      {tp.mode === "zones" && (
        <>
          <Section title={L(`Zone: ${zoneNames[tp.zoneLayout][z]}`, `Bölge: ${zoneNames[tp.zoneLayout][z]}`)}>
            <BindingEditor binding={tp.zones[z]} onChange={(b) => setZoneBinding(z, b)} />
          </Section>
          <Section title={L("Swipe gestures", "Kaydırma hareketleri")} className="tab-wide">
            <p className="hint">{L("Swipe quickly across more than a quarter of the pad. The action runs when you lift your finger.", "Tek parmağı hızla padin dörtte birinden fazla kaydır. Kalkınca çalışır.")}</p>
            <div className="swipe-list">
              {directions.map((d) => (
                <div key={d.id} className="bind-row">
                  <span className="label">{d.label}</span>
                  <ActionEditor action={tp.swipes[d.id]} onChange={(a) => set({ swipes: { ...tp.swipes, [d.id]: a } })} />
                </div>
              ))}
            </div>
          </Section>
        </>
      )}
    </div>
  );
}

// ---- gyro mouse ----

export function GyroMouseTab({ draft, editMapping, countdown, onTestTurn }: CalibrationProps) {
  const g = draft.mapping.gyroMouse;
  const input = useInput();
  const set = (patch: Partial<GyroMouse>) => editMapping((m) => ({ ...m, gyroMouse: { ...m.gyroMouse, ...patch } }));
  const needsButton = g.mode === "whileHeld" || g.mode === "whileReleased" || g.mode === "toggle";
  const f = (n: number) => (n >= 0 ? "+" : "−") + Math.abs(n).toFixed(1).replace(".", L(".", ","));

  return (
    <div className="tab-grid">
      <Section title={L("Gyro mouse", "Gyro fare")} className="tab-main">
        <label className="row-field">
          <span className="label">{L("Activation", "Çalışma")}</span>
          <select value={g.mode} onChange={(e) => {
            const mode = gyroModes.find(({ id }) => id === e.target.value);
            if (mode) set({ mode: mode.id });
          }}>
            {gyroModes.map(({ id, label }) => <option key={id} value={id}>{label}</option>)}
          </select>
        </label>
        {g.mode !== "whileTouchpad" && <label className="row-field">
          <span className="label">{L("Key", "Tuş")}</span>
          <select value={g.button} disabled={!needsButton} onChange={(e) => set({ button: e.target.value as ButtonName })}>
            {buttonDefs.map((b) => (
              <option key={b.id} value={b.id}>
                {b.label}
              </option>
            ))}
          </select>
          {needsButton && <span className="hint">{L("The button also runs its own mapping.", "Tuş kendi atamasını da çalıştırır.")}</span>}
        </label>}
        <div className="row-field">
          <span className="label">{L("Horizontal axis", "Yatay eksen")}</span>
          <Segmented
            label={L("Horizontal axis", "Yatay eksen")}
            value={g.axis}
            onChange={(axis) => set({ axis })}
            options={[
              { id: "yaw", label: L("Turn left or right", "Sağa-sola çevir") },
              { id: "roll", label: L("Tilt sideways", "Yana yatır") },
            ]}
          />
        </div>
        <div className="tab-controls">
          <Toggle label={L("Use 360° calibration (1× = 1:1 turn)", "360° kalibrasyonunu kullan (1× = 1:1 dönüş)")} checked={g.useCalibration} onChange={(useCalibration) => set({ useCalibration })} />
          <Slider label={L("Horizontal speed", "Yatay hız")} min={0.1} max={10} step={0.1} value={g.sensitivityX} format={times} onChange={(sensitivityX) => set({ sensitivityX })} />
          <Slider label={L("Vertical speed", "Dikey hız")} min={0.1} max={10} step={0.1} value={g.sensitivityY} format={times} onChange={(sensitivityY) => set({ sensitivityY })} />
          <Slider label={L("Smoothing", "Yumuşatma")} min={0} max={0.9} step={0.01} value={g.smoothing} format={pct} onChange={(smoothing) => set({ smoothing })} />
          <Slider label={L("Deadzone", "Ölü bölge")} min={0} max={5} step={0.1} value={g.deadzone} format={(v) => `${fixed(1)(v)}°/s`} onChange={(deadzone) => set({ deadzone })} />
          <div className="row-field">
            <span className="label">{L("Invert", "Ters çevir")}</span>
            <Check label={L("X", "X")} checked={g.invertX} onChange={(invertX) => set({ invertX })} />
            <Check label={L("Y", "Y")} checked={g.invertY} onChange={(invertY) => set({ invertY })} />
          </div>
        </div>
      </Section>
      <Section title={L("Live", "Canlı")}>
        <dl className="kv compact">
          <dt>{L("Gyro °/s", "Gyro °/sn")}</dt>
          <dd className="num">{input ? input.gyro.map(f).join("  ") : "—"}</dd>
        </dl>
        {!g.useCalibration && <p className="hint mode-hint">{L("At 1×, each degree moves the pointer 10 pixels.", "1× hızda her derece imleci 10 piksel hareket ettirir.")}</p>}
        <p className="hint mode-hint">{L("If it drifts, calibrate the gyro on the Sticks page.", "Kayma varsa Analoglar sayfasından gyroyu kalibre et.")}</p>
      </Section>
      <Section title={L("360° calibration", "360° kalibrasyon")} className="tab-wide">
        <CalibrationControls draft={draft} editMapping={editMapping} countdown={countdown} onTestTurn={onTestTurn} />
      </Section>
    </div>
  );
}

// ---- sticks ----

const keyPresets: { name: string; keys: Record<Direction, string> }[] = [
  { get name() { return L("WASD", "WASD"); }, keys: { up: "w", down: "s", left: "a", right: "d" } },
  { get name() { return L("Arrow keys", "Ok tuşları"); }, keys: { up: "up", down: "down", left: "left", right: "right" } },
  { get name() { return L("Numpad", "Sayısal tuş takımı"); }, keys: { up: "numpad8", down: "numpad2", left: "numpad4", right: "numpad6" } },
];

function StickPreview({ side, cfg, flickEnabled }: { side: "left" | "right"; cfg: StickMapping; flickEnabled: boolean }) {
  const { draft, status, dirty, activeId } = useApp();
  const input = useInput();
  const R = 50;
  const pos = status?.connected ? input?.[side] : undefined;
  const hostProcessed = side === "left" ? input?.leftProcessed : input?.rightProcessed;
  const [x, y] = pos && draft
    ? (!dirty && draft.id === activeId && hostProcessed) || processStick(rawAxis(pos[0]), rawAxis(pos[1]), draft.sticks[side])
    : [0, 0];
  const t = cfg.threshold * R;
  const keys = cfg.mode === "keys" && !flickEnabled;
  const lit = (d: Direction) => keys && ((d === "up" && y < -cfg.threshold) || (d === "down" && y > cfg.threshold) || (d === "left" && x < -cfg.threshold) || (d === "right" && x > cfg.threshold));
  return (
    <svg viewBox="-60 -60 120 120" className="stick-preview" role="img" aria-label={L("Stick threshold", "Analog eşiği")}>
      <circle r={R} className="rim" />
      {cfg.ringMode !== "off" && <circle r={cfg.ringThreshold * R} className="thr" />}
      {keys && <rect x={-t} y={-t} width={t * 2} height={t * 2} className="thr" />}
      {keys &&
        (["up", "down", "left", "right"] as Direction[]).map((d) => {
          const [cx, cy] = { up: [0, -R - 5], down: [0, R + 5], left: [-R - 5, 0], right: [R + 5, 0] }[d];
          return <circle key={d} cx={cx} cy={cy} r="3" className={lit(d) ? "dir on" : "dir"} />;
        })}
      <circle cx={clamp(x, -1, 1) * R} cy={clamp(y, -1, 1) * R} r="4" className="proc-dot" />
    </svg>
  );
}

function StickMappingPanel({ side, cfg, flickEnabled, onChange }: { side: "left" | "right"; cfg: StickMapping; flickEnabled: boolean; onChange: (c: StickMapping) => void }) {
  const set = (patch: Partial<StickMapping>) => onChange({ ...cfg, ...patch });
  const preset = keyPresets.find((p) => directions.every((d) => p.keys[d.id] === cfg.keys[d.id]));
  return (
    <Section title={side === "left" ? L("Left stick", "Sol analog") : L("Right stick", "Sağ analog")}>
      <div className="row-field">
        <Segmented
          label={L("Stick mode", "Analog kullanımı")}
          value={cfg.mode}
          onChange={(mode) => set({ mode })}
          options={[
            { id: "off", label: L("Off", "Kapalı") },
            { id: "keys", label: L("Mapping", "Tuşlar") },
            { id: "mouse", label: L("Mouse", "Fare") },
            { id: "scroll", label: L("Scroll", "Kaydırma") },
          ]}
        />
      </div>
      <div className="stick-map">
        <StickPreview side={side} cfg={cfg} flickEnabled={flickEnabled} />
        <div className="stick-map-controls">
          {flickEnabled ? <p className="hint">{L("Flick Stick handles the right stick. Ring actions still work.", "Sağ analoğu Flick Stick yönetir. Halka eylemleri çalışmaya devam eder.")}</p>
            : cfg.mode === "off" && cfg.ringMode === "off" && <p className="hint">{L("Stick input goes to the game only.", "Analog yalnız oyuna gider.")}</p>}
          {cfg.mode === "keys" && (
            <>
              <div className="row-field">
                <span className="label">{L("Ready", "Hazır")}</span>
                <div className="button-group">
                  {keyPresets.map((p) => (
                    <button key={p.name} className={preset === p ? "button on" : "button"} onClick={() => set({ keys: { ...p.keys } })}>
                      {p.name}
                    </button>
                  ))}
                </div>
              </div>
              {directions.map((d) => (
                <div key={d.id} className="row-field dir-key">
                  <span className="label">{d.label}</span>
                  <KeyField compact max={1} value={cfg.keys[d.id] === "none" ? [] : [cfg.keys[d.id]]} onChange={(k) => set({ keys: { ...cfg.keys, [d.id]: k[0] ?? "none" } })} />
                  {cfg.keys[d.id] !== "none" && (
                    <button className="button ghost" onClick={() => set({ keys: { ...cfg.keys, [d.id]: "none" } })}>
                      {L("None", "Yok")}
                    </button>
                  )}
                </div>
              ))}
              <Slider label={L("Threshold", "Eşik")} min={0.1} max={0.9} step={0.05} value={cfg.threshold} format={pct} onChange={(threshold) => set({ threshold })} />
              <p className="hint">{L("Uses the stick value after deadzone and curve settings. Diagonals press two keys.", "Analog ayarları (ölü bölge, eğri) uygulandıktan sonraki değer. Çaprazda iki tuş birden basılır.")}</p>
            </>
          )}
          {(cfg.mode === "mouse" || cfg.mode === "scroll") && (
            <>
              <Slider label={L("Speed", "Hız")} min={0.1} max={5} step={0.1} value={cfg.speed} format={times} onChange={(speed) => set({ speed })} />
              <p className="hint">{cfg.mode === "mouse" ? L("At full tilt, 1,200 pixels per second × speed.", "Tam eğimde saniyede 1200 piksel × hız.") : L("At full tilt, 15 ticks per second × speed.", "Tam eğimde saniyede 15 çentik × hız.")}</p>
            </>
          )}
        </div>
      </div>
      <div className="tab-controls">
        <h3>{L("Stick ring", "Analog halkası")}</h3>
        <Segmented label={L("Ring mode", "Halka modu")} value={cfg.ringMode} options={stickRingModes} onChange={(ringMode) => set({ ringMode })} />
        {cfg.ringMode !== "off" && (
          <>
            <Slider label={L("Ring threshold", "Halka eşiği")} min={0.2} max={0.98} step={0.01} value={cfg.ringThreshold} format={pct} onChange={(ringThreshold) => set({ ringThreshold })} />
            <Slider label={L("Ring hysteresis", "Halka bırakma farkı")} min={0.01} max={0.2} step={0.01} value={cfg.ringHysteresis} format={pct} onChange={(ringHysteresis) => set({ ringHysteresis })} />
            <div className="bind-row"><span className="label">{L("Ring action", "Halka eylemi")}</span><ActionEditor action={cfg.ringAction} onChange={(ringAction) => set({ ringAction })} /></div>
          </>
        )}
      </div>
    </Section>
  );
}

function CalibrationControls({ draft, editMapping, countdown, onTestTurn }: CalibrationProps) {
  const { countsPer360 } = draft.mapping.flickStick;
  const setCounts = (counts: number) => editMapping((m) => ({
    ...m, flickStick: { ...m.flickStick, countsPer360: clamp(counts, 500, 30000) },
  }));
  return (
    <div className="tab-controls">
      <p className="hint">{L("Pick a landmark in the game. After each test, adjust the count until the view returns to that landmark.", "Oyunda bir referans noktası seç. Her denemeden sonra görüntü aynı noktaya dönene kadar sayımı ayarla.")}</p>
      <Slider label={L("Mouse counts per 360°", "360° başına fare sayımı")} min={500} max={30000} value={countsPer360} onChange={setCounts} />
      <div className="button-group">
        {[-100, -10, 10, 100].map((step) => {
          const label = step > 0 ? `+${step}` : `${step}`;
          const next = clamp(countsPer360 + step, 500, 30000);
          return <button key={step} className="button" disabled={next === countsPer360} onClick={() => setCounts(next)}>{L(label, label)}</button>;
        })}
        <button className="button" disabled={countdown > 0} onClick={onTestTurn}>{L("Test 360° turn (2s)", "360° dönüşü dene (2 sn)")}</button>
        <span role="status" className="small">{countdown > 0 && L(`Focus the game · ${countdown}s`, `Oyuna geç · ${countdown} sn`)}</span>
      </div>
    </div>
  );
}

export function StickMappingTab({ draft, editMapping, countdown, onTestTurn }: CalibrationProps) {
  const f = draft.mapping.flickStick;
  const set = (patch: Partial<FlickStickConfig>) => editMapping((m) => ({ ...m, flickStick: { ...m.flickStick, ...patch } }));
  return (
    <div className="sticks-grid">
      {(["left", "right"] as const).map((side) => {
        const panel = <StickMappingPanel side={side} cfg={draft.mapping.sticks[side]} flickEnabled={side === "right" && f.enabled} onChange={(c) => editMapping((m) => ({ ...m, sticks: { ...m.sticks, [side]: c } }))} />;
        return (
          <div key={side} className="tab-controls">
            {panel}
            {side === "right" && (
              <Section title={L("Flick Stick and 360° calibration", "Flick Stick ve 360° kalibrasyon")}>
                <Toggle label={L("Enable Flick Stick", "Flick Stick’i aç")} checked={f.enabled} onChange={(enabled) => set({ enabled })} />
                <CalibrationControls draft={draft} editMapping={editMapping} countdown={countdown} onTestTurn={onTestTurn} />
                <Slider label={L("Flick threshold", "Hızlı dönüş eşiği")} min={0.5} max={0.98} step={0.01} value={f.threshold} format={pct} onChange={(threshold) => set({ threshold })} />
                <Slider label={L("Flick duration", "Hızlı dönüş süresi")} min={30} max={300} value={f.flickTimeMs} format={(ms) => L(`${ms} ms`, `${ms} ms`)} onChange={(flickTimeMs) => set({ flickTimeMs })} />
                <div className="row-field">
                  <span className="label">{L("Snap", "Yöne hizala")}</span>
                  <Segmented label={L("Snap", "Yöne hizala")} value={f.snap} options={flickSnapModes} onChange={(snap) => set({ snap })} />
                </div>
                <Slider label={L("Forward deadzone angle", "İleri yön ölü bölge açısı")} min={0} max={45} step={0.5} value={f.deadzoneAngle} format={(v) => `${fixed(1)(v)}°`} onChange={(deadzoneAngle) => set({ deadzoneAngle })} />
                <Slider label={L("Sweep smoothing", "Çevresel dönüş yumuşatma")} min={0} max={0.9} step={0.01} value={f.smoothing} format={pct} onChange={(smoothing) => set({ smoothing })} />
              </Section>
            )}
          </div>
        );
      })}
    </div>
  );
}
