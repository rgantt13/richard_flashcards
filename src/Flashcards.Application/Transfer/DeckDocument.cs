using System.Text.Json;
using System.Text.Json.Serialization;
using Flashcards.Application.Contracts;
using Flashcards.Domain.Cards;

namespace Flashcards.Application.Transfer;

/// <summary>
/// A portable bundle of subjects, cards and the images they use — what an export writes and an
/// import reads.
/// <para>
/// Nothing in here is an id from the database that produced it, with one exception: cards keep
/// theirs, purely as a hint for recognising a deck re-imported into the same library. Subjects are
/// identified by <em>name</em>, because a subject name is already unique across the whole library
/// (see <c>SubjectTreeRules.ValidateNameAsync</c>), and hierarchy travels as a parent name rather
/// than a parent id so the tree can be rebuilt somewhere the ids mean nothing.
/// </para>
/// <para>
/// Answer history deliberately does not travel. A deck is content — the record of how <em>you</em>
/// did on it belongs to your library, and importing someone else's practice would quietly
/// overwrite your own figures.
/// </para>
/// </summary>
public sealed record DeckDocument
{
    /// <summary>Bumped when the shape changes in a way an older reader could not cope with.</summary>
    public int FormatVersion { get; init; } = DeckSerializer.CurrentFormatVersion;

    public DateTimeOffset ExportedUtc { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<DeckSubject> Subjects { get; init; } = [];

    public IReadOnlyList<DeckCard> Cards { get; init; } = [];

    public IReadOnlyList<DeckMedia> Media { get; init; } = [];

    /// <summary>A one-line description of what is in the file, for the picker's header.</summary>
    public string Summary
        => $"{Describe(Cards.Count, "card")}, {Describe(Subjects.Count, "subject")}, {Describe(Media.Count, "image")}";

    private static string Describe(int count, string noun)
        => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}

/// <summary>
/// One subject. <paramref name="Parent"/> is another subject's name, or null for the top level;
/// the exporter always includes a subject's whole ancestor chain, so a parent named here is
/// always present in the same file.
/// </summary>
public sealed record DeckSubject(string Name, string? Parent, string? ColorHex, string? Description);

/// <summary>
/// One card, in the same block and choice shapes the designer already speaks.
/// <para>
/// <paramref name="Subjects"/> holds only the tags actually applied to the card, never the ones it
/// inherits from where those tags sit — inherited tags are derived from the tree, so re-deriving
/// them on the far side is both correct and the only way a re-filed subject stays honest.
/// </para>
/// </summary>
/// <para>
/// Every member is optional in the file and defaults to something harmless. Decks are written by
/// hand and by language models as well as by this app's exporter, and a card that omits
/// <c>Choices</c> because it is not a multiple-choice card should be reported as a card, not
/// crash the import on a null reference. A missing name fails validation with a sentence naming
/// the card; a missing collection is simply empty.
/// </para>
public sealed record DeckCard
{
    /// <summary>
    /// Only a hint. Used to recognise a deck exported from this same library and brought back;
    /// a generated deck can leave it out, or set it to all zeroes.
    /// </summary>
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public CardType CardType { get; init; }

    public string? Notes { get; init; }

    public bool IsSuspended { get; init; }

    public IReadOnlyList<string> Subjects { get; init; } = [];

    public IReadOnlyList<ContentBlockDto> Blocks { get; init; } = [];

    public IReadOnlyList<ChoiceDto> Choices { get; init; } = [];
}

/// <summary>
/// One image, inline. <paramref name="Id"/> is the id the blocks and choices in this file
/// reference; the importer stores the bytes afresh and re-points them at whatever id its own
/// content-addressed store hands back.
/// </summary>
public sealed record DeckMedia(Guid Id, string FileName, string MimeType, byte[] Bytes);

/// <summary>
/// Reads and writes <see cref="DeckDocument"/> as JSON.
/// <para>
/// One human-readable file rather than an archive: a deck is something you email to someone or
/// commit next to your notes, and being able to open it and see what is in it is worth more here
/// than the space base64 costs. Enums are written by name so the file survives anyone reordering
/// an enum, and stays legible.
/// </para>
/// </summary>
public static class DeckSerializer
{
    public const int CurrentFormatVersion = 1;

    /// <summary>The extension exports are written with. Nothing rejects a differently named file.</summary>
    public const string FileExtension = ".fcdeck";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static byte[] Write(DeckDocument deck)
        => JsonSerializer.SerializeToUtf8Bytes(deck, Options);

    /// <summary>
    /// Parses a file's bytes. Throws <see cref="DeckFormatException"/> for anything this cannot
    /// read, so a caller can report "that is not a deck" rather than a JSON parser's line number.
    /// </summary>
    public static DeckDocument Read(byte[] bytes)
    {
        DeckDocument? deck;

        try
        {
            deck = JsonSerializer.Deserialize<DeckDocument>(bytes, Options);
        }
        catch (JsonException exception)
        {
            // The parser's own message is carried through rather than swallowed. It says which
            // property and which line, and a deck written by hand or by a language model usually
            // fails on one bad enum name — "not valid JSON" alone would send you looking for a
            // missing brace that is not there.
            throw new DeckFormatException(
                $"That file could not be read as a flashcards deck. {exception.Message}", exception);
        }

        if (deck is null)
        {
            throw new DeckFormatException("That file is empty.");
        }

        if (deck.FormatVersion > CurrentFormatVersion)
        {
            throw new DeckFormatException(
                $"That deck was written by a newer version of the app (format {deck.FormatVersion}). Update and try again.");
        }

        if (deck.Cards.Count == 0 && deck.Subjects.Count == 0)
        {
            throw new DeckFormatException("That deck has no subjects or cards in it.");
        }

        return deck;
    }
}

/// <summary>A file that could not be read as a deck. Distinct from a deck that imports badly.</summary>
public sealed class DeckFormatException(string message, Exception? inner = null)
    : Exception(message, inner);
