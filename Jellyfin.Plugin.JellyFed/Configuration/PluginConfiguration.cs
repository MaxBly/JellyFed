using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Jellyfin.Plugin.JellyFed;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyFed.Configuration;

/// <summary>
/// JellyFed plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        SchemaVersion = FederationProtocol.CurrentSchemaVersion;
        InstanceId = string.Empty;
        Peers = [];
        BlockedPeerUrls = [];
        SyncIntervalHours = 6;
        LibraryPath = string.Empty; // Set to {DataPath}/jellyfed-library on first startup by Plugin.cs
        FederationToken = string.Empty;
        SelfUrl = string.Empty;
        SelfName = string.Empty;
        JellyfinApiKey = string.Empty;
        MoviesRootPath = string.Empty;
        SeriesRootPath = string.Empty;
        AnimeRootPath = string.Empty;
    }

    /// <summary>
    /// Gets or sets the schema version of this persisted configuration document.
    /// </summary>
    public int SchemaVersion { get; set; }

    /// <summary>
    /// Gets or sets the stable local instance identifier used for federation handshakes.
    /// Generated once and kept across upgrades.
    /// </summary>
    public string InstanceId { get; set; }

    /// <summary>
    /// Gets or sets the list of federated peers.
    /// </summary>
    /// <remarks>
    /// Must have a public setter so that Jellyfin's JSON/XML serializer can populate
    /// the collection on deserialization. CA2227/CA1002 suppressed intentionally.
    /// </remarks>
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Required for Jellyfin plugin config deserialization.")]
    [SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "Required for Jellyfin plugin config deserialization.")]
    public List<PeerConfiguration> Peers { get; set; }

    /// <summary>
    /// Gets or sets the sync interval in hours.
    /// </summary>
    public int SyncIntervalHours { get; set; }

    /// <summary>
    /// Gets or sets the directory for JellyFed metadata (.jellyfed-manifest.json, .jellyfed-peers.json).
    /// Default: {DataPath}/jellyfed-library. Movies/Series/Anime .strm trees use <see cref="GetEffectiveMoviesRoot"/> etc.
    /// </summary>
    public string LibraryPath { get; set; }

    /// <summary>
    /// Gets or sets the root folder for federated movies (.strm). When empty, defaults to {LibraryPath}/Films.
    /// </summary>
    public string MoviesRootPath { get; set; }

    /// <summary>
    /// Gets or sets the root folder for federated TV series. When empty, defaults to {LibraryPath}/Series.
    /// </summary>
    public string SeriesRootPath { get; set; }

    /// <summary>
    /// Gets or sets the root folder for federated anime (movies or series classified via genres).
    /// When empty, defaults to {LibraryPath}/Animes.
    /// </summary>
    public string AnimeRootPath { get; set; }

    /// <summary>
    /// Gets or sets the federation token exposed by this instance to peers.
    /// </summary>
    public string FederationToken { get; set; }

    /// <summary>
    /// Gets or sets URLs of peers that were manually removed and must not be auto-registered again.
    /// </summary>
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Required for Jellyfin plugin config deserialization.")]
    [SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "Required for Jellyfin plugin config deserialization.")]
    public List<string> BlockedPeerUrls { get; set; }

    /// <summary>
    /// Gets or sets the URL by which this instance is reachable from peers (used for auto-registration).
    /// </summary>
    public string SelfUrl { get; set; }

    /// <summary>
    /// Gets or sets the display name this instance uses when registering itself on peers.
    /// Defaults to "JellyFed" if left empty.
    /// </summary>
    public string SelfName { get; set; }

    /// <summary>
    /// Gets or sets a Jellyfin API key used server-side to redirect stream requests
    /// through Jellyfin's native pipeline (enabling transcoding).
    /// Never stored in .strm files — only used server-side in HTTP redirects.
    /// Create one in Dashboard → API Keys.
    /// </summary>
    public string JellyfinApiKey { get; set; }

    /// <summary>
    /// Effective root for movie .strm content (per-peer subfolders are created under this path).
    /// </summary>
    /// <returns>Absolute path, or empty when <see cref="LibraryPath"/> is not set and no override is configured.</returns>
    public string GetEffectiveMoviesRoot()
    {
        if (!string.IsNullOrWhiteSpace(MoviesRootPath))
        {
            return MoviesRootPath.Trim();
        }

        return CombineUnderLibrary("Films");
    }

    /// <summary>
    /// Effective root for TV series .strm content.
    /// </summary>
    /// <returns>Absolute path, or empty when <see cref="LibraryPath"/> is not set and no override is configured.</returns>
    public string GetEffectiveSeriesRoot()
    {
        if (!string.IsNullOrWhiteSpace(SeriesRootPath))
        {
            return SeriesRootPath.Trim();
        }

        return CombineUnderLibrary("Series");
    }

    /// <summary>
    /// Effective root for anime (movies or series with an Anime-related genre).
    /// </summary>
    /// <returns>Absolute path, or empty when <see cref="LibraryPath"/> is not set and no override is configured.</returns>
    public string GetEffectiveAnimeRoot()
    {
        if (!string.IsNullOrWhiteSpace(AnimeRootPath))
        {
            return AnimeRootPath.Trim();
        }

        return CombineUnderLibrary("Animes");
    }

    private string CombineUnderLibrary(string segment)
    {
        if (string.IsNullOrWhiteSpace(LibraryPath))
        {
            return string.Empty;
        }

        return Path.Combine(LibraryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), segment);
    }
}
