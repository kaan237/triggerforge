using Tactile.Device.DualSense;
using Tactile.Emulation;

namespace Tactile.Core;

internal sealed record SecondaryEmulationConnection(ControllerService Controllers, SecondaryControllers Pipeline, ControllerSlot Slot)
{
    public bool IsCurrent
    {
        get
        {
            IReadOnlyList<ControllerSlot> slots = Controllers.Slots;
            for (int i = 0; i < slots.Count; i++)
            {
                if (ReferenceEquals(slots[i], Slot))
                    return true;
            }

            return false;
        }
    }

    public void SetFeedback(OutputState? state, OutputFields fields) => Pipeline.SetGameFeedback(Slot, state, fields);
}

public sealed partial class EmulationService
{
    private readonly EmulationService?[] _secondarySlots = new EmulationService?[3];
    private readonly Lock _bindingLock = new();
    private volatile ControllerService? _controllers;
    private volatile SecondarySource? _secondarySource;
    private volatile PadRoute? _route;
    private volatile bool _retryHide;
    private Profile? _secondaryProfile;

    internal void BindSecondary(ControllerService controllers, SecondaryControllers? pipeline)
    {
        lock (_bindingLock)
        {
            _controllers = controllers;
            if (_disposed || ReferenceEquals(_secondarySource?.Pipeline, pipeline))
            {
                return;
            }

            UnbindSecondary();
            if (pipeline is not null)
            {
                _secondarySource = new(controllers, pipeline);
                pipeline.Connected += OnSecondaryProfile;
                pipeline.ProfileChanged += OnSecondaryProfile;
                pipeline.Disconnected += OnSecondaryDisconnected;
                pipeline.Input += OnSecondaryInput;
            }
        }

        WakeSecondaries();
    }

    private void UnbindSecondary()
    {
        lock (_bindingLock)
        {
            if (_secondarySource is { } source)
            {
                _secondarySource = null;
                source.Pipeline.Connected -= OnSecondaryProfile;
                source.Pipeline.ProfileChanged -= OnSecondaryProfile;
                source.Pipeline.Disconnected -= OnSecondaryDisconnected;
                source.Pipeline.Input -= OnSecondaryInput;
            }
        }
    }

    private void OnSecondaryProfile(ControllerSlot slot, Profile profile) => WakeSecondaries();

    private void OnSecondaryDisconnected(ControllerSlot slot) => WakeSecondaries();

    private void WakeSecondaries()
    {
        if (!_disposed)
        {
            // The worker reads the latest slot snapshot, even when concurrent updates emit events out of order.
            _wake?.Set();
        }
    }

    private void OnSecondaryInput(ControllerSlot slot, InputState input) =>
        Volatile.Read(ref _secondarySlots[slot.Index - 1])?.OnInput(input, slot);

    private IEnumerable<EmulationService> SecondarySlots()
    {
        for (int i = 0; i < _secondarySlots.Length; i++)
        {
            if (Volatile.Read(ref _secondarySlots[i]) is { } slot)
            {
                yield return slot;
            }
        }
    }

    private bool UpdateSecondaries(Wanted? primary)
    {
        SecondarySource? source = _secondarySource;
        IReadOnlyList<ControllerSlot> slots = source?.Controllers.Slots ?? [];
        if (source is not null && slots.FirstOrDefault(s => s.Index == 0)?.Controller.DevicePath != primary?.Controller?.Path)
        {
            // The primary tick has yet to publish its side of a connection change.
            return false;
        }

        EmulationSettings settings = (primary?.Settings ?? new()) with { Audio = false, KeepAudioDefaults = false };
        // Secondary profiles on Auto follow the same game as the primary.
        string? exe = _autoExe;
        for (int index = 1; index <= _secondarySlots.Length; index++)
        {
            EmulationService? service = _secondarySlots[index - 1];
            ControllerSlot? slot = slots.FirstOrDefault(s => s.Index == index && s.Controller.Connected);
            Profile? profile = slot is null ? null : source?.Pipeline.GetProfile(slot);
            RealController? real = null;
            if (source is not null && slot is not null && profile is not null)
            {
                if (service is null)
                {
                    service = new EmulationService(_platform, _server, _host, _clock, threaded: false, slotIndex: index);
                    Volatile.Write(ref _secondarySlots[index - 1], service);
                }

                real = service._wanted?.Controller;
                if (!ReferenceEquals(real?.Secondary?.Slot, slot) || real?.Secondary?.Pipeline != source.Pipeline)
                {
                    var features = slot.Controller.Features;
                    DualSenseIdentity? identity = features is { Calibration: { } cal, Pairing: { } pair, Firmware: { } fw }
                        ? new(cal, pair, fw) : null;
                    real = new RealController(slot.Controller.DevicePath, slot.Controller.Details.Link, identity,
                        new SecondaryEmulationConnection(source.Controllers, source.Pipeline, slot), slot.Controller.Details.Model);
                }
            }

            if (service is not null)
            {
                lock (service._stateLock)
                {
                    service._drivers = _drivers;
                }

                if (!ReferenceEquals(profile, service._secondaryProfile) || real != service._wanted?.Controller ||
                    settings != service._wanted?.Settings || exe != service._autoExe)
                {
                    service._secondaryProfile = profile;
                    service.UpdateWanted(profile, settings, real, exe, knownGame: true);
                }
            }
        }

        return true;
    }

    private bool StopAllLocked()
    {
        bool clean = StopLocked();
        foreach (EmulationService slot in SecondarySlots())
        {
            clean &= slot.StopLocked();
        }
        return clean;
    }

    private IReadOnlyList<EmulationSlotStatus> BuildSlots(EmulationSlotStatus primary)
    {
        List<EmulationSlotStatus> slots = [primary];
        foreach (EmulationService slot in SecondarySlots())
        {
            lock (slot._stateLock)
            {
                bool attached = slot._pad is not null;
                bool hidden = slot._hiddenId is not null;
                if (slot._wanted?.Controller is null && !attached && !hidden)
                {
                    continue;
                }

                EmulationMode mode = attached ? ModeOf(slot._kind) : EmulationMode.Off;
                string? error = slot._error is { } failure ? Text.Pick(failure.En, failure.Tr) : slot._hideError?.Value;
                slots.Add(new(slot._slotIndex, mode, attached, hidden, error));
            }
        }

        return slots;
    }

    private sealed record SecondarySource(ControllerService Controllers, SecondaryControllers Pipeline);
    private sealed record PadRoute(IVirtualPad Pad, SecondaryEmulationConnection? Secondary, Func<bool>? PrimaryCurrent)
    {
        public bool IsCurrent => PrimaryCurrent?.Invoke() ?? Secondary?.IsCurrent ?? true;
    }

    private sealed class SecondaryAxes(Profile profile)
    {
        private readonly StickProcessor _left = new(profile.Sticks.Left);
        private readonly StickProcessor _right = new(profile.Sticks.Right);
        private readonly TriggerResponseProcessor _l2 = new(profile.TriggerResponse.Left);
        private readonly TriggerResponseProcessor _r2 = new(profile.TriggerResponse.Right);

        public InputState Apply(InputState input) => input with
        {
            Left = Shape(_left, input.Left), Right = Shape(_right, input.Right),
            L2 = _l2.Process(input.L2), R2 = _r2.Process(input.R2),
        };

        private static Stick Shape(StickProcessor processor, Stick stick)
        {
            (float x, float y) = processor.Process(stick);
            return new((byte)Math.Round((1 + x) * 127.5), (byte)Math.Round((1 - y) * 127.5));
        }
    }
}
