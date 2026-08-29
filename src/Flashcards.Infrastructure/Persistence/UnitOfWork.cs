using Flashcards.Application.Abstractions.Persistence;

namespace Flashcards.Infrastructure.Persistence;

/// <summary>
/// Wraps the work in a single SQLite transaction on the ambient <see cref="DbSession"/>.
/// <para>
/// Re-entrant: a handler that calls another handler joins the outer transaction rather than
/// starting a nested one. SQLite has SAVEPOINT for real nesting, but nothing in this app needs
/// partial rollback, and joining is the behaviour people expect.
/// </para>
/// <para>
/// [T-SQL] SQLite's default isolation is SERIALIZABLE, and with WAL there is exactly one writer
/// at a time. There is no READ UNCOMMITTED, no NOLOCK, and no deadlock graph to untangle — a
/// contended write simply waits out <c>busy_timeout</c> and then fails with SQLITE_BUSY.
/// </para>
/// </summary>
internal sealed class UnitOfWork(DbSession session) : IUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        if (session.Transaction is not null)
        {
            return await work(cancellationToken);
        }

        var transaction = await session.BeginTransactionAsync(cancellationToken);

        try
        {
            var result = await work(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            session.ClearTransaction();
            await transaction.DisposeAsync();
        }
    }

    public async Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
        => await ExecuteAsync<object?>(async ct =>
        {
            await work(ct);
            return null;
        }, cancellationToken);
}
