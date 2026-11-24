using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Tactile.Core;
using Tactile.Core.Catalog;
using Tactile.Device.DualSense;
using Tactile.Drivers;
using Tactile.Input;
using Tactile.Tests.Drivers;
using Tactile.Tests.EmulationCore;
using Tactile.Tests.Multi;

namespace Tactile.Tests.Mapping;

[Collection("Localization")]
public sealed class RadialMenuTests
{
    private static readonly Key[] SliceKeys = [Key.A, Key.B, Key.C, Key.D, Key.E, Key.F, Key.G, Key.H];
    private static readonly Stick Center = new(128, 128);

    /// <summary>A stick tilted <paramref name="degrees"/> clockwise from 12 o'clock.</summary>
    private static Stick Tilt(double degrees, double radius = 0.8)
    {
        double a = degrees * Math.PI / 180;
        return new Stick((byte)Math.Round(127.5 + (Math.Sin(a) * radius * 127.5)), (byte)Math.Round(127.5 - (Math.Cos(a) * radius * 127.5)));
    }

    private static RadialMenu MenuOf(string id, RadialStick stick, int slices) => new()
    {
        Id = id,
        Name = "Menu",
        Stick = stick,
        Slices = Enumerable.Range(0, slices).Select(i => new RadialSlice { Label = $"S{i}", Action = Bind.Keys(SliceKeys[i]) }).ToArray(),
    };

    private static ActionConfig OpenMenu(string id) =>
        new() { Kind = ActionKind.Special, Special = SpecialAction.RadialMenu, RadialMenuId = id };

    private static MappingConfig Config(string id, RadialStick stick, int slices = 4, params (string Button, Binding Binding)[] extra) =>
        new()
        {
            Enabled = true,
            Buttons = Bind.Map([("l1", new Binding { Press = OpenMenu(id) }), .. extra]),
            RadialMenus = [MenuOf(id, stick, slices)],
        };

    private static StickMappingPair KeySticks() => new()
    {
        Left = new StickMapping { Mode = StickMappingMode.Keys },
        Right = new StickMapping { Mode = StickMappingMode.Keys, Keys = new StickKeys { Up = Key.I, Down = Key.K, Left = Key.J, Right = Key.L } },
    };

    /// <summary>An AppCore on a hand-driven clock with a fake keyboard, as the on-screen keyboard tests use it.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly TempDir _dir = new();

        public Rig(Func<string, MappingConfig> mapping)
        {
            Id = Validation.NewId();
            Core = new AppCore(_dir.Path, Foreground, new ControllerService(autoConnect: false), Time, runTimer: false, Fake.Platform);
            Profile = Core.Profiles.Save(Core.ActiveProfile with { Mapping = mapping(Id) });
            Core.RadialMenuChanged += States.Add;
            Core.OnInput(Inputs.Make());
        }

        public string Id { get; }
        public FakeForeground Foreground { get; } = new();
        public FakePlatform Fake { get; } = new();
        public ManualTime Time { get; } = new();
        public AppCore Core { get; }
        public Profile Profile { get; private set; }
        public List<RadialMenuState> States { get; } = [];

        public Rig Frame(Buttons buttons = Buttons.None, Stick? left = null, Stick? right = null)
        {
            Core.OnInput(Inputs.Make(buttons, left, right));
            return this;
        }

        public Rig Wait(double ms)
        {
            Time.Advance(TimeSpan.FromMilliseconds(ms));
            Core.Refresh();
            return this;
        }

        public string[] Take() => Fake.Injector.Take();

