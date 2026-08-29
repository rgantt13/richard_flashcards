namespace Flashcards.Application.Abstractions.Messaging;

/// <summary>Stand-in for "no return value" so every handler can be <c>Task&lt;T&gt;</c>.</summary>
public readonly record struct Unit
{
    public static readonly Unit Value = default;
}

/// <summary>A request that changes state and returns <typeparamref name="TResult"/>.</summary>
public interface ICommand<TResult>;

/// <summary>A request that changes state and returns nothing.</summary>
public interface ICommand : ICommand<Unit>;

/// <summary>A request that reads state. Query handlers must not write.</summary>
public interface IQuery<TResult>;

public interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

public interface ICommandHandler<in TCommand> : ICommandHandler<TCommand, Unit> where TCommand : ICommand;

public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// The single seam the presentation layer talks through. ViewModels take an
/// <see cref="IDispatcher"/> and nothing else from the application layer.
/// </summary>
public interface IDispatcher
{
    Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default);

    Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional pre-flight check. Registered per request type; the dispatcher runs every
/// registered validator before the handler and aggregates the failures.
/// This is where a behaviour pipeline would go if you wanted logging, retries or
/// transactions as cross-cutting decorators.
/// </summary>
public interface IValidator<in TRequest>
{
    IEnumerable<string> Validate(TRequest request);
}
