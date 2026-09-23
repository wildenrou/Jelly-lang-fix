using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.FrenchOriginals.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool PreviewOnly { get; set; } = true;
    public string[] LibraryIds { get; set; } = [];
    public bool UpdateTitlesAndSummaries { get; set; } = true;
    public bool IncludeSeasonsAndEpisodes { get; set; } = true;
    public bool RecheckCompletedItems { get; set; }
    public string ItemIds { get; set; } = "";
    public int ItemLimit { get; set; } = 25;
    public int MaxChanges { get; set; } = 80;
    public int DelayMilliseconds { get; set; } = 200;
    public int ProviderTimeoutSeconds { get; set; } = 30;
    public int JournalRetentionDays { get; set; } = 30;

    public RunOptions Snapshot()
    {
        if (ItemLimit < 0 || MaxChanges < 1 || DelayMilliseconds is < 0 or > 10000 ||
            ProviderTimeoutSeconds is < 5 or > 300 || JournalRetentionDays is < 1 or > 3650)
            throw new InvalidOperationException("Invalid limits, delay, provider timeout, or journal retention.");
        var libraries = ParseIds(LibraryIds ?? [], "library");
        if (libraries.Count == 0)
            throw new InvalidOperationException("Select at least one library in the plugin settings and save.");
        var items = ParseIds((ItemIds ?? "").Split([' ', ',', ';', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries), "item");
        return new(PreviewOnly, libraries, UpdateTitlesAndSummaries, IncludeSeasonsAndEpisodes,
            RecheckCompletedItems, items, ItemLimit, MaxChanges, DelayMilliseconds,
            ProviderTimeoutSeconds, JournalRetentionDays);
    }

    private static HashSet<Guid> ParseIds(IEnumerable<string> values, string kind)
    {
        HashSet<Guid> result = [];
        foreach (var value in values)
        {
            if (!Guid.TryParse(value, out var id) || id == Guid.Empty)
                throw new InvalidOperationException($"Invalid {kind} ID. Use the ID shown by Jellyfin.");
            result.Add(id);
        }
        return result;
    }
}

public sealed record RunOptions(bool PreviewOnly, HashSet<Guid> LibraryIds,
    bool UpdateText, bool IncludeChildren, bool RecheckCompleted, HashSet<Guid> ItemIds,
    int ItemLimit, int MaxChanges, int DelayMilliseconds, int ProviderTimeoutSeconds, int RetentionDays);
