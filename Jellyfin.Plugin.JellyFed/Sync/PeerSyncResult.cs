namespace Jellyfin.Plugin.JellyFed.Sync;

/// <summary>
/// Summary of a single-peer sync pass. Returned by <see cref="FederationSyncTask.SyncPeerAsync"/>.
/// </summary>
public class PeerSyncResult
{
    /// <summary>Gets or sets the number of newly added movies.</summary>
    public int AddedMovies { get; set; }

    /// <summary>Gets or sets the number of newly added series.</summary>
    public int AddedSeries { get; set; }

    /// <summary>Gets or sets the number of movies skipped (already local or already synced).</summary>
    public int SkippedMovies { get; set; }

    /// <summary>Gets or sets the number of series skipped.</summary>
    public int SkippedSeries { get; set; }

    /// <summary>Gets or sets the number of stale manifest entries removed during this pass.</summary>
    public int Pruned { get; set; }

    /// <summary>Gets or sets the duration of the pass in milliseconds.</summary>
    public long DurationMs { get; set; }

    /// <summary>Gets or sets an error message when the pass failed; null on success.</summary>
    public string? Error { get; set; }
}
