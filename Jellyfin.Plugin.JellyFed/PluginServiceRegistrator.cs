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
        serviceCollection.AddHttpClient("JellyFed")
            .ConfigureHttpClient(c =>
            {
                c.Timeout = System.TimeSpan.FromSeconds(30);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("JellyFed/0.1");
            });

        serviceCollection.AddScoped<FederationAuthFilter>();
        serviceCollection.AddSingleton<PeerClient>();
        serviceCollection.AddScoped<StrmWriter>();
        serviceCollection.AddScoped<FederationSyncTask>();

        serviceCollection.AddHostedService<PeerHeartbeatService>();
    }
}
