using Flashcards.Infrastructure.Persistence.Rows;
using Dapper;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Domain.Subjects;

namespace Flashcards.Infrastructure.Persistence.Repositories;

internal sealed class SubjectRepository(DbSession session) : ISubjectRepository
{
    /// <summary>
    /// The column list, written once. Every read here returns a whole subject, and keeping the
    /// projection in one place is what stops a new column reaching some queries and not others —
    /// which is exactly how <c>parent_id</c> could have shipped half-applied.
    /// </summary>
    private const string Columns =
        """
        SELECT id          AS Id,
               name        AS Name,
               color_hex   AS ColorHex,
               description AS Description,
               created_utc AS CreatedUtc,
               parent_id   AS ParentId
        FROM   subjects
        """;

    public async Task<Subject?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<SubjectRow>(new CommandDefinition(
            $"{Columns} WHERE id = @Id;",
            new { Id = id }, session.DbTransaction, cancellationToken: cancellationToken));

        return row?.ToDomain();
    }

    public async Task<Subject?> GetByNameAsync(string name, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);

        // The column is declared COLLATE NOCASE, so this comparison is already
        // case-insensitive without a LOWER() wrapper — and unlike LOWER(name) = @Name
        // it can still use ux_subjects_name.
        var row = await connection.QuerySingleOrDefaultAsync<SubjectRow>(new CommandDefinition(
            $"{Columns} WHERE name = @Name;",
            new { Name = name }, session.DbTransaction, cancellationToken: cancellationToken));

        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<Subject>> GetAllAsync(CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);

        // Flat and name-ordered on purpose. Assembling the tree is SubjectHierarchy's job, and it
        // needs the whole set anyway to validate a move.
        var rows = await connection.QueryAsync<SubjectRow>(new CommandDefinition(
            $"{Columns} ORDER BY name COLLATE NOCASE;",
            transaction: session.DbTransaction, cancellationToken: cancellationToken));

        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task AddAsync(Subject subject, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO subjects (id, name, color_hex, description, created_utc, parent_id)
            VALUES (@Id, @Name, @ColorHex, @Description, @CreatedUtc, @ParentId);
            """,
            new { subject.Id, subject.Name, subject.ColorHex, subject.Description, subject.CreatedUtc, subject.ParentId },
            session.DbTransaction, cancellationToken: cancellationToken));
    }

    public async Task UpdateAsync(Subject subject, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE subjects
            SET    name        = @Name,
                   color_hex   = @ColorHex,
                   description = @Description,
                   parent_id   = @ParentId
            WHERE  id = @Id;
            """,
            new { subject.Id, subject.Name, subject.ColorHex, subject.Description, subject.ParentId },
            session.DbTransaction, cancellationToken: cancellationToken));
    }

    public async Task DeleteAsync(Guid id, Guid? promoteTo, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);

        // Child subjects first, and in the same transaction. The column's own ON DELETE SET NULL
        // would scatter them to the top level instead of leaving them where their parent was, so
        // the explicit re-point has to win the race — which it does by happening before the delete.
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE subjects SET parent_id = @NewParent WHERE parent_id = @Id;",
            new { Id = id, NewParent = promoteTo },
            session.DbTransaction, cancellationToken: cancellationToken));

        if (promoteTo is not null)
        {
            // Then the cards, the same way. Without this they would fall through the join table's
            // cascade and end up wearing nothing, which the domain does not allow a card to be.
            //
            // OR IGNORE because a card may already wear the parent as well — "MSSQL" and "SQL"
            // together is legal — and the primary key would otherwise reject the duplicate.
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT OR IGNORE INTO card_subjects (card_id, subject_id)
                SELECT cs.card_id, @NewParent
                FROM   card_subjects cs
                WHERE  cs.subject_id = @Id;
                """,
                new { Id = id, NewParent = promoteTo },
                session.DbTransaction, cancellationToken: cancellationToken));
        }

        // What is left in card_subjects for this subject cascades away with the row.
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM subjects WHERE id = @Id;",
            new { Id = id },
            session.DbTransaction, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<string>> FindCardsOrphanedByDeleteAsync(
        Guid id,
        Guid? promoteTo,
        CancellationToken cancellationToken)
    {
        // With somewhere to promote to, every card keeps a subject by construction, so there is
        // nothing to look for and no reason to ask the database.
        if (promoteTo is not null)
        {
            return [];
        }

        var connection = await session.GetConnectionAsync(cancellationToken);

        // "Wears this subject and nothing else." NOT EXISTS rather than a count, so it stops at the
        // first other tag instead of tallying them all.
        var names = await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT   c.name
            FROM     flashcards    c
            JOIN     card_subjects cs ON cs.card_id = c.id AND cs.subject_id = @Id
            WHERE    NOT EXISTS (SELECT 1
                                 FROM   card_subjects other
                                 WHERE  other.card_id = c.id
                                   AND  other.subject_id <> @Id)
            ORDER BY c.name COLLATE NOCASE;
            """,
            new { Id = id },
            session.DbTransaction, cancellationToken: cancellationToken));

        return [.. names];
    }
}
