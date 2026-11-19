import { L } from "../i18n";
import { useEffect, useState } from "react";
import { send, type Profile } from "../host";
import { defaultProfile, effectLabel, reasonLabel, triggerSummary } from "../model";
import { useApp } from "../state";
import { PageHead } from "../ui";

function ImportBox({ onDone }: { onDone: () => void }) {
  const [text, setText] = useState("");
  const [fileName, setFileName] = useState<string | null>(null);

  return (
    <div className="import-box">
      <textarea value={text} onChange={(e) => setText(e.target.value)} placeholder={L("Paste profile JSON here", "Profil JSON'unu buraya yapıştır")} spellCheck={false} rows={6} />
      <div className="button-row">
        <label className="button file-btn">
          {L("Choose file", "Dosya seç")}
          <input
            type="file"
            accept=".json,application/json"
            onChange={async (e) => {
              const f = e.target.files?.[0];
              if (!f) return;
              setFileName(f.name);
              setText(await f.text());
            }}
          />
        </label>
        {fileName && <span className="faint small file-name">{fileName}</span>}
        <span className="spacer" />
        <button className="button ghost" onClick={onDone}>
          {L("Cancel", "Vazgeç")}
        </button>
        <button
          className="button primary"
          disabled={!text.trim()}
          onClick={() => {
            send({ type: "profile.import", json: text });
            onDone();
          }}
        >
          {L("Import", "İçe aktar")}
        </button>
      </div>
    </div>
  );
}

