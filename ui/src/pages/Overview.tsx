import { L, locale, modelName } from "../i18n";
import { ControllerDrawing } from "../ControllerDrawing";
import { send, type ControllerStatus, type Profile } from "../host";
import { EdgeButtons } from "../Live";
import { brightnessLabel, effectLabel, ledMask, reasonLabel, triggerSummary } from "../model";
import { useApp, useInput } from "../state";
import { PageHead } from "../ui";

const linkLabel: Record<string, string> = { Bluetooth: "Bluetooth", Usb: "USB", get Simulated() { return L("Simulation", "Simülasyon"); } };

function ControllerCard({ controller, profiles }: { controller: ControllerStatus; profiles: Profile[] }) {
  const { index, model, link, battery, charging, primary, mac, profileId, profileName } = controller;
  const name = L(`Controller ${index + 1}`, `Kontrolcü ${index + 1}`);
  return (
    <section className="panel controller-card">
      <h2>{name}</h2>
      <div className="controller-tags">
        <span className={model === "DualSenseEdge" ? "chip" : undefined}>{modelName(model)}</span>
        {primary && <span className="chip on">{L("Primary", "Birincil")}</span>}
      </div>
      <dl className="kv">
        <dt>{L("Connection", "Bağlantı")}</dt>
        <dd>{linkLabel[link]}</dd>
        <dt>{L("Battery", "Pil")}</dt>
        <dd>{battery === null ? "—" : L(`${battery}%`, `%${battery}`)}{charging && <span className="faint"> · {L("charging", "şarjda")}</span>}</dd>
        <dt>{L("Profile", "Profil")}</dt>
        <dd>{profileName}</dd>
      </dl>
      {!primary && (
        <>
          <label className="controller-session">
            <span>{L("Session profile", "Oturum profili")}</span>
            <select value={profileId} disabled={!mac} onChange={(e) => mac && send({ type: "controller.setProfile", index, mac, profileId: e.target.value })}>
              {!profiles.some((p) => p.id === profileId) && <option value={profileId} disabled>{profileName || "—"}</option>}
              {profiles.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
            </select>
          </label>
          <button className="button" disabled={!mac} onClick={() => mac && send({ type: "controller.setPrimary", mac })}>{L("Make primary", "Birincil yap")}</button>
        </>
      )}
    </section>
  );
}

export function Overview() {
  const { status, output, profiles, activeId } = useApp();
  const input = useInput();
  const connected = status?.connected ?? false;
  const controllers = status?.controllers ?? [];
  const primaryMissing = !connected && controllers.length > 0;
  const edge = connected && status?.model === "DualSenseEdge";
  const active = profiles.find((p) => p.id === activeId);

  return (
    <div className="page overview">
      <PageHead title={L("Overview", "Kontrolcü")}>
        <span className="legend">
          <span className="key raw" /> {L("raw", "ham")}
          <span className="key proc" /> {L("processed", "işlenmiş")}
        </span>
      </PageHead>

      {primaryMissing && <p className="warn" role="status">{L("Primary controller not connected", "Birincil kontrolcü bağlı değil")}</p>}
      {(controllers.length >= 2 || primaryMissing) && (
        <div className="controller-cards">
          {controllers.map((controller) => (
            <ControllerCard key={controller.index} controller={controller} profiles={profiles} />
          ))}
        </div>
      )}
      <div className="overview-grid">
        <div className={connected ? "drawing" : "drawing idle"}>
          <ControllerDrawing input={connected ? input : null} output={connected ? output : null} />
          {edge && <EdgeButtons input={input} />}
          {!connected && !primaryMissing && <p className="drawing-note">{L("Waiting for a controller. Plug it in via USB or pair over Bluetooth.", "Kontrolcü bekleniyor. USB ile tak ya da Bluetooth ile eşleştir.")}</p>}
        </div>

        <aside className="info">
          <section className="info-block">
            <h2>{L("Device", "Cihaz")}</h2>
            <dl className="kv">
              <dt>Model</dt>
              <dd>{connected ? <span className={edge ? "chip" : undefined}>{modelName(status?.model)}</span> : L("Disconnected", "Bağlı değil")}</dd>
              <dt>{L("Connection", "Bağlantı")}</dt>
              <dd>{connected && status?.link ? linkLabel[status.link] ?? status.link : "—"}</dd>
              <dt>MAC</dt>
              <dd className="num">{connected ? status?.mac ?? "—" : "—"}</dd>
              <dt>Firmware</dt>
              <dd className="num">{connected ? status?.firmware ?? "—" : "—"}</dd>
              <dt>{L("Battery", "Pil")}</dt>
              <dd>
                {input ? (
                  <span className="battery">
                    <span className="battery-bar">
                      <span style={{ width: `${input.battery}%` }} className={input.battery <= 20 ? "low" : ""} />
                    </span>
                    <span className="num">{L(`${input.battery}%`, `%${input.battery}`)}</span>
                    {input.charging && <span className="faint">{L("charging", "şarjda")}</span>}
                  </span>
                ) : (
                  "—"
                )}
              </dd>
            </dl>
            {input && (
              <p className="small faint num">
                {input.reports.toLocaleString(locale())} {L("reports", "rapor")}
                {input.badCrc > 0 ? L(` · ${input.badCrc} bad CRC`, ` · ${input.badCrc} bozuk CRC`) : L(" · CRC OK", " · CRC temiz")}
              </p>
            )}
          </section>

          <section className="info-block">
            <h2>{L("Active profile", "Etkin profil")}</h2>
            <div className="active-profile">
              <select value={activeId} onChange={(e) => send({ type: "profile.activate", id: e.target.value })} aria-label={L("Active profile", "Etkin profil")}>
                {profiles.map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.name}
                  </option>
                ))}
              </select>
              <span className="reason">{reasonLabel(status?.activeReason)}</span>
            </div>
            {active && (
              <dl className="kv summary">
                <dt>{L("Light", "Işık")}</dt>
                <dd>
                  {active.lightbar.effect === "static" || active.lightbar.effect === "breathe" || active.lightbar.effect === "strobe" ? (
                    <span className="swatch" style={{ background: active.lightbar.color }} />
                  ) : null}
                  {effectLabel(active.lightbar.effect)}
                  <span className="faint"> · {brightnessLabel[active.lightbar.brightness].toLowerCase()}</span>
                </dd>
                <dt>L2</dt>
                <dd>{triggerSummary(active.triggers.left)}</dd>
                <dt>R2</dt>
                <dd>{triggerSummary(active.triggers.right)}</dd>
                <dt>{L("Player", "Oyuncu")}</dt>
                <dd>
                  <span className="mini-leds">
                    {[0, 1, 2, 3, 4].map((i) => (
                      <span key={i} className={(ledMask(active.playerLeds) >> i) & 1 ? "on" : ""} />
                    ))}
                  </span>
                </dd>
                <dt>{L("Stick", "Analog")}</dt>
                <dd className="num">
                  {(() => {
                    const l = Math.round(active.sticks.left.innerDeadzone * 100);
                    const r = Math.round(active.sticks.right.innerDeadzone * 100);
                    return L(`deadzone ${l}% / ${r}%`, `ölü bölge %${l} / %${r}`);
                  })()}
                </dd>
                <dt>{L("Games", "Oyunlar")}</dt>
                <dd className="games-line">{active.games.length ? active.games.join(", ") : <span className="faint">{L("manual only", "yalnız elle")}</span>}</dd>
              </dl>
            )}
          </section>
        </aside>
      </div>
    </div>
  );
}
