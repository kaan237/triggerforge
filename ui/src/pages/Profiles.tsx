import { L, locale } from "../i18n";
import { useEffect, useMemo, useRef, useState } from "react";
import { send, subscribe, type CatalogController, type CatalogEntry, type CatalogError, type EmulationMode, type ExportShare, type Profile } from "../host";
import { gyroModes } from "../mapping";
import { defaultProfile, effectLabel, modeDef, reasonLabel, triggerSummary } from "../model";
import { useApp } from "../state";
import { PageHead, Section, Segmented } from "../ui";

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

function DailyBackups() {
  const { backups, dirty, restoreBackup } = useApp();
  const [confirmId, setConfirmId] = useState<string | null>(null);
  useEffect(() => send({ type: "backups.list" }), []);

  return (
    <Section title={L("Daily backups", "Günlük yedekler")}>
      {backups === null && <p className="hint">{L("Loading backups…", "Yedekler yükleniyor…")}</p>}
      {backups?.length === 0 && <p className="hint">{L("No backups yet.", "Henüz yedek yok.")}</p>}
      {backups?.map(({ id, date, profileCount }) => (
        <div className="row-field" key={id}>
          <span>
            {id.startsWith("pre-restore-") && <span className="tag">{L("Before restore", "Geri yükleme öncesi")}</span>}
            {" "}{L(`${date} · ${profileCount} profiles`, `${date} · ${profileCount} profil`)}
          </span>
          {confirmId === id ? (
            <span className="confirm-inline">
              <span className="small warn">{L("Replace the saved profiles with this backup? A copy of your current profiles is kept.", "Kayıtlı profiller bu yedekle değiştirilsin mi? Mevcut profillerin bir kopyası saklanır.")}</span>
              {dirty && <span className="small warn">{L("The backup copy contains saved profiles only.", "Yedek kopyası yalnızca kayıtlı profilleri içerir.")}</span>}
              <button className="button ghost" onClick={() => setConfirmId(null)}>{L("Cancel", "Vazgeç")}</button>
              <button className="button danger" onClick={() => {
                setConfirmId(null);
                restoreBackup(id);
              }}>{L("Restore", "Geri yükle")}</button>
            </span>
          ) : (
            <button className="button" onClick={() => setConfirmId(id)}>{L("Restore", "Geri yükle")}</button>
          )}
        </div>
      ))}
    </Section>
  );
}

// ---- community catalog ----
// Everything in a catalog entry comes from a third party: it is only ever rendered as text.

type ControllerFilter = "all" | "dualsense" | "edge";
const maxTagChips = 16;

const controllerLabel = (c: CatalogController) => (c === "edge" ? "DualSense Edge" : c === "dualsense" ? "DualSense" : L("Any controller", "Tüm kontrolcüler"));
const emulationLabel = (m: EmulationMode) =>
  m === "off" ? L("Off", "Kapalı") : m === "xbox360" ? "Xbox 360" : m === "dualSense" ? "DualSense" : m === "dualShock4" ? "DualShock 4" : L("Auto (by game)", "Otomatik (oyuna göre)");
const onOff = (v: boolean) => (v ? L("On", "Açık") : L("Off", "Kapalı"));
const gyroLabel = (mode: string) => gyroModes.find((g) => g.id === mode)?.label ?? mode;

