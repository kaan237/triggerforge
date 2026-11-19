import { useEffect } from "react";
import { send, type ButtonName, type DriverKind, type DriverState, type DriverStatus, type EmulationConfig, type EmulationMode, type EmulationStatus } from "../host";
import { buttonDefs, buttonLabel } from "../mapping";
import { useApp } from "../state";
import { DraftBar, PageHead, Section, Segmented, Slider, Toggle } from "../ui";

const driverNames: Record<DriverKind, { name: string; role: string }> = {
  usbIp: { name: "usbip-win2", role: "Sanal kontrolcüyü Windows'a takar" },
  hidHide: { name: "HidHide", role: "Gerçek kontrolcüyü oyunlardan gizler" },
};

const stateLabel: Record<DriverState, string> = {
  ready: "Hazır",
  missing: "Kurulu değil",
  unsupported: "Desteklenmeyen sürüm",
  error: "Hata",
};

const modeLabel: Record<EmulationMode, string> = { off: "Kapalı", xbox360: "Xbox 360", dualSense: "DualSense" };
const audioLabel: Record<EmulationStatus["audio"], string> = { off: "Kapalı", ready: "Hazır", playing: "Oynatılıyor" };

function DriverCard({ driver }: { driver: DriverStatus }) {
  const { installs } = useApp();
  const progress = installs[driver.kind];
  const running = progress !== undefined && !progress.done;
  const n = driverNames[driver.kind];
  return (
    <div className="driver-card">
      <div className="driver-head">
        <span className={driver.state === "ready" ? "dot live" : "dot"} aria-hidden />
        <span className="driver-name">{n.name}</span>
        <span className={driver.state === "ready" ? "small muted-text" : "small warn"}>{stateLabel[driver.state]}</span>
        <span className="spacer" />
        {driver.version && <span className="small faint num">{driver.version}</span>}
      </div>
      <p className="hint">{n.role}</p>
      {driver.detail && <p className="hint">{driver.detail}</p>}
      {driver.state !== "ready" && (
        <div className="button-row">
          <button className="button" disabled={running} onClick={() => send({ type: "driver.install", kind: driver.kind })}>
            {running ? "Kuruluyor…" : driver.state === "unsupported" ? "Desteklenen sürümü kur" : "Kur"}
          </button>
        </div>
      )}
      {progress && <p className={progress.done && !progress.ok ? "small warn driver-progress" : "small muted-text driver-progress"}>{progress.message}</p>}
      {driver.kind === "usbIp" && driver.state !== "ready" && (
        <p className="hint">Kurulum sırasında USB 3 hub'ları kısa süre yeniden başlar; fare ve klavye bir an kopabilir.</p>
      )}
    </div>
  );
}

function LiveStatus({ e }: { e: EmulationStatus }) {
  const latency = e.latency.samples > 0 ? `${e.latency.p50Ms.toFixed(1).replace(".", ",")} / ${e.latency.p95Ms.toFixed(1).replace(".", ",")} ms` : "—";
  return (
    <dl className="kv emu-kv">
      <dt>Sanal</dt>
      <dd>{e.attached ? `${modeLabel[e.mode]} takılı` : "takılı değil"}</dd>
      <dt>Gerçek</dt>
      <dd>{e.hidden ? "oyunlardan gizli" : "görünür"}</dd>
      <dt>Ses arayüzü</dt>
      <dd>{audioLabel[e.audio]}</dd>
      <dt>Gecikme</dt>
      <dd className="num" title="Oyunun gönderdiği titreşim ve ışığın kontrolcüye yazılması, p50 / p95">
        {latency}
      </dd>
    </dl>
  );
}

