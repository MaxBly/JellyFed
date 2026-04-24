using System;

namespace Jellyfin.Plugin.JellyFed.Sync;

/// <summary>
/// Runtime status of a federated peer, persisted between restarts.
/// </summary>
public class PeerStatus
{
    /// <summary>Gets or sets a value indicating whether the peer was reachable at last check.</summary>
    public bool Online { get; set; }

    /// <summary>Gets or sets the last time the peer was seen online (ISO 8601).</summary>
    public string? LastSeen { get; set; }

    /// <summary>Gets or sets the JellyFed version reported by the peer.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of movies in the peer's catalog.</summary>
    public int MovieCount { get; set; }

    /// <summary>Gets or sets the number of series in the peer's catalog.</summary>
    public int SeriesCount { get; set; }

    /// <summary>
    /// Gets or sets the ISO 8601 timestamp of the last attempted sync (success or failure).
    /// </summary>
    public string? LastSyncAt { get; set; }

    /// <summary>
    /// Gets or sets the status of the last sync attempt: "ok", "failed" or "never".
    /// </summary>
    public string LastSyncStatus { get; set; } = "never";

    /// <summary>
    /// Gets or sets the error message from the last failed sync, or null when the last sync succeeded.
    /// </summary>
    public string? LastSyncError { get; set; }

    /// <summary>
    /// Gets or sets the duration of the last sync attempt in milliseconds.
    /// </summary>
    public long LastSyncDurationMs { get; set; }

    /// <summary>
    /// Updates this status from a successful health + catalog response.
    /// </summary>
    /// <param name="version">JellyFed version string.</param>
    /// <param name="movieCount">Number of movies.</param>
    /// <param name="seriesCount">Number of series.</param>
    public void MarkOnline(string version, int movieCount, int seriesCount)
    {
        Online = true;
        LastSeen = DateTime.UtcNow.ToString("O");
        Version = version;
        MovieCount = movieCount;
        SeriesCount = seriesCount;
    }

    /// <summary>
    /// Marks the peer as offline without changing catalog size.
    /// </summary>
    public void MarkOffline()
    {
        Online = false;
    }

    /// <summary>
    /// Records a successful sync. Updates LastSyncAt/Status/DurationMs and clears the previous error.
    /// </summary>
    /// <param name="durationMs">Sync duration in milliseconds.</param>
    public void MarkSynced(long durationMs)
    {
        LastSyncAt = DateTime.UtcNow.ToString("O");
        LastSyncStatus = "ok";
        LastSyncError = null;
        LastSyncDurationMs = durationMs;
    }

    /// <summary>
    /// Records a failed sync attempt.
    /// </summary>
    /// <param name="error">Error message to surface in the admin UI.</param>
    /// <param name="durationMs">Sync duration in milliseconds.</param>
    public void MarkSyncFailed(string error, long durationMs)
    {
        LastSyncAt = DateTime.UtcNow.ToString("O");
        LastSyncStatus = "failed";
        LastSyncError = error;
        LastSyncDurationMs = durationMs;
    }
}
