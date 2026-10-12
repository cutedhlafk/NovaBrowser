using System.Windows;
using NovaBrowser.Models;
using NovaBrowser.Services;
using NovaBrowser.ViewModels;

namespace NovaBrowser;

public partial class App : System.Windows.Application
{
    private SqliteDatabase? _database;
    private BrowserShellViewModel? _shell;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            AppPaths.EnsureDirectories();
            _database = new SqliteDatabase();
            await _database.InitializeAsync();

            var settingsService = new SettingsService(_database);
            var initialSettings = await settingsService.GetAsync();
            var themeService = new ThemeService(this);
            themeService.ApplyTheme(initialSettings.Theme);

            var downloadService = new DownloadService();
            var browserEngine = new BrowserEngineService(AppPaths.WebViewUserDataDirectory, Dispatcher);
            _shell = new BrowserShellViewModel(
                settingsService,
                new HistoryService(_database),
                new BookmarkService(_database),
                new SessionService(_database),
                browserEngine,
                new AddressResolver(),
                themeService,
                new MemoryManager(Dispatcher),
                downloadService);

            var window = new MainWindow(_shell);
            MainWindow = window;
            window.Show();
            await _shell.InitializeAsync();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"NovaBrowser nie mógł zostać uruchomiony.\n\n{exception.Message}",
                "Błąd uruchamiania NovaBrowser",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shell?.Dispose();
        _database?.Dispose();
        base.OnExit(e);
    }
}
