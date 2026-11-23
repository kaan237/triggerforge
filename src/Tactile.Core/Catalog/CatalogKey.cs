namespace Tactile.Core.Catalog;

/// <summary>Pinned community catalog identity (docs/contract-phase14.md).</summary>
public static class CatalogKey
{
    /// <summary>ECDSA P-256 public key (SubjectPublicKeyInfo, base64) that signs index.json. Not the update key.</summary>
    public const string CatalogPublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEptYC6l/XalXkbN36c4EoDQGW3bC+G+6j52ysLZBSmiN+Nlu2diBhnFab+RF9QLI3XxIgr4+Nr3P6GosSHq53zA==";

    public const string IndexUrl = "https://raw.githubusercontent.com/kaan237/triggerforge-profiles/main/index.json";
    public const string SignatureUrl = "https://raw.githubusercontent.com/kaan237/triggerforge-profiles/main/index.json.sig";
    public const string ContentPrefix = "https://raw.githubusercontent.com/kaan237/triggerforge-profiles/main/";
    public const string RepositoryUrl = "https://github.com/kaan237/triggerforge-profiles";
}
