namespace Jellyfin.Plugin.JellyFed.Api.Dto;

/// <summary>
/// A single media stream (video, audio or subtitle track) within a catalog item.
/// </summary>
public class MediaStreamInfoDto
{
    /// <summary>Gets or sets the stream type: "Video", "Audio" or "Subtitle".</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the codec (e.g. h264, hevc, aac, eac3, ac3, subrip, ass).</summary>
    public string? Codec { get; set; }

    /// <summary>Gets or sets the ISO 639-2 language code (e.g. eng, fre, jpn).</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the track title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets a value indicating whether this is the default track.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Gets or sets a value indicating whether this is a forced track.</summary>
    public bool IsForced { get; set; }

    /// <summary>Gets or sets the upstream Jellyfin stream index, so consumers can map audio/subtitle
    /// selections back to a real ffmpeg stream.</summary>
    public int? Index { get; set; }

    /// <summary>Gets or sets the audio channel count (e.g. 2, 6, 8). Null for non-audio streams.</summary>
    public int? Channels { get; set; }

    /// <summary>Gets or sets the bitrate in bits per second.</summary>
    public long? BitRate { get; set; }

    /// <summary>Gets or sets the video width in pixels. Set on video streams only.</summary>
    public int? Width { get; set; }

    /// <summary>Gets or sets the video height in pixels. Set on video streams only.</summary>
    public int? Height { get; set; }

    /// <summary>Gets or sets the audio sample rate in Hz (e.g. 48000).</summary>
    public int? SampleRate { get; set; }

    /// <summary>Gets or sets the colour range for video streams ("SDR", "HDR", "HDR10", "DV"…).</summary>
    public string? VideoRange { get; set; }
}
