using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.FrenchOriginals.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FrenchOriginals;

public interface IItemStore
{
    bool Busy { get; }
    IEnumerable<WorkItem> Inventory(RunOptions options, CancellationToken token);
    BaseItem? Read(Guid id);
    void CheckCanSave(BaseItem item);
    Task Save(BaseItem expected, TextChange change, CancellationToken token);
}
public interface ITextLookup
{
    Task<LocalizedText?> Fetch(BaseItem item, int timeoutSeconds, CancellationToken token);
}

public sealed class JellyfinAdapter(ILibraryManager library, IProviderManager providers,
    ILogger<JellyfinAdapter> logger) : IItemStore, ITextLookup
{
    public bool Busy => library.IsScanRunning || providers.GetRefreshQueue().Count > 0;
    public BaseItem? Read(Guid id) => library.RetrieveItem(id);
    public void CheckCanSave(BaseItem item) => ArtworkGuard.EnsureAvailable(item);

    public IEnumerable<WorkItem> Inventory(RunOptions options, CancellationToken token)
    {
        var available = library.GetVirtualFolders().Select(v => Guid.TryParse(v.ItemId, out var id) ? id : Guid.Empty).ToHashSet();
        if (!options.LibraryIds.IsSubsetOf(available))
            throw new InvalidOperationException("A selected library no longer exists. Update the plugin's library selection.");
        HashSet<Guid> seen = [];
        HashSet<Guid> foundRequested = [];
        foreach (var libraryId in options.LibraryIds.Order())
        {
            foreach (var root in Pages(libraryId, [BaseItemKind.Movie, BaseItemKind.Series], token))
            {
                if (!seen.Add(root.Id)) continue;
                bool requested = options.ItemIds.Count == 0 || options.ItemIds.Contains(root.Id);
                if (options.ItemIds.Contains(root.Id)) foundRequested.Add(root.Id);
                if (requested) yield return new(root.Id, null);
                if (root is not Series || !Rules.IsFrench(root.OriginalLanguage) || root.IsLocked || !options.IncludeChildren) continue;
                foreach (var child in Pages(root.Id, [BaseItemKind.Season, BaseItemKind.Episode], token))
                {
                    if (!seen.Add(child.Id)) continue;
                    if (options.ItemIds.Contains(child.Id)) foundRequested.Add(child.Id);
                    // Exact IDs are exact: selecting a series ID does not silently expand it.
                    if (options.ItemIds.Count == 0 || options.ItemIds.Contains(child.Id)) yield return new(child.Id, root.Id);
                }
            }
        }
        if (!options.ItemIds.IsSubsetOf(foundRequested))
            throw new InvalidOperationException("One or more exact item IDs are outside the selected scope. No items were changed.");
    }

    private IEnumerable<BaseItem> Pages(Guid parent, BaseItemKind[] types, CancellationToken token)
    {
        var offset = 0;
        HashSet<Guid> pageIds = [];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (Busy) throw new InvalidOperationException("Jellyfin is scanning or refreshing metadata. Run this task again after it finishes.");
            var page = library.GetItemList(new InternalItemsQuery
            {
                ParentId = parent, Recursive = true, IncludeItemTypes = types,
                StartIndex = offset, Limit = 200, EnableTotalRecordCount = false,
                CollapseBoxSetItems = false, GroupByPresentationUniqueKey = false,
                OrderBy = [(ItemSortBy.DateCreated, SortOrder.Ascending), (ItemSortBy.SortName, SortOrder.Ascending)]
            });
            if (page.Count == 0) yield break;
            foreach (var item in page)
            {
                if (!pageIds.Add(item.Id)) throw new InvalidOperationException("The library changed during pagination. Please rerun the task.");
                yield return item;
            }
            offset += page.Count;
            if (page.Count < 200) yield break;
        }
    }

    public async Task Save(BaseItem expected, TextChange change, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Busy) throw new InvalidOperationException("A library scan or metadata refresh started; stopping before the next write.");
        var before = Rules.Snapshot(expected);
        var protectedBefore = Rules.ProtectedMetadata(expected);
        var persisted = Read(expected.Id) ?? throw new InvalidOperationException("Item disappeared before update.");
        var live = library.GetItemById(expected.Id) ?? throw new InvalidOperationException("Item disappeared before update.");
        if (Rules.Snapshot(persisted) != before || Rules.Snapshot(live) != before)
            throw new InvalidOperationException("Item was edited concurrently; stopping without overwriting the new values.");
        // Recheck immediately before mutating cached fields, even if preview or the
        // service preflight succeeded earlier. Skipping here guarantees zero writes.
        CheckCanSave(live);
        live.PreferredMetadataLanguage = change.Language!;
        live.Name = change.Name!;
        live.Overview = change.Overview!;
        try
        {
            // Once begun, finish and verify this one write even if the task is cancelled.
            // Jellyfin invokes configured metadata savers (e.g. NFO) through this supported API.
            await library.UpdateItemAsync(live, live.GetParent(), ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Do not retry an ambiguous write. Resynchronize just our cached fields from storage.
            var actual = Read(expected.Id);
            if (actual is not null)
            {
                live.PreferredMetadataLanguage = actual.PreferredMetadataLanguage;
                live.Name = actual.Name;
                live.Overview = actual.Overview;
            }
            throw;
        }
        Rules.Verify(before, protectedBefore, change, Read(expected.Id));
    }

    public Task<LocalizedText?> Fetch(BaseItem item, int timeoutSeconds, CancellationToken token) => item switch
    {
        Movie movie => FetchTyped<Movie, MovieInfo>(movie, timeoutSeconds, token),
        Series series => FetchTyped<Series, SeriesInfo>(series, timeoutSeconds, token),
        Season season => FetchTyped<Season, SeasonInfo>(season, timeoutSeconds, token),
        Episode episode => FetchTyped<Episode, EpisodeInfo>(episode, timeoutSeconds, token),
        _ => Task.FromResult<LocalizedText?>(null)
    };

    private async Task<LocalizedText?> FetchTyped<T, TInfo>(T item, int timeoutSeconds, CancellationToken token)
        where T : BaseItem, IHasLookupInfo<TInfo>
        where TInfo : ItemLookupInfo, new()
    {
        // No name-only matching: every accepted response must share an existing external ID.
        if (!item.ProviderIds.Any(p => !string.IsNullOrWhiteSpace(p.Value))) return null;
        var libraryOptions = library.GetLibraryOptions(item);
        string? title = null;
        string? summary = null;
        List<string> usedProviders = [];
        foreach (var provider in providers.GetMetadataProviders<T>(item, libraryOptions)
            .OfType<IRemoteMetadataProvider<T, TInfo>>())
        {
            token.ThrowIfCancellationRequested();
            var info = item.GetLookupInfo();
            info.MetadataLanguage = "fr";
            info.IsAutomated = true;
            // Lookup DTOs may reference the live item's dictionaries. Never pass those to a provider.
            info.ProviderIds = new(info.ProviderIds, StringComparer.OrdinalIgnoreCase);
            if (info is SeasonInfo season) season.SeriesProviderIds = new(season.SeriesProviderIds, StringComparer.OrdinalIgnoreCase);
            if (info is EpisodeInfo episode)
            {
                episode.SeriesProviderIds = new(episode.SeriesProviderIds, StringComparer.OrdinalIgnoreCase);
                episode.SeasonProviderIds = new(episode.SeasonProviderIds, StringComparer.OrdinalIgnoreCase);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                var result = await provider.GetMetadata(info, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                if (!result.HasMetadata || result.Item is null || !Rules.MatchingIdentity(item, result.Item)) continue;
                if (!string.IsNullOrWhiteSpace(result.ResultLanguage) && !Rules.IsFrench(result.ResultLanguage)) continue;
                if (!string.IsNullOrWhiteSpace(result.Item.OriginalLanguage) && !Rules.IsFrench(result.Item.OriginalLanguage)) continue;
                if (string.IsNullOrWhiteSpace(result.Item.Name) && string.IsNullOrWhiteSpace(result.Item.Overview)) continue;
                if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(result.Item.Name)) title = result.Item.Name;
                if (string.IsNullOrWhiteSpace(summary) && !string.IsNullOrWhiteSpace(result.Item.Overview)) summary = result.Item.Overview;
                usedProviders.Add(provider.Name);
                if ((!string.IsNullOrWhiteSpace(title) || item.LockedFields.Contains(MetadataField.Name)) &&
                    (!string.IsNullOrWhiteSpace(summary) || item.LockedFields.Contains(MetadataField.Overview))) break;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning("French Originals: provider {Provider} did not return usable metadata for {ItemId} ({ErrorType}).",
                    provider.Name, item.Id, ex.GetType().Name);
            }
        }
        return usedProviders.Count == 0 ? null : new(title, summary, string.Join(", ", usedProviders));
    }
}
