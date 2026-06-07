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
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// Current breaking-protocol version exposed by the federation API.
    /// </summary>
    public const int CurrentProtocolVersion = 1;

    /// <summary>
    /// Canonical JellyFed route prefix.
    /// </summary>
    public const string RoutePrefix = "JellyFed";

    /// <summary>
    /// Canonical JellyFed route prefix with a leading slash for URL generation.
    /// </summary>
    public const string RoutePrefixPath = "/JellyFed";

    /// <summary>
    /// Gets the advertised federation capabilities for handshake/debug purposes.
    /// </summary>
    public static IReadOnlyList<string> Capabilities =>
    [
        "stable-instance-id",
        "per-peer-access-tokens",
        "flattened-multi-version-layout",
        "sync-anime-toggle",
        "stream-proxy",
        "image-proxy"
    ];

    /// <summary>
    /// Builds an absolute JellyFed path from a relative suffix.
    /// </summary>
    /// <param name="suffix">Suffix such as <c>catalog</c> or <c>/stream/abc</c>.</param>
    /// <returns>The absolute JellyFed path.</returns>
    public static string ToPath(string suffix)
        => RoutePrefixPath + NormalizeSuffix(suffix);

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
