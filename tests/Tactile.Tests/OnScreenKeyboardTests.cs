using Tactile.Core;
using Tactile.Device.DualSense;
using Tactile.Input;
using Tactile.Input.Windows;
using Tactile.Drivers;
using Tactile.Tests.Drivers;
using Tactile.Tests.EmulationCore;
using Tactile.Tests.Multi;
using InputRecord = Tactile.Input.Windows.Native.InputRecord;
using KeybdInput = Tactile.Input.Windows.Native.KeybdInput;

namespace Tactile.Tests;

public sealed class OnScreenKeyboardStateTests
{
    private static readonly Stick Center = new(128, 128);

    private sealed class Rig
    {
        private TimeSpan _now;

        public Rig(Language language = Language.En, bool open = true)
        {
            Keyboard = new OnScreenKeyboard(Injector);
            Keyboard.Changed += States.Add;
            if (open)
            {
                Keyboard.Open(language);
            }
        }

        public FakeInjector Injector { get; } = new();
        public OnScreenKeyboard Keyboard { get; }
        public List<OnScreenKeyboardState> States { get; } = [];
        public OnScreenKeyboardState State => Keyboard.State;

        public Rig At(double ms)
        {
            _now = TimeSpan.FromMilliseconds(ms);
            return this;
        }

        public Rig Send(Buttons buttons = Buttons.None, Stick? left = null)
        {
            Keyboard.HandleInput(Inputs.Make(buttons, left ?? Center), _now);
            return this;
        }

        public Rig Click(Buttons b)
        {
            Send(b);
            return Send();
        }

        public Rig Repeat(Buttons b, int times)
        {
            for (int i = 0; i < times; i++)
            {
                Click(b);
            }

            return this;
        }
    }

    [Fact]
    public void Opens_at_row_one_column_zero_in_lower_case_and_closed_means_not_visible()
    {
        var rig = new Rig(open: false);
        Assert.False(rig.State.Visible);
        rig.Keyboard.Open(Language.En);
        OnScreenKeyboardState state = rig.State;
        Assert.True(state.Visible);
        Assert.Equal((1, 0, false, "q"), (state.Row, state.Column, state.Uppercase, state.CurrentKey));
        Assert.Equal(4, state.Grid.Count);
        Assert.Equal(12, state.Grid[0].Count);
        Assert.Equal(10, state.Grid[3].Count);
        Assert.Equal(Language.En, state.Language);
    }

    [Fact]
    public void Layouts_match_the_contract_in_both_languages_and_cases()
    {
        string Join(IReadOnlyList<IReadOnlyList<string>> grid) => string.Join('|', grid.Select(r => string.Concat(r)));
        Assert.Equal("1234567890-=|qwertyuiop[]|asdfghjkl;'/|zxcvbnm,.@", Join(OnScreenKeyboard.GridFor(Language.En, false)));
        Assert.Equal("!@#$%^&*()_+|QWERTYUIOP{}|ASDFGHJKL:\"?|ZXCVBNM<>_", Join(OnScreenKeyboard.GridFor(Language.En, true)));
        Assert.Equal("1234567890*-|qwertyuıopğü|asdfghjklşi,|zxcvbnmöç.", Join(OnScreenKeyboard.GridFor(Language.Tr, false)));
        Assert.Equal("!'^+%&/()=?_|QWERTYUIOPĞÜ|ASDFGHJKLŞİ;|ZXCVBNMÖÇ:", Join(OnScreenKeyboard.GridFor(Language.Tr, true)));
    }

    [Fact]
    public void Dpad_moves_on_press_edges_only_and_wraps_in_both_axes()
    {
        Rig rig = new Rig().Repeat(Buttons.Right, 12);
        Assert.Equal((1, 0), (rig.State.Row, rig.State.Column));
        rig.Click(Buttons.Left);
        Assert.Equal((1, 11, "]"), (rig.State.Row, rig.State.Column, rig.State.CurrentKey));

        // Held down does not repeat.
        rig.Send(Buttons.Right).Send(Buttons.Right).Send(Buttons.Right);
        Assert.Equal(0, rig.State.Column);
        rig.Send();

        rig.Click(Buttons.Up);
        Assert.Equal((0, 0, "1"), (rig.State.Row, rig.State.Column, rig.State.CurrentKey));
        rig.Click(Buttons.Up);
        Assert.Equal(3, rig.State.Row);
        rig.Click(Buttons.Down);
        Assert.Equal(0, rig.State.Row);
    }

    [Fact]
    public void Rows_of_different_length_keep_the_column_clamped_to_the_shorter_row()
    {
        Rig rig = new Rig().Repeat(Buttons.Left, 1);
        Assert.Equal((1, 11), (rig.State.Row, rig.State.Column));
        rig.Click(Buttons.Down).Click(Buttons.Down);
        Assert.Equal((3, 9, "@"), (rig.State.Row, rig.State.Column, rig.State.CurrentKey));
        rig.Click(Buttons.Down);
        Assert.Equal((0, 9, "0"), (rig.State.Row, rig.State.Column, rig.State.CurrentKey));
        rig.Click(Buttons.Up);
        Assert.Equal((3, 9), (rig.State.Row, rig.State.Column));
    }

    [Fact]
    public void Left_stick_moves_at_once_then_repeats_every_180_ms_and_a_new_tilt_moves_at_once()
    {
        Rig rig = new Rig().At(1000);
        var right = new Stick(255, 128);
        rig.Send(left: right);
        Assert.Equal(1, rig.State.Column);
        rig.At(1100).Send(left: right);
        rig.At(1179).Send(left: right);
        Assert.Equal(1, rig.State.Column);
        rig.At(1180).Send(left: right);
        Assert.Equal(2, rig.State.Column);
        rig.At(1359).Send(left: right);
        Assert.Equal(2, rig.State.Column);
        rig.At(1360).Send(left: right);
        Assert.Equal(3, rig.State.Column);

        rig.At(1400).Send();
        rig.At(1401).Send(left: new Stick(0, 128));
        Assert.Equal(2, rig.State.Column);

        // Up needs a deflection of at least 0.5, and the stronger axis wins.
        rig.At(2000).Send();
        rig.At(2001).Send(left: new Stick(128, 100));
        Assert.Equal((1, 2), (rig.State.Row, rig.State.Column));
        rig.At(2002).Send(left: new Stick(128, 0));
        Assert.Equal(0, rig.State.Row);
        rig.At(2003).Send();
        rig.At(2004).Send(left: new Stick(255, 0));
        Assert.Equal(3, rig.State.Column);
    }

