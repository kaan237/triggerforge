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
      <textarea value={text} onChange={(e) => setText(e.target.value)} placeholder="Profil JSON'unu buraya yapıştır" spellCheck={false} rows={6} />
      <div className="button-row">
        <label className="button file-btn">
          Dosya seç
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
          Vazgeç
        </button>
        <button
          className="button primary"
          disabled={!text.trim()}
          onClick={() => {
            send({ type: "profile.import", json: text });
            onDone();
          }}
        >
          İçe aktar
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
              <button aria-label={`${g} kaldır`} onClick={() => onChange(profile.games.filter((x) => x !== g))}>
                Kaldır
              </button>
            </li>
          ))}
        </ul>
      ) : (
        <p className="hint">Oyun eklenmedi. Bu profil yalnız elle seçilince açılır.</p>
      )}
      <form
        className="game-add"
        onSubmit={(e) => {
          e.preventDefault();
          add(value.endsWith(".exe") || !value.trim() ? value : `${value.trim()}.exe`);
        }}
      >
        <input type="text" value={value} onChange={(e) => setValue(e.target.value)} placeholder="ornek.exe" spellCheck={false} />
        <button className="button" type="submit" disabled={!value.trim()}>
          Ekle
        </button>
        <button
          className={picking ? "button on" : "button"}
          type="button"
          onClick={() => {
            if (!picking) send({ type: "foreground.pick" });
            setPicking(!picking);
          }}
        >
          Son açılanlardan seç
        </button>
      </form>
      {picking && (
        <div className="recent">
          {recent === null ? (
            <p className="hint">Liste alınıyor…</p>
          ) : options.length === 0 ? (
            <p className="hint">Eklenecek yeni uygulama yok.</p>
          ) : (
            <ul>
              {options.map((r) => (
                <li key={r}>
                  <button onClick={() => add(r)}>
                    <span className="num">{r}</span>
                    <span className="faint">ekle</span>
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
    const p = defaultProfile("Yeni profil");
    send({ type: "profile.save", profile: p });
    setSelectedId(p.id);
  };

  const delay = settings ? `${(settings.autoProfileDelayMs / 1000).toLocaleString("tr-TR")} sn` : "";
  const defaultName = profiles.find((p) => p.id === settings?.defaultProfileId)?.name ?? "Varsayılan";

  return (
    <div className="page profiles-page">
      <PageHead title="Profiller">
        <button className="button" onClick={() => setImporting(!importing)}>
          İçe aktar
        </button>
        <button className="button primary" onClick={create}>
          Yeni profil
        </button>
      </PageHead>

      {importing && <ImportBox onDone={() => setImporting(false)} />}

      <div className="profiles-grid">
        <ul className="profile-list" role="listbox" aria-label="Profiller">
          {profiles.map((p) => (
            <li key={p.id}>
              <button role="option" aria-selected={p.id === selected?.id} className={p.id === selected?.id ? "selected" : ""} onClick={() => setSelectedId(p.id)}>
                <span className="pl-name">
                  {p.name}
                  {p.id === activeId && <span className="tag accent">etkin</span>}
                  {p.id === settings?.defaultProfileId && <span className="tag">varsayılan</span>}
                </span>
                <span className="pl-games faint">{p.games.length ? p.games.join(", ") : "yalnız elle"}</span>
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
                aria-label="Profil adı"
                onChange={(e) => setName(e.target.value)}
                onBlur={() => name.trim() && name.trim() !== selected.name && saveMeta(selected, { name: name.trim() })}
                onKeyDown={(e) => e.key === "Enter" && (e.target as HTMLInputElement).blur()}
              />
              {selected.id === activeId ? (
                <span className="active-note">
                  Etkin · <span className="faint">{reasonLabel(status?.activeReason)}</span>
                </span>
              ) : (
                <button className="button primary" onClick={() => send({ type: "profile.activate", id: selected.id })}>
                  Etkinleştir
                </button>
              )}
            </div>

            <div className="detail-actions">
              <button className="button" onClick={() => send({ type: "profile.duplicate", id: selected.id, name: `${selected.name} (kopya)` })}>
                Çoğalt
              </button>
              <button className="button" onClick={() => send({ type: "profile.export", id: selected.id })}>
                Dışa aktar
              </button>
              <span className="spacer" />
              {profiles.length > 1 &&
                (confirmDelete ? (
                  <>
                    <span className="small warn">“{selected.name}” silinsin mi?</span>
                    <button className="button ghost" onClick={() => setConfirmDelete(false)}>
                      Vazgeç
                    </button>
                    <button
                      className="button danger"
                      onClick={() => {
                        send({ type: "profile.delete", id: selected.id });
                        setSelectedId(activeId);
                      }}
                    >
                      Sil
                    </button>
                  </>
                ) : (
                  <button className="button ghost" onClick={() => setConfirmDelete(true)}>
                    Sil
                  </button>
                ))}
            </div>

            <section className="detail-section">
              <h2>Oyunlar</h2>
              <Games profile={selected} onChange={(games) => saveMeta(selected, { games })} />
            </section>

            <section className="detail-section">
              <h2>İçerik</h2>
              <dl className="kv">
                <dt>Işık</dt>
                <dd>
                  <span className="swatch" style={{ background: selected.lightbar.effect === "off" ? "transparent" : selected.lightbar.color }} />
                  {effectLabel(selected.lightbar.effect)}
                </dd>
                <dt>L2 / R2</dt>
                <dd>
                  {triggerSummary(selected.triggers.left)} <span className="faint">/</span> {triggerSummary(selected.triggers.right)}
                </dd>
                <dt>Titreşim</dt>
                <dd className="num">%{Math.round(selected.rumble.strength * 100)}</dd>
              </dl>
            </section>

            <section className="detail-section rule">
              <h2>Otomatik geçiş</h2>
              {settings?.autoProfile ? (
                <ol>
                  <li>Ön plandaki uygulama bir profilin oyun listesindeyse, {delay} sonra o profil açılır.</li>
                  <li>Elle seçtiğin profil, ön plandaki oyun değişene kadar kalır.</li>
                  <li>Eşleşen oyun yoksa bu kontrolcüye atanmış profil, o da yoksa “{defaultName}” kullanılır.</li>
                </ol>
              ) : (
                <p className="hint">Otomatik geçiş kapalı. Profil yalnız elle değişir; Ayarlar'dan açabilirsin.</p>
              )}
            </section>
          </div>
        )}
      </div>
    </div>
  );
}
