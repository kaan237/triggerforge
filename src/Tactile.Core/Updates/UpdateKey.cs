namespace Tactile.Core.Updates;

/// <summary>Pinned update channel identity (docs/contract-phase7b.md).</summary>
public static class UpdateKey
{
    /// <summary>ECDSA P-256 public key (SubjectPublicKeyInfo, base64) that signs manifest.json.</summary>
    public const string ManifestPublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEP6N66o1XvPHfM4axX+vFD5f2T6kuDz0GvH1q0ZNpEQs+6XFyg9ShEhFCJDZr8KoKpeaZPRbotMew3axYuuzmrQ==";

    /// <summary>Thumbprint of the code signing certificate (CN=Wiaus) that signs the installer.</summary>
    public const string InstallerSignerThumbprint = "25C26195A82AC4EC3A5C57CF6B7D652939154655";

    public const string ManifestUrl = "https://github.com/kaan237/triggerforge/releases/latest/download/manifest.json";
    public const string ReleasesPage = "https://github.com/kaan237/triggerforge/releases";
    public const string AssetPrefix = "https://github.com/kaan237/triggerforge/releases/download/";
}