function catalogErrorText(error: CatalogError | null): string {
  switch (error) {
    case "network": return L("Could not reach the community catalog. Check your connection.", "Topluluk kataloğuna ulaşılamadı. Bağlantınızı kontrol edin.");
    case "untrustedHost": return L("The catalog was redirected to an untrusted address and was not loaded.", "Katalog güvenilmeyen bir adrese yönlendirildi ve yüklenmedi.");
    case "tooLarge": return L("The catalog is larger than allowed and was not loaded.", "Katalog izin verilenden büyük olduğu için yüklenmedi.");
    case "badSignature": return L("The catalog signature could not be verified, so it was not loaded.", "Katalog imzası doğrulanamadığı için yüklenmedi.");
    case "badManifest": return L("The catalog is malformed and was not loaded.", "Katalog biçimi bozuk olduğu için yüklenmedi.");
    case "badFile": return L("The profile file did not match the catalog and was not imported.", "Profil dosyası katalogla uyuşmadığı için içe aktarılmadı.");
    case "outdated": return L("The catalog is older than the last one you saw, so it was not loaded.", "Katalog en son gördüğünüzden eski olduğu için yüklenmedi.");
    case "storage": return L("The profile could not be saved.", "Profil kaydedilemedi.");
    default: return L("The community catalog could not be loaded.", "Topluluk kataloğu yüklenemedi.");
  }
}

const describe = (e: CatalogEntry) => L(e.description.en || e.description.tr, e.description.tr || e.description.en);
const formatSize = (bytes: number) => `${(bytes / 1024).toLocaleString(locale(), { maximumFractionDigits: 1 })} KB`;

function CommunityDetail({ entry, busy, importingThis }: { entry: CatalogEntry; busy: boolean; importingThis: boolean }) {
  const s = entry.summary;
  const text = describe(entry);
  return (
    <div className="profile-detail">
      <div className="detail-head">
        <span className="catalog-name">{entry.name}</span>
        <button className="button primary" disabled={busy} onClick={() => send({ type: "catalog.import", id: entry.id })}>
          {importingThis ? L("Importing…", "İçe aktarılıyor…") : L("Import", "İçe aktar")}
        </button>
      </div>
      <p className="catalog-meta faint">
        {L(`By ${entry.author}`, `Yazar: ${entry.author}`)} · <span className="num">v{entry.version}</span> · {controllerLabel(entry.controller)}
      </p>

      {text && (
        <section className="detail-section">
          <h2>{L("Description", "Açıklama")}</h2>
          <p className="catalog-text">{text}</p>
        </section>
      )}

      <section className="detail-section">
        <h2>{L("Preview", "Önizleme")}</h2>
        <dl className="kv compact">
          <dt>L2 / R2</dt>
          <dd>{modeDef(s.leftTrigger).label} <span className="faint">/</span> {modeDef(s.rightTrigger).label}</dd>
          <dt>{L("Virtual controller", "Sanal kontrolcü")}</dt>
          <dd>{emulationLabel(s.emulation)}</dd>
          <dt>{L("Button mapping", "Tuş eşleme")}</dt>
          <dd>{onOff(s.mappingEnabled)}</dd>
          <dt>{L("Layers / chords / macros", "Katman / akor / makro")}</dt>
          <dd className="num">{s.layerCount} / {s.chordCount} / {s.macroCount}</dd>
          <dt>{L("Gyro mouse", "Gyro fare")}</dt>
          <dd>{gyroLabel(s.gyroMouse)}</dd>
          <dt>{L("Gyro stick", "Gyro analog")}</dt>
          <dd>{gyroLabel(s.gyroStick)}</dd>
          <dt>{L("Flick stick", "Flick stick")}</dt>
          <dd>{onOff(s.flickStick)}</dd>
          <dt>{L("Audio haptics", "Ses titreşimi")}</dt>
          <dd>{onOff(s.audioHaptics)}</dd>
          <dt>{L("Telemetry rules", "Telemetri kuralları")}</dt>
          <dd className="num">{s.telemetryRules}</dd>
          <dt>{L("File size", "Dosya boyutu")}</dt>
          <dd className="num">{formatSize(entry.size)}</dd>
        </dl>
      </section>

      <section className="detail-section">
        <h2>{L("Games", "Oyunlar")}</h2>
        {entry.games.length > 0 ? (
          <ul className="game-list">
            {entry.games.map((g) => (
              <li key={g}><span className="num">{g}</span></li>
            ))}
          </ul>
        ) : (
          <p className="hint">{L("No games listed. Select this profile manually to activate it.", "Oyun belirtilmemiş. Bu profil yalnız elle seçilince açılır.")}</p>
        )}
        {entry.tags.length > 0 && (
          <div className="chips catalog-tags">
            {entry.tags.map((t) => <span key={t} className="chip">{t}</span>)}
          </div>
        )}
      </section>

      <section className="detail-section">
        <p className="hint">{L("Shared by a community member. Actions that launch programs are removed when you import a profile.", "Bir topluluk üyesi paylaştı. Profili içe aktarırken program çalıştıran eylemler kaldırılır.")}</p>
      </section>
    </div>
  );
}

