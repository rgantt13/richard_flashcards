namespace Flashcards.Application.Abstractions.Messaging;

/// <summary>
/// Raised by the dispatcher when one or more <see cref="IValidator{TRequest}"/> instances rejected
/// a request. Carries every failure so the editor can show them all at once.
/// </summary>
public sealed class ValidationException(IReadOnlyList<string> errors)
    : Exception(errors.Count == 1 ? errors[0] : $"{errors.Count} problems: {string.Join(" ", errors)}")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
