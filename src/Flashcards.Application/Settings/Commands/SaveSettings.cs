using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Application.Contracts;

namespace Flashcards.Application.Settings.Commands;

/// <summary>
/// Writes the whole settings record, not a field at a time. There are four of them and they are
/// read as a set, so a partial update would be more machinery for no benefit.
/// </summary>
public sealed record SaveSettingsCommand(AppSettings Settings) : ICommand<Unit>;

internal sealed class SaveSettingsHandler(ISettingsStore store) : ICommandHandler<SaveSettingsCommand, Unit>
{
    public async Task<Unit> HandleAsync(SaveSettingsCommand command, CancellationToken cancellationToken)
    {
        await store.SaveAsync(command.Settings, cancellationToken);
        return Unit.Value;
    }
}
