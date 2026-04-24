namespace Jellyfin.Plugin.JellyFed.Configuration;

/// <summary>
/// Configuration for a federated peer instance.
/// </summary>
public class PeerConfiguration
{
    /// <summary>
    /// Gets or sets the display name for this peer.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the base URL of the peer Jellyfin instance.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the federation API token for this peer.
    /// </summary>
    public string FederationToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether sync is enabled for this peer.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether movies should be synced from this peer.
    /// </summary>
    public bool SyncMovies { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether series should be synced from this peer.
    /// </summary>
    public bool SyncSeries { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether anime-classified items (movies or series
    /// whose genres include an anime tag) should be synced from this peer.
    /// </summary>
    public bool SyncAnime { get; set; } = true;

    /// <summary>
    /// Gets or sets the access token issued by this instance to this peer.
    /// The peer must present this token (not the global FederationToken) when
    /// querying our catalog. Null until the peer has completed auto-registration.
    /// Removing the peer revokes this token immediately.
    /// </summary>
    public string? AccessToken { get; set; }
}
