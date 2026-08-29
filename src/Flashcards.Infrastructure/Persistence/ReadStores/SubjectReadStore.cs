using Dapper;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Application.Contracts;
using Flashcards.Infrastructure.Persistence.Rows;
using Flashcards.Infrastructure.Persistence.Sql;

namespace Flashcards.Infrastructure.Persistence.ReadStores;

/// <summary>
/// The read side of the subject tree: the list every panel arranges into a tree, and the rolled-up
/// figures the study panel reports against each subject.
/// <para>
/// Split out of the card read store once subjects became a hierarchy. Everything here derives
/// ancestry through <see cref="SubjectClosure"/> rather than reading it off a column, so a subject
/// that moves takes its whole subtree's numbers with it without a single row being rewritten.
/// </para>
/// </summary>
internal sealed class SubjectReadStore(DbSession session) : ISubjectReadStore
{
    public async Task<IReadOnlyList<SubjectSummary>> GetSubjectSummariesAsync(CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<SubjectSummaryRow>(new CommandDefinition(
            $"""
             {SubjectClosure.Cte}
             SELECT s.id          AS Id,
                    s.name        AS Name,
                    s.color_hex   AS ColorHex,
                    s.description AS Description,
                    s.parent_id   AS ParentId,
                    -- Self is in the closure, so counting a subject's ancestors-including-self
                    -- gives its depth directly: a root has only itself and reports 1.
                    (SELECT COUNT(*) FROM subject_closure cl WHERE cl.descendant = s.id) AS Depth,
                    (SELECT COUNT(*) FROM card_subjects cs WHERE cs.subject_id = s.id)   AS CardCount,
                    -- DISTINCT because a card tagged with both a parent and one of its children
                    -- reaches the parent twice through the closure and is still one card.
                    (SELECT COUNT(DISTINCT cs.card_id)
                     FROM   subject_closure cl
                     JOIN   card_subjects   cs ON cs.subject_id = cl.descendant
                     WHERE  cl.ancestor = s.id)                                          AS TotalCardCount
             FROM   subjects s
             ORDER  BY s.name COLLATE NOCASE;
             """,
            transaction: session.DbTransaction, cancellationToken: cancellationToken));

        return
        [
            .. rows.Select(r => new SubjectSummary(
                r.Id, r.Name, r.ColorHex, r.Description, r.CardCount, r.ParentId, r.Depth, r.TotalCardCount)),
        ];
    }

    public async Task<IReadOnlyList<SubjectStats>> GetSubjectStatsAsync(CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);

        // Every figure is rolled up over the subtree, because selecting a subject studies the whole
        // subtree. "How am I doing on SQL" has to include the MSSQL and SQLite cards underneath it.
        //
        // scope is the (subject, card) set after that widening, and it is DISTINCT: a card tagged
        // both SQL and MSSQL reaches SQL by two different paths through the closure and must not be
        // counted or scored twice. Joining review_log straight onto card_subjects would instead
        // multiply card rows by answer rows and make CardCount wrong — the classic fan-out when
        // aggregating across two one-to-many relations at once.
        var rows = await connection.QueryAsync<SubjectStatsRow>(new CommandDefinition(
            $"""
             {SubjectClosure.Cte},
             scope(subject_id, card_id) AS (
                 SELECT DISTINCT cl.ancestor, cs.card_id
                 FROM   subject_closure cl
                 JOIN   card_subjects   cs ON cs.subject_id = cl.descendant
             ),
             card_tally(card_id, answered, correct) AS (
                 SELECT card_id, COUNT(*), COALESCE(SUM(was_correct), 0)
                 FROM   review_log
                 GROUP  BY card_id
             )
             SELECT s.id        AS Id,
                    s.name      AS Name,
                    s.color_hex AS ColorHex,
                    s.parent_id AS ParentId,
                    (SELECT COUNT(*) FROM subject_closure cl WHERE cl.descendant = s.id) AS Depth,
                    (SELECT COUNT(*) FROM card_subjects cs WHERE cs.subject_id = s.id)   AS DirectCardCount,
                    EXISTS (SELECT 1 FROM subjects ch WHERE ch.parent_id = s.id)         AS HasChildren,
                    COUNT(sc.card_id)                                                    AS CardCount,
                    COALESCE(SUM(t.answered), 0)                                         AS Answered,
                    COALESCE(SUM(t.correct), 0)                                          AS Correct,
                    COUNT(t.card_id)                                                     AS CardsPractised
             FROM       subjects s
             LEFT  JOIN scope      sc ON sc.subject_id = s.id
             LEFT  JOIN card_tally t  ON t.card_id = sc.card_id
             GROUP  BY s.id, s.name, s.color_hex, s.parent_id
             ORDER  BY s.name COLLATE NOCASE;
             """,
            transaction: session.DbTransaction, cancellationToken: cancellationToken));

        return
        [
            .. rows.Select(r => new SubjectStats(
                r.Id, r.Name, r.ColorHex,
                new PracticeStats(r.Answered, r.Correct),
                r.CardCount,
                r.CardsPractised,
                r.ParentId,
                r.Depth,
                r.DirectCardCount)
            {
                HasChildren = r.HasChildren != 0,
            }),
        ];
    }
}