    [Fact]
    public void A_stick_already_tilted_when_it_opens_does_not_move_until_it_returns()
    {
        var keyboard = new OnScreenKeyboard(new FakeInjector());
        keyboard.Open(Language.En, Inputs.Make(left: new Stick(255, 128)));
        keyboard.HandleInput(Inputs.Make(left: new Stick(255, 128)), TimeSpan.FromSeconds(5));
        Assert.Equal(0, keyboard.State.Column);
    }

    [Fact]
    public void Cross_types_the_current_key_and_square_toggles_upper_case()
    {
        Rig rig = new Rig().Click(Buttons.Cross).Click(Buttons.Right).Click(Buttons.Cross);
        rig.Click(Buttons.Square).Click(Buttons.Cross);
        rig.Click(Buttons.Up).Click(Buttons.Cross);
        rig.Click(Buttons.Square).Click(Buttons.Cross);
        Assert.Equal(["q", "w", "W", "@", "2"], rig.Injector.TypedText);
        Assert.False(rig.State.Uppercase);
    }

    [Fact]
    public void Turkish_layout_types_its_own_letters_and_cases_dotted_and_dotless_i_correctly()
    {
        Rig rig = new(Language.Tr);
        rig.Repeat(Buttons.Right, 7).Click(Buttons.Cross);
        Assert.Equal("ı", rig.State.CurrentKey);
        rig.Click(Buttons.Square).Click(Buttons.Cross);
        Assert.Equal("I", rig.State.CurrentKey);
        rig.Click(Buttons.Square);

        rig.Repeat(Buttons.Right, 3).Click(Buttons.Cross);
        Assert.Equal("ğ", rig.State.CurrentKey);
        rig.Click(Buttons.Down).Click(Buttons.Cross);
        Assert.Equal("i", rig.State.CurrentKey);
        rig.Click(Buttons.Square).Click(Buttons.Cross);
        Assert.Equal("İ", rig.State.CurrentKey);
        rig.Click(Buttons.Square);
        rig.Click(Buttons.Left).Click(Buttons.Cross);
        Assert.Equal("ş", rig.State.CurrentKey);
        rig.Click(Buttons.Down).Click(Buttons.Left).Click(Buttons.Cross);
        Assert.Equal("ç", rig.State.CurrentKey);
        rig.Click(Buttons.Left).Click(Buttons.Cross);
        Assert.Equal("ö", rig.State.CurrentKey);
        rig.Click(Buttons.Up).Click(Buttons.Up).Repeat(Buttons.Right, 4);
        Assert.Equal("ü", rig.State.CurrentKey);
        Assert.Equal(["ı", "I", "ğ", "i", "İ", "ş", "ç", "ö"], rig.Injector.TypedText);
    }

    [Fact]
    public void Circle_triangle_and_options_tap_in_one_call_and_never_leave_a_key_held()
    {
        Rig rig = new();
        rig.Click(Buttons.Circle).Click(Buttons.Triangle).Click(Buttons.Options);
        Assert.Equal(["Backspace down", "Backspace up", "Space down", "Space up", "Enter down", "Enter up"], rig.Injector.Take());
    }

    [Fact]
    public void Held_circle_repeats_after_400_ms_every_60_ms_and_stops_on_release()
    {
        Rig rig = new Rig().At(0);
        rig.Send(Buttons.Circle);
        Assert.Equal(["Backspace down", "Backspace up"], rig.Injector.Take());
        rig.At(399).Send(Buttons.Circle);
        Assert.Empty(rig.Injector.Take());
        rig.At(400).Send(Buttons.Circle);
        Assert.Equal(["Backspace down", "Backspace up"], rig.Injector.Take());
        rig.At(459).Send(Buttons.Circle);
        Assert.Empty(rig.Injector.Take());
        rig.At(460).Send(Buttons.Circle);
        rig.At(520).Send(Buttons.Circle);
        Assert.Equal(4, rig.Injector.Take().Length);
        rig.At(530).Send();
        rig.At(900).Send();
        Assert.Empty(rig.Injector.Take());
        rig.At(1000).Send(Buttons.Circle);
        Assert.Equal(["Backspace down", "Backspace up"], rig.Injector.Take());
    }

    [Fact]
    public void Ps_closes_without_other_effects_and_a_closed_keyboard_ignores_input()
    {
        Rig rig = new();
        rig.Send(Buttons.PS | Buttons.Cross | Buttons.Triangle);
        Assert.False(rig.Keyboard.IsOpen);
        Assert.False(rig.State.Visible);
        Assert.Empty(rig.Injector.Take());
        rig.Click(Buttons.Cross).Click(Buttons.Right);
        Assert.Empty(rig.Injector.Take());
        Assert.Equal(0, rig.State.Column);
    }

    [Fact]
    public void A_button_still_held_from_the_opening_press_does_not_act_until_pressed_again()
    {
        var injector = new FakeInjector();
        var keyboard = new OnScreenKeyboard(injector);
        keyboard.Open(Language.En, Inputs.Make(Buttons.Cross | Buttons.Triangle));
        keyboard.HandleInput(Inputs.Make(Buttons.Cross | Buttons.Triangle), TimeSpan.Zero);
        Assert.Empty(injector.Take());
        keyboard.HandleInput(Inputs.Make(), TimeSpan.Zero);
        keyboard.HandleInput(Inputs.Make(Buttons.Cross), TimeSpan.Zero);
        Assert.Equal(["q"], injector.TypedText);
    }

