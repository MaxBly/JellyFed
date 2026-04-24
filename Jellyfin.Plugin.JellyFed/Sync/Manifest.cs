using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Jellyfin.Plugin.JellyFed;

namespace Jellyfin.Plugin.JellyFed.Sync;

/// <summary>
/// Persisted manifest of all items written by JellyFed.
/// Keyed by TMDB ID ("tmdb:12345") or "no-tmdb:{peer}:{id}" when unavailable.
/// </summary>
public class Manifest
{
    /// <summary>Gets or sets the schema version of this persisted document.</summary>
    public int SchemaVersion { get; set; } = FederationProtocol.CurrentSchemaVersion;

    /// <summary>Gets or sets the synced movies: dedup key → ManifestEntry.</summary>
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Required so persisted manifest documents can be migrated and deserialized safely.")]
    public Dictionary<string, ManifestEntry> Movies { get; set; } = [];

    /// <summary>Gets or sets the synced series: dedup key → ManifestEntry.</summary>
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Required so persisted manifest documents can be migrated and deserialized safely.")]
    public Dictionary<string, ManifestEntry> Series { get; set; } = [];
}
