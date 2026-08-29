using Flashcards.Domain.Cards;
using Shouldly;

namespace Flashcards.Domain.Tests;

public sealed class ClozeParserTests
{
    [Fact]
    public void Two_blanks_on_one_line_do_not_merge()
    {
        // The regex is non-greedy for exactly this reason.
        var blanks = ClozeParser.Parse("A {{one}} and a {{two}}.");

        blanks.Count.ShouldBe(2);
        blanks[0].Answer.ShouldBe("one");
        blanks[1].Answer.ShouldBe("two");
    }

    [Fact]
    public void A_double_colon_separates_the_answer_from_a_hint()
    {
        var blank = ClozeParser.Parse("The capital is {{Paris::a city}}.").Single();

        blank.Answer.ShouldBe("Paris");
        blank.Hint.ShouldBe("a city");
    }

    [Fact]
    public void The_prompt_shows_underscores_or_the_hint()
    {
        ClozeParser.RenderPrompt("It is {{Paris}}.").ShouldBe("It is _____.");
        ClozeParser.RenderPrompt("It is {{Paris::a city}}.").ShouldBe("It is [a city].");
    }

    [Fact]
    public void Underscore_runs_are_clamped_so_length_is_not_a_giveaway()
    {
        ClozeParser.RenderPrompt("{{ok}}").ShouldBe("___");
        ClozeParser.RenderPrompt("{{supercalifragilisticexpialidocious}}").ShouldBe(new string('_', 16));
    }

    [Fact]
    public void Revealing_one_blank_leaves_the_others_hidden()
    {
        ClozeParser.RenderPrompt("{{a}} then {{bb}}", revealIndex: 2).ShouldBe("___ then bb");
    }

    [Fact]
    public void The_solution_fills_every_blank_and_strips_hints()
        => ClozeParser.RenderSolution("It is {{Paris::a city}} in {{France}}.").ShouldBe("It is Paris in France.");

    [Fact]
    public void Wrapping_a_selection_produces_markup()
    {
        const string Text = "The capital is Paris.";
        var start = Text.IndexOf("Paris", StringComparison.Ordinal);

        ClozeParser.Wrap(Text, start, 5).ShouldBe("The capital is {{Paris}}.");
        ClozeParser.Wrap(Text, start, 5, "a city").ShouldBe("The capital is {{Paris::a city}}.");
    }

    [Fact]
    public void Wrapping_an_out_of_range_selection_is_a_no_op()
        => ClozeParser.Wrap("short", 3, 99).ShouldBe("short");

    [Fact]
    public void Text_without_braces_round_trips_unchanged()
    {
        ClozeParser.HasBlanks("plain text").ShouldBeFalse();
        ClozeParser.RenderPrompt("plain text").ShouldBe("plain text");
        ClozeParser.RenderSolution("plain text").ShouldBe("plain text");
    }
}
