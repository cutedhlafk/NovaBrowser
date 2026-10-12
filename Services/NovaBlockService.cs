using Microsoft.Web.WebView2.Core;

namespace NovaBrowser.Services;

/// <summary>
/// Lightweight request-level blocker. It intentionally supports a curated set of
/// known advertising/tracking hosts, NOT the full EasyList/uBlock syntax.
/// All CoreWebView2 event subscriptions are removed when a tab closes.
/// </summary>
public sealed class NovaBlockService : IDisposable
{
    private static readonly HashSet<string> BlockedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "doubleclick.net", "googlesyndication.com", "googleadservices.com",
        "adservice.google.com", "google-analytics.com", "analytics.google.com",
        "googletagmanager.com", "adnxs.com", "adsrvr.org",
        "criteo.com", "criteo.net", "taboola.com", "outbrain.com",
        "scorecardresearch.com", "quantserve.com", "quantcount.com",
        "hotjar.com", "hotjar.io", "clarity.ms",
        "amazon-adsystem.com", "rubiconproject.com", "pubmatic.com",
        "openx.net", "casalemedia.com", "bidswitch.net",
        "smartadserver.com", "demdex.net", "adsafeprotected.com"
    };

    private readonly Dictionary<CoreWebView2, EventHandler<CoreWebView2WebResourceRequestedEventArgs>> _handlers = [];
    private readonly HashSet<string> _exceptions = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public bool Enabled { get; private set; } = true;
    public long BlockedRequests { get; private set; }
    public event Action? StateChanged;

    public string ExceptionsSetting => string.Join(";", _exceptions.Order(StringComparer.OrdinalIgnoreCase));

    public void Configure(bool enabled, string? exceptions)
    {
        ThrowIfDisposed();
        _exceptions.Clear();
        if (!string.IsNullOrWhiteSpace(exceptions))
        {
            foreach (var text in exceptions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (IsValidHost(text))
                    _exceptions.Add(text.ToLowerInvariant());
            }
        }

        Enabled = enabled;
        StateChanged?.Invoke();
    }

    public void SetEnabled(bool enabled)
    {
        ThrowIfDisposed();
        Enabled = enabled;
        StateChanged?.Invoke();
    }

    public bool IsExcluded(string? host) => host is not null && _exceptions.Contains(host);

    public bool ToggleException(string host)
    {
        ThrowIfDisposed();
        if (!IsValidHost(host))
            return false;

        host = host.ToLowerInvariant();
        if (!_exceptions.Remove(host))
            _exceptions.Add(host);

        StateChanged?.Invoke();
        return true;
    }

    public void Attach(CoreWebView2 core)
    {
        ThrowIfDisposed();
        if (_handlers.ContainsKey(core))
            return;

        EventHandler<CoreWebView2WebResourceRequestedEventArgs> handler = (_, args) =>
        {
            if (!Enabled || args.ResourceContext == CoreWebView2WebResourceContext.Document ||
                !Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var requested) ||
                (requested.Scheme != Uri.UriSchemeHttp && requested.Scheme != Uri.UriSchemeHttps))
                return;

            // An exception is bound to the current top-level site, not to the
            // third-party request's destination.
            if (Uri.TryCreate(core.Source, UriKind.Absolute, out var topLevel) &&
                _exceptions.Contains(topLevel.Host))
                return;

            if (!IsBlocked(requested.Host))
                return;

            args.Response = core.Environment.CreateWebResourceResponse(
                null, 403, "Blocked by NovaBlock", "Content-Type: text/plain\r\nCache-Control: no-store");
            BlockedRequests++;
            StateChanged?.Invoke();
        };

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += handler;
        _handlers.Add(core, handler);
    }

    public void Detach(CoreWebView2 core)
    {
        if (!_handlers.Remove(core, out var handler))
            return;

        try
        {
            core.WebResourceRequested -= handler;
            core.RemoveWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or System.Runtime.InteropServices.COMException)
        {
            // The renderer was already destroyed.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        foreach (var core in _handlers.Keys.ToArray())
            Detach(core);
        _disposed = true;
        StateChanged = null;
    }

    private static bool IsBlocked(string host)
    {
        var candidate = host;
        while (true)
        {
            if (BlockedHosts.Contains(candidate))
                return true;
            var dot = candidate.IndexOf('.');
            if (dot < 0)
                return false;
            candidate = candidate[(dot + 1)..];
        }
    }

    private static bool IsValidHost(string value) =>
        value.Length is > 0 and <= 253 &&
        Uri.CheckHostName(value) == UriHostNameType.Dns &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.');

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
