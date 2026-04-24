using System.Collections.Generic;
using Jellyfin.Plugin.JellyFed;

namespace Jellyfin.Plugin.JellyFed.Api.Dto;

/// <summary>
/// Handshake-oriented system information exposed by a JellyFed instance.
/// </summary>
public class FederationSystemInfoDto
{
    /// <summary>Gets or sets the plugin name.</summary>
    public string Name { get; set; } = "JellyFed";

    /// <summary>Gets or sets the plugin version.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets the stable instance identifier for this JellyFed node.</summary>
    public string? InstanceId { get; set; }

    /// <summary>Gets or sets the friendly local server name used for federation.</summary>
    public string? ServerName { get; set; }

    /// <summary>Gets or sets the current federation protocol version.</summary>
    public int? ProtocolVersion { get; set; }

    /// <summary>Gets or sets the current persisted schema version.</summary>
    public int? SchemaVersion { get; set; }

    /// <summary>Gets or sets the preferred route prefix clients should use.</summary>
    public string PreferredRoutePrefix { get; set; } = FederationProtocol.V1RoutePrefixPath;

    /// <summary>Gets or sets all supported route prefixes, ordered by preference.</summary>
    public IReadOnlyList<string> RoutePrefixes { get; set; } = FederationProtocol.SupportedRoutePrefixes;

    /// <summary>Gets or sets the capability flags exposed by this instance.</summary>
    public IReadOnlyList<string> Capabilities { get; set; } = FederationProtocol.Capabilities;
}
