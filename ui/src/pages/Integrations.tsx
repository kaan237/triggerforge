import { L, locale } from "../i18n";
import { useEffect, useState } from "react";
import {
  send, subscribe, type ForzaStatus, type IntegrationSettings, type IntegrationStatus, type TelemetryColor,
  type TelemetryRule, type TelemetrySignal, type TelemetryTarget, type TriggerConfig,
} from "../host";
import { clamp, deepEqual, defaultTelemetryRule, defaultTrigger, hexToRgb, modeDef, rgbToHex, triggerSummary, zoneFrequency } from "../model";
import { useApp } from "../state";
import { PageHead, Section, Toggle, fixed, pct } from "../ui";
import { TriggerEffectEditor, TriggerModeSelect } from "./Triggers";

const signals: Record<TelemetrySignal, string> = {
  get raceOn() { return L("Race active", "Yarış sürüyor"); }, get speedKmh() { return L("Speed (km/h)", "Hız (km/sa)"); }, get rpm() { return L("RPM", "Devir"); }, get gas() { return L("Throttle", "Gaz"); }, get brake() { return L("Brake", "Fren"); },
  get clutch() { return L("Clutch", "Debriyaj"); }, get handbrake() { return L("Handbrake", "El freni"); }, get gear() { return L("Gear", "Vites"); }, get steer() { return L("Steering", "Direksiyon"); },
  get slipFront() { return L("Front slip", "Ön kayma"); }, get slipRear() { return L("Rear slip", "Arka kayma"); }, get surface() { return L("Road vibration", "Yol titreşimi"); }, get rumbleStrip() { return L("Rumble strip", "Kasis"); },
  boost: "Turbo", abs: "ABS", get traction() { return L("Traction loss", "Patinaj"); },
};
const targets: Record<TelemetryTarget, string> = {
  get leftTrigger() { return L("Left trigger", "Sol tetik"); }, get rightTrigger() { return L("Right trigger", "Sağ tetik"); }, get lightbar() { return L("Light bar", "Işık çubuğu"); },
  get playerLeds() { return L("Player LEDs", "Oyuncu LED'leri"); }, get rumbleLeft() { return L("Left vibration", "Sol titreşim"); }, get rumbleRight() { return L("Right vibration", "Sağ titreşim"); },
};
const forzaStates: Record<ForzaStatus["state"], string> = {
  get off() { return L("Off", "Kapalı"); }, get listening() { return L("Waiting", "Bekliyor"); }, get receiving() { return L("Receiving data", "Veri geliyor"); },
  get portBusy() { return L("Port in use", "Port kullanımda"); }, get portConflict() { return L("Port conflict", "Port çakışıyor"); },
};
const forzaFormats: Record<NonNullable<ForzaStatus["format"]>, string> = {
  sled: "Sled", fm7: "FM7", fm: "FM", fh: "FH",
};
const releaseTimes = [
  { value: 0, get label() { return L("Never", "Hiçbir zaman"); } }, { value: 10, get label() { return L("10 sec", "10 sn"); } },
  { value: 30, get label() { return L("30 sec", "30 sn"); } }, { value: 60, get label() { return L("60 sec", "60 sn"); } },
];

const numberLabel = (value: number) => value.toLocaleString(locale(), { maximumSignificantDigits: 10 });
const isTrigger = (target: TelemetryTarget) => target === "leftTrigger" || target === "rightTrigger";
const colorHex = (color: TelemetryColor | null) => color ? rgbToHex(color.r, color.g, color.b) : "#000000";

function PortField({ label, port, onSave, onInput }: {
  label: string; port: number; onSave: (port: number) => void; onInput?: (port: number) => void;
}) {
  const [input, setInput] = useState<string>();
  return (
    <form className="row-field" onSubmit={(e) => {
      e.preventDefault();
      if (input) onSave(Number(input));
      setInput(undefined);
    }}>
      <label className="field-inline">
        {label}
        <input type="number" min={1024} max={65535} required value={input ?? port}
          onChange={(e) => {
            setInput(e.target.value);
            onInput?.(e.target.valueAsNumber);
          }} />
      </label>
      <button className="button" type="submit" disabled={!input || Number(input) === port}>{L("Apply", "Uygula")}</button>
    </form>
  );
}

