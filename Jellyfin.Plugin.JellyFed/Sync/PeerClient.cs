using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyFed.Api.Dto;
using Jellyfin.Plugin.JellyFed.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyFed.Sync;

/// <summary>
/// HTTP client for querying the JellyFed federation API of a remote peer.
/// </summary>
public class PeerClient
{
    private readonly HttpClient _http;
    private readonly ILogger<PeerClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of <see cref="IHttpClientFactory"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{PeerClient}"/> interface.</param>
    public PeerClient(IHttpClientFactory httpClientFactory, ILogger<PeerClient> logger)
    {
        _http = httpClientFactory.CreateClient("JellyFed");
        _logger = logger;
    }

    /// <summary>
    /// Fetches the full catalog from a peer, optionally filtered by date.
    /// </summary>
    /// <param name="peer">The peer configuration.</param>
    /// <param name="since">Only return items updated after this date (delta sync).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The catalog response, or null on failure.</returns>
    public async Task<CatalogResponseDto?> GetCatalogAsync(
        PeerConfiguration peer,
        DateTime? since,
        CancellationToken cancellationToken)
    {
        var url = BuildUrl(peer, "/JellyFed/catalog");
        if (since.HasValue)
        {
            url += $"?since={Uri.EscapeDataString(since.Value.ToString("O"))}";
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", peer.FederationToken);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<CatalogResponseDto>(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch catalog from peer {PeerName} ({Url})", peer.Name, url);
            return null;
        }
    }

    /// <summary>
    /// Fetches seasons and episodes for a series from a peer.
    /// </summary>
    /// <param name="peer">The peer configuration.</param>
    /// <param name="seriesId">The Jellyfin series ID on the remote peer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The seasons response, or null on failure.</returns>
    public async Task<SeasonsResponseDto?> GetSeasonsAsync(
        PeerConfiguration peer,
        string seriesId,
        CancellationToken cancellationToken)
    {
        var url = BuildUrl(peer, $"/JellyFed/catalog/series/{seriesId}/seasons");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", peer.FederationToken);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<SeasonsResponseDto>(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch seasons for series {SeriesId} from {PeerName}", seriesId, peer.Name);
            return null;
        }
    }

    /// <summary>
    /// Pings a peer's /JellyFed/health endpoint with the provided URL + token.
    /// Used by the admin UI when adding or editing a peer, so misconfiguration shows up immediately.
    /// </summary>
    /// <param name="url">Peer base URL.</param>
    /// <param name="federationToken">Federation token to present in the Bearer header.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tuple (reachable, reportedVersion); reportedVersion is null when unreachable.</returns>
    public async Task<(bool Reachable, string? Version)> HealthCheckAsync(
        string url,
        string federationToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return (false, null);
        }

        var healthUrl = url.TrimEnd('/') + "/JellyFed/health";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, healthUrl);
            if (!string.IsNullOrWhiteSpace(federationToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", federationToken);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, null);
            }

            var health = await response.Content
                .ReadFromJsonAsync<HealthDto>(cts.Token)
                .ConfigureAwait(false);
            return (true, health?.Version);
        }
#pragma warning disable CA1031 // Health-check is best-effort; surface any failure as "unreachable".
        catch
        {
            return (false, null);
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Downloads a remote image to a local file path.
    /// </summary>
    /// <param name="imageUrl">The remote image URL.</param>
    /// <param name="localPath">The local destination path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if downloaded successfully.</returns>
    public async Task<bool> DownloadImageAsync(
        string imageUrl,
        string localPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await _http.GetByteArrayAsync(new Uri(imageUrl), cancellationToken)
                .ConfigureAwait(false);
            await System.IO.File.WriteAllBytesAsync(localPath, bytes, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download image {Url}", imageUrl);
            return false;
        }
    }

    /// <summary>
    /// Announces this instance to a peer so it can register us as a peer in return.
    /// If the peer responds with a per-peer <c>accessToken</c>, that token replaces the
    /// peer's global <c>FederationToken</c> in the local config so that future API calls
    /// use the revocable per-peer credential instead of the shared global token.
    /// </summary>
    /// <param name="peer">The peer to notify.</param>
    /// <param name="selfName">Friendly name of this instance.</param>
    /// <param name="selfUrl">URL of this instance reachable by the peer.</param>
    /// <param name="selfToken">Federation token of this instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task RegisterOnPeerAsync(
        PeerConfiguration peer,
        string selfName,
        string selfUrl,
        string selfToken,
        CancellationToken cancellationToken)
    {
        var url = BuildUrl(peer, "/JellyFed/peer/register");
        try
        {
            var payload = new Api.Dto.RegisterPeerRequestDto
            {
                Name = selfName,
                Url = selfUrl,
                FederationToken = selfToken
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = System.Net.Http.Json.JsonContent.Create(payload);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("JellyFed: registered on peer {PeerName} — HTTP {Status}", peer.Name, (int)response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            // If the peer issued a per-peer access token, store it as our FederationToken
            // for that peer. From now on this token (not the global one) is used for all
            // API calls to that peer, and can be revoked by the peer on their end.
            var result = await response.Content
                .ReadFromJsonAsync<Api.Dto.RegisterPeerResponseDto>(cancellationToken)
                .ConfigureAwait(false);

            if (result is null ||
                string.IsNullOrEmpty(result.AccessToken) ||
                !string.Equals(result.Status, "ok", StringComparison.Ordinal))
            {
                return;
            }

            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                return;
            }

            var localPeer = config.Peers.FirstOrDefault(p =>
                string.Equals(p.Url, peer.Url, StringComparison.OrdinalIgnoreCase));

            if (localPeer is not null &&
                !string.Equals(localPeer.FederationToken, result.AccessToken, StringComparison.Ordinal))
            {
                localPeer.FederationToken = result.AccessToken;
                Plugin.Instance!.SaveConfiguration();
                _logger.LogInformation(
                    "JellyFed: stored per-peer access token from {PeerName} — future API calls use revocable token.",
                    peer.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "JellyFed: could not register on peer {PeerName} (non-fatal)", peer.Name);
        }
    }

    private static string BuildUrl(PeerConfiguration peer, string path)
    {
        var baseUrl = peer.Url.TrimEnd('/');
        return baseUrl + path;
    }

    private sealed class HealthDto
    {
        public string? Version { get; set; }
    }
}
