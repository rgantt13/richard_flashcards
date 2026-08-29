namespace Flashcards.Infrastructure.Persistence.Rows;

// These are Dapper materialization targets, not domain records. They are deliberately plain
// classes with a parameterless constructor rather than positional records: Dapper picks a
// constructor-based deserializer for any type without one, and that path requires each
// constructor parameter's CLR type to exactly match the raw ADO type (e.g. System.String for
// a TEXT column, System.Int64 for any SQLite INTEGER column) with no regard for registered
// ITypeHandlers or numeric widening. A parameterless constructor makes Dapper fall back to
// per-property assignment instead, which does apply type handlers (Guid <-> TEXT) and does
// widen Int64 -> int/bool automatically. Skipping this reliably throws
// "A parameterless default constructor or one matching signature (...) is required" the
// moment a query actually returns a row with a Guid or DateTimeOffset column.

// They live here, together, rather than nested inside the store or repository that happens to
// query them: several of these shapes are read by more than one of those, and the two copies of
// BlockRow that used to exist were identical to the character.

internal sealed class SummaryRow
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public int CardType { get; init; }
    public long IsSuspended { get; init; }
    public string QuestionPreview { get; init; } = "";
    public int BlockCount { get; init; }
    public long HasImages { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
    public int Answered { get; init; }
    public int Correct { get; init; }
    public int TotalCount { get; init; }
}

internal sealed class DetailRow
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public int CardType { get; init; }
    public string? Notes { get; init; }
    public long IsSuspended { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
}

internal sealed class BlockRow
{
    public Guid Id { get; init; }
    public int Face { get; init; }
    public int Ordinal { get; init; }
    public int Kind { get; init; }
    public string? Text { get; init; }
    public string? Language { get; init; }
    public Guid? MediaId { get; init; }
    public int Stretch { get; init; }
    public double? MaxHeight { get; init; }
    public string? AltText { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Width { get; init; }
    public double? Height { get; init; }
}

internal sealed class ChoiceRow
{
    public Guid Id { get; init; }
    public int Ordinal { get; init; }
    public string Text { get; init; } = "";
    public long IsCorrect { get; init; }
    public Guid? MediaId { get; init; }
}

internal sealed class CardSubjectRow
{
    public int IsInherited { get; init; }
    public Guid CardId { get; init; }
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string? ColorHex { get; init; }
}

internal sealed class CardRow
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public int CardType { get; init; }
    public string? Notes { get; init; }
    public long IsSuspended { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
}
