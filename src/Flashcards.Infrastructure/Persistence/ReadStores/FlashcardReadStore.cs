using System.Text;
using Flashcards.Domain.Cards;
using Dapper;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Application.Contracts;
using Flashcards.Infrastructure.Persistence.Rows;
using Flashcards.Infrastructure.Persistence.Sql;

namespace Flashcards.Infrastructure.Persistence.ReadStores;

/// <summary>
/// The read side of the card library: the management grid's search, and the one query that loads a
/// whole card for the designer.
/// <para>
/// No aggregates are constructed here — SQL joins, aggregates and window functions produce exactly
/// the shape each screen binds to, in one round trip. That is the half of CQRS that pays for
/// itself: no ORM materialising an object graph you are about to flatten anyway.
/// </para>
/// </summary>
internal sealed class FlashcardReadStore(DbSession session) : IFlashcardReadStore
{
    // A card's answer tally, correlated against the outer `c`. Spelled out once and reused in both
    // the SELECT list and the ORDER BY: SQLite will resolve a bare output alias in a simple ORDER
    // BY term, but not reliably inside a larger expression, and repeating the subquery is cheaper
    // than the bug that would cause.
    private const string AnsweredExpression =
        "(SELECT COUNT(*) FROM review_log l WHERE l.card_id = c.id)";

    private const string CorrectExpression =
        "(SELECT COALESCE(SUM(l.was_correct), 0) FROM review_log l WHERE l.card_id = c.id)";

    public async Task<PagedResult<FlashcardSummary>> SearchAsync(
        FlashcardSearchCriteria criteria,
        CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);

        var where = new StringBuilder("WHERE 1 = 1");
        var parameters = new DynamicParameters();

        if (!string.IsNullOrWhiteSpace(criteria.Text))
        {
            // LIKE in SQLite is case-insensitive for ASCII by default (PRAGMA case_sensitive_like
            // is OFF), which is the opposite of `=` on a column without COLLATE NOCASE. Worth
            // knowing, because it means LIKE and = disagree about case on the same column.
            //
            // ESCAPE is spelled the same as T-SQL, but the wildcards a user might type are only
            // % and _ — SQLite's LIKE has no [] character-class wildcard, so there is less to escape.
            where.Append(" AND (c.name LIKE @Like ESCAPE '\\' OR cs.search_text LIKE @Like ESCAPE '\\')");
            parameters.Add("Like", "%" + Escape(criteria.Text.Trim()) + "%");
        }

        // Selecting a subject selects everything under it, so the ticked ids are widened through
        // the closure before they are matched. Ticking "SQL" finds cards tagged only "MSSQL".
        var needsClosure = criteria.SubjectIds is { Count: > 0 };

        if (criteria.SubjectIds is { Count: > 0 })
        {
            // Built conditionally rather than as `(@None = 1 OR ...)`: the OR form defeats the
            // index on card_subjects, which is the classic "catch-all query" problem you also hit
            // in T-SQL (where the usual escape hatch is OPTION (RECOMPILE)).
            //
            // EXISTS rather than a join, because a card wearing three of the ticked tags must
            // still come back once — a join would duplicate it per match.
            where.Append(
                """
                 AND EXISTS (SELECT 1 FROM card_subjects csub
                             WHERE csub.card_id = c.id
                               AND csub.subject_id IN (SELECT cl.descendant
                                                       FROM   subject_closure cl
                                                       WHERE  cl.ancestor IN @SubjectIds))
                """);
            parameters.Add("SubjectIds", criteria.SubjectIds.ToArray());
        }

        if (criteria.CardType is { } cardType)
        {
            where.Append(" AND c.card_type = @CardType");
            parameters.Add("CardType", (int)cardType);
        }

        if (criteria.IsSuspended is { } suspended)
        {
            where.Append(" AND c.is_suspended = @IsSuspended");
            parameters.Add("IsSuspended", suspended ? 1 : 0);
        }

        if (criteria.UntouchedOnly)
        {
            where.Append(" AND NOT EXISTS (SELECT 1 FROM review_log l WHERE l.card_id = c.id)");
        }

        var pageSize = Math.Clamp(criteria.PageSize, 1, 500);
        var page = Math.Max(criteria.Page, 1);

        parameters.Add("Take", pageSize);
        parameters.Add("Skip", (page - 1) * pageSize);

