using Tactile.Device.DualSense;

namespace Tactile.Core;

public sealed record ControllerSlot(int Index, DualSenseController Controller)
{
    public string? Mac => Controller.Details.MacAddress;
}

/// <summary>
/// Reopens physical controllers after a disconnect and owns their slot assignment.
/// </summary>
public sealed class ControllerService : IDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(1.5);

    private readonly Lock _lock = new();
    private readonly Lock _scanLock = new();
    private readonly Lock _topologyLock = new();
    private readonly Timer _scan;
    private volatile IReadOnlyList<ControllerSlot> _slots = Array.Empty<ControllerSlot>();
    private DualSenseController? _controller => Controller;
    private readonly Func<IReadOnlyList<ControllerInfo>> _find;
    private readonly Func<ControllerInfo, DualSenseController> _open;
    private readonly Func<ControllerInfo, string?> _readMac;
    private int _maxControllers = 4;
    private string? _preferredMac;
    private (string Path, string? Mac)? _lastPrimary;
    private HapticStreamer? _haptics;
    private long _hapticsStarted;
    private Timer? _hapticStop;
    private volatile HapticBlockSource? _hapticSource;
    private byte _hapticBuffer = 64;
    private SpeakerPacket _speakerPacket = SpeakerPacket.Single36;
    internal SpeakerFrameSource? SpeakerSource { get; set; }
    private volatile HapticBlockSource? _tone;
    private volatile bool _disposed;
    private readonly Func<string, bool>? _isVirtual;
    /// <summary>True while one of our virtual pads is, or may be, attached.</summary>
    internal Func<bool>? VirtualPadMayBeActive { get; set; }
    private volatile OutputState _output = OutputState.Default;

    public ControllerService()
        : this(autoConnect: true)
    {
    }

    /// <param name="autoConnect">False never looks for hardware; outputs are only stored (tests).</param>
    /// <param name="isVirtual">True for HID paths of our own virtual pads, which the scan must skip.</param>
    public ControllerService(bool autoConnect, Func<string, bool>? isVirtual = null)
        : this(DualSenseIds.FindControllers, DualSenseController.Open, DualSenseController.ReadMac, isVirtual)
    {
        if (autoConnect)
        {
            StartScanning();
        }
    }

    internal ControllerService(Func<IReadOnlyList<ControllerInfo>> find, Func<ControllerInfo, DualSenseController> open,
        Func<ControllerInfo, string?> readMac, Func<string, bool>? isVirtual = null)
    {
        _find = find;
        _open = open;
        _readMac = readMac;
        _isVirtual = isVirtual;
        _scan = new Timer(_ => ScanIfIdle());
    }

    internal void StartScanning() => _scan.Change(TimeSpan.Zero, ScanInterval);

    public IReadOnlyList<ControllerSlot> Slots => _slots;

    internal ControllerSlot? PrimarySlot => _slots.FirstOrDefault() is { Index: 0 } slot ? slot : null;

    public DualSenseController? Controller => PrimarySlot?.Controller;

    public OutputState Output => _output;

    public event Action<DualSenseController?>? ConnectionChanged;

    public event Action<InputState>? Input;
    public event Action<ControllerSlot, InputState>? PrimaryInput;

    public event Action<ControllerSlot>? SlotConnected;
    public event Action<int>? SlotDisconnected;
    public event Action<int, InputState>? SlotInput;
    internal event Action<ControllerSlot, InputState>? IdentifiedSlotInput;

    public bool IsCurrent(ControllerSlot slot)
    {
        IReadOnlyList<ControllerSlot> slots = Slots;
        for (int i = 0; i < slots.Count; i++)
        {
            if (ReferenceEquals(slots[i], slot))
            {
                return slot.Controller.Connected;
            }
        }

        return false;
    }

    public bool SendSecondary(int index, OutputState state) =>
        SendSecondary(Slots.FirstOrDefault(s => s.Index == index), state);

    internal bool SendSecondary(ControllerSlot? slot, OutputState state) =>
        slot is { Index: > 0 } && slot.Controller.SendIfCurrent(state, () => IsCurrent(slot));

    public void SetOutput(Func<OutputState, OutputState> change)
    {
        lock (_lock)
        {
            _output = change(_output);
            ControllerSlot? slot = PrimarySlot;
            slot?.Controller.SendIfCurrent(_output, () => IsCurrent(slot));
        }
    }

    internal bool SetHapticSource(HapticBlockSource? source, byte bufferLength, SpeakerPacket speakerPacket)
    {
        lock (_lock)
        {
            _hapticSource = source;
            _hapticBuffer = bufferLength;
            _speakerPacket = speakerPacket;
            _haptics?.BufferLength = bufferLength;
            _haptics?.SpeakerPacket = speakerPacket;
            return ReconcileHapticsLocked();
        }
    }

    /// <summary>Plays a test tone on the haptic actuators (Bluetooth only).</summary>
    public string? PlayHapticTone(float frequencyHz, float amplitude, bool left, bool right, int milliseconds)
    {
        lock (_lock)
        {
            DualSenseController? c = _controller;
            if (c is not { Connected: true })
            {
                return Text.Pick("No controller connected.", "Kontrolcü bağlı değil.");
            }

            if (c.Details.Model == ControllerModel.DualShock4)
            {
                return Text.Pick("This controller has no haptic actuators.", "Bu kontrolcüde haptik motor yok.");
            }

            if (c.Details.Link != Link.Bluetooth)
            {
                return Text.Pick("The haptics test currently requires Bluetooth.", "Haptik test şimdilik yalnız Bluetooth'ta.");
            }

            ClearToneLocked();
            int blocks = (int)Math.Ceiling(milliseconds / HapticReport.Interval.TotalMilliseconds);
            HapticBlockSource tone = HapticStreamer.Tone(frequencyHz, amplitude, left, right, blocks);
            int firstBlock = -1;
            HapticBlockSource playback = (block, samples) =>
            {
                if (firstBlock < 0)
                {
                    firstBlock = block;
                }

                tone(block - firstBlock, samples);
            };
            _tone = playback;
            ReconcileHapticsLocked();
            // Keep streaming silence briefly so the controller buffer drains, then stop.
            _hapticStop = new Timer(_ => FinishTone(playback), null, milliseconds + 300, Timeout.Infinite);
            return null;
        }
    }

    public void StopHaptics()
    {
        lock (_lock)
        {
            ClearToneLocked();
            ReconcileHapticsLocked();
        }
    }

    public void Dispose()
    {
        IReadOnlyList<ControllerSlot> slots;
        lock (_topologyLock)
        {
            _disposed = true;
            slots = Slots;
            _slots = Array.Empty<ControllerSlot>();

            foreach (ControllerSlot slot in slots)
            {
                Unsubscribe(slot.Controller);
            }
        }

        _scan.Dispose();
        // An in-flight scan may still have to close a rejected device.
        lock (_scanLock) { }

        lock (_lock)
        {
            ClearToneLocked();
            _hapticSource = null;
            StopHapticsLocked();
        }

        foreach (ControllerSlot slot in slots)
        {
            slot.Controller.Dispose();
        }
    }

    private void FinishTone(HapticBlockSource playback)
    {
        lock (_lock)
        {
            if (_tone == playback)
            {
                ClearToneLocked();
                ReconcileHapticsLocked();
            }
        }
    }

    private void ClearToneLocked()
    {
        _tone = null;
        _hapticStop?.Dispose();
        _hapticStop = null;
    }

    private bool ReconcileHapticsLocked()
    {
        if (_disposed || _controller is not { Connected: true, Details.Link: Link.Bluetooth } controller ||
            controller.Details.Model == ControllerModel.DualShock4 ||
            (_hapticSource is null && _tone is null))
        {
            StopHapticsLocked();
            return false;
        }

        if (_haptics is not { Running: true })
        {
            // A streamer that stopped on a failed write is retried at most once a second.
            if (_haptics is not null && Environment.TickCount64 - _hapticsStarted < 1000)
            {
                return false;
            }

            StopHapticsLocked();
            _hapticsStarted = Environment.TickCount64;
            _haptics = new HapticStreamer(controller, FillHaptics, FillSpeaker, _hapticBuffer, _speakerPacket);
        }

        return _haptics.Running;
    }

    private void FillHaptics(int block, Span<sbyte> samples)
    {
        HapticBlockSource? source = _hapticSource;
        if (source is null)
        {
            samples.Clear();
        }
        else
        {
            source(block, samples);
        }

        _tone?.Invoke(block, samples);
    }

    private bool FillSpeaker(int block, Span<byte> frame) => SpeakerSource?.Invoke(block, frame) ?? false;

    private void StopHapticsLocked()
    {
        _haptics?.Dispose();
        _haptics = null;
    }

    internal void ScanIfIdle()
    {
        if (!_scanLock.TryEnter())
        {
            return;
        }

        try
        {
            Scan();
        }
        finally
        {
            _scanLock.Exit();
        }
    }

    private void Scan()
    {
        IReadOnlyList<ControllerSlot> slots;
        string? preferredMac;
        (string Path, string? Mac)? lastPrimary;
        int max;
        lock (_topologyLock)
        {
            slots = Slots;
            max = _maxControllers;
            preferredMac = SameMac(PrimarySlot?.Mac, _preferredMac) ? null : _preferredMac;
            if (_disposed || (slots.Count >= max && preferredMac is null))
            {
                return;
            }

            lastPrimary = Controller is null ? _lastPrimary : null;
        }

        var candidates = _find()
            .Where(c => !slots.Any(s => SamePath(s.Controller.DevicePath, c.Hid.Path)) && !IsVirtual(c.Hid.Path))
            .DistinctBy(c => c.Hid.Path, StringComparer.OrdinalIgnoreCase)
            // USB paths need not contain the MAC. Probe pairing without starting a reader.
            .Select(c => (Info: c, Mac: preferredMac is not null || (max > 1 && lastPrimary?.Mac is not null)
                ? TryReadMac(c) : null))
            .OrderByDescending(c => SameMac(c.Mac, preferredMac))
            .ToArray();

        foreach ((ControllerInfo info, string? mac) in candidates)
        {
            lock (_topologyLock)
            {
                if (_disposed)
                {
                    return;
                }

                if (Slots.Count >= _maxControllers && !ShouldPromote(mac))
                {
                    continue;
                }

                if (Controller is null && Slots.Count == _maxControllers - 1 && !CanFillPrimary(info.Hid.Path, mac))
                {
                    continue;
                }
            }

            DualSenseController controller;
            try
            {
                controller = _open(info);
            }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
            {
                continue;
            }

            if (!Accept(controller))
            {
                controller.Dispose();
            }
        }
    }

    private string? TryReadMac(ControllerInfo info)
    {
        try
        {
            return _readMac(info);
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    // With no other controller connected, any controller becomes the primary: a single-controller
    // user who switches pads must keep mapping, haptics and the rest.
    private bool CanFillPrimary(string path, string? mac) =>
        _maxControllers == 1 || _lastPrimary is null || Slots.Count == 0 || SameMac(mac, _preferredMac) ||
        SameMac(mac, _lastPrimary.Value.Mac) || SamePath(path, _lastPrimary.Value.Path);

    private bool ShouldPromote(string? mac) =>
        PrimarySlot is { } primary && SameMac(mac, _preferredMac) && !SameMac(primary.Mac, _preferredMac);

    private bool Accept(DualSenseController controller)
    {
        IReadOnlyList<ControllerSlot> before, after;
        ControllerSlot? retired = null;
        lock (_topologyLock)
        {
            bool promote = ShouldPromote(controller.Details.MacAddress);
            if (_disposed || (Slots.Count >= _maxControllers && !promote) || !controller.Connected)
            {
                return false;
            }

            bool primary = promote || (Controller is null && CanFillPrimary(controller.DevicePath, controller.Details.MacAddress));
            // Leave room for the missing primary even when every secondary slot is in use.
            int index = primary ? 0 : Enumerable.Range(1, 3)
                .FirstOrDefault(i => Slots.All(s => s.Index != i));
            if (!primary && (index == 0 || Slots.Count(s => s.Index > 0) >= _maxControllers - 1))
            {
                return false;
            }

            before = Slots;
            IEnumerable<ControllerSlot> next = before;
            if (promote && PrimarySlot is { } fallback)
            {
                next = next.Where(s => !ReferenceEquals(s, fallback));
                if (before.Count < _maxControllers)
                {
                    int free = Enumerable.Range(1, 3).First(i => before.All(s => s.Index != i));
                    next = next.Append(fallback with { Index = free });
                }
                else
                {
                    retired = fallback;
                    Unsubscribe(fallback.Controller);
                }
            }

            controller.InputReceived += OnInput;
            controller.Disconnected += OnDisconnected;
            after = ReplaceSlots(next.Append(new ControllerSlot(index, controller)));
        }

        try
        {
            DispatchTopology(before, after);
            if (!controller.Connected)
            {
                OnDisconnected(controller);
            }
        }
        finally
        {
            retired?.Controller.Dispose();
        }

        return true;
    }

    internal void Configure(AppSettings settings)
    {
        IReadOnlyList<ControllerSlot> before, after;
        ControllerSlot[] removed;
        lock (_topologyLock)
        {
            if (_disposed)
            {
                return;
            }

            bool changed = _preferredMac != settings.PrimaryController;
            before = Slots;
            _preferredMac = settings.PrimaryController;
            _maxControllers = settings.MaxControllers;
            ControllerSlot[] next = (changed ? SwapPrimary(_preferredMac) : before).ToArray();
            removed = next.Where(s => s.Index > 0).OrderBy(s => s.Index).Skip(_maxControllers - 1).ToArray();
            after = ReplaceSlots(next.Except(removed));
            foreach (ControllerSlot slot in removed)
            {
                Unsubscribe(slot.Controller);
            }
        }

        try
        {
            DispatchTopology(before, after);
        }
        finally
        {
            foreach (ControllerSlot slot in removed)
            {
                slot.Controller.Dispose();
            }
        }
    }

    internal void SelectPrimary(string mac)
    {
        IReadOnlyList<ControllerSlot> before, after;
        lock (_topologyLock)
        {
            before = Slots;
            after = ReplaceSlots(SwapPrimary(mac));
        }

        DispatchTopology(before, after);
    }

    private IEnumerable<ControllerSlot> SwapPrimary(string? mac)
    {
        ControllerSlot? selected = Slots.FirstOrDefault(s => s.Index > 0 && SameMac(s.Mac, mac));
        if (selected is null)
        {
            return Slots;
        }

        ControllerSlot? old = Slots.FirstOrDefault(s => s.Index == 0);
        ControllerSlot primary = selected with { Index = 0 };
        ControllerSlot? secondary = old is null ? null : old with { Index = selected.Index };
        IEnumerable<ControllerSlot> slots = Slots.Where(s => s != selected && s != old).Append(primary);
        return secondary is null ? slots : slots.Append(secondary);
    }

    private IReadOnlyList<ControllerSlot> ReplaceSlots(IEnumerable<ControllerSlot> slots)
    {
        var next = Array.AsReadOnly(slots.OrderBy(s => s.Index).ToArray());
        if (next.FirstOrDefault() is { Index: 0 } primary)
        {
            _lastPrimary = (primary.Controller.DevicePath, primary.Mac);
        }

        return _slots = next;
    }

    private void DispatchTopology(IReadOnlyList<ControllerSlot> before, IReadOnlyList<ControllerSlot> after)
    {
        ControllerSlot? primary = after.FirstOrDefault(s => s.Index == 0);
        if (!ReferenceEquals(before.FirstOrDefault(s => s.Index == 0), primary) &&
            !_disposed && ReferenceEquals(PrimarySlot, primary))
        {
            PrimaryChanged(primary);
        }

        foreach (ControllerSlot slot in before.Except<ControllerSlot>(after, ReferenceEqualityComparer.Instance))
        {
            if (!_disposed && !IsCurrent(slot))
            {
                SlotDisconnected?.Invoke(slot.Index);
            }
        }

        foreach (ControllerSlot slot in after.Except<ControllerSlot>(before, ReferenceEqualityComparer.Instance))
        {
            if (IsCurrent(slot))
            {
                SlotConnected?.Invoke(slot);
            }
        }
    }

    private void PrimaryChanged(ControllerSlot? slot)
    {
        lock (_lock)
        {
            if (_disposed || !ReferenceEquals(PrimarySlot, slot))
            {
                return;
            }

            ClearToneLocked();
            StopHapticsLocked();
        }

        if (ReferenceEquals(PrimarySlot, slot))
        {
            ConnectionChanged?.Invoke(slot?.Controller);
        }

        lock (_lock)
        {
            if (!_disposed && ReferenceEquals(PrimarySlot, slot))
            {
                slot?.Controller.SendIfCurrent(_output, () => IsCurrent(slot));
                ReconcileHapticsLocked();
            }
        }
    }

    private static bool SameMac(string? first, string? second) =>
        first is not null && string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    internal bool IsVirtual(string path)
    {
        if (_isVirtual is null)
        {
            return false;
        }

        try
        {
            return _isVirtual(path);
        }
        catch (Exception)
        {
            // A failed query says nothing: with a virtual pad around, skip the candidate and retry next scan.
            try
            {
                return VirtualPadMayBeActive?.Invoke() == true;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    /// <summary>Like <see cref="SetOutput"/>, but true only when the controller write itself went out.</summary>
    internal bool SetOutputAndSend(OutputState state, ControllerSlot? slot)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(slot, PrimarySlot))
            {
                return false;
            }

            _output = state;
            return slot?.Controller.SendIfCurrent(state, () => IsCurrent(slot)) ?? false;
        }
    }

    private void OnInput(DualSenseController controller, InputState state)
    {
        if (Slots.FirstOrDefault(s => s.Controller == controller) is not { } slot || !IsCurrent(slot))
        {
            return;
        }

        if (slot.Index == 0)
        {
            PrimaryInput?.Invoke(slot, state);
            if (IsCurrent(slot))
            {
                Input?.Invoke(state);
            }
        }

        if (IsCurrent(slot))
        {
            IdentifiedSlotInput?.Invoke(slot, state);
        }

        if (IsCurrent(slot))
        {
            SlotInput?.Invoke(slot.Index, state);
        }
    }

    private void OnDisconnected(DualSenseController c)
    {
        IReadOnlyList<ControllerSlot> before, after;
        lock (_topologyLock)
        {
            if (Slots.FirstOrDefault(s => s.Controller == c) is not { } slot)
            {
                return;
            }

            Unsubscribe(c);
            before = Slots;
            after = ReplaceSlots(before.Where(s => !ReferenceEquals(s, slot)));
        }

        try
        {
            DispatchTopology(before, after);
        }
        finally
        {
            c.Dispose();
        }
    }

    private void Unsubscribe(DualSenseController controller)
    {
        controller.InputReceived -= OnInput;
        controller.Disconnected -= OnDisconnected;
    }
}