    [Fact]
    public void Changed_is_raised_only_when_something_visible_changes()
    {
        Rig rig = new();
        Assert.Single(rig.States);
        Assert.True(rig.States[0].Visible);

        rig.Click(Buttons.Cross).Click(Buttons.Triangle).Click(Buttons.Circle).Click(Buttons.Options);
        rig.Send(left: new Stick(130, 128));
        Assert.Single(rig.States);

        rig.Click(Buttons.Right);
        Assert.Equal(2, rig.States.Count);
        rig.Click(Buttons.Square);
        Assert.Equal(3, rig.States.Count);
        Assert.True(rig.States[^1].Uppercase);
        rig.Keyboard.SetLanguage(Language.En);
        Assert.Equal(3, rig.States.Count);
        rig.Keyboard.SetLanguage(Language.Tr);
        Assert.Equal(4, rig.States.Count);
        rig.Click(Buttons.PS);
        Assert.False(rig.States[^1].Visible);
        int count = rig.States.Count;
        rig.Keyboard.Close();
        rig.Keyboard.SetLanguage(Language.En);
        Assert.Equal(count, rig.States.Count);
    }

    [Fact]
    public void Language_change_while_open_rebuilds_the_grid_and_keeps_the_cell_and_case()
    {
        Rig rig = new Rig().Repeat(Buttons.Right, 7).Click(Buttons.Square);
        Assert.Equal("I", rig.State.CurrentKey);
        rig.Keyboard.SetLanguage(Language.Tr);
        Assert.Equal((Language.Tr, true, "I"), (rig.State.Language, rig.State.Uppercase, rig.State.CurrentKey));
        rig.Click(Buttons.Right).Click(Buttons.Right).Click(Buttons.Right).Click(Buttons.Right);
        Assert.Equal("Ü", rig.State.CurrentKey);
        rig.Click(Buttons.Down).Click(Buttons.Down);
        Assert.Equal((3, 9), (rig.State.Row, rig.State.Column));
        rig.Keyboard.SetLanguage(Language.En);
        Assert.Equal((Language.En, 3, 9, "_"), (rig.State.Language, rig.State.Row, rig.State.Column, rig.State.CurrentKey));
    }
    [Fact]
    public void Events_arrive_in_order_even_when_a_handler_changes_the_keyboard_again()
    {
        var rig = new Rig(open: false);
        var seen = new List<bool>();
        rig.Keyboard.Changed += s =>
        {
            seen.Add(s.Visible);
            if (seen.Count == 1)
            {
                rig.Keyboard.Close();
            }
        };

        rig.Keyboard.Open(Language.En);
        Assert.Equal([true, false], seen);
    }

    [Fact]
    public void Idle_frames_publish_nothing()
    {
        var rig = new Rig();
        int count = rig.States.Count;
        for (int i = 0; i < 100; i++)
        {
            rig.At(i).Send();
        }

        Assert.Equal(count, rig.States.Count);
    }
}

[Collection("Localization")]
public sealed class OnScreenKeyboardCoreTests
{
    private static ActionConfig OpenKeyboard => new() { Kind = ActionKind.Special, Special = SpecialAction.OnScreenKeyboard };

    private static AppCore Core(TempDir dir, FakePlatform fake) =>
        new(dir.Path, new FakeForeground(), new ControllerService(autoConnect: false), new ManualTime(), runTimer: false, fake.Platform);

    private static Profile Map(AppCore core, params (string Button, ActionConfig Action)[] items) =>
        core.Profiles.Save(core.ActiveProfile with
        {
            Mapping = new MappingConfig
            {
                Enabled = true,
                Buttons = items.ToDictionary(i => i.Button, i => new Binding { Press = i.Action }),
            },
        });

    private static AppCore OpenedCore(TempDir dir, FakePlatform fake, params (string Button, ActionConfig Action)[] extra)
    {
        AppCore core = Core(dir, fake);
        Map(core, [("mic", OpenKeyboard), .. extra]);
        core.OnInput(Inputs.Make(Buttons.Mic));
        core.OnInput(Inputs.Make());
        Assert.True(core.OnScreenKeyboardOpen);
        fake.Injector.Take();
        return core;
    }

    [Fact]
    public void Special_action_opens_the_keyboard_and_lets_go_of_what_was_held()
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        using AppCore core = Core(dir, fake);
        Map(core, ("mic", OpenKeyboard), ("cross", Bind.Keys(Key.A)));
        var states = new List<OnScreenKeyboardState>();
        core.OnScreenKeyboardChanged += states.Add;

        core.OnInput(Inputs.Make(Buttons.Cross));
        Assert.Equal(["A down"], fake.Injector.Take());
        core.OnInput(Inputs.Make(Buttons.Cross | Buttons.Mic));

        Assert.True(core.OnScreenKeyboardOpen);
        Assert.Equal(["A up"], fake.Injector.Take());
        OnScreenKeyboardState state = Assert.Single(states);
        Assert.True(state.Visible);
        Assert.Equal("q", core.OnScreenKeyboardSnapshot.CurrentKey);

