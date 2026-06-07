using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
/// Writes flattened JellyFed .strm/.nfo materializations.
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
    /// Sanitizes a peer name for use as a single filesystem segment or file suffix.
    /// </summary>
    /// <param name="peerName">Peer display name.</param>
    /// <returns>Safe folder/file token.</returns>
    public static string SanitizePeerFolderSegment(string peerName)
    {
        if (string.IsNullOrWhiteSpace(peerName))
        {
            return "_peer";
        }

        return SanitizeName(peerName.Trim());
    }

    /// <summary>
    /// Writes or refreshes one movie source under the flattened item folder.
    /// </summary>
    /// <param name="contentRoot">Movie or anime root.</param>
    /// <param name="item">Catalog snapshot.</param>
    /// <param name="peer">Peer that owns this source.</param>
    /// <param name="entry">Logical manifest entry.</param>
    /// <param name="itemKey">Logical manifest key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The item folder path.</returns>
    public async Task<string> WriteMovieSourceAsync(
        string contentRoot,
        CatalogItemDto item,
        PeerConfiguration peer,
        ManifestEntry entry,
        string itemKey,
        CancellationToken cancellationToken)
    {
        var folderPath = Path.Combine(contentRoot, BuildItemFolderName(item, itemKey));
        Directory.CreateDirectory(folderPath);

        var source = FindSource(entry, peer.Name, item.JellyfinId);
        var fileBaseName = BuildMovieSourceFileName(item, peer.Name);
        var strmPath = Path.Combine(folderPath, $"{fileBaseName}.strm");
        var nfoPath = Path.Combine(folderPath, $"{fileBaseName}.nfo");

        await File.WriteAllTextAsync(strmPath, item.StreamUrl ?? string.Empty, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(nfoPath, BuildMovieNfo(item, entry, peer.Name, item.JellyfinId), Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);

        if (source is not null)
        {
            source.StrmPath = strmPath;
            source.NfoPath = nfoPath;
        }

        await DownloadArtworkIfMissingAsync(item.PosterUrl, Path.Combine(folderPath, "poster.jpg"), cancellationToken)
            .ConfigureAwait(false);
        await DownloadArtworkIfMissingAsync(item.BackdropUrl, Path.Combine(folderPath, "fanart.jpg"), cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Wrote movie source: {Title} ({PeerName}) -> {Path}", item.Title, peer.Name, strmPath);
        return folderPath;
    }

    /// <summary>
    /// Writes or refreshes one series source under the flattened series folder.
    /// </summary>
    /// <param name="contentRoot">Series or anime root.</param>
    /// <param name="item">Series catalog snapshot.</param>
    /// <param name="seasons">Season/episode snapshot for the peer source.</param>
    /// <param name="peer">Peer that owns this source.</param>
    /// <param name="entry">Logical manifest entry.</param>
    /// <param name="itemKey">Logical manifest key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The series folder path.</returns>
    public async Task<string> WriteSeriesSourceAsync(
        string contentRoot,
        CatalogItemDto item,
        SeasonsResponseDto seasons,
        PeerConfiguration peer,
        ManifestEntry entry,
        string itemKey,
        CancellationToken cancellationToken)
    {
        var folderPath = Path.Combine(contentRoot, BuildItemFolderName(item, itemKey));
        Directory.CreateDirectory(folderPath);

        var tvshowNfoPath = Path.Combine(folderPath, "tvshow.nfo");
        await File.WriteAllTextAsync(tvshowNfoPath, BuildSeriesNfo(item, entry), Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);

        await DownloadArtworkIfMissingAsync(item.PosterUrl, Path.Combine(folderPath, "poster.jpg"), cancellationToken)
            .ConfigureAwait(false);
        await DownloadArtworkIfMissingAsync(item.BackdropUrl, Path.Combine(folderPath, "fanart.jpg"), cancellationToken)
            .ConfigureAwait(false);

        string? firstStrmPath = null;
        string? firstNfoPath = null;

        foreach (var season in seasons.Seasons)
        {
            var seasonNum = season.SeasonNumber ?? 0;
            var seasonFolder = Path.Combine(folderPath, $"Season {seasonNum:D2}");
            Directory.CreateDirectory(seasonFolder);

            foreach (var episode in season.Episodes)
            {
                var episodeNum = episode.EpisodeNumber ?? 0;
                var fileBaseName = BuildEpisodeSourceFileName(seasonNum, episodeNum, episode.Title, peer.Name);
                var strmPath = Path.Combine(seasonFolder, $"{fileBaseName}.strm");
                var nfoPath = Path.Combine(seasonFolder, $"{fileBaseName}.nfo");

                await File.WriteAllTextAsync(strmPath, episode.StreamUrl ?? string.Empty, Encoding.UTF8, cancellationToken)
                    .ConfigureAwait(false);
                await File.WriteAllTextAsync(nfoPath, BuildEpisodeNfo(episode, seasonNum, entry, peer.Name, episode.JellyfinId), Encoding.UTF8, cancellationToken)
                    .ConfigureAwait(false);

                firstStrmPath ??= strmPath;
                firstNfoPath ??= nfoPath;
            }
        }

        var source = FindSource(entry, peer.Name, item.JellyfinId);
        if (source is not null)
        {
            source.StrmPath = firstStrmPath;
            source.NfoPath = firstNfoPath;
        }

        _logger.LogInformation("Wrote series source: {Title} ({PeerName}) -> {Path}", item.Title, peer.Name, folderPath);
        return folderPath;
    }

    /// <summary>
    /// Refreshes JellyFed provenance tags in all NFO files under an item folder.
    /// </summary>
    /// <param name="folderPath">Logical item folder.</param>
    /// <param name="entry">Manifest entry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "folderPath comes from JellyFed-owned manifest data.")]
    public async Task RefreshProvenanceAsync(
        string folderPath,
        ManifestEntry entry,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folderPath))
        {
            return;
        }

        foreach (var nfoPath in Directory.EnumerateFiles(folderPath, "*.nfo", SearchOption.AllDirectories))
        {
            var peerName = TryExtractPeerNameFromFile(nfoPath);
            var source = peerName is null
                ? null
                : entry.Sources.FirstOrDefault(s =>
                    string.Equals(s.PeerName, peerName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(SanitizePeerFolderSegment(s.PeerName), peerName, StringComparison.OrdinalIgnoreCase));

            await RewriteNfoProvenanceAsync(
                nfoPath,
                entry,
                source?.PeerName ?? entry.PeerName,
                source?.JellyfinId ?? entry.JellyfinId,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes every materialized file that belongs to one peer source under a logical item folder.
    /// </summary>
    /// <param name="folderPath">Logical item folder.</param>
    /// <param name="peerName">Peer to remove.</param>
    /// <returns>True when at least one file was deleted.</returns>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "folderPath comes from JellyFed-owned manifest data.")]
    public bool DeletePeerSourceFiles(string folderPath, string peerName)
    {
        if (!Directory.Exists(folderPath))
        {
            return false;
        }

        var suffix = $" [peer-{SanitizePeerFolderSegment(peerName)}]";
        var deleted = false;

        foreach (var filePath in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories).ToList())
        {
            var extension = Path.GetExtension(filePath);
            if (!string.Equals(extension, ".strm", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".nfo", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var nameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
            if (!nameWithoutExtension.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Delete(filePath);
            deleted = true;
        }

        DeleteEmptySeasonFolders(folderPath);
        return deleted;
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

    /// <summary>
    /// Deletes a logical item folder if no .strm versions remain.
    /// </summary>
    /// <param name="folderPath">Logical item folder.</param>
    /// <returns>True when the folder was deleted.</returns>
    public bool DeleteItemIfNoStreamsRemain(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            return false;
        }

        if (Directory.EnumerateFiles(folderPath, "*.strm", SearchOption.AllDirectories).Any())
        {
            return false;
        }

        Directory.Delete(folderPath, true);
        return true;
    }

    /// <summary>
    /// Renames peer-tagged files after an admin renames a peer.
    /// </summary>
    /// <param name="folderPath">Logical item folder.</param>
    /// <param name="oldName">Previous peer name.</param>
    /// <param name="newName">New peer name.</param>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "folderPath comes from JellyFed-owned manifest data.")]
    public void RenamePeerSourceFiles(string folderPath, string oldName, string newName)
    {
        if (!Directory.Exists(folderPath))
        {
            return;
        }

        var oldSuffix = $" [peer-{SanitizePeerFolderSegment(oldName)}]";
        var newSuffix = $" [peer-{SanitizePeerFolderSegment(newName)}]";

        foreach (var filePath in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories).ToList())
        {
            var extension = Path.GetExtension(filePath);
            if (!string.Equals(extension, ".strm", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".nfo", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var nameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
            if (!nameWithoutExtension.EndsWith(oldSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var renamedBase = nameWithoutExtension[..^oldSuffix.Length] + newSuffix;
            var renamedPath = Path.Combine(Path.GetDirectoryName(filePath) ?? folderPath, renamedBase + extension);
            if (!File.Exists(renamedPath))
            {
                File.Move(filePath, renamedPath);
            }
        }
    }

    private static string BuildItemFolderName(CatalogItemDto item, string itemKey)
    {
        var baseName = item.Year.HasValue
            ? $"{item.Title} ({item.Year.Value})"
            : item.Title;

        if (!string.IsNullOrWhiteSpace(item.TmdbId))
        {
            return SanitizeName($"{baseName} [tmdbid-{item.TmdbId}]");
        }

        return SanitizeName($"{baseName} [{itemKey.Replace(':', '-')}]");
    }

    private static string BuildMovieSourceFileName(CatalogItemDto item, string peerName)
    {
        var baseName = item.Year.HasValue
            ? $"{item.Title} ({item.Year.Value})"
            : item.Title;
        if (!string.IsNullOrWhiteSpace(item.Edition))
        {
            baseName += $" [edition-{item.Edition}]";
        }

        return SanitizeName($"{baseName} [peer-{SanitizePeerFolderSegment(peerName)}]");
    }

    private static string BuildEpisodeSourceFileName(int seasonNumber, int episodeNumber, string title, string peerName)
        => SanitizeName($"S{seasonNumber:D2}E{episodeNumber:D2} - {title} [peer-{SanitizePeerFolderSegment(peerName)}]");

    private static ManifestSource? FindSource(ManifestEntry entry, string peerName, string jellyfinId)
        => entry.Sources.FirstOrDefault(source =>
            string.Equals(source.PeerName, peerName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(source.JellyfinId, jellyfinId, StringComparison.Ordinal));

    private async Task DownloadArtworkIfMissingAsync(string? url, string localPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(url) || File.Exists(localPath))
        {
            return;
        }

        await _peerClient.DownloadImageAsync(url, localPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task RewriteNfoProvenanceAsync(
        string nfoPath,
        ManifestEntry entry,
        string peerName,
        string jellyfinId,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(nfoPath))
        {
            return;
        }

        var xml = await File.ReadAllTextAsync(nfoPath, cancellationToken).ConfigureAwait(false);
        var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var root = doc.Root;
        if (root is null)
        {
            return;
        }

        ApplyProvenance(root, entry, peerName, jellyfinId);
        await File.WriteAllTextAsync(nfoPath, doc.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildMovieNfo(CatalogItemDto item, ManifestEntry entry, string peerName, string jellyfinId)
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
            BuildUniqueIds(item));

        ApplyProvenance(movieEl, entry, peerName, jellyfinId);

        var fileInfo = BuildFileInfo(item.VideoCodec, item.Width, item.Height, item.MediaStreams, item.AudioCodec);
        if (fileInfo is not null)
        {
            movieEl.Add(fileInfo);
        }

        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), movieEl).ToString();
    }

    private static string BuildSeriesNfo(CatalogItemDto item, ManifestEntry entry)
    {
        var tvshowEl = new XElement(
            "tvshow",
            new XElement("title", item.Title),
            new XElement("originaltitle", item.OriginalTitle ?? item.Title),
            new XElement("year", item.Year),
            new XElement("plot", item.Overview ?? string.Empty),
            new XElement("rating", item.VoteAverage?.ToString("F1", CultureInfo.InvariantCulture)),
            item.Genres.Select(g => new XElement("genre", g)),
            BuildUniqueIds(item));

        ApplyProvenance(tvshowEl, entry, entry.PeerName, entry.JellyfinId);

        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), tvshowEl).ToString();
    }

    private static string BuildEpisodeNfo(EpisodeDto episode, int seasonNumber, ManifestEntry entry, string peerName, string jellyfinId)
    {
        var episodeEl = new XElement(
            "episodedetails",
            new XElement("title", episode.Title),
            new XElement("season", seasonNumber),
            new XElement("episode", episode.EpisodeNumber),
            new XElement("plot", episode.Overview ?? string.Empty),
            new XElement("aired", episode.AirDate ?? string.Empty),
            new XElement("runtime", episode.RuntimeMinutes));

        ApplyProvenance(episodeEl, entry, peerName, jellyfinId);

        var fileInfo = BuildFileInfo(episode.VideoCodec, episode.Width, episode.Height, episode.MediaStreams, episode.AudioCodec);
        if (fileInfo is not null)
        {
            episodeEl.Add(fileInfo);
        }

        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), episodeEl).ToString();
    }

    private static void ApplyProvenance(XElement root, ManifestEntry entry, string peerName, string jellyfinId)
    {
        root.Elements("jellyfed_peer").Remove();
        root.Elements("jellyfed_id").Remove();
        root.Elements("jellyfed_source_count").Remove();

        foreach (var studio in root.Elements("studio")
                     .Where(static element => element.Value.StartsWith("JellyFed:", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            studio.Remove();
        }

        foreach (var tag in root.Elements("tag")
                     .Where(static element =>
                         string.Equals(element.Value, "JellyFed", StringComparison.OrdinalIgnoreCase) ||
                         element.Value.StartsWith("JellyFed:", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            tag.Remove();
        }

        root.Add(new XElement("jellyfed_peer", peerName));
        root.Add(new XElement("jellyfed_id", jellyfinId));
        root.Add(new XElement("jellyfed_source_count", entry.Sources.Count));
        root.Add(new XElement("tag", "JellyFed"));

        if (entry.Sources.Count > 1)
        {
            root.Add(new XElement("tag", "JellyFed:multi-source"));
        }

        foreach (var source in entry.Sources
                     .OrderByDescending(SourcePixelCount)
                     .ThenBy(source => source.PeerName, StringComparer.OrdinalIgnoreCase))
        {
            root.Add(new XElement("studio", $"JellyFed:{source.PeerName}"));
            root.Add(new XElement("tag", $"JellyFed:source:{source.PeerName}"));
        }
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

        bool hasAudio = false;
        foreach (var stream in mediaStreams)
        {
            if (string.Equals(stream.Type, "Audio", StringComparison.OrdinalIgnoreCase))
            {
                hasAudio = true;
                var audioEl = new XElement("audio");
                if (!string.IsNullOrEmpty(stream.Codec))
                {
                    audioEl.Add(new XElement("codec", stream.Codec));
                }

                if (!string.IsNullOrEmpty(stream.Language))
                {
                    audioEl.Add(new XElement("language", stream.Language));
                }

                if (!string.IsNullOrEmpty(stream.Title))
                {
                    audioEl.Add(new XElement("title", stream.Title));
                }

                streamdetails.Add(audioEl);
            }
            else if (string.Equals(stream.Type, "Subtitle", StringComparison.OrdinalIgnoreCase))
            {
                var subEl = new XElement("subtitle");
                if (!string.IsNullOrEmpty(stream.Codec))
                {
                    subEl.Add(new XElement("codec", stream.Codec));
                }

                if (!string.IsNullOrEmpty(stream.Language))
                {
                    subEl.Add(new XElement("language", stream.Language));
                }

                if (!string.IsNullOrEmpty(stream.Title))
                {
                    subEl.Add(new XElement("title", stream.Title));
                }

                streamdetails.Add(subEl);
            }
        }

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

    private static string? TryExtractPeerNameFromFile(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        const string Marker = " [peer-";
        var markerIndex = name.LastIndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0 || !name.EndsWith(']'))
        {
            return null;
        }

        return name[(markerIndex + Marker.Length)..^1];
    }

    private static void DeleteEmptySeasonFolders(string folderPath)
    {
        foreach (var dir in Directory.EnumerateDirectories(folderPath, "Season *", SearchOption.TopDirectoryOnly).ToList())
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
    }

    private static int SourcePixelCount(ManifestSource source)
        => (source.Width ?? 0) * (source.Height ?? 0);

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
