using Microsoft.Data.Sqlite;
using NovaBrowser.Models;

namespace NovaBrowser.Services;

/// <summary>
/// Reads and writes the small set of browser preferences stored in SQLite.
/// Missing or invalid values deliberately fall back to safe defaults so that a
/// damaged preference row never prevents the browser from starting.
/// </summary>
public sealed class SettingsService
{
    private const string ThemeKey = "theme";
    private const string SearchEngineKey = "search_engine";
    private const string HomePageKey = "home_page";
    private const string RestoreSessionKey = "restore_session";
    private const string DownloadFolderKey = "download_folder";
    private const string MemoryModeKey = "memory_mode";
    private const string AdBlockEnabledKey = "adblock_enabled";
    private const string AdBlockExceptionsKey = "adblock_exceptions";

    private readonly SqliteDatabase _database;

    public SettingsService(SqliteDatabase? database = null)
    {
        _database = database ?? new SqliteDatabase();
    }

    public static BrowserSettings Default { get; } = new();

    public async Task<BrowserSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT key, value
            FROM settings
            WHERE key IN ($theme, $searchEngine, $homePage, $restoreSession, $downloadFolder, $memoryMode, $adBlockEnabled, $adBlockExceptions);
            """;
        command.Parameters.Add("$theme", SqliteType.Text).Value = ThemeKey;
        command.Parameters.Add("$searchEngine", SqliteType.Text).Value = SearchEngineKey;
        command.Parameters.Add("$homePage", SqliteType.Text).Value = HomePageKey;
        command.Parameters.Add("$restoreSession", SqliteType.Text).Value = RestoreSessionKey;
        command.Parameters.Add("$downloadFolder", SqliteType.Text).Value = DownloadFolderKey;
        command.Parameters.Add("$memoryMode", SqliteType.Text).Value = MemoryModeKey;
        command.Parameters.Add("$adBlockEnabled", SqliteType.Text).Value = AdBlockEnabledKey;
        command.Parameters.Add("$adBlockExceptions", SqliteType.Text).Value = AdBlockExceptionsKey;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return new BrowserSettings
        {
            Theme = ReadEnum(values, ThemeKey, Default.Theme),
            SearchEngine = ReadEnum(values, SearchEngineKey, Default.SearchEngine),
            HomePage = ReadNonEmptyString(values, HomePageKey, Default.HomePage),
            RestoreSession = ReadBoolean(values, RestoreSessionKey, Default.RestoreSession),
            DownloadFolder = ReadOptionalString(values, DownloadFolderKey),
            MemoryMode = ReadEnum(values, MemoryModeKey, Default.MemoryMode),
            AdBlockEnabled = ReadBoolean(values, AdBlockEnabledKey, Default.AdBlockEnabled),
            AdBlockExceptions = ReadOptionalString(values, AdBlockExceptionsKey) ?? string.Empty
        };
    }

    public async Task SaveAsync(BrowserSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = Normalize(settings);

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var updatedUtc = SqliteDatabase.UtcNowMilliseconds();
            await UpsertAsync(connection, transaction, ThemeKey, normalized.Theme.ToString(), updatedUtc, cancellationToken)
                .ConfigureAwait(false);
            await UpsertAsync(connection, transaction, SearchEngineKey, normalized.SearchEngine.ToString(), updatedUtc, cancellationToken)
                .ConfigureAwait(false);
            await UpsertAsync(connection, transaction, HomePageKey, normalized.HomePage, updatedUtc, cancellationToken)
                .ConfigureAwait(false);
            await UpsertAsync(connection, transaction, RestoreSessionKey, normalized.RestoreSession ? "1" : "0", updatedUtc, cancellationToken)
                .ConfigureAwait(false);
            await UpsertAsync(connection, transaction, DownloadFolderKey, normalized.DownloadFolder ?? string.Empty, updatedUtc, cancellationToken)
                .ConfigureAwait(false);
            await UpsertAsync(connection, transaction, MemoryModeKey, normalized.MemoryMode.ToString(), updatedUtc, cancellationToken)
                .ConfigureAwait(false);

            await UpsertAsync(connection, transaction, AdBlockEnabledKey, normalized.AdBlockEnabled ? "1" : "0", updatedUtc, cancellationToken)
                .ConfigureAwait(false);
            await UpsertAsync(connection, transaction, AdBlockExceptionsKey, normalized.AdBlockExceptions, updatedUtc, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Removes persisted preferences and returns to <see cref="Default"/> values.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM settings;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value,
        long updatedUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO settings (key, value, updated_utc)
            VALUES ($key, $value, $updatedUtc)
            ON CONFLICT(key) DO UPDATE SET
                value = excluded.value,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.Add("$key", SqliteType.Text).Value = key;
        command.Parameters.Add("$value", SqliteType.Text).Value = value;
        command.Parameters.Add("$updatedUtc", SqliteType.Integer).Value = updatedUtc;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static BrowserSettings Normalize(BrowserSettings settings)
    {
        if (!Enum.IsDefined(settings.Theme))
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Wybrany motyw nie jest obsługiwany.");
        }

        if (!Enum.IsDefined(settings.SearchEngine))
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Wybrana wyszukiwarka nie jest obsługiwana.");
        }

        if (!Enum.IsDefined(settings.MemoryMode))
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Wybrany tryb pamięci nie jest obsługiwany.");
        }

        var homePage = settings.HomePage?.Trim();
        if (string.IsNullOrWhiteSpace(homePage))
        {
            throw new ArgumentException("Strona startowa nie może być pusta.", nameof(settings));
        }

        var downloadFolder = string.IsNullOrWhiteSpace(settings.DownloadFolder)
            ? null
            : settings.DownloadFolder.Trim();

        return settings with
        {
            HomePage = homePage,
            DownloadFolder = downloadFolder
        };
    }

    private static TEnum ReadEnum<TEnum>(
        IReadOnlyDictionary<string, string> values,
        string key,
        TEnum fallback)
        where TEnum : struct, Enum
    {
        return values.TryGetValue(key, out var text) && Enum.TryParse<TEnum>(text, true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : fallback;
    }

    private static bool ReadBoolean(
        IReadOnlyDictionary<string, string> values,
        string key,
        bool fallback)
    {
        if (!values.TryGetValue(key, out var text))
        {
            return fallback;
        }

        return text switch
        {
            "1" => true,
            "0" => false,
            _ when bool.TryParse(text, out var value) => value,
            _ => fallback
        };
    }

    private static string ReadNonEmptyString(
        IReadOnlyDictionary<string, string> values,
        string key,
        string fallback)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
    }

    private static string? ReadOptionalString(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }
}