        // Cross is still held from before the keyboard opened: no key press for either side.
        core.OnInput(Inputs.Make(Buttons.Cross | Buttons.Mic));
        Assert.Empty(fake.Injector.Take());
    }

    [Fact]
    public void Open_keyboard_gets_the_input_and_mapping_gets_none_of_it()
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        using AppCore core = OpenedCore(dir, fake, ("cross", Bind.Keys(Key.A)), ("triangle", Bind.Keys(Key.T)),
            ("square", Bind.Keys(Key.S)), ("circle", Bind.Keys(Key.C)), ("options", Bind.Keys(Key.O)),
            ("up", Bind.Keys(Key.U)), ("right", Bind.Keys(Key.R)), ("ps", Bind.Keys(Key.P)));

        core.OnInput(Inputs.Make(Buttons.Cross));
        core.OnInput(Inputs.Make());
        core.OnInput(Inputs.Make(Buttons.Right));
        core.OnInput(Inputs.Make());
        core.OnInput(Inputs.Make(Buttons.Triangle | Buttons.Options | Buttons.Circle));
        core.OnInput(Inputs.Make());
        core.OnInput(Inputs.Make(left: new Stick(255, 128)));
        core.OnInput(Inputs.Make(Buttons.Up | Buttons.Square));
        core.OnInput(Inputs.Make());

        Assert.Equal("q", fake.Injector.TypedText[0]);
        string[] events = fake.Injector.Take();
        string[] expected = ["text q", "Backspace down", "Backspace up", "Space down", "Space up", "Enter down", "Enter up"];
        Assert.Equal(expected, events.Where(e => e.StartsWith("text ") || e.StartsWith("Backspace") || e.StartsWith("Space") || e.StartsWith("Enter")).ToArray());
        Assert.DoesNotContain(events, e => e is "A down" or "T down" or "S down" or "C down" or "O down" or "U down" or "R down" or "P down");
        Assert.DoesNotContain(events, e => e.StartsWith("move ") || e.StartsWith("mouse "));
    }

    [Fact]
    public void Ps_closes_and_stays_withheld_until_released_so_the_ps_layer_never_engages()
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        using AppCore core = Core(dir, fake);
        core.Profiles.Save(core.ActiveProfile with
        {
            Mapping = new MappingConfig
            {
                Enabled = true,
                Buttons = Bind.Map(("mic", new Binding { Press = OpenKeyboard }), ("cross", Bind.Key(Key.A))),
                Layers = [new() { Name = "Shift", Button = "ps", Mode = LayerMode.Hold, Buttons = Bind.Map(("cross", Bind.Key(Key.B))) }],
            },
        });
        var layers = new List<string?>();
        core.ActiveLayerChanged += layers.Add;
        core.OnInput(Inputs.Make(Buttons.Mic));
        core.OnInput(Inputs.Make());
        Assert.True(core.OnScreenKeyboardOpen);
        fake.Injector.Take();

        core.OnInput(Inputs.Make(Buttons.PS));
        Assert.False(core.OnScreenKeyboardOpen);
        core.OnInput(Inputs.Make(Buttons.PS));
        core.OnInput(Inputs.Make(Buttons.PS | Buttons.Cross));
        Assert.Equal(["A down"], fake.Injector.Take());
        core.OnInput(Inputs.Make());
        Assert.Equal(["A up"], fake.Injector.Take());
        Assert.Empty(layers);

        core.OnInput(Inputs.Make(Buttons.PS));
        core.OnInput(Inputs.Make(Buttons.PS | Buttons.Cross));
        Assert.Equal(["B down"], fake.Injector.Take());
        Assert.Equal(["Shift"], layers);
    }

    [Fact]
    public void The_action_key_cannot_leak_into_mapping_or_toggle_the_keyboard_while_it_is_open()
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        using AppCore core = OpenedCore(dir, fake);

        core.OnInput(Inputs.Make(Buttons.Mic));
        core.OnInput(Inputs.Make());
        core.OnInput(Inputs.Make(Buttons.Mic));
        Assert.True(core.OnScreenKeyboardOpen);
        Assert.Empty(fake.Injector.Take());

        // The core method closes it for the owner, and the same key opens it again afterwards.
        core.CloseOnScreenKeyboard();
        Assert.False(core.OnScreenKeyboardOpen);
        core.OnInput(Inputs.Make());
        core.OnInput(Inputs.Make(Buttons.Mic));
        Assert.True(core.OnScreenKeyboardOpen);
    }

    [Fact]
    public void Controller_pause_combo_is_ignored_while_the_keyboard_is_open()
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        using AppCore core = OpenedCore(dir, fake);
        core.Settings.Save(core.Settings.Current with { PauseCombo = ["l1", "r1"] });

        core.OnInput(Inputs.Make(Buttons.L1 | Buttons.R1));
        core.OnInput(Inputs.Make());
        Assert.False(core.UserMappingPaused);

        core.CloseOnScreenKeyboard();
        core.OnInput(Inputs.Make(Buttons.L1 | Buttons.R1));
        core.OnInput(Inputs.Make());
        core.OnInput(Inputs.Make(Buttons.L1 | Buttons.R1));
        Assert.False(core.UserMappingPaused);
        core.OnInput(Inputs.Make(Buttons.L1));
        core.OnInput(Inputs.Make(Buttons.L1 | Buttons.R1));
        Assert.True(core.UserMappingPaused);
    }

    [Fact]
    public void Disconnect_mapping_off_and_dispose_close_the_keyboard()
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        using AppCore core = OpenedCore(dir, fake);
        var states = new List<OnScreenKeyboardState>();
        core.OnScreenKeyboardChanged += states.Add;

        core.OnConnection(null);
        Assert.False(core.OnScreenKeyboardOpen);
        Assert.False(Assert.Single(states).Visible);

        core.OnInput(Inputs.Make(Buttons.Mic));
        core.OnInput(Inputs.Make());
        Assert.True(core.OnScreenKeyboardOpen);
        core.Profiles.Save(core.ActiveProfile with { Mapping = core.ActiveProfile.Mapping with { Enabled = false } });
        core.Refresh();
        Assert.False(core.OnScreenKeyboardOpen);
    }

    [Fact]
    public void Dispose_closes_an_open_keyboard()
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        AppCore core = OpenedCore(dir, fake);
        var states = new List<OnScreenKeyboardState>();
        core.OnScreenKeyboardChanged += states.Add;
        core.Dispose();
        Assert.False(core.OnScreenKeyboardOpen);
        Assert.False(Assert.Single(states).Visible);
    }

    [Fact]
    public void Language_follows_settings_when_opened_and_rebuilds_live_while_open()
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        using AppCore core = Core(dir, fake);
        Map(core, ("mic", OpenKeyboard));
        core.Settings.Save(core.Settings.Current with { Language = Language.Tr });
        core.OnInput(Inputs.Make(Buttons.Mic));
        Assert.Equal(Language.Tr, core.OnScreenKeyboardSnapshot.Language);
        Assert.Equal("q", core.OnScreenKeyboardSnapshot.CurrentKey);

        core.Settings.Save(core.Settings.Current with { Language = Language.En });
        Assert.Equal(Language.En, core.OnScreenKeyboardSnapshot.Language);
        Assert.True(core.OnScreenKeyboardOpen);
    }

    [Fact]
    public void Without_a_mapping_the_keyboard_does_not_open_from_the_toggle()
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        using AppCore core = Core(dir, fake);
        core.OnInput(Inputs.Make());
        core.ToggleOnScreenKeyboard();
        Assert.False(core.OnScreenKeyboardOpen);
    }

    [Fact]
    public void Emulation_gets_a_neutral_pad_while_open_and_nothing_held_stays_held_after_closing()
    {
        using var driver = new DriverRig();
        using var pads = new FakePads { Present = [1] };
        var fake = new FakePlatform();
        var server = new FakeServer(driver.Log);
        var tree = new FakeTree { InstanceIds = { ["1"] = @"TEST" } };
        using var core = new AppCore(driver.Dir.Path, new FakeForeground(), pads.Service, new ManualTime(), runTimer: false,
            platform: fake.Platform, drivers: new DriverPlatform(new FakeDrivers(), driver.Vhci, driver.Hider, driver.NewRecovery(), tree),
            server: server, steamRunning: () => false);
        Profile profile = core.Profiles.Save(core.ActiveProfile with
        {
            Emulation = new() { Mode = EmulationMode.Xbox360 },
            Mapping = new() { Enabled = true, Buttons = Bind.Map(("mic", new Binding { Press = OpenKeyboard })) },
        });
        core.Settings.Save(core.Settings.Current with { DefaultProfileId = profile.Id });
        pads.Service.ScanIfIdle();
        FakePad pad = server.Pads[0];
        Stick far = new(255, 0);
        Stick other = new(0, 255);

        pads[1].PublishInput(Inputs.Make(Buttons.Cross, far, other, l2: 200, r2: 90, gyro: new Vector3(500, 500, 500)));
        InputState before = pad.Submitted[^1];
        Assert.True(before.IsDown(Buttons.Cross));
        Assert.Equal(far, before.Left);

        // Opens while Cross and the sticks are held: the pad ends on a neutral frame, not the one that opened it.
        pads[1].PublishInput(Inputs.Make(Buttons.Cross | Buttons.Mic, far, other, l2: 200, r2: 90));
        Assert.True(core.OnScreenKeyboardOpen);
        AssertNeutral(pad.Submitted[^1]);

        pads[1].PublishInput(Inputs.Make(Buttons.Cross | Buttons.Up, far, other, l2: 200, r2: 90,
            touch1: new TouchPoint(true, 1, 100, 100), gyro: new Vector3(900, 900, 900)));
        AssertNeutral(pad.Submitted[^1]);

        // Closed by PS with Cross, Up and PS held: those stay withheld from the pad until let go.
        int count = pad.Submitted.Count;
        pads[1].PublishInput(Inputs.Make(Buttons.Cross | Buttons.Up | Buttons.PS, far));
        Assert.False(core.OnScreenKeyboardOpen);
        Assert.True(pad.Submitted.Count > count);
        Assert.Equal(Buttons.None, pad.Submitted[^1].Buttons);
        pads[1].PublishInput(Inputs.Make(Buttons.Cross | Buttons.Up));
        Assert.Equal(Buttons.None, pad.Submitted[^1].Buttons);
        pads[1].PublishInput(Inputs.Make(Buttons.Cross));
        Assert.Equal(Buttons.None, pad.Submitted[^1].Buttons);
        pads[1].PublishInput(Inputs.Make());
        pads[1].PublishInput(Inputs.Make(Buttons.Cross));
        Assert.True(pad.Submitted[^1].IsDown(Buttons.Cross));
    }

    private static void AssertNeutral(InputState s)
    {
        Assert.Equal(Buttons.None, s.Buttons);
        Assert.Equal(new Stick(128, 128), s.Left);
        Assert.Equal(new Stick(128, 128), s.Right);
        Assert.Equal((byte)0, s.L2);
        Assert.Equal((byte)0, s.R2);
        Assert.False(s.Touch1.Active);
        Assert.Equal(default, s.GyroRaw);
    }

    [Fact]
    public void Edge_fn1_triangle_toggles_on_the_primary_only_and_its_press_types_nothing()
    {
        using var dir = new TempDir();
        using var pads = new FakePads();
        var fake = new FakePlatform();
        using var core = new AppCore(dir.Path, new FakeForeground(), pads.Service, new ManualTime(), runTimer: false, fake.Platform);
        Map(core, ("triangle", Bind.Keys(Key.T)), ("fn1", Bind.Keys(Key.F)));
        pads.Service.ScanIfIdle();

        // Pad 2 is the Edge but only a secondary controller here: the shortcut does nothing.
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1));
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1 | Buttons.Triangle));
        Assert.False(core.OnScreenKeyboardOpen);
        pads[2].PublishInput(Inputs.Make());

        core.SetPrimaryController(FakePads.Mac(2));
        pads[2].PublishInput(Inputs.Make());
        fake.Injector.Take();
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1));
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1 | Buttons.Triangle));
        Assert.True(core.OnScreenKeyboardOpen);
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1 | Buttons.Triangle));
        pads[2].PublishInput(Inputs.Make());
        Assert.DoesNotContain("Space down", fake.Injector.Take());

        pads[2].PublishInput(Inputs.Make(Buttons.Fn1));
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1 | Buttons.Triangle));
        Assert.False(core.OnScreenKeyboardOpen);
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1 | Buttons.Triangle));
        pads[2].PublishInput(Inputs.Make());
        Assert.DoesNotContain(fake.Injector.Take(), e => e is "T down" or "F down" or "Space down");
    }

    [Fact]
    public void Edge_shortcut_is_ignored_when_edge_shortcuts_are_off()
    {
        using var dir = new TempDir();
        using var pads = new FakePads { Present = [2] };
        var fake = new FakePlatform();
        using var core = new AppCore(dir.Path, new FakeForeground(), pads.Service, new ManualTime(), runTimer: false, fake.Platform);
        Map(core, ("triangle", Bind.Keys(Key.T)));
        core.Settings.Save(core.Settings.Current with { EdgeShortcuts = false });
        pads.Service.ScanIfIdle();
        pads[2].PublishInput(Inputs.Make());
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1));
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1 | Buttons.Triangle));
        Assert.False(core.OnScreenKeyboardOpen);
    }

    [Fact]
    public void Threaded_mapper_opens_the_keyboard_in_the_same_input_pass()
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        using var core = new AppCore(dir.Path, new FakeForeground(), new ControllerService(autoConnect: false), new ManualTime(),
            runTimer: true, fake.Platform);
        Map(core, ("mic", OpenKeyboard));
        var states = new List<OnScreenKeyboardState>();
        core.OnScreenKeyboardChanged += states.Add;

        core.OnInput(Inputs.Make(Buttons.Mic));

        Assert.True(core.OnScreenKeyboardOpen);
        Assert.True(Assert.Single(states).Visible);
    }

    [Fact]
    public void Keyboard_events_are_raised_outside_the_input_lock()
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        using AppCore core = Core(dir, fake);
        Map(core, ("mic", OpenKeyboard));
        bool reached = false;
        core.OnScreenKeyboardChanged += state =>
        {
            if (state.Visible)
            {
                reached = Task.Run(core.CloseOnScreenKeyboard).Wait(TimeSpan.FromSeconds(5));
            }
        };

        core.OnInput(Inputs.Make(Buttons.Mic));
        Assert.True(reached);
        Assert.False(core.OnScreenKeyboardOpen);
    }

    [Fact]
    public void Edge_shortcuts_other_than_the_keyboard_one_are_ignored_while_it_is_open()
    {
        using var dir = new TempDir();
        using var pads = new FakePads { Present = [2] };
        var fake = new FakePlatform();
        using var core = new AppCore(dir.Path, new FakeForeground(), pads.Service, new ManualTime(), runTimer: false, fake.Platform);
        Map(core, ("cross", Bind.Keys(Key.T)));
        core.Profiles.Save(core.ActiveProfile with { Id = Validation.NewId(), Name = "Other" });
        pads.Service.ScanIfIdle();
        pads[2].PublishInput(Inputs.Make());
        string first = core.ActiveProfile.Id;

        pads[2].PublishInput(Inputs.Make(Buttons.Fn1));
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1 | Buttons.Triangle));
        Assert.True(core.OnScreenKeyboardOpen);
        pads[2].PublishInput(Inputs.Make());
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1));
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1 | Buttons.R1));
        Assert.Equal(first, core.ActiveProfile.Id);
        Assert.True(core.OnScreenKeyboardOpen);

        // The same shortcut works again once the keyboard is closed.
        pads[2].PublishInput(Inputs.Make());
        pads[2].PublishInput(Inputs.Make(Buttons.PS));
        Assert.False(core.OnScreenKeyboardOpen);
        pads[2].PublishInput(Inputs.Make());
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1));
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1 | Buttons.R1));
        Assert.NotEqual(first, core.ActiveProfile.Id);
    }

    [Fact]
    public void Edge_shortcut_does_not_open_while_paused_and_pausing_closes_the_keyboard()
    {
        using var dir = new TempDir();
        using var pads = new FakePads { Present = [2] };
        var fake = new FakePlatform();
        using var core = new AppCore(dir.Path, new FakeForeground(), pads.Service, new ManualTime(), runTimer: false, fake.Platform);
        Map(core, ("cross", Bind.Keys(Key.T)));
        pads.Service.ScanIfIdle();
        pads[2].PublishInput(Inputs.Make());

        core.UiMappingPaused = true;
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1));
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1 | Buttons.Triangle));
        Assert.False(core.OnScreenKeyboardOpen);

        core.UiMappingPaused = false;
        pads[2].PublishInput(Inputs.Make());
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1));
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1 | Buttons.Triangle));
        Assert.True(core.OnScreenKeyboardOpen);

        core.UiMappingPaused = true;
        Assert.False(core.OnScreenKeyboardOpen);
    }

    [Fact]
    public void Trigger_held_through_closing_and_motion_stay_neutral_on_the_virtual_pad()
    {
        using var driver = new DriverRig();
        using var pads = new FakePads { Present = [1] };
        var fake = new FakePlatform();
        var server = new FakeServer(driver.Log);
        var tree = new FakeTree { InstanceIds = { ["1"] = @"TEST" } };
        using var core = new AppCore(driver.Dir.Path, new FakeForeground(), pads.Service, new ManualTime(), runTimer: false,
            platform: fake.Platform, drivers: new DriverPlatform(new FakeDrivers(), driver.Vhci, driver.Hider, driver.NewRecovery(), tree),
            server: server, steamRunning: () => false);
        Profile profile = core.Profiles.Save(core.ActiveProfile with
        {
            Emulation = new() { Mode = EmulationMode.Xbox360 },
            Mapping = new() { Enabled = true, Buttons = Bind.Map(("mic", new Binding { Press = OpenKeyboard })) },
        });
        core.Settings.Save(core.Settings.Current with { DefaultProfileId = profile.Id });
        pads.Service.ScanIfIdle();
        FakePad pad = server.Pads[0];

        pads[1].PublishInput(Inputs.Make(Buttons.Mic | Buttons.L2, l2: 200) with { AccelRaw = new Vector3(5, 6, 7) });
        Assert.True(core.OnScreenKeyboardOpen);
        Assert.Equal(default, pad.Submitted[^1].AccelRaw);

        pads[1].PublishInput(Inputs.Make(Buttons.PS | Buttons.L2, l2: 200));
        Assert.False(core.OnScreenKeyboardOpen);
        Assert.Equal((byte)0, pad.Submitted[^1].L2);
        pads[1].PublishInput(Inputs.Make(Buttons.L2, l2: 180));
        Assert.Equal((byte)0, pad.Submitted[^1].L2);
        pads[1].PublishInput(Inputs.Make());
        pads[1].PublishInput(Inputs.Make(Buttons.L2, l2: 180));
        Assert.Equal((byte)180, pad.Submitted[^1].L2);
    }

    [Fact]
    public void Closed_keyboard_leaves_existing_mapping_behaviour_untouched()
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        using AppCore core = Core(dir, fake);
        Map(core, ("cross", Bind.Keys(Key.A)), ("circle", Bind.Keys(Key.P)));
        core.OnInput(Inputs.Make(Buttons.Cross | Buttons.Circle));
        core.OnInput(Inputs.Make());
        Assert.Equal(["A down", "A up", "P down", "P up"], fake.Injector.Take().Order().ToArray());
        Assert.False(core.OnScreenKeyboardOpen);
    }

    [Fact]
    public void Profile_change_announces_the_layer_reset_once_and_after_the_profile()
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        using AppCore core = Core(dir, fake);
        core.Profiles.Save(core.ActiveProfile with
        {
            Mapping = new MappingConfig
            {
                Enabled = true,
                Layers = [new() { Name = "Shift", Button = "l1", Mode = LayerMode.Hold, Buttons = Bind.Map(("cross", Bind.Key(Key.B))) }],
            },
        });
        Profile other = core.Profiles.Save(core.ActiveProfile with { Id = Validation.NewId(), Name = "Other", Mapping = new() { Enabled = true } });
        core.OnInput(Inputs.Make(Buttons.L1));
        var events = new List<string>();
        core.ActiveProfileChanged += () => events.Add("profile");
        core.ActiveLayerChanged += layer => events.Add($"layer:{layer}");

        core.ActivateProfile(other.Id);
        core.OnInput(Inputs.Make(Buttons.L1));

        Assert.Equal(["profile", "layer:"], events);
    }

    [Fact]
    public void Language_change_while_open_redraws_the_keyboard_at_once()
    {
        Language before = Text.Current;
        try
        {
            using var dir = new TempDir();
            var fake = new FakePlatform();
            using AppCore core = OpenedCore(dir, fake);
            var states = new List<OnScreenKeyboardState>();
            core.OnScreenKeyboardChanged += states.Add;
            Language target = core.Settings.Current.Language == Language.Tr ? Language.En : Language.Tr;

            core.Settings.Save(core.Settings.Current with { Language = target });

            Assert.Equal(target, Assert.Single(states).Language);
        }
        finally
        {
            Text.Current = before;
        }
    }

    [Fact]
    public void A_timer_driven_toggle_opens_the_keyboard_on_the_tick_without_new_input()
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        var time = new ManualTime();
        using var core = new AppCore(dir.Path, new FakeForeground(), new ControllerService(autoConnect: false), time, runTimer: false, fake.Platform);
        core.Profiles.Save(core.ActiveProfile with
        {
            Mapping = new MappingConfig
            {
                Enabled = true,
                Buttons = new Dictionary<string, Binding> { ["mic"] = new Binding { Hold = OpenKeyboard, HoldMs = 300 } },
            },
        });
        core.OnInput(Inputs.Make(Buttons.Mic));
        Assert.False(core.OnScreenKeyboardOpen);

        time.Advance(TimeSpan.FromMilliseconds(500));
        core.Refresh();

        Assert.True(core.OnScreenKeyboardOpen);
    }

    [Fact]
    public void Toggle_does_not_open_while_mapping_is_paused()
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        using AppCore core = Core(dir, fake);
        Map(core, ("cross", Bind.Keys(Key.A)));
        core.OnInput(Inputs.Make());
        core.UiMappingPaused = true;

        core.ToggleOnScreenKeyboard();
        Assert.False(core.OnScreenKeyboardOpen);

        core.UiMappingPaused = false;
        core.ToggleOnScreenKeyboard();
        Assert.True(core.OnScreenKeyboardOpen);
    }
}

