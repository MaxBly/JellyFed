using System.Collections.Generic;
using Jellyfin.Plugin.JellyFed.Api.Dto;

namespace Jellyfin.Plugin.JellyFed.Api;

/// <summary>
/// Aggregated per-source stream info extracted from a BaseItem. Mirrors the additive fields
/// the federation catalog exposes so receiving servers can write complete &lt;streamdetails&gt;
/// NFOs, store accurate per-source runtimes, and let consumers sort/label alternates by
/// bitrate / size / HDR variant.
/// </summary>
/// <param name="Container">Container format (mkv, mp4…).</param>
/// <param name="VideoCodec">Primary video codec.</param>
/// <param name="Width">Primary video width.</param>
/// <param name="Height">Primary video height.</param>
/// <param name="AudioCodec">Primary audio codec.</param>
/// <param name="BitRate">Total source bitrate in bits per second.</param>
/// <param name="SizeBytes">Total file size in bytes.</param>
/// <param name="VideoRange">Colour range (SDR / HDR10 / DV / HLG…).</param>
/// <param name="Edition">Edition tag if present in filename (Director's Cut, Theatrical…).</param>
/// <param name="MediaStreams">All video / audio / subtitle streams for the source.</param>
public readonly record struct ExtractedStreamInfo(
    string? Container,
    string? VideoCodec,
    int? Width,
    int? Height,
    string? AudioCodec,
    long? BitRate,
    long? SizeBytes,
    string? VideoRange,
    string? Edition,
    IReadOnlyList<MediaStreamInfoDto> MediaStreams);
