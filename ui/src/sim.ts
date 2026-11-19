// Browser-only stand-in for the host. Keeps profiles and settings in memory and fakes
// a controller so every screen can be used without hardware.

import type { Action, AppSettings, AudioApp, Binding, DriverKind, DriverState, EmulationStatus, HapticStatus, HostMessage, Output, Profile, TriggerConfig } from "./host";
import {
  clone,
  defaultBinding,
  defaultProfile,
  defaultSettings,
  effectColor,
  ledMask,
  newId,
  normalizeProfile,
  normalizeSettings,
  processStick,
  rawAxis,
} from "./model";

type Msg = { type: string; [key: string]: unknown };

function sampleProfiles(): Profile[] {
  const base = defaultProfile("Varsayılan", "0d1f6c2a8e4b4c39a1f05e7d3b2c9a10");

  const ghost = defaultProfile("Ghost of Tsushima", "7a2e91c4d05f4b8e9c3a1d6f2e8b4a57");
  ghost.games = ["GhostOfTsushima.exe"];
  ghost.lightbar = { effect: "breathe", color: "#E8E2D0", color2: "#401010", speed: 0.4, brightness: "medium" };
  ghost.triggers.left = { mode: "feedback", params: { start: 2, strength: 3 }, zones: null, frequency: 0 };
  ghost.triggers.right = { mode: "bow", params: { start: 1, end: 6, strength: 6, snap: 4 }, zones: null, frequency: 0 };
  ghost.sticks.right = { ...ghost.sticks.right, innerDeadzone: 0.08, curve: "exponential", exponent: 1.6 };

  const race = defaultProfile("Yarış", "c3b8f0e1a2d94f6b8e7c5a3d1f9b2e46");
  race.games = ["ForzaHorizon5.exe", "ACC.exe"];
  race.lightbar = { effect: "battery", color: "#20E040", color2: "#000000", speed: 1, brightness: "high" };
  race.playerLeds = { mode: "custom", number: 1, mask: 0b10001 };
  race.triggers.left = { mode: "slope", params: { start: 1, end: 9, startStrength: 2, endStrength: 8 }, zones: null, frequency: 0 };
  race.triggers.right = { mode: "zones", params: {}, zones: [1, 1, 2, 2, 2, 3, 3, 3, 4, 4], frequency: 0 };
  race.triggerResponse.right = { deadzone: 8, max: 240, curve: "exponential", exponent: 1.4, points: null };
  race.sticks.left = { ...race.sticks.left, innerDeadzone: 0.03, shape: "square" };
  race.macros = [
    {
      id: "5e1d7c3a9b2f4e60a8c4d2b1f0e9a736",
      name: "Fotoğraf modu",
      repeat: "once",
      steps: [
        { op: "tap", key: "p" },
        { op: "delay", ms: 250 },
        { op: "tap", key: "f12" },
      ],
    },
  ];
  race.mapping.buttons.create = { ...defaultBinding(), press: { kind: "macro", macroId: race.macros[0].id } };
  race.emulation = { mode: "xbox360", remap: { cross: "circle", circle: "cross" }, swapSticks: false, hideMappedButtons: true };

  return [base, ghost, race];
}

const recentExes = [
  "GhostOfTsushima.exe",
  "ForzaHorizon5.exe",
  "explorer.exe",
  "steam.exe",
  "Code.exe",
  "EldenRing.exe",
  "firefox.exe",
  "Discord.exe",
];

const audioApps: AudioApp[] = [
  { pid: 1001, name: "Spotify.exe", display: "Spotify" },
  { pid: 1002, name: "firefox.exe", display: "Firefox" },
];