public sealed class OnScreenKeyboardIntegrationPartsTests
{
    private static Profile ProfileNamed(string name) => new() { Id = Validation.NewId(), Name = name };

    [Fact]
    public void Edge_keyboard_shortcut_resolves_to_no_profile_and_leaves_profile_shortcuts_alone_with_many_profiles()
    {
        Profile[] profiles = Enumerable.Range(0, 6).Select(i => ProfileNamed($"P{i}")).ToArray();
        Assert.Null(EdgeShortcuts.Resolve(EdgeShortcut.OnScreenKeyboard, profiles, profiles[0].Id));
        Assert.Null(EdgeShortcuts.Resolve(EdgeShortcut.OnScreenKeyboard, profiles, "unknown"));
        Assert.Null(EdgeShortcuts.Resolve(EdgeShortcut.OnScreenKeyboard, [], "x"));
        Assert.Same(profiles[0], EdgeShortcuts.Resolve(EdgeShortcut.Profile1, profiles, profiles[3].Id));
        Assert.Same(profiles[3], EdgeShortcuts.Resolve(EdgeShortcut.Profile4, profiles, profiles[0].Id));
        Assert.Null(EdgeShortcuts.Resolve(EdgeShortcut.Profile4, profiles[..3], profiles[0].Id));
        Assert.Same(profiles[0], EdgeShortcuts.Resolve(EdgeShortcut.Next, profiles, profiles[5].Id));
        Assert.Same(profiles[5], EdgeShortcuts.Resolve(EdgeShortcut.Previous, profiles, profiles[0].Id));
    }

