using Tactile.Device.DualSense;

/// <summary>
/// Hardware checks from the command line. Every probe holds its effect for a few seconds,
/// then returns the controller to the default state.
///   probe info
///   probe light #RRGGBB [players-mask]
///   probe rumble &lt;left 0-255&gt; &lt;right 0-255&gt;
///   probe trigger &lt;left|right|both&gt; &lt;feedback|weapon|vibration|bow|galloping|machine|slope&gt;
///   probe haptic &lt;hz&gt; &lt;amplitude 0-1&gt; &lt;left|right|both&gt;
/// </summary>
internal static class Probe
{
    public static int Run(ControllerInfo info, string[] a)
    {
        using DualSenseController c = DualSenseController.Open(info);
        Thread.Sleep(150);
        Console.WriteLine($"{c.Details.Model} over {c.Details.Link}, fw {c.Details.Firmware?.Display}, update {c.Details.Firmware?.UpdateVersion:X4}, mac {(c.Details.MacAddress is null ? "?" : "read")}");

        int hold = 4000;
        switch (a[0])
        {
            case "info":
                InputState? s = c.Latest;
                Console.WriteLine(s is null ? "no input yet" : $"battery {s.Battery.Percent}% {s.Battery.Charge}, reports {c.Stats.Reports}, bad crc {c.Stats.BadCrc}");
                return 0;
            case "light":
                byte players = a.Length > 2 ? Convert.ToByte(a[2], 2) : (byte)0;
                Send(c, OutputState.Default with { Lightbar = Rgb.FromHex(a[1]), PlayerLeds = players, MicLed = MicLed.On });
                break;
            case "rumble":
                Send(c, OutputState.Default with { RumbleLeft = byte.Parse(a[1]), RumbleRight = byte.Parse(a[2]) });
                hold = 1500;
                break;
            case "trigger":
                TriggerEffect e = a[2] switch
                {
                    "feedback" => TriggerEffect.Feedback(2, 8),
                    "slope" => TriggerEffect.Slope(0, 9, 1, 8),
                    "weapon" => TriggerEffect.Weapon(3, 6, 8),
                    "vibration" => TriggerEffect.Vibration(0, 8, 30),
                    "bow" => TriggerEffect.Bow(1, 6, 6, 8),
                    "galloping" => TriggerEffect.Galloping(0, 9, 1, 4, 3),
                    "machine" => TriggerEffect.Machine(0, 9, 2, 7, 20, 8),
                    _ => throw new ArgumentException($"unknown effect {a[2]}"),
                };
                bool left = a[1] is "left" or "both", right = a[1] is "right" or "both";
                Send(c, OutputState.Default with
                {
                    LeftTrigger = left ? e : TriggerEffect.Off,
                    RightTrigger = right ? e : TriggerEffect.Off,
                });
                hold = 8000;
                break;
            case "haptic":
                bool hl = a[3] is "left" or "both", hr = a[3] is "right" or "both";
                int blocks = (int)(1500 / HapticReport.Interval.TotalMilliseconds);
                using (var h = new HapticStreamer(c, HapticStreamer.Tone(float.Parse(a[1]), float.Parse(a[2]), hl, hr, blocks)))
                {
                    Thread.Sleep(1800);
                    Console.WriteLine($"sent {h.Sent} haptic reports, write errors {c.Stats.WriteErrors}");
                }

                return 0;
            default:
                Console.Error.WriteLine("unknown probe");
                return 2;
        }

        Thread.Sleep(hold);
        Console.WriteLine($"writes {c.Stats.Writes}, write errors {c.Stats.WriteErrors}, reports {c.Stats.Reports}, bad crc {c.Stats.BadCrc}");
        return 0;
    }

    private static void Send(DualSenseController c, OutputState s)
    {
        if (!c.Send(s, force: true))
        {
            Console.Error.WriteLine("write failed");
        }
    }
}
