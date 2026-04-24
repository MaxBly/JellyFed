using System;
using Jellyfin.Plugin.JellyFed.Api.Dto;

namespace Jellyfin.Plugin.JellyFed.Sync;

/// <summary>
/// Routes catalog items into Movies / Series / Anime storage roots based on metadata.
/// </summary>
public static class CatalogItemClassifier
{
    /// <summary>
    /// Returns true when the item should be stored under the anime root (movies or series).
    /// Uses genre labels from Jellyfin (often includes "Anime" for Japanese animation).
    /// </summary>
    /// <param name="item">Catalog item from the remote peer.</param>
    /// <returns>True to use the configured anime root.</returns>
    public static bool IsAnime(CatalogItemDto item)
    {
        foreach (var g in item.Genres)
        {
            if (string.IsNullOrWhiteSpace(g))
            {
                continue;
            }

            if (g.Contains("Anime", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