function Connections({ status }: { status: IntegrationStatus | null }) {
  const { settings } = useApp();
  const [forzaInput, setForzaInput] = useState<number>();
  if (!settings) return null;
  const s = settings.integration;
  const save = (patch: Partial<IntegrationSettings>) => send({ type: "settings.save", settings: { ...settings, integration: { ...s, ...patch } } });
  const udp = status?.udp;
  const forza = status?.forza;
  const values = forza?.state === "receiving" ? forza.values : null;
  const forzaPort = forzaInput ?? s.forzaPort;
  const udpLabel = !udp ? L("Waiting…", "Bekleniyor…") : udp.state === "off" ? L("Off", "Kapalı")
    : udp.state === "portBusy" ? L("Port in use", "Port kullanımda") : L(`Listening on :${udp.port}`, `Dinliyor :${udp.port}`);

  return (
    <>
      <Section title="UDP API">
        <Toggle label="UDP API" checked={s.udpEnabled} onChange={(udpEnabled) => save({ udpEnabled })}
          hint={L("Other apps and DSX mods can control triggers, lights, and vibration. Only commands from this PC are accepted.", "Başka programlar ve DSX modları tetik, ışık ve titreşimi değiştirebilir. Yalnız bu bilgisayardan gelen komutlar kabul edilir.")} />
        <PortField label={L("UDP port", "UDP portu")} port={s.udpPort} onSave={(udpPort) => save({ udpPort })} />
        <Toggle label={L("DSX mod compatibility", "DSX modlarıyla uyumlu")} checked={s.dsx} onChange={(dsx) => save({ dsx })} />
        <Toggle label={L("Write DSX port file", "DSX port dosyasını yaz")} checked={s.dsxPortFile} onChange={(dsxPortFile) => save({ dsxPortFile })}
          hint={L(String.raw`Mods read the port from a file in C:\Temp\DualSenseX. Turn this off when using DSX.`, String.raw`Modlar portu C:\Temp\DualSenseX içindeki dosyadan okur. Gerçek DSX kullanırken kapatın.`)} />
        <div className="row-field">
          <label className="field-inline">
            {L("Release when idle", "Sessiz kalınca bırak")}
            <select value={s.releaseAfterSeconds} onChange={(e) => save({ releaseAfterSeconds: Number(e.target.value) })}>
              {!releaseTimes.some(({ value }) => value === s.releaseAfterSeconds) &&
                <option value={s.releaseAfterSeconds}>{s.releaseAfterSeconds} {L("sec", "sn")}</option>}
              {releaseTimes.map(({ value, label }) => <option key={value} value={value}>{label}</option>)}
            </select>
          </label>
        </div>
        <p className={udp?.state === "portBusy" ? "small warn" : "small muted-text"}>{udpLabel}</p>
        {udp && <p className="hint num">
          {udp.packets} {L("packets", "paket")}
          {udp.format && <> · {udp.format === "dsx" ? "DSX" : L("TriggerForge", "Tactile")}</>}
          {udp.lastPacketAgoMs !== null && <> {L("· Last command", "· Son komut")} {numberLabel(Math.floor(udp.lastPacketAgoMs / 1000))} {L("sec ago", "sn önce")}</>}
        </p>}
        <div className="button-row">
          <button className="button" disabled={!udp?.holding} onClick={() => send({ type: "integration.release" })}>{L("Release effects", "Efektleri bırak")}</button>
        </div>
      </Section>

      <Section title="Forza">
        <Toggle label={L("Forza telemetry", "Forza telemetrisi")} checked={s.forzaEnabled} onChange={(forzaEnabled) => save({ forzaEnabled })} />
        <PortField label={L("Forza port", "Forza portu")} port={s.forzaPort} onInput={(port) => setForzaInput(Number.isFinite(port) ? port : undefined)}
          onSave={(forzaPort) => { save({ forzaPort }); setForzaInput(undefined); }} />
        <p className="hint">{L("In Forza, go to Settings > HUD and Gameplay > Data Out. Set the IP to 127.0.0.1 and use this port. Avoid ports 5200–5300.", "Oyunda Ayarlar > HUD ve Oynanış > Data Out: IP 127.0.0.1, port bu sayı. 5200-5300 arası kullanılmasın.")}</p>
        {forzaPort >= 5200 && forzaPort <= 5300 && <p className="hint warn">{L("Avoid ports 5200–5300.", "5200-5300 arası kullanılmasın.")}</p>}
        {forzaPort === s.udpPort && <p className="hint warn">{L("The Forza and UDP ports must differ.", "Forza portu UDP portuyla aynı olamaz.")}</p>}
        <p className={forza?.state === "portBusy" || forza?.state === "portConflict" ? "small warn" : "small muted-text"}>
          {forza ? forzaStates[forza.state] : L("Waiting…", "Bekleniyor…")}
          {forza?.state === "receiving" && forza.format && ` (${forzaFormats[forza.format]})`}
        </p>
        {values && <dl className="kv compact">
          <dt>{signals.speedKmh}</dt><dd className="num">{fixed(1)(values.speedKmh)}</dd>
          <dt>{signals.rpm}</dt><dd className="num">{pct(values.rpm)}</dd>
          <dt>{signals.gas}</dt><dd className="num">{pct(values.gas)}</dd>
          <dt>{signals.brake}</dt><dd className="num">{pct(values.brake)}</dd>
          <dt>{signals.gear}</dt><dd className="num">{values.gear}</dd>
          <dt>{signals.slipFront}</dt><dd className="num">{fixed(2)(values.slipFront)}</dd>
          <dt>{signals.slipRear}</dt><dd className="num">{fixed(2)(values.slipRear)}</dd>
        </dl>}
      </Section>
    </>
  );
}

