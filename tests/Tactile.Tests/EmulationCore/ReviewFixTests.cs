using Tactile.Core;
using Tactile.Drivers;

namespace Tactile.Tests.EmulationCore;

public class EmulationReviewFixTests
{
    private static readonly Profile Xbox = EmulationRig.ProfileWith(EmulationMode.Xbox360);

    [Fact]
    public void Hidhide_busy_while_attached_keeps_the_pad_and_the_hide()
    {
        var rig = new EmulationRig();
        using EmulationService e = rig.Create();
        e.Update(Xbox, EmulationRig.Settings(), EmulationRig.Bluetooth);
        rig.Clear();

        rig.Drivers.HidHide = DriverState.Error;
        e.RecheckDrivers();

        Assert.Empty(rig.Log);
        Assert.True(e.Status.Attached);
        Assert.True(e.Status.Hidden);
    }

    [Fact]
    public void Failed_unhide_is_retried_and_noticed_once()
    {
        var rig = new EmulationRig();
        using EmulationService e = rig.Create();
        e.Update(Xbox, EmulationRig.Settings(), EmulationRig.Bluetooth);
        rig.Hider.FailUnhide = true;

        e.Update(Xbox, EmulationRig.Settings(), null);
        e.Retry();
        int notices = rig.Notices.Count(n => n.Message.Contains("görünür yapılamadı", StringComparison.Ordinal));

        rig.Hider.FailUnhide = false;
        rig.Clear();
        e.Retry();

        Assert.Equal(1, notices);
        Assert.Equal([$"unhide {EmulationRig.RealId}"], rig.Log);
    }
}