export function createSimulator(emit: (m: HostMessage) => void) {
  let profiles = sampleProfiles();
  let settings = normalizeSettings({
    ...defaultSettings(profiles[0].id),
    controllerDefaults: { "A0:AB:51:12:34:56": profiles[0].id },
  });
  let activeId = profiles[0].id;
  let activeReason = "controllerDefault";
  let timer: number | undefined;
  // Phase 1 test messages override the profile until "reset".
  let overrides: Partial<Output> = {};
  const preview: { left: TriggerConfig | null; right: TriggerConfig | null } = { left: null, right: null };
  let reports = 0;
  let tick = 0;
  let battery = 85;
  let mappingPaused = false;
  // Virtual controller: usbip-win2 starts missing so the install flow can be tried.
  const drivers: Record<DriverKind, { state: DriverState; version: string | null }> = {
    usbIp: { state: "missing", version: null },
    hidHide: { state: "ready", version: "1.5.230" },
  };
  const installing = new Set<DriverKind>();
  let emuError: string | null = null;
  let steamNoticed = false;
  let lastHaptics: HapticStatus | undefined;

  const active = () => profiles.find((p) => p.id === activeId) ?? profiles[0];

  let output: Output = {
    type: "output",
    lightbar: "#3060FF",
    brightness: 0,
    playerLeds: 0,
    micLed: 0,
    rumbleLeft: 0,
    rumbleRight: 0,
    leftTrigger: "off",
    rightTrigger: "off",
  };

  const status = () =>
    emit({
      type: "status",
      connected: true,
      model: "DualSense",
      link: "Simulated",
      mac: "A0:AB:51:12:34:56",
      firmware: "0x0430",
      version: "dev",
      activeProfileId: activeId,
      activeReason,
      mapping: { active: active().mapping.enabled && !mappingPaused, paused: mappingPaused, blockedBy: null },
      elevated: false,
    });
  const sendProfiles = () => emit({ type: "profiles", list: clone(profiles), activeId });

  function sendHaptics(force = false, gamePlaying = emulationStatus().audio === "playing") {
    const h = active().haptics;
    const source = h.audio === "off" ? h.speaker : h.audio;
    const app = h.audio === "off" ? h.speakerApp : h.app;
    const missingApp = [h.audio === "app" ? h.app : null, h.speaker === "app" ? h.speakerApp : null]
      .find((name) => name !== null && !audioApps.some((a) => a.name.toLowerCase() === name.toLowerCase()));
    const next: HapticStatus = {
      type: "haptics",
      streaming: source !== "off" || h.clicks || h.rumbleToHaptics ||
        (gamePlaying && h.gameAudio !== "off"),
      source: source === "app" ? `app:${app}` : source,
      problem: missingApp !== undefined
        ? missingApp ? `${missingApp} çalışmıyor.` : "Uygulamanın dosya adı boş."
        : null,
    };
    if (force || next.streaming !== lastHaptics?.streaming || next.source !== lastHaptics?.source || next.problem !== lastHaptics?.problem) {
      lastHaptics = next;
      emit(next);
    }
  }

  function emulationStatus(): EmulationStatus {
    const profile = active();
    const mode = profile.emulation.mode;
    const ready = drivers.usbIp.state === "ready" && (!settings.emulation.hideController || drivers.hidHide.state === "ready");
    const attached = settings.emulation.enabled && mode !== "off" && ready && emuError === null;
    const audio = attached && mode === "dualSense" && settings.emulation.audio
      ? profile.games.length > 0 ? "playing" : "ready"
      : "off";
    return {
      type: "emulation",
      available: true,
      enabled: settings.emulation.enabled,
      mode: attached ? mode : "off",
      attached,
      audio,
      hidden: attached && settings.emulation.hideController,
      drivers: (Object.keys(drivers) as DriverKind[]).map((kind) => ({ kind, state: drivers[kind].state, version: drivers[kind].version, detail: null })),
      latency: attached ? { p50Ms: 2.1 + Math.random() * 0.4, p95Ms: 3.8 + Math.random() * 0.8, samples: 512 } : { p50Ms: 0, p95Ms: 0, samples: 0 },
      error: emuError,
    };
  }

  function sendEmulation() {
    const e = emulationStatus();
    if (e.attached && !steamNoticed) {
      steamNoticed = true;
      emit({ type: "notice", kind: "emulation", message: "Steam Input bu oyunda açıksa çift kontrolcü görünür; oyunun Steam ayarlarında Steam Input'u kapatın." });
    }
    emit(e);
    sendHaptics(false, e.audio === "playing");
  }

  function install(kind: DriverKind) {
    if (installing.has(kind)) return emit({ type: "error", message: "Kurulum zaten sürüyor." });
    installing.add(kind);
    const steps = ["Kurulum dosyası indiriliyor…", "İmza ve SHA-256 denetleniyor…", "Kurulum başlatıldı, Windows izin isteyebilir…"];
    steps.forEach((message, i) => window.setTimeout(() => emit({ type: "driver.progress", kind, message, done: false, ok: false }), 400 + i * 900));
    window.setTimeout(() => {
      installing.delete(kind);
      drivers[kind] = { state: "ready", version: kind === "usbIp" ? "0.9.8.1" : "1.5.230" };
      emit({ type: "driver.progress", kind, message: "Kuruldu. Kurulumu bitirmek için bilgisayarı yeniden başlatın.", done: true, ok: true });
      sendEmulation();
    }, 400 + steps.length * 900);
  }

  function computeOutput(): Output {
    const p = active();
    const t = performance.now() / 1000;
    return {
      ...output,
      lightbar: effectColor(p.lightbar, t, battery, p.playerLeds.number),
      brightness: { high: 0, medium: 1, low: 2 }[p.lightbar.brightness],
      playerLeds: ledMask(p.playerLeds),
      micLed: { off: 0, on: 1, pulse: 2 }[p.micLed],
      leftTrigger: (preview.left ?? p.triggers.left).mode,
      rightTrigger: (preview.right ?? p.triggers.right).mode,
      ...overrides,
    };
  }

  function pushOutput() {
    const next = computeOutput();
    if (JSON.stringify(next) !== JSON.stringify(output)) {
      output = next;
      emit(output);
    }
  }

  function activate(id: string, reason: string) {
    activeId = id;
    activeReason = reason;
    status();
    sendProfiles();
    sendHaptics();
    pushOutput();
  }

  function calibrate() {
    let step = 0;
    const id = window.setInterval(() => {
      step++;
      if (step <= 10) {
        emit({ type: "notice", kind: "gyroCalibration", message: "Kontrolcüyü masada sabit tut", progress: step / 10 });
      }
      if (step === 10) {
        window.clearInterval(id);
        window.setTimeout(() => emit({ type: "notice", kind: "gyroCalibration", message: "Gyro kalibre edildi. Kayma −0,3 °/s düzeltildi.", progress: 1 }), 150);
      }
    }, 300);
    emit({ type: "notice", kind: "gyroCalibration", message: "Kontrolcüyü masada sabit tut", progress: 0 });
  }

  function simTick() {
    const t = performance.now() / 1000;
    const wave = (f: number, ph = 0) => Math.sin(t * f + ph);
    reports += 26;
    tick++;
    const buttons: string[] = [];
    if (wave(1.3) > 0.85) buttons.push("Cross");
    if (wave(0.9, 2) > 0.9) buttons.push("R1");
    if (wave(0.45, 1) > 0.94) buttons.push("Up");
    if (wave(0.7, 4) > 0.95) buttons.push("Triangle");
    const left: [number, number] = [128 + Math.round(wave(1.1) * 100), 128 + Math.round(wave(1.7) * 100)];
    const right: [number, number] = [128 + Math.round(wave(0.6, 1) * 60), 128 + Math.round(wave(0.8, 1) * 60)];
    const p = active();
    emit({
      type: "input",
      buttons,
      left,
      right,
      l2: Math.max(0, Math.round(wave(0.7) * 255)),
      r2: Math.max(0, Math.round(wave(0.5, 3) * 255)),
      gyro: [wave(2) * 40, wave(1.5) * 20, wave(1) * 10],
      accel: [wave(0.3) * 0.2, 0.98, wave(0.4) * 0.15],
      touch: wave(0.4) > 0.3 ? [{ id: 1, x: 960 + Math.round(wave(1.2) * 700), y: 540 + Math.round(wave(0.9) * 400) }] : [],
      battery,
      charging: false,
      headphones: false,
      reports,
      badCrc: 0,
      leftProcessed: processStick(rawAxis(left[0]), rawAxis(left[1]), p.sticks.left),
      rightProcessed: processStick(rawAxis(right[0]), rawAxis(right[1]), p.sticks.right),
    });
    // Animated effects: about 10 Hz is plenty for a preview.
    if (tick % 3 === 0) pushOutput();
    if (tick % 30 === 0) sendEmulation();
    // Slow drain so the low battery notice shows up once in a long session.
    if (tick % 1800 === 0 && battery > 5) {
      battery--;
      if (settings.lowBatteryWarnings.includes(battery)) emit({ type: "notice", kind: "lowBattery", message: `Pil %${battery}` });
    }
  }

  return function simulate(m: Msg) {
    switch (m.type) {
      case "hello":
        status();
        sendProfiles();
        emit({ type: "settings", settings: clone(settings) });
        output = computeOutput();
        emit(output);
        sendEmulation();
        sendHaptics(true);
        timer ??= window.setInterval(simTick, 33);
        return;

      case "profile.save": {
        const p = normalizeProfile(m.profile as Profile);
        const i = profiles.findIndex((x) => x.id === p.id);
        if (i >= 0) profiles[i] = p;
        else profiles = [...profiles, p];
        sendProfiles();
        if (p.id === activeId) status();
        sendHaptics();
        pushOutput();
        sendEmulation();
        return;
      }
      case "profile.delete": {
        if (profiles.length <= 1) return emit({ type: "error", message: "Son profil silinemez." });
        profiles = profiles.filter((p) => p.id !== m.id);
        if (settings.defaultProfileId === m.id) settings = { ...settings, defaultProfileId: profiles[0].id };
        if (activeId === m.id) activate(settings.defaultProfileId, "default");
        else sendProfiles();
        return;
      }
      case "profile.duplicate": {
        const src = profiles.find((p) => p.id === m.id);
        if (!src) return;
        profiles = [...profiles, { ...clone(src), id: newId(), name: String(m.name), games: [] }];
        sendProfiles();
        return;
      }
      case "profile.activate":
        if (profiles.some((p) => p.id === m.id)) {
          emuError = null;
          activate(String(m.id), "manual");
          sendEmulation();
        }
        return;
      case "profile.import": {
        try {
          const parsed = JSON.parse(String(m.json)) as Partial<Profile>;
          if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) throw new Error();
          const p = normalizeProfile({ ...parsed, id: newId(), name: parsed.name || "İçe aktarılan" });
          // Shared profiles must not start programs.
          let removed = 0;
          const strip = (a: Action | null): Action | null => (a && a.kind === "run" ? (removed++, { kind: "none" }) : a);
          const stripBinding = (b: Binding): Binding => ({ ...b, press: strip(b.press)!, hold: strip(b.hold), double: strip(b.double) });
          for (const map of [p.mapping.buttons, p.mapping.layer]) for (const k of Object.keys(map) as (keyof typeof map)[]) map[k] = stripBinding(map[k]!);
          p.mapping.touchpad.zones = p.mapping.touchpad.zones.map(stripBinding);
          for (const d of ["up", "down", "left", "right"] as const) p.mapping.touchpad.swipes[d] = strip(p.mapping.touchpad.swipes[d])!;
          profiles = [...profiles, p];
          sendProfiles();
          const note = removed > 0 ? ` ${removed} program çalıştırma eylemi kaldırıldı.` : "";
          emit({ type: "notice", kind: "info", message: `“${p.name}” içe aktarıldı.${note}` });
        } catch {
          emit({ type: "error", message: "JSON okunamadı. Dosyanın bir Tactile profili olduğundan emin ol." });
        }
        return;
      }
      case "profile.export": {
        const p = profiles.find((x) => x.id === m.id);
        if (p) emit({ type: "export", id: p.id, json: JSON.stringify(p, null, 2) });
        return;
      }
      case "trigger.preview":
        preview[m.side === "left" ? "left" : "right"] = (m.config as TriggerConfig | null) ?? null;
        pushOutput();
        return;
      case "settings.save":
        settings = normalizeSettings(m.settings as AppSettings);
        emit({ type: "settings", settings: clone(settings) });
        sendEmulation();
        return;
      case "emulation.status":
        sendEmulation();
        return;
      case "emulation.retry":
        emuError = null;
        sendEmulation();
        return;
      case "emulation.restore":
        settings = { ...settings, emulation: { ...settings.emulation, enabled: false } };
        emit({ type: "settings", settings: clone(settings) });
        emit({ type: "notice", kind: "emulation", message: "Kontrolcü geri getirildi: sanal kontrolcü kaldırıldı, gizleme kapatıldı, sanal kontrolcü ayarı kapandı." });
        sendEmulation();
        return;
      case "driver.install":
        install(m.kind === "hidHide" ? "hidHide" : "usbIp");
        return;
      case "gyro.calibrate":
        calibrate();
        return;
      case "foreground.pick":
        emit({ type: "foreground", recent: [...recentExes] });
        return;
      case "audio.apps":
        emit({ type: "audioApps", apps: audioApps });
        return;
      case "mapping.pause":
        mappingPaused = m.paused === true;
        status();
        return;
      case "run.pick":
        window.setTimeout(() => emit({ type: "runPicked", path: String.raw`C:\Program Files\Example\example.exe` }), 200);
        return;
      case "app.restartElevated":
        emit({ type: "notice", kind: "info", message: "Önizlemede yeniden başlatma yok. Uygulamada yönetici izni istenir." });
        return;

      // Phase 1 live overrides
      case "lightbar":
        overrides.lightbar = m.color as string;
        break;
      case "brightness":
        overrides.brightness = m.level as number;
        break;
      case "playerLeds":
        overrides.playerLeds = m.mask as number;
        break;
      case "micLed":
        overrides.micLed = m.mode as number;
        break;
      case "rumble":
        overrides = { ...overrides, rumbleLeft: m.left as number, rumbleRight: m.right as number };
        break;
      case "trigger":
        overrides[m.side === "left" ? "leftTrigger" : "rightTrigger"] = String(m.mode);
        break;
      case "reset":
        overrides = {};
        output = { ...output, rumbleLeft: 0, rumbleRight: 0 };
        break;
      default:
        return;
    }
    pushOutput();
  };
}

