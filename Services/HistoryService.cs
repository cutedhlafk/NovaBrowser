using Microsoft.Data.Sqlite;
using NovaBrowser.Models;

namespace NovaBrowser.Services;

/// <summary>
/// Persists completed navigations. Each navigation is an individual entry, which
/// preserves an honest chronological history even when the same URL is revisited.
/// </summary>
public sealed class HistoryService
{
    private const int MaximumQueryLimit = 5_000;
    private readonly SqliteDatabase _database;

    public HistoryService(SqliteDatabase? database = null)
    {
        _database = database ?? new SqliteDatabase();
    }

    public async Task<HistoryEntry> RecordVisitAsync(
        string url,
        string? title = null,
        string? faviconUrl = null,
        DateTimeOffset? visitedAt = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedUrl = RequireUrl(url);
        var normalizedTitle = title?.Trim() ?? string.Empty;
        var normalizedFaviconUrl = NormalizeOptionalText(faviconUrl);
        var timestamp = (visitedAt ?? DateTimeOffset.UtcNow).ToUniversalTime();

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO history (url, title, favicon_url, visited_utc)
                VALUES ($url, $title, $faviconUrl, $visitedUtc);
                """;
            command.Parameters.Add("$url", SqliteType.Text).Value = normalizedUrl;
            command.Parameters.Add("$title", SqliteType.Text).Value = normalizedTitle;
            command.Parameters.Add("$faviconUrl", SqliteType.Text).Value = normalizedFaviconUrl ?? (object)DBNull.Value;
            command.Parameters.Add("$visitedUtc", SqliteType.Integer).Value = timestamp.ToUnixTimeMilliseconds();
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var idCommand = connection.CreateCommand();
        idCommand.CommandText = "SELECT last_insert_rowid();";
        var id = Convert.ToInt64(await idCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));

        return new HistoryEntry(id, normalizedUrl, normalizedTitle, timestamp, normalizedFaviconUrl);
    }

    public Task<IReadOnlyList<HistoryEntry>> GetRecentAsync(
        int limit = 500,
        CancellationToken cancellationToken = default) =>
        QueryAsync(null, NormalizeLimit(limit), cancellationToken);

    public Task<IReadOnlyList<HistoryEntry>> SearchAsync(
        string query,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        return QueryAsync(query.Trim(), NormalizeLimit(limit), cancellationToken);
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        if (id <= 0)
        {
            return false;
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM history WHERE id = $id;";
        command.Parameters.Add("$id", SqliteType.Integer).Value = id;
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM history;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> GetCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM history;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task<IReadOnlyList<HistoryEntry>> QueryAsync(
        string? query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit == 0)
        {
            return Array.Empty<HistoryEntry>();
        }

        var entries = new List<HistoryEntry>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        if (query is null)
        {
            command.CommandText = """
                SELECT id, url, title, favicon_url, visited_utc
                FROM history
                ORDER BY visited_utc DESC, id DESC
                LIMIT $limit;
                """;
        }
        else
        {
            command.CommandText = """
                SELECT id, url, title, favicon_url, visited_utc
                FROM history
                WHERE url LIKE $query ESCAPE '\' OR title LIKE $query ESCAPE '\'
                ORDER BY visited_utc DESC, id DESC
                LIMIT $limit;
                """;
            command.Parameters.Add("$query", SqliteType.Text).Value = $"%{EscapeLikePattern(query)}%";
        }

        command.Parameters.Add("$limit", SqliteType.Integer).Value = limit;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(ReadHistoryEntry(reader));
        }

        return entries;
    }

    private static HistoryEntry ReadHistoryEntry(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
        reader.IsDBNull(3) ? null : reader.GetString(3));

    private static int NormalizeLimit(int limit) => limit <= 0 ? 0 : Math.Min(limit, MaximumQueryLimit);

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
