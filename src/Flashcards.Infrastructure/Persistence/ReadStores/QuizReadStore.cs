using Dapper;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Application.Contracts;
using Flashcards.Domain.Cards;
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
    /// <summary>
    /// The card types the app can mark on the learner's behalf. Standard and freeform cards are
    /// graded by the person answering them, so a drill that runs on a clock cannot use them.
    /// </summary>
    private static readonly int[] AutoGradedTypes = [(int)CardType.MultipleChoice, (int)CardType.Cloze];

    public async Task<IReadOnlyList<Guid>> GetQuizQueueAsync(QuizOptions options, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        parameters.Add("Max", Math.Clamp(options.MaxCards, 1, 10_000));

        // Scope narrows in order of specificity. An explicit card set is the user naming exactly
        // what they want, so it beats a subject filter rather than combining with it.
        var scope = "1 = 1";
        var needsClosure = false;

        if (options.CardIds.Count > 0)
        {
            scope = "c.id IN @CardIds";
            parameters.Add("CardIds", options.CardIds.ToArray());
        }
        else if (options.SubjectIds.Count > 0)
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
            parameters.Add("SubjectIds", options.SubjectIds.ToArray());
            needsClosure = true;
        }

        if (options.AutoGradedOnly)
        {
            scope += " AND c.card_type IN @AutoGraded";
            parameters.Add("AutoGraded", AutoGradedTypes);
        }

        // Two of the four modes also narrow *which* cards are eligible rather than only reordering
        // them, so the filter and the ordering are chosen together.
        var (filter, order) = options.Draw switch
        {
            // Never answered. `answered` comes from the CTE below, so this reads off work already done.
            QuizDraw.Untouched => ("WHERE answered = 0", "ORDER BY RANDOM()"),

            // Whatever you got wrong last time you saw it, most recent first. last_wrong_utc is
            // null for a card whose latest answer was right, which is what excludes it.
            QuizDraw.RecentlyMissed => ("WHERE last_wrong_utc IS NOT NULL", "ORDER BY last_wrong_utc DESC"),

            // Ranks by share answered wrong, with never-answered cards treated as 1.0 so they
            // lead: an untouched card is the one you have most to learn from. RANDOM() breaks ties,
            // so a run of equally-weak cards is not always presented in the same order.
            QuizDraw.HardestFirst => (
                string.Empty,
                """
                ORDER BY CASE WHEN answered = 0 THEN 1.0
                              ELSE 1.0 - (CAST(correct AS REAL) / answered) END DESC,
                         RANDOM()
                """),

            _ => (string.Empty, "ORDER BY RANDOM()"),
        };

        // The closure joins the CTE list as a sibling of `eligible` when a subject filter is in
        // play; SQLite takes one WITH per statement, so it cannot simply be prepended.
        var preamble = needsClosure ? $"{SubjectClosure.Cte}," : "WITH";

        // last_wrong_utc is a correlated subquery rather than a window function over review_log:
        // it has to be null when the *latest* answer was correct, and "the latest row, but only if
        // it failed" is a filter on one row rather than a rank over all of them.
        var ids = await connection.QueryAsync<Guid>(new CommandDefinition(
            $"""
             {preamble} eligible AS (
                 SELECT c.id AS card_id,
                        (SELECT COUNT(*)      FROM review_log l WHERE l.card_id = c.id) AS answered,
                        (SELECT COALESCE(SUM(l.was_correct), 0)
                                              FROM review_log l WHERE l.card_id = c.id) AS correct,
                        (SELECT l.reviewed_utc
                         FROM   review_log l
                         WHERE  l.card_id = c.id
                         ORDER  BY l.reviewed_utc DESC
                         LIMIT  1)                                                      AS last_utc,
                        (SELECT CASE WHEN l.was_correct = 0 THEN l.reviewed_utc END
                         FROM   review_log l
                         WHERE  l.card_id = c.id
                         ORDER  BY l.reviewed_utc DESC
                         LIMIT  1)                                                      AS last_wrong_utc
                 FROM   flashcards c
                 WHERE  c.is_suspended = 0
                   AND  {scope}
             )
             SELECT card_id
             FROM   eligible
             {filter}
             {order}
             LIMIT  @Max;
             """,
            parameters,
            session.DbTransaction, cancellationToken: cancellationToken));

        return [.. ids];
    }
}
