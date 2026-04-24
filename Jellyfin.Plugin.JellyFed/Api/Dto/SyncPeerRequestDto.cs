namespace Jellyfin.Plugin.JellyFed.Api.Dto;

/// <summary>
/// Request body for POST /JellyFed/v1/peer/sync.
/// </summary>
public class SyncPeerRequestDto
{
    /// <summary>Gets or sets the peer name to sync, or null to sync all peers.</summary>
    public string? PeerName { get; set; }
}
