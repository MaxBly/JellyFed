using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyFed.Api.Dto;

/// <summary>
/// Response envelope for GET /JellyFed/v1/peers/details.
/// </summary>
public class PeerDetailsResponseDto
{
    /// <summary>Gets the list of peers with full detail information.</summary>
    public IReadOnlyList<PeerDetailDto> Peers { get; init; } = [];

    /// <summary>Gets or sets the last global sync timestamp across all peers (ISO 8601), or null.</summary>
    public string? LastGlobalSyncAt { get; set; }
}
