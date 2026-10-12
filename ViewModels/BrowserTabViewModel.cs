using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NovaBrowser.Infrastructure;
using NovaBrowser.Services;

namespace NovaBrowser.ViewModels;

/// <summary>
/// Lekki stan jednej karty. Kontrolka WebView2 jest tworzona dopiero przy pierwszym wyborze karty.
/// </summary>
public sealed class BrowserTabViewModel : ObservableObject, IMemoryManagedTab, IDisposable
{
    private string _title;
    private string _address;
    private ImageSource? _favicon;
    private WebView2? _browserView;
    private CoreWebView2? _coreWebView;
    private bool _isInitialized;
    private bool _isLoading;
    private bool _canGoBack;
    private bool _canGoForward;
    private bool _isActive;
    private DateTimeOffset _inactiveSinceUtc;
    private string _statusText = "Gotowe";
    private string? _loadError;
    private Action<MemoryTargetLevel>? _memoryTargetSetter;

    public BrowserTabViewModel(string? initialUrl = null, string? title = null, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        InitialUrl = initialUrl;
        _address = initialUrl ?? string.Empty;
        _title = string.IsNullOrWhiteSpace(title) ? "Nowa karta" : title;
        _inactiveSinceUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; }

    /// <summary>Adres oczekujący na pierwszą, opóźnioną inicjalizację widoku.</summary>
    public string? InitialUrl { get; private set; }

    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, string.IsNullOrWhiteSpace(value) ? "Nowa karta" : value))
            {
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }
    }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "Nowa karta" : Title;

    public string Address
    {
        get => _address;
        set => SetProperty(ref _address, value);
    }

    public ImageSource? Favicon
    {
        get => _favicon;
        set => SetProperty(ref _favicon, value);
    }

    public WebView2? BrowserView
    {
        get => _browserView;
        internal set => SetProperty(ref _browserView, value);
    }

    internal CoreWebView2? CoreWebView
    {
        get => _coreWebView;
        set => SetProperty(ref _coreWebView, value);
    }

    public bool IsInitialized
    {
        get => _isInitialized;
        internal set => SetProperty(ref _isInitialized, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        internal set => SetProperty(ref _isLoading, value);
    }

    public bool CanGoBack
    {
        get => _canGoBack;
        internal set => SetProperty(ref _canGoBack, value);
    }

    public bool CanGoForward
    {
        get => _canGoForward;
        internal set => SetProperty(ref _canGoForward, value);
    }

    public bool IsActive
    {
        get => _isActive;
        private set => SetProperty(ref _isActive, value);
    }

    public DateTimeOffset InactiveSinceUtc
    {
        get => _inactiveSinceUtc;
        private set => SetProperty(ref _inactiveSinceUtc, value);
    }

    public DateTimeOffset LastActivatedUtc { get; private set; } = DateTimeOffset.UtcNow;

    public string StatusText
    {
        get => _statusText;
        internal set => SetProperty(ref _statusText, value);
    }

    public string? LoadError
    {
        get => _loadError;
        internal set => SetProperty(ref _loadError, value);
    }

    public void MarkActivated()
    {
        IsActive = true;
        LastActivatedUtc = DateTimeOffset.UtcNow;
        SetMemoryTarget(MemoryTargetLevel.Normal);
    }

    public void MarkDeactivated()
    {
        IsActive = false;
        InactiveSinceUtc = DateTimeOffset.UtcNow;
    }

    internal void SetInitialUrl(string? url)
    {
        InitialUrl = url;
        if (!string.IsNullOrWhiteSpace(url))
        {
            Address = url;
        }
    }

    internal string? ConsumeInitialUrl()
    {
        var url = InitialUrl;
        InitialUrl = null;
        return url;
    }

    internal void SetMemoryTargetHandler(Action<MemoryTargetLevel> memoryTargetSetter) =>
        _memoryTargetSetter = memoryTargetSetter;

    public void SetMemoryTarget(MemoryTargetLevel level) => _memoryTargetSetter?.Invoke(level);

    public void Dispose()
    {
        _memoryTargetSetter = null;
        CoreWebView = null;
        BrowserView?.Dispose();
        BrowserView = null;
    }
}
