using System.Runtime.CompilerServices;

namespace Flashcards.Domain.Common;

internal static class Guard
{
    public static string AgainstNullOrWhiteSpace(string? value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException($"{name} is required.");
        }

        return value.Trim();
    }

    public static string AgainstTooLong(string value, int maxLength, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value.Length > maxLength)
        {
            throw new DomainException($"{name} must be {maxLength} characters or fewer.");
        }

        return value;
    }
}