    [Theory]
    [InlineData(Buttons.Fn1, Buttons.Triangle, (int)EdgeShortcut.OnScreenKeyboard)]
    [InlineData(Buttons.Fn2, Buttons.Triangle, (int)EdgeShortcut.Profile1)]
    [InlineData(Buttons.Fn1, Buttons.R1, (int)EdgeShortcut.Next)]
    public void Edge_keyboard_shortcut_needs_an_edge_the_fn_key_held_and_a_fresh_triangle(Buttons fn, Buttons action, int expected)
    {
        Assert.Equal((EdgeShortcut)expected, EdgeShortcuts.Read(ControllerModel.DualSenseEdge, fn, fn | action));
        Assert.Null(EdgeShortcuts.Read(ControllerModel.DualSenseEdge, fn | action, fn | action));
        Assert.Null(EdgeShortcuts.Read(ControllerModel.DualSense, fn, fn | action));
    }

    [Fact]
    public void Unicode_records_press_and_release_each_utf16_unit_with_the_signature()
    {
        InputRecord[] records = WindowsInputInjector.BuildTextRecords("ğİ😀");
        Assert.Equal(8, records.Length);
        char[] units = ['ğ', 'İ', '\ud83d', '\ude00'];
        for (int i = 0; i < units.Length; i++)
        {
            KeybdInput down = records[i * 2].U.Keyboard;
            KeybdInput up = records[(i * 2) + 1].U.Keyboard;
            Assert.Equal(1u, records[i * 2].Type);
            Assert.Equal((ushort)0, down.Vk);
            Assert.Equal(units[i], (char)down.Scan);
            Assert.Equal(0x0004u, down.Flags);
            Assert.Equal(0x0006u, up.Flags);
            Assert.Equal(units[i], (char)up.Scan);
            Assert.Equal((nuint)WindowsInputInjector.Signature, down.ExtraInfo);
            Assert.Equal((nuint)WindowsInputInjector.Signature, up.ExtraInfo);
        }

        Assert.Empty(WindowsInputInjector.BuildTextRecords(""));
    }

