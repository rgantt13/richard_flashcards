using Flashcards.Domain.Common;
using Flashcards.Domain.Subjects;
using Shouldly;

namespace Flashcards.Domain.Tests;

public sealed class SubjectHierarchyTests
{
    // Databases > SQL > { MSSQL, SQLite }, plus an unrelated root. Fixed ids so a failure names the
    // same subject every run.
    private static readonly Guid Databases = new("00000001-0000-0000-0000-000000000000");
    private static readonly Guid Sql = new("00000002-0000-0000-0000-000000000000");
    private static readonly Guid Mssql = new("00000003-0000-0000-0000-000000000000");
    private static readonly Guid Sqlite = new("00000004-0000-0000-0000-000000000000");
    private static readonly Guid Networking = new("00000005-0000-0000-0000-000000000000");

    private static SubjectHierarchy Sample() => new(
    [
        new SubjectPlacement(Databases, null, "Databases"),
        new SubjectPlacement(Sql, Databases, "SQL"),
        new SubjectPlacement(Mssql, Sql, "MSSQL"),
        new SubjectPlacement(Sqlite, Sql, "SQLite"),
        new SubjectPlacement(Networking, null, "Networking"),
    ]);

    /// <summary>A chain of <paramref name="levels"/> subjects, each the child of the one before.</summary>
    private static (SubjectHierarchy Tree, Guid[] Ids) Chain(int levels)
    {
        var ids = Enumerable.Range(0, levels).Select(_ => Guid.CreateVersion7()).ToArray();

        var placements = ids.Select((id, i) =>
            new SubjectPlacement(id, i == 0 ? null : ids[i - 1], $"level{i + 1}"));

        return (new SubjectHierarchy(placements), ids);
    }

    [Fact]
    public void Depth_counts_from_one_at_the_root()
    {
        var tree = Sample();

        tree.DepthOf(Databases).ShouldBe(1);
        tree.DepthOf(Sql).ShouldBe(2);
        tree.DepthOf(Mssql).ShouldBe(3);
        tree.DepthOf(Guid.NewGuid()).ShouldBe(0);
    }

    [Fact]
    public void A_cards_inherited_subjects_are_its_tags_ancestors()
    {
        // This is the whole feature: tagging a card MSSQL is what makes it answer to SQL and
        // Databases, without anything being written to the card.
        Sample().AncestorsOf(Mssql).ShouldBe([Sql, Databases]);
        Sample().AncestorsOf(Databases).ShouldBeEmpty();
    }

    [Fact]
    public void Selecting_a_subject_selects_everything_beneath_it()
    {
        var scope = Sample().WithDescendants(Sql);

        scope.ShouldContain(Sql);
        scope.ShouldContain(Mssql);
        scope.ShouldContain(Sqlite);
        scope.ShouldNotContain(Databases);
        scope.ShouldNotContain(Networking);
    }

    [Fact]
    public void Tree_order_is_depth_first_with_siblings_by_name()
    {
        var ordered = Sample().InTreeOrder();

        ordered.Select(o => o.Subject.Name).ShouldBe(
            ["Databases", "SQL", "MSSQL", "SQLite", "Networking"]);

        // The depth travelling alongside each row is what the views indent by.
        ordered.Select(o => o.Depth).ShouldBe([1, 2, 3, 3, 1]);
    }

    [Fact]
    public void A_subject_cannot_be_dropped_onto_its_own_descendant()
    {
        // Allowing this would detach Databases > SQL from the tree entirely — both would point at
        // each other and neither would have a route to a root.
        Sample().CanMove(Databases, Mssql, out var reason).ShouldBeFalse();
        reason.ShouldContain("cannot be placed inside its own child");
    }

    [Fact]
    public void A_subject_cannot_be_dropped_onto_itself()
    {
        Sample().CanMove(Sql, Sql, out var reason).ShouldBeFalse();
        reason.ShouldContain("inside itself");
    }

    [Fact]
    public void Promoting_to_the_top_is_always_allowed()
        => Sample().CanMove(Mssql, null, out _).ShouldBeTrue();

