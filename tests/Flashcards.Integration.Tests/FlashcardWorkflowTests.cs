using Flashcards.Application.Cards.Commands;
using Flashcards.Application.Cards.Queries;
using Flashcards.Application.Contracts;
using Flashcards.Application.Media.Commands;
using Flashcards.Application.Media.Queries;
using Flashcards.Application.Quiz.Commands;
using Flashcards.Application.Quiz.Queries;
using Flashcards.Application.Stats.Commands;
using Flashcards.Application.Stats.Queries;
using Flashcards.Application.Subjects.Commands;
using Flashcards.Application.Subjects.Queries;
using Flashcards.Domain.Cards;
using Flashcards.Domain.Practice;
using Shouldly;

namespace Flashcards.Integration.Tests;

public sealed class FlashcardWorkflowTests
{
    private static ContentBlockDto Text(CardFace face, int ordinal, string text, ContentKind kind = ContentKind.PlainText)
        => new(Guid.Empty, face, ordinal, kind, text, null, null, ImageStretch.Uniform, null, null);

    /// <summary>Resolves a tag to its id, minting it if this is the first mention.</summary>
    private static Task<Guid> SubjectIdAsync(TestHost host, string name)
        => host.Dispatcher.SendAsync(new EnsureSubjectCommand(name));

