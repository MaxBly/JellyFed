using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyFed;

/// <summary>
/// Shared constants for the JellyFed federation protocol and route layout.
/// </summary>
public static class FederationProtocol
{
    /// <summary>
    /// Current persisted schema version for JellyFed-owned documents.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Current breaking-protocol version exposed by the federation API.
    /// </summary>
    public const int CurrentProtocolVersion = 1;

    /// <summary>
    /// Legacy unversioned route prefix kept as an alias for backward compatibility.
    /// </summary>
    public const string LegacyRoutePrefix = "JellyFed";

    /// <summary>
    /// Versioned route prefix that is authoritative for v1 peers.
    /// </summary>
    public const string V1RoutePrefix = "JellyFed/v1";

    /// <summary>
    /// Legacy route prefix with a leading slash for URL generation.
    /// </summary>
    public const string LegacyRoutePrefixPath = "/JellyFed";

    /// <summary>
    /// Versioned route prefix with a leading slash for URL generation.
    /// </summary>
    public const string V1RoutePrefixPath = "/JellyFed/v1";

    /// <summary>
    /// Gets the route prefixes supported by this plugin, ordered by preference.
    /// </summary>
    public static IReadOnlyList<string> SupportedRoutePrefixes =>
        [V1RoutePrefixPath, LegacyRoutePrefixPath];

    /// <summary>
    /// Gets the advertised federation capabilities for handshake/debug purposes.
    /// </summary>
    public static IReadOnlyList<string> Capabilities =>
        [
            "schema-versioning",
            "versioned-routes",
            "legacy-route-aliases",
            "stable-instance-id",
            "per-peer-access-tokens",
            "per-peer-roots",
            "sync-anime-toggle",
            "stream-proxy",
            "image-proxy"
        ];

    /// <summary>
    /// Builds an absolute JellyFed v1 path from a relative suffix.
    /// </summary>
    /// <param name="suffix">Suffix such as <c>catalog</c> or <c>/stream/abc</c>.</param>
    /// <returns>The absolute v1 path.</returns>
    public static string ToV1Path(string suffix)
        => V1RoutePrefixPath + NormalizeSuffix(suffix);

    /// <summary>
    /// Builds an absolute legacy JellyFed path from a relative suffix.
    /// </summary>
    /// <param name="suffix">Suffix such as <c>catalog</c> or <c>/stream/abc</c>.</param>
    /// <returns>The absolute legacy path.</returns>
    public static string ToLegacyPath(string suffix)
        => LegacyRoutePrefixPath + NormalizeSuffix(suffix);

    private static string NormalizeSuffix(string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return string.Empty;
        }

        return suffix.Length > 0 && suffix[0] == '/'
            ? suffix
            : "/" + suffix;
    }
}
