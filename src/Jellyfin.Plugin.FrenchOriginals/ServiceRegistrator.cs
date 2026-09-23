using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.FrenchOriginals;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<JellyfinAdapter>();
        services.AddSingleton<IItemStore>(sp => sp.GetRequiredService<JellyfinAdapter>());
        services.AddSingleton<ITextLookup>(sp => sp.GetRequiredService<JellyfinAdapter>());
        services.AddSingleton(_ => new StateStore((Plugin.Instance ?? throw new InvalidOperationException("Plugin not loaded.")).DataPath));
        services.AddSingleton<MetadataTaskService>();
    }
}