    private static Task<Guid> CreateCardAsync(TestHost host, string subject, string name)
        => host.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            SubjectNames = [subject],
            Name = name,
            CardType = CardType.Standard,
            Blocks =
            [
                Text(CardFace.Question, 0, $"Question for {name}"),
                Text(CardFace.Answer, 0, $"Answer for {name}"),
            ],
        });

    [Fact]
    public async Task A_card_round_trips_through_the_database_with_every_block_intact()
    {
        await using var host = await TestHost.CreateAsync();

        var id = await host.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            SubjectNames = ["SQL"],
            Name = "Window functions",
            CardType = CardType.Standard,
            Notes = "Arrived in SQLite 3.25.",
            Blocks =
            [
                Text(CardFace.Question, 0, "What does **COUNT(\\*) OVER ()** give you?", ContentKind.Markdown),
                Text(CardFace.Question, 1, "SELECT COUNT(*) OVER () FROM t;", ContentKind.Code),
                Text(CardFace.Answer, 0, "The unpaged row count."),
            ],
        });

        var detail = await host.Dispatcher.QueryAsync(new GetFlashcardDetailQuery(id));

        detail.ShouldNotBeNull();
        detail!.Name.ShouldBe("Window functions");
        detail.Subjects.Single().Name.ShouldBe("SQL");
        detail.Notes.ShouldBe("Arrived in SQLite 3.25.");
        detail.Blocks.Count.ShouldBe(3);
        detail.Blocks.Count(b => b.Face == CardFace.Question).ShouldBe(2);
        detail.Blocks.Single(b => b.Kind == ContentKind.Code).Language.ShouldNotBeNull();
    }

    [Fact]
    public async Task Saving_a_card_mints_the_subject_tag_it_names()
    {
        await using var host = await TestHost.CreateAsync();

        (await host.Dispatcher.QueryAsync(new GetSubjectsQuery())).ShouldBeEmpty();

        await CreateCardAsync(host, "Brand new tag", "First");

        var subjects = await host.Dispatcher.QueryAsync(new GetSubjectsQuery());

        subjects.Count.ShouldBe(1);
        subjects[0].Name.ShouldBe("Brand new tag");
        subjects[0].CardCount.ShouldBe(1);
        // A tag colours itself from its own name rather than waiting for someone to pick one.
        subjects[0].ColorHex.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_subject_tag_is_matched_case_insensitively_rather_than_duplicated()
    {
        await using var host = await TestHost.CreateAsync();

        await CreateCardAsync(host, "SQL Server", "First");
        await CreateCardAsync(host, "sql server", "Second");

        var subjects = await host.Dispatcher.QueryAsync(new GetSubjectsQuery());

        subjects.Count.ShouldBe(1);
        subjects[0].CardCount.ShouldBe(2);
        // The first spelling wins; the second card joins it rather than minting a near-duplicate.
        subjects[0].Name.ShouldBe("SQL Server");
    }

    [Fact]
    public async Task A_subject_outlives_the_last_card_that_wore_it()
    {
        await using var host = await TestHost.CreateAsync();

        var keep = await CreateCardAsync(host, "Keeper", "Stays");
        var doomed = await CreateCardAsync(host, "Transient", "Goes");

        (await host.Dispatcher.QueryAsync(new GetSubjectsQuery())).Count.ShouldBe(2);

        await host.Dispatcher.SendAsync(new DeleteFlashcardsCommand([doomed]));

        // Subjects used to retire themselves here. They are a curated tree now: a parent that only
        // groups its children has no cards of its own, so auto-retiring would delete the structure
        // the moment it was built. Emptying a subject leaves it in place with a count of zero.
        var remaining = await host.Dispatcher.QueryAsync(new GetSubjectsQuery());

        remaining.Count.ShouldBe(2);
        remaining.Single(s => s.Name == "Transient").CardCount.ShouldBe(0);
        remaining.Single(s => s.Name == "Keeper").CardCount.ShouldBe(1);

        // The surviving card is untouched.
        (await host.Dispatcher.QueryAsync(new GetFlashcardDetailQuery(keep))).ShouldNotBeNull();
    }

    [Fact]
    public async Task Retagging_the_last_card_off_a_subject_leaves_the_subject_standing()
    {
        await using var host = await TestHost.CreateAsync();

        var id = await CreateCardAsync(host, "Old tag", "Wanderer");

        await host.Dispatcher.SendAsync(new RetagCardsCommand([id], ["New tag"]));

        var subjects = await host.Dispatcher.QueryAsync(new GetSubjectsQuery());

        subjects.Count.ShouldBe(2);
        subjects.Single(s => s.Name == "New tag").CardCount.ShouldBe(1);
        subjects.Single(s => s.Name == "Old tag").CardCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_card_can_carry_several_tags_and_appears_under_each()
    {
        await using var host = await TestHost.CreateAsync();

        var id = await host.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            SubjectNames = ["SQL", "Databases"],
            Name = "Clustered indexes",
            CardType = CardType.Standard,
            Blocks = [Text(CardFace.Question, 0, "What is one?"), Text(CardFace.Answer, 0, "The table itself, sorted.")],
        });

        var detail = await host.Dispatcher.QueryAsync(new GetFlashcardDetailQuery(id));
        detail!.Subjects.Select(s => s.Name).OrderBy(n => n).ShouldBe(["Databases", "SQL"]);

        // Both tags exist and both count the one card.
        var subjects = await host.Dispatcher.QueryAsync(new GetSubjectsQuery());
        subjects.Count.ShouldBe(2);
        subjects.ShouldAllBe(s => s.CardCount == 1);

        // And it comes back once from search, not once per tag.
        var results = await Search(host, "Clustered indexes");
        results.TotalCount.ShouldBe(1);
        results.Items.Single().Subjects.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_card_shared_by_two_selected_tags_is_queued_only_once()
    {
        await using var host = await TestHost.CreateAsync();

        await host.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            SubjectNames = ["SQL", "Databases"],
            Name = "Shared",
            CardType = CardType.Standard,
            Blocks = [Text(CardFace.Question, 0, "Q"), Text(CardFace.Answer, 0, "A")],
        });

        var sql = await SubjectIdAsync(host, "SQL");
        var databases = await SubjectIdAsync(host, "Databases");

        var session = await host.Dispatcher.QueryAsync(
            new StartQuizSessionQuery(new QuizOptions { SubjectIds = [sql, databases] }));

        // The queue joins through card_subjects; without EXISTS this would be two entries.
        session.CardIds.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Dropping_a_tag_from_a_card_leaves_the_other_tag_intact()
    {
        await using var host = await TestHost.CreateAsync();

        var id = await host.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            SubjectNames = ["SQL", "Databases"],
            Name = "Retagged",
            CardType = CardType.Standard,
            Blocks = [Text(CardFace.Question, 0, "Q"), Text(CardFace.Answer, 0, "A")],
        });

        await host.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            Id = id,
            SubjectNames = ["SQL"],
            Name = "Retagged",
            CardType = CardType.Standard,
            Blocks = [Text(CardFace.Question, 0, "Q"), Text(CardFace.Answer, 0, "A")],
        });

        var detail = await host.Dispatcher.QueryAsync(new GetFlashcardDetailQuery(id));
        detail!.Subjects.Single().Name.ShouldBe("SQL");

        // "Databases" keeps existing with nothing in it — dropping a tag is not a reason to destroy
        // a subject somebody may have deliberately placed in the tree.
        var subjects = await host.Dispatcher.QueryAsync(new GetSubjectsQuery());
        subjects.Select(s => s.Name).OrderBy(n => n).ShouldBe(["Databases", "SQL"]);
        subjects.Single(s => s.Name == "Databases").CardCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_designed_card_round_trips_with_its_layout_and_ink()
    {
        await using var host = await TestHost.CreateAsync();

        var ink = InkSerializer.Serialize(
        [
            new InkStroke("#4C9AFF", 2.5, [new InkPoint(10, 20), new InkPoint(40.5, 60)]),
            new InkStroke("#EF4444", 4, [new InkPoint(100, 100), new InkPoint(140, 130)]),
        ]);

        var id = await host.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            SubjectNames = ["Diagrams"],
            Name = "Designed card",
            CardType = CardType.Freeform,
            Blocks =
            [
                // A placed text element.
                new ContentBlockDto(Guid.Empty, CardFace.Question, 0, ContentKind.Markdown,
                    "**Label** the parts", null, null, ImageStretch.Uniform, null, null,
                    X: 60, Y: 40, Width: 320, Height: 120),

                // The question face's ink layer.
                new ContentBlockDto(Guid.Empty, CardFace.Question, 1, ContentKind.Drawing,
                    ink, null, null, ImageStretch.Uniform, null, null,
                    X: 0, Y: 0, Width: CardCanvas.Width, Height: CardCanvas.Height),

                new ContentBlockDto(Guid.Empty, CardFace.Answer, 0, ContentKind.Markdown,
                    "The answer", null, null, ImageStretch.Uniform, null, null,
                    X: 100, Y: 200, Width: 400, Height: 150),
            ],
        });

        var detail = await host.Dispatcher.QueryAsync(new GetFlashcardDetailQuery(id));

        detail.ShouldNotBeNull();
        detail!.CardType.ShouldBe(CardType.Freeform);

        var text = detail.Blocks.Single(b => b.Face == CardFace.Question && b.Kind == ContentKind.Markdown);
        text.IsPlaced.ShouldBeTrue();
        text.X.ShouldBe(60);
        text.Y.ShouldBe(40);
        text.Width.ShouldBe(320);
        text.Height.ShouldBe(120);

        // Ink survives the round trip stroke for stroke, including fractional coordinates.
        var drawing = detail.Blocks.Single(b => b.Kind == ContentKind.Drawing);
        var strokes = InkSerializer.Parse(drawing.Text);

        strokes.Count.ShouldBe(2);
        strokes[0].ColorHex.ShouldBe("#4C9AFF");
        strokes[0].Thickness.ShouldBe(2.5);
        strokes[0].Points[1].ShouldBe(new InkPoint(40.5, 60));
        strokes[1].ColorHex.ShouldBe("#EF4444");
    }

    [Fact]
    public async Task An_element_placed_off_the_canvas_is_pulled_back_onto_it()
    {
        await using var host = await TestHost.CreateAsync();

        var id = await host.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            SubjectNames = ["Diagrams"],
            Name = "Out of bounds",
            CardType = CardType.Freeform,
            Blocks =
            [
                new ContentBlockDto(Guid.Empty, CardFace.Question, 0, ContentKind.Markdown,
                    "Way off", null, null, ImageStretch.Uniform, null, null,
                    X: CardCanvas.Width + 5_000, Y: -900, Width: 200, Height: 100),
                new ContentBlockDto(Guid.Empty, CardFace.Answer, 0, ContentKind.Markdown,
                    "Fine", null, null, ImageStretch.Uniform, null, null,
                    X: 10, Y: 10, Width: 200, Height: 100),
            ],
        });

        var detail = await host.Dispatcher.QueryAsync(new GetFlashcardDetailQuery(id));
        var block = detail!.Blocks.Single(b => b.Face == CardFace.Question);

        block.X.ShouldBe(CardCanvas.Width - 200);
        block.Y.ShouldBe(0);
    }

    [Fact]
    public async Task A_designed_card_needs_something_on_both_faces()
    {
        await using var host = await TestHost.CreateAsync();

        // An ink layer with no strokes is not content, so this answer face is effectively empty.
        await Should.ThrowAsync<Exception>(() => host.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            SubjectNames = ["Diagrams"],
            Name = "Half designed",
            CardType = CardType.Freeform,
            Blocks =
            [
                new ContentBlockDto(Guid.Empty, CardFace.Question, 0, ContentKind.Markdown,
                    "Question", null, null, ImageStretch.Uniform, null, null,
                    X: 10, Y: 10, Width: 200, Height: 100),
                new ContentBlockDto(Guid.Empty, CardFace.Answer, 0, ContentKind.Drawing,
                    "", null, null, ImageStretch.Uniform, null, null,
                    X: 0, Y: 0, Width: CardCanvas.Width, Height: CardCanvas.Height),
            ],
        }));
    }

    [Fact]
    public async Task Card_names_must_be_unique_inside_a_subject_but_not_across_subjects()
    {
        await using var host = await TestHost.CreateAsync();

        await CreateCardAsync(host, "SQL", "Joins");

        await Should.ThrowAsync<Exception>(() => CreateCardAsync(host, "SQL", "Joins"));

        // Same name, different tag: fine.
        await CreateCardAsync(host, ".NET", "Joins");
    }

    [Fact]
    public async Task Editing_a_card_replaces_its_blocks_rather_than_accumulating_them()
    {
        await using var host = await TestHost.CreateAsync();
        var id = await CreateCardAsync(host, "SQL", "Indexes");

        await host.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            Id = id,
            SubjectNames = ["SQL"],
            Name = "Indexes",
            CardType = CardType.Standard,
            Blocks = [Text(CardFace.Question, 0, "Rewritten question"), Text(CardFace.Answer, 0, "Rewritten answer")],
        });

        var detail = await host.Dispatcher.QueryAsync(new GetFlashcardDetailQuery(id));

        detail!.Blocks.Count.ShouldBe(2);
        detail.Blocks.First(b => b.Face == CardFace.Question).Text.ShouldBe("Rewritten question");
    }

    [Fact]
    public async Task Search_matches_question_text_not_only_the_card_name()
    {
        await using var host = await TestHost.CreateAsync();

        await host.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            SubjectNames = ["SQL"],
            Name = "Card A",
            CardType = CardType.Standard,
            Blocks =
            [
                Text(CardFace.Question, 0, "Explain the WAL journal mode"),
                Text(CardFace.Answer, 0, "Write-ahead logging."),
            ],
        });

        await CreateCardAsync(host, "SQL", "Card B");

        var byName = await Search(host, "Card B");
        byName.TotalCount.ShouldBe(1);

        // This is the flattened card_search column, kept current by the triggers in migration 002.
        var byBody = await Search(host, "journal");
        byBody.TotalCount.ShouldBe(1);
        byBody.Items[0].Name.ShouldBe("Card A");
    }

    [Fact]
    public async Task Search_is_case_insensitive_and_escapes_wildcards()
    {
        await using var host = await TestHost.CreateAsync();

        await host.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            SubjectNames = ["SQL"],
            Name = "Discounting",
            CardType = CardType.Standard,
            Blocks = [Text(CardFace.Question, 0, "What is 50% of 40?"), Text(CardFace.Answer, 0, "20")],
        });

        (await Search(host, "DISCOUNTING")).TotalCount.ShouldBe(1);
        (await Search(host, "50%")).TotalCount.ShouldBe(1);

        // A bare % must not behave as "match everything".
        (await Search(host, "99%")).TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task Deleting_a_card_cascades_to_its_blocks_tags_and_history()
    {
        await using var host = await TestHost.CreateAsync();
        var id = await CreateCardAsync(host, "SQL", "Doomed");

        await host.Dispatcher.SendAsync(new RecordAnswerCommand(id, true, TimeSpan.FromSeconds(3)));
        await host.Dispatcher.SendAsync(new DeleteFlashcardsCommand([id]));

        (await host.Dispatcher.QueryAsync(new GetFlashcardDetailQuery(id))).ShouldBeNull();

        var factory = (Flashcards.Infrastructure.Persistence.IDbConnectionFactory)
            host.Services.GetService(typeof(Flashcards.Infrastructure.Persistence.IDbConnectionFactory))!;

        await using var connection = await factory.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT (SELECT COUNT(*) FROM card_blocks   WHERE card_id = $id) + " +
            "       (SELECT COUNT(*) FROM card_subjects WHERE card_id = $id) + " +
            "       (SELECT COUNT(*) FROM review_log    WHERE card_id = $id) + " +
            "       (SELECT COUNT(*) FROM card_search   WHERE card_id = $id);";
        command.Parameters.AddWithValue("$id", id.ToString("D"));

        Convert.ToInt64(await command.ExecuteScalarAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Answering_a_card_does_not_take_it_out_of_the_queue()
    {
        await using var host = await TestHost.CreateAsync();
        var id = await CreateCardAsync(host, "SQL", "Always available");
        var subjectId = await SubjectIdAsync(host, "SQL");

        var before = await host.Dispatcher.QueryAsync(new StartQuizSessionQuery(new QuizOptions { SubjectIds = [subjectId] }));
        before.CardIds.ShouldContain(id);

        await host.Dispatcher.SendAsync(new RecordAnswerCommand(id, true, TimeSpan.FromSeconds(4)));

        // Nothing is scheduled, so a card you just answered is still yours to study again.
        var after = await host.Dispatcher.QueryAsync(new StartQuizSessionQuery(new QuizOptions { SubjectIds = [subjectId] }));
        after.CardIds.ShouldContain(id);
    }

    [Fact]
    public async Task Answering_records_the_outcome_against_the_card()
    {
        await using var host = await TestHost.CreateAsync();
        var id = await CreateCardAsync(host, "SQL", "Tallied");

        await host.Dispatcher.SendAsync(new RecordAnswerCommand(id, true, TimeSpan.FromSeconds(2)));
        await host.Dispatcher.SendAsync(new RecordAnswerCommand(id, false, TimeSpan.FromSeconds(6)));
        var last = await host.Dispatcher.SendAsync(new RecordAnswerCommand(id, true, TimeSpan.FromSeconds(4)));

        // The result carries the tally including the answer just given, not one behind it.
        last.Stats.Practice.Answered.ShouldBe(3);
        last.Stats.Practice.Correct.ShouldBe(2);
        last.Stats.Practice.Wrong.ShouldBe(1);
        last.Stats.Practice.Accuracy.ShouldBe(2 / 3d, 0.001);
        last.Stats.AverageSeconds!.Value.ShouldBe(4, 0.001);
        last.Stats.LastAnsweredUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task Overall_statistics_add_up_across_cards_and_subjects()
    {
        await using var host = await TestHost.CreateAsync();

        var a = await CreateCardAsync(host, "SQL", "A");
        var b = await CreateCardAsync(host, "SQL", "B");
        await CreateCardAsync(host, "SQL", "Never answered");

        await host.Dispatcher.SendAsync(new RecordAnswerCommand(a, true, TimeSpan.FromSeconds(1)));
        await host.Dispatcher.SendAsync(new RecordAnswerCommand(a, false, TimeSpan.FromSeconds(1)));
        await host.Dispatcher.SendAsync(new RecordAnswerCommand(b, true, TimeSpan.FromSeconds(1)));

        var overall = await host.Dispatcher.QueryAsync(new GetOverallStatsQuery());

        overall.Practice.Answered.ShouldBe(3);
        overall.Practice.Correct.ShouldBe(2);
        overall.Practice.Wrong.ShouldBe(1);
        overall.TotalCards.ShouldBe(3);
        overall.SubjectCount.ShouldBe(1);
        overall.CardsPractised.ShouldBe(2);
        overall.CardsUntouched.ShouldBe(1);
    }

    [Fact]
    public async Task A_multi_tagged_card_counts_towards_every_subject_it_wears()
    {
        await using var host = await TestHost.CreateAsync();

        var shared = await host.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            SubjectNames = ["SQL", "Databases"],
            Name = "Shared",
            CardType = CardType.Standard,
            Blocks = [Text(CardFace.Question, 0, "Q"), Text(CardFace.Answer, 0, "A")],
        });

        await host.Dispatcher.SendAsync(new RecordAnswerCommand(shared, true, TimeSpan.FromSeconds(1)));
        await host.Dispatcher.SendAsync(new RecordAnswerCommand(shared, false, TimeSpan.FromSeconds(1)));

        var subjects = await host.Dispatcher.QueryAsync(new GetSubjectStatsQuery());

        // Both tags report the same two answers — the card belongs to each of them fully.
        subjects.Count.ShouldBe(2);
        subjects.ShouldAllBe(s => s.Practice.Answered == 2 && s.Practice.Correct == 1);
        subjects.ShouldAllBe(s => s.CardsPractised == 1 && s.CardCount == 1);
    }

    [Fact]
    public async Task Clearing_a_cards_history_resets_only_that_card()
    {
        await using var host = await TestHost.CreateAsync();

        var kept = await CreateCardAsync(host, "SQL", "Kept");
        var wiped = await CreateCardAsync(host, "SQL", "Wiped");

        await host.Dispatcher.SendAsync(new RecordAnswerCommand(kept, true, TimeSpan.FromSeconds(1)));
        await host.Dispatcher.SendAsync(new RecordAnswerCommand(wiped, false, TimeSpan.FromSeconds(1)));

        await host.Dispatcher.SendAsync(new ClearCardHistoryCommand([wiped]));

        (await host.Dispatcher.QueryAsync(new GetCardStatsQuery(wiped))).Practice.HasHistory.ShouldBeFalse();
        (await host.Dispatcher.QueryAsync(new GetCardStatsQuery(kept))).Practice.Answered.ShouldBe(1);

        // The card itself survives; only its record went.
        (await host.Dispatcher.QueryAsync(new GetFlashcardDetailQuery(wiped))).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_custom_session_contains_exactly_the_cards_it_was_given()
    {
        await using var host = await TestHost.CreateAsync();

        var wanted = await CreateCardAsync(host, "SQL", "Wanted");
        var alsoWanted = await CreateCardAsync(host, "SQL", "Also wanted");
        await CreateCardAsync(host, "SQL", "Not wanted");
        await CreateCardAsync(host, "Other", "Different subject");

        var session = await host.Dispatcher.QueryAsync(new StartQuizSessionQuery(new QuizOptions
        {
            CardIds = [wanted, alsoWanted],
            MaxCards = 50,
        }));

        session.CardIds.OrderBy(id => id).ShouldBe(new[] { wanted, alsoWanted }.OrderBy(id => id));
    }

    [Fact]
    public async Task An_explicit_card_list_beats_a_subject_filter()
    {
        await using var host = await TestHost.CreateAsync();

        var pick = await CreateCardAsync(host, "SQL", "Picked");
        await CreateCardAsync(host, "SQL", "Ignored");
        var otherSubject = await SubjectIdAsync(host, "Other");

        // Subjects and cards disagree on purpose: naming cards is the more specific instruction,
        // so it wins outright rather than intersecting to nothing.
        var session = await host.Dispatcher.QueryAsync(new StartQuizSessionQuery(new QuizOptions
        {
            SubjectIds = [otherSubject],
            CardIds = [pick],
            MaxCards = 50,
        }));

        session.CardIds.ShouldBe([pick]);
    }

    [Fact]
    public async Task A_quick_session_with_no_scope_draws_from_the_whole_library()
    {
        await using var host = await TestHost.CreateAsync();

        await CreateCardAsync(host, "SQL", "One");
        await CreateCardAsync(host, "Databases", "Two");
        await CreateCardAsync(host, ".NET", "Three");

        // No subjects, no cards — the Random and Suggested modes rely on this meaning "everything".
        var session = await host.Dispatcher.QueryAsync(
            new StartQuizSessionQuery(new QuizOptions { MaxCards = 50 }));

        session.CardIds.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_quick_session_honours_its_card_count()
    {
        await using var host = await TestHost.CreateAsync();

        for (var i = 0; i < 6; i++)
        {
            await CreateCardAsync(host, "SQL", $"Card {i}");
        }

        var session = await host.Dispatcher.QueryAsync(
            new StartQuizSessionQuery(new QuizOptions { MaxCards = 4 }));

        session.CardIds.Count.ShouldBe(4);
    }

    [Fact]
    public async Task A_suspended_card_stays_out_even_when_named_explicitly()
    {
        await using var host = await TestHost.CreateAsync();

        var parked = await CreateCardAsync(host, "SQL", "Parked");
        await host.Dispatcher.SendAsync(new SetCardsSuspendedCommand([parked], true));

        var session = await host.Dispatcher.QueryAsync(new StartQuizSessionQuery(new QuizOptions
        {
            CardIds = [parked],
            MaxCards = 50,
        }));

        session.CardIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task Hardest_first_leads_with_the_card_answered_wrong_most()
    {
        await using var host = await TestHost.CreateAsync();

        var strong = await CreateCardAsync(host, "SQL", "Strong");
        var weak = await CreateCardAsync(host, "SQL", "Weak");
        var subjectId = await SubjectIdAsync(host, "SQL");

        await host.Dispatcher.SendAsync(new RecordAnswerCommand(strong, true, TimeSpan.FromSeconds(1)));
        await host.Dispatcher.SendAsync(new RecordAnswerCommand(strong, true, TimeSpan.FromSeconds(1)));
        await host.Dispatcher.SendAsync(new RecordAnswerCommand(weak, false, TimeSpan.FromSeconds(1)));
        await host.Dispatcher.SendAsync(new RecordAnswerCommand(weak, false, TimeSpan.FromSeconds(1)));

        var session = await host.Dispatcher.QueryAsync(new StartQuizSessionQuery(
            new QuizOptions { SubjectIds = [subjectId], HardestFirst = true }));

        session.CardIds[0].ShouldBe(weak);
    }

    [Fact]
    public async Task Suspended_cards_never_enter_the_quiz_queue()
    {
        await using var host = await TestHost.CreateAsync();
        var id = await CreateCardAsync(host, "SQL", "Parked");
        var subjectId = await SubjectIdAsync(host, "SQL");

        await host.Dispatcher.SendAsync(new SetCardsSuspendedCommand([id], true));

        var session = await host.Dispatcher.QueryAsync(new StartQuizSessionQuery(new QuizOptions { SubjectIds = [subjectId] }));

        session.CardIds.ShouldNotContain(id);
    }

    [Fact]
    public async Task A_quiz_card_carries_its_own_running_record()
    {
        await using var host = await TestHost.CreateAsync();
        var id = await CreateCardAsync(host, "SQL", "Tracked");

        await host.Dispatcher.SendAsync(new RecordAnswerCommand(id, true, TimeSpan.FromSeconds(2)));
        await host.Dispatcher.SendAsync(new RecordAnswerCommand(id, false, TimeSpan.FromSeconds(2)));

        var card = await host.Dispatcher.QueryAsync(new GetQuizCardQuery(id, false));

        card!.Stats.Practice.Answered.ShouldBe(2);
        card.Stats.Practice.Correct.ShouldBe(1);
    }

    [Fact]
    public async Task Identical_images_are_stored_once()
    {
        await using var host = await TestHost.CreateAsync();

        // A one-pixel PNG.
        var bytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        var first = await host.Dispatcher.SendAsync(new SaveMediaCommand(bytes, "a.png"));
        var second = await host.Dispatcher.SendAsync(new SaveMediaCommand(bytes.ToArray(), "b.png"));

        second.Id.ShouldBe(first.Id);
        first.MimeType.ShouldBe("image/png");

        (await host.Dispatcher.QueryAsync(new LoadMediaQuery(first.Id))).ShouldBe(bytes);
    }

    [Fact]
    public async Task An_image_block_survives_a_round_trip_with_its_stretch_and_height()
    {
        await using var host = await TestHost.CreateAsync();

        var bytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        var media = await host.Dispatcher.SendAsync(new SaveMediaCommand(bytes, "diagram.png"));

        var id = await host.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            SubjectNames = ["Diagrams"],
            Name = "Architecture diagram",
            CardType = CardType.Standard,
            Blocks =
            [
                Text(CardFace.Question, 0, "What does this show?"),
                new ContentBlockDto(Guid.Empty, CardFace.Answer, 0, ContentKind.Image, null, null,
                    media.Id, ImageStretch.UniformToFill, 260, "the diagram"),
            ],
        });

        var detail = await host.Dispatcher.QueryAsync(new GetFlashcardDetailQuery(id));
        var image = detail!.Blocks.Single(b => b.Kind == ContentKind.Image);

        image.MediaId.ShouldBe(media.Id);
        image.Stretch.ShouldBe(ImageStretch.UniformToFill);
        image.MaxHeight.ShouldBe(260);
        image.AltText.ShouldBe("the diagram");
    }

    [Fact]
    public async Task Subject_statistics_track_what_has_been_practised()
    {
        await using var host = await TestHost.CreateAsync();

        var a = await CreateCardAsync(host, "Counting", "A");
        await CreateCardAsync(host, "Counting", "B");

        var before = (await host.Dispatcher.QueryAsync(new GetSubjectStatsQuery())).Single();
        before.CardCount.ShouldBe(2);
        before.CardsPractised.ShouldBe(0);
        before.CardsUntouched.ShouldBe(2);
        before.Practice.HasHistory.ShouldBeFalse();

        await host.Dispatcher.SendAsync(new RecordAnswerCommand(a, false, TimeSpan.FromSeconds(1)));

        var after = (await host.Dispatcher.QueryAsync(new GetSubjectStatsQuery())).Single();
        after.CardsPractised.ShouldBe(1);
        after.CardsUntouched.ShouldBe(1);
        after.Practice.Answered.ShouldBe(1);
        after.Practice.Wrong.ShouldBe(1);
        after.Practice.Accuracy.ShouldBe(0);
    }

    [Fact]
    public async Task A_failed_save_leaves_nothing_behind()
    {
        await using var host = await TestHost.CreateAsync();

        // Multiple choice with no options: the aggregate rejects it after the blocks were staged.
        await Should.ThrowAsync<Exception>(() => host.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            SubjectNames = ["Atomicity"],
            Name = "Half written",
            CardType = CardType.MultipleChoice,
            Blocks = [Text(CardFace.Question, 0, "Pick one")],
        }));

        var results = await Search(host, "Half written");
        results.TotalCount.ShouldBe(0);

        // The tag was minted inside the same transaction, so it rolled back with the card.
        (await host.Dispatcher.QueryAsync(new GetSubjectsQuery())).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_seed_data_loads_and_is_idempotent()
    {
        await using var host = await TestHost.CreateAsync(seed: true);

        var subjects = await host.Dispatcher.QueryAsync(new GetSubjectsQuery());

        // Three tags across four cards: two of the cards wear both "SQLite vs T-SQL" and
        // "Databases", so the card counts sum to more than the number of cards.
        subjects.Count.ShouldBe(3);
        subjects.Sum(s => s.CardCount).ShouldBe(6);

        await ((Flashcards.Infrastructure.Persistence.SeedData)
            host.Services.GetService(typeof(Flashcards.Infrastructure.Persistence.SeedData))!)
            .EnsureSeededAsync();

        (await host.Dispatcher.QueryAsync(new GetSubjectsQuery())).Count.ShouldBe(3);
    }

    private static Task<PagedResult<FlashcardSummary>> Search(TestHost host, string text)
        => host.Dispatcher.QueryAsync(new SearchFlashcardsQuery(new FlashcardSearchCriteria { Text = text }));
}
