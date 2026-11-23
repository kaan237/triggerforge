namespace Tactile.Core.Catalog;

public enum CatalogError
{
    Network,
    UntrustedHost,
    TooLarge,
    BadSignature,
    BadManifest,
    BadFile,
    Outdated,

    /// <summary>The profile could not be written to disk.</summary>
    Storage,

    /// <summary>Something unexpected went wrong; details are in the log.</summary>
    Failed,
}

internal sealed class CatalogException(CatalogError code, string detail, Exception? inner = null)
    : Exception(detail, inner)
{
    public CatalogError Code { get; } = code;
}
