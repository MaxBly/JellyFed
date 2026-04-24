using System;
using Jellyfin.Plugin.JellyFed;
using Jellyfin.Plugin.JellyFed.Sync;

namespace Jellyfin.Plugin.JellyFed.Configuration;

/// <summary>
/// Applies in-place migrations for persisted JellyFed documents.
/// </summary>
public static class SchemaMigrator
{
    /// <summary>
    /// Migrates the plugin configuration to the current schema version.
    /// </summary>
    /// <param name="configuration">Configuration instance to normalize.</param>
    /// <returns><see langword="true"/> when the configuration was changed.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configuration"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the stored schema is newer than this build supports.</exception>
    public static bool MigrateConfiguration(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.SchemaVersion > FederationProtocol.CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"JellyFed configuration schema v{configuration.SchemaVersion} is newer than this plugin build supports (max v{FederationProtocol.CurrentSchemaVersion}).");
        }

        var changed = false;

        if (configuration.SchemaVersion < FederationProtocol.CurrentSchemaVersion)
        {
            configuration.SchemaVersion = FederationProtocol.CurrentSchemaVersion;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(configuration.InstanceId))
        {
            configuration.InstanceId = Guid.NewGuid().ToString("N");
            changed = true;
        }

        if (configuration.Peers is null)
        {
            configuration.Peers = [];
            changed = true;
        }

        if (configuration.BlockedPeerUrls is null)
        {
            configuration.BlockedPeerUrls = [];
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Migrates a loaded manifest to the current schema version.
    /// </summary>
    /// <param name="manifest">Manifest instance to normalize.</param>
    /// <param name="changed">Receives whether the manifest was changed.</param>
    /// <returns>The normalized manifest.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the stored schema is newer than this build supports.</exception>
    public static Manifest MigrateManifest(Manifest? manifest, out bool changed)
    {
        manifest ??= new Manifest();
        changed = false;

        if (manifest.SchemaVersion > FederationProtocol.CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"JellyFed manifest schema v{manifest.SchemaVersion} is newer than this plugin build supports (max v{FederationProtocol.CurrentSchemaVersion}).");
        }

        if (manifest.SchemaVersion < FederationProtocol.CurrentSchemaVersion)
        {
            manifest.SchemaVersion = FederationProtocol.CurrentSchemaVersion;
            changed = true;
        }

        if (manifest.Movies is null)
        {
            manifest.Movies = [];
            changed = true;
        }

        if (manifest.Series is null)
        {
            manifest.Series = [];
            changed = true;
        }

        return manifest;
    }
}
