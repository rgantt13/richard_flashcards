using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Abstractions.Persistence;

namespace Flashcards.Application.Media.Queries;

public sealed record LoadMediaQuery(Guid MediaId) : IQuery<byte[]?>;

internal sealed class LoadMediaHandler(IMediaStore store) : IQueryHandler<LoadMediaQuery, byte[]?>
{
    public Task<byte[]?> HandleAsync(LoadMediaQuery query, CancellationToken cancellationToken)
        => store.LoadAsync(query.MediaId, cancellationToken);
}
