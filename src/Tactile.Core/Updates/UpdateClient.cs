using System.Net;
using System.Text;

namespace Tactile.Core.Updates;

internal sealed class UpdateClient : IDisposable
{
    private const int ManifestLimit = 64 * 1024;
    private const int SignatureLimit = 1024;
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(10);
    private readonly HttpClient _http;
    private readonly byte[] _publicKey;

    public UpdateClient(string version, HttpMessageHandler? handler = null, byte[]? publicKey = null)
    {
        _publicKey = publicKey ?? Convert.FromBase64String(UpdateKey.ManifestPublicKey);
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

    public async Task<UpdateManifest> CheckAsync(CancellationToken cancellation)
    {
        using var manifest = new MemoryStream();
        using var signature = new MemoryStream();
        await FetchAsync(new Uri(UpdateKey.ManifestUrl), manifest, ManifestLimit, ManifestTimeout, cancellation)
            .ConfigureAwait(false);
        await FetchAsync(new Uri(UpdateKey.ManifestUrl + ".sig"), signature, SignatureLimit, ManifestTimeout, cancellation,
            signature: true).ConfigureAwait(false);
        return UpdateManifest.Verify(manifest.ToArray(), Encoding.ASCII.GetString(signature.ToArray()), _publicKey);
    }

    public Task DownloadAsync(UpdateAsset asset, Stream destination, Action<long> progress, CancellationToken cancellation) =>
        // The signed manifest states the exact size, so anything longer stops at once.
        FetchAsync(asset.Url, destination, checked((int)asset.Size), InstallerTimeout, cancellation, progress);

    private async Task FetchAsync(Uri url, Stream destination, int limit, TimeSpan timeout,
        CancellationToken cancellation, Action<long>? progress = null, bool signature = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        CancellationToken token = deadline.Token;
        try
        {
            using HttpResponseMessage response = await GetAsync(url, signature, token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > limit)
            {
                throw new UpdateException(UpdateError.TooLarge, "The update response exceeds its size limit.");
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            byte[] buffer = new byte[64 * 1024];
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
                    throw new UpdateException(UpdateError.TooLarge, "The update response exceeds its size limit.");
                }

                try
                {
                    await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                }
                catch (IOException e)
                {
                    throw new UpdateException(UpdateError.BadFile, "Could not write the update response to disk.", e);
                }

                progress?.Invoke(total);
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            throw new UpdateException(UpdateError.Network, "The update request failed.", e);
        }
        catch (OperationCanceledException e) when (!cancellation.IsCancellationRequested)
        {
            throw new UpdateException(UpdateError.Network, "The update request timed out.", e);
        }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri url, bool signature, CancellationToken cancellation)
    {
        for (int redirects = 0; ; redirects++)
        {
            if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps || url.Port != 443 ||
                url.UserInfo.Length != 0 ||
                url.IdnHost is not ("github.com" or "objects.githubusercontent.com" or "release-assets.githubusercontent.com"))
            {
                throw new UpdateException(UpdateError.UntrustedHost, "The update URL is outside the trusted hosts.");
            }

            if (redirects > 5)
            {
                throw new UpdateException(UpdateError.Network, "The update request exceeded the redirect limit.");
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
                        throw new UpdateException(UpdateError.UntrustedHost, "The update redirect has an invalid location.");
                    }

                    url = target;
                    continue;
                }

                if (signature && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    throw new UpdateException(UpdateError.BadSignature, "The manifest signature is missing.");
                }

                response.EnsureSuccessStatusCode();
            }
        }
    }

    public void Dispose() => _http.Dispose();
}
