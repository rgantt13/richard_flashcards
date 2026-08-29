using Dapper;
using System.Reflection;
using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Cards.Queries;
using Flashcards.Application.Stats.Queries;
using Flashcards.Application.Subjects.Queries;
using Flashcards.Application;
using Flashcards.Infrastructure;
using Flashcards.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Flashcards.Integration.Tests;

/// <summary>
/// Upgrade tests, as opposed to the fresh-install path every other test exercises.
/// <para>
/// A migration that only ever runs against an empty database is barely tested: the interesting
/// work in migration 004 is copying existing <c>flashcards.subject_id</c> values into the new join
/// table before dropping the column, and a fresh database has no rows to copy. This builds a
/// database at the pre-004 schema, puts real data in it, and only then runs the migrator.
/// </para>
/// </summary>
public sealed class MigrationUpgradeTests
{
    /// <summary>Runs the embedded migration scripts up to (and including) <paramref name="throughVersion"/>.</summary>
    private static async Task BuildLegacySchemaAsync(SqliteConnection connection, int throughVersion)
    {
        var assembly = typeof(DatabaseInitializer).Assembly;

        var scripts = assembly.GetManifestResourceNames()
            .Where(n => n.Contains(".Migrations.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        await Execute(connection,
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version     INTEGER NOT NULL PRIMARY KEY,
                name        TEXT    NOT NULL,
                applied_utc TEXT    NOT NULL
            );
            """);

        foreach (var resource in scripts)
        {
            var fileName = resource[(resource.LastIndexOf(".Migrations.", StringComparison.Ordinal) + ".Migrations.".Length)..];
            var digits = new string(fileName.SkipWhile(char.IsLetter).TakeWhile(char.IsDigit).ToArray());

            if (!int.TryParse(digits, out var version) || version > throughVersion)
            {
                continue;
            }

            await using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);

            await Execute(connection, await reader.ReadToEndAsync());
            await Execute(connection,
                $"INSERT INTO schema_migrations (version, name, applied_utc) VALUES ({version}, '{fileName}', '{DateTimeOffset.UtcNow:O}');");
        }
    }

    private static async Task Execute(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Migration_006_keeps_the_answer_history_when_scheduling_is_dropped()
    {
        var root = Path.Combine(Path.GetTempPath(), "flashcards-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "flashcards.db");

        var subjectId = Guid.CreateVersion7();
        var cardId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow.ToString("O");

        var subjectKey = subjectId.ToString("D").ToUpperInvariant();
        var cardKey = cardId.ToString("D").ToUpperInvariant();

        // ---- a database from before statistics replaced scheduling ----
        await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            await connection.OpenAsync();
            await Execute(connection, "PRAGMA foreign_keys = ON;");
            await BuildLegacySchemaAsync(connection, throughVersion: 5);

            await Execute(connection,
                $"""
                 INSERT INTO subjects (id, name, color_hex, description, created_utc)
                 VALUES ('{subjectKey}', 'Legacy tag', '#4C9AFF', NULL, '{now}');

                 INSERT INTO flashcards (id, name, card_type, notes, is_suspended, created_utc, updated_utc)
                 VALUES ('{cardKey}', 'Legacy card', 0, NULL, 0, '{now}', '{now}');

                 INSERT INTO card_subjects (card_id, subject_id) VALUES ('{cardKey}', '{subjectKey}');

                 -- Four reviews on the old four-point scale, none with was_correct filled in.
                 -- Again(0) is a failure; Hard(3), Good(4) and Easy(5) are all successful recalls.
                 INSERT INTO review_log (card_id, reviewed_utc, grade, prior_interval_days, new_interval_days, ease_after, elapsed_ms, was_correct)
                 VALUES ('{cardKey}', '{now}', 0, 0, 0.007, 2.5, 1000, NULL),
                        ('{cardKey}', '{now}', 3, 0, 1,     2.4, 2000, NULL),
                        ('{cardKey}', '{now}', 4, 1, 3,     2.5, 3000, NULL),
                        ('{cardKey}', '{now}', 5, 3, 8,     2.6, 4000, NULL);

                 -- And one row that already recorded the answer explicitly, disagreeing with its
                 -- grade: the explicit value must win.
                 INSERT INTO review_log (card_id, reviewed_utc, grade, prior_interval_days, new_interval_days, ease_after, elapsed_ms, was_correct)
                 VALUES ('{cardKey}', '{now}', 5, 8, 20, 2.7, 5000, 0);
                 """);
        }

        SqliteConnection.ClearAllPools();

        // ---- upgrade it ----
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddApplication();
        services.AddInfrastructure(new StoragePaths(root));

        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<DatabaseInitializer>().MigrateAsync();

        var stats = await provider.GetRequiredService<IDispatcher>()
            .QueryAsync(new GetCardStatsQuery(cardId));

        // Five answers survive. Three recovered as correct (Hard/Good/Easy), one as wrong
        // (Again), and the explicitly-wrong row stays wrong despite its Easy grade.
        stats.Practice.Answered.ShouldBe(5);
        stats.Practice.Correct.ShouldBe(3);
        stats.Practice.Wrong.ShouldBe(2);

        // Timings come across untouched: (1+2+3+4+5)/5 seconds.
        stats.AverageSeconds!.Value.ShouldBe(3, 0.001);

        await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            await connection.OpenAsync();

            async Task<long> CountAsync(string sql)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                return Convert.ToInt64(await command.ExecuteScalarAsync());
            }

            // The scheduling table is gone, and so are the scheduling columns.
            (await CountAsync("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='review_states';"))
                .ShouldBe(0, "review_states table");
            (await CountAsync("SELECT COUNT(*) FROM pragma_table_info('review_log') WHERE name IN ('grade','ease_after','prior_interval_days','new_interval_days');"))
                .ShouldBe(0, "scheduling columns on review_log");
            (await CountAsync("SELECT COUNT(*) FROM review_log WHERE was_correct IS NULL;"))
                .ShouldBe(0, "rows still missing an outcome");
        }

        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leaked handle on Windows should not fail the test run.
        }
    }

    [Fact]
    public async Task Migration_004_moves_existing_single_subject_cards_onto_the_join_table()
    {
        var root = Path.Combine(Path.GetTempPath(), "flashcards-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "flashcards.db");

        var subjectId = Guid.CreateVersion7();
        var cardId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow.ToString("O");

        // Ids go in the same casing Microsoft.Data.Sqlite writes them (see GuidStorageTests):
        // SQLite compares TEXT case-sensitively, so lower case here would be invisible to the app.
        var cardKey = cardId.ToString("D").ToUpperInvariant();
        var subjectKey = subjectId.ToString("D").ToUpperInvariant();

        // ---- a database as it existed before multi-tagging ----
        await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            await connection.OpenAsync();
            await Execute(connection, "PRAGMA foreign_keys = ON;");
            await BuildLegacySchemaAsync(connection, throughVersion: 3);

            await Execute(connection,
                $"""
                 INSERT INTO subjects (id, name, color_hex, description, created_utc)
                 VALUES ('{subjectKey}', 'Legacy tag', '#4C9AFF', NULL, '{now}');

                 INSERT INTO flashcards (id, subject_id, name, card_type, notes, is_suspended, created_utc, updated_utc)
                 VALUES ('{cardKey}', '{subjectKey}', 'Legacy card', 0, NULL, 0, '{now}', '{now}');

                 INSERT INTO card_blocks (id, card_id, face, ordinal, kind, text, language, media_id, stretch, max_height, alt_text)
                 VALUES ('{Guid.CreateVersion7().ToString("D").ToUpperInvariant()}', '{cardKey}', 0, 0, 0, 'Legacy question', NULL, NULL, 0, NULL, NULL);

                 INSERT INTO card_blocks (id, card_id, face, ordinal, kind, text, language, media_id, stretch, max_height, alt_text)
                 VALUES ('{Guid.CreateVersion7().ToString("D").ToUpperInvariant()}', '{cardKey}', 1, 0, 0, 'Legacy answer', NULL, NULL, 0, NULL, NULL);
                 """);

            // The column being dropped must actually be there to start with, or this test is vacuous.
            await using var probe = connection.CreateCommand();
            probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('flashcards') WHERE name = 'subject_id';";
            Convert.ToInt64(await probe.ExecuteScalarAsync()).ShouldBe(1);
        }

        SqliteConnection.ClearAllPools();

        // ---- upgrade it ----
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddApplication();
        services.AddInfrastructure(new StoragePaths(root));

        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<DatabaseInitializer>().MigrateAsync();

        var dispatcher = provider.GetRequiredService<IDispatcher>();

        // Raw row counts first, so a failure says which table lost its contents rather than
        // just "detail was null".
        await using (var probeConnection = new SqliteConnection($"Data Source={dbPath}"))
        {
            await probeConnection.OpenAsync();

            async Task<long> CountAsync(string table)
            {
                await using var command = probeConnection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM {table};";
                return Convert.ToInt64(await command.ExecuteScalarAsync());
            }

            (await CountAsync("subjects")).ShouldBe(1, "subjects");
            (await CountAsync("flashcards")).ShouldBe(1, "flashcards");
            (await CountAsync("card_subjects")).ShouldBe(1, "card_subjects");
            (await CountAsync("card_blocks")).ShouldBe(2, "card_blocks");

            await using var idProbe = probeConnection.CreateCommand();
            idProbe.CommandText = "SELECT id FROM flashcards;";
            var storedId = (string?)await idProbe.ExecuteScalarAsync();
            storedId.ShouldBe(cardKey, "stored flashcards.id");

            // Same predicate the detail query uses, bound the same way the Guid handler binds it.
            await using var whereProbe = probeConnection.CreateCommand();
            whereProbe.CommandText = "SELECT COUNT(*) FROM flashcards WHERE id = $id;";
            whereProbe.Parameters.AddWithValue("$id", cardKey);
            Convert.ToInt64(await whereProbe.ExecuteScalarAsync()).ShouldBe(1, "WHERE id = @Id");

            await using var colProbe = probeConnection.CreateCommand();
            colProbe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('card_choices') WHERE name = 'media_id';";
            Convert.ToInt64(await colProbe.ExecuteScalarAsync()).ShouldBe(1, "card_choices.media_id");
        }

        SqliteConnection.ClearAllPools();

        // Narrow the blast radius: does any read path see the migrated card?
        var subjectsSeen = await dispatcher.QueryAsync(new GetSubjectsQuery());
        subjectsSeen.Count.ShouldBe(1, "GetSubjectsQuery count");
        subjectsSeen[0].CardCount.ShouldBe(1, "GetSubjectsQuery card count");

        var searched = await dispatcher.QueryAsync(
            new SearchFlashcardsQuery(new Flashcards.Application.Contracts.FlashcardSearchCriteria()));
        searched.TotalCount.ShouldBe(1, "SearchFlashcardsQuery total");

        // The card kept its tag, now by way of card_subjects.
        var detail = await dispatcher.QueryAsync(new GetFlashcardDetailQuery(cardId));

        detail.ShouldNotBeNull();
        detail!.Name.ShouldBe("Legacy card");
        detail.Subjects.Count.ShouldBe(1);
        detail.Subjects[0].Id.ShouldBe(subjectId);
        detail.Subjects[0].Name.ShouldBe("Legacy tag");
        detail.Blocks.Count.ShouldBe(2);

        // The tag still counts its card, so it does not get swept up as empty.
        var subjects = await dispatcher.QueryAsync(new GetSubjectsQuery());
        subjects.Single().CardCount.ShouldBe(1);

        // And the old column really is gone.
        await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            await connection.OpenAsync();
            await using var probe = connection.CreateCommand();
            probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('flashcards') WHERE name = 'subject_id';";
            Convert.ToInt64(await probe.ExecuteScalarAsync()).ShouldBe(0);
        }

        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leaked handle on Windows should not fail the test run.
        }
    }
}
