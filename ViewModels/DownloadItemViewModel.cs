using System.Windows.Input;
using NovaBrowser.Infrastructure;

namespace NovaBrowser.ViewModels;

/// <summary>
/// State exposed to the downloads panel for one WebView2 download.
/// The view model does not own a WebView2 object; the <see cref="NovaBrowser.Services.DownloadService"/>
/// supplies the cancellation action and keeps the native event subscriptions alive.
/// </summary>
public sealed class DownloadItemViewModel : ObservableObject, IDisposable
{
    private readonly RelayCommand _cancelCommand;
    private Action? _cancelAction;
    private string _fileName;
    private string _destinationPath;
    private string? _sourceUrl;
    private long _bytesReceived;
    private long? _totalBytes;
    private DownloadItemState _state;
    private string? _errorMessage;
    private bool _cancellationRequested;
    private bool _isDisposed;

    /// <summary>
    /// Creates a display item. DownloadService normally creates instances and connects
    /// the cancel action after it has subscribed to the WebView2 operation.
    /// </summary>
    public DownloadItemViewModel(string fileName, string destinationPath, string? sourceUrl = null)
    {
        Id = Guid.NewGuid();
        _fileName = string.IsNullOrWhiteSpace(fileName) ? "pobieranie" : fileName;
        _destinationPath = destinationPath ?? string.Empty;
        _sourceUrl = sourceUrl;
        _state = DownloadItemState.InProgress;
        StartedUtc = DateTimeOffset.UtcNow;
        _cancelCommand = new RelayCommand(Cancel, () => CanCancel);
    }

    public Guid Id { get; }

    public DateTimeOffset StartedUtc { get; }

    public string FileName
    {
        get => _fileName;
        internal set => SetProperty(ref _fileName, string.IsNullOrWhiteSpace(value) ? "pobieranie" : value);
    }

    /// <summary>Absolute path selected for the downloaded file.</summary>
    public string DestinationPath
    {
        get => _destinationPath;
        internal set => SetProperty(ref _destinationPath, value ?? string.Empty);
    }

    public string? SourceUrl
    {
        get => _sourceUrl;
        internal set => SetProperty(ref _sourceUrl, value);
    }

    public long BytesReceived
    {
        get => _bytesReceived;
        private set => SetProperty(ref _bytesReceived, Math.Max(0, value));
    }

    /// <summary>
    /// Total size supplied by the server. It remains <c>null</c> when the server
    /// does not send Content-Length.
    /// </summary>
    public long? TotalBytes
    {
        get => _totalBytes;
        private set => SetProperty(ref _totalBytes, value is > 0 ? value : null);
    }

    public DownloadItemState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsFinished));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(StateText));
                _cancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    /// <summary>True after the user pressed cancel and before WebView2 confirms it.</summary>
    public bool CancellationRequested
    {
        get => _cancellationRequested;
        private set
        {
            if (SetProperty(ref _cancellationRequested, value))
            {
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(StateText));
                _cancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasKnownProgress => TotalBytes is > 0;

    /// <summary>Progress suitable for a WPF ProgressBar (0 through 100).</summary>
    public double ProgressPercent => HasKnownProgress
        ? Math.Min(100d, BytesReceived * 100d / TotalBytes!.Value)
        : 0d;

    public string ProgressText => HasKnownProgress
        ? $"{FormatBytes(BytesReceived)} z {FormatBytes(TotalBytes!.Value)}"
        : BytesReceived > 0 ? FormatBytes(BytesReceived) : "Przygotowywanie…";

    public bool IsFinished => State is not DownloadItemState.InProgress;

    public bool CanCancel => !_isDisposed && State is DownloadItemState.InProgress && !CancellationRequested && _cancelAction is not null;

    public string StateText => State switch
    {
        DownloadItemState.InProgress when CancellationRequested => "Anulowanie…",
        DownloadItemState.InProgress => "Pobieranie",
        DownloadItemState.Completed => "Ukończono",
        DownloadItemState.Cancelled => "Anulowano",
        DownloadItemState.Failed when !string.IsNullOrWhiteSpace(ErrorMessage) => $"Nie udało się: {ErrorMessage}",
        DownloadItemState.Failed => "Pobieranie nie powiodło się",
        _ => string.Empty
    };

    public ICommand CancelCommand => _cancelCommand;

    /// <summary>Requests cancellation through the service that owns the WebView2 operation.</summary>
    public void Cancel()
    {
        if (!CanCancel)
        {
            return;
        }

        CancellationRequested = true;
        try
        {
            _cancelAction?.Invoke();
        }
        catch (Exception exception)
        {
            MarkFailed(string.IsNullOrWhiteSpace(exception.Message)
                ? "Nie można anulować pobierania."
                : exception.Message);
        }
    }

    internal void SetCancelAction(Action? cancelAction)
    {
        _cancelAction = cancelAction;
        OnPropertyChanged(nameof(CanCancel));
        _cancelCommand.RaiseCanExecuteChanged();
    }

    internal void UpdateProgress(long bytesReceived, long? totalBytes)
    {
        BytesReceived = bytesReceived;
        TotalBytes = totalBytes;
        OnPropertyChanged(nameof(HasKnownProgress));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText));
    }

    internal void MarkCompleted(long bytesReceived, long? totalBytes)
    {
        UpdateProgress(bytesReceived, totalBytes);
        CancellationRequested = false;
        ErrorMessage = null;
        State = DownloadItemState.Completed;
        SetCancelAction(null);
    }

    internal void MarkCancelled(long bytesReceived, long? totalBytes)
    {
        UpdateProgress(bytesReceived, totalBytes);
        ErrorMessage = null;
        State = DownloadItemState.Cancelled;
        SetCancelAction(null);
    }

    internal void MarkFailed(string? message, long bytesReceived = 0, long? totalBytes = null)
    {
        UpdateProgress(bytesReceived, totalBytes);
        ErrorMessage = string.IsNullOrWhiteSpace(message) ? null : message;
        State = DownloadItemState.Failed;
        SetCancelAction(null);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _cancelAction = null;
        OnPropertyChanged(nameof(CanCancel));
        _cancelCommand.RaiseCanExecuteChanged();
    }

    private static string FormatBytes(long bytes)
    {
        const double kilobyte = 1024d;
        const double megabyte = kilobyte * 1024d;
        const double gigabyte = megabyte * 1024d;

        return bytes switch
        {
            < 1024 => $"{bytes:N0} B",
            _ when bytes < megabyte => $"{bytes / kilobyte:N1} KB",
            _ when bytes < gigabyte => $"{bytes / megabyte:N1} MB",
            _ => $"{bytes / gigabyte:N2} GB"
        };
    }
}

/// <summary>Lifecycle values used by <see cref="DownloadItemViewModel"/>.</summary>
public enum DownloadItemState
{
    InProgress,
    Completed,
    Cancelled,
    Failed
}
