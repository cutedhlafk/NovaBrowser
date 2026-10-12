using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using NovaBrowser.ViewModels;

namespace NovaBrowser.Services;

/// <summary>
/// Owns WebView2 download event subscriptions and exposes a small, bindable list
/// for the downloads panel. Register every initialized <see cref="CoreWebView2"/>
/// with <see cref="Attach"/> and unregister it when its tab is torn down.
/// </summary>
public sealed class DownloadService : IDisposable
{
    private const int MaxFileNameLength = 180;

    private readonly Dictionary<CoreWebView2, WebViewSubscription> _webViewSubscriptions = [];
    private readonly Dictionary<CoreWebView2DownloadOperation, ActiveDownload> _activeDownloads = [];
    private readonly HashSet<string> _reservedPaths = new(StringComparer.OrdinalIgnoreCase);
    private Func<string?>? _downloadFolderProvider;
    private bool _isDisposed;

    /// <summary>
    /// Creates the service. The provider is read at the start of each download so
    /// changes made in Settings are used immediately.
    /// </summary>
    public DownloadService(Func<string?>? downloadFolderProvider = null)
    {
        _downloadFolderProvider = downloadFolderProvider;
        Downloads = [];
    }

    /// <summary>Items intended for binding to the Downloads side panel.</summary>
    public ObservableCollection<DownloadItemViewModel> Downloads { get; }

    /// <summary>
    /// Replaces the function used to obtain the configured download directory.
    /// A null, blank, invalid, or inaccessible setting falls back to the Windows
    /// Downloads directory when a download begins.
    /// </summary>
    public void SetDownloadFolderProvider(Func<string?>? downloadFolderProvider) =>
        _downloadFolderProvider = downloadFolderProvider;

    /// <summary>
    /// Starts observing downloads for one initialized WebView2 instance. Calling it
    /// more than once for the same instance is safe; the optional provider is updated.
    /// </summary>
    public void Attach(CoreWebView2 coreWebView, Func<string?>? downloadFolderProvider = null)
    {
        ArgumentNullException.ThrowIfNull(coreWebView);
        ThrowIfDisposed();

        if (_webViewSubscriptions.TryGetValue(coreWebView, out var existing))
        {
            existing.DownloadFolderProvider = downloadFolderProvider ?? existing.DownloadFolderProvider;
            return;
        }

        EventHandler<CoreWebView2DownloadStartingEventArgs> handler = OnDownloadStarting;
        var subscription = new WebViewSubscription(handler, downloadFolderProvider);
        _webViewSubscriptions.Add(coreWebView, subscription);
        coreWebView.DownloadStarting += handler;
    }

    /// <summary>Compatibility-friendly alias for <see cref="Attach"/>.</summary>
    public void AttachToWebView(CoreWebView2 coreWebView, Func<string?>? downloadFolderProvider = null) =>
        Attach(coreWebView, downloadFolderProvider);

    /// <summary>
    /// Removes the DownloadStarting handler from a tab. Existing downloads remain
    /// observed so that closing a tab does not make their UI status stale.
    /// </summary>
    public void Detach(CoreWebView2 coreWebView)
    {
        ArgumentNullException.ThrowIfNull(coreWebView);

        if (!_webViewSubscriptions.Remove(coreWebView, out var subscription))
        {
            return;
        }

        try
        {
            coreWebView.DownloadStarting -= subscription.DownloadStartingHandler;
        }
        catch (Exception exception) when (exception is ObjectDisposedException or COMException)
        {
            // A WebView may already be disposed while its containing tab closes.
        }
    }

    /// <summary>Compatibility-friendly alias for <see cref="Detach"/>.</summary>
    public void DetachFromWebView(CoreWebView2 coreWebView) => Detach(coreWebView);

    /// <summary>
    /// Resolves and creates the configured download directory. It is also useful to
    /// callers that need to show that directory in Explorer.
    /// </summary>
    public string GetDownloadFolder()
    {
        var configuredFolder = ReadConfiguredFolder(_downloadFolderProvider);
        return EnsureDownloadFolder(configuredFolder);
    }

    /// <summary>Opens the currently configured download directory in Windows Explorer.</summary>
    public bool OpenDownloadsFolder()
    {
        return OpenDownloadsFolder(_downloadFolderProvider);
    }