        // ORDER BY comes from a whitelist. Never interpolate a user string into ORDER BY;
        // parameters cannot bind identifiers in SQLite any more than they can in T-SQL.
        var orderColumn = criteria.SortBy switch
        {
            FlashcardSortField.Name => "c.name COLLATE NOCASE",
            // A card has several tags now, so "sort by subject" means its alphabetically first.
            FlashcardSortField.SubjectName =>
                "(SELECT MIN(s2.name) FROM card_subjects cs2 " +
                " JOIN subjects s2 ON s2.id = cs2.subject_id WHERE cs2.card_id = c.id) COLLATE NOCASE",
            FlashcardSortField.CreatedUtc => "c.created_utc",
            FlashcardSortField.TimesAnswered => AnsweredExpression,
            // Never-answered cards sort as -1 so they group at one end rather than masquerading
            // as a perfect 100% or a dismal 0%, either of which would be a lie.
            FlashcardSortField.Accuracy =>
                $"CASE WHEN {AnsweredExpression} = 0 THEN -1.0 " +
                $"ELSE CAST({CorrectExpression} AS REAL) / {AnsweredExpression} END",
            _ => "c.updated_utc",
        };

        var direction = criteria.SortDescending ? "DESC" : "ASC";

        // The CTE only rides along when something actually filters by subject: a recursive walk of
        // the whole subject table is wasted work on an unfiltered search.
        var sql =
            $"""
             {(needsClosure ? SubjectClosure.Cte : string.Empty)}
             SELECT c.id           AS Id,
                    c.name         AS Name,
                    c.card_type    AS CardType,
                    c.is_suspended AS IsSuspended,
                    -- SUBSTR is 1-indexed, like T-SQL's SUBSTRING. There is no LEFT()/RIGHT() in
                    -- SQLite; SUBSTR(x, 1, n) is how you spell LEFT(x, n).
                    COALESCE(SUBSTR(cs.search_text, 1, 180), '') AS QuestionPreview,
                    (SELECT COUNT(*) FROM card_blocks b WHERE b.card_id = c.id) AS BlockCount,
                    EXISTS (SELECT 1 FROM card_blocks b WHERE b.card_id = c.id AND b.kind = 3) AS HasImages,
                    c.updated_utc  AS UpdatedUtc,
                    {AnsweredExpression} AS Answered,
                    {CorrectExpression}  AS Correct,
                    -- The whole-result count without a second query. Window functions arrived in
                    -- SQLite 3.25 and the syntax is identical to T-SQL's COUNT(*) OVER ().
                    -- They are evaluated before LIMIT, so this is the unpaged total.
                    COUNT(*) OVER () AS TotalCount
             FROM       flashcards    c
             LEFT  JOIN card_search   cs ON cs.card_id = c.id
             {where}
             ORDER BY {orderColumn} {direction}, c.name COLLATE NOCASE ASC
             -- [T-SQL] OFFSET n ROWS FETCH NEXT m ROWS ONLY becomes LIMIT m OFFSET n.
             -- SQLite does not require an ORDER BY for LIMIT, but you should always have one.
             LIMIT @Take OFFSET @Skip;
             """;

        var rows = (await connection.QueryAsync<SummaryRow>(new CommandDefinition(
            sql, parameters, session.DbTransaction, cancellationToken: cancellationToken))).ToList();

        // Tags come back in a second pass keyed on the page's ids rather than as a join on the
        // main query. Joining would multiply every card row by its tag count and break both the
        // LIMIT and the COUNT(*) OVER () total; GROUP_CONCAT would avoid that but hand back a
        // string to re-parse. One extra round trip for at most `pageSize` cards is the cheaper trade.
        var tags = await LoadSubjectsForCardsAsync(
            connection, rows.ConvertAll(r => r.Id), cancellationToken);

        var items = rows.ConvertAll(r => new FlashcardSummary(
            r.Id, r.Name,
            tags.TryGetValue(r.Id, out var subjects) ? subjects : [],
            (CardType)r.CardType, r.IsSuspended != 0, r.QuestionPreview, r.BlockCount,
            r.HasImages != 0, r.UpdatedUtc, new PracticeStats(r.Answered, r.Correct)));

        var total = rows.Count > 0 ? rows[0].TotalCount : 0;

