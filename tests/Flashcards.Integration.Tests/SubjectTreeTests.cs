using Flashcards.Application.Cards.Commands;
using Flashcards.Application.Cards.Queries;
using Flashcards.Application.Contracts;
using Flashcards.Application.Quiz.Queries;
using Flashcards.Application.Stats.Queries;
using Flashcards.Application.Subjects.Commands;
using Flashcards.Application.Subjects.Queries;
using Flashcards.Domain.Cards;
using Flashcards.Domain.Common;
using Flashcards.Domain.Subjects;
using Shouldly;

namespace Flashcards.Integration.Tests;

/// <summary>
/// The subject tree, end to end through SQLite.
/// <para>
/// These earn their place because the interesting half of the feature lives in two recursive CTEs
/// — the closure that widens a selected subject to its descendants, and the reverse walk that gives
/// a card its inherited tags. Neither is reachable from the domain tests, and both are the kind of
/// SQL that is easy to get subtly wrong: a fan-out that double-counts, a UNION ALL that never
/// terminates, a filter applied at the wrong level.
/// </para>
/// </summary>
public sealed class SubjectTreeTests
{
    private static ContentBlockDto Text(CardFace face, int ordinal, string text)
        => new(Guid.Empty, face, ordinal, ContentKind.PlainText, text, null, null, ImageStretch.Uniform, null, null);

