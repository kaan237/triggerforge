using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Tactile.Core;
using Tactile.Core.Integration;
using Tactile.Core.Logging;
using Tactile.Core.Updates;
using Tactile.Device.DualSense;
using Tactile.Drivers;
using Tactile.Input.Windows;

namespace Tactile.Host;

/// <summary>
/// Translates UI messages into core calls and core state into UI messages (docs/contract.md).
/// UI thread only. Nothing is posted until the page says hello, except notices, which wait.
/// </summary>
internal sealed class Bridge
{
    private const int MaxQueued = 50;

    private readonly AppCore _core;
    private readonly Control _owner;
    private readonly Action<string> _postRaw;
    private readonly AppPaths _paths;
    private readonly UpdateService _updates;
    private readonly List<string> _queued = [];
    private OutputState? _lastOutput;
    private HapticStatus? _lastHaptics;
    private UpdateStatus? _lastUpdate;
    private bool _ready;

    public Bridge(AppCore core, Control owner, Action<string> postRaw, AppPaths paths, UpdateService updates)
    {
        _core = core;
        _owner = owner;
        _postRaw = postRaw;
        _paths = paths;
        _updates = updates;
    }

    public bool Ready => _ready;

    /// <summary>The UI asked to restart as administrator. Raised on the UI thread, outside any WebView2 event.</summary>
    public event Action? RestartElevatedRequested;

    /// <summary>The UI requested controller restoration. Raised on the UI thread.</summary>
    public event Action? RestoreRequested;

    public event Action? UpdateStarted;

    /// <summary>The page is going away; hold posts until the next hello.</summary>
    public void Detach()
    {
        _ready = false;
        // A focused key-capture field cannot unpause any more.
        _core.UiMappingPaused = false;
    }

    public void Handle(string json, bool appSource)
    {
        string? type = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement m = doc.RootElement;
            if (m.ValueKind != JsonValueKind.Object)
            {
                throw new BadMessage(new LocalizedMessage("The message must be an object.", "Mesaj bir nesne olmalı."));
            }

            type = m.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (type?.StartsWith("update.", StringComparison.Ordinal) == true && (!appSource || !_ready))
            {
                Log.Info("Ignored an update message before hello or from outside the application page.");
                return;
            }

            if ((type is "mapping.togglePause" or "backups.list" or "backups.restore") && (!appSource || !_ready))
            {
                return;
            }

            Dispatch(type, m);
        }
        catch (Exception e)
        {
            Log.Warn("Could not handle a UI message.", e);
            PostError(Describe(e));
            if (type == "backups.restore")
            {
                try
                {
                    PostBackups();
                }
                catch (Exception listError) when (listError is IOException or UnauthorizedAccessException)
                {
                    Log.Warn("Could not list profile backups after a restore error.", listError);
                }
            }
        }

