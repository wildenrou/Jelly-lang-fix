using MediaBrowser.Controller;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.FrenchOriginals;

public sealed class ScheduledTask(MetadataTaskService service, IServerApplicationHost host) : IScheduledTask
{
    public string Name => "French Originals — update metadata";
    public string Key => "FrenchOriginals.UpdateMetadata";
    public string Description => "Preview or apply French metadata for French originals in the selected libraries.";
    public string Category => "Library";
    // Install safely: the administrator chooses the schedule in Dashboard > Scheduled Tasks.
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (host.ApplicationVersion.Major != 12)
            throw new NotSupportedException("This build supports Jellyfin 12.x only.");
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not loaded.");
        return service.Run(plugin.Configuration.Snapshot(), progress, cancellationToken);
    }
}