function SystemPanel() {
  const { emulation, settings } = useApp();
  if (!settings) return null;
  const s = settings.emulation;
  const save = (patch: Partial<typeof s>) => send({ type: "settings.save", settings: { ...settings, emulation: { ...s, ...patch } } });
  const unavailable = emulation?.available === false;
  const needsReboot = emulation?.drivers.some((d) => d.state !== "ready");

  return (
    <div className="emu-side">
      <Section title="Durum">
        {emulation ? <LiveStatus e={emulation} /> : <p className="hint">Bekleniyor…</p>}
        {emulation?.error && (
          <div className="map-status">
            <span className="small warn">{emulation.error}</span>
            {!unavailable && (
              <button className="button" onClick={() => send({ type: "emulation.retry" })}>
                Yeniden dene
              </button>
            )}
          </div>
        )}
        <Toggle label="Sanal kontrolcü" hint="Kapalıyken hiçbir şey takılmaz ve gizlenmez" checked={s.enabled} onChange={(enabled) => save({ enabled })} />
        <Toggle
          label="Gerçek kontrolcüyü gizle"
          hint="Sanal kontrolcü takılıyken oyunlar yalnız onu görür; kapalıysa çift kontrolcü görünebilir"
          checked={s.hideController}
          onChange={(hideController) => save({ hideController })}
        />
        <div className="setting-row">
          <span className="toggle-text">
            <span>Kontrolcüyü geri getir</span>
            <span className="hint">Sanal kontrolcüyü kaldırır, gizlemeyi kapatır ve bu özelliği kapatır</span>
          </span>
          <button className="button" onClick={() => send({ type: "emulation.restore" })}>
            Geri getir
          </button>
        </div>
      </Section>

      <Section title="Ses ve haptik akışı">
        <Toggle
          label="Ses arayüzü (oyun haptiği ve hoparlörü)"
          hint="Bu ayar değiştiğinde sanal DualSense çıkarılıp yeniden takılır."
          checked={s.audio}
          onChange={(audio) => save({ audio })}
        />
        <Toggle
          label="Varsayılan ses cihazlarımı koru"
          hint="Sanal DualSense takılınca Windows sesi ve mikrofonu ona geçirmesin."
          checked={s.keepAudioDefaults}
          onChange={(keepAudioDefaults) => save({ keepAudioDefaults })}
        />
        <Slider label="Haptik tamponu" min={16} max={128} value={s.hapticBuffer} onChange={(hapticBuffer) => save({ hapticBuffer })} />
        <p className="hint">Yalnız haptik akışında küçük tampon gecikmeyi, büyük tampon kesintileri azaltır.</p>
      </Section>

      <Section title="Sürücüler">
        {unavailable && <p className="hint warn">Sanal kontrolcü bu sistemde başlatılamadı; diğer özellikler çalışmaya devam eder.</p>}
        <div className="driver-list">
          {(emulation?.drivers ?? []).map((d) => (
            <DriverCard key={d.kind} driver={d} />
          ))}
        </div>
        {needsReboot && <p className="hint">İki sürücü de kurulumdan sonra bilgisayarın yeniden başlatılmasını ister.</p>}
      </Section>
    </div>
  );
}

