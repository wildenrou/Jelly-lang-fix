using Jellyfin.Plugin.FrenchOriginals.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.FrenchOriginals;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer)
    {
        Instance = this;
        DataPath = Path.Combine(paths.DataPath, "FrenchOriginals");
    }
    public override string Name => "French Originals";
    public override Guid Id => Guid.Parse("8a18225a-22b8-4e18-9921-b8f1b5900bbc");
    public override string Description => "French titles and summaries for French-original films and television.";
    public string DataPath { get; }
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        // Permit empty scope while initially configuring; the task fails closed until selected.
        var config = (PluginConfiguration)configuration;
        if (config.LibraryIds?.Length > 0) _ = config.Snapshot();
        base.UpdateConfiguration(configuration);
    }
    public IEnumerable<PluginPageInfo> GetPages() =>
    [new() { Name = "FrenchOriginals", EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html" }];
}
