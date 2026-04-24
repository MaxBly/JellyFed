using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.JellyFed.Configuration;

namespace Jellyfin.Plugin.JellyFed.Sync;

/// <summary>
/// Loads and saves the persisted JellyFed manifest with schema migration support.
/// </summary>
public static class ManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Loads the manifest from the configured library path, auto-migrating older schemas.
    /// </summary>
    /// <param name="libraryPath">JellyFed metadata directory.</param>
    /// <returns>The current manifest.</returns>
    public static Manifest Load(string libraryPath)
    {
        var path = Path.Combine(libraryPath, FederationSyncTask.ManifestFileName);
        if (!File.Exists(path))
        {
            return new Manifest();
        }

        try
        {
            var json = File.ReadAllText(path);
            var manifest = JsonSerializer.Deserialize<Manifest>(json, JsonOptions);
            manifest = SchemaMigrator.MigrateManifest(manifest, out var changed);
            if (changed)
            {
                Save(libraryPath, manifest);
            }

            return manifest;
        }
        catch (System.InvalidOperationException)
        {
            throw;
        }
#pragma warning disable CA1031 // Corrupt manifests must not crash startup/sync; fall back to an empty document.
        catch
        {
            return new Manifest();
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Saves the manifest to disk at the current schema version.
    /// </summary>
    /// <param name="libraryPath">JellyFed metadata directory.</param>
    /// <param name="manifest">Manifest to persist.</param>
    public static void Save(string libraryPath, Manifest manifest)
    {
        Directory.CreateDirectory(libraryPath);
        manifest = SchemaMigrator.MigrateManifest(manifest, out _);

        var path = Path.Combine(libraryPath, FederationSyncTask.ManifestFileName);
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        File.WriteAllText(path, json);
    }
}
