using Tactile.Core.Logging;
using Tactile.Device.DualSense;
using Tactile.Input;

namespace Tactile.Core;

/// <summary>What the keyboard view draws. <see cref="Grid"/> already holds the current letter case.</summary>
public sealed record OnScreenKeyboardState(bool Visible, Language Language, bool Uppercase, int Row, int Column,
    IReadOnlyList<IReadOnlyList<string>> Grid, string CurrentKey);

/// <summary>
/// The controller-driven keyboard as a pure state machine: input in, key output through the injector,
/// state out. No window here. Locked; input comes from the HID thread. Nothing allocates and no lock is
/// taken per frame while it is closed.
/// </summary>
internal sealed class OnScreenKeyboard
{
    public static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(180);
    public static readonly TimeSpan BackspaceDelay = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan BackspaceInterval = TimeSpan.FromMilliseconds(60);

    private const float StickThreshold = 0.5f;
    private const Buttons NavButtons = Buttons.Up | Buttons.Down | Buttons.Left | Buttons.Right;

    /// <summary>Buttons and the left stick the keyboard owns while it is open.</summary>
    public const Buttons UsedButtons = NavButtons | Buttons.Cross | Buttons.Circle | Buttons.Triangle |
        Buttons.Square | Buttons.Options | Buttons.PS;

    private static readonly IReadOnlyList<IReadOnlyList<string>> EnLower = Rows(
        "1 2 3 4 5 6 7 8 9 0 - =", "q w e r t y u i o p [ ]", "a s d f g h j k l ; ' /", "z x c v b n m , . @");

    private static readonly IReadOnlyList<IReadOnlyList<string>> EnUpper = Rows(
        "! @ # $ % ^ & * ( ) _ +", "Q W E R T Y U I O P { }", "A S D F G H J K L : \" ?", "Z X C V B N M < > _");

    private static readonly IReadOnlyList<IReadOnlyList<string>> TrLower = Rows(
        "1 2 3 4 5 6 7 8 9 0 * -", "q w e r t y u ı o p ğ ü", "a s d f g h j k l ş i ,", "z x c v b n m ö ç .");

    private static readonly IReadOnlyList<IReadOnlyList<string>> TrUpper = Rows(
        "! ' ^ + % & / ( ) = ? _", "Q W E R T Y U I O P Ğ Ü", "A S D F G H J K L Ş İ ;", "Z X C V B N M Ö Ç :");

    private readonly Lock _gate = new();
    private readonly IInputInjector _injector;
    private volatile bool _open;
    private Language _language = Language.En;
    private bool _uppercase;
    private int _row = 1;
    private int _column;
    private Buttons _prev;
    private int _stickDirection;
    private TimeSpan _nextStickMove;
    private TimeSpan? _nextBackspace;
    private OnScreenKeyboardState _published;
    private readonly OrderedEvents<OnScreenKeyboardState> _events = new();

    public OnScreenKeyboard(IInputInjector injector)
    {
        _injector = injector;
        _published = Snapshot(visible: false);
        _events.Handler = state => Changed?.Invoke(state);
    }

    public bool IsOpen => _open;

    /// <summary>Raised after a visible change (shown, hidden, moved, case or language). Not raised under the lock.</summary>
    public event Action<OnScreenKeyboardState>? Changed;

    public OnScreenKeyboardState State
    {
        get
        {
            lock (_gate)
            {
                return Snapshot(_open);
            }
        }
    }

    /// <summary>The grid for a language and letter case.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> GridFor(Language language, bool uppercase) => language == Language.Tr
        ? (uppercase ? TrUpper : TrLower)
        : (uppercase ? EnUpper : EnLower);

    /// <summary>
    /// Opens at row 1, column 0, lower case. <paramref name="current"/> is what is held now, so a
    /// button still down from the opening press does not count as a key press here.
    /// </summary>
    public void Open(Language language, InputState? current = null)
    {
        lock (_gate)
        {
            if (_open)
            {
                return;
            }

            _language = language;
            _uppercase = false;
            _row = 1;
            _column = 0;
            _prev = current?.Buttons ?? Buttons.None;
            _stickDirection = current is null ? 0 : StickDirection(current.Left);
            _nextStickMove = TimeSpan.MaxValue;
            _nextBackspace = null;
            _open = true;
            Stage();
        }

        _events.Drain();
    }

    public void Close()
    {
        lock (_gate)
        {
            if (!_open)
            {
                return;
            }

            _open = false;
            Stage();
        }

        _events.Drain();
    }

    /// <summary>Rebuilds the grid for another language while open; the cell stays inside the new rows.</summary>
    public void SetLanguage(Language language)
    {
        lock (_gate)
        {
            if (!_open || language == _language)
            {
                return;
            }

            _language = language;
            ClampCell();
            Stage();
        }

        _events.Drain();
    }

    /// <summary>Feeds one report. Does nothing, without locking, while closed. PS closes it.</summary>
    public void HandleInput(InputState s, TimeSpan now)
    {
        if (!_open)
        {
            return;
        }

        lock (_gate)
        {
            if (!_open)
            {
                return;
            }

            Buttons pressed = s.Buttons & ~_prev;
            Buttons held = s.Buttons;
            _prev = held;
            if ((pressed & Buttons.PS) != 0)
            {
                _open = false;
            }
            else
            {
                Navigate(pressed, s.Left, now);
                Act(pressed, held, now);
            }

            Stage();
        }

        _events.Drain();
    }

