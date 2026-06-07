using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyFed.Sync;

/// <summary>
/// Tracks one logical JellyFed item synced onto local disk.
/// <para>
/// <see cref="PeerName"/> and <see cref="JellyfinId"/> keep a stable default/display source for
/// legacy consumers; playback source selection is handled by Jellyfin's native version merging
/// over the materialized <c>.strm</c> files tracked in <see cref="Sources"/>.
/// </para>
/// </summary>
public class ManifestEntry
{
    /// <summary>Gets or sets the local folder path of the item.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the peer name of the default/display source.</summary>
    public string PeerName { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin item ID of the default/display source.</summary>
    public string JellyfinId { get; set; } = string.Empty;

    /// <summary>Gets or sets the ISO 8601 date of the last sync.</summary>
    public string SyncedAt { get; set; } = string.Empty;

    /// <summary>Gets or sets all currently known upstream sources for this logical item.</summary>
    public IReadOnlyList<ManifestSource> Sources { get; set; } = [];
}
