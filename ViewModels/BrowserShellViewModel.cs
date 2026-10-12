using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using NovaBrowser.Infrastructure;
using NovaBrowser.Models;
using NovaBrowser.Services;

namespace NovaBrowser.ViewModels;

/// <summary>
/// Główny model widoku powłoki przeglądarki. Łączy komendy UI z małymi,
/// wyspecjalizowanymi serwisami, bez umieszczania SQL lub WebView2 w XAML.
/// </summary>
public sealed class BrowserShellViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly HistoryService _historyService;
    private readonly BookmarkService _bookmarkService;
    private readonly SessionService _sessionService;
    private readonly BrowserEngineService _browserEngine;
    private readonly AddressResolver _addressResolver;
    private readonly ThemeService _themeService;
    private readonly MemoryManager _memoryManager;
    private readonly DownloadService _downloadService;
    private readonly NovaBlockService _novaBlock;
    private readonly Dictionary<BrowserTabViewModel, PropertyChangedEventHandler> _tabHandlers = [];
    private CancellationTokenSource? _sessionSaveCancellation;
    private BrowserSettings _settings = SettingsService.Default;
    private BrowserTabViewModel? _activeTab;
    private string _addressText = string.Empty;
    private SidePanelKind _sidePanel;
    private string _selectedTheme = "Ciemny";
    private string _selectedSearchEngine = "DuckDuckGo";
    private string _selectedMemoryMode = "Zbalansowany";
    private string _homePage = SettingsService.Default.HomePage;
    private bool _restoreSession = SettingsService.Default.RestoreSession;
    private bool _novaBlockEnabled = true;
    private string _filterUpdateStatus = "Listy filtrów nie zostały jeszcze zaktualizowane.";
    private string _downloadFolder = string.Empty;
    private bool _isShuttingDown;
    private bool _disposed;

    public BrowserShellViewModel(
        SettingsService settingsService,
        HistoryService historyService,
        BookmarkService bookmarkService,
        SessionService sessionService,
        BrowserEngineService browserEngine,
        AddressResolver addressResolver,
        ThemeService themeService,
        MemoryManager memoryManager,
        DownloadService downloadService,
        NovaBlockService novaBlock)
    {
        _settingsService = settingsService;
        _historyService = historyService;
        _bookmarkService = bookmarkService;
        _sessionService = sessionService;
        _browserEngine = browserEngine;
        _addressResolver = addressResolver;
        _themeService = themeService;
        _memoryManager = memoryManager;
        _downloadService = downloadService;
        _novaBlock = novaBlock;
        _novaBlock.StateChanged += OnNovaBlockChanged;
        _memoryManager.HasActiveDownloads = () => _downloadService.HasActiveDownloads;

        _browserEngine.NewTabRequestedAsync = url => CreateTabAsync(url, select: true);
        _browserEngine.PageVisitedAsync = OnPageVisitedAsync;
        _browserEngine.ShortcutRequested = HandleBrowserShortcut;
        _browserEngine.CoreWebViewInitialized = core =>
        {
            _downloadService.Attach(core, () => EffectiveDownloadFolder);
            _novaBlock.Attach(core);
        };
        _browserEngine.CoreWebViewDisposed = core =>
        {
            _downloadService.Detach(core);
            _novaBlock.Detach(core);
        };

        NewTabCommand = new AsyncRelayCommand(() => CreateTabAsync());
        CloseTabCommand = new AsyncRelayCommand(CloseTabAsync);
        CloseActiveTabCommand = new AsyncRelayCommand(() => CloseTabAsync(ActiveTab));
        RestoreClosedTabCommand = new AsyncRelayCommand(RestoreClosedTabAsync);
        NextTabCommand = new RelayCommand(SelectNextTab, () => Tabs.Count > 1);
        FocusAddressCommand = new RelayCommand(() => FocusAddressRequested?.Invoke());
        NavigateCommand = new AsyncRelayCommand(NavigateFromAddressAsync);
        BackCommand = new RelayCommand(() => _browserEngine.GoBack(ActiveTab));
        ForwardCommand = new RelayCommand(() => _browserEngine.GoForward(ActiveTab));
        ReloadCommand = new RelayCommand(() => _browserEngine.Reload(ActiveTab));
        StopCommand = new RelayCommand(() => _browserEngine.Stop(ActiveTab));
        ToggleBookmarkCommand = new AsyncRelayCommand(ToggleCurrentBookmarkAsync);
        AddCurrentBookmarkCommand = new AsyncRelayCommand(AddCurrentBookmarkAsync);
        ToggleHistoryCommand = new AsyncRelayCommand(() => TogglePanelAsync(SidePanelKind.History));
        ToggleBookmarksCommand = new AsyncRelayCommand(() => TogglePanelAsync(SidePanelKind.Bookmarks));
        ToggleDownloadsCommand = new AsyncRelayCommand(() => TogglePanelAsync(SidePanelKind.Downloads));
        ToggleSettingsCommand = new AsyncRelayCommand(() => TogglePanelAsync(SidePanelKind.Settings));
        CloseSidePanelCommand = new RelayCommand(() => SidePanel = SidePanelKind.None);
        RefreshHistoryCommand = new AsyncRelayCommand(RefreshHistoryAsync);
        ClearHistoryCommand = new AsyncRelayCommand(ClearHistoryAsync);
        NavigateHistoryEntryCommand = new AsyncRelayCommand(NavigateHistoryEntryAsync);
        DeleteHistoryEntryCommand = new AsyncRelayCommand(DeleteHistoryEntryAsync);
        RefreshBookmarksCommand = new AsyncRelayCommand(RefreshBookmarksAsync);
        NavigateBookmarkCommand = new AsyncRelayCommand(NavigateBookmarkAsync);
        DeleteBookmarkCommand = new AsyncRelayCommand(DeleteBookmarkAsync);
        OpenDownloadsFolderCommand = new RelayCommand(() => _downloadService.OpenDownloadsFolder(() => EffectiveDownloadFolder));
        ClearFinishedDownloadsCommand = new RelayCommand(_downloadService.ClearFinished);
        ChooseDownloadFolderCommand = new RelayCommand(ChooseDownloadFolder);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        ClearBrowsingDataCommand = new AsyncRelayCommand(ClearBrowsingDataAsync);
        OpenPrivateWindowCommand = new RelayCommand(() => PrivateWindowRequested?.Invoke());
        ToggleSiteProtectionCommand = new AsyncRelayCommand(ToggleSiteProtectionAsync);
        UpdateNovaBlockFiltersCommand = new AsyncRelayCommand(UpdateNovaBlockFiltersAsync);
    }

    public ObservableCollection<BrowserTabViewModel> Tabs { get; } = [];

    public ObservableCollection<HistoryEntry> HistoryEntries { get; } = [];

    public ObservableCollection<BookmarkItem> Bookmarks { get; } = [];

    public ObservableCollection<DownloadItemViewModel> Downloads => _downloadService.Downloads;

    public IReadOnlyList<string> ThemeOptions { get; } = ["Ciemny", "Jasny"];

    public IReadOnlyList<string> SearchEngineOptions { get; } = ["Google", "Bing", "DuckDuckGo", "Startpage"];

    public IReadOnlyList<string> MemoryModeOptions { get; } = ["Zbalansowany", "Oszczędny", "Wydajność"];

    public event Action? FocusAddressRequested;
    public event Action? PrivateWindowRequested;

    public BrowserTabViewModel? ActiveTab
    {
        get => _activeTab;
        set
        {
            if (ReferenceEquals(_activeTab, value))
            {
                return;
            }

            if (_activeTab is not null)
            {
                _activeTab.MarkDeactivated();
                _memoryManager.NotifyTabDeactivated(_activeTab);
            }

            _activeTab = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasNoTabs));

            if (_activeTab is not null)
            {
                _activeTab.MarkActivated();
                _memoryManager.NotifyTabActivated(_activeTab);
                AddressText = _activeTab.Address;
                OnPropertyChanged(nameof(SiteProtectionLabel));
                _ = ActivateTabAsync(_activeTab);
            }
            else
            {
                AddressText = string.Empty;
            }

            UpdateNavigationCommandState();
            ScheduleSessionSave();
        }
    }

    public bool HasNoTabs => Tabs.Count == 0;

    public string AddressText
    {
        get => _addressText;
        set => SetProperty(ref _addressText, value);
    }

    public SidePanelKind SidePanel
    {
        get => _sidePanel;
        private set
        {
            if (SetProperty(ref _sidePanel, value))
            {
                OnPropertyChanged(nameof(IsSidePanelOpen));
                OnPropertyChanged(nameof(SidePanelTitle));
            }
        }
    }

    public bool IsSidePanelOpen => SidePanel != SidePanelKind.None;

    public string SidePanelTitle => SidePanel switch
    {
        SidePanelKind.History => "Historia",
        SidePanelKind.Bookmarks => "Zakładki",
        SidePanelKind.Downloads => "Pobieranie",
        SidePanelKind.Settings => "Ustawienia",
        _ => string.Empty
    };

    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (SetProperty(ref _selectedTheme, value) && TryParseTheme(value, out var theme))
            {
                _themeService.ApplyTheme(theme);
            }
        }
    }

    public string SelectedSearchEngine
    {
        get => _selectedSearchEngine;
        set => SetProperty(ref _selectedSearchEngine, value);
    }

    public string SelectedMemoryMode
    {
        get => _selectedMemoryMode;
        set
        {
            if (SetProperty(ref _selectedMemoryMode, value) && TryParseMemoryMode(value, out var mode))
            {
                _memoryManager.SetMode(mode);
                OnPropertyChanged(nameof(MemoryStatusText));
            }
        }
    }

    public string HomePage
    {
        get => _homePage;
        set => SetProperty(ref _homePage, value);
    }

    public bool RestoreSession
    {
        get => _restoreSession;
        set => SetProperty(ref _restoreSession, value);
    }

    public string DownloadFolder
    {
        get => _downloadFolder;
        set => SetProperty(ref _downloadFolder, value);
    }

    public string MemoryStatusText =>
        $"RAM: {SelectedMemoryMode} • kart Low: {_memoryManager.LowTargetTabCount}";

    public bool NovaBlockEnabled
    {
        get => _novaBlockEnabled;
        set
        {
            if (SetProperty(ref _novaBlockEnabled, value))
            {
                _novaBlock.SetEnabled(value);
                OnPropertyChanged(nameof(NovaBlockStatus));
            }
        }
    }

    public string NovaBlockStatus =>
        $"Zablokowano: {_novaBlock.BlockedRequests} • filtrów domenowych: {_novaBlock.FilterCount}";

    public string FilterUpdateStatus
    {
        get => _filterUpdateStatus;
        private set => SetProperty(ref _filterUpdateStatus, value);
    }

    public string SiteProtectionLabel
    {
        get
        {
            if (ActiveTab is null ||
                !Uri.TryCreate(ActiveTab.Address, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
                return "Brak strony do wykluczenia";
            return _novaBlock.IsExcluded(uri.Host)
                ? $"Włącz ochronę: {uri.Host}"
                : $"Wyłącz ochronę: {uri.Host}";
        }
    }

    public AsyncRelayCommand NewTabCommand { get; }
    public AsyncRelayCommand CloseTabCommand { get; }
    public AsyncRelayCommand CloseActiveTabCommand { get; }
    public AsyncRelayCommand RestoreClosedTabCommand { get; }
    public RelayCommand NextTabCommand { get; }
    public RelayCommand FocusAddressCommand { get; }
    public AsyncRelayCommand NavigateCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand ForwardCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public RelayCommand StopCommand { get; }
    public AsyncRelayCommand ToggleBookmarkCommand { get; }
    public AsyncRelayCommand AddCurrentBookmarkCommand { get; }
    public AsyncRelayCommand ToggleHistoryCommand { get; }
    public AsyncRelayCommand ToggleBookmarksCommand { get; }
    public AsyncRelayCommand ToggleDownloadsCommand { get; }
    public AsyncRelayCommand ToggleSettingsCommand { get; }
    public RelayCommand CloseSidePanelCommand { get; }
    public AsyncRelayCommand RefreshHistoryCommand { get; }
    public AsyncRelayCommand ClearHistoryCommand { get; }
    public AsyncRelayCommand NavigateHistoryEntryCommand { get; }
    public AsyncRelayCommand DeleteHistoryEntryCommand { get; }
    public AsyncRelayCommand RefreshBookmarksCommand { get; }
    public AsyncRelayCommand NavigateBookmarkCommand { get; }
    public AsyncRelayCommand DeleteBookmarkCommand { get; }
    public RelayCommand OpenDownloadsFolderCommand { get; }
    public RelayCommand ClearFinishedDownloadsCommand { get; }
    public RelayCommand ChooseDownloadFolderCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public AsyncRelayCommand ClearBrowsingDataCommand { get; }
    public RelayCommand OpenPrivateWindowCommand { get; }
    public AsyncRelayCommand ToggleSiteProtectionCommand { get; }
    public AsyncRelayCommand UpdateNovaBlockFiltersCommand { get; }

    public async Task InitializeAsync()
    {
        ThrowIfDisposed();
        _settings = await _settingsService.GetAsync();
        ApplySettingsToEditor(_settings);
        _novaBlock.Configure(_settings.AdBlockEnabled, _settings.AdBlockExcludedSites);
        _themeService.ApplyTheme(_settings.Theme);
        _memoryManager.SetMode(_settings.MemoryMode);

        await RefreshHistoryAsync();
        await RefreshBookmarksAsync();

        if (_settings.RestoreSession)
        {
            var restoredTabs = await _sessionService.GetSessionAsync();
            foreach (var restored in restoredTabs)
            {
                AddRestoredTab(restored);
            }

            ActiveTab = Tabs.FirstOrDefault(tab => restoredTabs.FirstOrDefault(restored =>
                string.Equals(restored.Url, tab.Address, StringComparison.Ordinal) && restored.IsActive) is not null)
                ?? Tabs.FirstOrDefault();
        }

        if (ActiveTab is null)
        {
            await CreateTabAsync();
        }
    }

    public async Task ShutdownAsync()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;
        _sessionSaveCancellation?.Cancel();
        try
        {
            if (_settings.RestoreSession)
            {
                await SaveSessionAsync();
            }
            else
            {
                await _sessionService.ClearSessionAsync();
            }
        }
        finally
        {
            _novaBlock.Dispose();
            _downloadService.Dispose();
            _browserEngine.Dispose();
            _memoryManager.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessionSaveCancellation?.Cancel();
        foreach (var pair in _tabHandlers.ToArray())
        {
            pair.Key.PropertyChanged -= pair.Value;
        }

        _tabHandlers.Clear();
        _novaBlock.StateChanged -= OnNovaBlockChanged;
    }

    private async Task CreateTabAsync(string? url = null, string? title = null, bool select = true)
    {
        ThrowIfDisposed();
        var tab = new BrowserTabViewModel(url, title);
        AttachTab(tab);
        Tabs.Add(tab);
        _memoryManager.Register(tab);
        OnPropertyChanged(nameof(HasNoTabs));
        NextTabCommand.RaiseCanExecuteChanged();

        if (select || ActiveTab is null)
        {
            ActiveTab = tab;
        }
        else
        {
            tab.MarkDeactivated();
        }

        ScheduleSessionSave();
        await Task.CompletedTask;
    }

    private void AddRestoredTab(SessionTab restored)
    {
        var tab = new BrowserTabViewModel(restored.Url, restored.Title);
        AttachTab(tab);
        Tabs.Add(tab);
        _memoryManager.Register(tab);
        tab.MarkDeactivated();
    }

    private async Task ActivateTabAsync(BrowserTabViewModel tab)
    {
        try
        {
            await _browserEngine.EnsureInitializedAsync(tab, HomePage);
            tab.SetMemoryTarget(MemoryTargetLevel.Normal);
        }
        catch (Exception exception)
        {
            tab.LoadError = $"Nie można otworzyć karty: {exception.Message}";
            tab.StatusText = tab.LoadError;
        }
    }

    private async Task NavigateFromAddressAsync()
    {
        if (ActiveTab is null)
        {
            await CreateTabAsync();
        }

        if (ActiveTab is not { } tab)
        {
            return;
        }

        await _browserEngine.EnsureInitializedAsync(tab, HomePage);
        var resolution = _addressResolver.Resolve(AddressText, _settings.SearchEngine);
        if (!resolution.HasNavigation || resolution.NavigationUri is null)
        {
            return;
        }

        tab.Address = resolution.NavigationUri.AbsoluteUri;
        _browserEngine.Navigate(tab, resolution.NavigationUri.AbsoluteUri);
    }

    private async Task CloseTabAsync(object? parameter)
    {
        var tab = parameter as BrowserTabViewModel ?? ActiveTab;
        if (tab is null || !Tabs.Contains(tab))
        {
            return;
        }

        var index = Tabs.IndexOf(tab);
        var wasActive = ReferenceEquals(tab, ActiveTab);
        var nextActiveTab = wasActive && Tabs.Count > 1
            ? Tabs[index < Tabs.Count - 1 ? index + 1 : index - 1]
            : null;
        if (AddressResolver.TryResolveHttpAddress(tab.Address, out _) || string.IsNullOrWhiteSpace(tab.Address))
        {
            await _sessionService.SaveClosedTabAsync(new SessionTab(
                string.IsNullOrWhiteSpace(tab.Address) ? "about:newtab" : tab.Address,
                tab.Title));
        }

        // Change selection before unregistering the old tab. This lets MemoryManager
        // observe the normal deactivate/activate sequence without re-registering a
        // tab that is about to be disposed.
        if (wasActive)
        {
            ActiveTab = nextActiveTab;
        }

        DetachTab(tab);
        _memoryManager.Unregister(tab);
        _browserEngine.DisposeTab(tab);
        Tabs.Remove(tab);

        OnPropertyChanged(nameof(HasNoTabs));
        NextTabCommand.RaiseCanExecuteChanged();
        ScheduleSessionSave();
    }

    private async Task RestoreClosedTabAsync()
    {
        var tab = await _sessionService.RestoreLastClosedTabAsync();
        if (tab is null)
        {
            return;
        }

        await CreateTabAsync(tab.Url, tab.Title, select: true);
    }

    private void SelectNextTab()
    {
        if (Tabs.Count < 2 || ActiveTab is null)
        {
            return;
        }

        var index = Tabs.IndexOf(ActiveTab);
        ActiveTab = Tabs[(index + 1) % Tabs.Count];
    }

    private async Task ToggleCurrentBookmarkAsync()
    {
        if (ActiveTab is not { } tab || !AddressResolver.TryResolveHttpAddress(tab.Address, out var uri) || uri is null)
        {
            return;
        }

        var existing = await _bookmarkService.GetByUrlAsync(uri.AbsoluteUri);
        if (existing is null)
        {
            await _bookmarkService.AddAsync(uri.AbsoluteUri, tab.Title);
        }
        else
        {
            await _bookmarkService.RemoveAsync(existing.Id);
        }

        await RefreshBookmarksAsync();
    }

    private async Task AddCurrentBookmarkAsync()
    {
        if (ActiveTab is not { } tab || !AddressResolver.TryResolveHttpAddress(tab.Address, out var uri) || uri is null)
        {
            return;
        }

        await _bookmarkService.AddAsync(uri.AbsoluteUri, tab.Title);
        await RefreshBookmarksAsync();
    }

    private async Task TogglePanelAsync(SidePanelKind requestedPanel)
    {
        SidePanel = SidePanel == requestedPanel ? SidePanelKind.None : requestedPanel;
        switch (SidePanel)
        {
            case SidePanelKind.History:
                await RefreshHistoryAsync();
                break;
            case SidePanelKind.Bookmarks:
                await RefreshBookmarksAsync();
                break;
        }
    }

    private async Task RefreshHistoryAsync()
    {
        var entries = await _historyService.GetRecentAsync();
        HistoryEntries.Clear();
        foreach (var entry in entries)
        {
            HistoryEntries.Add(entry);
        }
    }

    private async Task ClearHistoryAsync()
    {
        await _historyService.ClearAsync();
        HistoryEntries.Clear();
    }

    private async Task NavigateHistoryEntryAsync(object? parameter)
    {
        if (parameter is not HistoryEntry entry)
        {
            return;
        }

        AddressText = entry.Url;
        await NavigateFromAddressAsync();
    }

    private async Task DeleteHistoryEntryAsync(object? parameter)
    {
        if (parameter is not HistoryEntry entry)
        {
            return;
        }

        await _historyService.DeleteAsync(entry.Id);
        HistoryEntries.Remove(entry);
    }

    private async Task RefreshBookmarksAsync()
    {
        var entries = await _bookmarkService.GetAllAsync();
        Bookmarks.Clear();
        foreach (var entry in entries)
        {
            Bookmarks.Add(entry);
        }
    }

    private async Task NavigateBookmarkAsync(object? parameter)
    {
        if (parameter is not BookmarkItem bookmark)
        {
            return;
        }

        AddressText = bookmark.Url;
        await NavigateFromAddressAsync();
    }

    private async Task DeleteBookmarkAsync(object? parameter)
    {
        if (parameter is not BookmarkItem bookmark)
        {
            return;
        }

        await _bookmarkService.RemoveAsync(bookmark.Id);
        Bookmarks.Remove(bookmark);
    }


    private void OnNovaBlockChanged()
    {
        OnPropertyChanged(nameof(NovaBlockStatus));
        OnPropertyChanged(nameof(SiteProtectionLabel));
    }

    private async Task ToggleSiteProtectionAsync()
    {
        if (ActiveTab is not { } tab ||
            !Uri.TryCreate(tab.Address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https")) return;

        _novaBlock.ToggleExcludedHost(uri.Host);
        await SaveSettingsAsync();
        _browserEngine.Reload(tab);
    }

    private async Task UpdateNovaBlockFiltersAsync()
    {
        try
        {
            FilterUpdateStatus = "Aktualizowanie list...";
            var count = await _novaBlock.UpdateFilterListsAsync();
            FilterUpdateStatus = $"Zaktualizowano. Domeny: {count}.";
        }
        catch (Exception ex)
        {
            FilterUpdateStatus = $"Nie udało się zaktualizować: {ex.Message}";
        }
    }

    private void ChooseDownloadFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Wybierz folder pobierania NovaBrowser",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(EffectiveDownloadFolder) ? EffectiveDownloadFolder : AppPaths.DefaultDownloadsDirectory
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            DownloadFolder = dialog.SelectedPath;
        }
    }

    private async Task SaveSettingsAsync()
    {
        if (!TryParseTheme(SelectedTheme, out var theme) ||
            !TryParseSearchEngine(SelectedSearchEngine, out var searchEngine) ||
            !TryParseMemoryMode(SelectedMemoryMode, out var memoryMode))
        {
            return;
        }

        var homePage = string.IsNullOrWhiteSpace(HomePage) ? SettingsService.Default.HomePage : HomePage.Trim();
        _settings = new BrowserSettings
        {
            Theme = theme,
            SearchEngine = searchEngine,
            HomePage = homePage,
            RestoreSession = RestoreSession,
            DownloadFolder = string.IsNullOrWhiteSpace(DownloadFolder) ? null : DownloadFolder.Trim(),
            MemoryMode = memoryMode,
            AdBlockEnabled = NovaBlockEnabled,
            AdBlockExcludedSites = _novaBlock.ExcludedSites
        };

        await _settingsService.SaveAsync(_settings);
        _themeService.ApplyTheme(theme);
        _memoryManager.SetMode(memoryMode);
        OnPropertyChanged(nameof(MemoryStatusText));
    }

    private async Task ClearBrowsingDataAsync()
    {
        await _historyService.ClearAsync();
        await _browserEngine.ClearBrowserDataAsync();
        HistoryEntries.Clear();
        if (ActiveTab is not null)
        {
            ActiveTab.StatusText = "Wyczyszczono historię i dane profilu przeglądarki.";
        }
    }

    private async Task OnPageVisitedAsync(BrowserTabViewModel tab)
    {
        if (!AddressResolver.TryResolveHttpAddress(tab.Address, out var uri) || uri is null)
        {
            return;
        }

        await _historyService.RecordVisitAsync(uri.AbsoluteUri, tab.Title);
        ScheduleSessionSave();
        if (SidePanel == SidePanelKind.History)
        {
            await RefreshHistoryAsync();
        }
    }

    private void HandleBrowserShortcut(BrowserShortcut shortcut)
    {
        switch (shortcut)
        {
            case BrowserShortcut.NewTab:
                NewTabCommand.Execute(null);
                break;
            case BrowserShortcut.CloseTab:
                CloseActiveTabCommand.Execute(null);
                break;
            case BrowserShortcut.FocusAddress:
                FocusAddressCommand.Execute(null);
                break;
            case BrowserShortcut.NextTab:
                NextTabCommand.Execute(null);
                break;
            case BrowserShortcut.RestoreClosedTab:
                RestoreClosedTabCommand.Execute(null);
                break;
        }
    }

    private void AttachTab(BrowserTabViewModel tab)
    {
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (ReferenceEquals(tab, ActiveTab) && args.PropertyName == nameof(BrowserTabViewModel.Address))
            {
                AddressText = tab.Address;
                OnPropertyChanged(nameof(SiteProtectionLabel));
            }

            if (args.PropertyName == nameof(BrowserTabViewModel.IsAudioPlaying))
            {
                _memoryManager.EvaluateNow();
            }

            if (args.PropertyName is nameof(BrowserTabViewModel.Address) or nameof(BrowserTabViewModel.Title))
            {
                ScheduleSessionSave();
            }

            if (ReferenceEquals(tab, ActiveTab) && args.PropertyName is nameof(BrowserTabViewModel.CanGoBack) or nameof(BrowserTabViewModel.CanGoForward))
            {
                UpdateNavigationCommandState();
            }
        };

        tab.PropertyChanged += handler;
        _tabHandlers.Add(tab, handler);
    }

    private void DetachTab(BrowserTabViewModel tab)
    {
        if (_tabHandlers.Remove(tab, out var handler))
        {
            tab.PropertyChanged -= handler;
        }
    }

    private void ScheduleSessionSave()
    {
        if (_isShuttingDown || _disposed)
        {
            return;
        }

        _sessionSaveCancellation?.Cancel();
        _sessionSaveCancellation?.Dispose();
        var cancellation = _sessionSaveCancellation = new CancellationTokenSource();
        _ = SaveSessionAfterDelayAsync(cancellation.Token);
    }

    private async Task SaveSessionAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(650), cancellationToken);
            await SaveSessionAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Kolejna zmiana karty/nawigacji zastąpiła oczekujący zapis.
        }
    }

    private Task SaveSessionAsync(CancellationToken cancellationToken = default)
    {
        var sessionTabs = Tabs
            .Select((tab, index) => new SessionTab(
                string.IsNullOrWhiteSpace(tab.Address) ? "about:newtab" : tab.Address,
                tab.Title,
                FaviconUrl: null,
                Position: index,
                IsActive: ReferenceEquals(tab, ActiveTab)))
            .ToArray();
        return _sessionService.SaveSessionAsync(sessionTabs, cancellationToken);
    }

    private string EffectiveDownloadFolder => string.IsNullOrWhiteSpace(DownloadFolder)
        ? AppPaths.DefaultDownloadsDirectory
        : DownloadFolder;

    private void ApplySettingsToEditor(BrowserSettings settings)
    {
        _selectedTheme = ThemeLabel(settings.Theme);
        _selectedSearchEngine = SearchEngineLabel(settings.SearchEngine);
        _selectedMemoryMode = MemoryModeLabel(settings.MemoryMode);
        _homePage = settings.HomePage;
        _restoreSession = settings.RestoreSession;
        _novaBlockEnabled = settings.AdBlockEnabled;
        _downloadFolder = settings.DownloadFolder ?? string.Empty;
        OnPropertyChanged(nameof(SelectedTheme));
        OnPropertyChanged(nameof(SelectedSearchEngine));
        OnPropertyChanged(nameof(SelectedMemoryMode));
        OnPropertyChanged(nameof(HomePage));
        OnPropertyChanged(nameof(RestoreSession));
        OnPropertyChanged(nameof(DownloadFolder));
        OnPropertyChanged(nameof(NovaBlockEnabled));
        OnPropertyChanged(nameof(NovaBlockStatus));
        OnPropertyChanged(nameof(MemoryStatusText));
    }

    private void UpdateNavigationCommandState()
    {
        NextTabCommand.RaiseCanExecuteChanged();
        BackCommand.RaiseCanExecuteChanged();
        ForwardCommand.RaiseCanExecuteChanged();
    }

    private static string ThemeLabel(ThemeMode theme) => theme == ThemeMode.Light ? "Jasny" : "Ciemny";

    private static string SearchEngineLabel(SearchEngineKind engine) => engine switch
    {
        SearchEngineKind.Google => "Google",
        SearchEngineKind.Bing => "Bing",
        SearchEngineKind.Startpage => "Startpage",
        _ => "DuckDuckGo"
    };

    private static string MemoryModeLabel(MemorySavingMode mode) => mode switch
    {
        MemorySavingMode.Conservative => "Oszczędny",
        MemorySavingMode.Performance => "Wydajność",
        _ => "Zbalansowany"
    };

    private static bool TryParseTheme(string? value, out ThemeMode theme)
    {
        theme = string.Equals(value, "Jasny", StringComparison.OrdinalIgnoreCase) ? ThemeMode.Light : ThemeMode.Dark;
        return value is "Ciemny" or "Jasny";
    }

    private static bool TryParseSearchEngine(string? value, out SearchEngineKind engine)
    {
        engine = value switch
        {
            "Google" => SearchEngineKind.Google,
            "Bing" => SearchEngineKind.Bing,
            "Startpage" => SearchEngineKind.Startpage,
            _ => SearchEngineKind.DuckDuckGo
        };
        return value is "Google" or "Bing" or "DuckDuckGo" or "Startpage";
    }

    private static bool TryParseMemoryMode(string? value, out MemorySavingMode mode)
    {
        mode = value switch
        {
            "Oszczędny" => MemorySavingMode.Conservative,
            "Wydajność" => MemorySavingMode.Performance,
            _ => MemorySavingMode.Balanced
        };
        return value is "Zbalansowany" or "Oszczędny" or "Wydajność";
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