function RuleNumber({ label, value, min, max, onChange }: {
  label: string; value: number | null; min: number; max: number; onChange: (value: number | null) => void;
}) {
  const [text, setText] = useState<string>();
  return (
    <label className="field-inline">
      {label}
      <input type="number" step="any" min={min} max={max} value={text ?? value ?? ""}
        onChange={(e) => {
          setText(e.target.value);
          const number = e.target.valueAsNumber;
          if (Number.isFinite(number)) onChange(clamp(number, min, max));
          else if (!e.target.validity.badInput) onChange(null);
        }}
        onBlur={() => setText(undefined)} />
    </label>
  );
}

function signalRange(signal: TelemetrySignal): [number, number] {
  const floatMax = 3.4028234663852886e38;
  switch (signal) {
    case "speedKmh": return [0, Math.sqrt(3) * floatMax * 3.6];
    case "slipFront": case "slipRear": case "surface": return [0, floatMax];
    case "boost": return [-floatMax, floatMax];
    case "gear": return [0, 255];
    case "steer": return [-1, 1];
    default: return [0, 1];
  }
}

function triggerParams(config: TriggerConfig) {
  if (config.mode === "zonesVibration") return [{ ...zoneFrequency, min: 0, max: 255 }];
  return modeDef(config.mode).params.map((param) => {
    if (param.key === "frequency" || param.key === "period") return { ...param, min: 0, max: 255 };
    if (["strength", "amplitude", "snap"].includes(param.key)) return { ...param, min: 0 };
    return param;
  });
}

