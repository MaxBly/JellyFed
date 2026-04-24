using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyFed.Configuration;

namespace Jellyfin.Plugin.JellyFed.Sync;

/// <summary>
/// Resolves configured storage roots and tests whether library item paths belong to JellyFed-managed trees.
/// </summary>
public static class FederatedPathHelper
{
    /// <summary>
    /// Returns distinct absolute roots where federated .strm content may live (movies, series, anime).
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>Non-empty distinct directory paths.</returns>
    public static IReadOnlyList<string> GetFederatedContentRoots(PluginConfiguration config)
    {
        if (config is null)
        {
            return [];
        }

        return new[]
            {
                config.GetEffectiveMoviesRoot(),
                config.GetEffectiveSeriesRoot(),
                config.GetEffectiveAnimeRoot()
            }
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// True when the item path lies under one of the federated content roots (synced .strm trees).
    /// </summary>
    /// <param name="itemPath">Filesystem path of a library item.</param>
    /// <param name="config">Current plugin configuration.</param>
    /// <returns>True if the path is under movies, series, or anime federated roots.</returns>
    public static bool IsUnderFederatedContent(string? itemPath, PluginConfiguration config)
    {
        if (string.IsNullOrEmpty(itemPath) || config is null)
        {
            return false;
        }

        foreach (var root in GetFederatedContentRoots(config))
        {
            var r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var p = itemPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(p, r, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (itemPath.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                itemPath.StartsWith(r + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Deletes the per-peer subfolder under each content root (movies, series, anime), if present.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="peerName">Peer display name.</param>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Paths are built from admin-configured roots and sanitized peer folder names; peer names come from saved plugin configuration.")]
    public static void TryDeletePeerContentFolders(PluginConfiguration config, string peerName)
    {
        if (config is null || string.IsNullOrWhiteSpace(peerName))
        {
            return;
        }

        var seg = StrmWriter.SanitizePeerFolderSegment(peerName);
        foreach (var root in new[]
                 {
                     config.GetEffectiveMoviesRoot(),
                     config.GetEffectiveSeriesRoot(),
                     config.GetEffectiveAnimeRoot()
                 })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var dir = Path.Combine(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), seg);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
