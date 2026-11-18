import { useEffect, useState } from "react";
import { inHost, send, subscribe, type Input, type Output, type Status } from "./host";
import { Live } from "./Live";
import { Outputs } from "./Outputs";

const sections = [
  { id: "controller", label: "Kontrolcü" },
  { id: "triggers", label: "Tetikler" },
  { id: "light", label: "Işık" },
  { id: "buttons", label: "Tuşlar" },
  { id: "haptics", label: "Ses ve haptik" },
  { id: "profiles", label: "Profiller" },
] as const;

type SectionId = (typeof sections)[number]["id"] | "settings";

const linkLabel: Record<string, string> = { Bluetooth: "Bluetooth", Usb: "USB", Simulated: "simülasyon" };

export function App() {
  const [section, setSection] = useState<SectionId>("controller");
  const [status, setStatus] = useState<Status | null>(null);
  const [input, setInput] = useState<Input | null>(null);
  const [output, setOutput] = useState<Output | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const off = subscribe((m) => {
      if (m.type === "status") {
        setStatus(m);
        if (!m.connected) setInput(null);
      } else if (m.type === "input") setInput(m);
      else if (m.type === "output") setOutput(m);
      else if (m.type === "error") setError(m.message);
    });
    send({ type: "hello" });
    return off;
  }, []);

  useEffect(() => {
    if (!error) return;
    const t = window.setTimeout(() => setError(null), 4000);
    return () => window.clearTimeout(t);
  }, [error]);

  // The interface accent follows the controller lightbar.
  useEffect(() => {
    const color = status?.connected && output ? output.lightbar : null;
    document.documentElement.style.setProperty("--accent", color && color !== "#000000" ? lift(color) : "#9a9ca1");
  }, [output, status]);

  const connected = status?.connected ?? false;

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
          <span className="version">{inHost ? `v${status?.version ?? "…"}` : "tarayıcı önizleme"}</span>
        </div>
      </nav>

      <main>
        <header className="status">
          <span className={connected ? "dot live" : "dot"} aria-hidden />
          <span>{connected ? `${status?.model ?? "DualSense"}` : "Kontrolcü bağlı değil"}</span>
          {connected && status?.firmware && <span className="faint">fw {status.firmware}</span>}
          <span className="spacer" />
          {error && <span className="error">{error}</span>}
          <span className="metric">
            <span className="label">pil</span>
            <span className="value">{input ? `${input.battery}%${input.charging ? " şarjda" : ""}` : "—"}</span>
          </span>
          <span className="metric">
            <span className="label">bağlantı</span>
            <span className="value">{connected && status?.link ? linkLabel[status.link] ?? status.link : "—"}</span>
          </span>
        </header>

        <section className="content">
          {section === "controller" ? (
            connected ? (
              <div className="controller-page">
                <Live input={input} />
                <Outputs output={output} link={status?.link} />
              </div>
            ) : (
              <div className="empty">
                <h1>Kontrolcü bekleniyor</h1>
                <p>DualSense'i USB kabloyla tak ya da Bluetooth ile eşleştir. Bağlandığında burada canlı görünecek.</p>
              </div>
            )
          ) : (
            <div className="empty">
              <h1>{section === "settings" ? "Ayarlar" : sections.find((s) => s.id === section)?.label}</h1>
              <p>Bu bölüm sonraki fazlarda gelecek. Şimdilik test araçları Kontrolcü sayfasında.</p>
            </div>
          )}
        </section>
      </main>
    </div>
  );
}

// Dim lightbar colours (e.g. #000040) are unreadable as UI accents; raise lightness, keep hue.
function lift(hex: string): string {
  const n = parseInt(hex.slice(1), 16);
  let r = (n >> 16) & 255,
    g = (n >> 8) & 255,
    b = n & 255;
  const max = Math.max(r, g, b);
  if (max === 0) return "#9a9ca1";
  const k = Math.max(1, 200 / max);
  r = Math.min(255, Math.round(r * k + 40));
  g = Math.min(255, Math.round(g * k + 40));
  b = Math.min(255, Math.round(b * k + 40));
  return `rgb(${r} ${g} ${b})`;
}
