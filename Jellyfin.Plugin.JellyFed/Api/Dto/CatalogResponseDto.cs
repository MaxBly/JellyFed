using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyFed.Api.Dto;

/// <summary>
/// Response envelope for the /JellyFed/catalog endpoint.
/// </summary>
public class CatalogResponseDto
{
    /// <summary>Gets or sets the total number of items (before pagination).</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets the catalog items for this page.</summary>
    public IReadOnlyList<CatalogItemDto> Items { get; set; } = [];
}
