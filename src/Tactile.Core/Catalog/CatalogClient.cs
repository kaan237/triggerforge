using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Tactile.Core.Catalog;

internal sealed class CatalogClient : IDisposable
{
    private const int IndexLimit = CatalogManifest.IndexLimit;
    private const int SignatureLimit = 1024;
    private const int ProfileLimit = 256 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private readonly HttpClient _http;
    private readonly byte[] _publicKey;
    private readonly bool _keyUsable;

    public CatalogClient(string version, HttpMessageHandler? handler = null, byte[]? publicKey = null)
    {
        _publicKey = publicKey ?? DecodeKey(CatalogKey.CatalogPublicKey);
        _keyUsable = IsUsable(_publicKey);
        _http = new HttpClient(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"TriggerForge/{version}");
    }

    public async Task<CatalogManifest> FetchIndexAsync(CancellationToken cancellation)
    {
        if (!_keyUsable)
        {
            throw new CatalogException(CatalogError.BadSignature, "No usable catalog key is pinned.");
        }

        byte[] index = await FetchAsync(new Uri(CatalogKey.IndexUrl), IndexLimit, exact: null, signature: false, cancellation)
            .ConfigureAwait(false);
        byte[] signature = await FetchAsync(new Uri(CatalogKey.SignatureUrl), SignatureLimit, exact: null, signature: true, cancellation)
            .ConfigureAwait(false);
        return CatalogManifest.Verify(index, Encoding.ASCII.GetString(signature).Trim(), _publicKey);
    }

    /// <summary>The bytes of the profile file, once their length and SHA-256 match the signed entry.</summary>
    public async Task<byte[]> FetchProfileAsync(CatalogEntry entry, CancellationToken cancellation)
    {
        if (!CatalogManifest.IsValidPath(entry.Path) || entry.Size is < 1 or > ProfileLimit)
        {
            throw new CatalogException(CatalogError.BadManifest, "Invalid catalog entry.");
        }

        byte[] bytes = await FetchAsync(new Uri(CatalogKey.ContentPrefix + entry.Path), (int)entry.Size, exact: entry.Size,
            signature: false, cancellation).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), entry.Sha256))
        {
            throw new CatalogException(CatalogError.BadFile, "The profile file does not match the catalog.");
        }

        return bytes;
    }

    private static byte[] DecodeKey(string base64)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private static bool IsUsable(byte[] key)
    {
        if (key.Length == 0)
        {
            return false;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(key, out int consumed);
            return consumed == key.Length && ecdsa.KeySize == 256;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private async Task<byte[]> FetchAsync(Uri url, int limit, long? exact, bool signature, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(RequestTimeout);
        CancellationToken token = deadline.Token;
        try
        {
            using HttpResponseMessage response = await GetAsync(url, signature, token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > limit)
            {
                throw new CatalogException(CatalogError.TooLarge, "The catalog response exceeds its size limit.");
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var result = new MemoryStream();
            byte[] buffer = new byte[16 * 1024];
            long total = 0;
            while (true)
            {
                int count = await body.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (count == 0)
                {
                    break;
                }

                total += count;
                if (total > limit)
                {
                    throw new CatalogException(CatalogError.TooLarge, "The catalog response exceeds its size limit.");
                }

                result.Write(buffer, 0, count);
            }

            if (exact is { } expected && total != expected)
            {
                throw new CatalogException(CatalogError.BadFile, "The profile file length does not match the catalog.");
            }

            return result.ToArray();
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            throw new CatalogException(CatalogError.Network, "The catalog request failed.", e);
        }
        catch (OperationCanceledException e) when (!cancellation.IsCancellationRequested)
        {
            throw new CatalogException(CatalogError.Network, "The catalog request timed out.", e);
        }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri url, bool signature, CancellationToken cancellation)
    {
        for (int redirects = 0; ; redirects++)
        {
            if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps || url.Port != 443 ||
                url.UserInfo.Length != 0 || url.IdnHost != "raw.githubusercontent.com")
            {
                throw new CatalogException(CatalogError.UntrustedHost, "The catalog URL is outside the trusted host.");
            }

            if (redirects > 5)
            {
                throw new CatalogException(CatalogError.Network, "The catalog request exceeded the redirect limit.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                    HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (response.Headers.Location is not { } location || !Uri.TryCreate(url, location, out Uri? target))
                    {
                        throw new CatalogException(CatalogError.UntrustedHost, "The catalog redirect has an invalid location.");
                    }

                    url = target;
                    continue;
                }

                if (signature && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    throw new CatalogException(CatalogError.BadSignature, "The catalog signature is missing.");
                }

                response.EnsureSuccessStatusCode();
            }
        }
    }

    public void Dispose() => _http.Dispose();
}