    /// <summary>
    /// Opens a caller-supplied download folder in Windows Explorer. This overload is
    /// useful while Settings are being edited but have not yet been saved.
    /// </summary>
    public bool OpenDownloadsFolder(Func<string?>? downloadFolderProvider)
    {
        try
        {
            var folder = EnsureDownloadFolder(ReadConfiguredFolder(downloadFolderProvider));
            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Removes completed, cancelled, and failed rows from the UI collection.</summary>
    public void ClearFinished()
    {
        for (var index = Downloads.Count - 1; index >= 0; index--)
        {
            var item = Downloads[index];
            if (!item.IsFinished)
            {
                continue;
            }

            Downloads.RemoveAt(index);
            item.Dispose();
        }
    }

    /// <summary>Compatibility-friendly alias for <see cref="ClearFinished"/>.</summary>
    public void ClearFinishedDownloads() => ClearFinished();

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs eventArgs)
    {
        if (_isDisposed)
        {
            eventArgs.Cancel = true;
            eventArgs.Handled = true;
            return;
        }

        var sourceWebView = sender as CoreWebView2;
        var folderProvider = sourceWebView is not null && _webViewSubscriptions.TryGetValue(sourceWebView, out var subscription)
            ? subscription.DownloadFolderProvider ?? _downloadFolderProvider
            : _downloadFolderProvider;

        var operation = eventArgs.DownloadOperation;
        var sourceUrl = operation.Uri;
        var suggestedFileName = GetSafeFileName(eventArgs.ResultFilePath, sourceUrl);
        string? destinationPath = null;

        try
        {
            var folder = EnsureDownloadFolder(ReadConfiguredFolder(folderProvider));
            destinationPath = ReserveUniquePath(folder, suggestedFileName);
            eventArgs.ResultFilePath = destinationPath;
            eventArgs.Handled = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (destinationPath is not null)
            {
                _reservedPaths.Remove(destinationPath);
            }

            eventArgs.Cancel = true;
            eventArgs.Handled = true;

            var failedItem = new DownloadItemViewModel(suggestedFileName, string.Empty, sourceUrl);
            failedItem.MarkFailed("Nie można przygotować folderu pobierania.");
            Downloads.Insert(0, failedItem);
            return;
        }

        // The assignment is completed before setting WebView2's path; reaching here
        // therefore guarantees a non-null, reserved path.
        var resolvedDestinationPath = destinationPath!;
        var item = new DownloadItemViewModel(Path.GetFileName(resolvedDestinationPath), resolvedDestinationPath, sourceUrl);
        var activeDownload = new ActiveDownload(operation, item, resolvedDestinationPath);
        activeDownload.BytesReceivedChangedHandler = (_, _) => UpdateProgress(activeDownload);
        activeDownload.StateChangedHandler = (_, _) => UpdateState(activeDownload);

        _activeDownloads.Add(operation, activeDownload);
        operation.BytesReceivedChanged += activeDownload.BytesReceivedChangedHandler;
        operation.StateChanged += activeDownload.StateChangedHandler;
        item.SetCancelAction(() => Cancel(activeDownload));
        item.UpdateProgress(operation.BytesReceived, GetTotalBytes(operation));
        Downloads.Insert(0, item);

        // An operation can finish before event handlers are subscribed on very fast,
        // local downloads. Reading State once closes that small race.
        UpdateState(activeDownload);
    }

    private void UpdateProgress(ActiveDownload activeDownload)
    {
        if (_isDisposed || !_activeDownloads.ContainsKey(activeDownload.Operation))
        {
            return;
        }

        activeDownload.Item.UpdateProgress(
            activeDownload.Operation.BytesReceived,
            GetTotalBytes(activeDownload.Operation));
    }

    private void UpdateState(ActiveDownload activeDownload)
    {
        if (_isDisposed || !_activeDownloads.ContainsKey(activeDownload.Operation))
        {
            return;
        }

        var operation = activeDownload.Operation;
        switch (operation.State)
        {
            case CoreWebView2DownloadState.Completed:
                activeDownload.Item.MarkCompleted(operation.BytesReceived, GetTotalBytes(operation));
                Complete(activeDownload);
                break;

            case CoreWebView2DownloadState.Interrupted:
                if (activeDownload.Item.CancellationRequested ||
                    string.Equals(operation.InterruptReason.ToString(), "UserCanceled", StringComparison.OrdinalIgnoreCase))
                {
                    activeDownload.Item.MarkCancelled(operation.BytesReceived, GetTotalBytes(operation));
                }
                else
                {
                    activeDownload.Item.MarkFailed(
                        GetInterruptionMessage(operation.InterruptReason),
                        operation.BytesReceived,
                        GetTotalBytes(operation));
                }

                Complete(activeDownload);
                break;

            default:
                UpdateProgress(activeDownload);
                break;
        }
    }

    private void Cancel(ActiveDownload activeDownload)
    {
        if (_isDisposed || !_activeDownloads.ContainsKey(activeDownload.Operation))
        {
            return;
        }

        activeDownload.Operation.Cancel();
    }

    private void Complete(ActiveDownload activeDownload)
    {
        if (!_activeDownloads.Remove(activeDownload.Operation))
        {
            return;
        }

        DetachOperationHandlers(activeDownload);
        _reservedPaths.Remove(activeDownload.DestinationPath);
    }

    private string ReserveUniquePath(string folder, string suggestedFileName)
    {
        var extension = Path.GetExtension(suggestedFileName);
        var baseName = Path.GetFileNameWithoutExtension(suggestedFileName);
        baseName = string.IsNullOrWhiteSpace(baseName) ? "pobieranie" : baseName;
        extension = extension.Length > 32 ? string.Empty : extension;

        for (var suffix = 0; suffix < int.MaxValue; suffix++)
        {
            var suffixText = suffix == 0 ? string.Empty : $" ({suffix})";
            var maxBaseLength = Math.Max(1, MaxFileNameLength - extension.Length - suffixText.Length);
            var truncatedBaseName = baseName.Length > maxBaseLength ? baseName[..maxBaseLength] : baseName;
            var fileName = $"{truncatedBaseName}{suffixText}{extension}";
            var candidate = Path.GetFullPath(Path.Combine(folder, fileName));

            // fileName is sanitized, but this check also defends the folder boundary
            // if an implementation changes the naming logic in the future.
            if (!IsChildPath(folder, candidate))
            {
                throw new IOException("Nieprawidłowa ścieżka docelowa pobierania.");
            }

            if (File.Exists(candidate) || _reservedPaths.Contains(candidate))
            {
                continue;
            }

            _reservedPaths.Add(candidate);
            return candidate;
        }

        throw new IOException("Nie można utworzyć unikalnej nazwy pliku pobierania.");
    }

    private static string EnsureDownloadFolder(string? configuredFolder)
    {
        var fallback = AppPaths.DefaultDownloadsDirectory;
        var requestedFolder = string.IsNullOrWhiteSpace(configuredFolder) ? fallback : configuredFolder.Trim();

        try
        {
            var fullPath = Path.GetFullPath(requestedFolder);
            Directory.CreateDirectory(fullPath);
            return fullPath;
        }
        catch (Exception) when (!string.Equals(requestedFolder, fallback, StringComparison.OrdinalIgnoreCase))
        {
            var fallbackPath = Path.GetFullPath(fallback);
            Directory.CreateDirectory(fallbackPath);
            return fallbackPath;
        }
    }

    private static string? ReadConfiguredFolder(Func<string?>? provider)
    {
        try
        {
            return provider?.Invoke();
        }
        catch
        {
            return null;
        }
    }

    private static string GetSafeFileName(string? suggestedPath, string? sourceUrl)
    {
        string? fileName = null;
        try
        {
            fileName = Path.GetFileName(suggestedPath);
        }
        catch (ArgumentException)
        {
            // Fall back to the final path segment from the source URL below.
        }

        if (string.IsNullOrWhiteSpace(fileName) && Uri.TryCreate(sourceUrl, UriKind.Absolute, out var sourceUri))
        {
            fileName = Path.GetFileName(sourceUri.LocalPath);
        }

        fileName = string.IsNullOrWhiteSpace(fileName) ? "pobieranie" : fileName;
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName.Select(character =>
            character is < ' ' || invalidCharacters.Contains(character) ? '_' : character).ToArray())
            .Trim(' ', '.');

        if (string.IsNullOrWhiteSpace(sanitized) || sanitized is "." or "..")
        {
            return "pobieranie";
        }

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(sanitized);
        if (IsWindowsReservedFileName(nameWithoutExtension))
        {
            sanitized = $"_{sanitized}";
        }

        return sanitized;
    }