    private void Navigate(Buttons pressed, Stick left, TimeSpan now)
    {
        if ((pressed & Buttons.Up) != 0) Move(-1, 0);
        if ((pressed & Buttons.Down) != 0) Move(1, 0);
        if ((pressed & Buttons.Left) != 0) Move(0, -1);
        if ((pressed & Buttons.Right) != 0) Move(0, 1);

        int direction = StickDirection(left);
        if (direction != _stickDirection)
        {
            _stickDirection = direction;
            if (direction != 0)
            {
                MoveStick(direction);
                _nextStickMove = now + RepeatInterval;
            }
        }
        else if (direction != 0 && now >= _nextStickMove)
        {
            MoveStick(direction);
            _nextStickMove = now + RepeatInterval;
        }
    }

    private void Act(Buttons pressed, Buttons held, TimeSpan now)
    {
        if ((pressed & Buttons.Cross) != 0)
        {
            _injector.Text(CurrentKey());
        }

        if ((pressed & Buttons.Circle) != 0)
        {
            Tap(Key.Backspace);
            _nextBackspace = now + BackspaceDelay;
        }
        else if ((held & Buttons.Circle) == 0)
        {
            _nextBackspace = null;
        }
        else if (_nextBackspace is { } due && now >= due)
        {
            Tap(Key.Backspace);
            _nextBackspace = now + BackspaceInterval;
        }

        if ((pressed & Buttons.Triangle) != 0)
        {
            Tap(Key.Space);
        }

        if ((pressed & Buttons.Square) != 0)
        {
            _uppercase = !_uppercase;
        }

        if ((pressed & Buttons.Options) != 0)
        {
            Tap(Key.Enter);
        }
    }

    // Down and up in one call: nothing can stay held.
    private void Tap(Key key)
    {
        _injector.Key(key, true);
        _injector.Key(key, false);
    }

    private void MoveStick(int direction)
    {
        switch (direction)
        {
            case 1: Move(-1, 0); break;
            case 2: Move(1, 0); break;
            case 3: Move(0, -1); break;
            default: Move(0, 1); break;
        }
    }

    private void Move(int dRow, int dColumn)
    {
        IReadOnlyList<IReadOnlyList<string>> grid = GridFor(_language, _uppercase);
        _row = (_row + dRow + grid.Count) % grid.Count;
        int length = grid[_row].Count;
        _column = dColumn == 0 ? Math.Min(_column, length - 1) : (_column + dColumn + length) % length;
    }

    private void ClampCell()
    {
        IReadOnlyList<IReadOnlyList<string>> grid = GridFor(_language, _uppercase);
        _row = Math.Min(_row, grid.Count - 1);
        _column = Math.Min(_column, grid[_row].Count - 1);
    }

    /// <summary>0 none, 1 up, 2 down, 3 left, 4 right; the stronger axis wins.</summary>
    private static int StickDirection(Stick stick)
    {
        (float x, float y) = stick.Normalized;
        if (Math.Abs(x) >= StickThreshold && Math.Abs(x) >= Math.Abs(y))
        {
            return x < 0 ? 3 : 4;
        }

        if (Math.Abs(y) >= StickThreshold)
        {
            return y > 0 ? 1 : 2;
        }

        return 0;
    }

    private string CurrentKey() => GridFor(_language, _uppercase)[_row][_column];

    private OnScreenKeyboardState Snapshot(bool visible) =>
        new(visible, _language, _uppercase, _row, _column, GridFor(_language, _uppercase), CurrentKey());

    // Under the gate: queues a state only when the view changed, so an idle frame allocates nothing.
    private void Stage()
    {
        if (_published.Visible == _open && _published.Language == _language && _published.Uppercase == _uppercase &&
            _published.Row == _row && _published.Column == _column)
        {
            return;
        }

        OnScreenKeyboardState state = Snapshot(_open);
        _published = state;
        _events.Enqueue(state);
    }

    private static IReadOnlyList<IReadOnlyList<string>> Rows(params string[] rows) =>
        rows.Select(r => (IReadOnlyList<string>)r.Split(' ')).ToArray();
}

/// <summary>Events delivered one at a time in the order they were queued, by whoever holds the drain turn.</summary>
internal sealed class OrderedEvents<T>
{
    private readonly Lock _gate = new();
    private readonly Queue<T> _queue = new();
    private bool _draining;
    private int _pending;

    public Action<T>? Handler { get; set; }

    /// <summary>Who owns these events; named in the log when a subscriber fails.</summary>
    public string Name { get; init; } = "On-screen keyboard";

    public void Enqueue(T item)
    {
        lock (_gate)
        {
            _queue.Enqueue(item);
            Volatile.Write(ref _pending, 1);
        }
    }

    /// <summary>Call outside any caller lock. A failing subscriber is logged; later events still go out.</summary>
    public void Drain()
    {
        if (Volatile.Read(ref _pending) == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_draining)
            {
                return;
            }

            _draining = true;
        }

        while (true)
        {
            T item;
            lock (_gate)
            {
                if (!_queue.TryDequeue(out item!))
                {
                    Volatile.Write(ref _pending, 0);
                    _draining = false;
                    return;
                }
            }

            try
            {
                Handler?.Invoke(item);
            }
            catch (Exception e)
            {
                Log.Error($"{Name} subscriber failed.", e);
            }
        }
    }
}
