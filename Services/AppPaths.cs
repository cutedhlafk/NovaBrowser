using System.IO;

namespace NovaBrowser.Services;

/// <summary>
/// Resolves NovaBrowser-owned paths without relying on the current working
/// directory. All state therefore remains per-user and survives app updates.
/// </summary>
public static class AppPaths
{
    private const string ApplicationFolderName = "NovaBrowser";

    public static string AppDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ApplicationFolderName);

    public static string DatabasePath => Path.Combine(AppDataDirectory, "novabrowser.db");

    public static string WebViewUserDataDirectory => Path.Combine(AppDataDirectory, "WebView2");

    public static string DefaultDownloadsDirectory
    {
        get
        {
            var profileDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(profileDirectory, "Downloads");
        }
    }

    /// <summary>
    /// Creates only the directories owned by the browser. The downloads folder is
    /// intentionally not created here: it may be managed by Windows or redirected
    /// by the user.
    /// </summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(WebViewUserDataDirectory);
    }
}
