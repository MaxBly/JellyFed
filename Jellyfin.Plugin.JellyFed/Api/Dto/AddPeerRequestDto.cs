namespace Jellyfin.Plugin.JellyFed.Api.Dto;

/// <summary>
/// Request payload for POST /JellyFed/v1/peers (add a peer from the admin UI).
/// </summary>
public class AddPeerRequestDto
{
    /// <summary>Gets or sets the display name for the peer.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the peer base URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the federation token provided by the remote peer.</summary>
    public string FederationToken { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the peer should be enabled. Defaults to true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether movies should be synced. Defaults to true.</summary>
    public bool SyncMovies { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether series should be synced. Defaults to true.</summary>
    public bool SyncSeries { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether anime-classified items should be synced. Defaults to true.</summary>
    public bool SyncAnime { get; set; } = true;
}
