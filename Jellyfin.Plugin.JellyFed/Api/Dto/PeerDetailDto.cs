namespace Jellyfin.Plugin.JellyFed.Api.Dto;

/// <summary>
/// Full detail view of a federated peer: identity, health, sync state, remote catalog counts,
/// local synced counts per type, disk usage and effective folder paths.
/// </summary>
public class PeerDetailDto
{
    /// <summary>Gets or sets the peer display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the peer base URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the peer is enabled for sync.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets a value indicating whether movies are synced from this peer.</summary>
    public bool SyncMovies { get; set; }

    /// <summary>Gets or sets a value indicating whether TV series are synced from this peer.</summary>
    public bool SyncSeries { get; set; }

    /// <summary>Gets or sets a value indicating whether anime-classified items are synced.</summary>
    public bool SyncAnime { get; set; }

    /// <summary>Gets or sets a value indicating whether a per-peer access token is issued.</summary>
    public bool HasAccessToken { get; set; }

    /// <summary>Gets or sets a value indicating whether the peer was reachable at last heartbeat.</summary>
    public bool Online { get; set; }

    /// <summary>Gets or sets the last time the peer was seen online (ISO 8601), or null.</summary>
    public string? LastSeen { get; set; }

    /// <summary>Gets or sets the JellyFed version reported by the peer.</summary>
    public string? Version { get; set; }

    /// <summary>Gets or sets the ISO 8601 timestamp of the last sync attempt, or null.</summary>
    public string? LastSyncAt { get; set; }

    /// <summary>Gets or sets the status of the last sync attempt ("ok", "failed", "never").</summary>
    public string LastSyncStatus { get; set; } = "never";

    /// <summary>Gets or sets the error message from the last failed sync, or null.</summary>
    public string? LastSyncError { get; set; }

    /// <summary>Gets or sets the duration of the last sync in milliseconds.</summary>
    public long LastSyncDurationMs { get; set; }

    /// <summary>Gets or sets the number of movies advertised by the peer's remote catalog.</summary>
    public int PeerMovieCount { get; set; }

    /// <summary>Gets or sets the number of series advertised by the peer's remote catalog.</summary>
    public int PeerSeriesCount { get; set; }

    /// <summary>Gets or sets the number of movies synced locally from this peer.</summary>
    public int LocalMovieCount { get; set; }

    /// <summary>Gets or sets the number of non-anime series synced locally from this peer.</summary>
    public int LocalSeriesCount { get; set; }

    /// <summary>Gets or sets the number of anime items (movies + series) synced locally from this peer.</summary>
    public int LocalAnimeCount { get; set; }

    /// <summary>Gets or sets the total disk bytes occupied by this peer's synced files.</summary>
    public long LocalDiskBytes { get; set; }

    /// <summary>Gets or sets the absolute movies folder for this peer, or null when unconfigured.</summary>
    public string? MoviesFolder { get; set; }

    /// <summary>Gets or sets the absolute series folder for this peer, or null when unconfigured.</summary>
    public string? SeriesFolder { get; set; }

    /// <summary>Gets or sets the absolute anime folder for this peer, or null when unconfigured.</summary>
    public string? AnimeFolder { get; set; }
}
