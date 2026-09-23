using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.FrenchOriginals;

public static class ArtworkGuard
{
    public static void EnsureAvailable(BaseItem item)
    {
        // Match Jellyfin's ValidateImages predicate without modifying ImageInfos. A
        // metadata save can otherwise discard these references as a side effect.
        // File.Exists also returns false for inaccessible files; do not guess why.
        var unavailable = item.ImageInfos
            .Where(image => image.IsLocalFile && !File.Exists(image.Path))
            .Select(image => new ArtworkReference(image.Path, image.Type)).ToArray();
        if (unavailable.Length > 0) throw new ArtworkUnavailableException(unavailable);
    }
}

public sealed record ArtworkReference(string Path, ImageType Type);

// Raised only before assigning metadata or invoking Jellyfin's save operation.
public sealed class ArtworkUnavailableException(IReadOnlyList<ArtworkReference> images) : InvalidOperationException(
    $"Local artwork is missing or inaccessible: {string.Join(", ", images.Select(i => Path.GetFileName(i.Path)).Distinct(StringComparer.Ordinal))}. " +
    "Item left unchanged. Check these files in Jellyfin, then retry; full paths are in the audit journal.")
{
    public IReadOnlyList<ArtworkReference> Images { get; } = images;
}
