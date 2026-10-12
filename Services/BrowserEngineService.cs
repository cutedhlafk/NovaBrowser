using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NovaBrowser.ViewModels;

namespace NovaBrowser.Services;

/// <summary>
/// Jedyny serwis, który zarządza kontrolkami WebView2. Współdzieli jedno środowisko Chromium
/// pomiędzy kartami i nie tworzy widoków dla kart, dopóki użytkownik ich nie wybierze.
/// </summary>
public sealed class BrowserEngineService : IDisposable
{
    private readonly string _userDataDirectory;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<Guid, TabSubscription> _subscriptions = [];
    private readonly SemaphoreSlim _environmentLock = new(1, 1);
    private readonly Dictionary<Guid, SemaphoreSlim> _initializationGates = [];
    private CoreWebView2Environment? _environment;
    private bool _disposed;

    public BrowserEngineService(string userDataDirectory, Dispatcher dispatcher)
    {
        _userDataDirectory = userDataDirectory;
        _dispatcher = dispatcher;
    }

    /// <summary>Wywoływane dla target=_blank i window.open.</summary>
    public Func<string, Task>? NewTabRequestedAsync { get; set; }

    /// <summary>Powiadomienie o udanej, głównej nawigacji do zapisania w historii/sesji.</summary>
    public Func<BrowserTabViewModel, Task>? PageVisitedAsync { get; set; }

    public Action<BrowserShortcut>? ShortcutRequested { get; set; }

    /// <summary>Integracja panelu pobierania bez sprzęgania silnika z jego modelem widoku.</summary>
    public Action<CoreWebView2>? CoreWebViewInitialized { get; set; }

    public Action<CoreWebView2>? CoreWebViewDisposed { get; set; }

