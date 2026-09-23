using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.FrenchOriginals;

public static class Rules
{
    public static bool IsFrench(string? value)
    {
        var code = value?.Trim().Replace('_', '-').Split('-')[0].ToLowerInvariant();
        return code is "fr" or "fra" or "fre";
    }
    public static bool IsRoot(BaseItem item) => item is Movie or Series;
    public static bool IsChild(BaseItem item) => item is Season or Episode;
    public static bool Eligible(BaseItem item, BaseItem? series)
    {
        if (item.IsLocked) return false;
        if (IsRoot(item)) return IsFrench(item.OriginalLanguage);
        if (!IsChild(item) || series is not Series || series.IsLocked || !IsFrench(series.OriginalLanguage)) return false;
        // Honour an explicit non-French child override and a known non-French original.
        return (string.IsNullOrWhiteSpace(item.PreferredMetadataLanguage) || IsFrench(item.PreferredMetadataLanguage)) &&
            (string.IsNullOrWhiteSpace(item.OriginalLanguage) || IsFrench(item.OriginalLanguage));
    }
    public static bool MatchingIdentity(BaseItem source, BaseItem result)
    {
        bool matched = false;
        foreach (var pair in source.ProviderIds.Where(p => !string.IsNullOrWhiteSpace(p.Value)))
        {
            var other = result.ProviderIds.FirstOrDefault(p => string.Equals(p.Key, pair.Key, StringComparison.OrdinalIgnoreCase)).Value;
            if (string.IsNullOrWhiteSpace(other)) continue;
            if (!string.Equals(pair.Value, other, StringComparison.OrdinalIgnoreCase)) return false;
            matched = true;
        }
        return matched;
    }
    public static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    public static JsonElement ProtectedMetadata(BaseItem i) => JsonSerializer.SerializeToElement(new
    {
        i.Id, Type = i.GetType().Name, i.ParentId, i.OriginalTitle, i.OriginalLanguage, i.ForcedSortName,
        i.PreferredMetadataCountryCode, i.IsLocked, Locks = i.LockedFields.OrderBy(x => x).ToArray(),
        Providers = i.ProviderIds.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(),
        i.OfficialRating, i.CustomRating, i.CommunityRating, i.CriticRating, i.ProductionYear,
        i.PremiereDate, i.EndDate, i.IndexNumber, i.ParentIndexNumber, i.RunTimeTicks,
        i.Genres, i.Tags, i.Studios, i.ProductionLocations, i.Path,
        Images = i.ImageInfos.Select(x => new { x.Path, x.Type }).ToArray()
    });
    public static string ProtectedFingerprint(BaseItem i) => Hash(ProtectedMetadata(i));
    public static ItemSnapshot Snapshot(BaseItem item) => new(item.Id, item.Name, item.Overview,
        item.PreferredMetadataLanguage, ProtectedFingerprint(item));
    public static string CompletionKey(BaseItem item, bool updateText) => Hash(new { Snapshot = Snapshot(item), UpdateText = updateText });
    public static void Verify(ItemSnapshot before, JsonElement protectedBefore, TextChange proposed, BaseItem? actual)
    {
        List<MetadataDifference> differences = [];
        void Compare(string field, JsonElement expected, JsonElement found)
        {
            if (expected.GetRawText() != found.GetRawText()) differences.Add(new(field, expected, found));
        }
        if (actual is null)
            Compare("Item", JsonSerializer.SerializeToElement(before.Id), JsonSerializer.SerializeToElement<object?>(null));
        else
        {
            Compare("Name", JsonSerializer.SerializeToElement(proposed.Name), JsonSerializer.SerializeToElement(actual.Name));
            Compare("Overview", JsonSerializer.SerializeToElement(proposed.Overview), JsonSerializer.SerializeToElement(actual.Overview));
            Compare("PreferredMetadataLanguage", JsonSerializer.SerializeToElement(proposed.Language), JsonSerializer.SerializeToElement(actual.PreferredMetadataLanguage));
            var protectedAfter = ProtectedMetadata(actual);
            foreach (var property in protectedBefore.EnumerateObject())
                Compare(property.Name, property.Value, protectedAfter.GetProperty(property.Name));
        }
        if (differences.Count > 0)
            throw new MetadataVerificationException(new(before, proposed, actual is null ? null : Snapshot(actual), differences));
    }
    public static TextChange Plan(BaseItem item, LocalizedText? text)
    {
        var title = item.Name;
        var overview = item.Overview;
        if (text is not null)
        {
            if (!item.LockedFields.Contains(MetadataField.Name) && !string.IsNullOrWhiteSpace(text.Name)) title = text.Name;
            if (!item.LockedFields.Contains(MetadataField.Overview) && !string.IsNullOrWhiteSpace(text.Overview)) overview = text.Overview;
        }
        return new(IsFrench(item.PreferredMetadataLanguage) ? item.PreferredMetadataLanguage : "fr", title, overview);
    }
}

public sealed record ItemSnapshot(Guid Id, string? Name, string? Overview, string? Language, string ProtectedFingerprint);
public sealed record TextChange(string? Language, string? Name, string? Overview)
{
    public bool Differs(BaseItem item) => Language != item.PreferredMetadataLanguage || Name != item.Name || Overview != item.Overview;
    public string ChangedFields(BaseItem item) => string.Join(", ",
        new[] { Name != item.Name ? "title" : null, Overview != item.Overview ? "summary" : null,
            Language != item.PreferredMetadataLanguage ? "language preference" : null }.OfType<string>());
}
public sealed record MetadataDifference(string Field, JsonElement Expected, JsonElement Actual);
public sealed record VerificationFailure(ItemSnapshot Before, TextChange Proposed, ItemSnapshot? Actual, IReadOnlyList<MetadataDifference> Differences);
public sealed class MetadataVerificationException(VerificationFailure failure) : InvalidOperationException(
    $"Read-back verification failed for '{failure.Before.Name}' ({failure.Before.Id}). Fields: {string.Join(", ", failure.Differences.Select(d => d.Field))}. Stopped immediately; see the audit journal for expected and actual values.")
{
    public VerificationFailure Failure { get; } = failure;
}
public sealed record LocalizedText(string? Name, string? Overview, string Provider);
public sealed record WorkItem(Guid Id, Guid? SeriesId);
public sealed record ReportRow(Guid Id, string? Title, string Type, string Action, string? NewTitle = null, string? Detail = null);
public sealed class RunReport
{
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedUtc { get; set; }
    public bool PreviewOnly { get; set; }
    public string Status { get; set; } = "Running";
    public string? Message { get; set; }
    public int Inspected { get; set; }
    public int Candidates { get; set; }
    public int Selected { get; set; }
    public int Changed { get; set; }
    public int AlreadyComplete { get; set; }
    public int NoChangeNeeded { get; set; }
    public int Skipped { get; set; }
    public List<ReportRow> Items { get; set; } = [];
    public void Add(ReportRow row) { if (Items.Count < 200) Items.Add(row); }
}
