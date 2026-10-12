using System.Windows.Threading;
using BrowserThemeMode = NovaBrowser.Models.ThemeMode;
using WpfApplication = System.Windows.Application;
using WpfResourceDictionary = System.Windows.ResourceDictionary;

namespace NovaBrowser.Services;

/// <summary>
/// Switches the palette dictionaries used by the WPF shell. The control styles use
/// DynamicResource references, so applying a new palette updates open windows too.
/// </summary>
public sealed class ThemeService
{
    private static readonly Uri DarkThemeUri = new("Themes/Colors.Dark.xaml", UriKind.Relative);
    private static readonly Uri LightThemeUri = new("Themes/Colors.Light.xaml", UriKind.Relative);

    private readonly WpfApplication _application;
    private readonly Dispatcher _dispatcher;

    public ThemeService(WpfApplication? application = null)
    {
        _application = application ?? WpfApplication.Current
            ?? throw new InvalidOperationException("ThemeService wymaga aktywnej aplikacji WPF.");
        _dispatcher = _application.Dispatcher;
    }

    /// <summary>
    /// The palette last applied through this service. Call <see cref="ApplyTheme"/>
    /// at startup even for Dark to ensure its resource dictionary is present.
    /// </summary>
    public BrowserThemeMode CurrentTheme { get; private set; } = BrowserThemeMode.Dark;

    public event Action<BrowserThemeMode>? ThemeChanged;

    /// <summary>
    /// Replaces any existing NovaBrowser colour dictionary with the requested one.
    /// This must run on the WPF UI dispatcher.
    /// </summary>
    public void ApplyTheme(BrowserThemeMode theme)
    {
        _dispatcher.VerifyAccess();

        // Construct before removing the current palette so a resource-loading error
        // leaves the currently usable theme intact.
        var palette = new WpfResourceDictionary { Source = GetThemeUri(theme) };
        var dictionaries = _application.Resources.MergedDictionaries;

        for (var index = dictionaries.Count - 1; index >= 0; index--)
        {
            if (IsNovaBrowserPalette(dictionaries[index]))
            {
                dictionaries.RemoveAt(index);
            }
        }

        // Keep the palette before Controls.xaml, matching the resource order used by
        // the application and making it available to any future static resources.
        dictionaries.Insert(0, palette);

        var changed = CurrentTheme != theme;
        CurrentTheme = theme;
        if (changed)
        {
            ThemeChanged?.Invoke(theme);
        }
    }

    /// <summary>
    /// Returns the WPF resource URI for a supported palette.
    /// </summary>
    public static Uri GetThemeUri(BrowserThemeMode theme) => theme switch
    {
        BrowserThemeMode.Dark => DarkThemeUri,
        BrowserThemeMode.Light => LightThemeUri,
        _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, "Nieznany motyw NovaBrowser.")
    };

    private static bool IsNovaBrowserPalette(WpfResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString;
        return source is not null &&
               (source.EndsWith("Themes/Colors.Dark.xaml", StringComparison.OrdinalIgnoreCase) ||
                source.EndsWith("Themes/Colors.Light.xaml", StringComparison.OrdinalIgnoreCase));
    }
}
