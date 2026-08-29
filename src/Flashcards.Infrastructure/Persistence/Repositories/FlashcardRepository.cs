using Flashcards.Infrastructure.Persistence.Rows;
using Dapper;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Domain.Cards;

namespace Flashcards.Infrastructure.Persistence.Repositories;

/// <summary>
/// Write side. Loads and saves the whole <see cref="Flashcard"/> aggregate.
/// <para>
/// Updates use delete-then-insert for the child collections. With an aggregate this small
/// (at most 24 blocks and 8 choices) a diff would be more code and more bugs for no measurable
/// gain, and it guarantees ordinals end up exactly as the domain arranged them.
/// </para>
/// </summary>
internal sealed class FlashcardRepository(DbSession session) : IFlashcardRepository
{
    public async Task<Flashcard?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);

        // Dapper's QueryMultiple sends several statements on one round trip and hands back a
        // reader you step through in order. [T-SQL] Identical usage to SQL Server; SQLite is
        // perfectly happy with a multi-statement batch here.
        using var multi = await connection.QueryMultipleAsync(new CommandDefinition(
            """
            SELECT id           AS Id,
                   name         AS Name,
                   card_type    AS CardType,
                   notes        AS Notes,
                   is_suspended AS IsSuspended,
                   created_utc  AS CreatedUtc,
                   updated_utc  AS UpdatedUtc
            FROM   flashcards
            WHERE  id = @Id;

            SELECT subject_id AS SubjectId
            FROM   card_subjects
            WHERE  card_id = @Id;

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

        var card = await multi.ReadSingleOrDefaultAsync<CardRow>();

        if (card is null)
        {
            return null;
        }

        // Read in the order the batch declares them, even when the card turned out to be missing
        // above — QueryMultiple is a forward-only reader, not a random-access result set.
        var subjectIds = (await multi.ReadAsync<Guid>()).ToList();
        var blocks = (await multi.ReadAsync<BlockRow>()).ToList();
        var choices = (await multi.ReadAsync<ChoiceRow>()).ToList();

        return Flashcard.Rehydrate(
            card.Id,
            subjectIds,
            card.Name,
            (CardType)card.CardType,
            card.Notes,
            card.IsSuspended != 0,
            card.CreatedUtc,
            card.UpdatedUtc,
            blocks.Select(b => ContentBlock.Rehydrate(
                b.Id, (CardFace)b.Face, b.Ordinal, (ContentKind)b.Kind, b.Text, b.Language,
                b.MediaId, (ImageStretch)b.Stretch, b.MaxHeight, b.AltText, ToBounds(b))),
            choices.Select(c => ChoiceOption.Rehydrate(c.Id, c.Ordinal, c.Text, c.IsCorrect != 0, c.MediaId)));
    }

    public async Task AddAsync(Flashcard card, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO flashcards (id, name, card_type, notes, is_suspended, created_utc, updated_utc)
            VALUES (@Id, @Name, @CardType, @Notes, @IsSuspended, @CreatedUtc, @UpdatedUtc);
            """,
            new
            {
                card.Id,
                card.Name,
                CardType = (int)card.CardType,
                card.Notes,
                IsSuspended = card.IsSuspended ? 1 : 0,
                card.CreatedUtc,
                card.UpdatedUtc,
            },
            session.DbTransaction, cancellationToken: cancellationToken));

        await WriteChildrenAsync(connection, card, cancellationToken);
    }

    public async Task UpdateAsync(Flashcard card, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE flashcards
            SET    name         = @Name,
                   card_type    = @CardType,
                   notes        = @Notes,
                   is_suspended = @IsSuspended,
                   updated_utc  = @UpdatedUtc
            WHERE  id = @Id;
            """,
            new
            {
                card.Id,
                card.Name,
                CardType = (int)card.CardType,
                card.Notes,
                IsSuspended = card.IsSuspended ? 1 : 0,
                card.UpdatedUtc,
            },
            session.DbTransaction, cancellationToken: cancellationToken));

        if (affected == 0)
        {
            throw new InvalidOperationException($"Card {card.Id} was not found; it may have been deleted elsewhere.");
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM card_blocks   WHERE card_id = @Id;
            DELETE FROM card_choices  WHERE card_id = @Id;
            DELETE FROM card_subjects WHERE card_id = @Id;
            """,
            new { card.Id }, session.DbTransaction, cancellationToken: cancellationToken));

        await WriteChildrenAsync(connection, card, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);

        // Children go by ON DELETE CASCADE — provided PRAGMA foreign_keys = ON, which the
        // connection factory sets on every connection. This is the single easiest thing to get
        // wrong when moving from SQL Server, where cascades are always live.
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM flashcards WHERE id = @Id;",
            new { Id = id }, session.DbTransaction, cancellationToken: cancellationToken));
    }

    public async Task<bool> ExistsWithNameAsync(
        IReadOnlyCollection<Guid> subjectIds,
        string name,
        Guid? excludingId,
        CancellationToken cancellationToken)
    {
        if (subjectIds.Count == 0)
        {
            return false;
        }

        var connection = await session.GetConnectionAsync(cancellationToken);

        // [T-SQL] SQLite has no EXISTS(...) that returns a bit; you SELECT the EXISTS expression,
        // which evaluates to INTEGER 0/1. `@Excluding IS NULL OR id <> @Excluding` is the same
        // optional-parameter trick you would use in T-SQL, and carries the same caveat: it can
        // spoil index selection. On a table this size it does not matter.
        //
        // The join makes this "shares any tag with the card being saved", which is the
        // multi-tag reading of the old unique index on (subject_id, name).
        var found = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT EXISTS (
                SELECT 1
                FROM       flashcards    c
                INNER JOIN card_subjects cs ON cs.card_id = c.id
                WHERE      cs.subject_id IN @SubjectIds
                  AND      c.name = @Name
                  AND      (@Excluding IS NULL OR c.id <> @Excluding)
            );
            """,
            new { SubjectIds = subjectIds.ToArray(), Name = name.Trim(), Excluding = excludingId },
            session.DbTransaction, cancellationToken: cancellationToken));

        return found != 0;
    }

    public async Task<Guid?> FindIdByNameAsync(
        IReadOnlyCollection<Guid> subjectIds,
        string name,
        CancellationToken cancellationToken)
    {
        if (subjectIds.Count == 0)
        {
            return null;
        }

        var connection = await session.GetConnectionAsync(cancellationToken);

        // The same join as ExistsWithNameAsync, returning the row instead of a flag. DISTINCT
        // because a card wearing two of the given tags joins twice; LIMIT 1 because the library
        // only ever holds one card of a given name per tag, so a second row would be a bug
        // upstream rather than something to report here.
        return await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            """
            SELECT DISTINCT c.id
            FROM       flashcards    c
            INNER JOIN card_subjects cs ON cs.card_id = c.id
            WHERE      cs.subject_id IN @SubjectIds
              AND      c.name = @Name
            LIMIT      1;
            """,
            new { SubjectIds = subjectIds.ToArray(), Name = name.Trim() },
            session.DbTransaction, cancellationToken: cancellationToken));
    }

    private async Task WriteChildrenAsync(Microsoft.Data.Sqlite.SqliteConnection connection, Flashcard card, CancellationToken cancellationToken)
    {
        // Tags first: the aggregate guarantees at least one, so this always writes a row.
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO card_subjects (card_id, subject_id) VALUES (@CardId, @SubjectId);",
            card.SubjectIds.Select(id => new { CardId = card.Id, SubjectId = id }).ToArray(),
            session.DbTransaction, cancellationToken: cancellationToken));

        if (card.Blocks.Count > 0)
        {
            // Passing an IEnumerable as the parameter object makes Dapper execute the statement
            // once per item on the same prepared command. [T-SQL] The closest analogue is a
            // table-valued parameter, except this is a real loop — for thousands of rows you
            // would batch multi-row VALUES instead.
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO card_blocks (id, card_id, face, ordinal, kind, text, language, media_id, stretch, max_height, alt_text, x, y, width, height)
                VALUES (@Id, @CardId, @Face, @Ordinal, @Kind, @Text, @Language, @MediaId, @Stretch, @MaxHeight, @AltText, @X, @Y, @Width, @Height);
                """,
                card.Blocks.Select(b => new
                {
                    b.Id,
                    CardId = card.Id,
                    Face = (int)b.Face,
                    b.Ordinal,
                    Kind = (int)b.Kind,
                    b.Text,
                    b.Language,
                    b.MediaId,
                    Stretch = (int)b.Stretch,
                    b.MaxHeight,
                    b.AltText,
                    // All four are null together for a flow-laid-out block.
                    X = b.Bounds?.X,
                    Y = b.Bounds?.Y,
                    Width = b.Bounds?.Width,
                    Height = b.Bounds?.Height,
                }).ToArray(),
                session.DbTransaction, cancellationToken: cancellationToken));
        }

        if (card.Choices.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO card_choices (id, card_id, ordinal, text, is_correct, media_id)
                VALUES (@Id, @CardId, @Ordinal, @Text, @IsCorrect, @MediaId);
                """,
                card.Choices.Select(c => new
                {
                    c.Id,
                    CardId = card.Id,
                    c.Ordinal,
                    c.Text,
                    IsCorrect = c.IsCorrect ? 1 : 0,
                    c.MediaId,
                }).ToArray(),
                session.DbTransaction, cancellationToken: cancellationToken));
        }
    }

    /// <summary>
    /// Geometry is present only for designed cards. Requiring all four columns guards against a
    /// half-written row placing an element at an undefined size.
    /// </summary>
    private static BlockBounds? ToBounds(BlockRow row)
        => row is { X: { } x, Y: { } y, Width: { } width, Height: { } height }
            ? new BlockBounds(x, y, width, height)
            : null;
}
