using Flashcards.Application.Cards.Queries;
using Flashcards.Application.Contracts;
using Flashcards.Application.Subjects.Queries;
using Flashcards.Application.Transfer;
using Flashcards.Domain.Cards;
using Shouldly;

namespace Flashcards.Integration.Tests;

/// <summary>
/// The deck shape <c>Assets/DeckPrompt.txt</c> tells a language model to produce.
/// <para>
/// This is the prompt's test. A prompt that documents a schema is a promise, and the only way to
/// keep it is to run the exact JSON it asks for through the real reader and the real importer. The
/// deck below is the four worked examples from that prompt, copied verbatim — if this ever fails,
/// the prompt is lying to whoever pastes it.
/// </para>
/// <para>
/// Note what it deliberately omits: no <c>ExportedUtc</c>, no <c>Media</c>, no <c>Id</c> on any
/// card, no <c>Ordinal</c> where it would be zero anyway on choices, and no <c>IsSuspended</c>.
/// A model writing this by hand will leave those out, so they have to be optional in fact and not
/// just in the documentation.
/// </para>
/// </summary>
public sealed class GeneratedDeckTests
{
    private const string GeneratedJson = """
        {
          "FormatVersion": 1,
          "Subjects": [
            { "Name": "Storage engines", "ColorHex": "#4C9AFF" },
            { "Name": "SQLite", "Parent": "Storage engines", "ColorHex": "#22C55E" }
          ],
          "Cards": [
            {
              "Name": "Why WAL mode helps concurrent reads",
              "CardType": "Standard",
              "Subjects": ["SQLite"],
              "Blocks": [
                { "Face": "Question", "Ordinal": 0, "Kind": "Markdown",
                  "Text": "Why does **WAL** mode let readers continue while a write is in progress?" },
                { "Face": "Answer", "Ordinal": 0, "Kind": "Markdown",
                  "Text": "Writers append to a separate write-ahead log instead of modifying the database file." }
              ],
              "Choices": []
            },
            {
              "Name": "Which SQLite statement is a valid upsert",
              "CardType": "MultipleChoice",
              "Subjects": ["SQLite"],
              "Blocks": [
                { "Face": "Question", "Ordinal": 0, "Kind": "PlainText",
                  "Text": "Which of these updates the row when card_id already exists?" },
                { "Face": "Answer", "Ordinal": 0, "Kind": "Markdown",
                  "Text": "SQLite has no `MERGE`. The upsert clause is `ON CONFLICT (col) DO UPDATE SET`." }
              ],
              "Choices": [
                { "Ordinal": 0, "Text": "INSERT ... ON CONFLICT (id) DO UPDATE SET v = excluded.v", "IsCorrect": true },
                { "Ordinal": 1, "Text": "MERGE INTO t USING ...", "IsCorrect": false },
                { "Ordinal": 2, "Text": "INSERT ... ON DUPLICATE KEY UPDATE v = @v", "IsCorrect": false },
                { "Ordinal": 3, "Text": "UPSERT INTO t (id, v) VALUES (@id, @v)", "IsCorrect": false }
              ]
            },
            {
              "Name": "Enabling foreign keys",
              "CardType": "Cloze",
              "Subjects": ["SQLite"],
              "Blocks": [
                { "Face": "Question", "Ordinal": 0, "Kind": "PlainText",
                  "Text": "SQLite enforces foreign keys only after {{PRAGMA foreign_keys = ON}}, and the setting applies per {{connection::not per database}}." }
              ],
              "Choices": []
            },
            {
              "Name": "Where WAL files sit relative to the database",
              "CardType": "Freeform",
              "Subjects": ["SQLite"],
              "Blocks": [
                { "Face": "Question", "Ordinal": 0, "Kind": "PlainText", "Text": "Name the three files WAL mode keeps.",
                  "X": 80, "Y": 60, "Width": 800, "Height": 90 },
                { "Face": "Answer", "Ordinal": 0, "Kind": "Markdown", "Text": "**app.db**",
                  "X": 60, "Y": 180, "Width": 260, "Height": 160 },
                { "Face": "Answer", "Ordinal": 1, "Kind": "Markdown", "Text": "**app.db-wal**",
                  "X": 350, "Y": 180, "Width": 260, "Height": 160 }
              ],
              "Choices": []
            }
          ]
        }
        """;

    private static DeckDocument Parse() => DeckSerializer.Read(System.Text.Encoding.UTF8.GetBytes(GeneratedJson));

