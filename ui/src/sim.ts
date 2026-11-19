import { L } from "./i18n";
// Browser-only stand-in for the host. Keeps profiles and settings in memory and fakes
// controller input so every screen can be used without hardware.

import type { Action, AppSettings, AudioApp, Binding, ControllerStatus, DriverKind, DriverState, EmulationStatus, HapticStatus, HostMessage, Output, Profile, TelemetryConfig, TelemetryRule, TelemetryValues, TriggerConfig, UdpStatus, UpdateStatus } from "./host";
import {
  clone,
  defaultBinding,
  defaultProfile,
  defaultSettings,
  defaultTelemetryRule,
  defaultTrigger,
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
  const base = defaultProfile(L("Default", "Varsayılan"), "0d1f6c2a8e4b4c39a1f05e7d3b2c9a10");

  const ghost = defaultProfile("Ghost of Tsushima", "7a2e91c4d05f4b8e9c3a1d6f2e8b4a57");
  ghost.games = ["GhostOfTsushima.exe"];
  ghost.lightbar = { effect: "breathe", color: "#E8E2D0", color2: "#401010", speed: 0.4, brightness: "medium" };
  ghost.triggers.left = { mode: "feedback", params: { start: 2, strength: 3 }, zones: null, frequency: 0 };
  ghost.triggers.right = { mode: "bow", params: { start: 1, end: 6, strength: 6, snap: 4 }, zones: null, frequency: 0 };
  ghost.sticks.right = { ...ghost.sticks.right, innerDeadzone: 0.08, curve: "exponential", exponent: 1.6 };

  const race = defaultProfile(L("Racing", "Yarış"), "c3b8f0e1a2d94f6b8e7c5a3d1f9b2e46");
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
      name: L("Photo mode", "Fotoğraf modu"),
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

function forzaTemplate(): TelemetryConfig {
  const feedback = (start: number): TriggerConfig => ({ ...defaultTrigger(), mode: "feedback", params: { start, strength: 1 } });
  const vibration = (start: number, amplitude: number, frequency: number): TriggerConfig =>
    ({ ...defaultTrigger(), mode: "vibration", params: { start, amplitude, frequency } });
  const rule = (signal: TelemetryRule["signal"], target: TelemetryRule["target"], patch: Partial<TelemetryRule>): TelemetryRule =>
    ({ ...defaultTelemetryRule(), trigger: null, signal, target, ...patch });
  return { rules: [
    rule("brake", "leftTrigger", { trigger: feedback(1), param: "strength", min: 1, max: 5 }),
    rule("abs", "leftTrigger", { from: 0.5, trigger: vibration(1, 6, 35) }),
    rule("rpm", "rightTrigger", { trigger: feedback(2), param: "strength", min: 1, max: 4 }),
    rule("traction", "rightTrigger", { from: 0.5, trigger: vibration(2, 5, 40) }),
    rule("rpm", "lightbar", { colorFrom: { r: 0, g: 255, b: 0 }, colorTo: { r: 255, g: 0, b: 0 }, blinkAt: 0.95 }),
    rule("rpm", "playerLeds", { from: 0.4, max: 5 }),
    rule("rumbleStrip", "rumbleRight", { from: 0.5, to: 0.5, max: 120 }),
  ] };
}

function sampleTelemetry(time: number): TelemetryValues {
  const wave = (speed: number, offset = 0) => (1 + Math.sin(time * speed + offset)) / 2;
  const gas = wave(0.65);
  const brake = 1 - gas;
  const speedKmh = 35 + wave(0.65, -0.7) * 175;
  const slipFront = wave(1.3) * brake * 1.8;
  const slipRear = wave(1.1, 2) * gas * 1.8;
  return {
    raceOn: 1, speedKmh, rpm: 0.2 + wave(1.2) * 0.8, gas, brake,
    clutch: 0, handbrake: 0, gear: 1 + Math.floor(speedKmh / 36), steer: Math.sin(time * 0.8),
    slipFront, slipRear, surface: wave(3.2) * 0.35, rumbleStrip: Number(wave(2) > 0.9),
    boost: gas * 15, abs: Number(slipFront > 1), traction: Number(slipRear > 1),
  };
}

export function createSimulator(emit: (m: HostMessage) => void) {
  const devices = [
    { mac: "A0:AB:51:12:34:56", model: "DualSense", link: "Bluetooth", battery: 85, charging: false },
    { mac: "A0:AB:51:12:34:57", model: "DualSenseEdge", link: "Usb", battery: 63, charging: true },
    { mac: "A0:AB:51:12:34:58", model: "DualSense", link: "Bluetooth", battery: 48, charging: false },
  ] satisfies Omit<ControllerStatus, "index" | "primary" | "profileId" | "profileName">[];
  const slots: (typeof devices[number] | undefined)[] = [devices[0], devices[1], undefined, undefined];
  const sessionProfiles = new Map<string, string>();
  let thirdConnected = false;
  let profiles = sampleProfiles();
  let settings = normalizeSettings({
    ...defaultSettings(profiles[0].id),
    controllerDefaults: { [devices[0].mac]: profiles[0].id, [devices[1].mac]: profiles[1].id },
  });
  settings.integration = { ...settings.integration, udpEnabled: true, forzaEnabled: true };
  const previewOptions = new URLSearchParams(window.location.search);
  const nextVersion = "0.10.0";
  let failNextCheck = previewOptions.get("update") === "error";
  let update: UpdateStatus = {
    type: "update.status", state: "idle", current: "0.9.0",
    latest: null, notes: null, released: null, progress: null,
    portable: previewOptions.get("portable") === "1",
    error: null, message: null, lastCheck: null,
  };
  let udpPackets = 3;
  let udpLastPacket = performance.now();
  let udpHolding = true;
  let udpFormat: UdpStatus["format"] = "dsx";
  let forzaPackets = 1;
  let telemetry = sampleTelemetry(performance.now() / 1000);
  let activeId = profiles[0].id;
  let activeReason = "controllerDefault";
  let timer: number | undefined;
  // Phase 1 test messages override the profile until "reset".
  let overrides: Partial<Output> = {};
  const preview: { left: TriggerConfig | null; right: TriggerConfig | null } = { left: null, right: null };
  let reports = 0;
  let tick = 0;
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

  function status() {
    const primary = slots[0];
    emit({
      type: "status",
      connected: !!primary,
      controllers: slots.flatMap((device, index) => {
        if (!device) return [];
        const profile = index === 0 ? active() :
          profiles.find((p) => p.id === sessionProfiles.get(device.mac)) ??
          profiles.find((p) => p.id === settings.controllerDefaults[device.mac]) ??
          active();
        return [{ ...device, index, primary: index === 0, profileId: profile.id, profileName: profile.name }];
      }),
      model: primary?.model,
      link: primary?.link,
      mac: primary?.mac,
      firmware: primary ? "0x0430" : undefined,
      version: update.current,
      activeProfileId: activeId,
      activeReason,
      mapping: { active: !!primary && active().mapping.enabled && !mappingPaused, paused: mappingPaused, blockedBy: null },
      elevated: false,
      portable: update.portable,
    });
  }
  const sendProfiles = () => emit({ type: "profiles", list: clone(profiles), activeId });

  function syncSlots() {
    const previousPrimary = slots[0];
    let secondaryCount = 0;
    for (const [index, device] of slots.entries()) {
      if (!device) continue;
      if ((index > 0 && secondaryCount >= settings.maxControllers - 1) || (device === devices[2] && !thirdConnected)) {
        sessionProfiles.delete(device.mac);
        slots[index] = undefined;
      } else if (index > 0) {
        secondaryCount++;
      }
    }
    for (const device of thirdConnected ? devices : devices.slice(0, 2)) {
      if (slots.includes(device)) continue;
      if (!slots[0] && (settings.maxControllers === 1 || device.mac === settings.primaryController)) {
        slots[0] = device;
      } else if (secondaryCount < settings.maxControllers - 1) {
        const index = slots.findIndex((slot, i) => i > 0 && !slot);
        slots[index] = device;
        secondaryCount++;
      }
    }
    if (slots[0] && slots[0] !== previousPrimary) selectPrimaryProfile();
    else status();
  }

  function selectPrimaryProfile() {
    const id = slots[0] ? settings.controllerDefaults[slots[0].mac] : undefined;
    if (id && profiles.some((p) => p.id === id)) activate(id, "controllerDefault");
    else activate(settings.defaultProfileId, "default");
  }

  function sendUpdate() {
    emit({
      ...update,
      notes: update.latest ? L("Improved controller setup and update checks.", "Kontrolcü kurulumu ve güncelleme denetimi iyileştirildi.") : null,
      message: update.error === "network" ? L("Could not check for updates. Check your connection.", "Güncellemeler denetlenemedi. Bağlantınızı kontrol edin.") : null,
    });
  }

  function setUpdate(patch: Partial<Omit<UpdateStatus, "type">>) {
    update = { ...update, ...patch };
    sendUpdate();
  }

  function checkUpdate() {
    if (update.state === "checking" || update.state === "downloading") return;
    setUpdate({ state: "checking", latest: null, released: null, progress: null, error: null });
    window.setTimeout(() => {
      const lastCheck = new Date().toISOString();
      if (failNextCheck) {
        failNextCheck = false;
        setUpdate({ state: "error", error: "network", lastCheck });
      } else if (update.current === nextVersion) {
        setUpdate({ state: "upToDate", lastCheck });
      } else {
        setUpdate({ state: "available", latest: nextVersion, released: "2026-12-01", lastCheck });
      }
    }, 600);
  }

  function downloadUpdate() {
    if (update.state !== "available" || update.portable) return;
    let progress = 0;
    setUpdate({ state: "downloading", progress });
    const download = window.setInterval(() => {
      progress += 0.25;
      if (progress < 1) {
        setUpdate({ progress });
      } else {
        window.clearInterval(download);
        setUpdate({ state: "ready", progress: null });
      }
    }, 250);
  }

  function sendIntegration() {
    const s = settings.integration;
    const elapsed = Math.floor(performance.now() - udpLastPacket);
    if (!s.udpEnabled || (s.releaseAfterSeconds > 0 && elapsed >= s.releaseAfterSeconds * 1000)) udpHolding = false;
    const forzaState = !s.forzaEnabled ? "off" : s.forzaPort === s.udpPort ? "portConflict" : "receiving";
    emit({
      type: "integration",
      udp: {
        state: s.udpEnabled ? "listening" : "off", port: s.udpPort,
        packets: udpPackets, rejected: 0, unsupported: 0, lastPacketAgoMs: elapsed,
        holding: udpHolding, format: udpFormat,
      },
      forza: {
        state: forzaState, port: s.forzaPort, format: forzaState === "receiving" ? "fh" : null,
        packets: forzaPackets, rejected: 0, values: forzaState === "receiving" ? telemetry : null,
      },
    });
  }

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
        ? missingApp ? L(`${missingApp} is not running.`, `${missingApp} çalışmıyor.`) : L("Enter an app file name.", "Uygulamanın dosya adı boş.")
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
      slots: [
        { index: 0, mode: attached ? mode : "off", attached, hidden: attached && settings.emulation.hideController, error: emuError },
        ...slots.flatMap((device, index): EmulationStatus["slots"] => {
          if (!device || index === 0) return [];
          const selected = profiles.find((p) => p.id === sessionProfiles.get(device.mac)) ??
            profiles.find((p) => p.id === settings.controllerDefaults[device.mac]) ?? profile;
          const mode = selected.emulation.mode;
          const attached = settings.emulation.enabled && ready && mode !== "off";
          return [{ index, mode: attached ? mode : "off", attached, hidden: attached && settings.emulation.hideController }];
        }),
      ],
    };
  }

  function sendEmulation() {
    const e = emulationStatus();
    if (e.attached && !steamNoticed) {
      steamNoticed = true;
      emit({ type: "notice", kind: "emulation", message: L("If the game shows two controllers, turn off Steam Input in its Steam settings.", "Steam Input bu oyunda açıksa çift kontrolcü görünür; oyunun Steam ayarlarında Steam Input'u kapatın.") });
    }
    emit(e);
    sendHaptics(false, e.audio === "playing");
  }

  function install(kind: DriverKind) {
    if (installing.has(kind)) return emit({ type: "error", message: L("An installation is already in progress.", "Kurulum zaten sürüyor.") });
    installing.add(kind);
    const steps = [
      () => L("Downloading installer…", "Kurulum dosyası indiriliyor…"),
      () => L("Checking signature and SHA-256…", "İmza ve SHA-256 denetleniyor…"),
      () => L("Setup started. Windows may ask for permission…", "Kurulum başlatıldı, Windows izin isteyebilir…"),
    ];
    steps.forEach((step, i) => window.setTimeout(() => emit({ type: "driver.progress", kind, message: step(), done: false, ok: false }), 400 + i * 900));
    window.setTimeout(() => {
      installing.delete(kind);
      drivers[kind] = { state: "ready", version: kind === "usbIp" ? "0.9.8.1" : "1.5.230" };
      emit({ type: "driver.progress", kind, message: L("Installed. Restart your PC to finish setup.", "Kuruldu. Kurulumu bitirmek için bilgisayarı yeniden başlatın."), done: true, ok: true });
      sendEmulation();
    }, 400 + steps.length * 900);
  }

  function computeOutput(): Output {
    const p = active();
    const t = performance.now() / 1000;
    return {
      ...output,
      lightbar: effectColor(p.lightbar, t, slots[0]?.battery ?? 0, p.playerLeds.number),
      brightness: { high: 0, medium: 1, low: 2 }[p.lightbar.brightness],
      playerLeds: ledMask(p.playerLeds),
      micLed: { off: 0, on: 1, pulse: 2 }[p.micLed],
      rumbleLeft: 0,
      rumbleRight: 0,
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
        emit({ type: "notice", kind: "gyroCalibration", message: L("Keep the controller still on a flat surface", "Kontrolcüyü masada sabit tut"), progress: step / 10 });
      }
      if (step === 10) {
        window.clearInterval(id);
        window.setTimeout(() => emit({ type: "notice", kind: "gyroCalibration", message: L("Gyro calibrated. Corrected −0.3 °/s drift.", "Gyro kalibre edildi. Kayma −0,3 °/s düzeltildi."), progress: 1 }), 150);
      }
    }, 300);
    emit({ type: "notice", kind: "gyroCalibration", message: L("Keep the controller still on a flat surface", "Kontrolcüyü masada sabit tut"), progress: 0 });
  }

  function simTick() {
    const t = performance.now() / 1000;
    const wave = (f: number, ph = 0) => Math.sin(t * f + ph);
    const primary = slots[0];
    reports += 26;
    tick++;
    const buttons: string[] = [];
    if (wave(1.3) > 0.85) buttons.push("Cross");
    if (wave(0.9, 2) > 0.9) buttons.push("R1");
    if (wave(0.45, 1) > 0.94) buttons.push("Up");
    if (wave(0.7, 4) > 0.95) buttons.push("Triangle");
    if (primary?.model === "DualSenseEdge") {
      if (wave(1.2, 1) > 0.7) buttons.push("Fn1");
      if (wave(1.1, 3) > 0.7) buttons.push("Fn2");
      if (wave(1.6, 2) > 0.7) buttons.push("PaddleLeft");
      if (wave(1.4, 4) > 0.7) buttons.push("PaddleRight");
    }
    const left: [number, number] = [128 + Math.round(wave(1.1) * 100), 128 + Math.round(wave(1.7) * 100)];
    const right: [number, number] = [128 + Math.round(wave(0.6, 1) * 60), 128 + Math.round(wave(0.8, 1) * 60)];
    const p = active();
    if (primary) emit({
      type: "input",
      mac: primary.mac,
      buttons,
      left,
      right,
      l2: Math.max(0, Math.round(wave(0.7) * 255)),
      r2: Math.max(0, Math.round(wave(0.5, 3) * 255)),
      gyro: [wave(2) * 40, wave(1.5) * 20, wave(1) * 10],
      accel: [wave(0.3) * 0.2, 0.98, wave(0.4) * 0.15],
      touch: wave(0.4) > 0.3 ? [{ id: 1, x: 960 + Math.round(wave(1.2) * 700), y: 540 + Math.round(wave(0.9) * 400) }] : [],
      battery: primary.battery,
      charging: primary.charging,
      headphones: false,
      reports,
      badCrc: 0,
      leftProcessed: processStick(rawAxis(left[0]), rawAxis(left[1]), p.sticks.left),
      rightProcessed: processStick(rawAxis(right[0]), rawAxis(right[1]), p.sticks.right),
    });
    // Animated effects: about 10 Hz is plenty for a preview.
    if (tick % 3 === 0) pushOutput();
    if (tick % 30 === 0) {
      sendEmulation();
      const s = settings.integration;
      if (s.forzaEnabled && s.forzaPort !== s.udpPort) {
        forzaPackets += 30;
        telemetry = sampleTelemetry(t);
      }
      sendIntegration();
    }
    // Slow drain so the low battery notice shows up once in a long session.
    if (tick % 1800 === 0 && primary && !primary.charging && primary.battery > 5) {
      const battery = --primary.battery;
      status();
      if (settings.lowBatteryWarnings.includes(battery)) emit({ type: "notice", kind: "lowBattery", message: L(`Battery ${battery}%`, `Pil %${battery}`) });
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
        sendIntegration();
        sendHaptics(true);
        sendUpdate();
        timer ??= window.setInterval(simTick, 33);
        return;

      case "profile.save": {
        const p = normalizeProfile(m.profile as Profile);
        const i = profiles.findIndex((x) => x.id === p.id);
        if (i >= 0) profiles[i] = p;
        else profiles = [...profiles, p];
        sendProfiles();
        status();
        sendHaptics();
        pushOutput();
        sendEmulation();
        return;
      }
      case "profile.delete": {
        if (profiles.length <= 1) return emit({ type: "error", message: L("The last profile cannot be deleted.", "Son profil silinemez.") });
        profiles = profiles.filter((p) => p.id !== m.id);
        for (const [mac, id] of sessionProfiles) if (id === m.id) sessionProfiles.delete(mac);
        if (settings.defaultProfileId === m.id) settings = { ...settings, defaultProfileId: profiles[0].id };
        if (activeId === m.id) activate(settings.defaultProfileId, "default");
        else {
          sendProfiles();
          status();
        }
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
          const p = normalizeProfile({ ...parsed, id: newId(), name: parsed.name || L("Imported", "İçe aktarılan") });
          // Shared profiles must not start programs.
          let removed = 0;
          const strip = (a: Action | null): Action | null => (a && a.kind === "run" ? (removed++, { kind: "none" }) : a);
          const stripBinding = (b: Binding): Binding => ({ ...b, press: strip(b.press)!, hold: strip(b.hold), double: strip(b.double) });
          for (const map of [p.mapping.buttons, p.mapping.layer]) for (const k of Object.keys(map) as (keyof typeof map)[]) map[k] = stripBinding(map[k]!);
          p.mapping.touchpad.zones = p.mapping.touchpad.zones.map(stripBinding);
          for (const d of ["up", "down", "left", "right"] as const) p.mapping.touchpad.swipes[d] = strip(p.mapping.touchpad.swipes[d])!;
          profiles = [...profiles, p];
          sendProfiles();
          const note = removed > 0 ? L(` Removed ${removed} launch actions.`, ` ${removed} program çalıştırma eylemi kaldırıldı.`) : "";
          emit({ type: "notice", kind: "info", message: L(`Imported “${p.name}”.${note}`, `“${p.name}” içe aktarıldı.${note}`) });
        } catch {
          emit({ type: "error", message: L("Could not read the JSON. Choose a TriggerForge profile.", "JSON okunamadı. Dosyanın bir Tactile profili olduğundan emin ol.") });
        }
        return;
      }
      case "profile.export": {
        const p = profiles.find((x) => x.id === m.id);
        if (p) emit({ type: "export", id: p.id, json: JSON.stringify(p, null, 2) });
        return;
      }
      case "logs.export":
        emit({ type: "logs.exported", ok: false, error: L("Log export is unavailable in preview.", "Önizlemede log arşivi oluşturulmaz.") });
        return;
      case "logs.open":
        emit({ type: "notice", kind: "info", message: L("The log folder is unavailable in preview.", "Önizlemede log klasörü açılmaz.") });
        return;
      case "telemetry.applyTemplate": {
        const index = profiles.findIndex((p) => p.id === m.profileId);
        if (index < 0) return emit({ type: "error", message: L("Profile not found.", "Profil bulunamadı.") });
        if (m.template !== "forza") return emit({ type: "error", message: L("Telemetry template not found.", "Telemetri şablonu bulunamadı.") });
        profiles[index] = { ...profiles[index], telemetry: forzaTemplate() };
        sendProfiles();
        return;
      }
      case "trigger.preview":
        preview[m.side === "left" ? "left" : "right"] = (m.config as TriggerConfig | null) ?? null;
        pushOutput();
        return;
      case "controller.setPrimary": {
        const index = slots.findIndex((device) => device && device.mac === m.mac);
        const device = slots[index];
        if (!device || index === 0) return;
        slots[index] = slots[0];
        slots[0] = device;
        sessionProfiles.delete(device.mac);
        settings = { ...settings, primaryController: device.mac };
        emit({ type: "settings", settings: clone(settings) });
        selectPrimaryProfile();
        sendEmulation();
        return;
      }
      case "controller.setProfile": {
        if (typeof m.index !== "number" || m.index === 0) return;
        const device = slots[m.index];
        if (!device || device.mac !== m.mac) return;
        const profile = profiles.find((p) => p.id === m.profileId);
        if (!profile) return;
        sessionProfiles.set(device.mac, profile.id);
        status();
        sendEmulation();
        return;
      }
      case "sim.toggleThirdController":
        if (settings.maxControllers < 3) return;
        thirdConnected = !thirdConnected;
        syncSlots();
        sendEmulation();
        return;
      case "settings.save": {
        const before = settings.integration;
        const language = settings.language;
        settings = normalizeSettings(m.settings as AppSettings);
        const after = settings.integration;
        if (after.udpEnabled && (!before.udpEnabled || before.udpPort !== after.udpPort)) {
          udpPackets += 3;
          udpLastPacket = performance.now();
          udpHolding = true;
          udpFormat = after.dsx ? "dsx" : "native";
        }
        emit({ type: "settings", settings: clone(settings) });
        syncSlots();
        if (language !== settings.language) sendUpdate();
        sendEmulation();
        sendIntegration();
        return;
      }
      case "update.check":
        checkUpdate();
        return;
      case "update.download":
        downloadUpdate();
        return;
      case "update.install":
        if (update.state === "ready" && update.latest && !update.portable) {
          setUpdate({ state: "upToDate", current: update.latest, latest: null, released: null });
          status();
        }
        return;
      case "update.openPage":
        if (update.state === "available" && update.portable) {
          emit({ type: "notice", kind: "info", message: L("The download page is unavailable in preview.", "Önizlemede indirme sayfası açılmaz.") });
        }
        return;
      case "integration.status":
        sendIntegration();
        return;
      case "integration.release":
        udpHolding = false;
        sendIntegration();
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
        emit({ type: "notice", kind: "emulation", message: L("Controller restored. The virtual controller was removed and emulation was turned off.", "Kontrolcü geri getirildi: sanal kontrolcü kaldırıldı, gizleme kapatıldı, sanal kontrolcü ayarı kapandı.") });
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
        emit({ type: "notice", kind: "info", message: L("Restart is unavailable in preview. The app would ask for administrator permission.", "Önizlemede yeniden başlatma yok. Uygulamada yönetici izni istenir.") });
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
        preview.left = null;
        preview.right = null;
        udpHolding = false;
        sendIntegration();
        break;
      default:
        return;
    }
    pushOutput();
  };
}

