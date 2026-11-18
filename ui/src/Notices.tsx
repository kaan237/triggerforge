import { useEffect } from "react";
import { useApp, type Notice } from "./state";

const title: Record<Notice["kind"], string> = {
  lowBattery: "Pil azaldı",
  brokenFile: "Bozuk dosya",
  idleSleep: "Uyku",
  gyroCalibration: "Gyro kalibrasyonu",
  info: "Bilgi",
  elevated: "Yönetici izni",
  mapping: "Tuş ataması",
  emulation: "Sanal kontrolcü",
  error: "Hata",
};

function Item({ n, dismiss }: { n: Notice; dismiss: (id: number) => void }) {
  const running = n.progress !== undefined && n.progress < 1;
  useEffect(() => {
    if (running) return;
    const t = window.setTimeout(() => dismiss(n.id), n.kind === "error" || n.kind === "brokenFile" || n.kind === "elevated" ? 9000 : 6000);
    return () => window.clearTimeout(t);
  }, [running, n.id, n.kind, n.message, dismiss]);

  return (
    <li className={`notice ${n.kind}`} role={n.kind === "error" ? "alert" : "status"}>
      <div className="notice-body">
        <span className="notice-title">{title[n.kind]}</span>
        <span className="notice-text">{n.message}</span>
        {n.progress !== undefined && (
          <span className="progress">
            <span style={{ width: `${n.progress * 100}%` }} />
          </span>
        )}
      </div>
      <button className="notice-close" aria-label="Kapat" onClick={() => dismiss(n.id)}>
        <svg viewBox="0 0 10 10" width="10" height="10" aria-hidden>
          <path d="M 1.5 1.5 L 8.5 8.5 M 8.5 1.5 L 1.5 8.5" />
        </svg>
      </button>
    </li>
  );
}

export function Notices() {
  const { notices, dismiss } = useApp();
  if (notices.length === 0) return null;
  return (
    <ul className="notices" aria-live="polite">
      {notices.map((n) => (
        <Item key={n.id} n={n} dismiss={dismiss} />
      ))}
    </ul>
  );
}
