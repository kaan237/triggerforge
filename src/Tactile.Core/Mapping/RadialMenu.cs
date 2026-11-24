using Tactile.Device.DualSense;

namespace Tactile.Core;

/// <summary>What the radial menu view draws. Not visible = nothing to draw.</summary>
public sealed record RadialMenuState(bool Visible, string MenuId, string Name, RadialStick Stick,
    IReadOnlyList<string> Labels, int? SelectedIndex, IReadOnlyList<bool> Assigned);

/// <summary>
/// The radial menu as a state machine: open from a held button, select by stick angle, commit or
/// cancel. Locked; the mapper and the input thread both call it. State changes are queued and
/// raised by <see cref="Drain"/>, which the owner calls with no lock held.
/// </summary>
internal sealed class RadialMenuController
{
    /// <summary>Tilt at which a slice is picked.</summary>
    public const double EnterRadius = 0.35;

    /// <summary>Below this tilt the selection is cleared (centre = cancel).</summary>
    public const double ExitRadius = 0.25;

    private readonly Lock _gate = new();
    private readonly OrderedEvents<RadialMenuState> _events = new() { Name = "Radial menu" };
    private Dictionary<string, RadialMenu> _menus = new(StringComparer.Ordinal);
    private RadialMenu? _menu;
    private int _session;
    private int? _selected;
    private Buttons _owner;
    // Bit 1 = left, 2 = right: sticks of closed menus that must be centred once. Independent per stick.
    private int _latched;
    private int _cancelledButtons;
    private RadialMenuState _published = Closed;
    // Bit 1 = left, 2 = right: the sticks that must read neutral (the open menu's and every latched one). Read without the lock every frame.
    private volatile int _neutral;
    private volatile bool _open;

    private static readonly RadialMenuState Closed = new(false, "", "", RadialStick.Left, [], null, []);

    public RadialMenuController() => _events.Handler = state => Changed?.Invoke(state);

    /// <summary>Raised from <see cref="Drain"/> when visibility or selection changed. Never under the lock.</summary>
    public event Action<RadialMenuState>? Changed;

    public bool IsOpen => _open;

    /// <summary>Button the next <see cref="TryOpen"/> belongs to; set by the mapper just before it presses.</summary>
    public Buttons PendingOwner { get; set; }

    public RadialMenuState State
    {
        get
        {
            lock (_gate)
            {
                return _menu is { } menu && _open ? Snapshot(menu) : Closed;
            }
        }
    }

    /// <summary>The left stick must read neutral: a menu is open on it, or a closed one's latch is not centred yet.</summary>
    public bool LeftNeutral => (_neutral & 1) != 0;

    /// <summary>The right stick must read neutral.</summary>
    public bool RightNeutral => (_neutral & 2) != 0;

