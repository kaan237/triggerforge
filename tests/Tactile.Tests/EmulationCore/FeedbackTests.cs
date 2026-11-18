using Tactile.Core;
using Tactile.Device.DualSense;
using Tactile.Emulation;

namespace Tactile.Tests.EmulationCore;

public class FeedbackTests
{
    private static readonly OutputState ProfileOutput = OutputState.Default with
    {
        Lightbar = new Rgb(0x30, 0x60, 0xFF),
        PlayerLeds = 0b00100,
        RightTrigger = TriggerEffect.Feedback(3, 4),
    };

    private static (EmulationRig, EmulationService, FakePad) Attached(EmulationMode mode, double rumbleStrength = 1)
    {
        var rig = new EmulationRig();
        EmulationService e = rig.Create();
        Profile p = EmulationRig.ProfileWith(mode) with { Rumble = new RumbleConfig { Strength = rumbleStrength } };
        e.Update(p, EmulationRig.Settings(), EmulationRig.Bluetooth);
        return (rig, e, rig.Server.Last!);
    }

    private static byte[] LightbarBlock(byte r, byte g, byte b)
    {
        byte[] block = new byte[OutputReport.StateLength];
        block[1] = 0x04;
        block[44] = r;
        block[45] = g;
        block[46] = b;
        return block;
    }

    [Fact]
    public void Xbox_rumble_becomes_the_emulation_layer()
    {
        (EmulationRig rig, EmulationService e, FakePad pad) = Attached(EmulationMode.Xbox360);
        using (e)
        {
            pad.Raise(new RumbleFeedback(0, Large: 200, Small: 50));
            OutputState o = rig.Apply(ProfileOutput);
            Assert.Equal(ProfileOutput with { RumbleLeft = 200, RumbleRight = 50 }, o);

            // The ring LED is not ours to show.
            pad.Raise(new LedFeedback(0, 3));
            Assert.Single(rig.Layers);
        }
    }

    [Fact]
    public void Rumble_follows_the_profile_strength()
    {
        (EmulationRig rig, EmulationService e, FakePad pad) = Attached(EmulationMode.Xbox360, rumbleStrength: 0.5);
        using (e)
        {
            pad.Raise(new RumbleFeedback(0, 200, 255));
            OutputState o = rig.Apply(ProfileOutput);
            Assert.Equal(100, o.RumbleLeft);
            Assert.Equal(128, o.RumbleRight);
        }
    }

    [Fact]
    public void Dualsense_output_overrides_only_what_the_game_set_and_accumulates()
    {
        (EmulationRig rig, EmulationService e, FakePad pad) = Attached(EmulationMode.DualSense);
        using (e)
        {
            pad.Raise(new DualSenseOutputFeedback(0, LightbarBlock(1, 2, 3)));
            Assert.Equal(ProfileOutput with { Lightbar = new Rgb(1, 2, 3) }, rig.Apply(ProfileOutput));

            // A later report without the lightbar flag keeps the game's colour and adds the trigger.
            byte[] trigger = new byte[OutputReport.StateLength];
            trigger[0] = 0x04;
            TriggerEffect.Weapon(2, 5, 7).Bytes.CopyTo(trigger.AsSpan(10));
            pad.Raise(new DualSenseOutputFeedback(0, trigger));

            Assert.Equal(ProfileOutput with { Lightbar = new Rgb(1, 2, 3), RightTrigger = TriggerEffect.Weapon(2, 5, 7) }, rig.Apply(ProfileOutput));
            Assert.Equal([EmulationService.GameLayer], rig.Layers.Keys);
        }
    }

    [Theory]
    [InlineData(48)]
    [InlineData(63)]
    public void Full_output_reports_are_accepted(int length)
    {
        (EmulationRig rig, EmulationService e, FakePad pad) = Attached(EmulationMode.DualSense);
        using (e)
        {
            byte[] report = new byte[length];
            report[0] = 0x02;
            LightbarBlock(9, 8, 7).CopyTo(report, 1);
            pad.Raise(new DualSenseOutputFeedback(0, report));
            Assert.Equal(new Rgb(9, 8, 7), rig.Apply(ProfileOutput).Lightbar);

            // Wrong sizes are dropped.
            pad.Raise(new DualSenseOutputFeedback(0, new byte[20]));
            Assert.Equal(new Rgb(9, 8, 7), rig.Apply(ProfileOutput).Lightbar);
        }
    }

    [Fact]
    public void Game_layers_go_when_the_pad_is_removed_and_late_feedback_is_ignored()
    {
        (EmulationRig rig, EmulationService e, FakePad pad) = Attached(EmulationMode.DualSense);
        using (e)
        {
            pad.Raise(new DualSenseOutputFeedback(0, LightbarBlock(1, 2, 3)));
            e.Update(EmulationRig.ProfileWith(EmulationMode.DualSense), EmulationRig.Settings(), null);
            Assert.Empty(rig.Layers);

            pad.Raise(new DualSenseOutputFeedback(0, LightbarBlock(4, 5, 6)));
            Assert.Empty(rig.Layers);

            // A new pad starts from the profile again.
            e.Update(EmulationRig.ProfileWith(EmulationMode.DualSense), EmulationRig.Settings(), EmulationRig.Bluetooth);
            byte[] rumble = new byte[OutputReport.StateLength];
            rumble[0] = 0x01;
            rumble[3] = 77;
            rig.Server.Last!.Raise(new DualSenseOutputFeedback(0, rumble));
            Assert.Equal(ProfileOutput with { RumbleLeft = 77 }, rig.Apply(ProfileOutput));
        }
    }

    [Fact]
    public void Latency_is_measured_from_arrival_to_the_write()
    {
        (EmulationRig rig, EmulationService e, FakePad pad) = Attached(EmulationMode.Xbox360);
        using (e)
        {
            rig.Now = Ticks.FromMs(1000);
            for (int i = 1; i <= 20; i++)
            {
                pad.Raise(new RumbleFeedback(rig.Now - Ticks.FromMs(i), (byte)i, 0));
            }

            LatencySummary l = e.Status.Latency;
            Assert.Equal(20, l.Samples);
            Assert.Equal(10, l.P50Ms, 3);
            Assert.Equal(19, l.P95Ms, 3);

            // No controller write, no sample.
            rig.Host.Sends = false;
            pad.Raise(new RumbleFeedback(rig.Now, 1, 1));
            Assert.Equal(20, e.Status.Latency.Samples);
        }
    }
}
