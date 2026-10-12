using Microsoft.Data.Sqlite;
using NovaBrowser.Models;

namespace NovaBrowser.Services;

/// <summary>
/// Stores lightweight tab metadata for startup restoration and recently closed
/// tabs. It never stores a WebView instance or page/form data.
/// </summary>
public sealed class SessionService
{
    public const int MaximumRecentlyClosedTabs = 25;

    private readonly SqliteDatabase _database;

    public SessionService(SqliteDatabase? database = null)
    {
        _database = database ?? new SqliteDatabase();
    }

    /// <summary>
    /// Atomically replaces the restore session. Input order becomes the restored
    /// tab order, and exactly one tab is marked active when the list is non-empty.
    /// </summary>
    public async Task SaveSessionAsync(
        IEnumerable<SessionTab> tabs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        var normalizedTabs = NormalizeTabs(tabs);

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using (var deleteCommand = connection.CreateCommand())
            {
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = "DELETE FROM session_tabs;";
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var tab in normalizedTabs)
            {
                await using var insertCommand = connection.CreateCommand();
                insertCommand.Transaction = transaction;
                insertCommand.CommandText = """
                    INSERT INTO session_tabs (position, url, title, favicon_url, is_active)
                    VALUES ($position, $url, $title, $faviconUrl, $isActive);
                    """;
                insertCommand.Parameters.Add("$position", SqliteType.Integer).Value = tab.Position;
                insertCommand.Parameters.Add("$url", SqliteType.Text).Value = tab.Url;
                insertCommand.Parameters.Add("$title", SqliteType.Text).Value = tab.Title;
                insertCommand.Parameters.Add("$faviconUrl", SqliteType.Text).Value = tab.FaviconUrl ?? (object)DBNull.Value;
                insertCommand.Parameters.Add("$isActive", SqliteType.Integer).Value = tab.IsActive ? 1 : 0;
                await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<SessionTab>> GetSessionAsync(CancellationToken cancellationToken = default)
    {
        var tabs = new List<SessionTab>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT url, title, favicon_url, position, is_active
            FROM session_tabs
            ORDER BY position ASC;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tabs.Add(new SessionTab(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt64(4) != 0));
        }

        return tabs;
    }

    public async Task ClearSessionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM session_tabs;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Saves a tab in the recently-closed stack, retaining the newest 25 items.</summary>
    public async Task SaveClosedTabAsync(SessionTab tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var normalized = NormalizeTab(tab, 0, false);

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using (var insertCommand = connection.CreateCommand())
            {
                insertCommand.Transaction = transaction;
                insertCommand.CommandText = """
                    INSERT INTO closed_tabs (url, title, favicon_url, closed_utc)
                    VALUES ($url, $title, $faviconUrl, $closedUtc);
                    """;
                insertCommand.Parameters.Add("$url", SqliteType.Text).Value = normalized.Url;
                insertCommand.Parameters.Add("$title", SqliteType.Text).Value = normalized.Title;
                insertCommand.Parameters.Add("$faviconUrl", SqliteType.Text).Value = normalized.FaviconUrl ?? (object)DBNull.Value;
                insertCommand.Parameters.Add("$closedUtc", SqliteType.Integer).Value = SqliteDatabase.UtcNowMilliseconds();
                await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var trimCommand = connection.CreateCommand())
            {
                trimCommand.Transaction = transaction;
                trimCommand.CommandText = """
                    DELETE FROM closed_tabs
                    WHERE id NOT IN (
                        SELECT id
                        FROM closed_tabs
                        ORDER BY closed_utc DESC, id DESC
                        LIMIT $maximum
                    );
                    """;
                trimCommand.Parameters.Add("$maximum", SqliteType.Integer).Value = MaximumRecentlyClosedTabs;
                await trimCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Removes and returns the most recently closed tab. The delete is part of the
    /// same transaction, so two callers cannot restore the same tab.
    /// </summary>
    public async Task<SessionTab?> RestoreLastClosedTabAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // An immediate transaction reserves the write lock before reading the top
        // item. A deferred read transaction could otherwise race another restore
        // attempt and fail while upgrading to a write transaction.
        using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            long? id = null;
            SessionTab? tab = null;

            await using (var selectCommand = connection.CreateCommand())
            {
                selectCommand.Transaction = transaction;
                selectCommand.CommandText = """
                    SELECT id, url, title, favicon_url
                    FROM closed_tabs
                    ORDER BY closed_utc DESC, id DESC
                    LIMIT 1;
                    """;
                await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    id = reader.GetInt64(0);
                    tab = new SessionTab(
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        Position: 0,
                        IsActive: true);
                }
            }

            if (id is not null)
            {
                await using var deleteCommand = connection.CreateCommand();
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = "DELETE FROM closed_tabs WHERE id = $id;";
                deleteCommand.Parameters.Add("$id", SqliteType.Integer).Value = id.Value;
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return tab;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<SessionTab>> GetRecentlyClosedAsync(
        int limit = MaximumRecentlyClosedTabs,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return Array.Empty<SessionTab>();
        }

        var tabs = new List<SessionTab>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT url, title, favicon_url
            FROM closed_tabs
            ORDER BY closed_utc DESC, id DESC
            LIMIT $limit;
            """;
        command.Parameters.Add("$limit", SqliteType.Integer).Value = Math.Min(limit, MaximumRecentlyClosedTabs);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var position = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tabs.Add(new SessionTab(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                position++,
                false));
        }

        return tabs;
    }

    public async Task ClearRecentlyClosedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM closed_tabs;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static List<SessionTab> NormalizeTabs(IEnumerable<SessionTab> tabs)
    {
        var normalizedTabs = new List<SessionTab>();
        var hasActiveTab = false;
        var position = 0;

        foreach (var tab in tabs)
        {
            ArgumentNullException.ThrowIfNull(tab);
            var isActive = tab.IsActive && !hasActiveTab;
            hasActiveTab |= isActive;
            normalizedTabs.Add(NormalizeTab(tab, position++, isActive));
        }

        if (!hasActiveTab && normalizedTabs.Count > 0)
        {
            normalizedTabs[0] = normalizedTabs[0] with { IsActive = true };
        }

        return normalizedTabs;
    }

    private static SessionTab NormalizeTab(SessionTab tab, int position, bool isActive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tab.Url);
        return tab with
        {
            Url = tab.Url.Trim(),
            Title = tab.Title?.Trim() ?? string.Empty,
            FaviconUrl = string.IsNullOrWhiteSpace(tab.FaviconUrl) ? null : tab.FaviconUrl.Trim(),
            Position = position,
            IsActive = isActive
        };
    }
}