function Community() {
  const { catalog } = useApp();
  const [query, setQuery] = useState("");
  const [controller, setController] = useState<ControllerFilter>("all");
  const [game, setGame] = useState("");
  const [tag, setTag] = useState("");
  const [selectedId, setSelectedId] = useState("");

  // The catalog is only fetched once this tab is opened.
  // Asked again when the host answers idle: a request sent before the handshake is dropped.
  const catalogState = catalog?.state;
  useEffect(() => {
    if (catalogState === undefined || catalogState === "idle") send({ type: "catalog.list" });
  }, [catalogState]);

  const entries = useMemo(() => catalog?.profiles ?? [], [catalog]);
  const loading = !catalog || catalog.state === "idle" || catalog.state === "loading";
  const failed = catalog?.state === "error";
  const busy = loading || catalog?.state === "importing";

  const games = useMemo(() => [...new Set(entries.flatMap((e) => e.games))].sort((a, b) => a.localeCompare(b)), [entries]);
  const tags = useMemo(() => {
    const counts = new Map<string, number>();
    for (const e of entries) for (const t of e.tags) counts.set(t, (counts.get(t) ?? 0) + 1);
    return [...counts].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0])).slice(0, maxTagChips).map(([t]) => t);
  }, [entries]);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    return entries
      .filter((e) => {
        if (controller !== "all" && e.controller !== "any" && e.controller !== controller) return false;
        if (game && !e.games.includes(game)) return false;
        if (tag && !e.tags.includes(tag)) return false;
        if (!q) return true;
        return [e.name, e.author, ...e.games, ...e.tags].some((v) => v.toLowerCase().includes(q));
      })
      .sort((a, b) => a.name.localeCompare(b.name));
  }, [entries, query, controller, game, tag]);

  const selected = filtered.find((e) => e.id === selectedId) ?? filtered[0];
  const filtering = query.trim() !== "" || controller !== "all" || game !== "" || tag !== "";
  const clearFilters = () => {
    setQuery("");
    setController("all");
    setGame("");
    setTag("");
  };

  return (
    <>
      <div className="catalog-bar">
        {entries.length > 0 && (
          <>
            <input
              type="text"
              className="catalog-search"
              value={query}
              aria-label={L("Search profiles", "Profillerde ara")}
              placeholder={L("Search name, game, author or tag…", "Ad, oyun, yazar veya etiket ara…")}
              spellCheck={false}
              onChange={(e) => setQuery(e.target.value)}
            />
            <select aria-label={L("Controller", "Kontrolcü")} value={controller} onChange={(e) => setController(e.target.value as ControllerFilter)}>
              <option value="all">{L("All controllers", "Tüm kontrolcüler")}</option>
              <option value="dualsense">DualSense</option>
              <option value="edge">DualSense Edge</option>
            </select>
            <select aria-label={L("Game", "Oyun")} value={game} onChange={(e) => setGame(e.target.value)}>
              <option value="">{L("All games", "Tüm oyunlar")}</option>
              {games.map((g) => <option key={g} value={g}>{g}</option>)}
            </select>
          </>
        )}
        <span className="spacer" />
        <button className="button" disabled={busy} onClick={() => send({ type: "catalog.list", refresh: true })}>
          {L("Refresh", "Yenile")}
        </button>
      </div>

      {tags.length > 0 && (
        <div className="chips catalog-chips" role="group" aria-label={L("Filter by tag", "Etikete göre süz")}>
          {tags.map((t) => (
            <button key={t} type="button" className={t === tag ? "chip on" : "chip"} aria-pressed={t === tag} onClick={() => setTag(t === tag ? "" : t)}>
              {t}
            </button>
          ))}
        </div>
      )}

      {loading && <p className="catalog-note" role="status">{L("Loading the community catalog…", "Topluluk kataloğu yükleniyor…")}</p>}

      {failed && (
        <div className="catalog-note" role="alert">
          <span className="warn">{catalogErrorText(catalog?.error ?? null)}</span>
          <span className="spacer" />
          <button className="button" onClick={() => send({ type: "catalog.list" })}>{L("Try again", "Tekrar dene")}</button>
        </div>
      )}

      {!loading && !failed && entries.length === 0 && (
        <p className="catalog-note" role="status">{L("The community catalog has no profiles yet.", "Topluluk kataloğunda henüz profil yok.")}</p>
      )}

      {!loading && entries.length > 0 && filtered.length === 0 && (
        <div className="catalog-note" role="status">
          <span>{L("No profiles match your search.", "Aramanla eşleşen profil yok.")}</span>
          {filtering && <><span className="spacer" /><button className="button" onClick={clearFilters}>{L("Clear filters", "Süzgeçleri temizle")}</button></>}
        </div>
      )}

      {filtered.length > 0 && (
        <div className="profiles-grid">
          <ul className="profile-list" role="listbox" aria-label={L("Community profiles", "Topluluk profilleri")}>
            {filtered.map((e) => (
              <li key={e.id}>
                <button role="option" aria-selected={e.id === selected?.id} className={e.id === selected?.id ? "selected" : ""} onClick={() => setSelectedId(e.id)}>
                  <span className="pl-name">{e.name}</span>
                  <span className="pl-games faint">{e.author} · v{e.version} · {controllerLabel(e.controller)}</span>
                  <span className="pl-games faint">{e.games.length ? e.games.join(", ") : L("manual only", "yalnız elle")}</span>
                  {e.tags.length > 0 && <span className="pl-games faint">{e.tags.map((t) => `#${t}`).join(" ")}</span>}
                </button>
              </li>
            ))}
          </ul>
          {selected && <CommunityDetail entry={selected} busy={busy} importingThis={catalog?.state === "importing" && catalog.importingId === selected.id} />}
        </div>
      )}

      <p className="hint catalog-foot">
        {catalog?.updated && <>{L(`Catalog updated ${catalog.updated}`, `Katalog güncellemesi: ${catalog.updated}`)} · </>}
        <button type="button" className="update-notes-link" onClick={() => send({ type: "catalog.openRepo" })}>
          {L("Open the catalog repository", "Katalog deposunu aç")}
        </button>
      </p>
    </>
  );
}

