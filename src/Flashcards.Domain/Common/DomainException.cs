namespace Flashcards.Domain.Common;

/// <summary>
/// Thrown when an operation would leave an aggregate in an invalid state.
/// The Application layer catches these and turns them into user-facing errors.
/// </summary>
public sealed class DomainException(string message) : Exception(message);
