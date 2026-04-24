using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyFed.Api.Dto;
using Jellyfin.Plugin.JellyFed.Configuration;
using Jellyfin.Plugin.JellyFed.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyFed.Api;

/// <summary>
/// JellyFed federation API endpoints.
/// </summary>
[ApiController]
[Route("JellyFed")]
[Produces(MediaTypeNames.Application.Json)]
public class FederationController : ControllerBase
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;
    private readonly FederationSyncTask _syncTask;
    private readonly PeerClient _peerClient;
    private readonly ILogger<FederationController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="taskManager">Instance of the <see cref="ITaskManager"/> interface.</param>
    /// <param name="syncTask">Instance of the <see cref="FederationSyncTask"/> used for per-peer sync.</param>
    /// <param name="peerClient">Instance of the <see cref="PeerClient"/> used for health checks.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{FederationController}"/> interface.</param>
    public FederationController(
        ILibraryManager libraryManager,
        ITaskManager taskManager,
        FederationSyncTask syncTask,
        PeerClient peerClient,
        ILogger<FederationController> logger)
    {
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _syncTask = syncTask;
        _peerClient = peerClient;
        _logger = logger;
    }

    /// <summary>
    /// Health check — no authentication required.
    /// </summary>
    /// <returns>Plugin version and status.</returns>
    [HttpGet("health")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetHealth()
    {
        return Ok(new
        {
            version = Plugin.Instance?.Version.ToString(3) ?? "unknown",
            name = "JellyFed",
            status = "ok"
        });
    }

    /// <summary>
    /// Returns the full catalog of this instance (movies + series).
    /// Supports delta sync via the <paramref name="since"/> parameter.
    /// </summary>
    /// <param name="type">Filter by type: "Movie", "Series", or omit for both.</param>
    /// <param name="since">ISO 8601 date — return only items updated after this date.</param>
    /// <param name="limit">Maximum number of items to return (default 5000).</param>
    /// <param name="offset">Number of items to skip (default 0).</param>
    /// <returns>Catalog response with matching items.</returns>
    [HttpGet("catalog")]
    [AllowAnonymous]
    [ServiceFilter(typeof(FederationAuthFilter))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<CatalogResponseDto> GetCatalog(
        [FromQuery] string? type = null,
        [FromQuery] string? since = null,
        [FromQuery] int limit = 5000,
        [FromQuery] int offset = 0)
    {
        var baseUrl = GetBaseUrl();
        var token = Plugin.Instance!.Configuration.FederationToken;
        var apiKey = Plugin.Instance.Configuration.JellyfinApiKey;

        DateTime? sinceDate = null;
        if (!string.IsNullOrEmpty(since) &&
            DateTime.TryParse(since, null, DateTimeStyles.RoundtripKind, out var parsed))
        {
            sinceDate = parsed;
        }

        var items = new List<CatalogItemDto>();

        if (type is null or "Movie")
        {
            items.AddRange(QueryItems(BaseItemKind.Movie, baseUrl, token, apiKey, sinceDate));
        }

        if (type is null or "Series")
        {
            items.AddRange(QueryItems(BaseItemKind.Series, baseUrl, token, apiKey, sinceDate));
        }

        var page = items.Skip(offset).Take(limit).ToArray();

        _logger.LogInformation(
            "GET /JellyFed/catalog — {Total} items (type={Type}, since={Since})",
            items.Count,
            type ?? "all",
            since ?? "all");

        return Ok(new CatalogResponseDto { Total = items.Count, Items = page });
    }

    /// <summary>
    /// Returns all seasons and episodes for a given series.
    /// </summary>
    /// <param name="seriesId">The Jellyfin item ID of the series.</param>
    /// <returns>Seasons with nested episodes.</returns>
    [HttpGet("catalog/series/{seriesId}/seasons")]
    [AllowAnonymous]
    [ServiceFilter(typeof(FederationAuthFilter))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<SeasonsResponseDto> GetSeriesSeasons([FromRoute] string seriesId)
    {
        if (!Guid.TryParse(seriesId, out var seriesGuid))
        {
            return BadRequest("Invalid series ID.");
        }

        var series = _libraryManager.GetItemById(seriesGuid);
        if (series is null)
        {
            return NotFound();
        }

        var baseUrl = GetBaseUrl();
        var token = Plugin.Instance!.Configuration.FederationToken;
        var apiKey = Plugin.Instance.Configuration.JellyfinApiKey;

        var seasons = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Season],
            ParentId = seriesGuid,
            OrderBy = [(ItemSortBy.IndexNumber, SortOrder.Ascending)]
        });

        var response = new SeasonsResponseDto { SeriesId = seriesId };

        foreach (var season in seasons)
        {
            var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                ParentId = season.Id,
                OrderBy = [(ItemSortBy.IndexNumber, SortOrder.Ascending)]
            });

            var seasonDto = new SeasonDto
            {
                JellyfinId = season.Id.ToString("N"),
                SeasonNumber = season.IndexNumber,
                Title = season.Name
            };

            foreach (var ep in episodes)
            {
                var epInfo = ExtractStreamInfo(ep);

                seasonDto.Episodes.Add(new EpisodeDto
                {
                    JellyfinId = ep.Id.ToString("N"),
                    EpisodeNumber = ep.IndexNumber,
                    Title = ep.Name,
                    Overview = ep.Overview,
                    AirDate = ep.PremiereDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    RuntimeMinutes = TicksToMinutes(ep.RunTimeTicks),
                    StillUrl = HasImage(ep, ImageType.Primary)
                        ? ImageUrl(baseUrl, ep.Id, "Primary", token, apiKey)
                        : null,
                    StreamUrl = $"{baseUrl}/JellyFed/stream/{ep.Id:N}?token={token}",
                    Container = epInfo.Container,
                    VideoCodec = epInfo.VideoCodec,
                    Width = epInfo.Width,
                    Height = epInfo.Height,
                    AudioCodec = epInfo.AudioCodec,
                    MediaStreams = epInfo.MediaStreams
                });
            }

            response.Seasons.Add(seasonDto);
        }

        _logger.LogInformation(
            "GET /JellyFed/catalog/series/{SeriesId}/seasons — {SeasonCount} seasons",
            seriesId,
            response.Seasons.Count);

        return Ok(response);
    }

    private IEnumerable<CatalogItemDto> QueryItems(
        BaseItemKind kind,
        string baseUrl,
        string token,
        string? apiKey,
        DateTime? since)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [kind],
            IsVirtualItem = false,
            Recursive = true,
            OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)]
        };

        var items = _libraryManager.GetItemList(query);

        // Exclude items that live inside our own jellyfed-library (.strm files synced from peers).
        // Without this filter, federated items would be re-exposed to other peers, causing
        // their titles (already containing the year from the folder name) to compound on each
        // sync hop: "Title (2025)" → "Title (2025) (2025)" → "Title (2025) (2025) (2025)".
        var fedConfig = Plugin.Instance?.Configuration;

        foreach (var item in items)
        {
            if (fedConfig is not null &&
                !string.IsNullOrEmpty(item.Path) &&
                FederatedPathHelper.IsUnderFederatedContent(item.Path, fedConfig))
            {
                continue;
            }

            if (since.HasValue && item.DateModified <= since.Value)
            {
                continue;
            }

            // Extract codec + all audio/subtitle tracks so the client writes complete
            // <fileinfo><streamdetails> in NFO files. Without this, Jellyfin defaults to
            // direct-play and the browser receives raw MKV/HEVC → fatal player error.
            var info = kind == BaseItemKind.Movie ? ExtractStreamInfo(item) : default;

            yield return new CatalogItemDto
            {
                JellyfinId = item.Id.ToString("N"),
                TmdbId = item.GetProviderId("Tmdb"),
                ImdbId = item.GetProviderId("Imdb"),
                Type = kind == BaseItemKind.Movie ? "Movie" : "Series",
                Title = item.Name,
                OriginalTitle = string.IsNullOrEmpty(item.OriginalTitle) ? null : item.OriginalTitle,
                Overview = item.Overview,
                Year = item.ProductionYear,
                RuntimeMinutes = TicksToMinutes(item.RunTimeTicks),
                VoteAverage = item.CommunityRating.HasValue ? (double)item.CommunityRating.Value : null,
                Genres = item.Genres ?? [],
                PosterUrl = HasImage(item, ImageType.Primary)
                    ? ImageUrl(baseUrl, item.Id, "Primary", token, apiKey)
                    : null,
                BackdropUrl = HasImage(item, ImageType.Backdrop)
                    ? ImageUrl(baseUrl, item.Id, "Backdrop", token, apiKey)
                    : null,
                StreamUrl = kind == BaseItemKind.Movie
                    ? $"{baseUrl}/JellyFed/stream/{item.Id:N}?token={token}"
                    : null,
                AddedAt = item.DateCreated.ToString("O", CultureInfo.InvariantCulture),
                UpdatedAt = item.DateModified.ToString("O", CultureInfo.InvariantCulture),
                Container = info.Container,
                VideoCodec = info.VideoCodec,
                Width = info.Width,
                Height = info.Height,
                AudioCodec = info.AudioCodec,
                MediaStreams = info.MediaStreams
            };
        }
    }

    /// <summary>
    /// Ensures the configured JellyFed content roots exist on disk.
    /// </summary>
    /// <returns>The effective movies / series / anime roots.</returns>
    [HttpPost("libraries/roots/ensure")]
    [AllowAnonymous]
    [ServiceFilter(typeof(FederationAuthFilter))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> EnsureLibraryRoots()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return Ok(new { moviesRoot = string.Empty, seriesRoot = string.Empty, animeRoot = string.Empty });
        }

        var moviesRoot = config.GetEffectiveMoviesRoot();
        var seriesRoot = config.GetEffectiveSeriesRoot();
        var animeRoot = config.GetEffectiveAnimeRoot();

        if (!string.IsNullOrWhiteSpace(moviesRoot))
        {
            Directory.CreateDirectory(moviesRoot);
        }

        if (!string.IsNullOrWhiteSpace(seriesRoot))
        {
            Directory.CreateDirectory(seriesRoot);
        }

        if (!string.IsNullOrWhiteSpace(animeRoot))
        {
            Directory.CreateDirectory(animeRoot);
        }

        return Ok(new { moviesRoot, seriesRoot, animeRoot });
    }

    /// <summary>
    /// Returns all configured peers with their current online/offline status.
    /// </summary>
    /// <returns>Peer list with status.</returns>
    [HttpGet("peers")]
    [AllowAnonymous]
    [ServiceFilter(typeof(FederationAuthFilter))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<PeersResponseDto> GetPeers()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return Ok(new PeersResponseDto());
        }

        var libraryPath = config.LibraryPath;
        var states = string.IsNullOrWhiteSpace(libraryPath)
            ? new System.Collections.Generic.Dictionary<string, PeerStatus>()
            : PeerStateStore.Load(libraryPath);

        var peers = config.Peers.Select(peer =>
        {
            states.TryGetValue(peer.Name, out var status);
            return new PeerDto
            {
                Name = peer.Name,
                Url = peer.Url,
                Enabled = peer.Enabled,
                Online = status?.Online ?? false,
                LastSeen = status?.LastSeen,
                Version = status?.Version,
                MovieCount = status?.MovieCount ?? 0,
                SeriesCount = status?.SeriesCount ?? 0
            };
        }).ToList();

        return Ok(new PeersResponseDto { Peers = peers });
    }

    /// <summary>
    /// Registers a federation request from a remote instance.
    /// Added as a disabled peer for the admin to review and enable.
    /// </summary>
    /// <param name="request">The registration request.</param>
    /// <returns>Status of the registration.</returns>
    [HttpPost("peer/register")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult RegisterPeer([FromBody] RegisterPeerRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.Url) ||
            string.IsNullOrWhiteSpace(request.FederationToken))
        {
            return BadRequest("Name, Url and FederationToken are required.");
        }

        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Plugin configuration unavailable.");
        }

        // Refus si le peer a été manuellement supprimé (blacklist)
        var isBlocked = config.BlockedPeerUrls.Any(u =>
            string.Equals(u, request.Url, StringComparison.OrdinalIgnoreCase));

        if (isBlocked)
        {
            _logger.LogInformation("JellyFed: registration from {Name} ({Url}) refused — peer is blocked.", request.Name, request.Url);
            return Ok(new RegisterPeerResponseDto { Status = "blocked", Message = "This peer has been removed by an admin." });
        }

        var existing = config.Peers.FirstOrDefault(p =>
            string.Equals(p.Url, request.Url, StringComparison.OrdinalIgnoreCase));

        string accessToken;

        if (existing is null)
        {
            // New peer: create entry with a fresh per-peer access token.
            accessToken = GenerateAccessToken();
            config.Peers.Add(new Configuration.PeerConfiguration
            {
                Name = request.Name,
                Url = request.Url,
                FederationToken = request.FederationToken,
                Enabled = true,
                SyncMovies = true,
                SyncSeries = true,
                AccessToken = accessToken
            });
            _logger.LogInformation("JellyFed: auto-registered peer {Name} ({Url}).", request.Name, request.Url);
        }
        else
        {
            // Existing peer: issue a token if not already done, reuse otherwise.
            // Regenerating on every re-registration would invalidate in-flight requests.
            if (string.IsNullOrEmpty(existing.AccessToken))
            {
                existing.AccessToken = GenerateAccessToken();
                _logger.LogInformation("JellyFed: issued access token for existing peer {Name} ({Url}).", request.Name, request.Url);
            }

            accessToken = existing.AccessToken;
        }

        Plugin.Instance!.SaveConfiguration();

        return Ok(new RegisterPeerResponseDto
        {
            Status = "ok",
            Message = "Peer registered.",
            AccessToken = accessToken
        });
    }

    /// <summary>
    /// Queues a federation sync task for all peers (or a named peer).
    /// </summary>
    /// <param name="request">Peer name, or null to sync all.</param>
    /// <returns>Acknowledgement.</returns>
    [HttpPost("peer/sync")]
    [AllowAnonymous]
    [ServiceFilter(typeof(FederationAuthFilter))]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult TriggerSync([FromBody] SyncPeerRequestDto request)
    {
        _taskManager.QueueScheduledTask<FederationSyncTask>();
        _logger.LogInformation("JellyFed: manual sync queued (peer={PeerName}).", request.PeerName ?? "all");
        return Accepted(new { status = "queued" });
    }

    /// <summary>
    /// Returns manifest stats (synced item counts) grouped by peer.
    /// </summary>
    /// <returns>Per-peer movie and series counts from the manifest.</returns>
    [HttpGet("manifest/stats")]
    [AllowAnonymous]
    [ServiceFilter(typeof(FederationAuthFilter))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<ManifestStatsDto> GetManifestStats()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(config.LibraryPath))
        {
            return Ok(new ManifestStatsDto());
        }

        var manifest = ReadManifest(config.LibraryPath);

        var stats = new Dictionary<string, PeerCatalogStatsDto>(StringComparer.Ordinal);

        foreach (var entry in manifest.Movies.Values)
        {
            if (!stats.TryGetValue(entry.PeerName, out var s))
            {
                s = new PeerCatalogStatsDto { Name = entry.PeerName };
                stats[entry.PeerName] = s;
            }

            s.MovieCount++;
        }

        foreach (var entry in manifest.Series.Values)
        {
            if (!stats.TryGetValue(entry.PeerName, out var s))
            {
                s = new PeerCatalogStatsDto { Name = entry.PeerName };
                stats[entry.PeerName] = s;
            }

            s.SeriesCount++;
        }

        return Ok(new ManifestStatsDto { Peers = [.. stats.Values.OrderBy(p => p.Name)] });
    }

    /// <summary>
    /// Purges all synced .strm files for a given peer from the manifest and filesystem.
    /// </summary>
    /// <param name="request">The peer name to purge.</param>
    /// <returns>Number of deleted movies and series.</returns>
    [HttpPost("peer/purge")]
    [AllowAnonymous]
    [ServiceFilter(typeof(FederationAuthFilter))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult PurgePeerCatalog([FromBody] PurgePeerCatalogRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.PeerName))
        {
            return BadRequest("PeerName is required.");
        }

        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(config.LibraryPath))
        {
            return BadRequest("LibraryPath is not configured.");
        }

        var manifest = ReadManifest(config.LibraryPath);
        var name = request.PeerName;

        var movieKeys = manifest.Movies
            .Where(kv => string.Equals(kv.Value.PeerName, name, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();

        var seriesKeys = manifest.Series
            .Where(kv => string.Equals(kv.Value.PeerName, name, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();

        // Collect folder paths before removing from manifest.
        var deletedPaths = movieKeys.Select(k => manifest.Movies[k].Path)
            .Concat(seriesKeys.Select(k => manifest.Series[k].Path))
            .ToList();

        foreach (var key in movieKeys)
        {
            var path = manifest.Movies[key].Path;
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            manifest.Movies.Remove(key);
        }

        foreach (var key in seriesKeys)
        {
            var path = manifest.Series[key].Path;
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            manifest.Series.Remove(key);
        }

        WriteManifest(config.LibraryPath, manifest);

        // Remove items from Jellyfin's library index so they disappear immediately
        // without waiting for a scheduled scan.
        RemoveLibraryItems(deletedPaths);

        FederatedPathHelper.TryDeletePeerContentFolders(config, name);

        _logger.LogInformation(
            "JellyFed: purged catalog for peer {PeerName} — {MovieCount} movies, {SeriesCount} series deleted.",
            name,
            movieKeys.Count,
            seriesKeys.Count);

        return Ok(new { status = "ok", deletedMovies = movieKeys.Count, deletedSeries = seriesKeys.Count });
    }

    /// <summary>
    /// Returns full per-peer details for the admin "Peers" tab: identity, online/offline status,
    /// remote catalog counts (from heartbeat), local synced counts by type, disk usage and folder paths.
    /// </summary>
    /// <returns>Per-peer detail list and last global sync timestamp.</returns>
    [HttpGet("peers/details")]
    [AllowAnonymous]
    [ServiceFilter(typeof(FederationAuthFilter))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<PeerDetailsResponseDto> GetPeersDetails()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return Ok(new PeerDetailsResponseDto());
        }

        var libraryPath = config.LibraryPath;
        var states = string.IsNullOrWhiteSpace(libraryPath)
            ? new Dictionary<string, PeerStatus>()
            : PeerStateStore.Load(libraryPath);

        var manifest = string.IsNullOrWhiteSpace(libraryPath)
            ? new Manifest()
            : ReadManifest(libraryPath);

        var moviesRoot = config.GetEffectiveMoviesRoot();
        var seriesRoot = config.GetEffectiveSeriesRoot();
        var animeRoot = config.GetEffectiveAnimeRoot();

        var peers = new List<PeerDetailDto>(config.Peers.Count);
        string? latestSyncAt = null;

        foreach (var peer in config.Peers)
        {
            states.TryGetValue(peer.Name, out var status);
            var peerSeg = StrmWriter.SanitizePeerFolderSegment(peer.Name);

            var moviesFolder = CombinePeerFolder(moviesRoot, peerSeg);
            var seriesFolder = CombinePeerFolder(seriesRoot, peerSeg);
            var animeFolder = CombinePeerFolder(animeRoot, peerSeg);

            int localMovies = 0, localSeries = 0, localAnime = 0;

            foreach (var entry in manifest.Movies.Values)
            {
                if (!string.Equals(entry.PeerName, peer.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsUnderRoot(entry.Path, animeRoot))
                {
                    localAnime++;
                }
                else
                {
                    localMovies++;
                }
            }

            foreach (var entry in manifest.Series.Values)
            {
                if (!string.Equals(entry.PeerName, peer.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsUnderRoot(entry.Path, animeRoot))
                {
                    localAnime++;
                }
                else
                {
                    localSeries++;
                }
            }

            long diskBytes = 0;
            diskBytes += DirectorySize(moviesFolder);
            diskBytes += DirectorySize(seriesFolder);
            diskBytes += DirectorySize(animeFolder);

            if (status?.LastSyncAt is not null &&
                (latestSyncAt is null ||
                 string.CompareOrdinal(status.LastSyncAt, latestSyncAt) > 0))
            {
                latestSyncAt = status.LastSyncAt;
            }

            peers.Add(new PeerDetailDto
            {
                Name = peer.Name,
                Url = peer.Url,
                Enabled = peer.Enabled,
                SyncMovies = peer.SyncMovies,
                SyncSeries = peer.SyncSeries,
                SyncAnime = peer.SyncAnime,
                HasAccessToken = !string.IsNullOrEmpty(peer.AccessToken),
                Online = status?.Online ?? false,
                LastSeen = status?.LastSeen,
                Version = status?.Version,
                LastSyncAt = status?.LastSyncAt,
                LastSyncStatus = status?.LastSyncStatus ?? "never",
                LastSyncError = status?.LastSyncError,
                LastSyncDurationMs = status?.LastSyncDurationMs ?? 0,
                PeerMovieCount = status?.MovieCount ?? 0,
                PeerSeriesCount = status?.SeriesCount ?? 0,
                LocalMovieCount = localMovies,
                LocalSeriesCount = localSeries,
                LocalAnimeCount = localAnime,
                LocalDiskBytes = diskBytes,
                MoviesFolder = moviesFolder,
                SeriesFolder = seriesFolder,
                AnimeFolder = animeFolder
            });
        }

        _logger.LogInformation(
            "JellyFed: GET /peers/details — {ConfigPeers} configured, {ReturnedPeers} returned, lastGlobalSyncAt={Sync}.",
            config.Peers.Count,
            peers.Count,
            latestSyncAt ?? "(none)");

        return Ok(new PeerDetailsResponseDto
        {
            Peers = peers,
            LastGlobalSyncAt = latestSyncAt
        });
    }

    /// <summary>
    /// Tests a peer URL + federation token without adding it. Used by the admin UI's
    /// "Test connection" button before confirming an add.
    /// </summary>
    /// <param name="request">Candidate peer URL and federation token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Reachability flag, reported version and a friendly message.</returns>
    [HttpPost("peers/test")]
    [AllowAnonymous]
    [ServiceFilter(typeof(FederationAuthFilter))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> TestPeerAsync(
        [FromBody] AddPeerRequestDto request,
        CancellationToken cancellationToken)
    {
        if (request is null ||
            string.IsNullOrWhiteSpace(request.Url) ||
            string.IsNullOrWhiteSpace(request.FederationToken))
        {
            return BadRequest(new { status = "error", message = "Url and FederationToken are required." });
        }

        var urlTrim = request.Url.Trim();
        var (reachable, version) = await _peerClient
            .HealthCheckAsync(urlTrim, request.FederationToken, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "JellyFed: test peer {Url} — reachable={Reachable}, version={Version}.",
            urlTrim,
            reachable,
            version ?? "unknown");

        return Ok(new
        {
            status = reachable ? "ok" : "unreachable",
            reachable,
            version,
            message = reachable
                ? $"Peer reachable (JellyFed v{version ?? "?"})."
                : "Peer unreachable — check URL / token / that JellyFed is installed and running on the remote side."
        });
    }

    /// <summary>
    /// Adds a new peer from the admin UI. Performs a health check on the provided URL+token
    /// and stores the peer even if unreachable (admin may be configuring before the peer is online).
    /// </summary>
    /// <param name="request">Peer to add (name, url, token, toggles).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Created peer detail or conflict.</returns>
    [HttpPost("peers")]
    [AllowAnonymous]
    [ServiceFilter(typeof(FederationAuthFilter))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> AddPeerAsync(
        [FromBody] AddPeerRequestDto request,
        CancellationToken cancellationToken)
    {
        if (request is null ||
            string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.Url) ||
            string.IsNullOrWhiteSpace(request.FederationToken))
        {
            return BadRequest("Name, Url and FederationToken are required.");
        }

        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Plugin configuration unavailable.");
        }

        var nameTrim = request.Name.Trim();
        var urlTrim = request.Url.Trim();

        if (config.Peers.Any(p => string.Equals(p.Name, nameTrim, StringComparison.OrdinalIgnoreCase)))
        {
            return Conflict(new { status = "error", message = "A peer with this name already exists." });
        }

        if (config.Peers.Any(p => string.Equals(p.Url, urlTrim, StringComparison.OrdinalIgnoreCase)))
        {
            return Conflict(new { status = "error", message = "A peer with this URL already exists." });
        }

        config.BlockedPeerUrls.RemoveAll(u => string.Equals(u, urlTrim, StringComparison.OrdinalIgnoreCase));

        var (reachable, version) = await _peerClient
            .HealthCheckAsync(urlTrim, request.FederationToken, cancellationToken)
            .ConfigureAwait(false);

        config.Peers.Add(new PeerConfiguration
        {
            Name = nameTrim,
            Url = urlTrim,
            FederationToken = request.FederationToken,
            Enabled = request.Enabled,
            SyncMovies = request.SyncMovies,
            SyncSeries = request.SyncSeries,
            SyncAnime = request.SyncAnime,
            AccessToken = null
        });

        Plugin.Instance!.SaveConfiguration();
        _logger.LogInformation(
            "JellyFed: peer {PeerName} added manually (reachable={Reachable}, version={Version}).",
            nameTrim,
            reachable,
            version ?? "unknown");

        return Ok(new
        {
            status = "ok",
            reachable,
            version
        });
    }

    /// <summary>
    /// Updates a peer in place. Supports renaming (moves per-peer folders and rewrites manifest paths),
    /// URL/token edits and per-type sync toggles.
    /// </summary>
    /// <param name="name">Current peer name (from the URL segment).</param>
    /// <param name="request">Partial update payload.</param>
    /// <returns>Status of the update.</returns>
    [HttpPatch("peer/{name}")]
    [AllowAnonymous]
    [ServiceFilter(typeof(FederationAuthFilter))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Folder paths are composed from admin-configured roots and sanitized peer segments; peer name is not a filesystem path.")]
    public ActionResult UpdatePeer(
        [FromRoute] string name,
        [FromBody] UpdatePeerRequestDto request)
    {
        if (request is null)
        {
            return BadRequest("Body is required.");
        }

        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Plugin configuration unavailable.");
        }

        var peer = config.Peers.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (peer is null)
        {
            return NotFound();
        }

        var oldName = peer.Name;

        if (!string.IsNullOrWhiteSpace(request.Name) &&
            !string.Equals(request.Name.Trim(), oldName, StringComparison.Ordinal))
        {
            var newName = request.Name.Trim();

            if (config.Peers.Any(p =>
                    p != peer &&
                    string.Equals(p.Name, newName, StringComparison.OrdinalIgnoreCase)))
            {
                return Conflict(new { status = "error", message = "Another peer already uses this name." });
            }

            RenamePeerOnDisk(config, oldName, newName);
            peer.Name = newName;
        }

        if (!string.IsNullOrWhiteSpace(request.Url))
        {
            var newUrl = request.Url.Trim();
            if (config.Peers.Any(p =>
                    p != peer &&
                    string.Equals(p.Url, newUrl, StringComparison.OrdinalIgnoreCase)))
            {
                return Conflict(new { status = "error", message = "Another peer already uses this URL." });
            }

            peer.Url = newUrl;
        }

        if (!string.IsNullOrWhiteSpace(request.FederationToken))
        {
            peer.FederationToken = request.FederationToken;
        }

        if (request.Enabled.HasValue)
        {
            peer.Enabled = request.Enabled.Value;
        }

        if (request.SyncMovies.HasValue)
        {
            peer.SyncMovies = request.SyncMovies.Value;
        }

        if (request.SyncSeries.HasValue)
        {
            peer.SyncSeries = request.SyncSeries.Value;
        }

        if (request.SyncAnime.HasValue)
        {
            peer.SyncAnime = request.SyncAnime.Value;
        }

        Plugin.Instance!.SaveConfiguration();
        _logger.LogInformation("JellyFed: peer {OldName} updated (now {NewName}).", oldName, peer.Name);
        return Ok(new { status = "ok" });
    }

    /// <summary>
    /// Runs a sync pass for a single peer and returns the summary inline (no task queue).
    /// </summary>
    /// <param name="name">Peer name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Sync result summary.</returns>
    [HttpPost("peer/{name}/sync")]
    [AllowAnonymous]
    [ServiceFilter(typeof(FederationAuthFilter))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PeerSyncResultDto>> SyncPeerAsync(
        [FromRoute] string name,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var peer = config?.Peers.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (peer is null)
        {
            return NotFound();
        }

        _logger.LogInformation("JellyFed: per-peer sync requested for {PeerName}.", peer.Name);
        var result = await _syncTask.SyncPeerAsync(peer, cancellationToken).ConfigureAwait(false);

        return Ok(new PeerSyncResultDto
        {
            Name = peer.Name,
            Status = result.Error is null ? "ok" : "failed",
            AddedMovies = result.AddedMovies,
            AddedSeries = result.AddedSeries,
            SkippedMovies = result.SkippedMovies,
            SkippedSeries = result.SkippedSeries,
            Pruned = result.Pruned,
            DurationMs = result.DurationMs,
            Error = result.Error
        });
    }

    /// <summary>
    /// Purges all .strm files for a single peer by name (route-based form of <see cref="PurgePeerCatalog"/>).
    /// Keeps the peer in the configuration.
    /// </summary>
    /// <param name="name">Peer name.</param>
    /// <returns>Deletion summary.</returns>
    [HttpPost("peer/{name}/purge")]
    [AllowAnonymous]
    [ServiceFilter(typeof(FederationAuthFilter))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult PurgePeerByName([FromRoute] string name)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(config.LibraryPath))
        {
            return BadRequest("LibraryPath is not configured.");
        }

        var peer = config.Peers.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (peer is null)
        {
            return NotFound();
        }

        var summary = PurgePeerData(config, peer.Name);

        // Reset the local counters in PeerStatus so the UI shows "never" after purge.
        var states = PeerStateStore.Load(config.LibraryPath);
        if (states.TryGetValue(peer.Name, out var status))
        {
            status.LastSyncStatus = "never";
            status.LastSyncAt = null;
            status.LastSyncError = null;
            status.LastSyncDurationMs = 0;
            PeerStateStore.Save(config.LibraryPath, states);
        }

        return Ok(new
        {
            status = "ok",
            deletedMovies = summary.DeletedMovies,
            deletedSeries = summary.DeletedSeries
        });
    }

    /// <summary>
    /// Removes a peer entirely: purge content, revoke its access token, drop from config and
    /// blacklist its URL so it can't auto-register again.
    /// </summary>
    /// <param name="name">Peer name.</param>
    /// <returns>Deletion summary.</returns>
    [HttpPost("peer/{name}/remove")]
    [AllowAnonymous]
    [ServiceFilter(typeof(FederationAuthFilter))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Peer name is used only as a dictionary key and sanitized folder segment, never as a direct filesystem path.")]
    public ActionResult RemovePeer([FromRoute] string name)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(config.LibraryPath))
        {
            return BadRequest("LibraryPath is not configured.");
        }

        var peer = config.Peers.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (peer is null)
        {
            return NotFound();
        }

        var summary = PurgePeerData(config, peer.Name);

        // Forget any stored state for this peer (online/offline, counts, last sync).
        var states = PeerStateStore.Load(config.LibraryPath);
        if (states.Remove(peer.Name))
        {
            PeerStateStore.Save(config.LibraryPath, states);
        }

        if (!string.IsNullOrWhiteSpace(peer.Url) &&
            !config.BlockedPeerUrls.Any(u => string.Equals(u, peer.Url, StringComparison.OrdinalIgnoreCase)))
        {
            config.BlockedPeerUrls.Add(peer.Url);
        }

        // Revoke the per-peer access token so it can no longer hit our API.
        peer.AccessToken = null;
        config.Peers.Remove(peer);
        Plugin.Instance!.SaveConfiguration();

        _logger.LogInformation(
            "JellyFed: peer {PeerName} removed (blocked URL, revoked token, {Movies} movies + {Series} series purged).",
            peer.Name,
            summary.DeletedMovies,
            summary.DeletedSeries);

        return Ok(new
        {
            status = "ok",
            deletedMovies = summary.DeletedMovies,
            deletedSeries = summary.DeletedSeries,
            blockedUrl = peer.Url
        });
    }

    /// <summary>
    /// Streams a media file directly, authenticated via federation token query parameter.
    /// Used by .strm files — players request this URL directly, no Bearer header support needed.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="token">The federation token of this instance.</param>
    /// <returns>The raw media file with range-request support.</returns>
    [HttpGet("stream/{itemId}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Path is retrieved from the Jellyfin library manager by GUID, not from user input directly.")]
    public ActionResult StreamItem([FromRoute] string itemId, [FromQuery] string? token)
    {
        if (!ValidateStreamToken(token))
        {
            return Unauthorized();
        }

        if (!Guid.TryParse(itemId, out var guid))
        {
            return BadRequest("Invalid item ID.");
        }

        // If a Jellyfin API key is configured, redirect through Jellyfin's native pipeline.
        // This enables server-side transcoding so all clients (browsers, apps) can play
        // any format — Jellyfin decides whether to direct-play or transcode automatically.
        var apiKey = Plugin.Instance?.Configuration.JellyfinApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            // Static=true → source Jellyfin serves the raw file with proper range request
            // support. This allows the client's FFmpeg to seek within the stream (for HLS
            // transcoding). Static=false would start a transcoding session on the source,
            // which doesn't support range-based seeking.
            return Redirect($"{GetBaseUrl()}/Videos/{itemId}/stream?api_key={apiKey}&Static=true");
        }

        // Fallback: serve the file directly (no transcoding — client must support the format).
        var item = _libraryManager.GetItemById(guid);
        if (item is null || string.IsNullOrEmpty(item.Path) || !System.IO.File.Exists(item.Path))
        {
            return NotFound();
        }

        return PhysicalFile(item.Path, GetMimeType(item.Path), enableRangeProcessing: true);
    }

    /// <summary>
    /// Serves an item image directly, authenticated via federation token query parameter.
    /// Avoids embedding a Jellyfin API key in catalog URLs.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="imageType">The image type: Primary or Backdrop.</param>
    /// <param name="token">The federation token of this instance.</param>
    /// <returns>The image file.</returns>
    [HttpGet("image/{itemId}/{imageType}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Path is retrieved from the Jellyfin library manager by GUID, not from user input directly.")]
    public ActionResult GetItemImage(
        [FromRoute] string itemId,
        [FromRoute] string imageType,
        [FromQuery] string? token)
    {
        if (!ValidateStreamToken(token))
        {
            return Unauthorized();
        }

        if (!Guid.TryParse(itemId, out var guid))
        {
            return BadRequest("Invalid item ID.");
        }

        var type = imageType switch
        {
            "Primary" => (ImageType?)ImageType.Primary,
            "Backdrop" => ImageType.Backdrop,
            _ => null
        };

        if (type is null)
        {
            return BadRequest("Invalid image type. Use Primary or Backdrop.");
        }

        var item = _libraryManager.GetItemById(guid);
        if (item is null || !HasImage(item, type.Value))
        {
            return NotFound();
        }

        var imageInfo = item.ImageInfos?.FirstOrDefault(img => img.Type == type.Value);
        if (imageInfo is null || string.IsNullOrEmpty(imageInfo.Path) || !System.IO.File.Exists(imageInfo.Path))
        {
            return NotFound();
        }

        var mimeType = Path.GetExtension(imageInfo.Path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };

        return PhysicalFile(imageInfo.Path, mimeType);
    }

    /// <summary>
    /// Emergency reset: generates a new federation token, removes all peers and
    /// deletes all synced .strm files. Remote peers with the old token will receive
    /// 401 errors and auto-clean on their next sync cycle.
    /// </summary>
    /// <returns>The new federation token.</returns>
    [HttpPost("network/reset")]
    [AllowAnonymous]
    [ServiceFilter(typeof(FederationAuthFilter))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult ResetNetwork()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Plugin configuration unavailable.");
        }

        var libraryPath = config.LibraryPath;

        // Remove federated items from Jellyfin's library index before deleting files.
        // Wrapped in try-catch: library API may vary across Jellyfin versions; files are
        // deleted from disk regardless, and Jellyfin will remove stale entries on next scan.
        if (!string.IsNullOrWhiteSpace(libraryPath))
        {
            try
            {
                var manifest = ReadManifest(libraryPath);
                var allPaths = manifest.Movies.Values.Select(e => e.Path)
                    .Concat(manifest.Series.Values.Select(e => e.Path))
                    .ToList();
                RemoveLibraryItems(allPaths);
                foreach (var p in allPaths)
                {
                    if (Directory.Exists(p))
                    {
                        Directory.Delete(p, true);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "JellyFed: network reset — could not remove library items from Jellyfin index (non-fatal, will clean up on next scan).");
            }
        }

        // Remove per-peer trees and legacy folders, then manifest / peer state on disk.
        foreach (var peer in config.Peers.ToList())
        {
            FederatedPathHelper.TryDeletePeerContentFolders(config, peer.Name);
        }

        if (!string.IsNullOrWhiteSpace(libraryPath))
        {
            foreach (var legacy in new[]
                     {
                         Path.Combine(libraryPath, "Films"),
                         Path.Combine(libraryPath, "Series"),
                         Path.Combine(libraryPath, "Animes")
                     })
            {
                if (Directory.Exists(legacy))
                {
                    Directory.Delete(legacy, true);
                }
            }

            var manifestPath = Path.Combine(libraryPath, ".jellyfed-manifest.json");
            if (System.IO.File.Exists(manifestPath))
            {
                System.IO.File.Delete(manifestPath);
            }

            var peersStatePath = Path.Combine(libraryPath, ".jellyfed-peers.json");
            if (System.IO.File.Exists(peersStatePath))
            {
                System.IO.File.Delete(peersStatePath);
            }
        }

        // Generate a new federation token and clear the peer list.
        config.FederationToken = GenerateAccessToken();
        config.Peers.Clear();
        config.BlockedPeerUrls.Clear();
        Plugin.Instance!.SaveConfiguration();

        _logger.LogWarning(
            "JellyFed: network reset — new federation token generated, all peers and STRMs cleared.");

        return Ok(new { status = "ok", newToken = config.FederationToken });
    }

    /// <summary>
    /// Finds and removes all Jellyfin library items whose path starts with one of
    /// the given folder paths. Files have already been deleted from disk; we pass
    /// <c>DeleteFileLocation = false</c> so Jellyfin only removes the DB record.
    /// Series are deleted top-level; Jellyfin cascades to seasons and episodes.
    /// </summary>
    private void RemoveLibraryItems(List<string> folderPaths)
    {
        if (folderPaths.Count == 0)
        {
            return;
        }

        var deleteOptions = new DeleteOptions { DeleteFileLocation = false };

        // Query only top-level media types — deleting a Series cascades to seasons/episodes.
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true,
            IsVirtualItem = false
        });

        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Path))
            {
                continue;
            }

            var underDeletedFolder = folderPaths.Any(p =>
                item.Path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

            if (!underDeletedFolder)
            {
                continue;
            }

            try
            {
                _libraryManager.DeleteItem(item, deleteOptions);
                _logger.LogInformation("JellyFed: removed library item '{Name}' ({Path})", item.Name, item.Path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "JellyFed: failed to remove library item '{Name}' ({Path})", item.Name, item.Path);
            }
        }
    }

    private static int? TicksToMinutes(long? ticks)
        => ticks.HasValue ? (int)(ticks.Value / TimeSpan.TicksPerMinute) : null;

    private static bool HasImage(BaseItem item, ImageType imageType)
        => item.HasImage(imageType, 0);

    private static string GenerateAccessToken() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Extracts codec and all audio/subtitle track info from a BaseItem.
    /// Called for every movie and episode exported in the catalog so the receiving
    /// server can write complete &lt;fileinfo&gt;&lt;streamdetails&gt; into NFO files.
    /// </summary>
    private (string? Container, string? VideoCodec, int? Width, int? Height, string? AudioCodec, IReadOnlyList<MediaStreamInfoDto> MediaStreams) ExtractStreamInfo(BaseItem item)
    {
        if (item is not Video video)
        {
            return (null, null, null, null, null, []);
        }

        var container = video.Container;
        string? videoCodec = null;
        int? width = null;
        int? height = null;
        string? primaryAudioCodec = null;
        var mediaStreams = new List<MediaStreamInfoDto>();

        try
        {
            var streams = video.GetMediaStreams();

            foreach (var s in streams)
            {
                if (s.Type == MediaStreamType.Video && videoCodec is null)
                {
                    videoCodec = s.Codec;
                    width = s.Width;
                    height = s.Height;
                }
                else if (s.Type == MediaStreamType.Audio)
                {
                    if (primaryAudioCodec is null)
                    {
                        primaryAudioCodec = s.Codec;
                    }

                    mediaStreams.Add(new MediaStreamInfoDto
                    {
                        Type = "Audio",
                        Codec = s.Codec,
                        Language = s.Language,
                        Title = s.Title,
                        IsDefault = s.IsDefault,
                        IsForced = s.IsForced
                    });
                }
                else if (s.Type == MediaStreamType.Subtitle)
                {
                    mediaStreams.Add(new MediaStreamInfoDto
                    {
                        Type = "Subtitle",
                        Codec = s.Codec,
                        Language = s.Language,
                        Title = s.Title,
                        IsDefault = s.IsDefault,
                        IsForced = s.IsForced
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "JellyFed: could not read media streams for item {Id}", item.Id);
        }

        return (container, videoCodec, width, height, primaryAudioCodec, mediaStreams);
    }

    /// <summary>
    /// Builds the base URL for this request, honouring X-Forwarded-Proto when behind a reverse proxy.
    /// </summary>
    private string GetBaseUrl()
    {
        var scheme = Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? Request.Scheme;
        return $"{scheme}://{Request.Host}{Request.PathBase.Value?.TrimEnd('/')}";
    }

    /// <summary>
    /// Returns an image URL. Uses the native Jellyfin Images API when an API key is available
    /// (avoids the JellyFed proxy hop and is more reliable), otherwise falls back to the
    /// JellyFed proxy endpoint authenticated with the federation token.
    /// </summary>
    private static string ImageUrl(string baseUrl, Guid itemId, string imageType, string token, string? apiKey)
        => !string.IsNullOrWhiteSpace(apiKey)
            ? $"{baseUrl}/Items/{itemId:N}/Images/{imageType}?api_key={apiKey}"
            : $"{baseUrl}/JellyFed/image/{itemId:N}/{imageType}?token={token}";

    private static bool ValidateStreamToken(string? token)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(config.FederationToken))
        {
            return false;
        }

        return string.Equals(token, config.FederationToken, StringComparison.Ordinal);
    }

    private static string GetMimeType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mkv" => "video/x-matroska",
            ".mp4" or ".m4v" => "video/mp4",
            ".avi" => "video/x-msvideo",
            ".mov" => "video/quicktime",
            ".wmv" => "video/x-ms-wmv",
            ".ts" => "video/mp2t",
            ".webm" => "video/webm",
            _ => "application/octet-stream"
        };
    }

    private static Manifest ReadManifest(string libraryPath)
    {
        var path = Path.Combine(libraryPath, ".jellyfed-manifest.json");
        if (!System.IO.File.Exists(path))
        {
            return new Manifest();
        }

        try
        {
            var json = System.IO.File.ReadAllText(path);
            return JsonSerializer.Deserialize<Manifest>(json, ManifestJsonOptions) ?? new Manifest();
        }
        catch
        {
            return new Manifest();
        }
    }

    private static void WriteManifest(string libraryPath, Manifest manifest)
    {
        var path = Path.Combine(libraryPath, ".jellyfed-manifest.json");
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(manifest, ManifestJsonOptions));
    }

    private static string? CombinePeerFolder(string root, string peerSeg)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        return Path.Combine(
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            peerSeg);
    }

    private static bool IsUnderRoot(string? path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(r + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Path is built from admin-configured roots + sanitized peer segments, not user input.")]
    private static long DirectorySize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return 0;
        }

        try
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
#pragma warning disable CA1031 // Best effort: ignore transient IO errors (file deleted/locked).
                catch
                {
                    // File may have been deleted between enumeration and FileInfo access.
                }
#pragma warning restore CA1031
            }

            return total;
        }
