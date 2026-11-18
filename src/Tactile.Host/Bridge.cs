using System.Text.Json;
using Tactile.Core;
using Tactile.Device.DualSense;

namespace Tactile.Host;

/// <summary>Translates UI messages into service calls and service state into UI messages.</summary>
internal sealed class Bridge
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ControllerService _service;
    private readonly Action<string> _post;

    public Bridge(ControllerService service, Action<string> post)
    {
        _service = service;
        _post = post;
    }

    public void Handle(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement m = doc.RootElement;
        string? type = m.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;
        try
        {
            switch (type)
            {
                case "hello":
                    PostStatus();
                    PostOutput();
                    break;
                case "lightbar":
                    _service.SetOutput(o => o with { Lightbar = Rgb.FromHex(m.GetProperty("color").GetString()!) });
                    break;
                case "brightness":
                    _service.SetOutput(o => o with { Brightness = (LedBrightness)m.GetProperty("level").GetInt32() });
                    break;
                case "playerLeds":
                    _service.SetOutput(o => o with { PlayerLeds = (byte)m.GetProperty("mask").GetInt32() });
                    break;
                case "micLed":
                    _service.SetOutput(o => o with { MicLed = (MicLed)m.GetProperty("mode").GetInt32() });
                    break;
                case "rumble":
                    _service.SetOutput(o => o with
                    {
                        RumbleLeft = (byte)m.GetProperty("left").GetInt32(),
                        RumbleRight = (byte)m.GetProperty("right").GetInt32(),
                    });
                    break;
                case "trigger":
                    TriggerEffect effect = ParseTrigger(m);
                    bool left = m.GetProperty("side").GetString() == "left";
                    _service.SetOutput(o => left ? o with { LeftTrigger = effect } : o with { RightTrigger = effect });
                    break;
                case "haptic":
                    string? error = _service.PlayHapticTone(
                        m.GetProperty("frequency").GetSingle(),
                        m.GetProperty("amplitude").GetSingle(),
                        m.GetProperty("left").GetBoolean(),
                        m.GetProperty("right").GetBoolean(),
                        m.GetProperty("ms").GetInt32());
                    if (error is not null)
                    {
                        PostError(error);
                    }

                    break;
                case "reset":
                    _service.StopHaptics();
                    _service.SetOutput(_ => OutputState.Default);
                    break;
            }
        }
        catch (Exception e) when (e is ArgumentException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            PostError(e.Message);
        }

        if (type is "lightbar" or "brightness" or "playerLeds" or "micLed" or "rumble" or "trigger" or "reset")
        {
            PostOutput();
        }
    }

    public void PostStatus()
    {
        DualSenseController? c = _service.Controller;
        Post(new
        {
            type = "status",
            connected = c is not null,
            model = c?.Details.Model.ToString(),
            link = c?.Details.Link.ToString(),
            mac = c?.Details.MacAddress,
            firmware = c?.Details.Firmware?.Display,
            version = Application.ProductVersion,
        });
    }

    public void PostInput(InputState s, ControllerStats stats, ImuCalibration cal)
    {
        Vector3 gyro = cal.GyroDegPerSec(s.GyroRaw);
        Vector3 accel = cal.AccelG(s.AccelRaw);
        Post(new
        {
            type = "input",
            buttons = Enum.GetValues<Buttons>().Where(b => b != Buttons.None && s.IsDown(b)).Select(b => b.ToString()),
            left = new[] { (int)s.Left.X, s.Left.Y },
            right = new[] { (int)s.Right.X, s.Right.Y },
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

    private void PostOutput()
    {
        OutputState o = _service.Output;
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

    private void PostError(string message) => Post(new { type = "error", message });

    private void Post(object message) => _post(JsonSerializer.Serialize(message, Json));

    private static TriggerEffect ParseTrigger(JsonElement m)
    {
        JsonElement p = m.TryGetProperty("params", out JsonElement x) ? x : default;
        int I(string name) => p.GetProperty(name).GetInt32();
        return m.GetProperty("mode").GetString() switch
        {
            "off" => TriggerEffect.Off,
            "feedback" => TriggerEffect.Feedback(I("start"), I("strength")),
            "slope" => TriggerEffect.Slope(I("start"), I("end"), I("startStrength"), I("endStrength")),
            "weapon" => TriggerEffect.Weapon(I("start"), I("end"), I("strength")),
            "vibration" => TriggerEffect.Vibration(I("start"), I("amplitude"), I("frequency")),
            "bow" => TriggerEffect.Bow(I("start"), I("end"), I("strength"), I("snap")),
            "galloping" => TriggerEffect.Galloping(I("start"), I("end"), I("firstFoot"), I("secondFoot"), I("frequency")),
            "machine" => TriggerEffect.Machine(I("start"), I("end"), I("amplitudeA"), I("amplitudeB"), I("frequency"), I("period")),
            string other => throw new ArgumentException($"Unknown trigger mode '{other}'."),
            null => throw new ArgumentException("Missing trigger mode."),
        };
    }
}