function RemapEditor({ config, onChange }: { config: EmulationConfig; onChange: (remap: EmulationConfig["remap"]) => void }) {
  const rows = Object.entries(config.remap) as [ButtonName, ButtonName | "none"][];
  const used = new Set(rows.map(([from]) => from));
  const free = buttonDefs.filter((b) => !used.has(b.id));

  const set = (from: ButtonName, to: ButtonName | "none", previous = from) => {
    const next: EmulationConfig["remap"] = {};
    // Keep row order when the source changes.
    for (const [k, v] of rows) {
      if (k === previous) next[from] = to;
      else next[k] = v;
    }
    onChange(next);
  };
  const remove = (from: ButtonName) => {
    const next = { ...config.remap };
    delete next[from];
    onChange(next);
  };
  const add = () => {
    const from = free[0]?.id;
    if (!from) return;
    onChange({ ...config.remap, [from]: from === "cross" ? "circle" : "cross" });
  };

  return (
    <div className="remap">
      {rows.length === 0 && <p className="hint">Tuşlar olduğu gibi gider. Bir tuşu başka bir tuşa ya da hiçbir şeye yönlendirmek için satır ekleyin.</p>}
      {rows.map(([from, to]) => (
        <div className="remap-row" key={from}>
          <select aria-label="Kaynak tuş" value={from} onChange={(e) => set(e.target.value as ButtonName, to, from)}>
            {buttonDefs
              .filter((b) => b.id === from || !used.has(b.id))
              .map((b) => (
                <option key={b.id} value={b.id}>
                  {b.label}
                </option>
              ))}
          </select>
          <span className="faint" aria-hidden>
            →
          </span>
          <select aria-label="Hedef tuş" value={to} onChange={(e) => set(from, e.target.value as ButtonName | "none")}>
            <option value="none">{buttonLabel("none")}</option>
            {buttonDefs.map((b) => (
              <option key={b.id} value={b.id}>
                {b.label}
              </option>
            ))}
          </select>
          <button className="button ghost" onClick={() => remove(from)}>
            Kaldır
          </button>
        </div>
      ))}
      <div className="button-row">
        <button className="button" disabled={free.length === 0} onClick={add}>
          Satır ekle
        </button>
      </div>
      {rows.some(([from]) => from === "l2" || from === "r2") && (
        <p className="hint">L2/R2 başka bir tetiğe giderse basınç değeri de gider; düz bir tuşa giderse tetik basılınca o tuş basılır.</p>
      )}
    </div>
  );
}

function ProfilePanel() {
  const { draft, edit, status } = useApp();
  if (!draft) return null;
  const c = draft.emulation;
  const set = (patch: Partial<EmulationConfig>) => edit((p) => ({ ...p, emulation: { ...p.emulation, ...patch } }));
  const usb = status?.connected && status.link === "Usb";

  return (
    <div className="emu-main">
      <Section title="Bu profilde">
        <div className="setting-row">
          <span className="toggle-text">
            <span>Oyunun gördüğü kontrolcü</span>
            <span className="hint">Kablolu bir kontrolcü olarak görünür; ışık, titreşim ve tetikler oyundan gelir</span>
          </span>
          <Segmented<EmulationMode>
            label="Sanal kontrolcü türü"
            value={c.mode}
            onChange={(mode) => set({ mode })}
            options={[
              { id: "off", label: "Kapalı" },
              { id: "xbox360", label: "Xbox 360" },
              { id: "dualSense", label: "DualSense", disabled: usb && c.mode !== "dualSense" },
            ]}
          />
        </div>
        {usb && (
          <p className="hint emu-note">Kontrolcü USB ile bağlı; oyunlar onu zaten kablolu DualSense olarak görür. DualSense kipi yalnız Bluetooth'ta çalışır, Xbox 360 kipi her iki bağlantıda da çalışır.</p>
        )}
        {c.mode !== "off" && (
          <p className="hint emu-note">Çevrim içi oyunların hile koruması sanal kontrolcülere izin vermeyebilir ya da hesabı işaretleyebilir. Çok oyunculu oyunlarda oyunun kurallarına bakın.</p>
        )}
        <Toggle label="Analogları yer değiştir" checked={c.swapSticks} onChange={(swapSticks) => set({ swapSticks })} />
        <Toggle
          label="Atanmış tuşları gizle"
          hint="Tuşlar sayfasında klavye/fareye atanan tuşlar sanal kontrolcüde basılmaz"
          checked={c.hideMappedButtons}
          onChange={(hideMappedButtons) => set({ hideMappedButtons })}
        />
      </Section>

      <Section title="Tuş yönlendirme">
        <RemapEditor config={c} onChange={(remap) => set({ remap })} />
      </Section>
    </div>
  );
}

export function Emulation() {
  useEffect(() => send({ type: "emulation.status" }), []);
  return (
    <div className="page emulation-page">
      <PageHead title="Sanal kontrolcü">
        <DraftBar />
      </PageHead>
      <div className="emu-grid">
        <ProfilePanel />
        <SystemPanel />
      </div>
    </div>
  );
}
