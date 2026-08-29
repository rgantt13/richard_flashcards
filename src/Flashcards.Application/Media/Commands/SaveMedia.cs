using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Application.Contracts;

namespace Flashcards.Application.Media.Commands;

/// <summary>Stores pasted or dropped image bytes and hands back the id a content block will reference.</summary>
public sealed record SaveMediaCommand(byte[] Bytes, string? SuggestedFileName) : ICommand<MediaDescriptor>;

internal sealed class SaveMediaHandler(IMediaStore store) : ICommandHandler<SaveMediaCommand, MediaDescriptor>
{
    public Task<MediaDescriptor> HandleAsync(SaveMediaCommand command, CancellationToken cancellationToken)
        => store.SaveAsync(command.Bytes, command.SuggestedFileName, cancellationToken);
}
