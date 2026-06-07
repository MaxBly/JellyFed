using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyFed.Api.Dto;
using Jellyfin.Plugin.JellyFed.Audit;
using Jellyfin.Plugin.JellyFed.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyFed.Sync;

/// <summary>
/// Scheduled task that synchronizes catalogs from configured federated peers.
/// </summary>
public class FederationSyncTask : IScheduledTask
{
    /// <summary>
    /// File name of the persisted manifest in the library path.
    /// </summary>
    public const string ManifestFileName = ".jellyfed-manifest.json";

    private readonly ILibraryManager _libraryManager;
    private readonly PeerClient _peerClient;
    private readonly StrmWriter _strmWriter;
    private readonly AuditLogService _auditLogService;
    private readonly ILogger<FederationSyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationSyncTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin library manager.</param>
    /// <param name="peerClient">HTTP client for remote JellyFed peers.</param>
    /// <param name="strmWriter">Materializer for local .strm/NFO files.</param>
    /// <param name="auditLogService">Audit service.</param>
    /// <param name="logger">Logger instance.</param>
    public FederationSyncTask(
        ILibraryManager libraryManager,
        PeerClient peerClient,
        StrmWriter strmWriter,
        AuditLogService auditLogService,
        ILogger<FederationSyncTask> logger)
    {
        _libraryManager = libraryManager;
        _peerClient = peerClient;
        _strmWriter = strmWriter;
        _auditLogService = auditLogService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "JellyFed — Sync federated catalogs";

    /// <inheritdoc />
    public string Key => "JellyFedSync";

    /// <inheritdoc />
    public string Description => "Fetches catalogs from configured peers and generates .strm versions.";

    /// <inheritdoc />
    public string Category => "JellyFed";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(
                Plugin.Instance?.Configuration.SyncIntervalHours ?? 6).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            _logger.LogWarning("JellyFed sync: plugin configuration unavailable.");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.LibraryPath))
        {
            _logger.LogWarning("JellyFed sync: LibraryPath is not configured.");
            return;
        }

        Directory.CreateDirectory(config.LibraryPath);

        var manifest = ManifestStore.Load(config.LibraryPath);
        var states = PeerStateStore.Load(config.LibraryPath);
        var seenMovieSources = new HashSet<string>(StringComparer.Ordinal);
        var seenSeriesSources = new HashSet<string>(StringComparer.Ordinal);
        var peersEligibleForPrune = new HashSet<string>(
            config.Peers.Where(static peer => !peer.Enabled).Select(static peer => peer.Name),
            StringComparer.OrdinalIgnoreCase);

        int totalPeers = config.Peers.Count;
        int peerIndex = 0;

        foreach (var peer in config.Peers)
        {
            if (!peer.Enabled)
            {
                peerIndex++;
                progress.Report((double)peerIndex / Math.Max(1, totalPeers) * 90);
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var peerResult = await SyncSinglePeerAsync(
                peer,
                config,
                manifest,
                seenMovieSources,
                seenSeriesSources,
                cancellationToken).ConfigureAwait(false);

            if (peerResult.CanPrune)
            {
                peersEligibleForPrune.Add(peer.Name);
            }

            UpdatePeerStatus(states, peer.Name, peerResult);

            peerIndex++;
            progress.Report((double)peerIndex / Math.Max(1, totalPeers) * 90);
        }

        await PruneEntriesAsync(manifest.Movies, seenMovieSources, peersEligibleForPrune, "Movie", cancellationToken)
            .ConfigureAwait(false);
        await PruneEntriesAsync(manifest.Series, seenSeriesSources, peersEligibleForPrune, "Series", cancellationToken)
            .ConfigureAwait(false);

        ManifestStore.Save(config.LibraryPath, manifest);
        PeerStateStore.Save(config.LibraryPath, states);

        progress.Report(95);
        _logger.LogInformation("JellyFed sync: triggering Jellyfin library scan.");
        _libraryManager.QueueLibraryScan();

        progress.Report(100);
        _logger.LogInformation("JellyFed sync: complete.");
    }

    /// <summary>
    /// Runs the full sync pipeline for a single peer and returns a summary result.
    /// </summary>
    /// <param name="peer">Peer to synchronize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the sync attempt.</returns>
    public async Task<PeerSyncResult> SyncPeerAsync(
        PeerConfiguration peer,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(config.LibraryPath))
        {
            return new PeerSyncResult { Error = "Plugin configuration unavailable." };
        }

        Directory.CreateDirectory(config.LibraryPath);
        var manifest = ManifestStore.Load(config.LibraryPath);
        var states = PeerStateStore.Load(config.LibraryPath);

        var seenMovieSources = new HashSet<string>(StringComparer.Ordinal);
        var seenSeriesSources = new HashSet<string>(StringComparer.Ordinal);

        var result = await SyncSinglePeerAsync(
            peer,
            config,
            manifest,
            seenMovieSources,
            seenSeriesSources,
            cancellationToken).ConfigureAwait(false);

        if (result.CanPrune)
        {
            var prunePeers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { peer.Name };
            result.Pruned += await PruneEntriesAsync(manifest.Movies, seenMovieSources, prunePeers, "Movie", cancellationToken)
                .ConfigureAwait(false);
            result.Pruned += await PruneEntriesAsync(manifest.Series, seenSeriesSources, prunePeers, "Series", cancellationToken)
                .ConfigureAwait(false);
        }

        UpdatePeerStatus(states, peer.Name, result);

        ManifestStore.Save(config.LibraryPath, manifest);
        PeerStateStore.Save(config.LibraryPath, states);

        _libraryManager.QueueLibraryScan();

        return result;
    }

    private async Task<PeerSyncResult> SyncSinglePeerAsync(
        PeerConfiguration peer,
        PluginConfiguration config,
        Manifest manifest,
        HashSet<string> seenMovieSources,
        HashSet<string> seenSeriesSources,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var result = new PeerSyncResult();

        try
        {
            PeerIdentity.EnsurePeerId(peer);
            _logger.LogInformation("JellyFed sync: starting peer {PeerName}", peer.Name);
            _auditLogService.WritePeerEvent(peer, "peer.sync.started", $"Started sync for {peer.Name}.");

            var catalog = await _peerClient.GetCatalogAsync(peer, null, cancellationToken)
                .ConfigureAwait(false);

            if (catalog is null)
            {
                result.Error = "Peer unreachable.";
                _logger.LogWarning("JellyFed sync: could not reach peer {PeerName}, skipping.", peer.Name);
                _auditLogService.WritePeerEvent(
                    peer,
                    "peer.sync.unreachable",
                    $"Skipped sync for {peer.Name} because the peer was unreachable.",
                    AuditLogSeverities.Warning);
                return result;
            }

            result.CanPrune = true;

            foreach (var item in catalog.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var key = ManifestKey(item.TmdbId, peer.Name, item.JellyfinId);
                var source = BuildSource(item, peer.Name);
                var isAnime = CatalogItemClassifier.IsAnime(item);

                if (isAnime && !peer.SyncAnime)
                {
                    continue;
                }

                if (string.Equals(item.Type, "Movie", StringComparison.Ordinal) && peer.SyncMovies)
                {
                    var root = isAnime ? config.GetEffectiveAnimeRoot() : config.GetEffectiveMoviesRoot();
                    if (string.IsNullOrWhiteSpace(root))
                    {
                        _logger.LogWarning("JellyFed sync: movies root not configured, skipping movie.");
                        continue;
                    }

                    var added = !manifest.Movies.TryGetValue(key, out var entry);
                    entry ??= new ManifestEntry
                    {
                        PeerName = peer.Name,
                        JellyfinId = item.JellyfinId
                    };

                    UpsertSource(entry, source);
                    NormalizeDisplaySource(entry);
                    entry.SyncedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    entry.Path = await _strmWriter.WriteMovieSourceAsync(root, item, peer, entry, key, cancellationToken)
                        .ConfigureAwait(false);

                    manifest.Movies[key] = entry;
                    seenMovieSources.Add(SourceKey(key, peer.Name));
                    if (added)
                    {
                        result.AddedMovies++;
                    }
                    else
                    {
                        result.SkippedMovies++;
                    }

                    continue;
                }

                if (string.Equals(item.Type, "Series", StringComparison.Ordinal) && peer.SyncSeries)
                {
                    var root = isAnime ? config.GetEffectiveAnimeRoot() : config.GetEffectiveSeriesRoot();
                    if (string.IsNullOrWhiteSpace(root))
                    {
                        _logger.LogWarning("JellyFed sync: series root not configured, skipping series.");
                        continue;
                    }

                    var seasons = await _peerClient.GetSeasonsAsync(peer, item.JellyfinId, cancellationToken)
                        .ConfigureAwait(false);
                    if (seasons is null)
                    {
                        _logger.LogWarning("JellyFed sync: failed to fetch seasons for {Title}, skipping.", item.Title);
                        continue;
                    }

                    var added = !manifest.Series.TryGetValue(key, out var entry);
                    entry ??= new ManifestEntry
                    {
                        PeerName = peer.Name,
                        JellyfinId = item.JellyfinId
                    };

                    UpsertSource(entry, source);
                    NormalizeDisplaySource(entry);
                    entry.SyncedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    entry.Path = await _strmWriter.WriteSeriesSourceAsync(root, item, seasons, peer, entry, key, cancellationToken)
                        .ConfigureAwait(false);

                    manifest.Series[key] = entry;
                    seenSeriesSources.Add(SourceKey(key, peer.Name));
                    if (added)
                    {
                        result.AddedSeries++;
                    }
                    else
                    {
                        result.SkippedSeries++;
                    }
                }
            }

            _logger.LogInformation(
                "JellyFed sync: peer {PeerName} — +{AddedMovies} movies, +{AddedSeries} series, skipped {SkipM}/{SkipS}",
                peer.Name,
                result.AddedMovies,
                result.AddedSeries,
                result.SkippedMovies,
                result.SkippedSeries);
            _auditLogService.WritePeerEvent(
                peer,
                "peer.sync.completed",
                $"Completed sync for {peer.Name}.",
                details: new
                {
                    result.AddedMovies,
                    result.AddedSeries,
                    result.SkippedMovies,
                    result.SkippedSeries,
                    result.Pruned
                });
        }
        catch (OperationCanceledException)
        {
            result.Error = "Sync cancelled.";
            throw;
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            result.Error = ex.Message;
            _auditLogService.WritePeerEvent(
                peer,
                "peer.sync.failed",
                $"Sync failed for {peer.Name}: {ex.Message}",
                AuditLogSeverities.Error,
                new { error = ex.Message });
            _logger.LogError(ex, "JellyFed sync: peer {PeerName} failed.", peer.Name);
        }
#pragma warning restore CA1031

        sw.Stop();
        result.DurationMs = sw.ElapsedMilliseconds;
        return result;
    }

    private async Task<int> PruneEntriesAsync(
        Dictionary<string, ManifestEntry> entries,
        HashSet<string> seenSourceKeys,
        HashSet<string> peersEligibleForPrune,
        string itemType,
        CancellationToken cancellationToken)
    {
        var removedEntries = 0;

        foreach (var key in entries.Keys.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = entries[key];
            var sources = entry.Sources.ToList();
            var removedAnySource = false;

            foreach (var source in sources.ToList())
            {
                if (!peersEligibleForPrune.Contains(source.PeerName) ||
                    seenSourceKeys.Contains(SourceKey(key, source.PeerName)))
                {
                    continue;
                }

                _ = _strmWriter.DeletePeerSourceFiles(entry.Path, source.PeerName);
                sources.Remove(source);
                removedAnySource = true;
            }

            if (!removedAnySource)
            {
                continue;
            }

            entry.Sources = sources;
            if (entry.Sources.Count == 0)
            {
                _strmWriter.DeleteItem(entry.Path);
                entries.Remove(key);
                removedEntries++;
                continue;
            }

            NormalizeDisplaySource(entry);
            entry.SyncedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            await _strmWriter.RefreshProvenanceAsync(entry.Path, entry, cancellationToken)
                .ConfigureAwait(false);
            if (_strmWriter.DeleteItemIfNoStreamsRemain(entry.Path))
            {
                entries.Remove(key);
                removedEntries++;
            }
        }

        _logger.LogDebug("JellyFed sync: pruned {Count} {ItemType} entries.", removedEntries, itemType);
        return removedEntries;
    }

    private static ManifestSource BuildSource(CatalogItemDto item, string peerName)
        => new()
        {
            PeerName = peerName,
            JellyfinId = item.JellyfinId,
            StreamUrl = item.StreamUrl,
            Container = item.Container,
            VideoCodec = item.VideoCodec,
            AudioCodec = item.AudioCodec,
            Width = item.Width,
            Height = item.Height,
            AddedAt = item.AddedAt,
            UpdatedAt = item.UpdatedAt,
            RuntimeTicks = MinutesToTicks(item.RuntimeMinutes),
            BitRate = item.BitRate,
            SizeBytes = item.SizeBytes,
            VideoRange = item.VideoRange,
            Edition = item.Edition,
            MediaStreams = item.MediaStreams
        };

    private static long? MinutesToTicks(int? minutes)
        => minutes.HasValue ? TimeSpan.FromMinutes(minutes.Value).Ticks : null;

    private static void UpsertSource(ManifestEntry entry, ManifestSource source)
    {
        var sources = entry.Sources.ToList();
        var existing = sources.FirstOrDefault(s =>
            string.Equals(s.PeerName, source.PeerName, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            sources.Add(source);
            entry.Sources = OrderSources(sources);
            return;
        }

        source.StrmPath = existing.StrmPath;
        source.NfoPath = existing.NfoPath;
        var index = sources.IndexOf(existing);
        sources[index] = source;
        entry.Sources = OrderSources(sources);
    }

    private static void NormalizeDisplaySource(ManifestEntry entry)
    {
        if (entry.Sources.Count == 0)
        {
            entry.PeerName = string.Empty;
            entry.JellyfinId = string.Empty;
            return;
        }

        var display = entry.Sources
            .OrderByDescending(SourcePixelCount)
            .ThenByDescending(SourceUpdatedAt)
            .ThenBy(source => source.PeerName, StringComparer.OrdinalIgnoreCase)
            .First();

        entry.PeerName = display.PeerName;
        entry.JellyfinId = display.JellyfinId;
    }

    private static List<ManifestSource> OrderSources(IEnumerable<ManifestSource> sources)
        => sources
            .OrderByDescending(SourcePixelCount)
            .ThenByDescending(SourceUpdatedAt)
            .ThenBy(source => source.PeerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static void UpdatePeerStatus(Dictionary<string, PeerStatus> states, string peerName, PeerSyncResult result)
    {
        if (!states.TryGetValue(peerName, out var status))
        {
            status = new PeerStatus();
            states[peerName] = status;
        }

        if (result.Error is null)
        {
            status.MarkSynced(result.DurationMs);
        }
        else
        {
            status.MarkSyncFailed(result.Error, result.DurationMs);
        }
    }

    private static int SourcePixelCount(ManifestSource source)
        => (source.Width ?? 0) * (source.Height ?? 0);

    private static DateTime SourceUpdatedAt(ManifestSource source)
        => DateTime.TryParse(source.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updatedAt)
            ? updatedAt
            : DateTime.MinValue;

    private static string ManifestKey(string? tmdbId, string peerName, string jellyfinId)
    {
        var p = peerName.Trim();
        return string.IsNullOrEmpty(tmdbId)
            ? $"no-tmdb:{p}:{jellyfinId}"
            : $"tmdb:{tmdbId}";
    }

    private static string SourceKey(string itemKey, string peerName)
        => $"{itemKey}|{peerName.Trim()}";
}
