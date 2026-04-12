using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.JellyFed.Api.Dto;

/// <summary>
/// A season with its episodes.
/// </summary>
public class SeasonDto
{
    /// <summary>Gets or sets the Jellyfin item ID of the season.</summary>
    public string JellyfinId { get; set; } = string.Empty;

    /// <summary>Gets or sets the season number.</summary>
    public int? SeasonNumber { get; set; }

    /// <summary>Gets or sets the season title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets the episodes in this season.</summary>
    public Collection<EpisodeDto> Episodes { get; init; } = [];
}
