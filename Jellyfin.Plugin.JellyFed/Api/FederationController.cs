using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
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
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "JellyFed: network reset — could not remove library items from Jellyfin index (non-fatal, will clean up on next scan).");
            }
        }

        // Delete all .strm files and the manifest from disk.
        if (!string.IsNullOrWhiteSpace(libraryPath))
        {
            var filmsDir = Path.Combine(libraryPath, "Films");
            var seriesDir = Path.Combine(libraryPath, "Series");
            var manifestPath = Path.Combine(libraryPath, ".jellyfed-manifest.json");
            if (Directory.Exists(filmsDir))
            {
                Directory.Delete(filmsDir, true);
            }

            if (Directory.Exists(seriesDir))
            {
                Directory.Delete(seriesDir, true);
            }

            if (System.IO.File.Exists(manifestPath))
            {
                System.IO.File.Delete(manifestPath);
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
}
