using NovaBrowser.Models;

namespace NovaBrowser.Services;

/// <summary>
/// Describes how text entered in the address bar was interpreted.
/// </summary>
public enum AddressResolutionKind
{
    Empty,
    Address,
    Search
}

/// <summary>
/// A safe navigation destination produced by <see cref="AddressResolver"/>.
/// <see cref="NavigationUri"/> is null only for an empty address-bar value.
/// </summary>
public sealed record AddressResolution(
    AddressResolutionKind Kind,
    Uri? NavigationUri,
    string Input)
{
    public bool HasNavigation => NavigationUri is not null;

    public bool IsSearchQuery => Kind == AddressResolutionKind.Search;
}

/// <summary>
/// Converts address-bar text into either an HTTP(S) URI or a search URI.
/// Schemes such as <c>file:</c>, <c>javascript:</c>, and <c>data:</c> are never
/// returned as navigation targets.
/// </summary>
public sealed class AddressResolver
{
    private const string GoogleSearchPrefix = "https://www.google.com/search?q=";
    private const string BingSearchPrefix = "https://www.bing.com/search?q=";
    private const string DuckDuckGoSearchPrefix = "https://duckduckgo.com/?q=";
    private const string StartpageSearchPrefix = "https://www.startpage.com/sp/search?query=";

    /// <summary>
    /// Resolves a value from the address bar. Empty text intentionally produces no
    /// navigation; all non-HTTP(S) text becomes a search query.
    /// </summary>
    public AddressResolution Resolve(string? input, SearchEngineKind searchEngine)
    {
        var value = input?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return new AddressResolution(AddressResolutionKind.Empty, null, string.Empty);
        }

        if (TryResolveHttpAddress(value, out var address))
        {
            return new AddressResolution(AddressResolutionKind.Address, address, value);
        }

        return new AddressResolution(
            AddressResolutionKind.Search,
            CreateSearchUri(value, searchEngine),
            value);
    }

    /// <summary>
    /// Attempts to parse only a navigable HTTP or HTTPS URI. A schemeless host-like
    /// input, such as <c>example.com</c>, is upgraded to HTTPS. No network access is
    /// performed by this method.
    /// </summary>
    public static bool TryResolveHttpAddress(string? input, out Uri? address)
    {
        address = null;

        var value = input?.Trim();
        if (string.IsNullOrWhiteSpace(value) || ContainsWhitespaceOrControlCharacters(value))
        {
            return false;
        }

        if (TryCreateHttpUri(value, out address))
        {
            return true;
        }

        // A value that already uses :// had an explicit scheme. Since it was not
        // HTTP(S), never reinterpret it as a host name (for example,
        // custom.scheme://host remains a search query).
        if (value.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        var hostCandidate = value.StartsWith("//", StringComparison.Ordinal)
            ? value[2..]
            : value;

        if (LooksLikeWebHost(hostCandidate) &&
            TryCreateHttpUri($"https://{hostCandidate}", out address))
        {
            return true;
        }

        // Any unrecognised scheme (javascript:, file:, data:, etc.) reaches this
        // path and is converted into a search by Resolve rather than passed to
        // WebView2 as a navigation target.
        return false;
    }

    /// <summary>
    /// Builds a URL for the selected search provider, escaping the complete user
    /// query as a query-string value.
    /// </summary>
    public static Uri CreateSearchUri(string query, SearchEngineKind searchEngine)
    {
        ArgumentNullException.ThrowIfNull(query);

        var prefix = searchEngine switch
        {
            SearchEngineKind.Google => GoogleSearchPrefix,
            SearchEngineKind.Bing => BingSearchPrefix,
            SearchEngineKind.Startpage => StartpageSearchPrefix,
            _ => DuckDuckGoSearchPrefix
        };

        return new Uri(prefix + Uri.EscapeDataString(query), UriKind.Absolute);
    }

    private static bool TryCreateHttpUri(string value, out Uri? address)
    {
        address = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            (!parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo))
        {
            return false;
        }

        address = parsed;
        return true;
    }

    private static bool LooksLikeWebHost(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('@'))
        {
            return false;
        }

        var authorityEnd = value.IndexOfAny(['/', '?', '#']);
        var authority = authorityEnd >= 0 ? value[..authorityEnd] : value;
        if (authority.Length == 0)
        {
            return false;
        }

        if (authority[0] == '[')
        {
            // Bracketed IPv6 may contain colons, and Uri validates the rest later.
            return authority.IndexOf(']') > 1;
        }

        var colonIndex = authority.IndexOf(':');
        var host = colonIndex >= 0 ? authority[..colonIndex] : authority;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var hostType = Uri.CheckHostName(host);
        return hostType is UriHostNameType.IPv4 or UriHostNameType.IPv6 ||
               (hostType == UriHostNameType.Dns && host.Contains('.', StringComparison.Ordinal));
    }

    private static bool ContainsWhitespaceOrControlCharacters(string value)
    {
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                return true;
            }
        }

        return false;
    }
}
