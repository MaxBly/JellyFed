using System.Collections.Generic;
using Jellyfin.Plugin.JellyFed.Api.Dto;

namespace Jellyfin.Plugin.JellyFed.Sync;

/// <summary>
/// Tracks one concrete upstream source for a logical JellyFed item.
/// </summary>
public class ManifestSource
{
    /// <summary>Gets or sets the peer name that owns this source.</summary>
    public string PeerName { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin item ID on the remote peer.</summary>
    public string JellyfinId { get; set; } = string.Empty;

    /// <summary>Gets or sets the direct stream URL when applicable (movies / episodes fallback groundwork).</summary>
    public string? StreamUrl { get; set; }

    /// <summary>Gets or sets the container format.</summary>
    public string? Container { get; set; }

    /// <summary>Gets or sets the video codec.</summary>
    public string? VideoCodec { get; set; }

    /// <summary>Gets or sets the audio codec.</summary>
    public string? AudioCodec { get; set; }

    /// <summary>Gets or sets the width in pixels.</summary>
    public int? Width { get; set; }

    /// <summary>Gets or sets the height in pixels.</summary>
    public int? Height { get; set; }

    /// <summary>Gets or sets when the source item was created on the peer.</summary>
    public string? AddedAt { get; set; }

    /// <summary>Gets or sets when the source item was updated on the peer.</summary>
    public string? UpdatedAt { get; set; }

    /// <summary>Gets or sets the runtime in 100ns ticks for this specific source (different cuts
    /// can have different runtimes — Director's Cut, Theatrical, etc.).</summary>
    public long? RuntimeTicks { get; set; }

    /// <summary>Gets or sets the total bitrate in bits per second for this source.</summary>
    public long? BitRate { get; set; }

    /// <summary>Gets or sets the file size in bytes for this source.</summary>
    public long? SizeBytes { get; set; }

    /// <summary>Gets or sets the colour range / HDR variant ("SDR", "HDR10", "DV"…).</summary>
    public string? VideoRange { get; set; }

    /// <summary>Gets or sets the edition tag if any (Director's Cut, Theatrical, Extended…).</summary>
    public string? Edition { get; set; }

    /// <summary>Gets or sets all known tracks (video, audio, subtitle) for this source.</summary>
    public IReadOnlyList<MediaStreamInfoDto> MediaStreams { get; set; } = [];
}
