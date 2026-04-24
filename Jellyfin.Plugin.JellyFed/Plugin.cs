using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.JellyFed.Configuration;
using Jellyfin.Plugin.JellyFed.Sync;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JellyFed;

/// <summary>
/// JellyFed plugin — native federation between Jellyfin instances.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        var needsSave = SchemaMigrator.MigrateConfiguration(Configuration);

        // Use Jellyfin's data directory as the library base path.
        // Works on Docker (/config/data/), Linux standalone, Windows, etc.
        // Migrates the old hardcoded /config/jellyfed-library default automatically.
        var defaultLibraryPath = Path.Combine(applicationPaths.DataPath, "jellyfed-library");
        if (string.IsNullOrWhiteSpace(Configuration.LibraryPath) ||
            Configuration.LibraryPath == "/config/jellyfed-library")
        {
            Configuration.LibraryPath = defaultLibraryPath;
            needsSave = true;
        }

        // Auto-generate a federation token on first startup.
        if (string.IsNullOrWhiteSpace(Configuration.FederationToken))
        {
            Configuration.FederationToken = Guid.NewGuid().ToString("N");
            needsSave = true;
        }

        if (!string.IsNullOrWhiteSpace(Configuration.LibraryPath))
        {
            _ = ManifestStore.Load(Configuration.LibraryPath);
        }

        if (needsSave)
        {
            SaveConfiguration();
        }
    }

    /// <inheritdoc />
    public override string Name => "JellyFed";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("ff0164d7-e8c0-44c1-bc61-45017125a155");

    /// <inheritdoc />
    public override string Description => "Native federation between Jellyfin instances.";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace)
            }
        ];
    }
}
