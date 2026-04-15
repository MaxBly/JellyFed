namespace Jellyfin.Plugin.JellyFed.Api.Dto;

/// <summary>
/// A single media stream (audio or subtitle track) within a catalog item.
/// </summary>
public class MediaStreamInfoDto
{
    /// <summary>Gets or sets the stream type: "Audio" or "Subtitle".</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the codec (e.g. aac, eac3, ac3, subrip, ass).</summary>
    public string? Codec { get; set; }

    /// <summary>Gets or sets the ISO 639-2 language code (e.g. eng, fre, jpn).</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the track title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets a value indicating whether this is the default track.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Gets or sets a value indicating whether this is a forced track.</summary>
    public bool IsForced { get; set; }
}
