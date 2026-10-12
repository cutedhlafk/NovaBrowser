namespace NovaBrowser.Models;

/// <summary>
/// Lightweight data needed to restore a browser tab without creating its WebView
/// until the tab is activated.
/// </summary>
public sealed record SessionTab(
    string Url,
    string Title = "",
    string? FaviconUrl = null,
    int Position = 0,
    bool IsActive = false);
