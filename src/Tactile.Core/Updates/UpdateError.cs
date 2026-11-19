namespace Tactile.Core.Updates;

public enum UpdateError
{
    Network,
    UntrustedHost,
    TooLarge,
    BadSignature,
    BadManifest,
    BadFile,
    BadSigner,
}

internal sealed class UpdateException(UpdateError code, string detail, Exception? inner = null)
    : Exception(detail, inner)
{
    public UpdateError Code { get; } = code;
}