    [Fact]
    public void Ordered_events_survive_a_failing_subscriber_and_keep_their_order()
    {
        var seen = new List<int>();
        var events = new OrderedEvents<int>
        {
            Handler = i =>
            {
                seen.Add(i);
                if (i == 1)
                {
                    throw new InvalidOperationException("subscriber");
                }
            },
        };
        events.Enqueue(1);
        events.Enqueue(2);
        events.Drain();
        events.Enqueue(3);
        events.Drain();
        events.Drain();

        Assert.Equal([1, 2, 3], seen);
    }

    [Fact]
    public void A_layer_activator_let_go_while_mapping_is_inactive_is_not_withheld_afterwards()
    {
        using var rig = new MapperRig(new MappingConfig
        {
            Layers = [new() { Name = "Shift", Button = "ps", Mode = LayerMode.Hold, Buttons = Bind.Map(("cross", Bind.Key(Key.B))) }],
        });
        rig.Mapper.SuppressButtons(Buttons.PS);
        rig.Press(Buttons.PS);
        Assert.Null(rig.Mapper.ActiveLayerName);
        rig.Mapper.SetActive(false);
        rig.Release(Buttons.PS);
        rig.Mapper.SetActive(true);

        rig.Press(Buttons.PS);

        Assert.Equal("Shift", rig.Mapper.ActiveLayerName);
    }

