import { useEffect, useState } from "react";
import { inHost, send, subscribe } from "./host";

const sections = [
  { id: "controller", label: "Kontrolcü" },
  { id: "triggers", label: "Tetikler" },
  { id: "light", label: "Işık" },
  { id: "buttons", label: "Tuşlar" },
  { id: "haptics", label: "Ses ve haptik" },
  { id: "profiles", label: "Profiller" },
] as const;

type SectionId = (typeof sections)[number]["id"] | "settings";

export function App() {
  const [section, setSection] = useState<SectionId>("controller");
  const [hostVersion, setHostVersion] = useState<string | null>(null);

  useEffect(() => {
    const off = subscribe((m) => {
      if (m.type === "host") setHostVersion(m.version);
    });
    send({ type: "hello" });
    return off;
  }, []);

  return (
    <div className="shell">
      <nav className="rail">
        <div className="wordmark">tactile</div>
        <ul>
          {sections.map((s) => (
            <li key={s.id}>
              <button className={section === s.id ? "active" : ""} onClick={() => setSection(s.id)}>
                {s.label}
              </button>
            </li>
          ))}
        </ul>
        <div className="rail-foot">
          <button className={section === "settings" ? "active" : ""} onClick={() => setSection("settings")}>
            Ayarlar
          </button>
          <span className="version">{inHost ? `v${hostVersion ?? "…"}` : "tarayıcı önizleme"}</span>
        </div>
      </nav>

      <main>
        <header className="status">
          <span className="dot" aria-hidden />
          <span>Kontrolcü bağlı değil</span>
          <span className="spacer" />
          <span className="metric">
            <span className="label">pil</span>
            <span className="value">—</span>
          </span>
          <span className="metric">
            <span className="label">bağlantı</span>
            <span className="value">—</span>
          </span>
        </header>

        <section className="content">
          {section === "controller" ? (
            <div className="empty">
              <h1>Kontrolcü bekleniyor</h1>
              <p>DualSense'i USB kabloyla tak ya da Bluetooth ile eşleştir. Bağlandığında burada canlı görünecek.</p>
            </div>
          ) : (
            <div className="empty">
              <h1>{section === "settings" ? "Ayarlar" : sections.find((s) => s.id === section)?.label}</h1>
              <p>Bu bölüm sonraki fazlarda gelecek.</p>
            </div>
          )}
        </section>
      </main>
    </div>
  );
}
