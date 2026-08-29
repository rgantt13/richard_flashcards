namespace Flashcards.Infrastructure.Persistence.Sql;

/// <summary>
/// SQL every read store that touches the subject tree needs.
/// <para>
/// Extracted when the one big read store became four. Three of them walk the tree — cards widen a
/// selected subject to its descendants, subjects roll their figures up, the quiz queue does both —
/// and a recursive CTE copied into three files is three chances for them to disagree about what
/// "under this subject" means.
/// </para>
/// </summary>
internal static class SubjectClosure
{
    /// <summary>
    /// The transitive closure of the subject tree: one row per (ancestor, descendant) pair, with
    /// every subject listed as its own ancestor so a childless subject still appears.
    /// <para>
    /// This is what makes ancestry derivable. Selecting "SQL" has to reach cards tagged MSSQL, and
    /// the alternative — storing a card_subjects row per ancestor — would need every card beneath a
    /// subject rewritten each time it moved.
    /// </para>
    /// <para>
    /// [T-SQL] The recursive CTE is spelled almost identically to SQL Server's, with two
    /// differences: RECURSIVE is a required keyword here, and there is no MAXRECURSION hint. That
    /// second point is why this is <c>UNION</c> and not <c>UNION ALL</c> — UNION discards rows it
    /// has already produced, so a cycle that somehow reached storage terminates instead of spinning
    /// forever. The domain forbids cycles; this makes a corrupt row a wrong answer rather than a
    /// hung application.
    /// </para>
    /// </summary>
    public const string Cte =
        """
        WITH RECURSIVE subject_closure(ancestor, descendant) AS (
            SELECT id, id FROM subjects
            UNION
            SELECT cl.ancestor, s.id
            FROM   subject_closure cl
            JOIN   subjects        s ON s.parent_id = cl.descendant
        )
        """;
}
