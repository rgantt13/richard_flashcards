using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Application.Contracts;

namespace Flashcards.Application.Settings.Queries;

public sealed record GetSettingsQuery : IQuery<AppSettings>;

internal sealed class GetSettingsHandler(ISettingsStore store) : IQueryHandler<GetSettingsQuery, AppSettings>
{
    public Task<AppSettings> HandleAsync(GetSettingsQuery query, CancellationToken cancellationToken)
        => store.LoadAsync(cancellationToken);
}

/// <summary>Where the library lives on disk, and whether it was moved there for this run.</summary>
public sealed record GetDataLocationQuery : IQuery<DataLocation>;

internal sealed class GetDataLocationHandler(ISettingsStore store) : IQueryHandler<GetDataLocationQuery, DataLocation>
{
    public Task<DataLocation> HandleAsync(GetDataLocationQuery query, CancellationToken cancellationToken)
        => Task.FromResult(store.Location);
}