    public static bool IsRuntimeAvailable()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return false;
        }
    }

    public async Task EnsureInitializedAsync(BrowserTabViewModel tab, string homePage, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (tab.IsInitialized || tab.IsDisposed) return;
        if (!_initializationGates.TryGetValue(tab.Id, out var gate))
            _initializationGates[tab.Id] = gate = new SemaphoreSlim(1, 1);

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (tab.IsInitialized || tab.IsDisposed) return;
            await InitializeTabCoreAsync(tab, homePage, cancellationToken);
        }
        finally { gate.Release(); }
    }

    private async Task InitializeTabCoreAsync(BrowserTabViewModel tab, string homePage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsRuntimeAvailable())
        {
            tab.LoadError = "Brak Microsoft Edge WebView2 Runtime. Zainstaluj Evergreen Runtime, aby otwierać strony.";
            tab.StatusText = tab.LoadError;
            return;
        }

        var browser = new WebView2();
        tab.BrowserView = browser;

        try
        {
            var environment = await GetEnvironmentAsync().ConfigureAwait(true);
            await browser.EnsureCoreWebView2Async(environment);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            tab.LoadError = "Brak Microsoft Edge WebView2 Runtime. Zainstaluj Evergreen Runtime.";
            tab.StatusText = tab.LoadError;
            browser.Dispose();
            tab.BrowserView = null;
            return;
        }
        catch (Exception exception)
        {
            tab.LoadError = $"Nie udało się uruchomić silnika strony: {exception.Message}";
            tab.StatusText = tab.LoadError;
            browser.Dispose();
            tab.BrowserView = null;
            return;
        }

        if (tab.IsDisposed)
        {
            browser.Dispose();
            return;
        }

        if (browser.CoreWebView2 is not { } core)
        {
            tab.LoadError = "Silnik WebView2 nie został zainicjalizowany.";
            tab.StatusText = tab.LoadError;
            return;
        }

        tab.CoreWebView = core;
        AttachEvents(tab, browser, core);
        tab.SetMemoryTargetHandler(level => TrySetMemoryTarget(tab, level));
        tab.IsInitialized = true;
        tab.StatusText = "Gotowe";
        CoreWebViewInitialized?.Invoke(core);

        var initialUrl = tab.ConsumeInitialUrl();
        if (string.IsNullOrWhiteSpace(initialUrl) || initialUrl.Equals("about:newtab", StringComparison.OrdinalIgnoreCase))
        {
            NavigateHomeOrNewTab(tab, homePage);
        }
        else
        {
            Navigate(tab, initialUrl);
        }
    }

    public void Navigate(BrowserTabViewModel? tab, string uri)
    {
        if (tab?.CoreWebView is not { } core || string.IsNullOrWhiteSpace(uri))
        {
            return;
        }

        tab.LoadError = null;
        try
        {
            core.Navigate(uri);
        }
        catch (Exception exception)
        {
            tab.LoadError = $"Nie można przejść pod ten adres: {exception.Message}";
            tab.StatusText = tab.LoadError;
        }
    }

    public void NavigateHomeOrNewTab(BrowserTabViewModel? tab, string? homePage)
    {
        if (tab?.CoreWebView is not { } core)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(homePage) || homePage.Equals("about:newtab", StringComparison.OrdinalIgnoreCase))
        {
            tab.Address = string.Empty;
            core.NavigateToString(NewTabDocument);
            return;
        }

        Navigate(tab, homePage);
    }

    public void GoBack(BrowserTabViewModel? tab)
    {
        if (tab?.CoreWebView?.CanGoBack == true)
        {
            tab.CoreWebView.GoBack();
        }
    }

    public void GoForward(BrowserTabViewModel? tab)
    {
        if (tab?.CoreWebView?.CanGoForward == true)
        {
            tab.CoreWebView.GoForward();
        }
    }

    public void Reload(BrowserTabViewModel? tab)
    {
        tab?.CoreWebView?.Reload();
    }

    public void Stop(BrowserTabViewModel? tab)
    {
        tab?.CoreWebView?.Stop();
    }

    public bool TrySetMemoryTarget(BrowserTabViewModel tab, MemoryTargetLevel target)
    {
        if (tab.CoreWebView is null)
        {
            return false;
        }

        try
        {
            // Reflection keeps NovaBrowser compatible with an older Evergreen Runtime where the property is absent.
            var property = typeof(CoreWebView2).GetProperty("MemoryUsageTargetLevel", BindingFlags.Instance | BindingFlags.Public);
            if (property?.CanWrite != true || !property.PropertyType.IsEnum)
            {
                return false;
            }

            var value = Enum.Parse(property.PropertyType, target.ToString(), ignoreCase: true);
            property.SetValue(tab.CoreWebView, value);
            return true;
        }
        catch (NotImplementedException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is NotImplementedException or COMException)
        {
            return false;
        }
    }

    public async Task ClearBrowserDataAsync()
    {
        // Wszystkie karty używają wspólnego profilu; wystarczy wyczyścić go raz.
        var core = _subscriptions.Values.Select(subscription => subscription.Core).FirstOrDefault();
        if (core?.Profile is not { } profile)
        {
            return;
        }

        var method = profile.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(candidate => candidate.Name == "ClearBrowsingDataAsync" && candidate.GetParameters().Length == 1);

        if (method is null)
        {
            return;
        }

        try
        {
            var parameterType = method.GetParameters()[0].ParameterType;
            var allProfile = Enum.Parse(parameterType, "AllProfile", ignoreCase: true);
            if (method.Invoke(profile, [allProfile]) is Task task)
            {
                await task.ConfigureAwait(true);
            }
        }
        catch (Exception)
        {
            // Czyszczenie własnych danych SQLite nadal przebiega; starszy Runtime może nie obsługiwać API profilu.
        }
    }

    public void DisposeTab(BrowserTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        _initializationGates.Remove(tab.Id);
        if (_subscriptions.Remove(tab.Id, out var subscription))
        {
            DetachEvents(subscription);
            CoreWebViewDisposed?.Invoke(subscription.Core);
        }

        tab.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var subscription in _subscriptions.Values.ToArray())
        {
            DetachEvents(subscription);
            CoreWebViewDisposed?.Invoke(subscription.Core);
            subscription.Browser.Dispose();
        }

        _subscriptions.Clear();
        _initializationGates.Clear();
    }

    private async Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        if (_environment is not null)
        {
            return _environment;
        }

        await _environmentLock.WaitAsync();
        try
        {
            if (_environment is null)
            {
                Directory.CreateDirectory(_userDataDirectory);
                _environment = await CoreWebView2Environment.CreateAsync(null, _userDataDirectory);
            }

            return _environment;
        }
        finally
        {
            _environmentLock.Release();
        }
    }

    private void AttachEvents(BrowserTabViewModel tab, WebView2 browser, CoreWebView2 core)
    {
        EventHandler<CoreWebView2NavigationStartingEventArgs> navigationStarting = (_, args) =>
        {
            tab.IsLoading = true;
            tab.LoadError = null;
            tab.StatusText = "Ładowanie…";
            if (!string.IsNullOrWhiteSpace(args.Uri))
            {
                tab.Address = args.Uri;
            }
        };

        EventHandler<CoreWebView2SourceChangedEventArgs> sourceChanged = (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(core.Source))
            {
                tab.Address = core.Source;
            }
        };

        EventHandler<CoreWebView2NavigationCompletedEventArgs> navigationCompleted = async (_, args) =>
        {
            tab.IsLoading = false;
            if (args.IsSuccess)
            {
                tab.StatusText = "Gotowe";
                if (!string.IsNullOrWhiteSpace(core.Source))
                {
                    tab.Address = core.Source;
                }

                if (PageVisitedAsync is not null && !string.IsNullOrWhiteSpace(core.Source))
                {
                    await PageVisitedAsync(tab);
                }
            }
            else
            {
                tab.LoadError = $"Nie udało się załadować strony ({args.WebErrorStatus}).";
                tab.StatusText = tab.LoadError;
            }
        };

        EventHandler<object> documentTitleChanged = (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(core.DocumentTitle))
            {
                tab.Title = core.DocumentTitle;
            }
        };

        EventHandler<object> historyChanged = (_, _) =>
        {
            tab.CanGoBack = core.CanGoBack;
            tab.CanGoForward = core.CanGoForward;
        };

        EventHandler<object> faviconChanged = async (_, _) => await UpdateFaviconAsync(tab, core);

        EventHandler<object> audioChanged = (_, _) =>
            tab.IsAudioPlaying = core.IsDocumentPlayingAudio;
        tab.IsAudioPlaying = core.IsDocumentPlayingAudio;

        EventHandler<CoreWebView2NewWindowRequestedEventArgs> newWindowRequested = (_, args) =>
        {
            var uri = args.Uri;
            args.Handled = true;
            if (!string.IsNullOrWhiteSpace(uri) && NewTabRequestedAsync is not null)
            {
                _dispatcher.BeginInvoke(async () => await NewTabRequestedAsync(uri));
            }
        };

        EventHandler<CoreWebView2ProcessFailedEventArgs> processFailed = (_, args) =>
        {
            tab.IsLoading = false;
            tab.LoadError = $"Proces strony zakończył się nieoczekiwanie ({args.ProcessFailedKind}).";
            tab.StatusText = tab.LoadError;
        };

        EventHandler<object> windowCloseRequested = (_, _) =>
        {
            ShortcutRequested?.Invoke(BrowserShortcut.CloseTab);
        };

        EventHandler<CoreWebView2DownloadStartingEventArgs> downloadStarting = (_, _) => { };

        core.NavigationStarting += navigationStarting;
        core.SourceChanged += sourceChanged;
        core.NavigationCompleted += navigationCompleted;
        core.DocumentTitleChanged += documentTitleChanged;
        core.HistoryChanged += historyChanged;
        core.FaviconChanged += faviconChanged;
        core.IsDocumentPlayingAudioChanged += audioChanged;
        core.NewWindowRequested += newWindowRequested;
        core.ProcessFailed += processFailed;
        core.WindowCloseRequested += windowCloseRequested;
        core.DownloadStarting += downloadStarting;
        // The WPF wrapper forwards WebView accelerator keys through WPF's routed
        // input pipeline. MainWindow handles them in PreviewKeyDown, avoiding a
        // synchronous call back into WebView2 from an accelerator callback.

        _subscriptions[tab.Id] = new TabSubscription(
            tab.Id,
            browser,
            core,
            navigationStarting,
            sourceChanged,
            navigationCompleted,
            documentTitleChanged,
            historyChanged,
            faviconChanged,
            audioChanged,
            newWindowRequested,
            processFailed,
            windowCloseRequested,
            downloadStarting);
    }

    private static void DetachEvents(TabSubscription subscription)
    {
        subscription.Core.NavigationStarting -= subscription.NavigationStarting;
        subscription.Core.SourceChanged -= subscription.SourceChanged;
        subscription.Core.NavigationCompleted -= subscription.NavigationCompleted;
        subscription.Core.DocumentTitleChanged -= subscription.DocumentTitleChanged;
        subscription.Core.HistoryChanged -= subscription.HistoryChanged;
        subscription.Core.FaviconChanged -= subscription.FaviconChanged;
        subscription.Core.IsDocumentPlayingAudioChanged -= subscription.AudioChanged;
        subscription.Core.NewWindowRequested -= subscription.NewWindowRequested;
        subscription.Core.ProcessFailed -= subscription.ProcessFailed;
        subscription.Core.WindowCloseRequested -= subscription.WindowCloseRequested;
        subscription.Core.DownloadStarting -= subscription.DownloadStarting;
    }

    private static async Task UpdateFaviconAsync(BrowserTabViewModel tab, CoreWebView2 core)
    {
        try
        {
            await using var faviconStream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            using var memory = new MemoryStream();
            await faviconStream.CopyToAsync(memory);
            memory.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = memory;
            image.EndInit();
            image.Freeze();
            tab.Favicon = image;
        }
        catch (Exception)
        {
            // Favicona jest dodatkiem wizualnym: brak lub nieprawidłowy obraz nie powinien zatrzymać nawigacji.
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record TabSubscription(
        Guid TabId,
        WebView2 Browser,
        CoreWebView2 Core,
        EventHandler<CoreWebView2NavigationStartingEventArgs> NavigationStarting,
        EventHandler<CoreWebView2SourceChangedEventArgs> SourceChanged,
        EventHandler<CoreWebView2NavigationCompletedEventArgs> NavigationCompleted,
        EventHandler<object> DocumentTitleChanged,
        EventHandler<object> HistoryChanged,
        EventHandler<object> FaviconChanged,
        EventHandler<object> AudioChanged,
        EventHandler<CoreWebView2NewWindowRequestedEventArgs> NewWindowRequested,
        EventHandler<CoreWebView2ProcessFailedEventArgs> ProcessFailed,
        EventHandler<object> WindowCloseRequested,
        EventHandler<CoreWebView2DownloadStartingEventArgs> DownloadStarting);

    private const string NewTabDocument = """
        <!doctype html>
        <html lang="pl">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width,initial-scale=1">
          <title>Nowa karta</title>
          <style>
            :root { color-scheme: dark; }
            * { box-sizing: border-box; }
            body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: #16161d; color: #f1f0f7; font: 16px "Segoe UI", sans-serif; }
            main { text-align: center; max-width: 560px; padding: 32px; }
            .mark { width: 54px; height: 54px; margin: 0 auto 18px; border-radius: 17px; display: grid; place-items: center; background: linear-gradient(135deg,#a78bfa,#7156d9); font-size: 29px; font-weight: 700; }
            h1 { margin: 0; font-size: 34px; letter-spacing: -.6px; }
            p { color: #b6b4c5; line-height: 1.55; }
            kbd { display: inline-block; padding: 2px 7px; border: 1px solid #4a4859; border-bottom-width: 2px; border-radius: 6px; color: #e4dfff; font: 13px "Segoe UI", sans-serif; }
          </style>
        </head>
        <body>
          <main>
            <div class="mark">N</div>
            <h1>NovaBrowser</h1>
            <p>Wpisz adres lub zapytanie w pasku u góry. Użyj <kbd>Ctrl</kbd> + <kbd>L</kbd>, aby od razu przejść do paska adresu.</p>
          </main>
        </body>
        </html>
        """;
}
