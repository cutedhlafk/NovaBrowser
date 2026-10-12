using Microsoft.Data.Sqlite;
using NovaBrowser.Models;

namespace NovaBrowser.Services;

/// <summary>
/// Manages the user's URL-unique bookmark collection.
/// </summary>
public sealed class BookmarkService
{
    private const int MaximumQueryLimit = 5_000;
    private readonly SqliteDatabase _database;

    public BookmarkService(SqliteDatabase? database = null)
    {
        _database = database ?? new SqliteDatabase();
    }

    /// <summary>
    /// Adds a bookmark, or refreshes title/favicon metadata when the URL was already bookmarked.
    /// </summary>
    public async Task<BookmarkItem> AddAsync(
        string url,
        string? title = null,
        string? faviconUrl = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedUrl = RequireUrl(url);
        var normalizedTitle = title?.Trim() ?? string.Empty;
        var normalizedFaviconUrl = NormalizeOptionalText(faviconUrl);

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO bookmarks (url, title, favicon_url, created_utc)
                VALUES ($url, $title, $faviconUrl, $createdUtc)
                ON CONFLICT(url) DO UPDATE SET
                    title = CASE
                        WHEN excluded.title = '' THEN bookmarks.title
                        ELSE excluded.title
                    END,
                    favicon_url = COALESCE(excluded.favicon_url, bookmarks.favicon_url);
                """;
            command.Parameters.Add("$url", SqliteType.Text).Value = normalizedUrl;
            command.Parameters.Add("$title", SqliteType.Text).Value = normalizedTitle;
            command.Parameters.Add("$faviconUrl", SqliteType.Text).Value = normalizedFaviconUrl ?? (object)DBNull.Value;
            command.Parameters.Add("$createdUtc", SqliteType.Integer).Value = SqliteDatabase.UtcNowMilliseconds();
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var bookmark = await GetByUrlCoreAsync(connection, normalizedUrl, cancellationToken).ConfigureAwait(false);
        return bookmark ?? throw new InvalidOperationException("Nie udało się zapisać zakładki.");
    }

    public async Task<IReadOnlyList<BookmarkItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var bookmarks = new List<BookmarkItem>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, url, title, favicon_url, created_utc
            FROM bookmarks
            ORDER BY created_utc DESC, id DESC;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            bookmarks.Add(ReadBookmark(reader));
        }

        return bookmarks;
    }

    public async Task<IReadOnlyList<BookmarkItem>> SearchAsync(
        string query,
        int limit = 250,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (limit <= 0)
        {
            return Array.Empty<BookmarkItem>();
        }

        var bookmarks = new List<BookmarkItem>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, url, title, favicon_url, created_utc
            FROM bookmarks
            WHERE url LIKE $query ESCAPE '\' OR title LIKE $query ESCAPE '\'
            ORDER BY created_utc DESC, id DESC
            LIMIT $limit;
            """;
        command.Parameters.Add("$query", SqliteType.Text).Value = $"%{EscapeLikePattern(query.Trim())}%";
        command.Parameters.Add("$limit", SqliteType.Integer).Value = Math.Min(limit, MaximumQueryLimit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            bookmarks.Add(ReadBookmark(reader));
        }

        return bookmarks;
    }

    public async Task<BookmarkItem?> GetByUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        var normalizedUrl = RequireUrl(url);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await GetByUrlCoreAsync(connection, normalizedUrl, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsBookmarkedAsync(string url, CancellationToken cancellationToken = default)
    {
        var normalizedUrl = RequireUrl(url);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM bookmarks WHERE url = $url);";
        command.Parameters.Add("$url", SqliteType.Text).Value = normalizedUrl;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    public async Task<bool> RemoveAsync(long id, CancellationToken cancellationToken = default)
    {
        if (id <= 0)
        {
            return false;
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM bookmarks WHERE id = $id;";
        command.Parameters.Add("$id", SqliteType.Integer).Value = id;
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<bool> RemoveByUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        var normalizedUrl = RequireUrl(url);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM bookmarks WHERE url = $url;";
        command.Parameters.Add("$url", SqliteType.Text).Value = normalizedUrl;
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM bookmarks;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<BookmarkItem?> GetByUrlCoreAsync(
        SqliteConnection connection,
        string normalizedUrl,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, url, title, favicon_url, created_utc
            FROM bookmarks
            WHERE url = $url;
            """;
        command.Parameters.Add("$url", SqliteType.Text).Value = normalizedUrl;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadBookmark(reader)
            : null;
    }

    private static BookmarkItem ReadBookmark(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
        reader.IsDBNull(3) ? null : reader.GetString(3));

    private static string RequireUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return url.Trim();
    }

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string EscapeLikePattern(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