    private static Task<Guid> CardAsync(TestHost host, string subject, string name)
        => host.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            SubjectNames = [subject],
            Name = name,
            CardType = CardType.Standard,
            Blocks = [Text(CardFace.Question, 0, $"Q {name}"), Text(CardFace.Answer, 0, $"A {name}")],
        });

    /// <summary>Databases &gt; SQL &gt; { MSSQL, SQLite }, with one card filed at each level.</summary>
    private static async Task<(Guid Databases, Guid Sql, Guid Mssql, Guid Sqlite)> TreeAsync(TestHost host)
    {
        var databases = await host.Dispatcher.SendAsync(new CreateSubjectCommand("Databases"));
        var sql = await host.Dispatcher.SendAsync(new CreateSubjectCommand("SQL", databases));
        var mssql = await host.Dispatcher.SendAsync(new CreateSubjectCommand("MSSQL", sql));
        var sqlite = await host.Dispatcher.SendAsync(new CreateSubjectCommand("SQLite", sql));

        return (databases, sql, mssql, sqlite);
    }

    private static async Task<IReadOnlyList<string>> CardNamesUnderAsync(TestHost host, Guid subjectId)
    {
        var page = await host.Dispatcher.QueryAsync(new SearchFlashcardsQuery(new FlashcardSearchCriteria
        {
            SubjectIds = [subjectId],
            PageSize = 100,
        }));

        return [.. page.Items.Select(i => i.Name).OrderBy(n => n)];
    }

    [Fact]
    public async Task Selecting_a_parent_finds_the_cards_filed_under_its_children()
    {
        await using var host = await TestHost.CreateAsync();

        var (databases, sql, mssql, _) = await TreeAsync(host);

        await CardAsync(host, "MSSQL", "MERGE syntax");
        await CardAsync(host, "SQLite", "Upsert syntax");
        await CardAsync(host, "SQL", "What ACID stands for");

        // The whole point: a question about MSSQL is a SQL question and a database question,
        // without anything having been tagged twice.
        (await CardNamesUnderAsync(host, mssql)).ShouldBe(["MERGE syntax"]);
        (await CardNamesUnderAsync(host, sql)).ShouldBe(["MERGE syntax", "Upsert syntax", "What ACID stands for"]);
        (await CardNamesUnderAsync(host, databases)).ShouldBe(["MERGE syntax", "Upsert syntax", "What ACID stands for"]);
    }

    [Fact]
    public async Task A_card_reports_its_tag_plus_the_ancestors_that_come_with_it()
    {
        await using var host = await TestHost.CreateAsync();

        await TreeAsync(host);
        var card = await CardAsync(host, "MSSQL", "MERGE syntax");

        var detail = await host.Dispatcher.QueryAsync(new GetFlashcardDetailQuery(card));

        detail!.Subjects.Select(s => s.Name).ShouldBe(["Databases", "SQL", "MSSQL"]);

        // Only MSSQL was chosen, and only MSSQL can be taken off again. The other two are a
        // consequence of where MSSQL sits, which is what stops the designer offering to remove them.
        detail.Subjects.Single(s => s.Name == "MSSQL").IsInherited.ShouldBeFalse();
        detail.Subjects.Single(s => s.Name == "SQL").IsInherited.ShouldBeTrue();
        detail.Subjects.Single(s => s.Name == "Databases").IsInherited.ShouldBeTrue();
    }

    [Fact]
    public async Task A_subject_tagged_directly_stays_removable_even_when_also_inherited()
    {
        await using var host = await TestHost.CreateAsync();

        await TreeAsync(host);

        // Tagged with both a child and one of its own ancestors.
        var card = await host.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            SubjectNames = ["MSSQL", "SQL"],
            Name = "Both",
            CardType = CardType.Standard,
            Blocks = [Text(CardFace.Question, 0, "Q"), Text(CardFace.Answer, 0, "A")],
        });

        var detail = await host.Dispatcher.QueryAsync(new GetFlashcardDetailQuery(card));

        // SQL is reachable as MSSQL's ancestor, but it is also a tag the user applied, and being
        // implied by something else must not quietly take away the ability to remove it.
        detail!.Subjects.Single(s => s.Name == "SQL").IsInherited.ShouldBeFalse();
        detail.Subjects.Single(s => s.Name == "Databases").IsInherited.ShouldBeTrue();
    }

    [Fact]
    public async Task Re_parenting_a_subject_re_files_every_card_beneath_it_with_no_rewrite()
    {
        await using var host = await TestHost.CreateAsync();

        var (databases, sql, _, _) = await TreeAsync(host);
        var other = await host.Dispatcher.SendAsync(new CreateSubjectCommand("Theory"));

        var card = await CardAsync(host, "MSSQL", "MERGE syntax");

        (await CardNamesUnderAsync(host, databases)).ShouldBe(["MERGE syntax"]);
        (await CardNamesUnderAsync(host, other)).ShouldBeEmpty();

        // Move the middle of the branch. Nothing touches card_subjects.
        await host.Dispatcher.SendAsync(new MoveSubjectCommand(sql, other));

        (await CardNamesUnderAsync(host, other)).ShouldBe(["MERGE syntax"]);
        (await CardNamesUnderAsync(host, databases)).ShouldBeEmpty();

        // The card's own tag is exactly what it always was; only what it inherits has changed.
        var detail = await host.Dispatcher.QueryAsync(new GetFlashcardDetailQuery(card));

        detail!.Subjects.Single(s => !s.IsInherited).Name.ShouldBe("MSSQL");
        detail.Subjects.Select(s => s.Name).ShouldContain("Theory");
        detail.Subjects.Select(s => s.Name).ShouldNotContain("Databases");
    }

    [Fact]
    public async Task Subject_statistics_roll_up_the_subtree_without_double_counting()
    {
        await using var host = await TestHost.CreateAsync();

        var (databases, sql, _, _) = await TreeAsync(host);

        await CardAsync(host, "MSSQL", "One");
        await CardAsync(host, "SQLite", "Two");

        // Tagged with both a parent and its child, so it reaches SQL by two routes through the
        // closure. It is still one card.
        await host.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            SubjectNames = ["SQL", "MSSQL"],
            Name = "Three",
            CardType = CardType.Standard,
            Blocks = [Text(CardFace.Question, 0, "Q"), Text(CardFace.Answer, 0, "A")],
        });

        var stats = await host.Dispatcher.QueryAsync(new GetSubjectStatsQuery());

        stats.Single(s => s.Id == sql).CardCount.ShouldBe(3);
        stats.Single(s => s.Id == databases).CardCount.ShouldBe(3);

        // The direct figure is the un-rolled-up one: only "Three" wears SQL itself.
        stats.Single(s => s.Id == sql).DirectCardCount.ShouldBe(1);
        stats.Single(s => s.Id == databases).DirectCardCount.ShouldBe(0);

        // Depth and parentage travel with the row so the study grid can indent without re-walking.
        stats.Single(s => s.Id == databases).Depth.ShouldBe(1);
        stats.Single(s => s.Id == sql).Depth.ShouldBe(2);
        stats.Single(s => s.Id == sql).ParentId.ShouldBe(databases);
        stats.Single(s => s.Id == sql).HasChildren.ShouldBeTrue();
    }

    [Fact]
    public async Task A_quiz_drawn_from_a_parent_queues_the_cards_under_its_children()
    {
        await using var host = await TestHost.CreateAsync();

        var (_, sql, _, _) = await TreeAsync(host);

        await CardAsync(host, "MSSQL", "One");
        await CardAsync(host, "SQLite", "Two");

        var session = await host.Dispatcher.QueryAsync(new StartQuizSessionQuery(new QuizOptions
        {
            SubjectIds = [sql],
            MaxCards = 50,
        }));

        session.CardIds.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Deleting_a_subject_promotes_its_children_into_its_place()
    {
        await using var host = await TestHost.CreateAsync();

        var (databases, sql, mssql, sqlite) = await TreeAsync(host);

        await CardAsync(host, "MSSQL", "MERGE syntax");

        await host.Dispatcher.SendAsync(new DeleteSubjectCommand(sql));

        var subjects = await host.Dispatcher.QueryAsync(new GetSubjectsQuery());

        subjects.Select(s => s.Name).ShouldNotContain("SQL");

        // MSSQL and SQLite moved up to where SQL was, rather than being deleted with it or
        // scattered to the top level.
        subjects.Single(s => s.Id == mssql).ParentId.ShouldBe(databases);
        subjects.Single(s => s.Id == sqlite).ParentId.ShouldBe(databases);
        subjects.Single(s => s.Id == mssql).Depth.ShouldBe(2);

        // The card kept its tag and is still found from the top of the branch.
        (await CardNamesUnderAsync(host, databases)).ShouldBe(["MERGE syntax"]);
    }

    [Fact]
    public async Task Deleting_a_subject_moves_its_cards_up_rather_than_untagging_them()
    {
        await using var host = await TestHost.CreateAsync();

        var (databases, sql, _, _) = await TreeAsync(host);
        var card = await CardAsync(host, "SQL", "What ACID stands for");

        await host.Dispatcher.SendAsync(new DeleteSubjectCommand(sql));

        // The card was tagged only "SQL". Deleting it used to drop the row through the join
        // table's cascade and leave the card wearing nothing at all.
        var detail = await host.Dispatcher.QueryAsync(new GetFlashcardDetailQuery(card));

        detail!.Subjects.ShouldNotBeEmpty();
        detail.Subjects.Single(s => !s.IsInherited).Name.ShouldBe("Databases");

        // And it is still reachable from where it now lives.
        (await CardNamesUnderAsync(host, databases)).ShouldBe(["What ACID stands for"]);
    }

    [Fact]
    public async Task A_card_already_wearing_the_parent_is_not_duplicated_by_the_promotion()
    {
        await using var host = await TestHost.CreateAsync();

        var (_, sql, _, _) = await TreeAsync(host);

        // Wears both the subject being deleted and the parent it would be promoted to.
        var card = await host.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            SubjectNames = ["SQL", "Databases"],
            Name = "Both",
            CardType = CardType.Standard,
            Blocks = [Text(CardFace.Question, 0, "Q"), Text(CardFace.Answer, 0, "A")],
        });

        await host.Dispatcher.SendAsync(new DeleteSubjectCommand(sql));

        var detail = await host.Dispatcher.QueryAsync(new GetFlashcardDetailQuery(card));

        detail!.Subjects.Count(s => s.Name == "Databases").ShouldBe(1);
    }

    [Fact]
    public async Task Deleting_a_top_level_subject_is_refused_when_a_card_wears_only_it()
    {
        await using var host = await TestHost.CreateAsync();

        var solo = await host.Dispatcher.SendAsync(new CreateSubjectCommand("Networking"));

        await CardAsync(host, "Networking", "What a MAC address is");
        await CardAsync(host, "Networking", "What ARP does");

        // Nowhere to promote them to, and a card with no subject is not a state the domain allows,
        // so the delete is refused rather than quietly stranding them.
        var error = await Should.ThrowAsync<DomainException>(
            host.Dispatcher.SendAsync(new DeleteSubjectCommand(solo)));

        // The message names the cards, so it can be acted on without hunting for them.
        error.Message.ShouldContain("What a MAC address is");
        error.Message.ShouldContain("What ARP does");
        error.Message.ShouldContain("Networking");

        // Nothing was destroyed on the way to refusing.
        (await host.Dispatcher.QueryAsync(new GetSubjectsQuery()))
            .Select(s => s.Name).ShouldContain("Networking");
    }

    [Fact]
    public async Task A_top_level_subject_whose_cards_have_another_tag_still_deletes()
    {
        await using var host = await TestHost.CreateAsync();

        await TreeAsync(host);
        var spare = await host.Dispatcher.SendAsync(new CreateSubjectCommand("Interview prep"));

        // The card keeps MSSQL when "Interview prep" goes, so it is never at risk and must not be
        // listed as a blocker — the refusal is about cards that would end up with nothing.
        var card = await host.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            SubjectNames = ["MSSQL", "Interview prep"],
            Name = "MERGE syntax",
            CardType = CardType.Standard,
            Blocks = [Text(CardFace.Question, 0, "Q"), Text(CardFace.Answer, 0, "A")],
        });

        await host.Dispatcher.SendAsync(new DeleteSubjectCommand(spare));

        var detail = await host.Dispatcher.QueryAsync(new GetFlashcardDetailQuery(card));

        detail!.Subjects.Select(s => s.Name).ShouldNotContain("Interview prep");
        detail.Subjects.Single(s => !s.IsInherited).Name.ShouldBe("MSSQL");
    }

    [Fact]
    public async Task An_empty_top_level_subject_still_deletes()
    {
        await using var host = await TestHost.CreateAsync();

        var empty = await host.Dispatcher.SendAsync(new CreateSubjectCommand("Unused"));

        await host.Dispatcher.SendAsync(new DeleteSubjectCommand(empty));

        (await host.Dispatcher.QueryAsync(new GetSubjectsQuery()))
            .Select(s => s.Name).ShouldNotContain("Unused");
    }

    [Fact]
    public async Task Deleting_a_root_promotes_its_children_to_roots()
    {
        await using var host = await TestHost.CreateAsync();

        var (databases, sql, _, _) = await TreeAsync(host);

        await host.Dispatcher.SendAsync(new DeleteSubjectCommand(databases));

        var subjects = await host.Dispatcher.QueryAsync(new GetSubjectsQuery());

        subjects.Single(s => s.Id == sql).ParentId.ShouldBeNull();
        subjects.Single(s => s.Id == sql).Depth.ShouldBe(1);
    }

    [Fact]
    public async Task A_duplicate_or_blank_subject_name_is_refused()
    {
        await using var host = await TestHost.CreateAsync();

        await host.Dispatcher.SendAsync(new CreateSubjectCommand("Databases"));

        // Case-insensitively, because the name column is COLLATE NOCASE and two subjects that
        // differ only by case would be indistinguishable on a chip.
        await Should.ThrowAsync<DomainException>(
            host.Dispatcher.SendAsync(new CreateSubjectCommand("databases")));

        await Should.ThrowAsync<DomainException>(
            host.Dispatcher.SendAsync(new CreateSubjectCommand("   ")));

        // Whitespace is trimmed rather than being treated as part of the name.
        await Should.ThrowAsync<DomainException>(
            host.Dispatcher.SendAsync(new CreateSubjectCommand("  Databases  ")));
    }

    [Fact]
    public async Task The_depth_limit_is_enforced_through_the_dispatcher()
    {
        await using var host = await TestHost.CreateAsync();

        Guid? parent = null;

        for (var level = 0; level < SubjectHierarchy.MaxDepth; level++)
        {
            parent = await host.Dispatcher.SendAsync(new CreateSubjectCommand($"level{level + 1}", parent));
        }

        await Should.ThrowAsync<DomainException>(
            host.Dispatcher.SendAsync(new CreateSubjectCommand("too deep", parent)));
    }

    [Fact]
    public async Task A_subject_cannot_be_moved_inside_its_own_branch()
    {
        await using var host = await TestHost.CreateAsync();

        var (databases, _, mssql, _) = await TreeAsync(host);

        await Should.ThrowAsync<DomainException>(
            host.Dispatcher.SendAsync(new MoveSubjectCommand(databases, mssql)));
    }

    [Fact]
    public async Task Renaming_keeps_the_branch_and_refuses_a_name_already_in_use()
    {
        await using var host = await TestHost.CreateAsync();

        var (databases, sql, _, _) = await TreeAsync(host);

        await host.Dispatcher.SendAsync(new RenameSubjectCommand(sql, "Relational"));

        var subjects = await host.Dispatcher.QueryAsync(new GetSubjectsQuery());

        subjects.Single(s => s.Id == sql).Name.ShouldBe("Relational");
        subjects.Single(s => s.Id == sql).ParentId.ShouldBe(databases);

        await Should.ThrowAsync<DomainException>(
            host.Dispatcher.SendAsync(new RenameSubjectCommand(sql, "Databases")));

        // Saving a subject under the name it already has is not a clash with itself.
        await host.Dispatcher.SendAsync(new RenameSubjectCommand(sql, "Relational"));
    }
}
