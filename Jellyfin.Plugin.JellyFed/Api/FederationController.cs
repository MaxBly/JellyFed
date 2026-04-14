using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyFed.Api.Dto;
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
    private readonly ILogger<FederationController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="taskManager">Instance of the <see cref="ITaskManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{FederationController}"/> interface.</param>
    public FederationController(
        ILibraryManager libraryManager,
        ITaskManager taskManager,
        ILogger<FederationController> logger)
    {
        _libraryManager = libraryManager;
        _taskManager = taskManager;
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
        var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase.Value?.TrimEnd('/')}";
        var token = Plugin.Instance!.Configuration.FederationToken;

        DateTime? sinceDate = null;
        if (!string.IsNullOrEmpty(since) &&
            DateTime.TryParse(since, null, DateTimeStyles.RoundtripKind, out var parsed))
        {
            sinceDate = parsed;
        }

        var items = new List<CatalogItemDto>();

        if (type is null or "Movie")
        {
            items.AddRange(QueryItems(BaseItemKind.Movie, baseUrl, token, sinceDate));
        }

        if (type is null or "Series")
        {
            items.AddRange(QueryItems(BaseItemKind.Series, baseUrl, token, sinceDate));
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

        var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase.Value?.TrimEnd('/')}";
        var token = Plugin.Instance!.Configuration.FederationToken;

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
                seasonDto.Episodes.Add(new EpisodeDto
                {
                    JellyfinId = ep.Id.ToString("N"),
                    EpisodeNumber = ep.IndexNumber,
                    Title = ep.Name,
                    Overview = ep.Overview,
                    AirDate = ep.PremiereDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    RuntimeMinutes = TicksToMinutes(ep.RunTimeTicks),
                    StillUrl = HasImage(ep, ImageType.Primary)
                        ? $"{baseUrl}/Items/{ep.Id:N}/Images/Primary?api_key={token}"
                        : null,
                    StreamUrl = $"{baseUrl}/Videos/{ep.Id:N}/stream?api_key={token}&Static=true"
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
        var libPath = Plugin.Instance?.Configuration.LibraryPath;

        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(libPath) &&
                !string.IsNullOrEmpty(item.Path) &&
                item.Path.StartsWith(libPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (since.HasValue && item.DateModified <= since.Value)
            {
                continue;
            }

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
                    ? $"{baseUrl}/Items/{item.Id:N}/Images/Primary?api_key={token}"
                    : null,
                BackdropUrl = HasImage(item, ImageType.Backdrop)
                    ? $"{baseUrl}/Items/{item.Id:N}/Images/Backdrop?api_key={token}"
                    : null,
                StreamUrl = kind == BaseItemKind.Movie
                    ? $"{baseUrl}/Videos/{item.Id:N}/stream?api_key={token}&Static=true"
                    : null,
                AddedAt = item.DateCreated.ToString("O", CultureInfo.InvariantCulture),
                UpdatedAt = item.DateModified.ToString("O", CultureInfo.InvariantCulture)
            };
        }
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

        _logger.LogInformation(
            "JellyFed: purged catalog for peer {PeerName} — {MovieCount} movies, {SeriesCount} series deleted.",
            name,
            movieKeys.Count,
            seriesKeys.Count);

        return Ok(new { status = "ok", deletedMovies = movieKeys.Count, deletedSeries = seriesKeys.Count });
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
}