#pragma warning disable CA1031 // Best effort: directory may have become inaccessible.
        catch
        {
            return 0;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Shared purge pipeline used by both the name-based <c>/peer/{name}/purge</c> endpoint and
    /// <c>/peer/{name}/remove</c>. Removes all manifest entries, deletes .strm folders from disk,
    /// clears Jellyfin library rows and deletes the per-peer subfolders.
    /// </summary>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Paths come from the plugin manifest written by this plugin and admin-configured roots.")]
    private (int DeletedMovies, int DeletedSeries) PurgePeerData(PluginConfiguration config, string peerName)
    {
        var manifest = ReadManifest(config.LibraryPath);

        var movieKeys = manifest.Movies
            .Where(kv => string.Equals(kv.Value.PeerName, peerName, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();

        var seriesKeys = manifest.Series
            .Where(kv => string.Equals(kv.Value.PeerName, peerName, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();

        var deletedPaths = movieKeys.Select(k => manifest.Movies[k].Path)
            .Concat(seriesKeys.Select(k => manifest.Series[k].Path))
            .ToList();

        foreach (var key in movieKeys)
        {
            var p = manifest.Movies[key].Path;
            if (Directory.Exists(p))
            {
                Directory.Delete(p, true);
            }

            manifest.Movies.Remove(key);
        }

        foreach (var key in seriesKeys)
        {
            var p = manifest.Series[key].Path;
            if (Directory.Exists(p))
            {
                Directory.Delete(p, true);
            }

            manifest.Series.Remove(key);
        }

        WriteManifest(config.LibraryPath, manifest);
        RemoveLibraryItems(deletedPaths);
        FederatedPathHelper.TryDeletePeerContentFolders(config, peerName);

        return (movieKeys.Count, seriesKeys.Count);
    }

    /// <summary>
    /// Renames a peer's folder under each effective root (movies / series / anime) and rewrites
    /// all manifest entries referencing the old path prefix. Updates the peer-state store key.
    /// </summary>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Folder paths are composed from admin-configured roots + sanitized peer segments (SanitizePeerFolderSegment).")]
    private void RenamePeerOnDisk(PluginConfiguration config, string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(config.LibraryPath))
        {
            return;
        }

        var oldSeg = StrmWriter.SanitizePeerFolderSegment(oldName);
        var newSeg = StrmWriter.SanitizePeerFolderSegment(newName);
        if (string.Equals(oldSeg, newSeg, StringComparison.Ordinal))
        {
            // Segments identical (e.g. only casing changed after sanitization) — still update
            // manifest PeerName below but skip the disk moves.
        }

        var roots = new[]
        {
            config.GetEffectiveMoviesRoot(),
            config.GetEffectiveSeriesRoot(),
            config.GetEffectiveAnimeRoot()
        };

        var renames = new List<(string OldDir, string NewDir)>();

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var oldDir = Path.Combine(r, oldSeg);
            var newDir = Path.Combine(r, newSeg);

            if (!string.Equals(oldDir, newDir, StringComparison.Ordinal) && Directory.Exists(oldDir))
            {
                if (Directory.Exists(newDir))
                {
                    _logger.LogWarning(
                        "JellyFed rename: target folder already exists, skipping move: {NewDir}",
                        newDir);
                    continue;
                }

                try
                {
                    Directory.Move(oldDir, newDir);
                    renames.Add((oldDir, newDir));
                    _logger.LogInformation("JellyFed rename: moved {Old} -> {New}", oldDir, newDir);
                }
#pragma warning disable CA1031 // Best effort: a failed move is logged but doesn't block the config update.
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "JellyFed rename: could not move {Old} -> {New}", oldDir, newDir);
                }
#pragma warning restore CA1031
            }
        }

        // Rewrite manifest paths and PeerName for every entry of the old peer.
        var manifest = ReadManifest(config.LibraryPath);
        bool manifestChanged = false;

        foreach (var dict in new[] { manifest.Movies, manifest.Series })
        {
            foreach (var entry in dict.Values)
            {
                if (!string.Equals(entry.PeerName, oldName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                entry.PeerName = newName;
                foreach (var (oldDir, newDir) in renames)
                {
                    if (entry.Path.StartsWith(oldDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(entry.Path, oldDir, StringComparison.OrdinalIgnoreCase))
                    {
                        entry.Path = string.Equals(entry.Path, oldDir, StringComparison.OrdinalIgnoreCase)
                            ? newDir
                            : string.Concat(newDir, entry.Path.AsSpan(oldDir.Length));
                    }
                }

                manifestChanged = true;
            }
        }

        if (manifestChanged)
        {
            WriteManifest(config.LibraryPath, manifest);
        }

        // Rename the PeerStateStore key so UI stats survive the rename.
        var states = PeerStateStore.Load(config.LibraryPath);
        if (states.Remove(oldName, out var existing))
        {
            states[newName] = existing;
            PeerStateStore.Save(config.LibraryPath, states);
        }
    }
}
