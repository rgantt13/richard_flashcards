namespace Flashcards.Application.Abstractions.Persistence;

/// <summary>
/// Wraps a set of repository calls in one SQLite transaction.
/// Deliberately explicit rather than an ambient/implicit unit of work — with one local database and
/// no web request to hang a scope off, "the handler opens it, the handler commits it" is clearer.
/// </summary>
public interface IUnitOfWork
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);

    Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken);
}

/// <summary>Injectable clock so scheduling logic is testable.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
