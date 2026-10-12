namespace NovaBrowser.Models;

/// <summary>
/// A single completed page navigation stored in the browser history.
/// </summary>
public sealed record HistoryEntry(
    long Id,
    string Url,
    string Title,
    DateTimeOffset VisitedAt,
    string? FaviconUrl = null);