    private static bool IsWindowsReservedFileName(string fileName)
    {
        if (fileName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fileName.Length == 4 &&
               (fileName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               fileName[3] is >= '1' and <= '9';
    }

    private static bool IsChildPath(string folder, string candidate)
    {
        var folderWithSeparator = Path.EndsInDirectorySeparator(folder)
            ? folder
            : folder + Path.DirectorySeparatorChar;

        return candidate.StartsWith(folderWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetInterruptionMessage(CoreWebView2DownloadInterruptReason reason)
    {
        return reason.ToString() switch
        {
            "FileFailed" => "Nie można zapisać pliku.",
            "FileAccessDenied" => "Brak uprawnień do zapisu pliku.",
            "FileNoSpace" => "Brak miejsca na dysku.",
            "NetworkDisconnected" => "Połączenie sieciowe zostało przerwane.",
            "NetworkServerDown" => "Serwer pobierania jest niedostępny.",
            "NetworkInvalidRequest" => "Serwer odrzucił pobieranie.",
            _ => $"Pobieranie przerwano ({reason})."
        };
    }

    private static void DetachOperationHandlers(ActiveDownload activeDownload)
    {
        try
        {
            activeDownload.Operation.BytesReceivedChanged -= activeDownload.BytesReceivedChangedHandler;
            activeDownload.Operation.StateChanged -= activeDownload.StateChangedHandler;
        }
        catch (Exception exception) when (exception is ObjectDisposedException or COMException)
        {
            // A tab can be torn down by WebView2 before its download operation has
            // delivered a terminal state notification.
        }
    }

    private static long? GetTotalBytes(CoreWebView2DownloadOperation operation)
    {
        var totalBytes = operation.TotalBytesToReceive;
        if (totalBytes is null)
        {
            return null;
        }

        // The WebView2 contract uses UInt64 whereas the rest of the application
        // represents file sizes as signed values. An impossible-in-practice value
        // beyond Int64 is capped rather than allowed to wrap into a negative number.
        return totalBytes.Value > (ulong)long.MaxValue ? long.MaxValue : (long)totalBytes.Value;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        foreach (var (webView, subscription) in _webViewSubscriptions)
        {
            try
            {
                webView.DownloadStarting -= subscription.DownloadStartingHandler;
            }
            catch (Exception exception) when (exception is ObjectDisposedException or COMException)
            {
                // The owner may dispose WebView2 before disposing this service.
            }
        }

        _webViewSubscriptions.Clear();

        foreach (var activeDownload in _activeDownloads.Values)
        {
            DetachOperationHandlers(activeDownload);
            activeDownload.Item.Dispose();
        }

        _activeDownloads.Clear();
        _reservedPaths.Clear();

        foreach (var item in Downloads)
        {
            item.Dispose();
        }

        Downloads.Clear();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    private sealed class WebViewSubscription(
        EventHandler<CoreWebView2DownloadStartingEventArgs> downloadStartingHandler,
        Func<string?>? downloadFolderProvider)
    {
        public EventHandler<CoreWebView2DownloadStartingEventArgs> DownloadStartingHandler { get; } = downloadStartingHandler;

        public Func<string?>? DownloadFolderProvider { get; set; } = downloadFolderProvider;
    }

    private sealed class ActiveDownload(CoreWebView2DownloadOperation operation, DownloadItemViewModel item, string destinationPath)
    {
        public CoreWebView2DownloadOperation Operation { get; } = operation;

        public DownloadItemViewModel Item { get; } = item;

        public string DestinationPath { get; } = destinationPath;

        public EventHandler<object> BytesReceivedChangedHandler { get; set; } = null!;

        public EventHandler<object> StateChangedHandler { get; set; } = null!;
    }
}
