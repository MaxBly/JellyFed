using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyFed.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyFed.Sync;

/// <summary>
/// Scheduled task that synchronizes catalogs from all configured federated peers.
/// Exposes <see cref="SyncPeerAsync"/> so admin endpoints can re-use the same per-peer
/// pipeline without going through the scheduler queue.
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
    private readonly ILogger<FederationSyncTask> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationSyncTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of <see cref="ILibraryManager"/>.</param>
    /// <param name="peerClient">Instance of <see cref="PeerClient"/>.</param>
    /// <param name="strmWriter">Instance of <see cref="StrmWriter"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{FederationSyncTask}"/> interface.</param>
    public FederationSyncTask(
        ILibraryManager libraryManager,
        PeerClient peerClient,
        StrmWriter strmWriter,
        ILogger<FederationSyncTask> logger)
    {
        _libraryManager = libraryManager;
        _peerClient = peerClient;
        _strmWriter = strmWriter;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "JellyFed — Sync federated catalogs";

    /// <inheritdoc />
    public string Key => "JellyFedSync";

    /// <inheritdoc />
    public string Description => "Fetches catalogs from all configured peers and generates .strm files.";

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

        var libraryPath = config.LibraryPath;
        if (string.IsNullOrWhiteSpace(libraryPath))
        {
            _logger.LogWarning("JellyFed sync: LibraryPath is not configured.");
            return;
        }

        Directory.CreateDirectory(libraryPath);

        var manifest = LoadManifest(libraryPath);
        var localTmdbIds = BuildLocalTmdbIds(config);
        var states = PeerStateStore.Load(libraryPath);

        var allSeenMovieKeys = new HashSet<string>(StringComparer.Ordinal);
        var allSeenSeriesKeys = new HashSet<string>(StringComparer.Ordinal);

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
                localTmdbIds,
                allSeenMovieKeys,
                allSeenSeriesKeys,
                cancellationToken).ConfigureAwait(false);

            // Persist per-peer status so the admin UI reflects success/failure instantly.
            if (!states.TryGetValue(peer.Name, out var status))
            {
                status = new PeerStatus();
                states[peer.Name] = status;
            }

            if (peerResult.Error is null)
            {
                status.MarkSynced(peerResult.DurationMs);
            }
            else
            {
                status.MarkSyncFailed(peerResult.Error, peerResult.DurationMs);
            }

            peerIndex++;
            progress.Report((double)peerIndex / Math.Max(1, totalPeers) * 90);
        }

        // Prune globally: any manifest entry whose key wasn't seen in any enabled peer's pass.
        PruneDeleted(manifest.Movies, allSeenMovieKeys);
        PruneDeleted(manifest.Series, allSeenSeriesKeys);

        SaveManifest(libraryPath, manifest);
        PeerStateStore.Save(libraryPath, states);

        progress.Report(95);
        _logger.LogInformation("JellyFed sync: triggering Jellyfin library scan.");
        _libraryManager.QueueLibraryScan();

        progress.Report(100);
        _logger.LogInformation("JellyFed sync: complete.");
    }

    /// <summary>
    /// Runs the full sync pipeline for a single peer and returns a summary result.
    /// Used by <see cref="ExecuteAsync"/> and by the admin <c>/peer/{name}/sync</c> endpoint.
    /// Callers that invoke this independently are responsible for persisting the manifest
    /// and peer state afterwards (see <see cref="SyncPeerAsync"/>).
    /// </summary>
    /// <param name="peer">The peer to sync.</param>
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
        var manifest = LoadManifest(config.LibraryPath);
        var localTmdbIds = BuildLocalTmdbIds(config);
        var states = PeerStateStore.Load(config.LibraryPath);

        // For single-peer sync, only consider this peer's keys when pruning so we never
        // touch entries belonging to other peers.
        var seenMovieKeys = new HashSet<string>(StringComparer.Ordinal);
        var seenSeriesKeys = new HashSet<string>(StringComparer.Ordinal);

        var result = await SyncSinglePeerAsync(
            peer,
            config,
            manifest,
            localTmdbIds,
            seenMovieKeys,
            seenSeriesKeys,
            cancellationToken).ConfigureAwait(false);

        // Prune only this peer's stale entries.
        result.Pruned += PruneDeletedForPeer(manifest.Movies, seenMovieKeys, peer.Name);
        result.Pruned += PruneDeletedForPeer(manifest.Series, seenSeriesKeys, peer.Name);

        if (!states.TryGetValue(peer.Name, out var status))
        {
            status = new PeerStatus();
            states[peer.Name] = status;
        }

        if (result.Error is null)
        {
            status.MarkSynced(result.DurationMs);
        }
        else
        {
            status.MarkSyncFailed(result.Error, result.DurationMs);
        }

        SaveManifest(config.LibraryPath, manifest);
        PeerStateStore.Save(config.LibraryPath, states);

        _libraryManager.QueueLibraryScan();

        return result;
    }

    private async Task<PeerSyncResult> SyncSinglePeerAsync(
        PeerConfiguration peer,
        PluginConfiguration config,
        Manifest manifest,
        HashSet<string> localTmdbIds,
        HashSet<string> seenMovieKeys,
        HashSet<string> seenSeriesKeys,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var result = new PeerSyncResult();

        try
        {
            _logger.LogInformation("JellyFed sync: starting peer {PeerName}", peer.Name);

            var catalog = await _peerClient.GetCatalogAsync(peer, null, cancellationToken)
                .ConfigureAwait(false);

            if (catalog is null)
            {
                result.Error = "Peer unreachable.";
                _logger.LogWarning("JellyFed sync: could not reach peer {PeerName}, skipping.", peer.Name);
                return result;
            }

            var peerSeg = StrmWriter.SanitizePeerFolderSegment(peer.Name);

            foreach (var item in catalog.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var key = ManifestKey(item.TmdbId, peer.Name, item.JellyfinId);
                var isAnime = CatalogItemClassifier.IsAnime(item);

                // Skip items already owned locally (same TMDB ID in local library).
                if (!string.IsNullOrEmpty(item.TmdbId) && localTmdbIds.Contains(item.TmdbId))
                {
                    if (item.Type == "Movie")
                    {
                        result.SkippedMovies++;
                    }
                    else
                    {
                        result.SkippedSeries++;
                    }

                    continue;
                }

                // Honor per-peer type toggles. Anime items require SyncAnime in addition to the
                // Movies/Series toggle so admins can keep syncing normal series while filtering
                // out anime (or vice versa).
                if (isAnime && !peer.SyncAnime)
                {
                    continue;
                }

                if (item.Type == "Movie" && peer.SyncMovies)
                {
                    var movieTypeRoot = isAnime
                        ? config.GetEffectiveAnimeRoot()
                        : config.GetEffectiveMoviesRoot();
                    if (string.IsNullOrWhiteSpace(movieTypeRoot))
                    {
                        _logger.LogWarning("JellyFed sync: movies root not configured, skipping movie.");
                        continue;
                    }

                    seenMovieKeys.Add(key);
                    if (manifest.Movies.TryGetValue(key, out var existingMovieEntry))
                    {
                        if (!string.IsNullOrEmpty(item.StreamUrl))
                        {
                            var folderName = Path.GetFileName(existingMovieEntry.Path);
                            var strmPath = Path.Combine(existingMovieEntry.Path, folderName + ".strm");
                            if (File.Exists(strmPath))
                            {
                                var current = await File.ReadAllTextAsync(strmPath, cancellationToken)
                                    .ConfigureAwait(false);
                                if (!string.Equals(current.Trim(), item.StreamUrl, StringComparison.Ordinal))
                                {
                                    await File.WriteAllTextAsync(strmPath, item.StreamUrl, System.Text.Encoding.UTF8, cancellationToken)
                                        .ConfigureAwait(false);
                                    _logger.LogDebug("JellyFed sync: updated STRM URL for '{Title}'", item.Title);
                                }
                            }
                        }

                        if (Directory.Exists(existingMovieEntry.Path))
                        {
                            await _strmWriter.UpdateMovieNfoAsync(existingMovieEntry.Path, item, peer, cancellationToken)
                                .ConfigureAwait(false);
                        }

                        result.SkippedMovies++;
                        continue;
                    }

                    var movieContentRoot = Path.Combine(movieTypeRoot, peerSeg);
                    Directory.CreateDirectory(movieContentRoot);
                    var folderPath = await _strmWriter.WriteMovieAsync(movieContentRoot, item, peer, cancellationToken)
                        .ConfigureAwait(false);

                    manifest.Movies[key] = new ManifestEntry
                    {
                        Path = folderPath,
                        PeerName = peer.Name,
                        JellyfinId = item.JellyfinId,
                        SyncedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                    };
                    result.AddedMovies++;
                }
                else if (item.Type == "Series" && peer.SyncSeries)
                {
                    var seriesTypeRoot = isAnime
                        ? config.GetEffectiveAnimeRoot()
                        : config.GetEffectiveSeriesRoot();
                    if (string.IsNullOrWhiteSpace(seriesTypeRoot))
                    {
                        _logger.LogWarning("JellyFed sync: series root not configured, skipping series.");
                        continue;
                    }

                    seenSeriesKeys.Add(key);
                    if (manifest.Series.ContainsKey(key))
                    {
                        result.SkippedSeries++;
                        continue;
                    }

                    var seasons = await _peerClient.GetSeasonsAsync(peer, item.JellyfinId, cancellationToken)
                        .ConfigureAwait(false);

                    if (seasons is null)
                    {
                        _logger.LogWarning("JellyFed sync: failed to fetch seasons for {Title}, skipping.", item.Title);
                        continue;
                    }

                    var seriesContentRoot = Path.Combine(seriesTypeRoot, peerSeg);
                    Directory.CreateDirectory(seriesContentRoot);
                    var folderPath = await _strmWriter.WriteSeriesAsync(seriesContentRoot, item, seasons, peer, cancellationToken)
                        .ConfigureAwait(false);

                    manifest.Series[key] = new ManifestEntry
                    {
                        Path = folderPath,
                        PeerName = peer.Name,
                        JellyfinId = item.JellyfinId,
                        SyncedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                    };
                    result.AddedSeries++;
                }
            }

            _logger.LogInformation(
                "JellyFed sync: peer {PeerName} — +{AddedMovies} movies, +{AddedSeries} series, skipped {SkipM}/{SkipS}",
                peer.Name,
                result.AddedMovies,
                result.AddedSeries,
                result.SkippedMovies,
                result.SkippedSeries);

            // Auto-registration: announce self so the peer can add us back.
            var selfUrl = config.SelfUrl;
            if (!string.IsNullOrWhiteSpace(selfUrl))
            {
                var selfName = string.IsNullOrWhiteSpace(config.SelfName)
                    ? Plugin.Instance!.Name
                    : config.SelfName;

                await _peerClient.RegisterOnPeerAsync(
                    peer,
                    selfName,
                    selfUrl,
                    config.FederationToken,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            result.Error = "Sync cancelled.";
            throw;
        }
#pragma warning disable CA1031 // Broad catch to record the error in PeerStatus without crashing the task.
        catch (Exception ex)
        {
            result.Error = ex.Message;
            _logger.LogError(ex, "JellyFed sync: peer {PeerName} failed.", peer.Name);
        }
#pragma warning restore CA1031

        sw.Stop();
        result.DurationMs = sw.ElapsedMilliseconds;
        return result;
    }

    private void PruneDeleted(Dictionary<string, ManifestEntry> entries, HashSet<string> seenKeys)
    {
        var toRemove = new List<string>();
        foreach (var (key, entry) in entries)
        {
            if (!seenKeys.Contains(key))
            {
                _strmWriter.DeleteItem(entry.Path);
                toRemove.Add(key);
            }
        }

        foreach (var key in toRemove)
        {
            entries.Remove(key);
        }
    }

    private int PruneDeletedForPeer(
        Dictionary<string, ManifestEntry> entries,
        HashSet<string> seenKeys,
        string peerName)
    {
        var toRemove = new List<string>();
        foreach (var (key, entry) in entries)
        {
            if (!string.Equals(entry.PeerName, peerName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!seenKeys.Contains(key))
            {
                _strmWriter.DeleteItem(entry.Path);
                toRemove.Add(key);
            }
        }

        foreach (var key in toRemove)
        {
            entries.Remove(key);
        }

        return toRemove.Count;
    }

    private HashSet<string> BuildLocalTmdbIds(PluginConfiguration pluginConfig)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            IsVirtualItem = false,
            Recursive = true
        });

        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item.Path) &&
                FederatedPathHelper.IsUnderFederatedContent(item.Path, pluginConfig))
            {
                continue;
            }

            var tmdbId = item.GetProviderId("Tmdb");
            if (!string.IsNullOrEmpty(tmdbId))
            {
                result.Add(tmdbId);
            }
        }

        return result;
    }

    private static string ManifestKey(string? tmdbId, string peerName, string jellyfinId)
    {
        var p = peerName.Trim();
        return string.IsNullOrEmpty(tmdbId)
            ? $"no-tmdb:{p}:{jellyfinId}"
            : $"tmdb:{tmdbId}:{p}";
    }

    private static Manifest LoadManifest(string libraryPath)
    {
        var path = Path.Combine(libraryPath, ManifestFileName);
        if (!File.Exists(path))
        {
            return new Manifest();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Manifest>(json, JsonOptions) ?? new Manifest();
        }
#pragma warning disable CA1031 // Intentionally broad: corrupt manifest must not crash sync.
        catch
        {
            return new Manifest();
        }
#pragma warning restore CA1031
    }

    private static void SaveManifest(string libraryPath, Manifest manifest)
    {
        var path = Path.Combine(libraryPath, ManifestFileName);
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        File.WriteAllText(path, json);
    }
}
