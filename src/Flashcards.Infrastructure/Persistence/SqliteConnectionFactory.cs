using System.Data;
using Microsoft.Data.Sqlite;

namespace Flashcards.Infrastructure.Persistence;

public interface IDbConnectionFactory
{
    /// <summary>Opens a connection with the app's pragmas already applied.</summary>
    Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken);

    string DatabasePath { get; }
}

/// <summary>
/// Creates and configures SQLite connections.
/// <para>
/// The pragmas below are the part people miss when they come from SQL Server, where the
/// equivalents are either server-wide settings or simply not optional:
/// </para>
/// <list type="bullet">
///   <item><b>foreign_keys = ON</b> — SQLite ships with FK enforcement OFF for backwards
///   compatibility, <i>per connection</i>. Every <c>ON DELETE CASCADE</c> in the schema is inert
///   until you set this. There is no server-level equivalent; forget it once and you get orphans.</item>
///   <item><b>journal_mode = WAL</b> — write-ahead logging. Readers stop blocking the writer,
///   which is roughly what READ COMMITTED SNAPSHOT buys you in SQL Server. It is a persistent
///   property of the database file, not the connection, so setting it once would do — it is
///   cheap and idempotent, so it stays here.</item>
///   <item><b>busy_timeout</b> — SQLite has a single writer. Without this, a concurrent write
///   returns SQLITE_BUSY immediately instead of waiting. This is the closest thing to
///   SET LOCK_TIMEOUT.</item>
///   <item><b>synchronous = NORMAL</b> — safe with WAL, and much faster than FULL for a
///   local desktop app.</item>
/// </list>
/// </summary>
public sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(string databasePath)
    {
        DatabasePath = databasePath;

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Cache=Shared plus the default Pooling=true is what lets several connections in one
            // process share the same in-memory page cache.
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var pragma = connection.CreateCommand();
        pragma.CommandText =
            """
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;
            PRAGMA busy_timeout = 5000;
            PRAGMA synchronous = NORMAL;
            PRAGMA temp_store = MEMORY;
            """;
        await pragma.ExecuteNonQueryAsync(cancellationToken);

        return connection;
    }
}

/// <summary>
/// Ambient per-operation connection + transaction, resolved from DI as scoped-per-unit-of-work.
/// Repositories ask this for the connection they should use, so a handler that wraps several
/// repository calls in <see cref="Flashcards.Application.Abstractions.Persistence.IUnitOfWork"/>
/// gets all of them on one transaction without any of them knowing.
/// </summary>
public sealed class DbSession(IDbConnectionFactory factory) : IAsyncDisposable
{
    private SqliteConnection? _connection;

    public SqliteTransaction? Transaction { get; private set; }

    public async Task<SqliteConnection> GetConnectionAsync(CancellationToken cancellationToken)
        => _connection ??= await factory.OpenAsync(cancellationToken);

    public IDbTransaction? DbTransaction => Transaction;

    public async Task<SqliteTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken);
        Transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        return Transaction;
    }

    public void ClearTransaction() => Transaction = null;

    public async ValueTask DisposeAsync()
    {
        if (Transaction is not null)
        {
            await Transaction.DisposeAsync();
            Transaction = null;
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }
}
