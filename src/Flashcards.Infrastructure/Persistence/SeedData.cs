using Dapper;
using Flashcards.Domain.Cards;
using Flashcards.Domain.Subjects;
using Flashcards.Infrastructure.Persistence.Repositories;

namespace Flashcards.Infrastructure.Persistence;

/// <summary>
/// Puts a handful of cards in the database the first time the app runs, so every panel has
/// something to show. Deliberately uses one card of each type, including one with a code block.
/// </summary>
public sealed class SeedData(IDbConnectionFactory factory)
{
    public async Task EnsureSeededAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenAsync(cancellationToken);

        var subjectCount = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM subjects;");

        if (subjectCount > 0)
        {
            return;
        }

        await using var session = new DbSession(factory);
        var subjects = new SubjectRepository(session);
        var cards = new FlashcardRepository(session);

        var transaction = await session.BeginTransactionAsync(cancellationToken);

        try
        {
            var sqlite = Subject.Create("SQLite vs T-SQL", "#4C9AFF", "Dialect differences worth remembering");
            var dotnet = Subject.Create("Modern .NET", "#7A5AF8", "Language and runtime features");
            var databases = Subject.Create("Databases", "#22C55E", "Storage engines and query planning");
            await subjects.AddAsync(sqlite, cancellationToken);
            await subjects.AddAsync(dotnet, cancellationToken);
            await subjects.AddAsync(databases, cancellationToken);

            // Two of these wear both tags — the seed doubles as a demonstration that a card can
            // sit under more than one subject.
            var upsert = Flashcard.Create([sqlite.Id, databases.Id], "Upsert syntax", CardType.Standard);
            upsert.AddTextBlock(CardFace.Question, ContentKind.Markdown,
                "SQL Server has `MERGE`. **What is SQLite's equivalent**, and what is the pseudo-table called?");
            upsert.AddTextBlock(CardFace.Answer, ContentKind.Markdown,
                "`INSERT ... ON CONFLICT (target) DO UPDATE SET ...`\n\nThe pseudo-table holding the would-be-inserted row is called **excluded**.");
            upsert.AddTextBlock(CardFace.Answer, ContentKind.Code,
                "INSERT INTO card_search (card_id, search_text)\nVALUES (@CardId, @Text)\nON CONFLICT (card_id) DO UPDATE SET\n    search_text = excluded.search_text;", "sql");

            var fks = Flashcard.Create([sqlite.Id, databases.Id], "Foreign key enforcement", CardType.MultipleChoice);
            fks.AddTextBlock(CardFace.Question, ContentKind.PlainText,
                "A SQLite table declares ON DELETE CASCADE. You delete the parent row and the children survive. Why?");
            fks.AddTextBlock(CardFace.Answer, ContentKind.Markdown,
                "Foreign keys are enforced **per connection** and default to OFF. Run `PRAGMA foreign_keys = ON;` on every connection you open.");
            fks.ReplaceChoices(
            [
                ChoiceOption.Create(0, "ON DELETE CASCADE is not supported in SQLite", false),
                ChoiceOption.Create(1, "PRAGMA foreign_keys was not enabled on that connection", true),
                ChoiceOption.Create(2, "The child table was missing an index", false),
                ChoiceOption.Create(3, "WAL mode disables cascades", false),
            ]);

            var paging = Flashcard.Create([sqlite.Id], "Paging syntax", CardType.Cloze);
            paging.AddTextBlock(CardFace.Question, ContentKind.PlainText,
                "T-SQL pages with OFFSET n ROWS FETCH NEXT m ROWS ONLY. SQLite writes the same thing as {{LIMIT}} m {{OFFSET}} n.");
            paging.AddTextBlock(CardFace.Answer, ContentKind.PlainText,
                "SQLite also accepts the MySQL-style LIMIT n, m — avoid it, the argument order is reversed and it reads badly.");

            var guids = Flashcard.Create([dotnet.Id], "Guid.CreateVersion7", CardType.Standard);
            guids.AddTextBlock(CardFace.Question, ContentKind.Markdown,
                "What does `Guid.CreateVersion7()` give you that `Guid.NewGuid()` does not?");
            guids.AddTextBlock(CardFace.Answer, ContentKind.Markdown,
                "A **time-ordered** UUID. The leading 48 bits are a Unix millisecond timestamp, so newly created ids sort in creation order — which keeps a clustered or primary-key index appending at the end instead of fragmenting with random inserts.");

            foreach (var card in new[] { upsert, fks, paging, guids })
            {
                await cards.AddAsync(card, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            session.ClearTransaction();
        }
    }
}
