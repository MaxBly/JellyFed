namespace Jellyfin.Plugin.JellyFed.Api.Dto;

/// <summary>
/// Result of a single peer sync attempt exposed to the admin UI.
/// </summary>
public class PeerSyncResultDto
{
    /// <summary>Gets or sets the peer display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the status of the attempt: "ok", "failed" or "unreachable".</summary>
    public string Status { get; set; } = "ok";

    /// <summary>Gets or sets the number of new movies added during this run.</summary>
    public int AddedMovies { get; set; }

    /// <summary>Gets or sets the number of new series added during this run.</summary>
    public int AddedSeries { get; set; }

    /// <summary>Gets or sets the number of movies skipped (already local or already synced).</summary>
    public int SkippedMovies { get; set; }

    /// <summary>Gets or sets the number of series skipped.</summary>
    public int SkippedSeries { get; set; }

    /// <summary>Gets or sets the number of stale entries pruned from the manifest / disk.</summary>
    public int Pruned { get; set; }

    /// <summary>Gets or sets the sync duration in milliseconds.</summary>
    public long DurationMs { get; set; }

    /// <summary>Gets or sets the error message in case of failure, or null.</summary>
    public string? Error { get; set; }
}
