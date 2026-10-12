using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NovaBrowser.Models;
using NovaBrowser.Services;

namespace NovaBrowser;

/// <summary>
/// Separate private shell. It intentionally never references HistoryService,
/// SessionService, BookmarkService or SqliteDatabase.
/// WebView2 InPrivate controller options enforce an off-the-record profile.
/// </summary>
public partial class PrivateWindow : Window
{
    private sealed class PrivateTab(TabItem item, WebView2 view, string url)
    {
        public TabItem Item { get; } = item;
        public WebView2 View { get; } = view;
        public string InitialUrl { get; } = url;
        public Task? Initialization { get; set; }
        public bool Closed { get; set; }
    }

    private readonly Dictionary<TabItem, PrivateTab> _tabs = [];
    private readonly AddressResolver _resolver = new();
    private CoreWebView2Environment? _environment;
    private bool _closed;

    public PrivateWindow()
    {
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // The same user-data folder is used, but an InPrivate named profile
            // is isolated from the ordinary default profile.
            _environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: AppPaths.WebViewUserDataDirectory);
            AddTab("https://www.google.com/");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Nie można uruchomić trybu prywatnego:\n{ex.Message}",
                "Nova Private", MessageBoxButton.OK, MessageBoxImage.Error);
            Close(); // Never fall back to a regular, persistent profile.
        }
    }

    private void AddTab(string? address = null)
    {
        if (_closed) return;
        var tabItem = new TabItem { Header = "Prywatna karta", MaxWidth = 220 };
        var view = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(22, 22, 29) };
        tabItem.Content = view;
        _tabs[tabItem] = new PrivateTab(tabItem, view, address ?? "https://www.google.com/");
        PrivateTabs.Items.Add(tabItem);
        PrivateTabs.SelectedItem = tabItem;
    }

    private async void PrivateTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != PrivateTabs || _closed || PrivateTabs.SelectedItem is not TabItem item ||
            !_tabs.TryGetValue(item, out var tab)) return;

        AddressBox.Text = tab.View.CoreWebView2?.Source ?? tab.InitialUrl;
        try
        {
            await EnsureTabAsync(tab);
        }
        catch (Exception ex)
        {
            if (!_closed && !tab.Closed)
            {
                tab.Item.Header = "Błąd prywatnej karty";
                System.Windows.MessageBox.Show(ex.Message, "Nova Private",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private Task EnsureTabAsync(PrivateTab tab)
    {
        if (_environment is null || _closed || tab.Closed) return Task.CompletedTask;
        return tab.Initialization ??= InitializeCoreAsync(tab);
    }

    private async Task InitializeCoreAsync(PrivateTab tab)
    {
        // Wait for TabControl to insert the selected tab's WebView into the visual tree.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (_closed || tab.Closed || _environment is null) return;

        var options = _environment.CreateCoreWebView2ControllerOptions();
        options.ProfileName = "NovaPrivate";
        options.IsInPrivateModeEnabled = true;
        await tab.View.EnsureCoreWebView2Async(_environment, options);
        if (_closed || tab.Closed) return;

        var core = tab.View.CoreWebView2
            ?? throw new InvalidOperationException("WebView2 nie został zainicjalizowany.");

        if (!core.Profile.IsInPrivateModeEnabled)
            throw new InvalidOperationException("Nie udało się włączyć izolowanego profilu InPrivate.");

        core.NavigationStarting += (_, a) =>
        {
            if (!tab.Closed && ReferenceEquals(PrivateTabs.SelectedItem, tab.Item))
                AddressBox.Text = a.Uri;
        };
        core.SourceChanged += (_, _) =>
        {
            if (!tab.Closed && ReferenceEquals(PrivateTabs.SelectedItem, tab.Item))
                AddressBox.Text = core.Source;
        };
        core.DocumentTitleChanged += (_, _) =>
        {
            if (!tab.Closed)
                tab.Item.Header = string.IsNullOrWhiteSpace(core.DocumentTitle)
                    ? "Prywatna karta"
                    : core.DocumentTitle.Length > 25 ? core.DocumentTitle[..25] + "…" : core.DocumentTitle;
        };
        core.NewWindowRequested += (_, a) =>
        {
            a.Handled = true;
            if (!string.IsNullOrWhiteSpace(a.Uri))
                AddTab(a.Uri);
        };
        core.WindowCloseRequested += (_, _) => CloseTab(tab.Item);
        core.Navigate(tab.InitialUrl);
    }

    private PrivateTab? ActiveTab =>
        PrivateTabs.SelectedItem is TabItem item && _tabs.TryGetValue(item, out var tab) ? tab : null;

    private void CloseTab(TabItem item)
    {
        if (!_tabs.Remove(item, out var tab)) return;
        tab.Closed = true;
        PrivateTabs.Items.Remove(item);
        tab.Item.Content = null;
        tab.View.Dispose();
        if (PrivateTabs.Items.Count == 0 && !_closed) Close();
    }

    private async void NavigateAddress()
    {
        var tab = ActiveTab;
        if (tab is null) return;
        try
        {
            await EnsureTabAsync(tab);
            var result = _resolver.Resolve(AddressBox.Text, SearchEngineKind.DuckDuckGo);
            if (result.HasNavigation && result.NavigationUri is not null && !tab.Closed)
                tab.View.CoreWebView2?.Navigate(result.NavigationUri.AbsoluteUri);
        }
        catch (Exception ex)
        {
            if (!_closed) System.Windows.MessageBox.Show(ex.Message, "Nova Private");
        }
    }

    private void AddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        NavigateAddress();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (!ctrl) return;
        switch (e.Key)
        {
            case Key.T:
                AddTab();
                e.Handled = true;
                break;
            case Key.W:
                if (ActiveTab is { } tab) CloseTab(tab.Item);
                e.Handled = true;
                break;
            case Key.L:
                AddressBox.Focus();
                AddressBox.SelectAll();
                e.Handled = true;
                break;
        }
    }

    private void BackClicked(object sender, RoutedEventArgs e)
    {
        var core = ActiveTab?.View.CoreWebView2;
        if (core?.CanGoBack == true) core.GoBack();
    }

    private void ForwardClicked(object sender, RoutedEventArgs e)
    {
        var core = ActiveTab?.View.CoreWebView2;
        if (core?.CanGoForward == true) core.GoForward();
    }

    private void ReloadClicked(object sender, RoutedEventArgs e) =>
        ActiveTab?.View.CoreWebView2?.Reload();

    private void NewTabClicked(object sender, RoutedEventArgs e) => AddTab();

    private void CloseTabClicked(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is { } tab) CloseTab(tab.Item);
    }

    private void MinimizeClicked(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClicked(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        foreach (var tab in _tabs.Values.ToArray())
        {
            tab.Closed = true;
            tab.Item.Content = null;
            tab.View.Dispose();
        }
        _tabs.Clear();
        PrivateTabs.Items.Clear();
        // WebView2 releases the InPrivate session when its last controller closes.
        // We deliberately do not delete any shared user-data-folder files.
    }
}