    [Fact]
    public void A_suppressed_activator_does_not_switch_off_an_open_toggle_layer()
    {
        using var rig = new MapperRig(new MappingConfig
        {
            Layers = [new() { Name = "Slow", Button = "r1", Mode = LayerMode.Toggle, Buttons = Bind.Map(("cross", Bind.Key(Key.C))) }],
        });
        rig.Press(Buttons.R1).Release(Buttons.R1);
        Assert.Equal("Slow", rig.Mapper.ActiveLayerName);

        rig.Mapper.SuppressButtons(Buttons.R1);
        rig.Press(Buttons.R1);

        Assert.Equal("Slow", rig.Mapper.ActiveLayerName);
    }

    [Fact]
    public void Configuring_another_profile_resets_layers_without_announcing_them()
    {
        using var rig = new MapperRig(new MappingConfig
        {
            Layers = [new() { Name = "Fast", Button = "l1", Mode = LayerMode.Hold, Buttons = Bind.Map(("cross", Bind.Key(Key.B))) }],
        });
        rig.Press(Buttons.L1);
        Assert.Equal(["Fast"], rig.Layers);

        rig.Mapper.Configure(Validation.Normalize(new Profile { Id = Validation.NewId(), Name = "Other", Mapping = new() { Enabled = true } }));

        Assert.Null(rig.Mapper.ActiveLayerName);
        Assert.Equal(["Fast"], rig.Layers);
    }

    [Fact]
    public void Layer_notifications_follow_the_topmost_active_layer_including_the_reset_paths()
    {
        var mapping = new MappingConfig
        {
            Enabled = true,
            Layers =
            [
                new() { Name = "Fast", Button = "l1", Mode = LayerMode.Hold, Buttons = Bind.Map(("cross", Bind.Key(Key.B))) },
                new() { Name = "Slow", Button = "r1", Mode = LayerMode.Toggle, Buttons = Bind.Map(("cross", Bind.Key(Key.C))) },
            ],
        };
        using var rig = new MapperRig(mapping);
        Assert.Null(rig.Mapper.ActiveLayerName);

        rig.Press(Buttons.L1);
        Assert.Equal("Fast", rig.Mapper.ActiveLayerName);
        rig.Press(Buttons.R1).Release(Buttons.R1);
        Assert.Equal("Slow", rig.Mapper.ActiveLayerName);
        rig.Release(Buttons.L1);
        Assert.Equal("Slow", rig.Mapper.ActiveLayerName);
        Assert.Equal(["Fast", "Slow", "Slow"], rig.Layers);

        // Reset: the layers drop and that is announced; a second reset announces nothing.
        rig.Mapper.ReleaseAll();
        rig.Advance(1);
        Assert.Null(rig.Mapper.ActiveLayerName);
        Assert.Equal(["Fast", "Slow", "Slow", null], rig.Layers);
        rig.Mapper.ReleaseAll();
        rig.Advance(1);
        Assert.Equal(4, rig.Layers.Count);

        rig.Press(Buttons.R1).Release(Buttons.R1);
        rig.Mapper.ResetController();
        rig.Advance(1);
        Assert.Equal(["Fast", "Slow", "Slow", null, "Slow", null], rig.Layers);
    }

    [Fact]
    public void Special_keyboard_action_posts_a_toggle_to_the_host_and_presses_no_keys()
    {
        using var rig = new MapperRig(Bind.Buttons(("mic", new Binding
        {
            Press = new ActionConfig { Kind = ActionKind.Special, Special = SpecialAction.OnScreenKeyboard },
        })));
        rig.Press(Buttons.Mic).Release(Buttons.Mic);
        Assert.Equal(1, rig.KeyboardToggles);
        Assert.Equal(0, rig.ShowAppCount);
        rig.Run(100);
        Assert.Empty(rig.Take());
    }

    [Fact]
    public void Suppressed_layer_button_cannot_open_its_layer_until_it_is_let_go()
    {
        using var rig = new MapperRig(Bind.Buttons(("cross", Bind.Key(Key.A))) with { LayerButton = "ps" }, confirmPulses: true);
        rig.Mapper.SuppressButtons(Buttons.PS);
        rig.Press(Buttons.PS);
        rig.Advance(1);
        Assert.Empty(rig.Layers);
        Assert.Equal(0, rig.ConfirmPulses);

        rig.Release(Buttons.PS);
        rig.Press(Buttons.PS);
        rig.Advance(1);
        Assert.Single(rig.Layers);
    }

    [Fact]
    public void Suppressed_buttons_do_not_fire_until_they_are_released()
    {
        using var rig = new MapperRig(Bind.Buttons(("circle", Bind.Key(Key.P)), ("cross", Bind.Key(Key.A))));
        rig.Mapper.SuppressButtons(Buttons.Circle);
        rig.Press(Buttons.Circle).Press(Buttons.Cross);
        Assert.Equal(["A down"], rig.Take());
        rig.Release(Buttons.Circle).Release(Buttons.Cross);
        rig.Press(Buttons.Circle).Release(Buttons.Circle);
        Assert.Equal(["A up", "P down", "P up"], rig.Take());
    }
}
