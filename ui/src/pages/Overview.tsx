import { ControllerDrawing } from "../ControllerDrawing";
import { send } from "../host";
import { brightnessLabel, effectLabel, ledMask, reasonLabel, triggerSummary } from "../model";
import { useApp, useInput } from "../state";
import { PageHead } from "../ui";

const linkLabel: Record<string, string> = { Bluetooth: "Bluetooth", Usb: "USB", Simulated: "Simülasyon" };

export function Overview() {
  const { status, output, profiles, activeId } = useApp();
  const input = useInput();
  const connected = status?.connected ?? false;
  const active = profiles.find((p) => p.id === activeId);

  return (
    <div className="page overview">
      <PageHead title="Kontrolcü">
        <span className="legend">
          <span className="key raw" /> ham
          <span className="key proc" /> işlenmiş
        </span>
      </PageHead>

      <div className="overview-grid">
        <div className={connected ? "drawing" : "drawing idle"}>
          <ControllerDrawing input={connected ? input : null} output={connected ? output : null} />
          {!connected && <p className="drawing-note">Kontrolcü bekleniyor. USB ile tak ya da Bluetooth ile eşleştir.</p>}
        </div>

        <aside className="info">
          <section className="info-block">
            <h2>Cihaz</h2>
            <dl className="kv">
              <dt>Model</dt>
              <dd>{connected ? status?.model ?? "DualSense" : "Bağlı değil"}</dd>
              <dt>Bağlantı</dt>
              <dd>{connected && status?.link ? linkLabel[status.link] ?? status.link : "—"}</dd>
              <dt>MAC</dt>
              <dd className="num">{connected ? status?.mac ?? "—" : "—"}</dd>
              <dt>Firmware</dt>
              <dd className="num">{connected ? status?.firmware ?? "—" : "—"}</dd>
              <dt>Pil</dt>
              <dd>
                {input ? (
                  <span className="battery">
                    <span className="battery-bar">
                      <span style={{ width: `${input.battery}%` }} className={input.battery <= 20 ? "low" : ""} />
                    </span>
                    <span className="num">%{input.battery}</span>
                    {input.charging && <span className="faint">şarjda</span>}
                  </span>
                ) : (
                  "—"
                )}
              </dd>
            </dl>
            {input && (
              <p className="small faint num">
                {input.reports.toLocaleString("tr-TR")} rapor
                {input.badCrc > 0 ? ` · ${input.badCrc} bozuk CRC` : " · CRC temiz"}
              </p>
            )}
          </section>

          <section className="info-block">
            <h2>Etkin profil</h2>
            <div className="active-profile">
              <select value={activeId} onChange={(e) => send({ type: "profile.activate", id: e.target.value })} aria-label="Etkin profil">
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
                <dt>Işık</dt>
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
                <dt>Oyuncu</dt>
                <dd>
                  <span className="mini-leds">
                    {[0, 1, 2, 3, 4].map((i) => (
                      <span key={i} className={(ledMask(active.playerLeds) >> i) & 1 ? "on" : ""} />
                    ))}
                  </span>
                </dd>
                <dt>Analog</dt>
                <dd className="num">
                  ölü bölge %{Math.round(active.sticks.left.innerDeadzone * 100)} / %{Math.round(active.sticks.right.innerDeadzone * 100)}
                </dd>
                <dt>Oyunlar</dt>
                <dd className="games-line">{active.games.length ? active.games.join(", ") : <span className="faint">yalnız elle</span>}</dd>
              </dl>
            )}
          </section>
        </aside>
      </div>
    </div>
  );
}
