using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.JellyFed.Api.Dto;
using Jellyfin.Plugin.JellyFed.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyFed.Sync;

/// <summary>
/// Writes .strm, .nfo and artwork files for federated items.
/// </summary>
public class StrmWriter
{
    private readonly PeerClient _peerClient;
    private readonly ILogger<StrmWriter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmWriter"/> class.
    /// </summary>
    /// <param name="peerClient">Instance of <see cref="PeerClient"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{StrmWriter}"/> interface.</param>
    public StrmWriter(PeerClient peerClient, ILogger<StrmWriter> logger)
    {
        _peerClient = peerClient;
        _logger = logger;
    }

    /// <summary>
    /// Writes a movie item: folder, .strm, .nfo, poster and backdrop.
    /// </summary>
    /// <param name="libraryPath">Root library path.</param>
    /// <param name="item">The catalog item.</param>
    /// <param name="peer">The source peer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The folder path that was written.</returns>
    public async Task<string> WriteMovieAsync(
        string libraryPath,
        CatalogItemDto item,
        PeerConfiguration peer,
        CancellationToken cancellationToken)
    {
        var folderName = SanitizeName($"{item.Title} ({item.Year})");
        var folderPath = Path.Combine(libraryPath, "Films", folderName);
        Directory.CreateDirectory(folderPath);

        var strmPath = Path.Combine(folderPath, $"{folderName}.strm");
        await File.WriteAllTextAsync(strmPath, item.StreamUrl ?? string.Empty, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);

        var nfoPath = Path.Combine(folderPath, $"{folderName}.nfo");
        await File.WriteAllTextAsync(nfoPath, BuildMovieNfo(item, peer), Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);

        await DownloadArtworkAsync(item.PosterUrl, Path.Combine(folderPath, "poster.jpg"), cancellationToken)
            .ConfigureAwait(false);
        await DownloadArtworkAsync(item.BackdropUrl, Path.Combine(folderPath, "fanart.jpg"), cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Wrote movie: {Title} ({Year}) → {Path}", item.Title, item.Year, folderPath);
        return folderPath;
    }

    /// <summary>
    /// Writes a series item: folder, tvshow.nfo, poster and all seasons/episodes.
    /// </summary>
    /// <param name="libraryPath">Root library path.</param>
    /// <param name="item">The series catalog item.</param>
    /// <param name="seasons">The seasons and episodes.</param>
    /// <param name="peer">The source peer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The folder path that was written.</returns>
    public async Task<string> WriteSeriesAsync(
        string libraryPath,
        CatalogItemDto item,
        SeasonsResponseDto seasons,
        PeerConfiguration peer,
        CancellationToken cancellationToken)
    {
        var folderName = SanitizeName($"{item.Title} ({item.Year})");
        var folderPath = Path.Combine(libraryPath, "Series", folderName);
        Directory.CreateDirectory(folderPath);

        var nfoPath = Path.Combine(folderPath, "tvshow.nfo");
        await File.WriteAllTextAsync(nfoPath, BuildSeriesNfo(item, peer), Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);

        await DownloadArtworkAsync(item.PosterUrl, Path.Combine(folderPath, "poster.jpg"), cancellationToken)
            .ConfigureAwait(false);
        await DownloadArtworkAsync(item.BackdropUrl, Path.Combine(folderPath, "fanart.jpg"), cancellationToken)
            .ConfigureAwait(false);

        foreach (var season in seasons.Seasons)
        {
            var seasonNum = season.SeasonNumber ?? 0;
            var seasonFolder = Path.Combine(folderPath, $"Season {seasonNum:D2}");
            Directory.CreateDirectory(seasonFolder);

            foreach (var ep in season.Episodes)
            {
                var epNum = ep.EpisodeNumber ?? 0;
                var epName = SanitizeName($"S{seasonNum:D2}E{epNum:D2} - {ep.Title}");

                var strmPath = Path.Combine(seasonFolder, $"{epName}.strm");
                await File.WriteAllTextAsync(strmPath, ep.StreamUrl ?? string.Empty, Encoding.UTF8, cancellationToken)
                    .ConfigureAwait(false);

                var epNfoPath = Path.Combine(seasonFolder, $"{epName}.nfo");
                await File.WriteAllTextAsync(epNfoPath, BuildEpisodeNfo(ep, seasonNum, peer), Encoding.UTF8, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var epCount = seasons.Seasons.Sum(s => s.Episodes.Count);
        _logger.LogInformation(
            "Wrote series: {Title} — {SeasonCount} seasons, {EpCount} episodes → {Path}",
            item.Title,
            seasons.Seasons.Count,
            epCount,
            folderPath);

        return folderPath;
    }

    /// <summary>
    /// Rewrites the NFO file for an already-synced movie with fresh codec/track info.
    /// Called during sync for existing manifest entries so that library rescans pick up
    /// the correct stream metadata without requiring a full Reset Network.
    /// </summary>
    /// <param name="folderPath">The movie's existing folder path.</param>
    /// <param name="item">The catalog item from the latest catalog fetch.</param>
    /// <param name="peer">The source peer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task UpdateMovieNfoAsync(
        string folderPath,
        CatalogItemDto item,
        PeerConfiguration peer,
        CancellationToken cancellationToken)
    {
        var folderName = Path.GetFileName(folderPath);
        var nfoPath = Path.Combine(folderPath, $"{folderName}.nfo");
        await File.WriteAllTextAsync(nfoPath, BuildMovieNfo(item, peer), Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a previously synced item folder.
    /// </summary>
    /// <param name="folderPath">The item folder to remove.</param>
    public void DeleteItem(string folderPath)
    {
        if (Directory.Exists(folderPath))
        {
            Directory.Delete(folderPath, true);
            _logger.LogInformation("Deleted item: {Path}", folderPath);
        }
    }

    private async Task DownloadArtworkAsync(string? url, string localPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        await _peerClient.DownloadImageAsync(url, localPath, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildMovieNfo(CatalogItemDto item, PeerConfiguration peer)
    {
        var movieEl = new XElement(
            "movie",
            new XElement("title", item.Title),
            new XElement("originaltitle", item.OriginalTitle ?? item.Title),
            new XElement("year", item.Year),
            new XElement("plot", item.Overview ?? string.Empty),
            new XElement("runtime", item.RuntimeMinutes),
            new XElement("rating", item.VoteAverage?.ToString("F1", CultureInfo.InvariantCulture)),
            item.Genres.Select(g => new XElement("genre", g)),
            BuildUniqueIds(item),
            new XElement("jellyfed_peer", peer.Name),
            new XElement("jellyfed_id", item.JellyfinId));

        // Embed codec metadata so Jellyfin knows the format during library scan.
        // Without this, Jellyfin has no codec info for .strm remote URLs and defaults
        // to direct-play — the browser receives raw MKV/HEVC and crashes.
        var fileInfo = BuildFileInfo(item.VideoCodec, item.Width, item.Height, item.MediaStreams, item.AudioCodec);
        if (fileInfo is not null)
        {
            movieEl.Add(fileInfo);
        }

        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), movieEl).ToString();
    }

    private static string BuildSeriesNfo(CatalogItemDto item, PeerConfiguration peer)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(
                "tvshow",
                new XElement("title", item.Title),
                new XElement("originaltitle", item.OriginalTitle ?? item.Title),
                new XElement("year", item.Year),
                new XElement("plot", item.Overview ?? string.Empty),
                new XElement("rating", item.VoteAverage?.ToString("F1", CultureInfo.InvariantCulture)),
                item.Genres.Select(g => new XElement("genre", g)),
                BuildUniqueIds(item),
                new XElement("jellyfed_peer", peer.Name),
                new XElement("jellyfed_id", item.JellyfinId)));

        return doc.ToString();
    }

    private static string BuildEpisodeNfo(EpisodeDto ep, int seasonNumber, PeerConfiguration peer)
    {
        var epEl = new XElement(
            "episodedetails",
            new XElement("title", ep.Title),
            new XElement("season", seasonNumber),
            new XElement("episode", ep.EpisodeNumber),
            new XElement("plot", ep.Overview ?? string.Empty),
            new XElement("aired", ep.AirDate ?? string.Empty),
            new XElement("runtime", ep.RuntimeMinutes),
            new XElement("jellyfed_peer", peer.Name),
            new XElement("jellyfed_id", ep.JellyfinId));

        var fileInfo = BuildFileInfo(ep.VideoCodec, ep.Width, ep.Height, ep.MediaStreams, ep.AudioCodec);
        if (fileInfo is not null)
        {
            epEl.Add(fileInfo);
        }

        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), epEl).ToString();
    }

    private static XElement? BuildFileInfo(
        string? videoCodec,
        int? width,
        int? height,
        IReadOnlyList<MediaStreamInfoDto> mediaStreams,
        string? fallbackAudioCodec)
    {
        if (string.IsNullOrEmpty(videoCodec) && mediaStreams.Count == 0)
        {
            return null;
        }

        var videoEl = new XElement("video");
        if (!string.IsNullOrEmpty(videoCodec))
        {
            videoEl.Add(new XElement("codec", videoCodec));
        }

        if (width.HasValue)
        {
            videoEl.Add(new XElement("width", width.Value));
        }

        if (height.HasValue)
        {
            videoEl.Add(new XElement("height", height.Value));
        }

        var streamdetails = new XElement("streamdetails", videoEl);

        // Audio and subtitle tracks — written in stream order so Jellyfin respects
        // IsDefault/IsForced flags when presenting language/track selectors.
        bool hasAudio = false;
        foreach (var s in mediaStreams)
        {
            if (s.Type == "Audio")
            {
                hasAudio = true;
                var audioEl = new XElement("audio");
                if (!string.IsNullOrEmpty(s.Codec))
                {
                    audioEl.Add(new XElement("codec", s.Codec));
                }

                if (!string.IsNullOrEmpty(s.Language))
                {
                    audioEl.Add(new XElement("language", s.Language));
                }

                if (!string.IsNullOrEmpty(s.Title))
                {
                    audioEl.Add(new XElement("title", s.Title));
                }

                streamdetails.Add(audioEl);
            }
            else if (s.Type == "Subtitle")
            {
                var subEl = new XElement("subtitle");
                if (!string.IsNullOrEmpty(s.Language))
                {
                    subEl.Add(new XElement("language", s.Language));
                }

                if (!string.IsNullOrEmpty(s.Title))
                {
                    subEl.Add(new XElement("title", s.Title));
                }

                streamdetails.Add(subEl);
            }
        }

        // Fallback: if older source plugin didn't send MediaStreams, use the scalar AudioCodec.
        if (!hasAudio && !string.IsNullOrEmpty(fallbackAudioCodec))
        {
            streamdetails.Add(new XElement("audio", new XElement("codec", fallbackAudioCodec)));
        }

        return new XElement("fileinfo", streamdetails);
    }

    private static XElement[] BuildUniqueIds(CatalogItemDto item)
    {
        if (!string.IsNullOrEmpty(item.TmdbId) && !string.IsNullOrEmpty(item.ImdbId))
        {
            return
            [
                new XElement("uniqueid", new XAttribute("type", "tmdb"), new XAttribute("default", "true"), item.TmdbId),
                new XElement("uniqueid", new XAttribute("type", "imdb"), item.ImdbId)
            ];
        }

        if (!string.IsNullOrEmpty(item.TmdbId))
        {
            return [new XElement("uniqueid", new XAttribute("type", "tmdb"), new XAttribute("default", "true"), item.TmdbId)];
        }

        if (!string.IsNullOrEmpty(item.ImdbId))
        {
            return [new XElement("uniqueid", new XAttribute("type", "imdb"), new XAttribute("default", "true"), item.ImdbId)];
        }

        return [];
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return sb.ToString();
    }
}