function saveFile(fileName: string, json: string) {
  const safe = fileName.replace(/[\\/:*?"<>|]+/g, "").trim() || "profile.json";
  const url = URL.createObjectURL(new Blob([json], { type: "application/json" }));
  const a = document.createElement("a");
  a.href = url;
  a.download = safe;
  a.click();
  window.setTimeout(() => URL.revokeObjectURL(url), 1000);
}

function ShareBox({ share, onClose }: { share: ExportShare; onClose: () => void }) {
  const [copied, setCopied] = useState<"idle" | "ok" | "fail">("idle");
  const head = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    setCopied("idle");
    head.current?.focus();
  }, [share]);

  const copy = () => {
    const done = (ok: boolean) => setCopied(ok ? "ok" : "fail");
    if (!navigator.clipboard) return done(false);
    navigator.clipboard.writeText(share.entryJson).then(() => done(true), () => done(false));
  };

  return (
    <div className="import-box share-box" role="region" aria-labelledby="share-title">
      <h2 id="share-title" ref={head} tabIndex={-1}>{L("Export for sharing", "Paylaşım için dışa aktar")}</h2>
      <p className="small">
        {share.removedRunActions > 0
          ? L(`For safety, ${share.removedRunActions} action(s) that launch programs were removed from this copy.`, `Güvenlik için bu kopyadan program çalıştıran ${share.removedRunActions} eylem çıkarıldı.`)
          : L("No actions that launch programs were found. The copy is ready to share.", "Program çalıştıran eylem bulunmadı. Kopya paylaşıma hazır.")}
      </p>
      <ol className="share-steps small">
        <li>{L("Download the file and add it to the profiles folder of the catalog repository.", "Dosyayı indir ve katalog deposundaki profiles klasörüne ekle.")}</li>
        <li>{L("Copy the catalog entry below and add it to index.json. Its size and checksum are already filled in; do not edit the file afterwards.", "Aşağıdaki katalog girdisini kopyala ve index.json dosyasına ekle. Boyut ve sağlama toplamı hazır; dosyayı sonradan değiştirme.")}</li>
        <li>{L("Open a pull request on the repository.", "Depoda bir çekme isteği (pull request) aç.")}</li>
      </ol>
      <textarea readOnly value={share.entryJson} rows={8} spellCheck={false} aria-label={L("Catalog entry", "Katalog girdisi")} onFocus={(e) => e.currentTarget.select()} />
      <div className="button-row">
        <button className="button" onClick={() => saveFile(share.fileName, share.json)}>{L("Download file", "Dosyayı indir")}</button>
        <button className="button" onClick={copy}>{L("Copy catalog entry", "Katalog girdisini kopyala")}</button>
        <button className="button" onClick={() => send({ type: "catalog.openRepo" })}>{L("Open the catalog repository", "Katalog deposunu aç")}</button>
        <span className="spacer" />
        <span className="small faint" role="status">
          {copied === "ok" && L("Copied.", "Kopyalandı.")}
          {copied === "fail" && L("Could not copy. Select the text and copy it manually.", "Kopyalanamadı. Metni seçip elle kopyala.")}
        </span>
        <button className="button ghost" onClick={onClose}>{L("Close", "Kapat")}</button>
      </div>
    </div>
  );
}

