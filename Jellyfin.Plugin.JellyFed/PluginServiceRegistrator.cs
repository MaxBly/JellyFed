using Jellyfin.Plugin.JellyFed.Api;
using Jellyfin.Plugin.JellyFed.Sync;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyFed;

/// <summary>
/// Registers JellyFed services into the Jellyfin DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddScoped<FederationAuthFilter>();
        serviceCollection.AddScoped<FederationSyncTask>();
    }
}