function RuleEditor({ rule, onChange }: { rule: TelemetryRule; onChange: (rule: TelemetryRule) => void }) {
  const set = (patch: Partial<TelemetryRule>) => onChange({ ...rule, ...patch });
  const trigger = isTrigger(rule.target) ? rule.trigger ?? defaultTrigger() : null;
  const params = trigger ? triggerParams(trigger) : [];
  const param = params.find(({ key }) => key === rule.param);
  const [inputMin, inputMax] = signalRange(rule.signal);
  const outputMin = param?.min ?? 0;
  const outputMax = param?.max ?? (rule.target === "playerLeds" ? 5 : 255);

  return (
    <div className="detail-section integration-rule-editor">
      <div className="row-field">
        <label className="field-inline">
          {L("Signal", "Sinyal")}
          <select value={rule.signal} onChange={(e) => {
            const signal = e.target.value as TelemetrySignal;
            const [min, max] = signalRange(signal);
            set({ signal, from: clamp(rule.from, min, max), to: clamp(rule.to, min, max) });
          }}>
            {Object.entries(signals).map(([id, label]) => <option key={id} value={id}>{label}</option>)}
          </select>
        </label>
        <label className="field-inline">
          {L("Target", "Hedef")}
          <select value={rule.target} onChange={(e) => {
            const target = e.target.value as TelemetryTarget;
            if (isTrigger(target) && trigger) set({ target });
            else set({ target, trigger: isTrigger(target) ? defaultTrigger() : null, param: null, min: 0, max: 0 });
          }}>
            {Object.entries(targets).map(([id, label]) => <option key={id} value={id}>{label}</option>)}
          </select>
        </label>
      </div>
      <div className="row-field">
        <RuleNumber label={L("Start", "Başlangıç")} value={rule.from} min={inputMin} max={inputMax} onChange={(from) => from !== null && set({ from })} />
        <RuleNumber label={L("End", "Bitiş")} value={rule.to} min={inputMin} max={inputMax} onChange={(to) => to !== null && set({ to })} />
        <RuleNumber label={L("Curve exponent", "Eğri üssü")} value={rule.exponent} min={0.2} max={5} onChange={(exponent) => exponent !== null && set({ exponent })} />
      </div>
      {trigger && (
        <>
          <div className="row-field">
            <TriggerModeSelect config={trigger} onChange={(config) => set({ trigger: config, param: null, min: 0, max: 0 })} />
            <label className="field-inline">
              {L("Trigger parameter", "Tetik parametresi")}
              <select value={rule.param ?? ""} onChange={(e) => {
                const field = params.find(({ key }) => key === e.target.value);
                set({ param: field?.key ?? null, min: field?.min ?? 0, max: field?.max ?? 0 });
              }}>
                <option value="">{L("Fixed preset", "Sabit şablon")}</option>
                {params.map(({ key, label }) => <option key={key} value={key}>{label}</option>)}
              </select>
            </label>
          </div>
          <div className="panel graph-panel">
            <TriggerEffectEditor config={trigger} onChange={(trigger) => set({ trigger })} />
          </div>
        </>
      )}
      {rule.target === "lightbar" ? (
        <div className="row-field">
          {([{ key: "colorFrom", label: L("Start color", "Başlangıç rengi") }, { key: "colorTo", label: L("End color", "Bitiş rengi") }] as const).map(({ key, label }) => (
            <label key={key} className="field-inline">
              {label}
              <input type="color" value={colorHex(rule[key])} onChange={(e) => {
                const [r, g, b] = hexToRgb(e.target.value);
                set({ [key]: { r, g, b } });
              }} />
            </label>
          ))}
          <RuleNumber label={L("Flash threshold", "Yanıp sönme eşiği")} value={rule.blinkAt} min={0} max={1} onChange={(blinkAt) => set({ blinkAt })} />
        </div>
      ) : (!trigger || param) && (
        <div className="row-field">
          <RuleNumber label={L("Minimum output", "En düşük çıkış")} value={rule.min} min={outputMin} max={outputMax} onChange={(min) => min !== null && set({ min })} />
          <RuleNumber label={L("Maximum output", "En yüksek çıkış")} value={rule.max} min={outputMin} max={outputMax} onChange={(max) => max !== null && set({ max })} />
        </div>
      )}
    </div>
  );
}

function ruleEffect(rule: TelemetryRule) {
  const range = rule.to <= rule.from ? numberLabel(rule.max) : `${numberLabel(rule.min)} → ${numberLabel(rule.max)}`;
  if (isTrigger(rule.target) && rule.trigger) {
    const param = triggerParams(rule.trigger).find(({ key }) => key === rule.param);
    return `${triggerSummary(rule.trigger)} · ${param ? `${param.label} ${range}` : L("Fixed preset", "Sabit şablon")}`;
  }
  if (rule.target === "lightbar") {
    const colors = rule.to <= rule.from ? colorHex(rule.colorTo) : `${colorHex(rule.colorFrom)} → ${colorHex(rule.colorTo)}`;
    return rule.blinkAt === null ? colors : L(`${colors} · Flash at ${pct(rule.blinkAt)}`, `${colors} · Yanıp sönme ${pct(rule.blinkAt)}`);
  }
  return range;
}

