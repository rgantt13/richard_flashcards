using Dapper;
using Flashcards.Application.Cards.Commands;
using Flashcards.Application.Contracts;
using Flashcards.Domain.Cards;
using Flashcards.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Flashcards.Integration.Tests;

/// <summary>
/// Pins down how Guid keys are actually stored, because the answer decides whether hand-written
/// SQL and external tooling can match them.
/// </summary>
public sealed class GuidStorageTests
{
    [Fact]
    public async Task Guid_keys_round_trip_and_are_stored_as_text()
    {
        await using var host = await TestHost.CreateAsync();

        var id = await host.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            SubjectNames = ["Storage"],
            Name = "How is my id stored?",
            CardType = CardType.Standard,
            Blocks =
            [
                new ContentBlockDto(Guid.Empty, CardFace.Question, 0, ContentKind.PlainText, "Q", null, null, ImageStretch.Uniform, null, null),
                new ContentBlockDto(Guid.Empty, CardFace.Answer, 0, ContentKind.PlainText, "A", null, null, ImageStretch.Uniform, null, null),
            ],
        });

        var factory = (IDbConnectionFactory)host.Services.GetService(typeof(IDbConnectionFactory))!;
        await using var connection = await factory.OpenAsync(CancellationToken.None);

        // SQLite is dynamically typed: typeof() reports what the value actually is, not what the
        // column was declared as. A Guid written as a 16-byte BLOB reports 'blob' here even though
        // the column says TEXT, and would then be invisible to any query comparing it to a string.
        var storageClass = await connection.ExecuteScalarAsync<string>(
            "SELECT typeof(id) FROM flashcards LIMIT 1;");

        var stored = await connection.ExecuteScalarAsync<string>(
            "SELECT CAST(id AS TEXT) FROM flashcards LIMIT 1;");

        storageClass.ShouldBe("text");

        // Upper case, and deliberately asserted rather than normalised away: this is
        // Microsoft.Data.Sqlite's own DbType.Guid formatting, not the "D" the GuidHandler in
        // SqlMappings writes — Dapper keeps Guid in its built-in type map, so a custom handler
        // for it is never consulted when binding a parameter. Since SQLite compares TEXT
        // case-sensitively, any hand-written SQL or migration script that matches an id has to use
        // this casing. Changing it now would orphan every existing row.
        stored.ShouldBe(id.ToString("D").ToUpperInvariant());

        // And the app can still find it by Guid — the handler has to work in both directions.
        var found = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM flashcards WHERE id = @Id;", new { Id = id });

        found.ShouldBe(1);
    }
}
