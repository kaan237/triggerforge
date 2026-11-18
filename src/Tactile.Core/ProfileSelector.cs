namespace Tactile.Core;

public sealed record ProfileSelection(Profile Profile, string Reason);

/// <summary>
/// Decides which profile is active. Priority: manual pin, then a profile listing the foreground
/// exe, then the controller's default, then the app default, then the first profile.
/// The foreground exe only counts after it has stayed in front for <c>autoProfileDelayMs</c>.
/// Not thread-safe; the engine calls it from one place.
/// </summary>
public sealed class ProfileSelector
{
    public const string Manual = "manual";
    public const string ControllerDefault = "controllerDefault";
    public const string Default = "default";
    public const string GamePrefix = "game:";

    private string? _candidate;
    private TimeSpan _candidateSince;
    private string? _settled;
    private bool _started;
    private string? _pinnedId;

    /// <summary>The foreground exe after the delay.</summary>
    public string? SettledForeground => _settled;

    public string? PinnedId => _pinnedId;

    /// <summary>Keeps <paramref name="profileId"/> active until the settled foreground exe changes.</summary>
    public void Pin(string profileId) => _pinnedId = profileId;

    public void Unpin() => _pinnedId = null;

    /// <param name="foreground">Exe in front right now, or null when unknown.</param>
    /// <param name="profiles">Never empty; the store guarantees one.</param>
    /// <param name="now">Any monotonic clock.</param>
    public ProfileSelection Select(string? foreground, IReadOnlyList<Profile> profiles, AppSettings settings,
        string? controllerMac, TimeSpan now)
    {
        if (profiles.Count == 0)
        {
            throw new ArgumentException("En az bir profil gerekli.", nameof(profiles));
        }

        Settle(foreground, settings, now);

        if (_pinnedId is not null)
        {
            Profile? pinned = Find(profiles, _pinnedId);
            if (pinned is not null)
            {
                return new(pinned, Manual);
            }

            _pinnedId = null;
        }

        if (settings.AutoProfile && _settled is not null)
        {
            foreach (Profile p in profiles)
            {
                if (p.Games.Contains(_settled, StringComparer.OrdinalIgnoreCase))
                {
                    return new(p, GamePrefix + _settled);
                }
            }
        }

        if (controllerMac is not null &&
            settings.ControllerDefaults.TryGetValue(controllerMac.ToUpperInvariant(), out string? macProfile) &&
            Find(profiles, macProfile) is { } forController)
        {
            return new(forController, ControllerDefault);
        }

        if (settings.DefaultProfileId is not null && Find(profiles, settings.DefaultProfileId) is { } fallback)
        {
            return new(fallback, Default);
        }

        return new(profiles[0], Default);
    }

    private void Settle(string? foreground, AppSettings settings, TimeSpan now)
    {
        if (!_started)
        {
            // Whatever is in front at startup counts at once.
            _started = true;
            _candidate = _settled = foreground;
            _candidateSince = now;
            return;
        }

        if (!string.Equals(foreground, _candidate, StringComparison.OrdinalIgnoreCase))
        {
            _candidate = foreground;
            _candidateSince = now;
        }

        if (string.Equals(_candidate, _settled, StringComparison.OrdinalIgnoreCase) ||
            now - _candidateSince < TimeSpan.FromMilliseconds(settings.AutoProfileDelayMs))
        {
            return;
        }

        _settled = _candidate;
        // With automatic switching off, a manual choice stays until the next manual choice.
        if (settings.AutoProfile)
        {
            _pinnedId = null;
        }
    }

    private static Profile? Find(IReadOnlyList<Profile> profiles, string id)
    {
        foreach (Profile p in profiles)
        {
            if (p.Id == id)
            {
                return p;
            }
        }

        return null;
    }
}
