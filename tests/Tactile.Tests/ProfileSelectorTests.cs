using Tactile.Core;

namespace Tactile.Tests;

public class ProfileSelectorTests
{
    private static readonly Profile A = new() { Id = new string('a', 32), Name = "A" };
    private static readonly Profile Game = new() { Id = new string('b', 32), Name = "Game", Games = ["Game.exe"] };
    private static readonly Profile Pad = new() { Id = new string('c', 32), Name = "Pad" };
    private static readonly Profile Fallback = new() { Id = new string('d', 32), Name = "Fallback" };
    private static readonly Profile[] All = [A, Game, Pad, Fallback];

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Priority_order()
    {
        var settings = new AppSettings
        {
            DefaultProfileId = Fallback.Id,
            ControllerDefaults = new Dictionary<string, string> { ["AA:BB:CC:DD:EE:FF"] = Pad.Id },
        };
        var s = new ProfileSelector();

        Assert.Equal((Game, "game:game.exe"), Pick(s.Select("game.exe", All, settings, "aa:bb:cc:dd:ee:ff", Ms(0))));

        s.Pin(A.Id);
        Assert.Equal((A, "manual"), Pick(s.Select("game.exe", All, settings, null, Ms(10))));

        var fresh = new ProfileSelector();
        Assert.Equal((Pad, "controllerDefault"), Pick(fresh.Select("other.exe", All, settings, "AA:BB:CC:DD:EE:FF", Ms(0))));
        Assert.Equal((Fallback, "default"), Pick(fresh.Select("other.exe", All, settings, null, Ms(0))));
        Assert.Equal((A, "default"), Pick(fresh.Select("other.exe", All, new AppSettings(), null, Ms(0))));
    }

    [Fact]
    public void Auto_profile_off_skips_game_match()
    {
        var s = new ProfileSelector();
        Assert.Equal(A, s.Select("game.exe", All, new AppSettings { AutoProfile = false }, null, Ms(0)).Profile);
    }

    [Fact]
    public void Foreground_must_stay_for_the_delay()
    {
        var settings = new AppSettings { AutoProfileDelayMs = 800 };
        var s = new ProfileSelector();
        Assert.Equal(A, s.Select("explorer.exe", All, settings, null, Ms(0)).Profile);

        Assert.Equal(A, s.Select("game.exe", All, settings, null, Ms(100)).Profile);
        Assert.Equal(A, s.Select("game.exe", All, settings, null, Ms(899)).Profile);
        Assert.Equal(Game, s.Select("game.exe", All, settings, null, Ms(900)).Profile);

        // A short alt-tab does not switch back.
        Assert.Equal(Game, s.Select("explorer.exe", All, settings, null, Ms(1000)).Profile);
        Assert.Equal(Game, s.Select("game.exe", All, settings, null, Ms(1500)).Profile);
        Assert.Equal(Game, s.Select("game.exe", All, settings, null, Ms(5000)).Profile);
    }

    [Fact]
    public void Manual_pin_lasts_until_foreground_changes()
    {
        var settings = new AppSettings { AutoProfileDelayMs = 500 };
        var s = new ProfileSelector();
        s.Select("game.exe", All, settings, null, Ms(0));
        s.Pin(Pad.Id);

        Assert.Equal((Pad, "manual"), Pick(s.Select("game.exe", All, settings, null, Ms(100))));
        Assert.Equal((Pad, "manual"), Pick(s.Select("game.exe", All, settings, null, Ms(60_000))));

        s.Select("notepad.exe", All, settings, null, Ms(61_000));
        Assert.Equal((A, "default"), Pick(s.Select("notepad.exe", All, settings, null, Ms(61_600))));
        Assert.Null(s.PinnedId);
    }

    [Fact]
    public void Pin_to_a_deleted_profile_is_dropped()
    {
        var s = new ProfileSelector();
        s.Pin("ffffffffffffffffffffffffffffffff");
        Assert.Equal("default", s.Select(null, All, new AppSettings(), null, Ms(0)).Reason);
        Assert.Null(s.PinnedId);
    }

    private static (Profile, string) Pick(ProfileSelection s) => (s.Profile, s.Reason);
}