    [Fact]
    public void Moving_somewhere_it_already_is_reports_rather_than_churning()
    {
        Sample().CanMove(Mssql, Sql, out var reason).ShouldBeFalse();
        reason.ShouldContain("already there");
    }

    [Fact]
    public void The_depth_limit_counts_the_height_of_the_branch_being_moved()
    {
        // Networking is a leaf, so it fits under MSSQL: that lands it at level 4.
        Sample().CanMove(Networking, Mssql, out _).ShouldBeTrue();

        // Now give Networking two levels of its own, keeping it unrelated to the SQL branch so the
        // depth rule is what decides this and not the cycle rule.
        var wifi = new Guid("00000006-0000-0000-0000-000000000000");
        var wpa = new Guid("00000007-0000-0000-0000-000000000000");

        var tree = new SubjectHierarchy(
        [
            new SubjectPlacement(Databases, null, "Databases"),
            new SubjectPlacement(Sql, Databases, "SQL"),
            new SubjectPlacement(Mssql, Sql, "MSSQL"),
            new SubjectPlacement(Networking, null, "Networking"),
            new SubjectPlacement(wifi, Networking, "WiFi"),
            new SubjectPlacement(wpa, wifi, "WPA"),
        ]);

        tree.HeightOf(Networking).ShouldBe(3);

        // MSSQL sits at level 3 and the branch is 3 tall, so WPA would land at level 6. The move is
        // judged by the deepest leaf being dragged, not by the subject you grabbed.
        tree.CanMove(Networking, Mssql, out var reason).ShouldBeFalse();
        reason.ShouldContain("6 levels deep");

        // The same branch fits one level higher up, where its leaves land exactly on the limit.
        tree.CanMove(Networking, Sql, out _).ShouldBeTrue();
    }

    [Fact]
    public void A_full_depth_chain_is_legal_and_one_more_is_not()
    {
        var (tree, ids) = Chain(SubjectHierarchy.MaxDepth);

        tree.DepthOf(ids[^1]).ShouldBe(SubjectHierarchy.MaxDepth);
        tree.HeightOf(ids[0]).ShouldBe(SubjectHierarchy.MaxDepth);

        // Nothing new may go under the deepest level.
        tree.CanAddUnder(ids[^1], out var reason).ShouldBeFalse();
        reason.ShouldContain($"{SubjectHierarchy.MaxDepth} levels deep");

        // But the level above still has room.
        tree.CanAddUnder(ids[^2], out _).ShouldBeTrue();
    }

    [Fact]
    public void A_new_subject_at_the_top_is_always_allowed()
        => Sample().CanAddUnder(null, out _).ShouldBeTrue();

    [Fact]
    public void The_throwing_forms_report_the_same_reason()
    {
        var tree = Sample();

        Should.Throw<DomainException>(() => tree.EnsureCanMove(Databases, Mssql))
            .Message.ShouldContain("cannot be placed inside its own child");

        var (chain, ids) = Chain(SubjectHierarchy.MaxDepth);

        Should.Throw<DomainException>(() => chain.EnsureCanAddUnder(ids[^1]));
    }

    [Fact]
    public void A_parent_pointing_at_a_subject_that_is_not_present_is_treated_as_a_root()
    {
        // Rather than silently dropping the branch from every screen that renders the tree.
        var orphan = Guid.CreateVersion7();
        var tree = new SubjectHierarchy([new SubjectPlacement(orphan, Guid.CreateVersion7(), "Stray")]);

        tree.InTreeOrder().Single().Subject.Id.ShouldBe(orphan);
        tree.DepthOf(orphan).ShouldBe(1);
    }

    [Fact]
    public void A_cycle_that_somehow_reached_storage_does_not_hang_the_walk()
    {
        // Nothing should be able to write this, but a read path that spins forever turns a bad row
        // into a frozen application, so the walk is bounded by what it has already seen.
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();

        var tree = new SubjectHierarchy(
        [
            new SubjectPlacement(a, b, "A"),
            new SubjectPlacement(b, a, "B"),
        ]);

        tree.AncestorsOf(a).Count.ShouldBeLessThanOrEqualTo(2);
        tree.WithDescendants(a).Count.ShouldBeLessThanOrEqualTo(2);
    }
}
