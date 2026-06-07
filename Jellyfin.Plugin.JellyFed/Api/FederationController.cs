using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyFed;
using Jellyfin.Plugin.JellyFed.Api.Dto;
using Jellyfin.Plugin.JellyFed.Audit;
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
[Route(FederationProtocol.RoutePrefix)]
[Produces(MediaTypeNames.Application.Json)]
public class FederationController : ControllerBase
{
    private static readonly ExtractedStreamInfo EmptyStreamInfo =
        new(null, null, null, null, null, null, null, null, null, []);

    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;
    private readonly FederationSyncTask _syncTask;
    private readonly PeerClient _peerClient;
    private readonly StrmWriter _strmWriter;
    private readonly AuditLogService _auditLogService;
    private readonly ILogger<FederationController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="taskManager">Instance of the <see cref="ITaskManager"/> interface.</param>
    /// <param name="syncTask">Instance of the <see cref="FederationSyncTask"/> used for per-peer sync.</param>
    /// <param name="peerClient">Instance of the <see cref="PeerClient"/> used for health checks.</param>
    /// <param name="strmWriter">Instance of the <see cref="StrmWriter"/> used for local materialization updates.</param>
    /// <param name="auditLogService">Audit service.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{FederationController}"/> interface.</param>
    public FederationController(
        ILibraryManager libraryManager,
        ITaskManager taskManager,
        FederationSyncTask syncTask,
        PeerClient peerClient,
        StrmWriter strmWriter,
        AuditLogService auditLogService,
        ILogger<FederationController> logger)
    {
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _syncTask = syncTask;
        _peerClient = peerClient;
        _strmWriter = strmWriter;
        _auditLogService = auditLogService;
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
    /// Returns the current local JellyFed version metadata.
    /// </summary>
    /// <returns>Version, protocol and schema information for this instance.</returns>
    [HttpGet("version")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetVersion()
    {
        var config = Plugin.Instance?.Configuration;

        return Ok(new
        {
            version = Plugin.Instance?.Version.ToString(3) ?? "unknown",
            protocolVersion = FederationProtocol.CurrentProtocolVersion,
            schemaVersion = config?.SchemaVersion ?? FederationProtocol.CurrentSchemaVersion,
            instanceId = config?.InstanceId,
            serverName = string.IsNullOrWhiteSpace(config?.SelfName)
                ? Plugin.Instance?.Name ?? "JellyFed"
                : config.SelfName
        });
    }

    /// <summary>
    /// Returns handshake-oriented system information for federation peers.
    /// </summary>
    /// <returns>Stable instance ID, schema/protocol versions and capabilities.</returns>
    [HttpGet("system/info")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<FederationSystemInfoDto> GetSystemInfo()
    {
        var config = Plugin.Instance?.Configuration;
        var serverName = string.IsNullOrWhiteSpace(config?.SelfName)
            ? Plugin.Instance?.Name ?? "JellyFed"
            : config.SelfName;

        return Ok(new FederationSystemInfoDto
        {
            Name = Plugin.Instance?.Name ?? "JellyFed",
            Version = Plugin.Instance?.Version.ToString(3) ?? "unknown",
            InstanceId = config?.InstanceId,
            ServerName = serverName,
            ProtocolVersion = FederationProtocol.CurrentProtocolVersion,
            SchemaVersion = config?.SchemaVersion ?? FederationProtocol.CurrentSchemaVersion,
            Capabilities = FederationProtocol.Capabilities
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
        var baseUrl = GetAdvertisedBaseUrl();
        var token = GetEffectivePeerTokenOrGlobal();
        var apiKey = Plugin.Instance?.Configuration?.JellyfinApiKey;

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

        _auditLogService.WriteRequestEvent(
            HttpContext,
            AuditLogCategories.PeerAccess,
            "catalog.exported",
            $"Exported {page.Length} catalog items to a federated peer.",
            statusCode: StatusCodes.Status200OK,
            details: new { total = items.Count, returned = page.Length, type = type ?? "all", since, offset, limit });

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

        var baseUrl = GetAdvertisedBaseUrl();
        var token = GetEffectivePeerTokenOrGlobal();
        var apiKey = Plugin.Instance?.Configuration?.JellyfinApiKey;

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
                    StreamUrl = $"{baseUrl}{FederationProtocol.ToPath($"stream/{ep.Id:N}")}?token={token}",
                    Container = epInfo.Container,
                    VideoCodec = epInfo.VideoCodec,
                    Width = epInfo.Width,
                    Height = epInfo.Height,
                    AudioCodec = epInfo.AudioCodec,
                    BitRate = epInfo.BitRate,
                    SizeBytes = epInfo.SizeBytes,
                    VideoRange = epInfo.VideoRange,
                    MediaStreams = epInfo.MediaStreams
                });
            }

            response.Seasons.Add(seasonDto);
        }

        _logger.LogInformation(
            "GET /JellyFed/catalog/series/{SeriesId}/seasons — {SeasonCount} seasons",
            seriesId,
            response.Seasons.Count);

        _auditLogService.WriteRequestEvent(
            HttpContext,
            AuditLogCategories.PeerAccess,
            "catalog.series-seasons.exported",
            $"Exported seasons and episodes for series {seriesId}.",
            statusCode: StatusCodes.Status200OK,
            details: new { seriesId, seasonCount = response.Seasons.Count });

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
            var info = kind == BaseItemKind.Movie ? ExtractStreamInfo(item) : EmptyStreamInfo;

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
                    ? $"{baseUrl}{FederationProtocol.ToPath($"stream/{item.Id:N}")}?token={token}"
                    : null,
                AddedAt = item.DateCreated.ToString("O", CultureInfo.InvariantCulture),
                UpdatedAt = item.DateModified.ToString("O", CultureInfo.InvariantCulture),
                Container = info.Container,
                VideoCodec = info.VideoCodec,
                Width = info.Width,
                Height = info.Height,
                AudioCodec = info.AudioCodec,
                BitRate = info.BitRate,
                SizeBytes = info.SizeBytes,
                VideoRange = info.VideoRange,
                Edition = info.Edition,
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

        return Ok(new PeersResponseDto
        {
            Peers = peers,
            SelfDiscoverable = config.Discoverable
        });
    }

    /// <summary>
    /// Returns the discovery announcement for this instance.
    /// The payload includes this instance plus discoverable direct peers only;
    /// discovered suggestions are never relayed recursively.
    /// </summary>
    /// <returns>Discovery announcement for direct peers.</returns>
    [HttpGet("discovery")]
    [AllowAnonymous]
    [ServiceFilter(typeof(FederationAuthFilter))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<DiscoveryResponseDto> GetDiscovery()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return Ok(new DiscoveryResponseDto());
        }

        var states = string.IsNullOrWhiteSpace(config.LibraryPath)
            ? new Dictionary<string, PeerStatus>()
            : PeerStateStore.Load(config.LibraryPath);

        var directPeers = config.Peers
            .Where(p => p.Enabled)
            .Select(peer =>
            {
                states.TryGetValue(peer.Name, out var status);
                return new { Peer = peer, Status = status };
            })
            .Where(x => x.Status?.Discoverable == true)
            .Select(x => new DiscoveryPeerDto
            {
                Name = x.Peer.Name,
                Url = x.Peer.Url,
                FederationToken = GetDiscoveryToken(x.Peer),
                Version = x.Status?.Version,
                Discoverable = true
            })
            .Where(p => !string.IsNullOrWhiteSpace(p.Url) && !string.IsNullOrWhiteSpace(p.FederationToken))
            .GroupBy(p => NormalizeUrl(p.Url), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.Name)
            .ToList();

        return Ok(new DiscoveryResponseDto
        {
            Self = BuildSelfDiscoveryDto(config),
            DirectPeers = directPeers
        });
    }

