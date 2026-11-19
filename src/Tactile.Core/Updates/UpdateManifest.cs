using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Tactile.Core.Updates;

internal sealed record UpdateAsset(string Name, Uri Url, long Size, byte[] Sha256);

internal sealed record UpdateManifest(string Version, Version Number, string Released,
    LocalizedMessage Notes, UpdateAsset Installer, UpdateAsset Portable)
{
    internal const int AssetLimit = 300 * 1024 * 1024;

    public static UpdateManifest Verify(byte[] body, string signature, byte[] publicKey)
    {
        Span<byte> bytes = stackalloc byte[64];
        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out int consumed);
            if (consumed != publicKey.Length || key.KeySize != 256 ||
                !Convert.TryFromBase64String(signature, bytes, out int written) || written != bytes.Length ||
                !key.VerifyData(body, bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                throw new UpdateException(UpdateError.BadSignature, "Manifest signature did not verify.");
            }
        }
        catch (CryptographicException e)
        {
            throw new UpdateException(UpdateError.BadSignature, "Manifest signature could not be verified.", e);
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(body, new JsonDocumentOptions { AllowDuplicateProperties = false });
            JsonElement root = json.RootElement;
            string version = RequiredString(root, "version");
            string released = RequiredString(root, "released");
            if (root.GetProperty("schema").GetInt32() != 1 ||
                version.Any(c => !char.IsAsciiDigit(c) && c != '.') || !System.Version.TryParse(version, out Version? number) ||
                !DateOnly.TryParseExact(released, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                throw new UpdateException(UpdateError.BadManifest, "Invalid manifest schema, version or release date.");
            }

            JsonElement notes = root.GetProperty("notes");
            return new UpdateManifest(version, number, released,
                new LocalizedMessage(RequiredString(notes, "en"), RequiredString(notes, "tr")),
                ReadAsset(root.GetProperty("installer"), version, "setup.exe"),
                ReadAsset(root.GetProperty("portable"), version, "portable.zip"));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new UpdateException(UpdateError.BadManifest, "Invalid manifest fields.", e);
        }
    }

    private static UpdateAsset ReadAsset(JsonElement asset, string version, string suffix)
    {
        string name = RequiredString(asset, "name");
        string url = RequiredString(asset, "url");
        string sha256 = RequiredString(asset, "sha256");
        long size = asset.GetProperty("size").GetInt64();
        string prefix = $"{UpdateKey.AssetPrefix}v{version}/";
        if (name != $"TriggerForge-{version}-{suffix}" ||
            !url.StartsWith(prefix, StringComparison.Ordinal) ||
            !url.EndsWith("/" + name, StringComparison.Ordinal) ||
            !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            !uri.GetLeftPart(UriPartial.Path).StartsWith(prefix, StringComparison.Ordinal) ||
            !uri.AbsolutePath.EndsWith("/" + name, StringComparison.Ordinal) ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            size is < 1 or > AssetLimit || sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit))
        {
            throw new UpdateException(UpdateError.BadManifest, "Invalid manifest asset.");
        }

        return new UpdateAsset(name, uri, size, Convert.FromHexString(sha256));
    }

    private static string RequiredString(JsonElement element, string name) =>
        element.GetProperty(name).GetString() ?? throw new UpdateException(UpdateError.BadManifest, $"Missing {name}.");
}
