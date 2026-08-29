using Flashcards.Domain.Cards;
using Flashcards.Domain.Common;
using Shouldly;

namespace Flashcards.Domain.Tests;

public sealed class FlashcardTests
{
    private static Flashcard NewCard(CardType type = CardType.Standard)
        => Flashcard.Create([Guid.CreateVersion7()], "Test card", type);

    [Fact]
    public void A_card_must_be_created_with_at_least_one_subject()
        => Should.Throw<DomainException>(() => Flashcard.Create([], "Untagged", CardType.Standard));

    [Fact]
    public void Subject_tags_are_de_duplicated_and_ignore_the_empty_guid()
    {
        var sql = Guid.CreateVersion7();

        var card = Flashcard.Create([sql, sql, Guid.Empty], "Tagged", CardType.Standard);

        card.SubjectIds.Count.ShouldBe(1);
        card.SubjectIds.ShouldContain(sql);
    }

    [Fact]
    public void A_card_can_carry_several_subject_tags()
    {
        var sql = Guid.CreateVersion7();
        var databases = Guid.CreateVersion7();

        var card = Flashcard.Create([sql], "Tagged", CardType.Standard);
        card.AddSubject(databases);

        card.SubjectIds.Count.ShouldBe(2);
        card.SubjectIds.ShouldContain(databases);
    }

    [Fact]
    public void Removing_the_last_subject_tag_is_refused()
    {
        var sql = Guid.CreateVersion7();
        var databases = Guid.CreateVersion7();

        var card = Flashcard.Create([sql, databases], "Tagged", CardType.Standard);

        card.RemoveSubject(databases);
        card.SubjectIds.Count.ShouldBe(1);

        // The one remaining tag is load-bearing: an untagged card would be unreachable.
        Should.Throw<DomainException>(() => card.RemoveSubject(sql));
        card.SubjectIds.Count.ShouldBe(1);
    }

    [Fact]
    public void Replacing_the_subject_tags_with_an_empty_set_is_refused()
    {
        var card = NewCard();

        Should.Throw<DomainException>(() => card.SetSubjects([]));
    }

    [Fact]
    public void A_standard_card_needs_both_sides()
    {
        var card = NewCard();
        card.AddTextBlock(CardFace.Question, ContentKind.PlainText, "Question?");

        card.Validate().ShouldContain(e => e.Contains("answer block"));

        card.AddTextBlock(CardFace.Answer, ContentKind.PlainText, "Answer.");

        card.Validate().ShouldBeEmpty();
    }

    [Fact]
    public void Blocks_keep_dense_ordinals_after_a_removal()
    {
        var card = NewCard();
        var first = card.AddTextBlock(CardFace.Question, ContentKind.PlainText, "one");
        var second = card.AddTextBlock(CardFace.Question, ContentKind.PlainText, "two");
        var third = card.AddTextBlock(CardFace.Question, ContentKind.PlainText, "three");

        card.RemoveBlock(second.Id);

        card.QuestionBlocks.Select(b => b.Ordinal).ShouldBe(new[] { 0, 1 });
        card.QuestionBlocks.Select(b => b.Id).ShouldBe(new[] { first.Id, third.Id });
    }

    [Fact]
    public void Moving_a_block_reorders_only_its_own_face()
    {
        var card = NewCard();
        card.AddTextBlock(CardFace.Question, ContentKind.PlainText, "q1");
        var q2 = card.AddTextBlock(CardFace.Question, ContentKind.PlainText, "q2");
        card.AddTextBlock(CardFace.Answer, ContentKind.PlainText, "a1");

        card.MoveBlock(q2.Id, -1);

        card.QuestionBlocks.First().Text.ShouldBe("q2");
        card.AnswerBlocks.Single().Ordinal.ShouldBe(0);
    }

    [Fact]
    public void One_face_cannot_exceed_the_block_ceiling()
    {
        var card = NewCard();

        for (var i = 0; i < Flashcard.MaxBlocksPerFace; i++)
        {
            card.AddTextBlock(CardFace.Question, ContentKind.PlainText, $"block {i}");
        }

        Should.Throw<DomainException>(() => card.AddTextBlock(CardFace.Question, ContentKind.PlainText, "one too many"));
    }

    [Fact]
    public void Multiple_choice_needs_two_options_and_a_wrong_one()
    {
        var card = NewCard(CardType.MultipleChoice);
        card.AddTextBlock(CardFace.Question, ContentKind.PlainText, "Pick one");

        card.Validate().ShouldContain(e => e.Contains("at least two options"));

        card.ReplaceChoices([ChoiceOption.Create(0, "a", true), ChoiceOption.Create(1, "b", true)]);
        card.Validate().ShouldContain(e => e.Contains("must be incorrect"));

        card.ReplaceChoices([ChoiceOption.Create(0, "a", true), ChoiceOption.Create(1, "b", false)]);
        card.Validate().ShouldBeEmpty();
    }

    [Fact]
    public void Switching_away_from_multiple_choice_drops_the_options()
    {
        var card = NewCard(CardType.MultipleChoice);
        card.ReplaceChoices([ChoiceOption.Create(0, "a", true), ChoiceOption.Create(1, "b", false)]);

        card.ChangeType(CardType.Standard);

        card.Choices.ShouldBeEmpty();
    }

    [Fact]
    public void Cloze_blanks_are_numbered_across_every_question_block()
    {
        var card = NewCard(CardType.Cloze);
        card.AddTextBlock(CardFace.Question, ContentKind.PlainText, "The {{first}} and the {{second}}.");
        card.AddTextBlock(CardFace.Question, ContentKind.PlainText, "Then a {{third}}.");
        // Answer-side braces must not be counted.
        card.AddTextBlock(CardFace.Answer, ContentKind.PlainText, "Not a {{blank}}.");

        card.ClozeBlanks.Select(b => b.Index).ShouldBe(new[] { 1, 2, 3 });
        card.ClozeBlanks.Select(b => b.Answer).ShouldBe(new[] { "first", "second", "third" });
    }

    [Fact]
    public void A_cloze_card_without_blanks_is_invalid()
    {
        var card = NewCard(CardType.Cloze);
        card.AddTextBlock(CardFace.Question, ContentKind.PlainText, "No blanks here.");

        card.Validate().ShouldContain(e => e.Contains("{{blank}}"));
    }

    [Fact]
    public void An_image_block_carries_no_text_and_keeps_its_layout()
    {
        var card = NewCard();
        var mediaId = Guid.CreateVersion7();

        var block = card.AddImageBlock(CardFace.Question, mediaId, ImageStretch.UniformToFill, 300, "a diagram");

        block.IsImage.ShouldBeTrue();
        block.Text.ShouldBeNull();
        block.MediaId.ShouldBe(mediaId);
        block.Stretch.ShouldBe(ImageStretch.UniformToFill);
        block.MaxHeight.ShouldBe(300);
        Should.Throw<DomainException>(() => block.UpdateText("nope"));
    }
}