function Rules() {
  const { profiles, activeId, telemetryDrafts, setTelemetryDraft } = useApp();
  const [selectedId, setSelectedId] = useState("");
  const [editing, setEditing] = useState<{ profileId: string; index: number }>();
  const [confirm, setConfirm] = useState("");
  const profile = profiles.find((p) => p.id === selectedId) ?? profiles.find((p) => p.id === activeId) ?? profiles[0];
  if (!profile) return null;
  const rules = telemetryDrafts.get(profile.id) ?? profile.telemetry.rules;
  const dirty = !deepEqual(rules, profile.telemetry.rules);
  const index = editing?.profileId === profile.id ? editing.index : -1;
  const selectRule = (index: number) => setEditing({ profileId: profile.id, index });
  const update = (next: TelemetryRule[]) => setTelemetryDraft(profile.id, next);
  const forget = () => setTelemetryDraft(profile.id);
  const move = (from: number, to: number) => {
    const next = [...rules];
    [next[from], next[to]] = [next[to], next[from]];
    update(next);
    selectRule(to);
  };

  return (
    <Section title={L("Telemetry rules", "Telemetri kuralları")}>
      <div className="row-field">
        <label className="field-inline">
          {L("Profile", "Profil")}
          <select value={profile.id} onChange={(e) => {
            setSelectedId(e.target.value);
            setEditing(undefined);
            setConfirm("");
          }}>
            {profiles.map((p) => <option key={p.id} value={p.id}>{p.name}{p.id === activeId ? L(" (active)", " (etkin)") : ""}</option>)}
          </select>
        </label>
      </div>
      <p className="hint">{L("Later rules take priority. Trigger effects sent by the game override these rules.", "Kurallarda sonraki kural kazanır. Oyun kendi tetik efektini gönderirse oyununki geçerli olur.")}</p>
      {rules.length === 0 && <p className="hint">{L("No telemetry rules in this profile.", "Bu profilde telemetri kuralı yok.")}</p>}
      {rules.length > 0 && <ol className="integration-rules">
        {rules.map((rule, i) => (
          <li key={i}>
            <div className="row-field">
              <span className="small">
                {signals[rule.signal]} {numberLabel(rule.from)} → {numberLabel(rule.to)} · {targets[rule.target]} · {ruleEffect(rule)}
              </span>
              <span className="spacer" />
              <button className="button ghost" aria-expanded={i === index} onClick={() => selectRule(i === index ? -1 : i)}>{L("Edit", "Düzenle")}</button>
              <button className="button ghost" aria-label={L(`Move rule ${i + 1} up`, `${i + 1}. kuralı yukarı taşı`)} disabled={i === 0} onClick={() => move(i, i - 1)}>↑</button>
              <button className="button ghost" aria-label={L(`Move rule ${i + 1} down`, `${i + 1}. kuralı aşağı taşı`)} disabled={i === rules.length - 1} onClick={() => move(i, i + 1)}>↓</button>
              <button className="button ghost" onClick={() => {
                update(rules.filter((_, j) => i !== j));
                selectRule(i === index ? -1 : i < index ? index - 1 : index);
              }}>{L("Remove", "Kaldır")}</button>
            </div>
            {i === index && <RuleEditor key={profile.id} rule={rule} onChange={(updated) => update(rules.map((r, j) => j === i ? updated : r))} />}
          </li>
        ))}
      </ol>}
      <div className="button-row integration-actions">
        <button className="button" disabled={rules.length >= 32} onClick={() => {
          update([...rules, defaultTelemetryRule()]);
          selectRule(rules.length);
        }}>{L("Add rule", "Kural ekle")}</button>
        <button className="button" onClick={() => setConfirm(profile.id)}>{L("Apply Forza template", "Forza şablonunu uygula")}</button>
        <span className="spacer" />
        {dirty && <>
          <span className="unsaved">{L("Unsaved", "Kaydedilmedi")}</span>
          <button className="button ghost" onClick={() => { forget(); setEditing(undefined); }}>{L("Revert", "Geri al")}</button>
          <button className="button primary" onClick={() => {
            send({ type: "profile.save", profile: { ...profile, telemetry: { rules } } });
            forget();
          }}>{L("Save", "Kaydet")}</button>
        </>}
      </div>
      {confirm === profile.id && (
        <div className="button-row integration-actions">
          <span className="small warn">{L("This will replace the profile's rules.", "Bu profilin kuralları değiştirilecek.")}</span>
          <button className="button ghost" onClick={() => setConfirm("")}>{L("Cancel", "Vazgeç")}</button>
          <button className="button primary" onClick={() => {
            send({ type: "telemetry.applyTemplate", profileId: profile.id, template: "forza" });
            forget();
            setEditing(undefined);
            setConfirm("");
          }}>{L("Apply", "Uygula")}</button>
        </div>
      )}
    </Section>
  );
}

export function Integrations() {
  const [status, setStatus] = useState<IntegrationStatus | null>(null);
  useEffect(() => {
    const unsubscribe = subscribe((message) => {
      if (message.type === "integration") setStatus(message);
    });
    send({ type: "integration.status" });
    return unsubscribe;
  }, []);
  return (
    <div className="page">
      <PageHead title={L("Integrations", "Entegrasyonlar")} />
      <div className="integration-layout">
        <Connections status={status} />
        <Rules />
      </div>
    </div>
  );
}
