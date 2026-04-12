namespace Jellyfin.Plugin.JellyFed.Api.Dto;

/// <summary>
/// A single episode within a season.
/// </summary>
public class EpisodeDto
{
    /// <summary>Gets or sets the Jellyfin item ID.</summary>
    public string JellyfinId { get; set; } = string.Empty;

    /// <summary>Gets or sets the episode number within the season.</summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>Gets or sets the episode title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the episode synopsis.</summary>
    public string? Overview { get; set; }

    /// <summary>Gets or sets the air date (ISO 8601).</summary>
    public string? AirDate { get; set; }

    /// <summary>Gets or sets the runtime in minutes.</summary>
    public int? RuntimeMinutes { get; set; }

    /// <summary>Gets or sets the still image URL.</summary>
    public string? StillUrl { get; set; }

    /// <summary>Gets or sets the direct stream URL.</summary>
    public string? StreamUrl { get; set; }
}