        public void Dispose()
        {
            Core.Dispose();
            _dir.Dispose();
        }
    }

    [Fact]
    public void Selects_correct_slice_for_2_to_8_slices_and_respects_deadzone_hysteresis()
    {
        for (int n = 2; n <= 8; n++)
        {
            double width = 360.0 / n;
            for (int k = 0; k < n; k++)
            {
                // Slice 0 is centred at 12 o'clock and the rest follow clockwise; each owns half a width either side.
                foreach (double offset in new[] { 0, (width / 2) - 1, -((width / 2) - 1) })
                {
                    double a = ((k * width) + offset) * Math.PI / 180;
                    Assert.Equal(k, RadialMenuController.Select((float)(Math.Sin(a) * 0.8), (float)(Math.Cos(a) * 0.8), n, null));
                }
            }
        }

        // 4 slices: right is 1, down is 2, left is 3. 8 slices: the diagonal up-right is 1.
        Assert.Equal(1, RadialMenuController.Select(0.8f, 0, 4, null));
        Assert.Equal(2, RadialMenuController.Select(0, -0.8f, 4, null));
        Assert.Equal(3, RadialMenuController.Select(-0.8f, 0, 4, null));
        Assert.Equal(1, RadialMenuController.Select(0.56f, 0.56f, 8, null));
        Assert.Equal(1, RadialMenuController.Select(0.8f, 0, 2, null));

        // Enter at 0.35, hold down to 0.25, clear below it.
        Assert.Null(RadialMenuController.Select(0, 0.34f, 4, null));
        Assert.Equal(0, RadialMenuController.Select(0, 0.36f, 4, null));
        Assert.Null(RadialMenuController.Select(0, 0.30f, 4, null));
        Assert.Equal(0, RadialMenuController.Select(0, 0.30f, 4, 0));
        Assert.Null(RadialMenuController.Select(0, 0.24f, 4, 0));

        // The same through the controller and real stick bytes.
        string id = Validation.NewId();
        var menu = new RadialMenuController();
        menu.Configure([MenuOf(id, RadialStick.Left, 4)]);
        Assert.True(menu.TryOpen(id) > 0);
        menu.Update(Tilt(90, 0.8), Center);
        Assert.Equal(1, menu.State.SelectedIndex);
        menu.Update(Tilt(90, 0.30), Center);
        Assert.Equal(1, menu.State.SelectedIndex);
        menu.Update(Tilt(90, 0.20), Center);
        Assert.Null(menu.State.SelectedIndex);
        menu.Update(Tilt(90, 0.30), Center);
        Assert.Null(menu.State.SelectedIndex);
        menu.Update(Tilt(90, 0.40), Center);
        Assert.Equal(1, menu.State.SelectedIndex);
        // The other stick never selects.
        menu.Update(Center, Tilt(180));
        Assert.Null(menu.State.SelectedIndex);
    }

    [Fact]
    public void State_marks_unassigned_slices()
    {
        var menu = new RadialMenuController();
        menu.Configure([MenuOf("m", RadialStick.Left, 3) with
        {
            Slices = [new() { Label = "A", Action = Bind.Keys(Key.A) }, new() { Label = "" }, new() { Label = "C", Action = Bind.Keys(Key.C) }],
        }]);
        Assert.NotEqual(0, menu.TryOpen("m"));
        Assert.Equal([true, false, true], menu.State.Assigned);
    }

    [Fact]
    public void Hold_select_and_release_taps_slice_action_and_center_cancels()
    {
        using var rig = new Rig(id => Config(id, RadialStick.Left));
        rig.Frame(Buttons.L1, Tilt(90));
        Assert.True(rig.Core.RadialMenuOpen);
        Assert.Equal(1, rig.Core.RadialMenuSnapshot.SelectedIndex);
        Assert.Equal(["S0", "S1", "S2", "S3"], rig.Core.RadialMenuSnapshot.Labels);
        Assert.Equal([true, true, true, true], rig.Core.RadialMenuSnapshot.Assigned);
        rig.Frame(Buttons.L1, Tilt(90));
        Assert.Empty(rig.Take());

        // Release on the slice: one tap, down now and up 30 ms later.
        rig.Frame(Buttons.None, Tilt(90));
        Assert.False(rig.Core.RadialMenuOpen);
        Assert.Equal(["B down"], rig.Take());
        rig.Wait(29);
        Assert.Empty(rig.Take());
        rig.Wait(1);
        Assert.Equal(["B up"], rig.Take());
        Assert.False(rig.States[^1].Visible);
        Assert.Contains(rig.States, s => s.Visible && s.SelectedIndex == 1);
        Assert.Equal(1, rig.States.Count(s => !s.Visible));

        // Centred release: nothing at all.
        rig.Frame(Buttons.None);
        rig.Frame(Buttons.L1);
        Assert.True(rig.Core.RadialMenuOpen);
        Assert.Null(rig.Core.RadialMenuSnapshot.SelectedIndex);
        rig.Frame(Buttons.None);
        rig.Wait(200);
        Assert.Empty(rig.Take());

        // Tilt onto a slice, come back to the centre, release: still nothing.
        rig.Frame(Buttons.L1, Tilt(180));
        Assert.Equal(2, rig.Core.RadialMenuSnapshot.SelectedIndex);
        rig.Frame(Buttons.L1, Center);
        Assert.Null(rig.Core.RadialMenuSnapshot.SelectedIndex);
        rig.Frame(Buttons.None, Center);
        rig.Wait(200);
        Assert.Empty(rig.Take());
    }

    [Fact]
    public void Unassigned_slice_does_nothing_on_release()
    {
        using var rig = new Rig(id => Config(id, RadialStick.Left) with
        {
            RadialMenus =
            [
                MenuOf(id, RadialStick.Left, 4) with
                {
                    Slices = [new RadialSlice { Label = "x" }, new RadialSlice { Label = "y" }, new RadialSlice { Label = "z" }],
                },
            ],
        });
        rig.Frame(Buttons.L1, Tilt(90));
        Assert.Equal(1, rig.Core.RadialMenuSnapshot.SelectedIndex);
        rig.Frame(Buttons.None, Tilt(90));
        rig.Wait(100);
        Assert.Empty(rig.Take());
    }

    [Theory]
    [InlineData(RadialStick.Left)]
    [InlineData(RadialStick.Right)]
    public async Task Neutralizes_only_configured_stick_across_mapper_emulation_and_dsu(RadialStick side)
    {
        using var rig = new EmulationRig(side);
        using DsuProbe dsu = await DsuProbe.Start(rig.Core, rig.DsuPort);
        Stick far = new(128, 0);
        Stick other = new(255, 128);
        Stick Left(bool configuredNeutral) => side == RadialStick.Left ? (configuredNeutral ? Center : far) : other;
        Stick Right(bool configuredNeutral) => side == RadialStick.Right ? (configuredNeutral ? Center : far) : other;

        async Task Check(Buttons buttons, bool neutral, string step)
        {
            rig.Pads[1].PublishInput(Inputs.Make(buttons, side == RadialStick.Left ? far : other, side == RadialStick.Right ? far : other,
                l2: 200, r2: 90, gyro: new Vector3(500, 500, 500)));
            InputState pad = rig.Pad.Submitted[^1];
            Assert.True(Left(neutral) == pad.Left, $"{step}: left stick on the virtual pad");
            Assert.True(Right(neutral) == pad.Right, $"{step}: right stick on the virtual pad");
            Assert.Equal(200, pad.L2);
            Assert.Equal(90, pad.R2);
            Assert.True(pad.IsDown(Buttons.Cross), $"{step}: other buttons pass through");
            byte[] payload = await dsu.Next();
            Assert.Equal((byte)(Left(neutral).X), payload[20]);
            Assert.Equal((byte)(255 - Left(neutral).Y), payload[21]);
            Assert.Equal((byte)(Right(neutral).X), payload[22]);
            Assert.Equal((byte)(255 - Right(neutral).Y), payload[23]);
        }

        rig.Pads[1].PublishInput(Inputs.Make());
        await dsu.Next();
        rig.Fake.Injector.Take();

        // The opening frame already reads neutral everywhere; the other stick drives its keys.
        await Check(Buttons.L1 | Buttons.Cross, neutral: true, "opening frame");
        Assert.True(rig.Core.RadialMenuOpen);
        string[] events = rig.Fake.Injector.Take();
        string menuStickKey = side == RadialStick.Left ? "W down" : "I down";
        string otherStickKey = side == RadialStick.Left ? "L down" : "D down";
        Assert.DoesNotContain(menuStickKey, events);
        Assert.Contains(otherStickKey, events);
        await Check(Buttons.L1 | Buttons.Cross, neutral: true, "open");

        // Released with the stick still tilted: the closing frame and the ones after stay neutral until it is centred.
        await Check(Buttons.Cross, neutral: true, "closing frame");
        Assert.False(rig.Core.RadialMenuOpen);
        await Check(Buttons.Cross, neutral: true, "after closing");
        Assert.DoesNotContain(menuStickKey, rig.Fake.Injector.Take());

        rig.Pads[1].PublishInput(Inputs.Make(Buttons.Cross, Left(true), Right(true)));
        await dsu.Next();
        await Check(Buttons.Cross, neutral: false, "tilted again");
        Assert.Contains(menuStickKey, rig.Fake.Injector.Take());
    }

    [Fact]
    public async Task A_menu_opened_by_the_chord_window_in_tick_selects_the_slice_and_sends_a_neutral_stick_without_a_new_report()
    {
        using var rig = new EmulationRig(RadialStick.Left, chord: true);
        using DsuProbe dsu = await DsuProbe.Start(rig.Core, rig.DsuPort);
        rig.Pads[1].PublishInput(Inputs.Make());
        await dsu.Next();

        rig.Pads[1].PublishInput(Inputs.Make(Buttons.L1, Tilt(90), Center));
        Assert.False(rig.Core.RadialMenuOpen);
        rig.Time.Advance(TimeSpan.FromMilliseconds(60));
        rig.Core.Refresh();

        Assert.True(rig.Core.RadialMenuOpen);
        Assert.Equal(1, rig.Core.RadialMenuSnapshot.SelectedIndex);
        Assert.Equal(Center, rig.Pad.Submitted[^1].Left);
        byte[] payload = await dsu.Next();
        while (true)
        {
            // Drain anything queued before the open; the last packet is the neutral one.
            if (payload[20] == 128 && payload[21] == 127)
            {
                break;
            }

            payload = await dsu.Next();
        }

        Assert.Equal((byte)128, payload[20]);
        Assert.Equal((byte)127, payload[21]);
    }

    [Fact]
    public async Task Dsu_gets_the_neutral_stick_on_the_opening_frame()
    {
        using var rig = new EmulationRig(RadialStick.Left);
        using DsuProbe dsu = await DsuProbe.Start(rig.Core, rig.DsuPort);
        rig.Pads[1].PublishInput(Inputs.Make());
        await dsu.Next();

        rig.Pads[1].PublishInput(Inputs.Make(Buttons.L1, left: new Stick(0, 0), right: new Stick(255, 255)));
        byte[] payload = await dsu.Next();
        Assert.True(rig.Core.RadialMenuOpen);
        Assert.Equal((byte)128, payload[20]);
        Assert.Equal((byte)127, payload[21]);
        Assert.Equal((byte)255, payload[22]);
        Assert.Equal((byte)0, payload[23]);
    }

    [Fact]
    public void Mapper_neutralizes_the_menu_stick_on_the_opening_frame_and_releases_its_keys()
    {
        var menuId = Validation.NewId();
        using var rig = new MapperRig(Config(menuId, RadialStick.Left) with { Sticks = KeySticks() });
        rig.Sticks(left: new Stick(128, 0));
        Assert.Equal(["W down"], rig.Take());

        // The stick is held up and the menu opens on this very frame: its key is let go and the stick reads centre.
        rig.Press(Buttons.L1);
        Assert.Equal(["W up"], rig.Take());
        Assert.True(rig.Mapper.Radial.IsOpen);
        rig.Sticks(left: new Stick(0, 128));
        Assert.Empty(rig.Take());

        // The other stick still maps.
        rig.Sticks(right: new Stick(255, 128));
        Assert.Equal(["L down"], rig.Take());
    }

    [Fact]
    public void Pause_profile_switch_disconnect_and_osk_cancel_without_firing()
    {
        (string Name, Action<Rig> Cancel, bool CanReopen)[] paths =
        [
            ("user pause", r => r.Core.ToggleUserPause(), false),
            ("ui pause", r => r.Core.UiMappingPaused = true, false),
            ("profile switch", r => r.Core.ActivateProfile(r.Core.Profiles.Save(new Profile
            {
                Name = "Other",
                Mapping = Bind.Buttons(("l1", Bind.Key(Key.Z))) with { Enabled = true },
            }).Id), false),
            ("disconnect", r => r.Core.OnConnection(null), false),
            ("osk", r => r.Core.ToggleOnScreenKeyboard(), false),
            ("shift layer", r => r.Frame(Buttons.L1 | Buttons.PS, Tilt(90)), false),
            ("api", r => r.Core.CancelRadialMenu(), true),
        ];

        foreach ((string name, Action<Rig> cancel, bool canReopen) in paths)
        {
            using var rig = new Rig(id => Config(id, RadialStick.Left));
            rig.Frame(Buttons.L1, Tilt(90));
            Assert.True(rig.Core.RadialMenuOpen, name);
            Assert.Equal(1, rig.Core.RadialMenuSnapshot.SelectedIndex);
            rig.Take();

            cancel(rig);

            Assert.False(rig.Core.RadialMenuOpen, name);
            Assert.False(rig.Core.RadialMenuSnapshot.Visible, name);
            Assert.False(rig.States[^1].Visible, name);
            Assert.Empty(rig.Take());
            if (name == "disconnect")
            {
                // A reconnect starts from nothing held, so a held button counts as a new press there.
                continue;
            }

            // The trigger is still held: it neither fires the slice nor opens again, and the release is silent too.
            rig.Frame(Buttons.L1 | (name == "shift layer" ? Buttons.PS : Buttons.None), Tilt(90));
            Assert.False(rig.Core.RadialMenuOpen, name);
            rig.Frame(Buttons.None, Tilt(90));
            rig.Wait(100);
            string[] events = rig.Take();
            Assert.DoesNotContain("B down", events);
            if (name == "profile switch")
            {
                // The new profile's L1 key waits for a fresh press instead of firing from the held one.
                Assert.DoesNotContain("Z down", events);
            }

            if (canReopen)
            {
                rig.Frame(Buttons.L1, Tilt(90));
                Assert.True(rig.Core.RadialMenuOpen, name);
            }
        }
    }

    [Fact]
    public void Cancel_masks_the_held_button_for_the_virtual_pad_until_it_is_let_go()
    {
        using var rig = new EmulationRig(RadialStick.Left, hideMapped: false);
        rig.Pads[1].PublishInput(Inputs.Make());
        rig.Pads[1].PublishInput(Inputs.Make(Buttons.L1 | Buttons.Cross));
        Assert.True(rig.Core.RadialMenuOpen);
        Assert.True(rig.Pad.Submitted[^1].IsDown(Buttons.L1));

        rig.Core.CancelRadialMenu();
        rig.Pads[1].PublishInput(Inputs.Make(Buttons.L1 | Buttons.Cross));
        Assert.False(rig.Core.RadialMenuOpen);
        Assert.False(rig.Pad.Submitted[^1].IsDown(Buttons.L1));
        Assert.True(rig.Pad.Submitted[^1].IsDown(Buttons.Cross));

        rig.Pads[1].PublishInput(Inputs.Make(Buttons.Cross));
        rig.Pads[1].PublishInput(Inputs.Make(Buttons.L1));
        Assert.True(rig.Core.RadialMenuOpen);
        Assert.True(rig.Pad.Submitted[^1].IsDown(Buttons.L1));
    }

    [Fact]
    public void Cancel_from_pause_and_profile_change_does_not_deadlock_and_masks_the_held_button()
    {
        using var rig = new Rig(id => Config(id, RadialStick.Left, 4, ("cross", Bind.Key(Key.X))));
        var handlerRan = new List<bool>();
        rig.Core.RadialMenuChanged += state =>
        {
            if (!state.Visible)
            {
                // Another thread needs the input, mapping and gyro locks; none may be held while subscribers run.
                handlerRan.Add(Task.Run(() =>
                {
                    rig.Core.OnInput(Inputs.Make(Buttons.L1, Tilt(90)));
                    _ = rig.Core.Mapping;
                    _ = rig.Core.GyroCalibrating;
                }).Wait(TimeSpan.FromSeconds(5)));
            }
        };

        rig.Frame(Buttons.L1, Tilt(90));
        rig.Core.UiMappingPaused = true;
        Assert.False(rig.Core.RadialMenuOpen);
        rig.Core.UiMappingPaused = false;
        rig.Frame(Buttons.None, Tilt(90));
        rig.Take();

        rig.Frame(Buttons.L1, Tilt(90));
        Assert.True(rig.Core.RadialMenuOpen);
        rig.Core.ToggleUserPause();
        rig.Core.ToggleUserPause();
        Assert.False(rig.Core.RadialMenuOpen);

        // Still held through the pause: no open, no slice, and the next press starts fresh.
        rig.Frame(Buttons.L1, Tilt(90));
        Assert.False(rig.Core.RadialMenuOpen);
        Assert.Empty(rig.Take());
        rig.Frame(Buttons.None);
        rig.Frame(Buttons.L1, Tilt(90));
        Assert.True(rig.Core.RadialMenuOpen);

        // A real profile change (another profile, same menus) while a menu is open.
        rig.Frame(Buttons.None);
        rig.Frame(Buttons.L1, Tilt(90));
        Assert.True(rig.Core.RadialMenuOpen);
        Profile other = rig.Core.Profiles.Save(rig.Profile with { Id = "", Name = "Other" });
        rig.Core.ActivateProfile(other.Id);
        Assert.Equal(other.Id, rig.Core.ActiveProfile.Id);
        Assert.False(rig.Core.RadialMenuOpen);
        rig.Frame(Buttons.L1, Tilt(90));
        Assert.False(rig.Core.RadialMenuOpen);
        Assert.Empty(rig.Take());
        rig.Frame(Buttons.None);
        rig.Frame(Buttons.L1, Tilt(90));
        Assert.True(rig.Core.RadialMenuOpen);

        Assert.True(handlerRan.Count >= 3);
        Assert.All(handlerRan, ok => Assert.True(ok));
    }

    [Fact]
    public void Menu_stick_stays_neutral_after_closing_until_centered_and_the_closing_frame_is_neutral()
    {
        using var rig = new Rig(id => Config(id, RadialStick.Left) with { Sticks = KeySticks() });
        rig.Frame(Buttons.L1, left: new Stick(128, 0));
        Assert.True(rig.Core.RadialMenuOpen);
        Assert.Empty(rig.Take());

        // Release on slice 0: the key tap fires, the stick key does not.
        rig.Frame(Buttons.None, left: new Stick(128, 0));
        Assert.Equal(["A down"], rig.Take());
        rig.Frame(Buttons.None, left: new Stick(128, 0));
        rig.Wait(100);
        Assert.Equal(["A up"], rig.Take());

        // Centre once: the stick works again.
        rig.Frame(Buttons.None, left: Center);
        Assert.Empty(rig.Take());
        rig.Frame(Buttons.None, left: new Stick(128, 0));
        Assert.Equal(["W down"], rig.Take());
    }

    [Fact]
    public void Opening_cancels_a_running_flick_and_the_stick_stays_neutral_afterwards()
    {
        string id = Validation.NewId();
        using var rig = new Rig(_ => Config(id, RadialStick.Right) with { FlickStick = new FlickStickConfig { Enabled = true } });
        Stick far = new(255, 128);

        rig.Frame(right: far);
        rig.Wait(40);
        int partial = rig.Fake.Injector.MovedX;
        Assert.InRange(partial, 1, 1500);

        rig.Frame(Buttons.L1, right: far);
        Assert.True(rig.Core.RadialMenuOpen);
        int atOpen = rig.Fake.Injector.MovedX;
        rig.Wait(300);
        Assert.Equal(atOpen, rig.Fake.Injector.MovedX);
        Assert.InRange(atOpen, partial, 1500);

        // Closed with the stick still out: no new flick until it has been centred.
        rig.Frame(Buttons.None, right: far);
        rig.Wait(300);
        Assert.Equal(atOpen, rig.Fake.Injector.MovedX);
        rig.Frame(Buttons.None, right: Center);
        rig.Frame(Buttons.None, right: far);
        rig.Wait(300);
        Assert.True(rig.Fake.Injector.MovedX > atOpen + 1000);
    }

    [Fact]
    public void The_first_flick_after_a_menu_closes_and_the_stick_is_centred_is_not_swallowed()
    {
        string id = Validation.NewId();
        using var rig = new Rig(_ => Config(id, RadialStick.Right) with { FlickStick = new FlickStickConfig { Enabled = true } });
        Stick far = new(255, 128);

        rig.Frame(Buttons.L1, right: far);
        Assert.True(rig.Core.RadialMenuOpen);
        rig.Frame(Buttons.None, right: far);
        rig.Frame(Buttons.None, right: Center);
        rig.Wait(50);
        int before = rig.Fake.Injector.MovedX;
        rig.Frame(Buttons.None, right: far);
        rig.Wait(300);
        Assert.True(rig.Fake.Injector.MovedX > before + 1000);
    }

    [Fact]
    public void Second_open_is_ignored_and_its_release_never_closes_or_fires_the_open_menu()
    {
        string a = Validation.NewId();
        string b = Validation.NewId();
        using var rig = new MapperRig(new MappingConfig
        {
            Buttons = Bind.Map(("l1", new Binding { Press = OpenMenu(a) }), ("r1", new Binding { Press = OpenMenu(b) }),
                ("cross", Bind.Key(Key.X))),
            RadialMenus = [MenuOf(a, RadialStick.Left, 4), MenuOf(b, RadialStick.Right, 4)],
        });
        RadialMenuController radial = rig.Mapper.Radial;
        rig.Press(Buttons.L1);
        radial.Update(Tilt(90), Center);
        Assert.Equal(a, radial.State.MenuId);

        rig.Press(Buttons.R1).Release(Buttons.R1);
        Assert.True(radial.IsOpen);
        Assert.Equal(a, radial.State.MenuId);
        Assert.Equal(1, radial.State.SelectedIndex);
        Assert.Empty(rig.Take());

        rig.Release(Buttons.L1);
        Assert.False(radial.IsOpen);
        Assert.Equal(["B down"], rig.Take());
    }

    [Fact]
    public void Tap_ignores_a_radial_menu_action_and_an_unknown_id_opens_nothing()
    {
        string id = Validation.NewId();
        using var rig = new MapperRig(new MappingConfig());
        // Built without validation on purpose: a double-press binding whose delayed tap is the radial press.
        rig.Mapper.Configure(new Profile
        {
            Id = Validation.NewId(),
            Mapping = new MappingConfig
            {
                Enabled = true,
                Buttons = Bind.Map(("l1", new Binding { Press = OpenMenu(id), Double = Bind.Keys(Key.X) }),
                    ("r1", new Binding { Press = OpenMenu(Validation.NewId()) })),
                RadialMenus = [MenuOf(id, RadialStick.Left, 4)],
            },
        });
        rig.Press(Buttons.L1).Release(Buttons.L1);
        rig.Run(600);
        Assert.False(rig.Mapper.Radial.IsOpen);
        Assert.Empty(rig.Take());

        rig.Press(Buttons.R1);
        Assert.False(rig.Mapper.Radial.IsOpen);
        rig.Release(Buttons.R1);
        Assert.Empty(rig.Take());
    }

    [Fact]
    public void Validation_limits_menus_slices_blocks_nesting_and_strips_run_actions()
    {
        string first = Validation.NewId();
        string path = OperatingSystem.IsWindows() ? @"C:\Tools\a.exe" : "/tmp/a";
        ActionConfig run = new() { Kind = ActionKind.Run, Path = path };
        RadialSlice Slice(ActionConfig a) => new() { Label = "  label  ", Action = a };
        var menus = new List<RadialMenu>
        {
            new()
            {
                Id = first, Name = " Wheel ", Stick = RadialStick.Right,
                Slices = [Slice(run), Slice(OpenMenu(first)), Slice(Bind.Keys(Key.A)), Slice(run)],
            },
            new() { Id = "not-an-id", Name = "", Slices = [Slice(Bind.Keys(Key.B))] },
            new() { Id = first, Name = new string('n', 60), Slices = Enumerable.Range(0, 12).Select(_ => new RadialSlice { Label = new string('l', 40) }).ToArray() },
            new() { Id = Validation.NewId(), Name = "d", Slices = [] },
            new() { Id = Validation.NewId(), Name = "e", Slices = [new(), new()] },
            new() { Id = Validation.NewId(), Name = "f", Slices = [new(), new()] },
        };
        var issues = new List<string>();
        Profile p = Validation.Normalize(new Profile
        {
            Id = Validation.NewId(),
            Name = "R",
            Mapping = new MappingConfig
            {
                Enabled = true,
                Buttons = Bind.Map(("l1", new Binding { Press = OpenMenu(first) }), ("r1", new Binding { Press = OpenMenu(Validation.NewId()) })),
                RadialMenus = menus,
            },
        }, issues);

        IReadOnlyList<RadialMenu> kept = p.Mapping.RadialMenus;
        Assert.Equal(4, kept.Count);
        Assert.Equal(4, kept.Select(m => m.Id).Distinct().Count());
        Assert.All(kept, m => Assert.True(Validation.IsId(m.Id)));
        Assert.Equal(first, kept[0].Id);
        Assert.Equal(("Wheel", RadialStick.Right), (kept[0].Name, kept[0].Stick));
        Assert.Equal(["label", "label", "label", "label"], kept[0].Slices.Select(s => s.Label));
        Assert.Equal(ActionKind.Run, kept[0].Slices[0].Action.Kind);
        Assert.Equal(ActionKind.None, kept[0].Slices[1].Action.Kind);
        Assert.Equal(2, kept[1].Slices.Count);
        Assert.Equal(ActionKind.None, kept[1].Slices[1].Action.Kind);
        Assert.Equal("", kept[1].Slices[1].Label);
        Assert.NotEqual("", kept[1].Name);
        Assert.Equal(32, kept[2].Name.Length);
        Assert.Equal(8, kept[2].Slices.Count);
        Assert.All(kept[2].Slices, s => Assert.Equal(24, s.Label.Length));
        Assert.Equal(2, kept[3].Slices.Count);
        Assert.Equal(first, p.Mapping.Buttons["l1"].Press.RadialMenuId);
        Assert.Equal(ActionKind.None, p.Mapping.Buttons["r1"].Press.Kind);
        Assert.NotEmpty(issues);

        Profile stripped = Validation.StripRunActions(p, out int removed);
        Assert.Equal(2, removed);
        Assert.Equal(ActionKind.None, stripped.Mapping.RadialMenus[0].Slices[0].Action.Kind);
        Assert.Equal(ActionKind.None, stripped.Mapping.RadialMenus[0].Slices[3].Action.Kind);
        Assert.Equal(ActionKind.Key, stripped.Mapping.RadialMenus[0].Slices[2].Action.Kind);

        // A profile saved before radial menus existed loads with none and round-trips unchanged.
        const string legacy = """{"version":1,"name":"Old","mapping":{"enabled":true,"buttons":{"cross":{"press":{"kind":"mouseButton","button":"left"}}}}}""";
        Profile old = Validation.Normalize(JsonSerializer.Deserialize<Profile>(legacy, TactileJson.Options)!);
        Assert.Empty(old.Mapping.RadialMenus);
        Assert.Equal(ActionKind.MouseButton, old.Mapping.Buttons["cross"].Press.Kind);
        string once = JsonSerializer.Serialize(old, TactileJson.Options);
        Assert.Equal(once, JsonSerializer.Serialize(Validation.Normalize(JsonSerializer.Deserialize<Profile>(once, TactileJson.Options)!), TactileJson.Options));

        string json = JsonSerializer.Serialize(p.Mapping.Buttons["l1"].Press, TactileJson.Compact);
        Assert.Contains("\"special\":\"radialMenu\"", json);
        Assert.Contains($"\"radialMenuId\":\"{first}\"", json);
        Assert.Contains("\"stick\":\"right\"", JsonSerializer.Serialize(kept[0], TactileJson.Compact));
    }

    [Fact]
    public void Radial_menu_is_only_valid_as_a_plain_button_press()
    {
        string id = Validation.NewId();
        ActionConfig menu = OpenMenu(id);
        Binding Plain() => new() { Press = menu };
        var issues = new List<string>();
        Profile p = Validation.Normalize(new Profile
        {
            Id = Validation.NewId(),
            Name = "R",
            Mapping = new MappingConfig
            {
                Enabled = true,
                Buttons = Bind.Map(
                    ("l1", Plain()),
                    ("r1", new Binding { Press = menu, Hold = menu, Double = menu, Triple = menu, Cycle = [Bind.Keys(Key.B)], Turbo = true, Toggle = true }),
                    ("cross", new Binding { Press = Bind.Keys(Key.A), Hold = menu, Double = menu, Triple = menu, Cycle = [menu, Bind.Keys(Key.B)] }),
                    ("square", new Binding { Press = Bind.Keys(Key.A), Turbo = true })),
                Layers = [new MappingLayer { Name = "Shift", Button = "ps", Buttons = Bind.Map(("circle", Plain())) }],
                Chords = [new ButtonChord { Buttons = ["l1", "r1"], Action = menu }],
                Triggers = new TriggerMappingPair { Left = new TriggerMapping { Mode = TriggerStageMode.Additive, Soft = menu, Full = menu } },
                Sticks = new StickMappingPair { Left = new StickMapping { RingMode = StickRingMode.Outer, RingAction = menu } },
                Touchpad = new TouchpadMapping
                {
                    Mode = TouchpadMode.Zones,
                    Zones = [Plain(), Plain()],
                    Swipes = new SwipeActions { Up = menu, Down = menu, Left = menu, Right = menu },
                },
                RadialMenus = [MenuOf(id, RadialStick.Left, 3) with
                {
                    Slices = [new() { Action = menu }, new() { Action = Bind.Keys(Key.A) }, new() { Action = Bind.Keys(Key.B) }],
                }],
            },
        }, issues);

        MappingConfig m = p.Mapping;
        Assert.Equal(id, m.Buttons["l1"].Press.RadialMenuId);
        Assert.Equal(id, m.Layers[0].Buttons["circle"].Press.RadialMenuId);

        // Press stays, everything that would delay, repeat or latch it goes.
        Binding r1 = m.Buttons["r1"];
        Assert.Equal(id, r1.Press.RadialMenuId);
        Assert.Null(r1.Hold);
        Assert.Null(r1.Double);
        Assert.Null(r1.Triple);
        Assert.Null(r1.Cycle);
        Assert.False(r1.Turbo);
        Assert.False(r1.Toggle);
        Assert.True(m.Buttons["square"].Turbo);

        Binding cross = m.Buttons["cross"];
        Assert.Null(cross.Hold);
        Assert.Null(cross.Double);
        Assert.Null(cross.Triple);
        Assert.Equal([ActionKind.Key], cross.Cycle!.Select(a => a.Kind));
        Assert.Empty(m.Chords);
        Assert.Equal((ActionKind.None, ActionKind.None), (m.Triggers.Left.Soft.Kind, m.Triggers.Left.Full.Kind));
        Assert.Equal(ActionKind.None, m.Sticks.Left.RingAction.Kind);
        Assert.All(m.Touchpad.Zones, z => Assert.Equal(ActionKind.None, z.Press.Kind));
        Assert.All(new[] { m.Touchpad.Swipes.Up, m.Touchpad.Swipes.Down, m.Touchpad.Swipes.Left, m.Touchpad.Swipes.Right },
            a => Assert.Equal(ActionKind.None, a.Kind));
        Assert.Equal(ActionKind.None, m.RadialMenus[0].Slices[0].Action.Kind);
        Assert.Equal(ActionKind.Key, m.RadialMenus[0].Slices[1].Action.Kind);
        Assert.True(issues.Count >= 8);
    }

    // The keys a list of "X down" / "X up" / "mouse B down" events leaves held.
    private static string[] StillHeld(IEnumerable<string> events)
    {
        var held = new List<string>();
        foreach (string e in events)
        {
            int cut = e.LastIndexOf(' ');
            string name = e[..cut];
            if (e.EndsWith(" down", StringComparison.Ordinal))
            {
                held.Add(name);
            }
            else if (e.EndsWith(" up", StringComparison.Ordinal))
            {
                held.Remove(name);
            }
        }

        return [.. held];
    }

    [Fact]
    public void Two_menus_on_different_sticks_each_keep_their_own_latch()
    {
        string a = Validation.NewId();
        string b = Validation.NewId();
        Stick up = new(128, 0);
        using var rig = new MapperRig(new MappingConfig
        {
            Buttons = Bind.Map(("l1", new Binding { Press = OpenMenu(a) }), ("r1", new Binding { Press = OpenMenu(b) })),
            RadialMenus = [MenuOf(a, RadialStick.Left, 4), MenuOf(b, RadialStick.Right, 4)],
            Sticks = KeySticks(),
        });
        RadialMenuController radial = rig.Mapper.Radial;
        rig.Sticks(left: up, right: up);
        Assert.Equal(["I down", "W down"], rig.Take().Order());

        rig.Press(Buttons.L1).Release(Buttons.L1);
        Assert.True(radial.LeftNeutral);
        Assert.False(radial.RightNeutral);
        Assert.Equal(["W up"], rig.Take());

        // Opening the second menu keeps the first stick's latch.
        rig.Press(Buttons.R1);
        Assert.True(radial.IsOpen);
        Assert.True(radial.LeftNeutral);
        Assert.True(radial.RightNeutral);
        rig.Release(Buttons.R1);
        Assert.False(radial.IsOpen);
        Assert.True(radial.LeftNeutral);
        Assert.True(radial.RightNeutral);
        Assert.Equal(["I up"], rig.Take());

        // Each latch clears on its own, when that raw stick is centred.
        radial.Update(Center, up);
        Assert.False(radial.LeftNeutral);
        Assert.True(radial.RightNeutral);
        rig.Sticks(left: up);
        Assert.Equal(["W down"], rig.Take());
        radial.Update(up, Center);
        Assert.False(radial.RightNeutral);
        rig.Send();
        Assert.Equal(["I down"], rig.Take());
    }

    [Fact]
    public void Reopening_a_menu_on_a_latched_stick_works_and_a_centred_stick_clears_only_its_own_latch()
    {
        string id = Validation.NewId();
        var menu = new RadialMenuController();
        menu.Configure([MenuOf(id, RadialStick.Left, 4)]);
        Assert.True(menu.TryOpen(id) > 0);
        menu.Cancel();
        Assert.True(menu.LeftNeutral);
        Assert.True(menu.TryOpen(id) > 0);
        Assert.True(menu.LeftNeutral);
        menu.Cancel();
        menu.Update(Tilt(0), Center);
        Assert.True(menu.LeftNeutral);
        menu.Update(Center, Tilt(0));
        Assert.False(menu.LeftNeutral);
        Assert.False(menu.RightNeutral);
    }

    [Fact]
    public void Chord_member_radial_press_opens_when_the_window_ends_in_tick_and_releases_the_stick_there()
    {
        string id = Validation.NewId();
        using var rig = new MapperRig(Config(id, RadialStick.Left) with
        {
            Chords = [new ButtonChord { Buttons = ["l1", "r1"], Action = Bind.Keys(Key.X) }],
            Sticks = KeySticks(),
        });
        rig.Sticks(left: new Stick(128, 0));
        Assert.Equal(["W down"], rig.Take());

        rig.Press(Buttons.L1);
        Assert.False(rig.Mapper.Radial.IsOpen);
        rig.Run(60);
        Assert.True(rig.Mapper.Radial.IsOpen);
        Assert.Equal(["W up"], rig.Take());
        rig.Run(100);
        Assert.Empty(rig.Take());

        // The flush named the owner: a cancel withholds the chord member's button.
        rig.Mapper.Radial.Cancel();
        Assert.Equal(Buttons.L1, rig.Mapper.Radial.TakeCancelledButtons());
    }

    [Fact]
    public void Chord_member_radial_press_and_release_inside_the_window_opens_and_latches_nothing()
    {
        string id = Validation.NewId();
        using var rig = new MapperRig(Config(id, RadialStick.Left) with
        {
            Chords = [new ButtonChord { Buttons = ["l1", "r1"], Action = Bind.Keys(Key.X) }],
            Sticks = KeySticks(),
        });
        var states = new List<RadialMenuState>();
        rig.Mapper.Radial.Changed += states.Add;
        rig.Sticks(left: new Stick(128, 0));
        rig.Take();

        rig.Press(Buttons.L1).Release(Buttons.L1);
        rig.Run(100);
        rig.Mapper.Radial.Drain();
        Assert.False(rig.Mapper.Radial.IsOpen);
        Assert.False(rig.Mapper.Radial.LeftNeutral);
        Assert.Empty(states);
        Assert.Equal(Buttons.None, rig.Mapper.Radial.TakeCancelledButtons());
        Assert.Empty(rig.Take());
    }

    [Fact]
    public void A_neutral_stick_is_not_fed_to_the_processor_so_a_zero_deadzone_stick_does_not_move_the_mouse()
    {
        string id = Validation.NewId();
        using var rig = new MapperRig(Config(id, RadialStick.Left) with
        {
            Sticks = new StickMappingPair { Left = new StickMapping { Mode = StickMappingMode.Mouse } },
        }, sticks: new StickPair { Left = new StickConfig { InnerDeadzone = 0 } });
        rig.Press(Buttons.L1);
        Assert.True(rig.Mapper.Radial.IsOpen);
        rig.Take();
        rig.Run(1500);
        rig.Send();
        Assert.DoesNotContain(rig.Take(), e => e.StartsWith("move", StringComparison.Ordinal));

        // Closed but still latched: the same.
        rig.Release(Buttons.L1);
        rig.Run(1500);
        Assert.DoesNotContain(rig.Take(), e => e.StartsWith("move", StringComparison.Ordinal));

        // Centred once: the stick maps again (a real tilt now moves the cursor).
        rig.Mapper.Radial.Update(Center, Center);
        rig.Sticks(left: new Stick(255, 128));
        rig.Run(200);
        Assert.Contains(rig.Take(), e => e.StartsWith("move", StringComparison.Ordinal));
    }

    [Fact]
    public void Opening_the_keyboard_announces_the_radial_cancel_before_the_keyboard_state()
    {
        using var rig = new Rig(id => Config(id, RadialStick.Left));
        var order = new List<string>();
        rig.Core.RadialMenuChanged += s => order.Add(s.Visible ? "radial open" : "radial closed");
        rig.Core.OnScreenKeyboardChanged += s => order.Add(s.Visible ? "keyboard open" : "keyboard closed");
        rig.Frame(Buttons.L1);
        Assert.Equal(["radial open"], order);

        rig.Core.ToggleOnScreenKeyboard();
        Assert.Equal(["radial open", "radial closed", "keyboard open"], order);
    }

    [Fact]
    public void Ordered_events_name_their_owner_for_the_log()
    {
        Assert.Equal("On-screen keyboard", new OrderedEvents<int>().Name);
        Assert.Equal("Radial menu", new OrderedEvents<int> { Name = "Radial menu" }.Name);
    }

    [Fact]
    public void Mapping_off_dispose_and_fullscreen_pause_neither_fire_nor_leave_keys_held()
    {
        (string Name, Action<Rig> Act)[] paths =
        [
            ("mapping disabled", r => r.Core.Profiles.Save(r.Core.ActiveProfile with { Mapping = r.Core.ActiveProfile.Mapping with { Enabled = false } })),
            ("fullscreen pause", r =>
            {
                r.Core.Settings.Save(r.Core.Settings.Current with { PauseOnFullscreen = true });
                r.Foreground.IsFullscreen = true;
                r.Foreground.Set("browser.exe");
            }),
            ("dispose", r => r.Core.Dispose()),
        ];

        foreach ((string name, Action<Rig> act) in paths)
        {
            using var rig = new Rig(id => Config(id, RadialStick.Left) with { Sticks = KeySticks() });
            var events = new List<string>();
            rig.Frame(Buttons.None, left: Center, right: new Stick(255, 128));
            rig.Frame(Buttons.L1, Tilt(90), new Stick(255, 128));
            Assert.True(rig.Core.RadialMenuOpen, name);
            Assert.Equal(1, rig.Core.RadialMenuSnapshot.SelectedIndex);
            events.AddRange(rig.Take());

            act(rig);
            rig.Wait(100);
            events.AddRange(rig.Take());

            Assert.False(rig.Core.RadialMenuOpen, name);
            Assert.False(rig.States[^1].Visible, name);
            Assert.DoesNotContain("B down", events);
            Assert.Empty(StillHeld(events));
            if (name == "dispose")
            {
                continue;
            }

            rig.Frame(Buttons.L1, Tilt(90), new Stick(255, 128));
            rig.Frame(Buttons.None, Tilt(90), new Stick(255, 128));
            rig.Wait(100);
            events.AddRange(rig.Take());
            Assert.DoesNotContain("B down", events);
            Assert.Empty(StillHeld(events));
        }
    }

    [Fact]
    public void Emulation_mode_change_saving_the_profile_cancels_an_open_menu_without_firing_or_leaving_keys_held()
    {
        using var rig = new EmulationRig(RadialStick.Left, hideMapped: false);
        rig.Pads[1].PublishInput(Inputs.Make());
        rig.Pads[1].PublishInput(Inputs.Make(Buttons.L1, Tilt(90), new Stick(255, 128)));
        Assert.True(rig.Core.RadialMenuOpen);
        var events = new List<string>(rig.Fake.Injector.Take());

        rig.Core.Profiles.Save(rig.Core.ActiveProfile with { Emulation = new EmulationConfig { Mode = EmulationMode.DualSense } });
        rig.Pads[1].PublishInput(Inputs.Make(Buttons.L1, Tilt(90), new Stick(255, 128)));
        events.AddRange(rig.Fake.Injector.Take());
        Assert.DoesNotContain("B down", events);
        // Saving the profile cancels the open menu: it closes without firing and the held button stays withheld.
        Assert.False(rig.Core.RadialMenuOpen);

        // Released on the slice or cancelled: at most one tap, and nothing stays down.
        rig.Pads[1].PublishInput(Inputs.Make(Buttons.None, Tilt(90), new Stick(255, 128)));
        rig.Pads[1].PublishInput(Inputs.Make());
        rig.Time.Advance(TimeSpan.FromMilliseconds(100));
        rig.Core.Refresh();
        rig.Pads[1].PublishInput(Inputs.Make());
        events.AddRange(rig.Fake.Injector.Take());
        Assert.True(events.Count(e => e == "B down") <= 1);
        Assert.Empty(StillHeld(events));
        Assert.False(rig.Core.RadialMenuOpen);
    }

    [Fact]
    public void A_while_held_macro_slice_runs_as_a_tap_and_stops()
    {
        var macro = new Macro
        {
            Id = Validation.NewId(), Name = "M", Repeat = MacroRepeat.WhileHeld,
            Steps = [new MacroStep { Op = MacroOp.Tap, Key = Key.Z }, new MacroStep { Op = MacroOp.Delay, Ms = 50 }],
        };
        string id = Validation.NewId();
        using var rig = new MapperRig(new MappingConfig
        {
            Buttons = Bind.Map(("l1", new Binding { Press = OpenMenu(id) })),
            RadialMenus =
            [
                MenuOf(id, RadialStick.Left, 4) with
                {
                    Slices = [new RadialSlice { Label = "m", Action = Bind.Macro(macro.Id) }, new RadialSlice { Label = "b", Action = Bind.Keys(Key.B) }],
                },
            ],
        }, [macro]);
        rig.Press(Buttons.L1);
        rig.Mapper.Radial.Update(Tilt(0), Center);
        rig.Release(Buttons.L1);
        rig.Run(120);
        string[] first = rig.Take();
        Assert.InRange(first.Count(e => e == "Z down"), 1, 3);
        rig.Run(1000);
        Assert.Empty(rig.Take());
        Assert.Empty(StillHeld(first));
    }

    [Fact]
    public void Import_and_share_export_strip_run_actions_in_radial_slices()
    {
        string id = Validation.NewId();
        string path = OperatingSystem.IsWindows() ? @"C:\Tools\a.exe" : "/tmp/a";
        Profile profile = Validation.Normalize(new Profile
        {
            Id = Validation.NewId(),
            Name = "Radial run",
            Mapping = Config(id, RadialStick.Left) with
            {
                RadialMenus =
                [
                    MenuOf(id, RadialStick.Left, 3) with
                    {
                        Slices = [new() { Label = "run", Action = new ActionConfig { Kind = ActionKind.Run, Path = path } }, new() { Label = "a", Action = Bind.Keys(Key.A) }, new()],
                    },
                ],
            },
        });
        Assert.Equal(ActionKind.Run, profile.Mapping.RadialMenus[0].Slices[0].Action.Kind);

        using var dir = new TempDir();
        var store = new ProfileStore(dir.Path);
        string json = JsonSerializer.Serialize(profile, TactileJson.Options);
        Profile imported = store.Import(json, out int removed);
        Assert.Equal(1, removed);
        Assert.Equal(ActionKind.None, imported.Mapping.RadialMenus[0].Slices[0].Action.Kind);
        Assert.Equal(ActionKind.Key, imported.Mapping.RadialMenus[0].Slices[1].Action.Kind);
        Assert.Equal(ActionKind.None, store.Get(imported.Id)!.Mapping.RadialMenus[0].Slices[0].Action.Kind);

        CatalogSharePackage package = CatalogShareExporter.BuildSharePackage(profile);
        Assert.Equal(1, package.RemovedRunActions);
        Assert.False(CatalogProfileFile.ContainsRunAction(System.Text.Encoding.UTF8.GetBytes(package.Json)));
        Assert.Contains("\"radialMenus\"", package.Json);
    }

    [Fact]
    public void A_cancelled_trigger_button_stays_withheld_after_the_keyboard_closes_until_it_is_let_go()
    {
        using var rig = new EmulationRig(RadialStick.Left, hideMapped: false);
        rig.Pads[1].PublishInput(Inputs.Make());
        rig.Pads[1].PublishInput(Inputs.Make(Buttons.L1 | Buttons.Cross));
        Assert.True(rig.Core.RadialMenuOpen);

        rig.Core.ToggleOnScreenKeyboard();
        Assert.False(rig.Core.RadialMenuOpen);
        rig.Pads[1].PublishInput(Inputs.Make(Buttons.L1 | Buttons.Cross));
        rig.Core.CloseOnScreenKeyboard();

        rig.Pads[1].PublishInput(Inputs.Make(Buttons.L1 | Buttons.Cross));
        Assert.False(rig.Core.RadialMenuOpen);
        Assert.False(rig.Pad.Submitted[^1].IsDown(Buttons.L1));

        rig.Pads[1].PublishInput(Inputs.Make());
        rig.Pads[1].PublishInput(Inputs.Make(Buttons.L1 | Buttons.Cross));
        Assert.True(rig.Core.RadialMenuOpen);
        Assert.True(rig.Pad.Submitted[^1].IsDown(Buttons.L1));
    }

    [Fact]
    public void An_explicit_none_hold_beside_a_radial_press_is_not_a_conflict()
    {
        string id = Validation.NewId();
        List<string> Issues(Binding binding)
        {
            var issues = new List<string>();
            Validation.Normalize(new Profile
            {
                Id = Validation.NewId(),
                Name = "R",
                Mapping = new MappingConfig { Enabled = true, Buttons = Bind.Map(("l1", binding)), RadialMenus = [MenuOf(id, RadialStick.Left, 3)] },
            }, issues);
            return issues;
        }

        List<string> plain = Issues(new Binding { Press = OpenMenu(id) });
        Assert.Equal(plain, Issues(new Binding { Press = OpenMenu(id), Hold = ActionConfig.None, Double = ActionConfig.None, Triple = ActionConfig.None }));
        Assert.NotEqual(plain, Issues(new Binding { Press = OpenMenu(id), Hold = Bind.Keys(Key.A) }));
        Assert.NotEqual(plain, Issues(new Binding { Press = OpenMenu(id), Turbo = true }));
    }

    private sealed class EmulationRig : IDisposable
    {
        private readonly DriverRig _driver = new();

        public EmulationRig(RadialStick side, bool hideMapped = true, bool chord = false)
        {
            Pads = new FakePads { Present = [1] };
            var server = new FakeServer(_driver.Log);
            var tree = new FakeTree { InstanceIds = { ["1"] = @"TEST" } };
            Core = new AppCore(_driver.Dir.Path, new FakeForeground(), Pads.Service, Time, runTimer: false,
                platform: Fake.Platform, drivers: new DriverPlatform(new FakeDrivers(), _driver.Vhci, _driver.Hider, _driver.NewRecovery(), tree),
                server: server, steamRunning: () => false);
            string id = Validation.NewId();
            Profile profile = Core.Profiles.Save(Core.ActiveProfile with
            {
                Emulation = new EmulationConfig { Mode = EmulationMode.Xbox360, HideMappedButtons = hideMapped },
                Mapping = Config(id, side) with
                {
                    Sticks = KeySticks(),
                    Chords = chord ? [new ButtonChord { Buttons = ["l1", "r1"], Action = Bind.Keys(Key.X) }] : [],
                },
            });
            using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                DsuPort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
            }

            Core.Settings.Save(Core.Settings.Current with
            {
                DefaultProfileId = profile.Id,
                Integration = new IntegrationSettings { DsxPortFile = false, DsuEnabled = true, DsuPort = DsuPort },
            });
            Pads.Service.ScanIfIdle();
            Pad = server.Pads[0];
        }

        public FakePads Pads { get; }
        public ManualTime Time { get; } = new();
        public FakePlatform Fake { get; } = new();
        public AppCore Core { get; }
        public FakePad Pad { get; }
        public int DsuPort { get; }

        public void Dispose()
        {
            Core.Dispose();
            Pads.Dispose();
            _driver.Dispose();
        }
    }

    /// <summary>A loopback motion-server client that reads one data packet per call.</summary>
    private sealed class DsuProbe : IDisposable
    {
        private readonly UdpClient _client;

        private DsuProbe(UdpClient client) => _client = client;

        public static async Task<DsuProbe> Start(AppCore core, int port)
        {
            var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            client.Connect(IPAddress.Loopback, port);
            byte[] packet = new byte[28];
            "DSUC"u8.CopyTo(packet);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), 1001);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), (ushort)(packet.Length - 16));
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), 123);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), 0x100002);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), Crc32.Compute(packet));
            await client.SendAsync(packet.AsMemory());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (core.Integration.Dsu is not { Clients: 1 })
            {
                await Task.Delay(10, timeout.Token);
            }

            return new DsuProbe(client);
        }

        /// <summary>The payload after the 20-byte header: sticks are bytes 20 to 23 (left X, left Y inverted, right X, right Y inverted).</summary>
        public async Task<byte[]> Next()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            return (await _client.ReceiveAsync(timeout.Token)).Buffer[20..];
        }

        public void Dispose() => _client.Dispose();
    }
}
