using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.JellyFed.Api.Dto;

/// <summary>
/// Response envelope for the /JellyFed/v1/catalog/series/{id}/seasons endpoint.
/// </summary>
public class SeasonsResponseDto
{
    /// <summary>Gets or sets the Jellyfin series ID.</summary>
    public string SeriesId { get; set; } = string.Empty;

    /// <summary>Gets the seasons and their episodes.</summary>
    public Collection<SeasonDto> Seasons { get; init; } = [];
}
