using Tactile.Drivers;

namespace Tactile.Tests.Drivers;

public class ReviewFixTests
{
    private const string Pad = @"HID\VID_054C&PID_0CE6\7&1&0&0000";

    [Fact]
    public void Unhide_throws_when_hidhide_is_busy_and_keeps_the_journal()
    {
        using var rig = new DriverRig();
        Assert.True(rig.Hider.Hide(Pad));
        rig.HidHide.Available = false;

        Assert.Throws<DriverException>(() => rig.Hider.Unhide(Pad));

        Assert.Single(rig.Read().HidHide!.Devices);
        Assert.Contains(Pad, rig.HidHide.Blacklist);
    }

    [Fact]
    public void Detach_stops_reconnect_attempts_even_when_plug_out_fails()
    {
        using var rig = new DriverRig();
        int hub = rig.Vhci.Attach(50123, "1-1");
        rig.VhciControl.FailPlugOut.Add(hub);
        rig.Log.Clear();

        Assert.Throws<DriverException>(() => rig.Vhci.Detach(hub));

        Assert.Equal([$"plugout {hub}", "stop 127.0.0.1:50123/1-1"], rig.Log);
        Assert.Single(rig.Read().Attaches);
    }
}