    /// <summary>
    /// Handles a legacy federation registration request from a remote instance.
    /// v1 discovery is manual-add only: unknown peers are never auto-created here.
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
            _auditLogService.WriteSecurityEvent(
                "peer.registration.blocked",
                $"Blocked registration attempt from {request.Name}.",
                HttpContext,
                details: new { request.Name, request.Url });
            return Ok(new RegisterPeerResponseDto { Status = "blocked", Message = "This peer has been removed by an admin." });
        }

        var existing = config.Peers.FirstOrDefault(p =>
            string.Equals(p.Url, request.Url, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            _logger.LogInformation(
                "JellyFed: registration from {Name} ({Url}) requires manual admin approval; no peer created.",
                request.Name,
                request.Url);

            return Ok(new RegisterPeerResponseDto
            {
                Status = "manual_add_required",
                Message = "Manual admin approval required. Add this peer from the Peers page to enable sync.",
                AccessToken = null
            });
        }

        PeerIdentity.EnsurePeerId(existing);

        // Existing manually-added peer: issue a token if not already done, reuse otherwise.
        // Regenerating on every re-registration would invalidate in-flight requests.
        if (string.IsNullOrEmpty(existing.AccessToken))
        {
            existing.AccessToken = GenerateAccessToken();
            _logger.LogInformation("JellyFed: issued access token for existing peer {Name} ({Url}).", request.Name, request.Url);
        }

        if (string.IsNullOrWhiteSpace(existing.DiscoveryToken))
        {
            existing.DiscoveryToken = request.FederationToken;
        }

        Plugin.Instance!.SaveConfiguration();

        return Ok(new RegisterPeerResponseDto
        {
            Status = "ok",
            Message = "Peer already approved.",
            AccessToken = existing.AccessToken
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

        var manifest = ManifestStore.Load(config.LibraryPath);

        var stats = new Dictionary<string, PeerCatalogStatsDto>(StringComparer.Ordinal);

        foreach (var entry in manifest.Movies.Values)
        {
            foreach (var peerName in EnumerateSourcePeers(entry))
            {
                if (!stats.TryGetValue(peerName, out var s))
                {
                    s = new PeerCatalogStatsDto { Name = peerName };
                    stats[peerName] = s;
                }

                s.MovieCount++;
            }
        }

        foreach (var entry in manifest.Series.Values)
        {
            foreach (var peerName in EnumerateSourcePeers(entry))
            {
                if (!stats.TryGetValue(peerName, out var s))
                {
                    s = new PeerCatalogStatsDto { Name = peerName };
                    stats[peerName] = s;
                }

                s.SeriesCount++;
            }
        }

        return Ok(new ManifestStatsDto { Peers = [.. stats.Values.OrderBy(p => p.Name)] });
    }

    /// <summary>
    /// Purges all synced .strm files for a given peer from the manifest and filesystem.
    /// </summary>
    /// <param name="request">The peer name to purge.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of deleted movies and series.</returns>
    [HttpPost("peer/purge")]
    [AllowAnonymous]
    [ServiceFilter(typeof(FederationAuthFilter))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> PurgePeerCatalog(
        [FromBody] PurgePeerCatalogRequestDto request,
        CancellationToken cancellationToken)
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

        var name = request.PeerName;
        var summary = await PurgePeerDataAsync(config, name, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "JellyFed: purged catalog for peer {PeerName} — {MovieCount} movies, {SeriesCount} series deleted.",
            name,
            summary.DeletedMovies,
            summary.DeletedSeries);

        return Ok(new { status = "ok", deletedMovies = summary.DeletedMovies, deletedSeries = summary.DeletedSeries });
    }

    /// <summary>
    /// Returns full per-peer details for the admin "Peers" tab: identity, online/offline status,
    /// remote catalog counts (from heartbeat), local synced counts by type, disk usage and folder paths.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Per-peer detail list and last global sync timestamp.</returns>
    [HttpGet("peers/details")]
    [AllowAnonymous]
    [ServiceFilter(typeof(FederationAuthFilter))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PeerDetailsResponseDto>> GetPeersDetails(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return Ok(new PeerDetailsResponseDto());
        }

        await RefreshDiscoverySuggestionsAsync(config, cancellationToken).ConfigureAwait(false);

        var libraryPath = config.LibraryPath;
        var states = string.IsNullOrWhiteSpace(libraryPath)
            ? new Dictionary<string, PeerStatus>()
            : PeerStateStore.Load(libraryPath);

        var manifest = string.IsNullOrWhiteSpace(libraryPath)
            ? new Manifest()
            : ManifestStore.Load(libraryPath);

        var moviesRoot = config.GetEffectiveMoviesRoot();
        var seriesRoot = config.GetEffectiveSeriesRoot();
        var animeRoot = config.GetEffectiveAnimeRoot();

        var peers = new List<PeerDetailDto>(config.Peers.Count);
        string? latestSyncAt = null;

        foreach (var peer in config.Peers)
        {
            states.TryGetValue(peer.Name, out var status);

            int localMovies = 0, localSeries = 0, localAnime = 0;
            long diskBytes = 0;

            foreach (var entry in manifest.Movies.Values)
            {
                if (!EntryContainsPeer(entry, peer.Name))
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

                diskBytes += SourceDiskBytes(entry, peer.Name);
            }

            foreach (var entry in manifest.Series.Values)
            {
                if (!EntryContainsPeer(entry, peer.Name))
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

                diskBytes += SourceDiskBytes(entry, peer.Name);
            }

            if (status?.LastSyncAt is not null &&
                (latestSyncAt is null ||
                 string.CompareOrdinal(status.LastSyncAt, latestSyncAt) > 0))
            {
                latestSyncAt = status.LastSyncAt;
            }

            var peerVersion = status?.Version;
            var selfVersion = Plugin.Instance?.Version.ToString(3) ?? "unknown";

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
                Version = peerVersion,
                VersionMismatch = !string.IsNullOrWhiteSpace(peerVersion)
                    && !string.Equals(peerVersion, selfVersion, StringComparison.Ordinal),
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
                MoviesFolder = string.IsNullOrWhiteSpace(moviesRoot) ? null : moviesRoot,
                SeriesFolder = string.IsNullOrWhiteSpace(seriesRoot) ? null : seriesRoot,
                AnimeFolder = string.IsNullOrWhiteSpace(animeRoot) ? null : animeRoot
            });
        }

        _logger.LogInformation(
            "JellyFed: GET /peers/details — {ConfigPeers} configured, {ReturnedPeers} returned, {DiscoveredPeers} discovered, lastGlobalSyncAt={Sync}.",
            config.Peers.Count,
            peers.Count,
            config.DiscoveredPeers.Count,
            latestSyncAt ?? "(none)");

        return Ok(new PeerDetailsResponseDto
        {
            SelfVersion = Plugin.Instance?.Version.ToString(3) ?? "unknown",
            Peers = peers,
            DiscoveredPeers = config.DiscoveredPeers
                .OrderBy(p => p.Name)
                .ThenBy(p => p.Url)
                .Select(p => new DiscoveredPeerDto
                {
                    Name = p.Name,
                    Url = p.Url,
                    FederationToken = p.FederationToken,
                    SourcePeerName = p.SourcePeerName,
                    Version = p.Version,
                    HopCount = p.HopCount,
                    LastDiscoveredAt = p.LastDiscoveredAt
                })
                .ToList(),
            SelfDiscoverable = config.Discoverable,
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

        var newPeer = new PeerConfiguration
        {
            PeerId = Guid.NewGuid().ToString("N"),
            Name = nameTrim,
            Url = urlTrim,
            FederationToken = request.FederationToken,
            DiscoveryToken = request.FederationToken,
            Enabled = request.Enabled,
            SyncMovies = request.SyncMovies,
            SyncSeries = request.SyncSeries,
            SyncAnime = request.SyncAnime,
            AccessToken = null
        };
        config.Peers.Add(newPeer);

        config.DiscoveredPeers.RemoveAll(p => string.Equals(p.Url, urlTrim, StringComparison.OrdinalIgnoreCase));

        Plugin.Instance!.SaveConfiguration();
        _logger.LogInformation(
            "JellyFed: peer {PeerName} added manually (reachable={Reachable}, version={Version}).",
            nameTrim,
            reachable,
            version ?? "unknown");
        _auditLogService.WritePeerEvent(
            newPeer,
            "peer.added-manually",
            $"Added peer {newPeer.Name} from the admin UI.",
            details: new { reachable, version, syncMovies = newPeer.SyncMovies, syncSeries = newPeer.SyncSeries, syncAnime = newPeer.SyncAnime });

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

        PeerIdentity.EnsurePeerId(peer);

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
            peer.DiscoveryToken = request.FederationToken;
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
        _auditLogService.WritePeerEvent(
            peer,
            "peer.updated",
            $"Updated peer {oldName}.",
            details: new { oldName, newName = peer.Name, peer.Url, peer.Enabled, peer.SyncMovies, peer.SyncSeries, peer.SyncAnime });
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

        _auditLogService.WritePeerEvent(
            peer,
            result.Error is null ? "peer.sync.requested-success" : "peer.sync.requested-failed",
            result.Error is null
                ? $"Manual sync completed for {peer.Name}."
                : $"Manual sync failed for {peer.Name}: {result.Error}",
            result.Error is null ? AuditLogSeverities.Info : AuditLogSeverities.Error,
            new
            {
                result.AddedMovies,
                result.AddedSeries,
                result.SkippedMovies,
                result.SkippedSeries,
                result.Pruned,
                result.DurationMs,
                result.Error
            });

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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Deletion summary.</returns>
    [HttpPost("peer/{name}/purge")]
    [AllowAnonymous]
    [ServiceFilter(typeof(FederationAuthFilter))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> PurgePeerByName([FromRoute] string name, CancellationToken cancellationToken)
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

        PeerIdentity.EnsurePeerId(peer);

        var summary = await PurgePeerDataAsync(config, peer.Name, cancellationToken).ConfigureAwait(false);

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

        _auditLogService.WritePeerEvent(
            peer,
            "peer.purged",
            $"Purged federated content for {peer.Name} while keeping the peer configured.",
            AuditLogSeverities.Warning,
            new { summary.DeletedMovies, summary.DeletedSeries });

        return Ok(new
        {
            status = "ok",
            deletedMovies = summary.DeletedMovies,
            deletedSeries = summary.DeletedSeries
        });
    }

    /// <summary>
    /// Removes a peer entirely: purge content, revoke its access token, drop from config and
    /// blacklist its URL so it stays hidden from discovery suggestions until unblocked.
    /// </summary>
    /// <param name="name">Peer name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Deletion summary.</returns>
    [HttpPost("peer/{name}/remove")]
    [AllowAnonymous]
    [ServiceFilter(typeof(FederationAuthFilter))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Peer name is used only as a dictionary key and sanitized folder segment, never as a direct filesystem path.")]
    public async Task<ActionResult> RemovePeer([FromRoute] string name, CancellationToken cancellationToken)
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

        var summary = await PurgePeerDataAsync(config, peer.Name, cancellationToken).ConfigureAwait(false);

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
        config.DiscoveredPeers.RemoveAll(p => string.Equals(p.Url, peer.Url, StringComparison.OrdinalIgnoreCase));
        Plugin.Instance!.SaveConfiguration();

        _logger.LogInformation(
            "JellyFed: peer {PeerName} removed (blocked URL, revoked token, {Movies} movies + {Series} series purged).",
            peer.Name,
            summary.DeletedMovies,
            summary.DeletedSeries);

        _auditLogService.WritePeerEvent(
            peer,
            "peer.removed",
            $"Removed peer {peer.Name} and revoked its access token.",
            AuditLogSeverities.Warning,
            new { summary.DeletedMovies, summary.DeletedSeries, blockedUrl = peer.Url });

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
        var requestIdentity = ResolveQueryTokenIdentity(token);
        if (requestIdentity is null)
        {
            _auditLogService.WriteSecurityEvent(
                "stream.invalid-token",
                $"Rejected stream request for item {itemId} because the query token was invalid.",
                HttpContext,
                details: new { itemId });
            return Unauthorized();
        }

        FederationRequestIdentityAccessor.Set(HttpContext, requestIdentity);

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
            _auditLogService.WriteRequestEvent(
                HttpContext,
                AuditLogCategories.PeerAccess,
                "stream.redirected",
                $"Accepted stream request for item {itemId} and redirected through Jellyfin.",
                statusCode: StatusCodes.Status302Found,
                details: new { itemId, authMode = requestIdentity.AuthMode, mode = "jellyfin-redirect" });
            // Static=true → source Jellyfin serves the raw file with proper range request
            // support. This allows the client's FFmpeg to seek within the stream (for HLS
            // transcoding). Static=false would start a transcoding session on the source,
            // which doesn't support range-based seeking.
            return Redirect($"{GetAdvertisedBaseUrl()}/Videos/{itemId}/stream?api_key={apiKey}&Static=true");
        }

        // Fallback: serve the file directly (no transcoding — client must support the format).
        var item = _libraryManager.GetItemById(guid);
        if (item is null || string.IsNullOrEmpty(item.Path) || !System.IO.File.Exists(item.Path))
        {
            return NotFound();
        }

        _auditLogService.WriteRequestEvent(
            HttpContext,
            AuditLogCategories.PeerAccess,
            "stream.served",
            $"Served a direct media stream for item {itemId}.",
            statusCode: StatusCodes.Status200OK,
            details: new { itemId, authMode = requestIdentity.AuthMode, mode = "direct-file" });

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
        var requestIdentity = ResolveQueryTokenIdentity(token);
        if (requestIdentity is null)
        {
            _auditLogService.WriteSecurityEvent(
                "image.invalid-token",
                $"Rejected image request for item {itemId} because the query token was invalid.",
                HttpContext,
                details: new { itemId, imageType });
            return Unauthorized();
        }

        FederationRequestIdentityAccessor.Set(HttpContext, requestIdentity);

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

        _auditLogService.WriteRequestEvent(
            HttpContext,
            AuditLogCategories.PeerAccess,
            "image.served",
            $"Served a {imageType} image for item {itemId}.",
            statusCode: StatusCodes.Status200OK,
            details: new { itemId, imageType, authMode = requestIdentity.AuthMode });

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
                var manifest = ManifestStore.Load(libraryPath);
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

        var clearedPeerCount = config.Peers.Count;

        // Generate a new federation token and clear the peer list.
        config.FederationToken = GenerateAccessToken();
        config.Peers.Clear();
        config.DiscoveredPeers.Clear();
        config.BlockedPeerUrls.Clear();
        Plugin.Instance!.SaveConfiguration();

        _logger.LogWarning(
            "JellyFed: network reset — new federation token generated, all peers and STRMs cleared.");

        _auditLogService.Write(new AuditLogEntry
        {
            Category = AuditLogCategories.Security,
            EventType = "network.reset",
            Severity = AuditLogSeverities.Warning,
            Message = "Reset the JellyFed network, generated a new federation token, and cleared all peers.",
            ActorType = "admin-or-local",
            Method = HttpContext.Request.Method,
            Path = HttpContext.Request.Path.Value,
            RemoteIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            StatusCode = StatusCodes.Status200OK,
            DetailsJson = AuditLogEntry.SerializeDetails(new { clearedPeerCount })
        });

        return Ok(new { status = "ok", newToken = config.FederationToken });
    }

    private async Task RefreshDiscoverySuggestionsAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        var directPeers = config.Peers
            .Where(p => p.Enabled)
            .Where(p => !string.IsNullOrWhiteSpace(p.Url) && !string.IsNullOrWhiteSpace(p.FederationToken))
            .OrderBy(p => p.Name)
            .ToList();

        var directUrls = directPeers
            .Select(p => NormalizeUrl(p.Url))
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var blockedUrls = config.BlockedPeerUrls
            .Select(NormalizeUrl)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var selfUrl = NormalizeUrl(config.SelfUrl);
        var previous = config.DiscoveredPeers ?? [];
        var previousByUrl = previous
            .Where(p => !string.IsNullOrWhiteSpace(p.Url))
            .GroupBy(p => NormalizeUrl(p.Url), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var refreshedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var next = new Dictionary<string, DiscoveredPeerConfiguration>(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, PeerStatus>? states = null;
        var statesChanged = false;
        if (!string.IsNullOrWhiteSpace(config.LibraryPath))
        {
            states = PeerStateStore.Load(config.LibraryPath);
        }

        foreach (var peer in directPeers)
        {
            var discovery = await _peerClient.GetDiscoveryAsync(peer, cancellationToken).ConfigureAwait(false);
            if (discovery is null)
            {
                continue;
            }

            refreshedSources.Add(peer.Name);

            if (states is not null)
            {
                if (!states.TryGetValue(peer.Name, out var status))
                {
                    status = new PeerStatus();
                    states[peer.Name] = status;
                    statesChanged = true;
                }

                var discoverable = discovery.Self?.Discoverable;
                if (status.Discoverable != discoverable)
                {
                    status.Discoverable = discoverable;
                    statesChanged = true;
                }

                if (!string.IsNullOrWhiteSpace(discovery.Self?.Version) &&
                    !string.Equals(status.Version, discovery.Self.Version, StringComparison.Ordinal))
                {
                    status.Version = discovery.Self.Version;
                    statesChanged = true;
                }
            }

            foreach (var candidate in discovery.DirectPeers ?? [])
            {
                var candidateUrl = NormalizeUrl(candidate.Url);
                if (string.IsNullOrWhiteSpace(candidateUrl) ||
                    string.IsNullOrWhiteSpace(candidate.FederationToken) ||
                    !candidate.Discoverable ||
                    directUrls.Contains(candidateUrl) ||
                    blockedUrls.Contains(candidateUrl) ||
                    string.Equals(candidateUrl, selfUrl, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(candidateUrl, NormalizeUrl(peer.Url), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (next.ContainsKey(candidateUrl))
                {
                    continue;
                }

                previousByUrl.TryGetValue(candidateUrl, out var previousEntry);

                next[candidateUrl] = new DiscoveredPeerConfiguration
                {
                    Name = string.IsNullOrWhiteSpace(candidate.Name) ? candidateUrl : candidate.Name.Trim(),
                    Url = candidateUrl,
                    FederationToken = candidate.FederationToken.Trim(),
                    SourcePeerName = peer.Name,
                    Version = candidate.Version,
                    HopCount = 2,
                    LastDiscoveredAt = previousEntry?.LastDiscoveredAt ?? DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                };
            }
        }

        foreach (var stale in previous)
        {
            var staleUrl = NormalizeUrl(stale.Url);
            if (string.IsNullOrWhiteSpace(staleUrl) ||
                next.ContainsKey(staleUrl) ||
                directUrls.Contains(staleUrl) ||
                blockedUrls.Contains(staleUrl) ||
                string.Equals(staleUrl, selfUrl, StringComparison.OrdinalIgnoreCase) ||
                refreshedSources.Contains(stale.SourcePeerName) ||
                !directPeers.Any(p => string.Equals(p.Name, stale.SourcePeerName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            next[staleUrl] = stale;
        }

        var nextList = next.Values
            .OrderBy(p => p.Name)
            .ThenBy(p => p.Url)
            .ToList();

        if (statesChanged && states is not null && !string.IsNullOrWhiteSpace(config.LibraryPath))
        {
            PeerStateStore.Save(config.LibraryPath, states);
        }

        if (!DiscoveryListsEqual(previous, nextList))
        {
            config.DiscoveredPeers = nextList;
            Plugin.Instance!.SaveConfiguration();
        }
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

    private static DiscoveryPeerDto BuildSelfDiscoveryDto(PluginConfiguration config)
        => new()
        {
            Name = string.IsNullOrWhiteSpace(config.SelfName)
                ? Plugin.Instance?.Name ?? "JellyFed"
                : config.SelfName.Trim(),
            Url = NormalizeUrl(config.SelfUrl),
            FederationToken = config.FederationToken,
            Version = Plugin.Instance?.Version.ToString(3),
            Discoverable = config.Discoverable
        };

    private static string GetDiscoveryToken(PeerConfiguration peer)
        => !string.IsNullOrWhiteSpace(peer.DiscoveryToken)
            ? peer.DiscoveryToken
            : string.IsNullOrWhiteSpace(peer.AccessToken)
                ? peer.FederationToken
                : string.Empty;

    private static bool DiscoveryListsEqual(
        List<DiscoveredPeerConfiguration> left,
        List<DiscoveredPeerConfiguration> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            var a = left[i];
            var b = right[i];
            if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal) ||
                !string.Equals(NormalizeUrl(a.Url), NormalizeUrl(b.Url), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(a.FederationToken, b.FederationToken, StringComparison.Ordinal) ||
                !string.Equals(a.SourcePeerName, b.SourcePeerName, StringComparison.Ordinal) ||
                !string.Equals(a.Version, b.Version, StringComparison.Ordinal) ||
                a.HopCount != b.HopCount)
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeUrl(string? url)
        => string.IsNullOrWhiteSpace(url)
            ? string.Empty
            : url.Trim().TrimEnd('/');

    /// <summary>
    /// Extracts codec, runtime-shaping metadata and all video/audio/subtitle track info from a
    /// BaseItem. Called for every movie and episode exported in the catalog so the receiving
    /// server can write complete &lt;fileinfo&gt;&lt;streamdetails&gt; into NFO files and the
    /// federation provider can surface real per-stream indexes / bitrates / channels.
    /// </summary>
    private ExtractedStreamInfo ExtractStreamInfo(BaseItem item)
    {
        if (item is not Video video)
        {
            return EmptyStreamInfo;
        }

        var container = video.Container;
        string? videoCodec = null;
        int? width = null;
        int? height = null;
        string? videoRange = null;
        string? primaryAudioCodec = null;
        var mediaStreams = new List<MediaStreamInfoDto>();

        try
        {
            var streams = video.GetMediaStreams();

            foreach (var s in streams)
            {
                if (s.Type == MediaStreamType.Video)
                {
                    if (videoCodec is null)
                    {
                        videoCodec = s.Codec;
                        width = s.Width;
                        height = s.Height;
                        videoRange = NormalizeVideoRange(s);
                    }

                    mediaStreams.Add(new MediaStreamInfoDto
                    {
                        Type = "Video",
                        Codec = s.Codec,
                        Language = s.Language,
                        Title = s.Title,
                        IsDefault = s.IsDefault,
                        IsForced = s.IsForced,
                        Index = s.Index,
                        BitRate = s.BitRate,
                        Width = s.Width,
                        Height = s.Height,
                        VideoRange = NormalizeVideoRange(s)
                    });
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
                        IsForced = s.IsForced,
                        Index = s.Index,
                        Channels = s.Channels,
                        BitRate = s.BitRate,
                        SampleRate = s.SampleRate
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
                        IsForced = s.IsForced,
                        Index = s.Index
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "JellyFed: could not read media streams for item {Id}", item.Id);
        }

        long? bitRate = null;
        long? sizeBytes = null;
        try
        {
            var sources = video.GetMediaSources(enablePathSubstitution: false);
            if (sources.Count > 0)
            {
                bitRate = sources[0].Bitrate;
                sizeBytes = sources[0].Size;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "JellyFed: could not read media source bitrate/size for item {Id}", item.Id);
        }

        var edition = ExtractEdition(video);

        return new ExtractedStreamInfo(
            container,
            videoCodec,
            width,
            height,
            primaryAudioCodec,
            bitRate,
            sizeBytes,
            videoRange,
            edition,
            mediaStreams);
    }

    /// <summary>
    /// Normalises HDR-related metadata into a single short token (SDR / HDR10 / DV / HLG…).
    /// Jellyfin's MediaStream uses <c>VideoRange</c> (HDR/SDR) plus <c>VideoRangeType</c>
    /// (HDR10/HDR10+/HLG/DV…). The richer one wins when available.
    /// </summary>
    private static string? NormalizeVideoRange(MediaStream stream)
    {
        var rangeType = stream.VideoRangeType.ToString();
        if (!string.IsNullOrEmpty(rangeType) && !string.Equals(rangeType, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return rangeType;
        }

        var range = stream.VideoRange.ToString();
        return string.IsNullOrEmpty(range) || string.Equals(range, "Unknown", StringComparison.OrdinalIgnoreCase)
            ? null
            : range;
    }

    /// <summary>
    /// Extracts a Jellyfin-style edition tag from the file/folder name (<c>[edition-XXX]</c> or
    /// <c>{edition-XXX}</c>). Returns <c>null</c> when no tag is present.
    /// </summary>
    private static string? ExtractEdition(Video video)
    {
        var folderName = string.IsNullOrWhiteSpace(video.ContainingFolderPath)
            ? null
            : System.IO.Path.GetFileName(video.ContainingFolderPath);

        foreach (var candidate in new[] { video.FileNameWithoutExtension, folderName })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var match = System.Text.RegularExpressions.Regex.Match(
                candidate,
                @"[\[\{]edition-([^\]\}]+)[\]\}]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the base URL for this request, honouring X-Forwarded-Proto when behind a reverse proxy.
    /// </summary>
    private string GetBaseUrl()
    {
        var scheme = Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? Request.Scheme;
        return $"{scheme}://{Request.Host}{Request.PathBase.Value?.TrimEnd('/')}";
    }

    private string GetAdvertisedBaseUrl()
    {
        var selfUrl = Plugin.Instance?.Configuration.SelfUrl;
        if (!string.IsNullOrWhiteSpace(selfUrl))
        {
            return selfUrl.Trim().TrimEnd('/');
        }

        return GetBaseUrl();
    }

    /// <summary>
    /// Returns an image URL. Uses the native Jellyfin Images API when an API key is available
    /// (avoids the JellyFed proxy hop and is more reliable), otherwise falls back to the
    /// JellyFed proxy endpoint authenticated with the federation token.
    /// </summary>
    private static string ImageUrl(string baseUrl, Guid itemId, string imageType, string token, string? apiKey)
        => !string.IsNullOrWhiteSpace(apiKey)
            ? $"{baseUrl}/Items/{itemId:N}/Images/{imageType}?api_key={apiKey}"
            : $"{baseUrl}{FederationProtocol.ToPath($"image/{itemId:N}/{imageType}")}?token={token}";

    private string GetEffectivePeerTokenOrGlobal()
    {
        var requestIdentity = FederationRequestIdentityAccessor.Get(HttpContext);
        if (!string.IsNullOrWhiteSpace(requestIdentity?.PresentedToken) && requestIdentity.IsPeerAttributed)
        {
            return requestIdentity.PresentedToken!;
        }

        return Plugin.Instance!.Configuration.FederationToken;
    }

    private FederationRequestIdentity? ResolveQueryTokenIdentity(string? token)
        => _auditLogService.ResolveFederationToken(token);

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

    /// <summary>
    /// Shared purge pipeline used by both the name-based <c>/peer/{name}/purge</c> endpoint and
    /// <c>/peer/{name}/remove</c>. Removes this peer's materialized files from flattened item
    /// folders and preserves logical items that still have alternate sources.
    /// </summary>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Paths come from the plugin manifest written by this plugin and admin-configured roots.")]
    private async Task<(int DeletedMovies, int DeletedSeries)> PurgePeerDataAsync(
        PluginConfiguration config,
        string peerName,
        CancellationToken cancellationToken)
    {
        var manifest = ManifestStore.Load(config.LibraryPath);

        var deletedPaths = new List<string>();
        var deletedMovies = await RemovePeerFromEntriesAsync(manifest.Movies, peerName, deletedPaths, cancellationToken)
            .ConfigureAwait(false);
        var deletedSeries = await RemovePeerFromEntriesAsync(manifest.Series, peerName, deletedPaths, cancellationToken)
            .ConfigureAwait(false);

        ManifestStore.Save(config.LibraryPath, manifest);
        if (deletedPaths.Count > 0)
        {
            RemoveLibraryItems(deletedPaths);
        }

        _libraryManager.QueueLibraryScan();

        return (deletedMovies, deletedSeries);
    }

    private async Task<int> RemovePeerFromEntriesAsync(
        Dictionary<string, ManifestEntry> entries,
        string peerName,
        List<string> deletedPaths,
        CancellationToken cancellationToken)
    {
        var deletedCount = 0;

        foreach (var key in entries.Keys.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = entries[key];
            entry.Sources ??= [];
            if (entry.Sources.Count == 0 && !string.IsNullOrWhiteSpace(entry.PeerName))
            {
                entry.Sources =
                [
                    new ManifestSource
                    {
                        PeerName = entry.PeerName,
                        JellyfinId = entry.JellyfinId
                    }
                ];
            }

            var remainingSources = entry.Sources.ToList();
            var removedSources = remainingSources
                .Where(source => string.Equals(source.PeerName, peerName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (removedSources.Count == 0)
            {
                continue;
            }

            foreach (var source in removedSources)
            {
                remainingSources.Remove(source);
                _ = _strmWriter.DeletePeerSourceFiles(entry.Path, source.PeerName);
            }

            entry.Sources = remainingSources;

            if (entry.Sources.Count == 0)
            {
                deletedPaths.Add(entry.Path);
                if (Directory.Exists(entry.Path))
                {
                    Directory.Delete(entry.Path, true);
                }

                entries.Remove(key);
                deletedCount++;
                continue;
            }

            NormalizeDisplaySource(entry);
            await _strmWriter.RefreshProvenanceAsync(entry.Path, entry, cancellationToken).ConfigureAwait(false);

            entry.SyncedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        }

        return deletedCount;
    }

    private static DateTime ParseUpdatedAt(string? value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTime.MinValue;

    private static IEnumerable<string> EnumerateSourcePeers(ManifestEntry entry)
        => entry.Sources is { Count: > 0 }
            ? entry.Sources.Select(source => source.PeerName).Distinct(StringComparer.OrdinalIgnoreCase)
            : string.IsNullOrWhiteSpace(entry.PeerName)
                ? []
                : [entry.PeerName];

    private static bool EntryContainsPeer(ManifestEntry entry, string peerName)
        => EnumerateSourcePeers(entry).Any(name => string.Equals(name, peerName, StringComparison.OrdinalIgnoreCase));

    private static long SourceDiskBytes(ManifestEntry entry, string peerName)
    {
        long total = 0;
        foreach (var source in entry.Sources.Where(source => string.Equals(source.PeerName, peerName, StringComparison.OrdinalIgnoreCase)))
        {
            total += FileSize(source.StrmPath);
            total += FileSize(source.NfoPath);
        }

        if (total > 0 || !Directory.Exists(entry.Path))
        {
            return total;
        }

        var peerSuffix = $" [peer-{StrmWriter.SanitizePeerFolderSegment(peerName)}]";
        foreach (var filePath in Directory.EnumerateFiles(entry.Path, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(filePath);
            if (name.EndsWith(peerSuffix, StringComparison.OrdinalIgnoreCase))
            {
                total += FileSize(filePath);
            }
        }

        return total;
    }

    private static long FileSize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            return 0;
        }

        return new FileInfo(path).Length;
    }

    private static void NormalizeDisplaySource(ManifestEntry entry)
    {
        if (entry.Sources.Count == 0)
        {
            entry.PeerName = string.Empty;
            entry.JellyfinId = string.Empty;
            return;
        }

        var display = entry.Sources
            .OrderByDescending(source => (source.Width ?? 0) * (source.Height ?? 0))
            .ThenByDescending(source => ParseUpdatedAt(source.UpdatedAt))
            .ThenBy(source => source.PeerName, StringComparer.OrdinalIgnoreCase)
            .First();

        entry.PeerName = display.PeerName;
        entry.JellyfinId = display.JellyfinId;
    }

    /// <summary>
    /// Renames peer-tagged flattened files and rewrites manifest sources. Updates the peer-state store key.
    /// </summary>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Paths come from the JellyFed-owned manifest.")]
    private void RenamePeerOnDisk(PluginConfiguration config, string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(config.LibraryPath))
        {
            return;
        }

        var manifest = ManifestStore.Load(config.LibraryPath);
        bool manifestChanged = false;

        foreach (var dict in new[] { manifest.Movies, manifest.Series })
        {
            foreach (var entry in dict.Values)
            {
                var touchesPeer = string.Equals(entry.PeerName, oldName, StringComparison.OrdinalIgnoreCase) ||
                                  EntryContainsPeer(entry, oldName);
                if (!touchesPeer)
                {
                    continue;
                }

                if (string.Equals(entry.PeerName, oldName, StringComparison.OrdinalIgnoreCase))
                {
                    entry.PeerName = newName;
                }

                foreach (var source in entry.Sources.Where(source => string.Equals(source.PeerName, oldName, StringComparison.OrdinalIgnoreCase)))
                {
                    source.PeerName = newName;
                }

                _strmWriter.RenamePeerSourceFiles(entry.Path, oldName, newName);
                NormalizeDisplaySource(entry);

                manifestChanged = true;
            }
        }

        if (manifestChanged)
        {
            ManifestStore.Save(config.LibraryPath, manifest);
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
