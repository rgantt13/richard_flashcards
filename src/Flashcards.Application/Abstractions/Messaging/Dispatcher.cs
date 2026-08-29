using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Flashcards.Application.Abstractions.Messaging;

/// <summary>
/// A ~60-line in-process mediator. Resolves the one handler registered for a request type and
/// invokes it, running any registered validators first.
/// <para>
/// Why hand-rolled instead of MediatR: it is small enough to read in one sitting, has no license
/// question hanging over it, and — the real point — the reflection that makes CQRS feel magical
/// is right here where you can step through it.
/// </para>
/// <para>
/// The trick below is the standard way to call a closed generic method when you only know the
/// type at runtime: cache a non-generic abstract "invoker" per request type, whose generic
/// subclass does the cast. That avoids <c>MethodInfo.Invoke</c> (slow, and it wraps your
/// exceptions in <c>TargetInvocationException</c>) and avoids <c>dynamic</c>.
/// </para>
/// </summary>
internal sealed class Dispatcher(IServiceScopeFactory scopeFactory) : IDispatcher
{
    private static readonly ConcurrentDictionary<(Type Request, Type Result), object> InvokerCache = new();

    public async Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Every request runs in its own DI scope. That is what gives each operation its own
        // database connection and transaction: the infrastructure registers DbSession as scoped,
        // so a handler and every repository it touches share one connection, and it is disposed
        // the moment the request finishes. Without this, a desktop app that fires two async
        // operations from the UI thread would have them trampling one SqliteConnection.
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;

        RunValidators(services, command);

        var invoker = (CommandInvoker<TResult>)InvokerCache.GetOrAdd(
            (command.GetType(), typeof(TResult)),
            static key => Activator.CreateInstance(typeof(CommandInvoker<,>).MakeGenericType(key.Request, key.Result))!);

        var handlerType = typeof(ICommandHandler<,>).MakeGenericType(command.GetType(), typeof(TResult));
        var handler = services.GetService(handlerType)
            ?? throw new InvalidOperationException(
                $"No ICommandHandler<{command.GetType().Name}, {typeof(TResult).Name}> is registered. " +
                "Add it in Flashcards.Application.DependencyInjection.");

        return await invoker.InvokeAsync(handler, command, cancellationToken);
    }

    public async Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;

        RunValidators(services, query);

        var invoker = (QueryInvoker<TResult>)InvokerCache.GetOrAdd(
            (query.GetType(), typeof(TResult)),
            static key => Activator.CreateInstance(typeof(QueryInvoker<,>).MakeGenericType(key.Request, key.Result))!);

        var handlerType = typeof(IQueryHandler<,>).MakeGenericType(query.GetType(), typeof(TResult));
        var handler = services.GetService(handlerType)
            ?? throw new InvalidOperationException(
                $"No IQueryHandler<{query.GetType().Name}, {typeof(TResult).Name}> is registered. " +
                "Add it in Flashcards.Application.DependencyInjection.");

        return await invoker.InvokeAsync(handler, query, cancellationToken);
    }

    private static void RunValidators<TRequest>(IServiceProvider services, TRequest request) where TRequest : notnull
    {
        var validatorType = typeof(IValidator<>).MakeGenericType(request.GetType());
        var enumerableType = typeof(IEnumerable<>).MakeGenericType(validatorType);

        if (services.GetService(enumerableType) is not System.Collections.IEnumerable validators)
        {
            return;
        }

        List<string>? errors = null;

        foreach (var validator in validators)
        {
            // Every IValidator<T> exposes the same single method, so one cached MethodInfo per
            // closed type would work too; there are few enough validators that this is fine.
            var method = validatorType.GetMethod(nameof(IValidator<object>.Validate))!;

            if (method.Invoke(validator, [request]) is IEnumerable<string> failures)
            {
                foreach (var failure in failures)
                {
                    (errors ??= []).Add(failure);
                }
            }
        }

        if (errors is { Count: > 0 })
        {
            throw new ValidationException(errors);
        }
    }

    private abstract class CommandInvoker<TResult>
    {
        public abstract Task<TResult> InvokeAsync(object handler, object command, CancellationToken cancellationToken);
    }

    private sealed class CommandInvoker<TCommand, TResult> : CommandInvoker<TResult>
        where TCommand : ICommand<TResult>
    {
        public override Task<TResult> InvokeAsync(object handler, object command, CancellationToken cancellationToken)
            => ((ICommandHandler<TCommand, TResult>)handler).HandleAsync((TCommand)command, cancellationToken);
    }

    private abstract class QueryInvoker<TResult>
    {
        public abstract Task<TResult> InvokeAsync(object handler, object query, CancellationToken cancellationToken);
    }

    private sealed class QueryInvoker<TQuery, TResult> : QueryInvoker<TResult>
        where TQuery : IQuery<TResult>
    {
        public override Task<TResult> InvokeAsync(object handler, object query, CancellationToken cancellationToken)
            => ((IQueryHandler<TQuery, TResult>)handler).HandleAsync((TQuery)query, cancellationToken);
    }
}
