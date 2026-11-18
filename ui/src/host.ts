// Bridge to the C# host (WebView2). Outside the host (vite dev in a browser) it stays silent.

export type HostMessage = { type: "host"; version: string; controllers: number };

type WebView = {
  postMessage(message: unknown): void;
  addEventListener(type: "message", listener: (e: { data: unknown }) => void): void;
  removeEventListener(type: "message", listener: (e: { data: unknown }) => void): void;
};

const webview: WebView | undefined = (window as unknown as { chrome?: { webview?: WebView } }).chrome?.webview;

export const inHost = webview !== undefined;

export function send(message: { type: string; [key: string]: unknown }) {
  webview?.postMessage(message);
}

export function subscribe(handler: (message: HostMessage) => void): () => void {
  if (!webview) return () => {};
  const listener = (e: { data: unknown }) => handler(e.data as HostMessage);
  webview.addEventListener("message", listener);
  return () => webview.removeEventListener("message", listener);
}
