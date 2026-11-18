using Jellyfin.Plugin.Karaoke.Services;
using Jellyfin.Plugin.Karaoke.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Karaoke;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<StemStore>();
        serviceCollection.AddSingleton<PlaylistSync>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<PlaylistSync>());
        serviceCollection.AddTransient<IStartupFilter, ScriptInjectionStartupFilter>();
    }
}
