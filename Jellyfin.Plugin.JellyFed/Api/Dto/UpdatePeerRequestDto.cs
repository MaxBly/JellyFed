namespace Jellyfin.Plugin.JellyFed.Api.Dto;

/// <summary>
/// Partial update payload for PATCH /JellyFed/peer/{name}.
/// All fields are optional — only properties that are not null are applied.
/// </summary>
public class UpdatePeerRequestDto
{
    /// <summary>Gets or sets the new display name (triggers folder/manifest rename), or null to keep.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the new peer base URL, or null to keep.</summary>
    public string? Url { get; set; }

    /// <summary>Gets or sets the new federation token to use when querying this peer, or null to keep.</summary>
    public string? FederationToken { get; set; }

    /// <summary>Gets or sets a value to enable/disable sync for this peer, or null to keep.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Gets or sets a value to toggle movie sync, or null to keep.</summary>
    public bool? SyncMovies { get; set; }

    /// <summary>Gets or sets a value to toggle series sync, or null to keep.</summary>
    public bool? SyncSeries { get; set; }

    /// <summary>Gets or sets a value to toggle anime sync, or null to keep.</summary>
    public bool? SyncAnime { get; set; }
}
