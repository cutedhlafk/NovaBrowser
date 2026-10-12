using System.IO;
using System.Net.Http;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace NovaBrowser.Services;

/// <summary>
/// Domain-based ad/tracker filtering. Supported EasyList subset: ||hostname^
/// without resource-type modifiers, path rules, cosmetic rules or exceptions.
/// Unsupported rules are ignored deliberately to avoid misapplying their semantics.
/// </summary>
public sealed class NovaBlockService : IDisposable
{
    private const int MaximumListBytes = 8 * 1024 * 1024;
    private const int MaximumRules = 160_000;
    private static readonly Uri[] Sources =
    [
        new("https://easylist.to/easylist/easylist.txt"),
        new("https://easylist.to/easylist/easyprivacy.txt")
    ];
    private static readonly HashSet<string> BuiltInHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "doubleclick.net", "googlesyndication.com", "googleadservices.com",
        "google-analytics.com", "googletagmanager.com", "adnxs.com",
        "criteo.com", "criteo.net", "taboola.com", "outbrain.com",
        "scorecardresearch.com", "quantserve.com", "hotjar.com",
        "clarity.ms", "amazon-adsystem.com", "rubiconproject.com",
        "pubmatic.com", "openx.net", "casalemedia.com", "adsrvr.org"
    };

    private readonly Dictionary<CoreWebView2, EventHandler<CoreWebView2WebResourceRequestedEventArgs>> _subscriptions = [];
    private readonly HashSet<string> _exclusions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hosts = new(BuiltInHosts, StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly string _cachedRulesPath;
    private DateTimeOffset _lastCounterEvent = DateTimeOffset.MinValue;
    private bool _disposed;

    public NovaBlockService(string cachedRulesPath)
    {
        _cachedRulesPath = cachedRulesPath;
        if (File.Exists(_cachedRulesPath))
        {
            try { AddRules(File.ReadAllText(_cachedRulesPath, Encoding.UTF8), _hosts); }
            catch (IOException) { /* Keep built-in protection when cache cannot be read. */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    public bool Enabled { get; private set; } = true;
    public long BlockedRequests { get; private set; }
    public int FilterCount => _hosts.Count;
    public string ExcludedSites => string.Join(";", _exclusions.OrderBy(x => x));
    public event Action? StateChanged;

    public void Configure(bool enabled, string? excludedSites)
    {
        Enabled = enabled;
        _exclusions.Clear();
        foreach (var raw in (excludedSites ?? string.Empty).Split(';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ValidHost(raw)) _exclusions.Add(raw.ToLowerInvariant());
        }
        StateChanged?.Invoke();
    }

    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        StateChanged?.Invoke();
    }

    public bool IsExcluded(string host) => _exclusions.Contains(host);

    public void ToggleExcludedHost(string host)
    {
        if (!ValidHost(host)) return;
        if (!_exclusions.Remove(host)) _exclusions.Add(host.ToLowerInvariant());
        StateChanged?.Invoke();
    }

    public void Attach(CoreWebView2 webView)
    {
        if (_disposed || _subscriptions.ContainsKey(webView)) return;
        EventHandler<CoreWebView2WebResourceRequestedEventArgs> handler = (_, args) =>
        {
            if (!Enabled || args.ResourceContext == CoreWebView2WebResourceContext.Document ||
                !Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var request) ||
                request.Scheme is not ("http" or "https")) return;

            // Exempt the current first-party site; NEVER use the blocked request host
            // as the exemption's identity.
            if (Uri.TryCreate(webView.Source, UriKind.Absolute, out var firstParty)
                && _exclusions.Contains(firstParty.Host)) return;

            if (!IsBlocked(request.Host)) return;

            args.Response = webView.Environment.CreateWebResourceResponse(
                null, 403, "Blocked by NovaBlock",
                "Content-Type: text/plain\r\nCache-Control: no-store");
            BlockedRequests++;
            var now = DateTimeOffset.UtcNow;
            if (now - _lastCounterEvent > TimeSpan.FromSeconds(1))
            {
                _lastCounterEvent = now;
                StateChanged?.Invoke();
            }
        };
        webView.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        webView.WebResourceRequested += handler;
        _subscriptions.Add(webView, handler);
    }

    public void Detach(CoreWebView2 webView)
    {
        if (!_subscriptions.Remove(webView, out var handler)) return;
        try
        {
            webView.WebResourceRequested -= handler;
            webView.RemoveWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or System.Runtime.InteropServices.COMException) { }
    }

    public async Task<int> UpdateFilterListsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var lists = new List<string>();
        // Only fixed, HTTPS filter sources are contacted. User input is not a URL.
        foreach (var source in Sources)
        {
            using var response = await _client.GetAsync(source,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumListBytes)
                throw new InvalidDataException("Lista filtrów przekracza limit rozmiaru.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[32 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + read > MaximumListBytes)
                    throw new InvalidDataException("Lista filtrów przekracza limit rozmiaru.");
                memory.Write(buffer, 0, read);
            }
            lists.Add(Encoding.UTF8.GetString(memory.ToArray()));
        }

        var combined = string.Join("\n", lists);
        var parsed = new HashSet<string>(BuiltInHosts, StringComparer.OrdinalIgnoreCase);
        AddRules(combined, parsed);
        if (parsed.Count <= BuiltInHosts.Count)
            throw new InvalidDataException("Nie udało się wczytać reguł domenowych z list.");

        Directory.CreateDirectory(Path.GetDirectoryName(_cachedRulesPath)!);
        var tmp = _cachedRulesPath + ".tmp";
        await File.WriteAllTextAsync(tmp, combined, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        File.Move(tmp, _cachedRulesPath, overwrite: true);

        // Assign on the WPF dispatcher through the caller's captured context.
        // Concurrent requests read the set by reference and never see mutations.
        _hosts.Clear();
        _hosts.UnionWith(parsed);
        StateChanged?.Invoke();
        return _hosts.Count;
    }

    private bool IsBlocked(string host)
    {
        for (var cursor = host; ; )
        {
            if (_hosts.Contains(cursor)) return true;
            var index = cursor.IndexOf('.');
            if (index < 0) return false;
            cursor = cursor[(index + 1)..];
        }
    }

    private static void AddRules(string content, HashSet<string> targets)
    {
        using var reader = new StringReader(content);
        while (targets.Count < MaximumRules && reader.ReadLine() is { } line)
        {
            var candidate = line.Trim();
            if (!candidate.StartsWith("||", StringComparison.Ordinal) ||
                candidate.StartsWith("@@", StringComparison.Ordinal) ||
                candidate.Contains('$') || candidate.Contains('*') ||
                candidate.Contains('/') || candidate.Contains('#')) continue;
            var end = candidate.IndexOf('^', 2);
            if (end != candidate.Length - 1 || end < 3) continue;
            var host = candidate[2..end];
            if (ValidHost(host)) targets.Add(host.ToLowerInvariant());
        }
    }

    private static bool ValidHost(string host) =>
        host.Length is > 2 and <= 253 &&
        Uri.CheckHostName(host) == UriHostNameType.Dns &&
        host.Contains('.') && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.');

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var webView in _subscriptions.Keys.ToArray()) Detach(webView);
        _client.Dispose();
        _disposed = true;
        StateChanged = null;
    }
}
