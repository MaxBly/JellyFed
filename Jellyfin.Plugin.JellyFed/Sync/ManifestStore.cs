using System;
using System.Globalization;
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
            if (UsesLegacyMaterializedLayout(manifest))
            {
                var emptyManifest = new Manifest();
                ArchiveLegacyManifest(path, manifest!.SchemaVersion);
                Save(libraryPath, emptyManifest);
                return emptyManifest;
            }

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

    private static bool UsesLegacyMaterializedLayout(Manifest? manifest)
    {
        if (manifest is null || manifest.SchemaVersion >= FederationProtocol.CurrentSchemaVersion)
        {
            return false;
        }

        return (manifest.Movies?.Count ?? 0) > 0 || (manifest.Series?.Count ?? 0) > 0;
    }

    private static void ArchiveLegacyManifest(string path, int schemaVersion)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var archiveBasePath = Path.Combine(
            directory,
            string.Format(
                CultureInfo.InvariantCulture,
                "{0}.schema-v{1}.archived-{2}",
                fileName,
                schemaVersion,
                timestamp));
        var archivePath = archiveBasePath + extension;
        var suffix = 1;
        while (File.Exists(archivePath))
        {
            archivePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}-{1}{2}",
                archiveBasePath,
                suffix,
                extension);
            suffix++;
        }

        File.Move(path, archivePath);
    }
}