    public void Configure(IReadOnlyList<RadialMenu>? menus)
    {
        lock (_gate)
        {
            _menus = (menus ?? []).Where(m => m is { Slices.Count: >= 2 } && !string.IsNullOrEmpty(m.Id))
                .GroupBy(m => m.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        }
    }

    /// <summary>Opens a menu. Returns its session (never 0), or 0 when the id is unknown or a menu is already open.</summary>
    public int TryOpen(string? id)
    {
        lock (_gate)
        {
            if (_open || id is null || !_menus.TryGetValue(id, out RadialMenu? menu))
            {
                return 0;
            }

            _menu = menu;
            _selected = null;
            _owner = PendingOwner;
            _latched &= ~Bit(menu.Stick);
            _open = true;
            _session = _session == int.MaxValue ? 1 : _session + 1;
            SetNeutral();
            Stage();
            return _session;
        }
    }

    /// <summary>
    /// Closes the menu for the release of the button that opened it. Null when that session is not the open
    /// one any more (cancelled) or nothing is selected; otherwise the slice action to tap.
    /// </summary>
    public ActionConfig? TryCommit(int session)
    {
        lock (_gate)
        {
            if (!_open || session != _session)
            {
                return null;
            }

            ActionConfig? action = _selected is { } i && _menu!.Slices[i].Action is { Kind: not ActionKind.None } a ? a : null;
            Close();
            return action;
        }
    }

    /// <summary>Closes without firing anything. The button that opened it is reported by <see cref="TakeCancelledButtons"/>.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            if (!_open)
            {
                return;
            }

            Interlocked.Or(ref _cancelledButtons, (int)_owner);
            Close();
        }
    }

    /// <summary>Disconnect or dispose: closes and forgets every pending stick and button state.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (_open)
            {
                Close();
            }

            _latched = 0;
            Volatile.Write(ref _cancelledButtons, 0);
            SetNeutral();
        }
    }

    /// <summary>Buttons whose press was cancelled since the last call; the input thread withholds them until let go.</summary>
    public Buttons TakeCancelledButtons() => (Buttons)Interlocked.Exchange(ref _cancelledButtons, 0);

    /// <summary>Feeds the raw sticks of one report. No lock while no menu is open and no stick is held back.</summary>
    public void Update(Stick left, Stick right)
    {
        if (_neutral == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_open)
            {
                RadialMenu menu = _menu!;
                (float x, float y) = (menu.Stick == RadialStick.Left ? left : right).Normalized;
                int? next = Select(x, y, menu.Slices.Count, _selected);
                if (next != _selected)
                {
                    _selected = next;
                    Stage();
                }
            }

            if (_latched != 0)
            {
                if ((_latched & 1) != 0 && Centred(left))
                {
                    _latched &= ~1;
                }

                if ((_latched & 2) != 0 && Centred(right))
                {
                    _latched &= ~2;
                }

                SetNeutral();
            }
        }
    }

    private static bool Centred(Stick stick)
    {
        (float x, float y) = stick.Normalized;
        return Math.Sqrt((x * x) + (y * y)) < ExitRadius;
    }

    private static int Bit(RadialStick stick) => stick == RadialStick.Left ? 1 : 2;

    /// <summary>Raises the queued states in order. Call with no lock held.</summary>
    public void Drain() => _events.Drain();

    /// <summary>
    /// The slice under a stick position (x right, y up, -1..1) for <paramref name="count"/> slices: slice 0
    /// is centred at 12 o'clock, clockwise. Null below the dead zone.
    /// </summary>
    public static int? Select(float x, float y, int count, int? current)
    {
        double radius = Math.Sqrt((x * x) + (y * y));
        if (radius < (current is null ? EnterRadius : ExitRadius))
        {
            return null;
        }

        double angle = Math.Atan2(x, y);
        if (angle < 0)
        {
            angle += 2 * Math.PI;
        }

        double width = 2 * Math.PI / count;
        double shifted = (angle + (width / 2)) % (2 * Math.PI);
        return Math.Min(count - 1, (int)(shifted / width));
    }

    // Under the gate.
    private void Close()
    {
        _open = false;
        _latched |= Bit(_menu!.Stick);
        _selected = null;
        SetNeutral();
        Stage();
    }

    private void SetNeutral()
    {
        _neutral = _latched | (_open ? Bit(_menu!.Stick) : 0);
    }

    private RadialMenuState Snapshot(RadialMenu menu) =>
        new(true, menu.Id, menu.Name, menu.Stick, [.. menu.Slices.Select(s => s.Label)], _selected,
        [.. menu.Slices.Select(s => s.Action is { Kind: not ActionKind.None })]);

    // Under the gate: queues a state only when what the view shows changed.
    private void Stage()
    {
        RadialMenuState state = _open && _menu is { } menu ? Snapshot(menu) : Closed;
        if (state.Visible == _published.Visible && state.MenuId == _published.MenuId && state.SelectedIndex == _published.SelectedIndex)
        {
            return;
        }

        _published = state;
        _events.Enqueue(state);
    }
}
