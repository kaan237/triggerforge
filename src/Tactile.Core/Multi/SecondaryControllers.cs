using Tactile.Core.Integration;
using Tactile.Device.DualSense;

namespace Tactile.Core;

/// <summary>HID output and session profiles for the secondary controller slots.</summary>
public sealed class SecondaryControllers : IDisposable
{
    private static readonly TimeSpan SendInterval = TimeSpan.FromMilliseconds(10);
    private readonly Lock _lock = new();
    private readonly ControllerService _controllers;
    private readonly ProfileStore _profiles;
    private readonly SettingsStore _settings;
    private readonly Action _refresh;
    private readonly Action<Notice> _report;
    private readonly Func<TimeSpan> _now;
    private readonly Func<Profile> _primary;
    private readonly Dictionary<int, Entry> _entries = [];
    private readonly Dictionary<string, string> _sessionProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<ControllerSlot, UdpState> _udp;
    private string? _fallbackId;
    private bool _disposed;

    internal SecondaryControllers(ControllerService controllers, ProfileStore profiles, SettingsStore settings,
        Func<ControllerSlot, UdpState> udp, Func<TimeSpan> now, Func<Profile> primary, Action refresh, Action<Notice> report)
    {
        _controllers = controllers;
        _profiles = profiles;
        _settings = settings;
        _udp = udp;
        _now = now;
        _primary = primary;
        _refresh = refresh;
        _report = report;
        controllers.SlotConnected += OnConnected;
        controllers.SlotDisconnected += OnDisconnected;
        controllers.IdentifiedSlotInput += OnInput;
    }

    public event Action<ControllerSlot, Profile>? Connected;
    public event Action<ControllerSlot>? Disconnected;
    public event Action<ControllerSlot, Profile>? ProfileChanged;
    public event Action<ControllerSlot, InputState>? Input;

    public Profile? GetProfile(ControllerSlot slot)
    {
        lock (_lock)
        {
            return Find(slot)?.Profile;
        }
    }