    [Fact]
    public void The_documented_shape_parses()
    {
        var deck = Parse();

        deck.Subjects.Count.ShouldBe(2);
        deck.Cards.Count.ShouldBe(4);

        // Omitted throughout, and defaulted rather than throwing.
        deck.Media.ShouldBeEmpty();
        deck.Cards.ShouldAllBe(c => c.Id == Guid.Empty);
        deck.Cards.ShouldAllBe(c => !c.IsSuspended);
    }

    [Fact]
    public async Task Every_card_type_in_the_prompt_imports_without_a_warning()
    {
        await using var host = await TestHost.CreateAsync();

        var deck = Parse();

        var result = await host.Dispatcher.SendAsync(new ImportDeckCommand(
            deck,
            [.. deck.Subjects.Select(s => s.Name)],
            [.. deck.Cards.Select(c => c.Id)]));

        // The warnings are the assertion that matters: each one would name a card the prompt
        // taught a model to write and this app then refused.
        result.Warnings.ShouldBeEmpty();
        result.CardsAdded.ShouldBe(4);
        result.SubjectsCreated.ShouldBe(2);
    }

    /// <summary>
    /// Card ids are all <see cref="Guid.Empty"/> because the prompt tells models to omit them.
    /// Four cards sharing one id must still import as four cards, not collide into one.
    /// </summary>
    [Fact]
    public async Task Cards_without_ids_do_not_collide_with_each_other()
    {
        await using var host = await TestHost.CreateAsync();
        var deck = Parse();

        await host.Dispatcher.SendAsync(new ImportDeckCommand(
            deck, [.. deck.Subjects.Select(s => s.Name)], [.. deck.Cards.Select(c => c.Id)]));

        var page = await host.Dispatcher.QueryAsync(new SearchFlashcardsQuery(new FlashcardSearchCriteria()));

        page.Items.Count.ShouldBe(4);
        page.Items.Select(c => c.CardType).Distinct().Count().ShouldBe(4);
    }

    [Fact]
    public async Task The_subject_tree_the_prompt_asks_for_arrives_nested()
    {
        await using var host = await TestHost.CreateAsync();
        var deck = Parse();

        await host.Dispatcher.SendAsync(new ImportDeckCommand(
            deck, [.. deck.Subjects.Select(s => s.Name)], [.. deck.Cards.Select(c => c.Id)]));

        var subjects = (await host.Dispatcher.QueryAsync(new GetSubjectsQuery())).ToDictionary(s => s.Name);

        subjects["SQLite"].ParentId.ShouldBe(subjects["Storage engines"].Id);

        // Cards carry only the specific tag, and answer to the parent through the tree.
        subjects["Storage engines"].CardCount.ShouldBe(0);
        subjects["Storage engines"].TotalCardCount.ShouldBe(4);
    }

    /// <summary>
    /// The freeform card's geometry has to survive, or the layout the model arranged is lost and
    /// the card silently becomes a stack of text boxes.
    /// </summary>
    [Fact]
    public async Task Freeform_placement_survives_the_import()
    {
        await using var host = await TestHost.CreateAsync();
        var deck = Parse();

        await host.Dispatcher.SendAsync(new ImportDeckCommand(
            deck, [.. deck.Subjects.Select(s => s.Name)], [.. deck.Cards.Select(c => c.Id)]));

        var page = await host.Dispatcher.QueryAsync(new SearchFlashcardsQuery(new FlashcardSearchCriteria
        {
            CardType = CardType.Freeform,
        }));

        var detail = await host.Dispatcher.QueryAsync(new GetFlashcardDetailQuery(page.Items.Single().Id));

        detail!.Blocks.ShouldAllBe(b => b.IsPlaced);
        detail.Blocks.Single(b => b.Face == CardFace.Question).X.ShouldBe(80);
        detail.Blocks.First(b => b.Face == CardFace.Answer).Width.ShouldBe(260);
    }

    /// <summary>
    /// A misspelled enum is the single most likely way a generated deck fails, so the message has
    /// to locate it rather than saying "not valid JSON" and sending you hunting for a brace.
    /// <para>
    /// It reports the property path and the type it could not convert to — <c>$.Cards[1].CardType</c>
    /// — but not the offending value itself, which is what System.Text.Json gives us. The path is
    /// the useful half: it names the card by position and the property by name.
    /// </para>
    /// </summary>
    [Fact]
    public void A_misspelled_card_type_is_located_by_path()
    {
        var broken = GeneratedJson.Replace("\"MultipleChoice\"", "\"MultiChoice\"");

        var failure = Should.Throw<DeckFormatException>(
            () => DeckSerializer.Read(System.Text.Encoding.UTF8.GetBytes(broken)));

        failure.Message.ShouldContain("could not be read as a flashcards deck");
        failure.Message.ShouldContain("CardType");
        failure.Message.ShouldContain("$.Cards[1]");
    }
}
