import { send, type UpdateSettings } from "../host";
import { L, locale } from "../i18n";
import { useApp } from "../state";
import { pct, Toggle } from "../ui";

export function Updates({ settings, onChange }: { settings: UpdateSettings; onChange: (settings: UpdateSettings) => void }) {
  const { status, update } = useApp();
  const state = update?.state ?? "idle";
  const busy = state === "checking" || state === "downloading";
  const lastCheck = update?.lastCheck;
  const progress = update?.progress;
  const downloadLabel = L("Downloading…", "İndiriliyor…");

  return (
    <section className="settings-group updates">
      <h2>{L("Updates", "Güncellemeler")}</h2>
      <Toggle label={L("Check for updates automatically", "Güncellemeleri otomatik denetle")} checked={settings.autoCheck} onChange={(autoCheck) => onChange({ autoCheck })} />
      <div className="setting-row">
        <span>{L("Current version", "Mevcut sürüm")}</span>
        <span className="num">{update?.current ?? status?.version ?? "—"}</span>
      </div>
      <div className="setting-row">
        <span>{L("Last check", "Son denetim")}</span>
        {lastCheck ? <time dateTime={lastCheck}>{new Date(lastCheck).toLocaleString(locale())}</time> : <span>{L("Never", "Henüz yok")}</span>}
      </div>
      <div className="update-state">
        {state === "checking" && <p role="status">{L("Checking for updates…", "Güncellemeler denetleniyor…")}</p>}
        {state === "upToDate" && <p role="status">{L("You’re up to date.", "En güncel sürümü kullanıyorsunuz.")}</p>}
        {update && state === "available" && (
          <>
            <p role="status">{L("Available version", "Yeni sürüm")} <strong>{update.latest}</strong></p>
            {update.released && <p>{L("Release date", "Yayın tarihi")} <time dateTime={update.released}>{update.released}</time></p>}
            {update.notes && <p className="update-notes">{update.notes}</p>}
          </>
        )}
        {state === "downloading" && (
          <>
            <label htmlFor="update-progress">{downloadLabel} {progress != null && pct(progress)}</label>
            <progress id="update-progress" max={1} value={progress ?? undefined} />
          </>
        )}
        {state === "ready" && <p role="status">{L("Ready to install.", "Kuruluma hazır.")}</p>}
        {update?.message
          ? <p className={state === "error" ? "warn" : undefined} role="status">{update.message}</p>
          : state === "error" && <p className="warn" role="status">{L("The update failed.", "Güncelleme başarısız oldu.")}</p>}
        <div className="button-row setup-actions">
          <button className="button" disabled={busy} onClick={() => send({ type: "update.check" })}>
            {state === "error" ? L("Try again", "Yeniden dene") : L("Check now", "Şimdi denetle")}
          </button>
          {update && state === "available" && (
            <button className="button primary" onClick={() => send({ type: update.portable ? "update.openPage" : "update.download" })}>
              {update.portable ? L("Open download page", "İndirme sayfasını aç") : L("Download", "İndir")}
            </button>
          )}
          {state === "ready" && (
            <button className="button primary" onClick={() => send({ type: "update.install" })}>{L("Install and restart", "Kur ve yeniden başlat")}</button>
          )}
        </div>
      </div>
    </section>
  );
}