function Games({ profile, onChange }: { profile: Profile; onChange: (games: string[]) => void }) {
  const { recent } = useApp();
  const [value, setValue] = useState("");
  const [picking, setPicking] = useState(false);

  const add = (exe: string) => {
    const name = exe.trim();
    if (!name) return;
    if (!profile.games.some((g) => g.toLowerCase() === name.toLowerCase())) onChange([...profile.games, name]);
    setValue("");
  };

  const options = (recent ?? []).filter((r) => !profile.games.some((g) => g.toLowerCase() === r.toLowerCase()));

  return (
    <div className="games">
      {profile.games.length > 0 ? (
        <ul className="game-list">
          {profile.games.map((g) => (
            <li key={g}>
              <span className="num">{g}</span>
              <button aria-label={L(`Remove ${g}`, `${g} kaldır`)} onClick={() => onChange(profile.games.filter((x) => x !== g))}>
                {L("Remove", "Kaldır")}
              </button>
            </li>
          ))}
        </ul>
      ) : (
        <p className="hint">{L("No games added. Select this profile manually to activate it.", "Oyun eklenmedi. Bu profil yalnız elle seçilince açılır.")}</p>
      )}
      <form
        className="game-add"
        onSubmit={(e) => {
          e.preventDefault();
          add(value.endsWith(".exe") || !value.trim() ? value : `${value.trim()}.exe`);
        }}
      >
        <input type="text" value={value} onChange={(e) => setValue(e.target.value)} placeholder={L("example.exe", "ornek.exe")} spellCheck={false} />
        <button className="button" type="submit" disabled={!value.trim()}>
          {L("Add", "Ekle")}
        </button>
        <button
          className={picking ? "button on" : "button"}
          type="button"
          onClick={() => {
            if (!picking) send({ type: "foreground.pick" });
            setPicking(!picking);
          }}
        >
          {L("Choose from recent apps", "Son açılanlardan seç")}
        </button>
      </form>
      {picking && (
        <div className="recent">
          {recent === null ? (
            <p className="hint">{L("Loading list…", "Liste alınıyor…")}</p>
          ) : options.length === 0 ? (
            <p className="hint">{L("No new apps to add.", "Eklenecek yeni uygulama yok.")}</p>
          ) : (
            <ul>
              {options.map((r) => (
                <li key={r}>
                  <button onClick={() => add(r)}>
                    <span className="num">{r}</span>
                    <span className="faint">{L("add", "ekle")}</span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </div>
  );
}

export function Profiles() {
  const { profiles, activeId, settings, status, draft, dirty, edit } = useApp();
  const [selectedId, setSelectedId] = useState(activeId);
  const [importing, setImporting] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [name, setName] = useState("");

  const selected = profiles.find((p) => p.id === selectedId) ?? profiles.find((p) => p.id === activeId) ?? profiles[0];

  useEffect(() => {
    setName(selected?.name ?? "");
    setConfirmDelete(false);
  }, [selected?.id, selected?.name]);

  // Management actions write straight away; keep an open draft of the same profile in step.
  const saveMeta = (p: Profile, patch: Partial<Profile>) => {
    send({ type: "profile.save", profile: { ...p, ...patch } });
    if (dirty && draft?.id === p.id) edit((d) => ({ ...d, ...patch }));
  };

  const create = () => {
    const p = defaultProfile(L("New profile", "Yeni profil"));
    send({ type: "profile.save", profile: p });
    setSelectedId(p.id);
  };

  const delay = settings ? L(`${(settings.autoProfileDelayMs / 1000).toLocaleString("en-US")} sec`, `${(settings.autoProfileDelayMs / 1000).toLocaleString("tr-TR")} sn`) : "";
  const defaultName = profiles.find((p) => p.id === settings?.defaultProfileId)?.name ?? L("Default", "Varsayılan");

  return (
    <div className="page profiles-page">
      <PageHead title={L("Profiles", "Profiller")}>
        <button className="button" onClick={() => setImporting(!importing)}>
          {L("Import", "İçe aktar")}
        </button>
        <button className="button primary" onClick={create}>
          {L("New profile", "Yeni profil")}
        </button>
      </PageHead>

      {importing && <ImportBox onDone={() => setImporting(false)} />}

      <div className="profiles-grid">
        <ul className="profile-list" role="listbox" aria-label={L("Profiles", "Profiller")}>
          {profiles.map((p) => (
            <li key={p.id}>
              <button role="option" aria-selected={p.id === selected?.id} className={p.id === selected?.id ? "selected" : ""} onClick={() => setSelectedId(p.id)}>
                <span className="pl-name">
                  {p.name}
                  {p.id === activeId && <span className="tag accent">{L("active", "etkin")}</span>}
                  {p.id === settings?.defaultProfileId && <span className="tag">{L("default", "varsayılan")}</span>}
                </span>
                <span className="pl-games faint">{p.games.length ? p.games.join(", ") : L("manual only", "yalnız elle")}</span>
              </button>
            </li>
          ))}
        </ul>

        {selected && (
          <div className="profile-detail">
            <div className="detail-head">
              <input
                className="name-input"
                value={name}
                aria-label={L("Profile name", "Profil adı")}
                onChange={(e) => setName(e.target.value)}
                onBlur={() => name.trim() && name.trim() !== selected.name && saveMeta(selected, { name: name.trim() })}
                onKeyDown={(e) => e.key === "Enter" && (e.target as HTMLInputElement).blur()}
              />
              {selected.id === activeId ? (
                <span className="active-note">
                  {L("Active ·", "Etkin ·")} <span className="faint">{reasonLabel(status?.activeReason)}</span>
                </span>
              ) : (
                <button className="button primary" onClick={() => send({ type: "profile.activate", id: selected.id })}>
                  {L("Activate", "Etkinleştir")}
                </button>
              )}
            </div>

            <div className="detail-actions">
              <button className="button" onClick={() => send({ type: "profile.duplicate", id: selected.id, name: L(`${selected.name} (copy)`, `${selected.name} (kopya)`) })}>
                {L("Duplicate", "Çoğalt")}
              </button>
              <button className="button" onClick={() => send({ type: "profile.export", id: selected.id })}>
                {L("Export", "Dışa aktar")}
              </button>
              <span className="spacer" />
              {profiles.length > 1 &&
                (confirmDelete ? (
                  <>
                    <span className="small warn">{L(`Delete “${selected.name}”?`, `“${selected.name}” silinsin mi?`)}</span>
                    <button className="button ghost" onClick={() => setConfirmDelete(false)}>
                      {L("Cancel", "Vazgeç")}
                    </button>
                    <button
                      className="button danger"
                      onClick={() => {
                        send({ type: "profile.delete", id: selected.id });
                        setSelectedId(activeId);
                      }}
                    >
                      {L("Delete", "Sil")}
                    </button>
                  </>
                ) : (
                  <button className="button ghost" onClick={() => setConfirmDelete(true)}>
                    {L("Delete", "Sil")}
                  </button>
                ))}
            </div>

            <section className="detail-section">
              <h2>{L("Games", "Oyunlar")}</h2>
              <Games profile={selected} onChange={(games) => saveMeta(selected, { games })} />
            </section>

            <section className="detail-section">
              <h2>{L("Contents", "İçerik")}</h2>
              <dl className="kv">
                <dt>{L("Light", "Işık")}</dt>
                <dd>
                  <span className="swatch" style={{ background: selected.lightbar.effect === "off" ? "transparent" : selected.lightbar.color }} />
                  {effectLabel(selected.lightbar.effect)}
                </dd>
                <dt>L2 / R2</dt>
                <dd>
                  {triggerSummary(selected.triggers.left)} <span className="faint">/</span> {triggerSummary(selected.triggers.right)}
                </dd>
                <dt>{L("Vibration", "Titreşim")}</dt>
                <dd className="num">{L(`${Math.round(selected.rumble.strength * 100)}%`, `%${Math.round(selected.rumble.strength * 100)}`)}</dd>
              </dl>
            </section>

            <section className="detail-section rule">
              <h2>{L("Auto-switching", "Otomatik geçiş")}</h2>
              {settings?.autoProfile ? (
                <ol>
                  <li>{L(`If the foreground app matches a profile, switch to it after ${delay}.`, `Ön plandaki uygulama bir profilin oyun listesindeyse, ${delay} sonra o profil açılır.`)}</li>
                  <li>{L("A manually selected profile stays active until the foreground game changes.", "Elle seçtiğin profil, ön plandaki oyun değişene kadar kalır.")}</li>
                  <li>{L("When no game matches, use this controller's profile or fall back to “", "Eşleşen oyun yoksa bu kontrolcüye atanmış profil, o da yoksa “")}{defaultName}{L("”.", "” kullanılır.")}</li>
                </ol>
              ) : (
                <p className="hint">{L("Auto-switching is off. Profiles switch manually. Enable it in Settings.", "Otomatik geçiş kapalı. Profil yalnız elle değişir; Ayarlar'dan açabilirsin.")}</p>
              )}
            </section>
          </div>
        )}
      </div>
    </div>
  );
}
