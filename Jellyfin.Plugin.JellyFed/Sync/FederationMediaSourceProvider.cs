using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyFed.Api.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyFed.Sync;

/// <summary>
/// Exposes alternate JellyFed upstreams as player-selectable media sources when a local item
/// has a <c>sources.json</c> sidecar.
/// </summary>
public class FederationMediaSourceProvider : IMediaSourceProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<FederationMediaSourceProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationMediaSourceProvider"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public FederationMediaSourceProvider(ILogger<FederationMediaSourceProvider> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        var sourcesFilePath = FindSourcesFilePath(item);
        if (sourcesFilePath is null)
        {
            return [];
        }

        SourcesFile? sourcesFile;
        try
        {
            using var stream = File.OpenRead(sourcesFilePath);
            sourcesFile = await JsonSerializer.DeserializeAsync<SourcesFile>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Unable to read JellyFed sources sidecar for {ItemPath}", item.Path);
            return [];
        }

        if (sourcesFile is null)
        {
            return [];
        }

        // Le peer primaire est déjà représenté par la MediaSource native du .strm
        // (créée par le core Jellyfin, le core ne pouvant être supprimé par un
        // IMediaSourceProvider). On ne ré-émet donc QUE les sources alternatives
        // → PlaybackInfo = native(primaire) + alternates, sans doublon de la primaire.
        var primaryPeer = sourcesFile.PrimaryPeerName;

        if (string.Equals(sourcesFile.ItemType, "Movie", StringComparison.OrdinalIgnoreCase))
        {
            return sourcesFile.Sources
                .Where(source => !string.IsNullOrWhiteSpace(source.StreamUrl)
                    && !string.Equals(source.PeerName, primaryPeer, StringComparison.OrdinalIgnoreCase))
                .Select((source, index) => BuildMediaSource(item, source, index, primaryPeer))
                .ToList();
        }

        if (!string.Equals(sourcesFile.ItemType, "Series", StringComparison.OrdinalIgnoreCase) ||
            item.ParentIndexNumber is null ||
            item.IndexNumber is null)
        {
            return [];
        }

        var episodeGroup = sourcesFile.EpisodeSources.FirstOrDefault(group =>
            group.SeasonNumber == item.ParentIndexNumber.Value &&
            group.EpisodeNumber == item.IndexNumber.Value);

        if (episodeGroup is null)
        {
            return [];
        }

        return episodeGroup.Sources
            .Where(source => !string.IsNullOrWhiteSpace(source.StreamUrl)
                && !string.Equals(source.PeerName, primaryPeer, StringComparison.OrdinalIgnoreCase))
            .Select((source, index) => BuildMediaSource(item, source, index, primaryPeer))
            .ToList();
    }

    /// <inheritdoc />
    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        return Task.FromResult<ILiveStream>(null!);
    }

    private static MediaSourceInfo BuildMediaSource(BaseItem item, ManifestSource source, int index, string primaryPeerName)
    {
        var mediaStreams = source.MediaStreams
            .Select(ToMediaStream)
            .ToList();

        var defaultAudioStream = mediaStreams.FirstOrDefault(stream =>
            stream.Type == MediaStreamType.Audio &&
            stream.IsDefault);

        var defaultSubtitleStream = mediaStreams.FirstOrDefault(stream =>
            stream.Type == MediaStreamType.Subtitle &&
            stream.IsDefault);

        return new MediaSourceInfo
        {
            Id = $"jellyfed:{item.Id}:{source.PeerName}",
            Name = BuildDisplayName(source, primaryPeerName),
            Path = source.StreamUrl!,
            Protocol = MediaProtocol.Http,
            Container = source.Container ?? item.Container,
            IsRemote = true,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            SupportsProbing = mediaStreams.Count == 0,
            Type = MediaSourceType.Default,
            RunTimeTicks = source.RuntimeTicks ?? item.RunTimeTicks,
            Bitrate = source.BitRate.HasValue ? (int?)Math.Min(source.BitRate.Value, int.MaxValue) : null,
            Size = source.SizeBytes,
            MediaStreams = mediaStreams,
            DefaultAudioStreamIndex = defaultAudioStream?.Index,
            DefaultSubtitleStreamIndex = defaultSubtitleStream?.Index,
            VideoType = VideoType.VideoFile,
            RequiredHttpHeaders = [],
            IgnoreDts = true,
            IgnoreIndex = false,
            BufferMs = null,
            AnalyzeDurationMs = null,
            ReadAtNativeFramerate = false,
            IsInfiniteStream = false,
            RequiresOpening = false,
            RequiresClosing = false,
            RequiresLooping = false,
            UseMostCompatibleTranscodingProfile = false,
            DefaultAudioIndexSource = index == 0 ? AudioIndexSource.Default : AudioIndexSource.None
        };
    }

    private static string? FindSourcesFilePath(BaseItem item)
    {
        foreach (var root in new[] { item.ContainingFolderPath, GetFolderPath(item.Path) }
                     .Where(static path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var current = root; !string.IsNullOrWhiteSpace(current); current = Path.GetDirectoryName(current))
            {
                var sidecarPath = Path.Combine(current!, StrmWriter.SourcesFileName);
                if (File.Exists(sidecarPath))
                {
                    return sidecarPath;
                }
            }
        }

        return null;
    }

    private static string? GetFolderPath(string? itemPath)
    {
        if (string.IsNullOrWhiteSpace(itemPath))
        {
            return null;
        }

        if (Directory.Exists(itemPath))
        {
            return itemPath;
        }

        return Path.GetDirectoryName(itemPath);
    }

    private static MediaStream ToMediaStream(MediaStreamInfoDto stream)
    {
        return new MediaStream
        {
            Index = stream.Index ?? -1,
            Type = ParseMediaStreamType(stream.Type),
            Codec = stream.Codec,
            Language = stream.Language,
            Title = stream.Title,
            IsDefault = stream.IsDefault,
            IsForced = stream.IsForced,
            IsExternal = false,
            SupportsExternalStream = false,
            Channels = stream.Channels,
            BitRate = stream.BitRate.HasValue ? (int?)Math.Min(stream.BitRate.Value, int.MaxValue) : null,
            Width = stream.Width,
            Height = stream.Height,
            SampleRate = stream.SampleRate
        };
    }

    private static MediaStreamType ParseMediaStreamType(string? type)
        => string.Equals(type, "Subtitle", StringComparison.OrdinalIgnoreCase)
            ? MediaStreamType.Subtitle
            : string.Equals(type, "Video", StringComparison.OrdinalIgnoreCase)
                ? MediaStreamType.Video
                : MediaStreamType.Audio;

    private static string BuildDisplayName(ManifestSource source, string primaryPeerName)
    {
        var parts = new List<string> { source.PeerName };

        if (string.Equals(source.PeerName, primaryPeerName, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("default");
        }

        if (source.Width is > 0 && source.Height is > 0)
        {
            parts.Add($"{source.Width}x{source.Height}");
        }

        if (!string.IsNullOrWhiteSpace(source.VideoCodec))
        {
            parts.Add(source.VideoCodec!);
        }

        if (!string.IsNullOrWhiteSpace(source.AudioCodec))
        {
            parts.Add(source.AudioCodec!);
        }

        return string.Join(" • ", parts);
    }
}
