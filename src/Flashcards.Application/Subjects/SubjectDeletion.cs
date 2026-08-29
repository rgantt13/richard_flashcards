namespace Flashcards.Application.Subjects;

/// <summary>
/// The one wording for "this subject cannot be deleted yet".
/// <para>
/// Shared because it is produced from two places: the manage panel asks before offering a
/// confirmation, so an impossible delete never gets confirmed, and the command checks again when it
/// runs in case the tree moved in between. Two copies of the message would drift, and the whole
/// value of it is that it names the exact cards to go and fix.
/// </para>
/// </summary>
public static class SubjectDeletion
{
    /// <summary>
    /// How many cards to name before summarising the rest. The message goes in a dialog, and past
    /// a handful the list stops being something you can act on and starts being a wall.
    /// </summary>
    private const int NamedLimit = 10;

    public static string Describe(string subjectName, IReadOnlyList<string> strandedCards)
    {
        var listed = string.Join("\n", strandedCards.Take(NamedLimit).Select(name => $"  •  {name}"));
        var remainder = strandedCards.Count - NamedLimit;

        if (remainder > 0)
        {
            listed += $"\n  •  …and {remainder} more";
        }

        var lead = strandedCards.Count == 1
            ? $"One card wears \"{subjectName}\" and nothing else:"
            : $"{strandedCards.Count} cards wear \"{subjectName}\" and nothing else:";

        return $"""
                {lead}

                {listed}

                "{subjectName}" is at the top level, so there is no parent subject for these cards
                to move up into, and a card must always have at least one subject.

                Give them another subject first — they are listed on the manage panel when no
                subject is selected — and then delete this one.
                """;
    }
}
