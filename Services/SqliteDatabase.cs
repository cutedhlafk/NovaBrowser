using Microsoft.Data.Sqlite;
using System.IO;

namespace NovaBrowser.Services;

/// <summary>
/// Owns the SQLite database lifecycle and applies forward-only schema migrations.
/// Services should share one instance, although separate instances are safe because
/// the schema migrations and SQLite WAL mode are idempotent.
/// </summary>
public sealed class SqliteDatabase : IDisposable
{
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly string _connectionString;
    private bool _initialized;
    private bool _disposed;

    private static readonly IReadOnlyList<DatabaseMigration> Migrations =
    [
        new(
            1,
            """
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL,
                updated_utc INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS history (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                url TEXT NOT NULL,
                title TEXT NOT NULL DEFAULT '',
                favicon_url TEXT NULL,
                visited_utc INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_history_visited_utc
                ON history (visited_utc DESC);

            CREATE INDEX IF NOT EXISTS ix_history_url
                ON history (url);

            CREATE TABLE IF NOT EXISTS bookmarks (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                url TEXT NOT NULL UNIQUE,
                title TEXT NOT NULL DEFAULT '',
                favicon_url TEXT NULL,
                created_utc INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_bookmarks_created_utc
                ON bookmarks (created_utc DESC);

            CREATE TABLE IF NOT EXISTS session_tabs (
                position INTEGER NOT NULL PRIMARY KEY,
                url TEXT NOT NULL,
                title TEXT NOT NULL DEFAULT '',
                favicon_url TEXT NULL,
                is_active INTEGER NOT NULL DEFAULT 0 CHECK (is_active IN (0, 1))
            );

            CREATE TABLE IF NOT EXISTS closed_tabs (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                url TEXT NOT NULL,
                title TEXT NOT NULL DEFAULT '',
                favicon_url TEXT NULL,
                closed_utc INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_closed_tabs_closed_utc
                ON closed_tabs (closed_utc DESC, id DESC);
            """)
    ];

    public SqliteDatabase(string? databasePath = null)
    {
        DatabasePath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(databasePath) ? AppPaths.DatabasePath : databasePath);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>
    /// Ensures the database exists and applies every pending migration exactly once.
    /// It is safe to call on every application launch and from several services.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var databaseDirectory = Path.GetDirectoryName(DatabasePath);
            if (string.IsNullOrWhiteSpace(databaseDirectory))
            {
                throw new InvalidOperationException("Nie można ustalić katalogu bazy danych NovaBrowser.");
            }

            Directory.CreateDirectory(databaseDirectory);

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            await EnsureMigrationLedgerAsync(connection, cancellationToken).ConfigureAwait(false);

            var appliedVersions = await ReadAppliedMigrationVersionsAsync(connection, cancellationToken)
                .ConfigureAwait(false);

            foreach (var migration in Migrations)
            {
                if (appliedVersions.Contains(migration.Version))
                {
                    continue;
                }

                await ApplyMigrationAsync(connection, migration, cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    /// <summary>
    /// Opens a ready-to-use SQLite connection. The caller owns and must dispose the
    /// returned connection.
    /// </summary>
    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var connection = CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _initializationLock.Dispose();
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    private static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureMigrationLedgerAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER NOT NULL PRIMARY KEY,
                applied_utc INTEGER NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HashSet<int>> ReadAppliedMigrationVersionsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var versions = new HashSet<int>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_migrations;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions;
    }

    private static async Task ApplyMigrationAsync(
        SqliteConnection connection,
        DatabaseMigration migration,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var ledgerCommand = connection.CreateCommand())
            {
                ledgerCommand.Transaction = transaction;
                ledgerCommand.CommandText = """
                    INSERT INTO schema_migrations (version, applied_utc)
                    VALUES ($version, $appliedUtc);
                    """;
                ledgerCommand.Parameters.Add("$version", SqliteType.Integer).Value = migration.Version;
                ledgerCommand.Parameters.Add("$appliedUtc", SqliteType.Integer).Value = UtcNowMilliseconds();
                await ledgerCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal static long UtcNowMilliseconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record DatabaseMigration(int Version, string Sql);
}
