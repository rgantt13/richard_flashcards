using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Application.Cards.Commands;
using Flashcards.Application.Cards.Queries;
using Flashcards.Application.Contracts;
using Flashcards.Application.Subjects.Commands;
using Flashcards.Application.Subjects.Queries;
using Flashcards.Application.Transfer;
using Flashcards.Domain.Cards;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Flashcards.Integration.Tests;

/// <summary>
/// Exporting a deck and importing it somewhere else, end to end.
/// <para>
/// Every test here does the round trip through two separate <see cref="TestHost"/>s — a real
/// export into bytes, then a real import into a database that has never seen those ids. That is
/// the only arrangement that catches the thing this feature is actually about: nothing in the file
/// can be an id, because ids do not survive the trip. Importing back into the source library is
/// tested too, because it is the case where they <em>do</em>.
/// </para>
/// </summary>
public sealed class DeckTransferTests
{
    private static ContentBlockDto Text(CardFace face, int ordinal, string text)
        => new(Guid.Empty, face, ordinal, ContentKind.PlainText, text, null, null, ImageStretch.Uniform, null, null);

    private static Task<Guid> CardAsync(TestHost host, string name, params string[] subjects)
        => host.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            SubjectNames = subjects,
            Name = name,
            CardType = CardType.Standard,
            Blocks = [Text(CardFace.Question, 0, $"Q {name}"), Text(CardFace.Answer, 0, $"A {name}")],
        });

    /// <summary>Everything in the source library, as a file's worth of bytes.</summary>
    private static async Task<byte[]> ExportEverythingAsync(TestHost host)
    {
        var subjects = await host.Dispatcher.QueryAsync(new GetSubjectsQuery());

        var cards = await host.Dispatcher.QueryAsync(new SearchFlashcardsQuery(new FlashcardSearchCriteria
        {
            PageSize = 500,
        }));

        var deck = await host.Dispatcher.QueryAsync(new BuildDeckExportQuery(
            [.. subjects.Select(s => s.Id)],
            [.. cards.Items.Select(c => c.Id)]));

        return DeckSerializer.Write(deck);
    }

    private static Task<DeckImportResult> ImportEverythingAsync(
        TestHost host,
        DeckDocument deck,
        DeckImportMode mode = DeckImportMode.Skip)
        => host.Dispatcher.SendAsync(new ImportDeckCommand(
            deck,
            [.. deck.Subjects.Select(s => s.Name)],
            [.. deck.Cards.Select(c => c.Id)],
            mode));

    private static async Task<IReadOnlyList<string>> CardNamesAsync(TestHost host)
    {
        var page = await host.Dispatcher.QueryAsync(new SearchFlashcardsQuery(new FlashcardSearchCriteria
        {
            PageSize = 500,
        }));

        return [.. page.Items.Select(c => c.Name).OrderBy(n => n)];
    }

    [Fact]
    public async Task Round_trip_rebuilds_the_subject_tree_in_a_library_that_never_saw_it()
    {
        await using var source = await TestHost.CreateAsync();

        var databases = await source.Dispatcher.SendAsync(new CreateSubjectCommand("Databases"));
        var sql = await source.Dispatcher.SendAsync(new CreateSubjectCommand("SQL", databases));
        await source.Dispatcher.SendAsync(new CreateSubjectCommand("MSSQL", sql));

        await CardAsync(source, "Paging syntax", "MSSQL");

        var bytes = await ExportEverythingAsync(source);

        await using var destination = await TestHost.CreateAsync();
        var result = await ImportEverythingAsync(destination, DeckSerializer.Read(bytes));

        result.CardsAdded.ShouldBe(1);
        result.SubjectsCreated.ShouldBe(3);
        result.Warnings.ShouldBeEmpty();

        var arrived = await destination.Dispatcher.QueryAsync(new GetSubjectsQuery());
        var byName = arrived.ToDictionary(s => s.Name);

        byName["Databases"].ParentId.ShouldBeNull();
        byName["SQL"].ParentId.ShouldBe(byName["Databases"].Id);
        byName["MSSQL"].ParentId.ShouldBe(byName["SQL"].Id);

        // Depth is derived from the rebuilt tree, so this is the real proof the shape survived.
        byName["MSSQL"].Depth.ShouldBe(3);
    }

    [Fact]
    public async Task Exporting_a_card_brings_the_subjects_it_needs_even_when_they_were_not_picked()
    {
        await using var source = await TestHost.CreateAsync();

        var databases = await source.Dispatcher.SendAsync(new CreateSubjectCommand("Databases"));
        var sql = await source.Dispatcher.SendAsync(new CreateSubjectCommand("SQL", databases));
        var card = await CardAsync(source, "Window functions", "SQL");

        // The card alone, with nothing ticked in the subject tier.
        var deck = await source.Dispatcher.QueryAsync(new BuildDeckExportQuery([], [card]));

        // Its own tag, plus the ancestor that tag hangs off — otherwise the tree arrives with a hole.
        deck.Subjects.Select(s => s.Name).ShouldBe(["Databases", "SQL"]);
        deck.Subjects.Single(s => s.Name == "SQL").Parent.ShouldBe("Databases");

        // The inherited tag is not written as if it had been applied.
        deck.Cards.Single().Subjects.ShouldBe(["SQL"]);

        sql.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Importing_twice_skips_what_is_already_there()
    {
        await using var source = await TestHost.CreateAsync();
        await source.Dispatcher.SendAsync(new CreateSubjectCommand("Runtime"));
        await CardAsync(source, "Garbage collection", "Runtime");

        var deck = DeckSerializer.Read(await ExportEverythingAsync(source));

        await using var destination = await TestHost.CreateAsync();

        (await ImportEverythingAsync(destination, deck)).CardsAdded.ShouldBe(1);

        var second = await ImportEverythingAsync(destination, deck);

        second.CardsAdded.ShouldBe(0);
        second.CardsSkipped.ShouldBe(1);

        // And nothing was quietly duplicated under a near-identical name.
        (await CardNamesAsync(destination)).ShouldBe(["Garbage collection"]);
    }

    [Fact]
    public async Task Replace_overwrites_the_card_already_here_rather_than_adding_a_second()
    {
        await using var source = await TestHost.CreateAsync();
        await source.Dispatcher.SendAsync(new CreateSubjectCommand("Runtime"));
        await CardAsync(source, "Spans", "Runtime");

        var deck = DeckSerializer.Read(await ExportEverythingAsync(source));

        await using var destination = await TestHost.CreateAsync();
        await destination.Dispatcher.SendAsync(new CreateSubjectCommand("Runtime"));

        // Same name, same tag, different content — the collision the import has to notice.
        var existing = await destination.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            SubjectNames = ["Runtime"],
            Name = "Spans",
            CardType = CardType.Standard,
            Blocks = [Text(CardFace.Question, 0, "stale question"), Text(CardFace.Answer, 0, "stale answer")],
        });

        var result = await ImportEverythingAsync(destination, deck, DeckImportMode.Replace);

        result.CardsReplaced.ShouldBe(1);
        result.CardsAdded.ShouldBe(0);

        (await CardNamesAsync(destination)).ShouldBe(["Spans"]);

        var detail = await destination.Dispatcher.QueryAsync(new GetFlashcardDetailQuery(existing));
        detail!.Blocks.Single(b => b.Face == CardFace.Question).Text.ShouldBe("Q Spans");
    }

    [Fact]
    public async Task Images_travel_with_the_cards_that_use_them()
    {
        await using var source = await TestHost.CreateAsync();

        // A one-pixel PNG. Real bytes, because the media store sniffs the format from them.
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        var media = await source.Services.GetRequiredService<IMediaStore>().SaveAsync(png, "dot.png", default);

        await source.Dispatcher.SendAsync(new CreateSubjectCommand("Diagrams"));

        await source.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            SubjectNames = ["Diagrams"],
            Name = "The diagram",
            CardType = CardType.Standard,
            Blocks =
            [
                Text(CardFace.Question, 0, "What does this show?"),
                new ContentBlockDto(Guid.Empty, CardFace.Answer, 0, ContentKind.Image, null, null,
                    media.Id, ImageStretch.Uniform, null, "a dot"),
            ],
        });

        var deck = DeckSerializer.Read(await ExportEverythingAsync(source));
        deck.Media.Single().Bytes.ShouldBe(png);

        await using var destination = await TestHost.CreateAsync();
        var result = await ImportEverythingAsync(destination, deck);

        result.CardsAdded.ShouldBe(1);
        result.Images.ShouldBe(1);

        var page = await destination.Dispatcher.QueryAsync(new SearchFlashcardsQuery(new FlashcardSearchCriteria()));
        var detail = await destination.Dispatcher.QueryAsync(new GetFlashcardDetailQuery(page.Items.Single().Id));
        var image = detail!.Blocks.Single(b => b.Kind == ContentKind.Image);

        // Re-pointed at the destination's own id, not the one written in the file.
        image.MediaId.ShouldNotBeNull();
        image.MediaId.ShouldNotBe(media.Id);

        var stored = await destination.Services.GetRequiredService<IMediaStore>()
            .LoadAsync(image.MediaId!.Value, default);

        stored.ShouldBe(png);
    }

    [Fact]
    public async Task Skipped_cards_do_not_drag_their_images_into_the_store()
    {
        await using var source = await TestHost.CreateAsync();

        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        var media = await source.Services.GetRequiredService<IMediaStore>().SaveAsync(png, "dot.png", default);

        await source.Dispatcher.SendAsync(new CreateSubjectCommand("Diagrams"));

        await source.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            SubjectNames = ["Diagrams"],
            Name = "The diagram",
            CardType = CardType.Standard,
            Blocks =
            [
                Text(CardFace.Question, 0, "What does this show?"),
                new ContentBlockDto(Guid.Empty, CardFace.Answer, 0, ContentKind.Image, null, null,
                    media.Id, ImageStretch.Uniform, null, "a dot"),
            ],
        });

        var deck = DeckSerializer.Read(await ExportEverythingAsync(source));

        // Straight back into the library it came from, so every card is a skip.
        var result = await ImportEverythingAsync(source, deck);

        result.CardsSkipped.ShouldBe(1);
        result.CardsAdded.ShouldBe(0);

        // Nothing was written, so nothing should have been said about images either.
        result.Images.ShouldBe(0);
        result.Summary.ShouldNotContain("image");
    }

    [Fact]
    public async Task An_import_leaves_a_subject_you_already_had_exactly_where_you_filed_it()
    {
        await using var source = await TestHost.CreateAsync();
        var languages = await source.Dispatcher.SendAsync(new CreateSubjectCommand("Languages"));
        await source.Dispatcher.SendAsync(new CreateSubjectCommand("Spanish", languages));
        await CardAsync(source, "Ser vs estar", "Spanish");

        var deck = DeckSerializer.Read(await ExportEverythingAsync(source));

        await using var destination = await TestHost.CreateAsync();

        // The same subject, but this library keeps it at the top level.
        await destination.Dispatcher.SendAsync(new CreateSubjectCommand("Spanish"));

        await ImportEverythingAsync(destination, deck);

        var arrived = await destination.Dispatcher.QueryAsync(new GetSubjectsQuery());

        // Importing adds to your library; it does not rearrange the tree you built.
        arrived.Single(s => s.Name == "Spanish").ParentId.ShouldBeNull();
        arrived.Count(s => s.Name == "Spanish").ShouldBe(1);
    }

    [Fact]
    public async Task Re_importing_into_the_library_it_came_from_recognises_its_own_cards()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Dispatcher.SendAsync(new CreateSubjectCommand("Runtime"));
        var card = await CardAsync(host, "Finalizers", "Runtime");

        var deck = DeckSerializer.Read(await ExportEverythingAsync(host));
        deck.Cards.Single().Id.ShouldBe(card);

        var result = await ImportEverythingAsync(host, deck);

        result.CardsSkipped.ShouldBe(1);
        result.CardsAdded.ShouldBe(0);
        result.SubjectsCreated.ShouldBe(0);
    }

    [Fact]
    public void A_file_that_is_not_a_deck_is_refused_by_name()
    {
        var exception = Should.Throw<DeckFormatException>(
            () => DeckSerializer.Read("not json at all"u8.ToArray()));

        exception.Message.ShouldContain("not a flashcards deck");
    }

    [Fact]
    public void A_deck_from_a_newer_app_says_so_rather_than_half_importing()
    {
        var future = DeckSerializer.Write(new DeckDocument
        {
            FormatVersion = DeckSerializer.CurrentFormatVersion + 1,
            Subjects = [new DeckSubject("Anything", null, null, null)],
        });

        Should.Throw<DeckFormatException>(() => DeckSerializer.Read(future))
            .Message.ShouldContain("newer version");
    }

    [Fact]
    public async Task Picking_one_card_out_of_a_deck_leaves_the_rest_behind()
    {
        await using var source = await TestHost.CreateAsync();
        await source.Dispatcher.SendAsync(new CreateSubjectCommand("Runtime"));
        await CardAsync(source, "Keep me", "Runtime");
        await CardAsync(source, "Leave me", "Runtime");

        var deck = DeckSerializer.Read(await ExportEverythingAsync(source));

        await using var destination = await TestHost.CreateAsync();

        await destination.Dispatcher.SendAsync(new ImportDeckCommand(
            deck,
            [],
            [deck.Cards.Single(c => c.Name == "Keep me").Id]));

        (await CardNamesAsync(destination)).ShouldBe(["Keep me"]);
    }
}