    internal bool SetProfile(int index, string id, string? expectedMac = null)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(index, out Entry? entry) || !_controllers.IsCurrent(entry.Slot) ||
                (expectedMac is not null && !string.Equals(entry.Slot.Mac, expectedMac, StringComparison.OrdinalIgnoreCase)))
            {
                if (expectedMac is not null)
                {
                    return false;
                }

                throw new ArgumentException(Text.Pick("Secondary controller is not connected.", "İkincil kontrolcü bağlı değil."));
            }

            if (_profiles.Get(id) is null)
            {
                throw new KeyNotFoundException(Text.Pick($"Profile not found: {id}", $"Profil bulunamadı: {id}"));
            }

            _sessionProfiles[entry.Identity] = id;
        }

        Update(_now(), _primary(), _settings.Current);
        return true;
    }

    /// <summary>A null state releases feedback. A slot from an earlier connection is ignored.</summary>
    public void SetGameFeedback(ControllerSlot slot, OutputState? state, OutputFields fields)
    {
        lock (_lock)
        {
            if (Find(slot) is { } entry)
            {
                entry.Game = state;
                entry.GameFields = fields;
            }
        }
    }

    internal void Update(TimeSpan now, Profile primary, AppSettings settings)
    {
        List<(Entry Entry, Profile Profile, bool Connected)> changes = [];
        List<ControllerSlot> disconnected = [];
        List<(Entry Entry, TimeSpan? PreviousSend, OutputState Output)> outputs = [];
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            IReadOnlyList<ControllerSlot> slots = _controllers.Slots;
            foreach (Entry entry in _entries.Values.ToArray())
            {
                if (!_controllers.IsCurrent(entry.Slot))
                {
                    _entries.Remove(entry.Slot.Index);
                    disconnected.Add(entry.Slot);
                }
            }

            if (slots.Any(s => s.Index == 0) || _fallbackId is null)
            {
                _fallbackId = primary.Id;
            }

            Profile fallback = _profiles.Get(_fallbackId) ?? primary;
            bool autoLeds = settings.AutoPlayerLeds && slots.Count >= 2;
            foreach (ControllerSlot slot in slots)
            {
                if (slot.Index == 0 || !_controllers.IsCurrent(slot))
                {
                    continue;
                }

                Entry? entry = Find(slot);
                bool connected = entry is null;
                if (entry is null)
                {
                    entry = new Entry(slot);
                    _entries[slot.Index] = entry;
                }

                Profile? profile = _sessionProfiles.TryGetValue(entry.Identity, out string? chosen)
                    ? _profiles.Get(chosen) : null;
                if (profile is null)
                {
                    _sessionProfiles.Remove(entry.Identity);
                    profile = slot.Mac is { } mac && settings.ControllerDefaults.TryGetValue(mac, out string? id)
                        ? _profiles.Get(id) : null;
                }

                profile ??= fallback;
                if (!ReferenceEquals(entry.Profile, profile))
                {
                    entry.Profile = profile;
                    entry.Triggers = ProfileOutput.TriggersFor(profile);
                    changes.Add((entry, profile, connected));
                }

                if (entry.Sending || (entry.LastSend is { } last && now - last < SendInterval))
                {
                    continue;
                }

                OutputState output = ProfileOutput.Build(profile, entry.Triggers, now,
                    slot.Controller.Latest?.Battery.Percent, autoLeds ? slot.Index + 1 : null);
                UdpState udp = _udp(slot);
                if (udp != UdpState.Empty)
                {
                    output = udp.Overlay(output);
                }
                if (entry.Game is { } game)
                {
                    output = GameOutput.Overlay(output, game, entry.GameFields, profile.Rumble.Strength);
                }

                if (entry.LastSend is null || output != slot.Controller.LastSent)
                {
                    outputs.Add((entry, entry.LastSend, output));
                }
            }
        }

        foreach ((Entry entry, TimeSpan? previousSend, OutputState output) in outputs)
        {
            lock (_lock)
            {
                if (entry.Sending || entry.LastSend != previousSend || Find(entry.Slot) != entry)
                {
                    continue;
                }

                entry.LastSend = now;
                entry.Sending = true;
            }

            try
            {
                _controllers.SendSecondary(entry.Slot, output);
            }
            finally
            {
                lock (_lock)
                {
                    entry.Sending = false;
                }
            }
        }

        foreach (ControllerSlot slot in disconnected)
        {
            Disconnected?.Invoke(slot);
        }

        foreach ((Entry entry, Profile profile, bool connected) in changes)
        {
            if (!_controllers.IsCurrent(entry.Slot))
            {
                continue;
            }

            if (connected)
            {
                Connected?.Invoke(entry.Slot, profile);
            }
            else
            {
                ProfileChanged?.Invoke(entry.Slot, profile);
            }
        }
    }

    public void Dispose()
    {
        _controllers.SlotConnected -= OnConnected;
        _controllers.SlotDisconnected -= OnDisconnected;
        _controllers.IdentifiedSlotInput -= OnInput;
        lock (_lock)
        {
            _disposed = true;
            _entries.Clear();
        }
    }

    private Entry? Find(ControllerSlot slot) =>
        _entries.TryGetValue(slot.Index, out Entry? entry) && ReferenceEquals(entry.Slot, slot) ? entry : null;

    private void OnConnected(ControllerSlot slot)
    {
        if (slot.Index > 0)
        {
            _refresh();
        }
    }

    private void OnDisconnected(int index)
    {
        if (index > 0)
        {
            _refresh();
        }
    }

    private void OnInput(ControllerSlot slot, InputState state)
    {
        Entry? entry;
        EdgeShortcut? shortcut;
        string profileId;
        lock (_lock)
        {
            entry = Find(slot);
            if (entry?.Profile is null || !_controllers.IsCurrent(slot))
            {
                return;
            }

            shortcut = _settings.Current.EdgeShortcuts
                ? EdgeShortcuts.Read(slot.Controller.Details.Model, entry.Buttons ?? state.Buttons, state.Buttons) : null;
            entry.Buttons = state.Buttons;
            profileId = entry.Profile.Id;
        }

        if (shortcut is { } action &&
            EdgeShortcuts.Resolve(action, _profiles.All, profileId) is { } profile)
        {
            bool applied;
            lock (_lock)
            {
                applied = Find(slot) == entry && _controllers.IsCurrent(slot) && _profiles.Get(profile.Id) is not null;
                if (applied)
                {
                    _sessionProfiles[entry.Identity] = profile.Id;
                }
            }

            if (applied)
            {
                Update(_now(), _primary(), _settings.Current);
                _report(EdgeShortcuts.Changed(profile, slot.Index));
            }
        }

        if (_controllers.IsCurrent(slot))
        {
            Input?.Invoke(slot, state);
        }
    }

    private sealed class Entry(ControllerSlot slot)
    {
        public ControllerSlot Slot { get; } = slot;
        public string Identity { get; } = slot.Mac ?? slot.Controller.DevicePath;
        public Profile? Profile { get; set; }
        public Buttons? Buttons { get; set; }
        public (TriggerEffect Left, TriggerEffect Right) Triggers { get; set; }
        public TimeSpan? LastSend { get; set; }
        public bool Sending { get; set; }
        public OutputState? Game { get; set; }
        public OutputFields GameFields { get; set; }
    }
}
