namespace NovaBrowser.Models;

/// <summary>
/// A user bookmark. URLs are unique in the local store.
/// </summary>
public sealed record BookmarkItem(
    long Id,
    string Url,
    string Title,
    DateTimeOffset CreatedAt,
    string? FaviconUrl = null);
