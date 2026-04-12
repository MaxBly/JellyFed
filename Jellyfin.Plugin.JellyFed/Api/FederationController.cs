using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyFed.Api.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
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
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<FederationController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{FederationController}"/> interface.</param>
    public FederationController(ILibraryManager libraryManager, ILogger<FederationController> logger)
    {
        _libraryManager = libraryManager;
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
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
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

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
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

        foreach (var item in items)
        {
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

    private static int? TicksToMinutes(long? ticks)
        => ticks.HasValue ? (int)(ticks.Value / TimeSpan.TicksPerMinute) : null;

    private static bool HasImage(BaseItem item, ImageType imageType)
        => item.HasImage(imageType, 0);
}
