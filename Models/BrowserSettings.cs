namespace NovaBrowser.Models;

/// <summary>
/// Colour palette used by the NovaBrowser shell.
/// </summary>
public enum ThemeMode
{
    Dark,
    Light
}

/// <summary>
/// Search providers supported by the address bar.
/// </summary>
public enum SearchEngineKind
{
    Google,
    Bing,
    DuckDuckGo,
    Startpage
}

/// <summary>
/// Controls how aggressively inactive WebView instances may release memory.
/// </summary>
public enum MemorySavingMode
{
    Balanced,
    Conservative,
    Performance
}

/// <summary>
/// Persisted browser preferences. A null <see cref="DownloadFolder"/> means that
/// <c>AppPaths.DefaultDownloadsDirectory</c> should be used.
/// </summary>
public sealed record BrowserSettings
{
    public ThemeMode Theme { get; init; } = ThemeMode.Dark;

    public SearchEngineKind SearchEngine { get; init; } = SearchEngineKind.DuckDuckGo;

    public string HomePage { get; init; } = "https://www.google.com/";

    public bool RestoreSession { get; init; } = true;

    public string? DownloadFolder { get; init; }

    public MemorySavingMode MemoryMode { get; init; } = MemorySavingMode.Balanced;

    public bool AdBlockEnabled { get; init; } = true;

    public string AdBlockExcludedSites { get; init; } = string.Empty;
}
