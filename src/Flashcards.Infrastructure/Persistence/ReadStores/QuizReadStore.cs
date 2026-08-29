using Dapper;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Infrastructure.Persistence.Sql;

namespace Flashcards.Infrastructure.Persistence.ReadStores;

/// <summary>
/// Assembling one sitting's queue.
/// <para>
/// One query, but it does not belong with the management grid's search: nothing is filtered out by
/// when it was last seen, the ordering rules are about how badly you do on a card rather than about
/// sorting a list, and the result is a list of ids rather than anything a screen binds to.
/// </para>
/// </summary>
internal sealed class QuizReadStore(DbSession session) : IQuizReadStore
{
    public async Task<IReadOnlyList<Guid>> GetQuizQueueAsync(
        IReadOnlyCollection<Guid> subjectIds,
        IReadOnlyCollection<Guid> cardIds,
        int maxCards,
        bool hardestFirst,
        CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        parameters.Add("Max", Math.Clamp(maxCards, 1, 1000));

        // Scope narrows in order of specificity. An explicit card set is the user naming exactly
        // what they want, so it beats a subject filter rather than combining with it.
        var scope = "1 = 1";
        var needsClosure = false;

        if (cardIds.Count > 0)
        {
            scope = "c.id IN @CardIds";
            parameters.Add("CardIds", cardIds.ToArray());
        }
        else if (subjectIds.Count > 0)
        {
            // EXISTS, not a join: a card tagged with three of the chosen subjects is still one
            // card and must be queued once. The chosen subjects widen through the closure first, so
            // choosing "SQL" queues everything filed under MSSQL and SQLite as well.
            scope = """
                    EXISTS (SELECT 1 FROM card_subjects csub
                            WHERE csub.card_id = c.id
                              AND csub.subject_id IN (SELECT cl.descendant
                                                      FROM   subject_closure cl
                                                      WHERE  cl.ancestor IN @SubjectIds))
                    """;
            parameters.Add("SubjectIds", subjectIds.ToArray());
            needsClosure = true;
        }

        // Nothing is filtered out by when it was last seen — every unsuspended card in the chosen
        // subjects is eligible. Only the order differs.
        //
        // "Hardest first" ranks by share answered wrong, with never-answered cards treated as 1.0
        // so they lead: an untouched card is the one you have most to learn from. RANDOM() breaks
        // ties, so a run of equally-weak cards is not always presented in the same order.
        var order = hardestFirst
            ? """
              ORDER BY CASE WHEN answered = 0 THEN 1.0
                            ELSE 1.0 - (CAST(correct AS REAL) / answered) END DESC,
                       RANDOM()
              """
            : "ORDER BY RANDOM()";

        // The closure joins the CTE list as a sibling of `eligible` when a subject filter is in
        // play; SQLite takes one WITH per statement, so it cannot simply be prepended.
        var preamble = needsClosure ? $"{SubjectClosure.Cte}," : "WITH";

        var ids = await connection.QueryAsync<Guid>(new CommandDefinition(
            $"""
             {preamble} eligible AS (
                 SELECT c.id AS card_id,
                        (SELECT COUNT(*)      FROM review_log l WHERE l.card_id = c.id) AS answered,
                        (SELECT COALESCE(SUM(l.was_correct), 0)
                                              FROM review_log l WHERE l.card_id = c.id) AS correct
                 FROM   flashcards c
                 WHERE  c.is_suspended = 0
                   AND  {scope}
             )
             SELECT card_id
             FROM   eligible
             {order}
             LIMIT  @Max;
             """,
            parameters,
            session.DbTransaction, cancellationToken: cancellationToken));

        return [.. ids];
    }
}