export function Profiles() {
  const { profiles, activeId, settings, status, draft, dirty, edit } = useApp();
  const [selectedId, setSelectedId] = useState(activeId);
  const [importing, setImporting] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [name, setName] = useState("");
  const [tab, setTab] = useState<"mine" | "community">("mine");
  const [share, setShare] = useState<ExportShare | null>(null);

  useEffect(() => subscribe((m) => {
    if (m.type === "exportShare") setShare(m);
  }), []);

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
        {tab === "mine" && (
          <>
            <button className="button" onClick={() => setImporting(!importing)}>
              {L("Import", "İçe aktar")}
            </button>
            <button className="button primary" onClick={create}>
              {L("New profile", "Yeni profil")}
            </button>
          </>
        )}
      </PageHead>

      <div className="profiles-tabs">
        <Segmented
          label={L("Profile source", "Profil kaynağı")}
          value={tab}
          onChange={setTab}
          options={[
            { id: "mine", label: L("My profiles", "Profillerim") },
            { id: "community", label: L("Community", "Topluluk") },
          ]}
        />
      </div>

      {tab === "community" ? <Community /> : <>
      {importing && <ImportBox onDone={() => setImporting(false)} />}
      {share && <ShareBox share={share} onClose={() => setShare(null)} />}

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
              <button className="button" onClick={() => send({ type: "profile.exportShare", id: selected.id })}>
                {L("Export for sharing", "Paylaşım için dışa aktar")}
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
      <DailyBackups />
      </>}
    </div>
  );
}