        if (type is "lightbar" or "brightness" or "playerLeds" or "micLed" or "rumble" or "trigger" or "reset" or "trigger.preview")
        {
            PostOutputIfChanged();
        }
    }

    private void Dispatch(string? type, JsonElement m)
    {
        switch (type)
        {
            case "hello":
                _ready = true;
                PostSettings();
                PostStatus();
                PostUpdateStatus(force: true);
                _lastOutput = null;
                PostOutputIfChanged();
                PostProfiles();
                PostBackups();
                PostEmulation();
                PostIntegration();
                _lastHaptics = null;
                PostHapticsIfChanged();
                foreach (string q in _queued)
                {
                    _postRaw(q);
                }

                _queued.Clear();
                break;

            case "profile.save":
            {
                Profile p = Read<Profile>(m, "profile");
                _core.Profiles.Save(p, out IReadOnlyList<string> issues);
                if (issues.Count > 0)
                {
                    PostNotice(new Notice(NoticeKinds.Info, Text.Pick("Profile saved with corrections: ", "Profil kaydedildi, düzeltilenler: ") + string.Join("; ", issues)));
                }

                break;
            }

            case "profile.delete":
            {
                string id = Str(m, "id");
                if (_core.Profiles.Get(id) is null)
                {
                    throw new BadMessage(new LocalizedMessage("Profile not found.", "Profil bulunamadı."));
                }

                if (!_core.Profiles.Delete(id))
                {
                    throw new BadMessage(new LocalizedMessage("The last profile cannot be deleted.", "Son profil silinemez."));
                }

                break;
            }

            case "profile.duplicate":
            {
                string id = Str(m, "id");
                Profile source = _core.Profiles.Get(id) ?? throw new BadMessage(new LocalizedMessage("Profile not found.", "Profil bulunamadı."));
                string name = OptStr(m, "name") is { Length: > 0 } n ? n : source.Name + " (kopya)";
                _core.Profiles.Duplicate(id, name);
                break;
            }

            case "profile.activate":
                _core.ActivateProfile(Str(m, "id"));
                break;

            case "controller.setPrimary":
                _core.SetPrimaryController(Str(m, "mac"));
                PostStatus();
                break;

            case "controller.setProfile":
                if (!m.TryGetProperty("index", out JsonElement slotIndex) ||
                    slotIndex.ValueKind != JsonValueKind.Number || !slotIndex.TryGetInt32(out int index) ||
                    index is < 1 or > 3)
                {
                    throw new BadMessage(new LocalizedMessage("Controller index must be 1, 2 or 3.", "Kontrolcü indeksi 1, 2 veya 3 olmalı."));
                }

                if (!_core.TrySetSecondaryProfile(index, Str(m, "mac"), Str(m, "profileId")))
                {
                    Log.Info(Text.Pick($"Ignored a profile change for slot {index} because the controller changed.",
                        $"Kontrolcü değiştiği için {index}. yuva için profil değişikliği yok sayıldı."));
                    break;
                }

                PostStatus();
                break;

            case "profile.import":
            {
                Profile p = _core.Profiles.Import(Str(m, "json"), out int removed);
                string message = removed > 0
                    ? Text.Pick($"Imported “{p.Name}”. Removed {removed} launch actions for safety.", $"“{p.Name}” içe aktarıldı. Güvenlik için {removed} program çalıştırma eylemi kaldırıldı.")
                    : Text.Pick($"Imported “{p.Name}”.", $"“{p.Name}” içe aktarıldı.");
                PostNotice(new Notice(NoticeKinds.Info, message));
                break;
            }

            case "profile.export":
            {
                string id = Str(m, "id");
                if (_core.Profiles.Get(id) is null)
                {
                    throw new BadMessage(new LocalizedMessage("Profile not found.", "Profil bulunamadı."));
                }

                Post(new { type = "export", id, json = _core.Profiles.Export(id) });
                break;
            }

            case "trigger.preview":
            {
                bool left = Side(m);
                TriggerConfig? config = m.TryGetProperty("config", out JsonElement c) && c.ValueKind != JsonValueKind.Null
                    ? c.Deserialize<TriggerConfig>(TactileJson.Options)
                    : null;
                _core.PreviewTrigger(left, config);
                break;
            }

            case "settings.save":
                SaveSettings(Read<AppSettings>(m, "settings"));
                break;

            case "update.check":
                _ = _updates.CheckAsync();
                break;

            case "update.download":
                _ = _updates.DownloadAsync();
                break;

            case "update.install":
                // The elevation prompt must run outside the WebView2 message event.
                _owner.BeginInvoke(new Action(async () =>
                {
                    if (await _updates.InstallAsync(StartInstaller))
                    {
                        UpdateStarted?.Invoke();
                    }
                }));
                break;

            case "update.openPage":
                OpenUpdatePage();
                break;

            case "logs.export":
                _owner.BeginInvoke(ExportLogs);
                break;

            case "logs.open":
                OpenLogs();
                break;

            case "gyro.calibrate":
                if (_core.Controllers.Controller is null)
                {
                    throw new BadMessage(new LocalizedMessage("No controller connected.", "Kontrolcü bağlı değil."));
                }

                _core.StartGyroCalibration();
                break;

            case "mapping.pause":
                _core.UiMappingPaused = Bool(m, "paused");
                break;

            case "mapping.togglePause":
                _core.ToggleUserPause();
                PostStatus();
                break;

            case "backups.list":
                PostBackups();
                break;

            case "backups.restore":
            {
                string id = Str(m, "id");
                int restored = _core.RestoreProfileBackup(id);
                PostProfiles();
                PostStatus();
                PostBackups();
                PostNotice(new Notice(NoticeKinds.Info, $"Restored {restored} profiles from {id}.",
                    $"{id} yedeğinden {restored} profil geri yüklendi."));
                break;
            }

            // Modal UI must not run inside the WebView2 message event, so these run right after it.
            case "run.pick":
                _owner.BeginInvoke(PickRun);
                break;

            case "app.restartElevated":
                if (WindowsInputPlatform.IsElevated)
                {
                    PostNotice(new Notice(NoticeKinds.Info, Text.Pick("TriggerForge is already running as administrator.", "TriggerForge zaten yönetici olarak çalışıyor.")));
                    break;
                }

                _owner.BeginInvoke(() => RestartElevatedRequested?.Invoke());
                break;

            case "emulation.status":
                PostEmulation();
                break;

            case "integration.status":
                PostIntegration();
                break;

            case "integration.release":
                _core.ReleaseIntegration();
                PostIntegration();
                PostOutputIfChanged();
                break;

            case "telemetry.applyTemplate":
            {
                Profile profile = _core.Profiles.Get(Str(m, "profileId")) ?? throw new BadMessage(new LocalizedMessage("Profile not found.", "Profil bulunamadı."));
                if (Str(m, "template") != "forza")
                {
                    throw new BadMessage(new LocalizedMessage("Telemetry template not found.", "Telemetri şablonu bulunamadı."));
                }

                _core.Profiles.Save(profile with { Telemetry = TelemetryTemplates.Forza });
                break;
            }

            case "emulation.retry":
                (_core.Emulation ?? throw new BadMessage(Unavailable)).Retry();
                break;

            case "emulation.restore":
                _owner.BeginInvoke(() => RestoreRequested?.Invoke());
                break;

            case "driver.install":
                Install(Str(m, "kind") switch
                {
                    "usbIp" => DriverKind.UsbIp,
                    "hidHide" => DriverKind.HidHide,
                    _ => throw new BadMessage(new LocalizedMessage("kind must be usbIp or hidHide.", "kind usbIp ya da hidHide olmalı.")),
                });
                break;

            case "foreground.pick":
                Post(new { type = "foreground", recent = _core.RecentForeground });
                break;

            case "audio.apps":
                // Session enumeration is COM work that can stall on a busy audio device; keep it off the UI thread.
                Task.Run(() =>
                {
                    var apps = _core.ListAudioApps().Select(a => new { pid = a.ProcessId, name = a.ProcessName, display = a.DisplayName }).ToArray();
                    try
                    {
                        _owner.BeginInvoke(() => Post(new { type = "audioApps", apps }));
                    }
                    catch (InvalidOperationException e)
                    {
                        Log.Info("Window handle was unavailable for the audio app list.", e);
                    }
                });
                break;

            // Phase 1 live controls: layers over the profile until reset.
            case "lightbar":
            {
                Rgb color = ParseColor(Str(m, "color"));
                _core.SetOverride("live.lightbar", o => o with { Lightbar = color });
                break;
            }

            case "brightness":
            {
                var level = (LedBrightness)Int(m, "level", 0, 2);
                _core.SetOverride("live.brightness", o => o with { Brightness = level });
                break;
            }

            case "playerLeds":
            {
                byte mask = (byte)Int(m, "mask", 0, 31);
                _core.SetOverride("live.playerLeds", o => o with { PlayerLeds = mask });
                break;
            }

            case "micLed":
            {
                var mode = (MicLed)Int(m, "mode", 0, 2);
                _core.SetOverride("live.micLed", o => o with { MicLed = mode });
                break;
            }

            case "rumble":
            {
                byte l = (byte)Int(m, "left", 0, 255);
                byte r = (byte)Int(m, "right", 0, 255);
                _core.SetOverride("live.rumble", o => o with { RumbleLeft = l, RumbleRight = r });
                break;
            }

            case "trigger":
            {
                bool left = Side(m);
                Str(m, "mode");
                // The message carries mode/params/zones at the top level, the same names as TriggerConfig.
                TriggerEffect effect = Triggers.ToEffect(m.Deserialize<TriggerConfig>(TactileJson.Options) ?? TriggerConfig.Off);
                _core.SetOverride(left ? "live.trigger.left" : "live.trigger.right",
                    o => left ? o with { LeftTrigger = effect } : o with { RightTrigger = effect });
                break;
            }

            case "haptic":
            {
                string? error = _core.Controllers.PlayHapticTone(
                    Float(m, "frequency"),
                    Math.Clamp(Float(m, "amplitude"), 0f, 1f),
                    Bool(m, "left"),
                    Bool(m, "right"),
                    Int(m, "ms", 1, 10_000));
                if (error is not null)
                {
                    PostError(error);
                }

                break;
            }

            case "reset":
                _core.Controllers.StopHaptics();
                _core.ClearOverrides();
                _core.ReleaseIntegration();
                break;

            case null:
                throw new BadMessage(new LocalizedMessage("The message type is missing.", "Mesaj türü eksik."));

            default:
                throw new BadMessage(new LocalizedMessage($"Unknown message: {type}", $"Bilinmeyen mesaj: {type}"));
        }
    }

    private static readonly LocalizedMessage Unavailable = new("Could not start a virtual controller on this system.", "Sanal kontrolcü bu sistemde başlatılamadı.");

    private readonly HashSet<DriverKind> _installing = [];

    private void Install(DriverKind kind)
    {
        EmulationService emulation = _core.Emulation ?? throw new BadMessage(Unavailable);
        if (!_installing.Add(kind))
        {
            throw new BadMessage(new LocalizedMessage("An installation is already in progress.", "Kurulum zaten sürüyor."));
        }

        _ = InstallAsync(emulation, kind);
    }

    /// <summary>Never throws: every outcome becomes a <c>driver.progress</c> message.</summary>
    private async Task InstallAsync(EmulationService emulation, DriverKind kind)
    {
        string name = kind == DriverKind.UsbIp ? "usbIp" : "hidHide";
        // Created on the UI thread, so reports come back here.
        var progress = new Progress<string>(message => Post(new { type = "driver.progress", kind = name, message, done = false, ok = false }));
        try
        {
            Log.Info($"Driver install started: {name}.");
            InstallResult result = await emulation.InstallAsync(kind, progress, CancellationToken.None);
            if (result.Ok)
            {
                Log.Info($"Driver install succeeded: {name}; reboot required: {result.RebootRequired}.");
            }
            else
            {
                Log.Warn($"Driver install failed: {name}. {result.EnglishMessage}");
            }

            string message = result.Ok && result.RebootRequired
                ? Text.Pick($"{result.Message} Restart your PC to finish driver setup.", $"{result.Message} Kurulumu bitirmek için bilgisayarı yeniden başlatın.")
                : result.Message;
            Post(new { type = "driver.progress", kind = name, message, done = true, ok = result.Ok });
        }
        catch (Exception e)
        {
            Log.Error($"Driver install failed: {name}.", e);
            Post(new { type = "driver.progress", kind = name, message = Text.Pick($"Installation failed: {e.Message}", $"Kurulum başarısız: {e.Message}"), done = true, ok = false });
        }
        finally
        {
            _installing.Remove(kind);
            emulation.RecheckDrivers();
        }
    }

    /// <summary>Also sent every second while the window is on screen.</summary>
    public void PostEmulation()
    {
        EmulationStatus s = _core.Emulation?.Status ?? EmulationStatus.Unavailable with { Error = Unavailable.Value };
        Post(new
        {
            type = "emulation",
            available = s.Available,
            enabled = _core.Settings.Current.Emulation.Enabled,
            mode = s.Mode,
            attached = s.Attached,
            hidden = s.Hidden,
            audio = s.Audio,
            drivers = s.Drivers.Select(d => new { kind = d.Kind, state = d.State, version = d.Version, detail = d.Detail }),
            latency = new { p50Ms = s.Latency.P50Ms, p95Ms = s.Latency.P95Ms, samples = s.Latency.Samples },
            error = s.Error,
            slots = s.Slots.Select(slot => new { index = slot.Index, mode = slot.Mode, attached = slot.Attached, hidden = slot.Hidden, error = slot.Error }),
        });
    }

    public void PostIntegration()
    {
        IntegrationStatus status = _core.Integration;
        Post(new { type = "integration", status.Udp, status.Forza });
    }

    private void PickRun()
    {
        using var dialog = new OpenFileDialog
        {
            Title = Text.Pick("Choose a program", "Çalıştırılacak program"),
            Filter = Text.Pick("Programs (*.exe)|*.exe", "Programlar (*.exe)|*.exe"),
            CheckFileExists = true,
            DereferenceLinks = true,
            RestoreDirectory = true,
        };
        string? path = dialog.ShowDialog(_owner) == DialogResult.OK ? dialog.FileName : null;
        Post(new { type = "runPicked", path });
    }

    private void SaveSettings(AppSettings s)
    {
        bool before = _core.Settings.Current.StartWithWindows;
        _core.Settings.Save(s);
        bool after = _core.Settings.Current.StartWithWindows;
        if (_paths.Portable || after == before)
        {
            return;
        }

        try
        {
            Autostart.Apply(after);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warn("Could not update the Windows startup entry.", e);
            _core.Settings.Save(_core.Settings.Current with { StartWithWindows = before });
            throw new BadMessage(new LocalizedMessage("Could not change the Windows startup setting.", "Windows ile başlatma ayarlanamadı."));
        }
    }

    private void ExportLogs()
    {
        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
            using var dialog = new SaveFileDialog
            {
                Title = Text.Pick("Export logs", "Logları dışa aktar"),
                FileName = $"TriggerForge-logs-{stamp}.zip",
                Filter = Text.Pick("ZIP archive (*.zip)|*.zip", "ZIP arşivi (*.zip)|*.zip"),
                DefaultExt = "zip",
                RestoreDirectory = true,
            };
            if (dialog.ShowDialog(_owner) != DialogResult.OK)
            {
                Post(new { type = "logs.exported", ok = false });
                return;
            }

            List<string> system =
            [
                $"TriggerForge: {Application.ProductVersion}",
                $"Windows: {Environment.OSVersion.VersionString}",
                $"Portable: {(_paths.Portable ? "yes" : "no")}",
            ];
            IReadOnlyList<DriverStatus> drivers = _core.Emulation?.Status.Drivers ?? [];
            foreach (DriverStatus driver in drivers)
            {
                system.Add($"{driver.Kind}: {driver.State} {driver.Version} {driver.EnglishDetail}".TrimEnd());
            }

            if (drivers.Count == 0)
            {
                system.Add("Driver states unavailable.");
            }

            Log.Export(dialog.FileName, _paths.DataDirectory, _core.Settings.Current, string.Join(Environment.NewLine, system));
            Post(new { type = "logs.exported", ok = true, path = dialog.FileName });
        }
        catch (Exception e)
        {
            Log.Error("Could not export logs.", e);
            Post(new { type = "logs.exported", ok = false, error = Text.Pick("Could not export logs.", "Loglar dışa aktarılamadı.") });
        }
    }

    private void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(_paths.LogDirectory);
            using Process? explorer = Process.Start(new ProcessStartInfo(_paths.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Log.Warn("Could not open the log folder.", e);
            PostError(Text.Pick("Could not open the log folder.", "Log klasörü açılamadı."));
        }
    }

    private static void StartInstaller(string path)
    {
        // No "runas": setup elevates itself and keeps the signed-in user's token, which it needs to
        // relaunch the app unelevated. Started elevated, it would relaunch the app as administrator.
        using Process? installer = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            // An elevated app has no unelevated token to hand down, so setup would relaunch it as
            // administrator; then the user starts it again from Start instead.
            Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS" + (WindowsInputPlatform.IsElevated ? "" : " /RELAUNCH"),
        });
        if (installer is null)
        {
            throw new IOException("The shell did not start the installer.");
        }
    }

    private void OpenUpdatePage()
    {
        try
        {
            using Process? browser = Process.Start(new ProcessStartInfo(UpdateKey.ReleasesPage) { UseShellExecute = true });
        }
        catch (Win32Exception e)
        {
            Log.Warn($"Could not open the releases page (network, Windows error {e.NativeErrorCode}).");
            PostError(Text.Pick("Could not open the releases page.", "Sürümler sayfası açılamadı."));
        }
    }

    public void PostUpdateStatus(bool force = false)
    {
        UpdateStatus status = _updates.Status;
        if (!_ready || (!force && status == _lastUpdate))
        {
            return;
        }

        _lastUpdate = status;
        Post(new
        {
            type = "update.status",
            status.State,
            status.Current,
            status.Latest,
            status.Notes,
            status.Released,
            status.Progress,
            status.Portable,
            status.Error,
            status.Message,
            status.LastCheck,
        });
    }

    public void PostStatus()
    {
        DualSenseController? c = _core.Controllers.Controller;
        MappingStatus mapping = _core.Mapping;
        Post(new
        {
            type = "status",
            connected = c is not null,
            model = c?.Details.Model.ToString(),
            link = c?.Details.Link.ToString(),
            mac = c?.Details.MacAddress,
            firmware = c?.Details.Firmware?.Display,
            version = _updates.Status.Current,
            activeProfileId = _core.ActiveProfile.Id,
            activeReason = _core.ActiveReason,
            controllers = _core.Controllers.Slots.Select(slot =>
            {
                Battery? battery = slot.Controller.Latest?.Battery;
                Profile profile = _core.ProfileForController(slot);
                return new
                {
                    slot.Index,
                    slot.Mac,
                    model = slot.Controller.Details.Model.ToString(),
                    link = slot.Controller.Details.Link.ToString(),
                    battery = battery?.Percent,
                    charging = battery is { Charge: ChargeState.Charging or ChargeState.Full },
                    primary = slot.Index == 0,
                    profileId = profile.Id,
                    profileName = profile.Name,
                };
            }).ToArray(),
            mapping = new { active = mapping.Active, paused = mapping.Paused, pauseReason = mapping.PauseReason, blockedBy = mapping.BlockedBy },
            elevated = WindowsInputPlatform.IsElevated,
            portable = _paths.Portable,
        });
    }

    public void PostInput(ControllerSlot slot, InputState s)
    {
        if (!_ready || !_core.Controllers.IsCurrent(slot))
        {
            return;
        }

        DualSenseController c = slot.Controller;
        Vector3 gyro = GyroCalibrationResult.Apply(c.Details.Calibration.GyroDegPerSec(s.GyroRaw), _core.GyroBias);
        Vector3 accel = c.Details.Calibration.AccelG(s.AccelRaw);
        (float lx, float ly) = _core.LeftStick.Process(s.Left);
        (float rx, float ry) = _core.RightStick.Process(s.Right);
        ControllerStats stats = c.Stats;
        if (!_core.Controllers.IsCurrent(slot))
        {
            return;
        }

        Post(new
        {
            type = "input",
            mac = slot.Mac,
            buttons = Enum.GetValues<Buttons>().Where(b => b != Buttons.None && s.IsDown(b)).Select(b => b.ToString()),
            left = new[] { (int)s.Left.X, s.Left.Y },
            right = new[] { (int)s.Right.X, s.Right.Y },
            leftProcessed = new[] { lx, ly },
            rightProcessed = new[] { rx, ry },
            l2 = s.L2,
            r2 = s.R2,
            gyro = new[] { gyro.X, gyro.Y, gyro.Z },
            accel = new[] { accel.X, accel.Y, accel.Z },
            touch = new[] { s.Touch1, s.Touch2 }.Where(p => p.Active).Select(p => new { id = p.Id, x = p.X, y = p.Y }),
            battery = s.Battery.Percent,
            charging = s.Battery.Charge != ChargeState.Discharging,
            headphones = s.HeadphoneConnected,
            reports = stats.Reports,
            badCrc = stats.BadCrc,
        });
    }

    /// <summary>Lightbar effects animate, so this is also called from the 30 Hz timer.</summary>
    public void PostOutputIfChanged()
    {
        if (!_ready)
        {
            return;
        }

        OutputState o = _core.Controllers.Output;
        if (o.Equals(_lastOutput))
        {
            return;
        }

        _lastOutput = o;
        Post(new
        {
            type = "output",
            lightbar = o.Lightbar.ToHex(),
            brightness = (int)o.Brightness,
            playerLeds = (int)o.PlayerLeds,
            micLed = (int)o.MicLed,
            rumbleLeft = (int)o.RumbleLeft,
            rumbleRight = (int)o.RumbleRight,
            leftTrigger = o.LeftTrigger.ToString(),
            rightTrigger = o.RightTrigger.ToString(),
        });
    }

    public void PostHapticsIfChanged()
    {
        HapticStatus status = _core.Haptics;
        if (_ready && status != _lastHaptics)
        {
            _lastHaptics = status;
            Post(new { type = "haptics", status.Streaming, status.Source, status.Problem });
        }
    }

    public void PostProfiles() =>
        Post(new { type = "profiles", list = _core.Profiles.All, activeId = _core.ActiveProfile.Id });

    private void PostBackups() => Post(new { type = "backups", list = _core.Profiles.ListBackups() });

    public void PostSettings() => Post(new { type = "settings", settings = _core.Settings.Current });

    public void PostNotice(Notice n)
    {
        string json;
        if (n.Controller is int controller)
        {
            string message = Text.Pick($"{n.Message} (Controller {controller})", $"{n.Message} (Kontrolcü {controller})");
            json = Serialize(new { type = "notice", kind = n.Kind, message, controller });
        }
        else
        {
            json = n.Progress is double p
                ? Serialize(new { type = "notice", kind = n.Kind, message = n.Message, progress = p })
                : Serialize(new { type = "notice", kind = n.Kind, message = n.Message });
        }
        if (_ready)
        {
            _postRaw(json);
        }
        else if (_queued.Count < MaxQueued)
        {
            _queued.Add(json);
        }
    }

    private void PostError(string message) => Post(new { type = "error", message });

    private void Post(object message)
    {
        if (_ready)
        {
            _postRaw(Serialize(message));
        }
    }

    private static string Serialize(object message) => JsonSerializer.Serialize(message, TactileJson.Compact);

    private static string Describe(Exception e) => e switch
    {
        LocalizedException => e.Message,
        _ when e.Data[typeof(LocalizedMessage)] is LocalizedMessage localized => localized.Value,
        JsonException j => string.IsNullOrEmpty(j.Path) ? Text.Pick("Could not read the message.", "Mesaj okunamadı.") : Text.Pick($"Invalid field: {j.Path}", $"Geçersiz alan: {j.Path}"),
        // Core validation supplies its own user-facing message.
        ArgumentException or KeyNotFoundException or InvalidOperationException
            when e.TargetSite?.DeclaringType?.Assembly == typeof(AppCore).Assembly => e.Message,
        ArgumentOutOfRangeException a => Text.Pick($"Value out of range: {a.ParamName}", $"Değer aralık dışında: {a.ParamName}"),
        ArgumentException => Text.Pick("Invalid value.", "Geçersiz değer."),
        IOException or UnauthorizedAccessException => Text.Pick("Could not write the file.", "Dosya yazılamadı."),
        _ => Text.Pick($"Unexpected error ({e.GetType().Name}).", $"Beklenmeyen hata ({e.GetType().Name})."),
    };

    private static T Read<T>(JsonElement m, string name)
        where T : class
    {
        if (!m.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Object)
        {
            throw new BadMessage(new LocalizedMessage($"Missing field: {name}", $"Eksik alan: {name}"));
        }

        return v.Deserialize<T>(TactileJson.Options) ?? throw new BadMessage(new LocalizedMessage($"Missing field: {name}", $"Eksik alan: {name}"));
    }

    private static string Str(JsonElement m, string name) =>
        OptStr(m, name) ?? throw new BadMessage(new LocalizedMessage($"Missing field: {name}", $"Eksik alan: {name}"));

    private static string? OptStr(JsonElement m, string name) =>
        m.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement m, string name, int min, int max)
    {
        if (!m.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double d))
        {
            throw new BadMessage(new LocalizedMessage($"Missing field: {name}", $"Eksik alan: {name}"));
        }

        if (double.IsNaN(d) || d < min || d > max)
        {
            throw new BadMessage(new LocalizedMessage($"{name} must be between {min} and {max}.", $"{name} {min}-{max} arası olmalı."));
        }

        return (int)Math.Round(d);
    }

    private static float Float(JsonElement m, string name) =>
        m.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out float f) && float.IsFinite(f)
            ? f
            : throw new BadMessage(new LocalizedMessage($"Missing field: {name}", $"Eksik alan: {name}"));

    private static bool Bool(JsonElement m, string name) =>
        m.TryGetProperty(name, out JsonElement v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : throw new BadMessage(new LocalizedMessage($"Missing field: {name}", $"Eksik alan: {name}"));

    private static bool Side(JsonElement m) => Str(m, "side") switch
    {
        "left" => true,
        "right" => false,
        _ => throw new BadMessage(new LocalizedMessage("side must be left or right.", "side left ya da right olmalı.")),
    };

    private static Rgb ParseColor(string hex)
    {
        try
        {
            return Rgb.FromHex(hex);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or IndexOutOfRangeException)
        {
            throw new BadMessage(new LocalizedMessage("Invalid color.", "Geçersiz renk."));
        }
    }

    /// <summary>A message the UI got wrong; the text goes back as is.</summary>
    private sealed class BadMessage(LocalizedMessage translation) : LocalizedException(translation, null);
}