        return new PagedResult<FlashcardSummary>(items, total, page, pageSize);
    }

    public async Task<FlashcardDetail?> GetDetailAsync(Guid id, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);

        using var multi = await connection.QueryMultipleAsync(new CommandDefinition(
            """
            SELECT c.id           AS Id,
                   c.name         AS Name,
                   c.card_type    AS CardType,
                   c.notes        AS Notes,
                   c.is_suspended AS IsSuspended,
                   c.created_utc  AS CreatedUtc,
                   c.updated_utc  AS UpdatedUtc
            FROM   flashcards c
            WHERE  c.id = @Id;

            SELECT id         AS Id,
                   face       AS Face,
                   ordinal    AS Ordinal,
                   kind       AS Kind,
                   text       AS Text,
                   language   AS Language,
                   media_id   AS MediaId,
                   stretch    AS Stretch,
                   max_height AS MaxHeight,
                   alt_text   AS AltText,
                   x          AS X,
                   y          AS Y,
                   width      AS Width,
                   height     AS Height
            FROM   card_blocks
            WHERE  card_id = @Id
            ORDER  BY face, ordinal;

            SELECT id         AS Id,
                   ordinal    AS Ordinal,
                   text       AS Text,
                   is_correct AS IsCorrect,
                   media_id   AS MediaId
            FROM   card_choices
            WHERE  card_id = @Id
            ORDER  BY ordinal;
            """,
            new { Id = id }, session.DbTransaction, cancellationToken: cancellationToken));

        var head = await multi.ReadSingleOrDefaultAsync<DetailRow>();

        if (head is null)
        {
            return null;
        }

        var blocks = (await multi.ReadAsync<BlockRow>())
            .Select(b => new ContentBlockDto(
                b.Id, (CardFace)b.Face, b.Ordinal, (ContentKind)b.Kind, b.Text, b.Language,
                b.MediaId, (ImageStretch)b.Stretch, b.MaxHeight, b.AltText,
                b.X, b.Y, b.Width, b.Height))
            .ToList();

        var choices = (await multi.ReadAsync<ChoiceRow>())
            .Select(c => new ChoiceDto(c.Id, c.Ordinal, c.Text, c.IsCorrect != 0, c.MediaId))
            .ToList();

        var tags = await LoadSubjectsForCardsAsync(connection, [head.Id], cancellationToken);

        return new FlashcardDetail(
            head.Id,
            tags.TryGetValue(head.Id, out var subjects) ? subjects : [],
            head.Name, (CardType)head.CardType,
            head.Notes, head.IsSuspended != 0, head.CreatedUtc, head.UpdatedUtc, blocks, choices);
    }

    /// <summary>
    /// The tags worn by each of these cards, keyed by card id. Returns an empty map for an empty
    /// input rather than issuing a query with an empty IN list, which SQLite rejects.
    /// </summary>
    private async Task<Dictionary<Guid, IReadOnlyList<SubjectRef>>> LoadSubjectsForCardsAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        IReadOnlyCollection<Guid> cardIds,
        CancellationToken cancellationToken)
    {
        if (cardIds.Count == 0)
        {
            return [];
        }

        // A card wears the tag it was given plus every ancestor of that tag, so the closure is
        // walked in the *opposite* direction here: for each applied tag, find the subjects it sits
        // under. Tagging a card "MSSQL" is what makes it answer to "SQL" and "Databases".
        //
        // MIN(...) = 0 over the group is how a subject that is both applied directly and reachable
        // as an ancestor reports as applied: it is a real tag the user can remove, and the fact
        // that it is also implied by another tag does not take that away.
        var rows = await connection.QueryAsync<CardSubjectRow>(new CommandDefinition(
            $"""
             {SubjectClosure.Cte}
             SELECT   cs.card_id                              AS CardId,
                      s.id                                    AS Id,
                      s.name                                  AS Name,
                      s.color_hex                             AS ColorHex,
                      MIN(CASE WHEN cl.ancestor = cs.subject_id THEN 0 ELSE 1 END) AS IsInherited
             FROM     card_subjects   cs
             JOIN     subject_closure cl ON cl.descendant = cs.subject_id
             JOIN     subjects        s  ON s.id = cl.ancestor
             WHERE    cs.card_id IN @CardIds
             GROUP BY cs.card_id, s.id, s.name, s.color_hex
             -- Ancestors before the tags beneath them, so the chips read as a path.
             ORDER BY (SELECT COUNT(*) FROM subject_closure d WHERE d.descendant = s.id),
                      s.name COLLATE NOCASE;
             """,
            new { CardIds = cardIds.ToArray() },
            session.DbTransaction, cancellationToken: cancellationToken));

        return rows
            .GroupBy(r => r.CardId)
            .ToDictionary(
                g => g.Key,
                IReadOnlyList<SubjectRef> (g) =>
                    [.. g.Select(r => new SubjectRef(r.Id, r.Name, r.ColorHex, r.IsInherited != 0))]);
    }

    /// <summary>Escapes the LIKE metacharacters so a user typing "50%" searches for a literal 50%.</summary>
    private static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
