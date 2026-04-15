using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyFed.Api.Dto;

/// <summary>
/// A single item (movie or series) in the federated catalog.
/// </summary>
public class CatalogItemDto
{
    /// <summary>Gets or sets the Jellyfin item ID.</summary>
    public string JellyfinId { get; set; } = string.Empty;

    /// <summary>Gets or sets the TMDB ID, if available.</summary>
    public string? TmdbId { get; set; }

    /// <summary>Gets or sets the IMDb ID, if available.</summary>
    public string? ImdbId { get; set; }

    /// <summary>Gets or sets the item type: "Movie" or "Series".</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the item title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the original title.</summary>
    public string? OriginalTitle { get; set; }

    /// <summary>Gets or sets the synopsis.</summary>
    public string? Overview { get; set; }

    /// <summary>Gets or sets the production year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the runtime in minutes.</summary>
    public int? RuntimeMinutes { get; set; }

    /// <summary>Gets or sets the community rating.</summary>
    public double? VoteAverage { get; set; }

    /// <summary>Gets or sets the genres.</summary>
    public IReadOnlyList<string> Genres { get; set; } = [];

    /// <summary>Gets or sets the poster image URL.</summary>
    public string? PosterUrl { get; set; }

    /// <summary>Gets or sets the backdrop image URL.</summary>
    public string? BackdropUrl { get; set; }

    /// <summary>Gets or sets the direct stream URL (for movies).</summary>
    public string? StreamUrl { get; set; }

    /// <summary>Gets or sets the date this item was added (ISO 8601).</summary>
    public string AddedAt { get; set; } = string.Empty;

    /// <summary>Gets or sets the date this item was last updated (ISO 8601).</summary>
    public string UpdatedAt { get; set; } = string.Empty;

    /// <summary>Gets or sets the media container format (e.g. mkv, mp4).</summary>
    public string? Container { get; set; }

    /// <summary>Gets or sets the video codec (e.g. hevc, h264).</summary>
    public string? VideoCodec { get; set; }

    /// <summary>Gets or sets the video width in pixels.</summary>
    public int? Width { get; set; }

    /// <summary>Gets or sets the video height in pixels.</summary>
    public int? Height { get; set; }

    /// <summary>Gets or sets the audio codec (e.g. aac, ac3).</summary>
    public string? AudioCodec { get; set; }

    /// <summary>Gets or sets all audio and subtitle tracks.</summary>
    [JsonPropertyName("mediaStreams")]
    public IReadOnlyList<MediaStreamInfoDto> MediaStreams { get; set; } = [];
}
