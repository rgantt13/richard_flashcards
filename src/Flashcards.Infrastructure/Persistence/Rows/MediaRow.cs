namespace Flashcards.Infrastructure.Persistence.Rows;

// Dapper materialization target for the media table. See CardRows.cs for why this is a plain
// class with a parameterless constructor rather than a positional record.

internal sealed class MediaRow
{
    public Guid Id { get; init; }
    public string FileName { get; init; } = "";
    public string MimeType { get; init; } = "";
    public long ByteSize { get; init; }
    public string Sha256 { get; init; } = "";
}
