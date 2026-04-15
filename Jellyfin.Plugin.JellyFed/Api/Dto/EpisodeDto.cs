using System.Collections.Generic;
using System.Text.Json.Serialization;

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
