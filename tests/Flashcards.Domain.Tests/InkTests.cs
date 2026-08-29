using Flashcards.Domain.Common;
using System.Globalization;
using Flashcards.Domain.Cards;
using Shouldly;

namespace Flashcards.Domain.Tests;

public sealed class InkTests
{
    private static InkStroke Stroke(string color, double thickness, params (double X, double Y)[] points)
        => new(color, thickness, [.. points.Select(p => new InkPoint(p.X, p.Y))]);

    [Fact]
    public void Strokes_round_trip_through_the_stored_form()
    {
        InkStroke[] original =
        [
            Stroke("#4C9AFF", 2.5, (10, 20), (11.5, 22), (13, 25)),
            Stroke("#EF4444", 4, (80, 90), (82, 95)),
        ];

        var parsed = InkSerializer.Parse(InkSerializer.Serialize(original));

        parsed.Count.ShouldBe(2);
        parsed[0].ColorHex.ShouldBe("#4C9AFF");
        parsed[0].Thickness.ShouldBe(2.5);
        parsed[0].Points.Count.ShouldBe(3);
        parsed[0].Points[1].ShouldBe(new InkPoint(11.5, 22));
        parsed[1].ColorHex.ShouldBe("#EF4444");
        parsed[1].Points[1].ShouldBe(new InkPoint(82, 95));
    }

    [Fact]
    public void The_stored_form_is_culture_invariant()
    {
        var previous = CultureInfo.CurrentCulture;

        try
        {
            // A locale where the decimal separator is a comma — the same character that separates
            // x from y. If serialisation followed the ambient culture this would corrupt silently.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var serialized = InkSerializer.Serialize([Stroke("#000000", 1.5, (10.25, 20.5))]);

            serialized.ShouldContain("10.25,20.5");

            var parsed = InkSerializer.Parse(serialized);
            parsed.Single().Points.Single().ShouldBe(new InkPoint(10.25, 20.5));
            parsed.Single().Thickness.ShouldBe(1.5);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Empty_and_missing_ink_parse_to_nothing()
    {
        InkSerializer.Parse(null).ShouldBeEmpty();
        InkSerializer.Parse("").ShouldBeEmpty();
        InkSerializer.Parse("   ").ShouldBeEmpty();
        InkSerializer.Serialize([]).ShouldBe(string.Empty);
    }

    [Fact]
    public void A_malformed_stroke_is_skipped_rather_than_failing_the_whole_drawing()
    {
        // Second stroke has a junk thickness; third has a junk coordinate pair.
        var parsed = InkSerializer.Parse("#111111:2:1,1 2,2|#222222:banana:3,3|#333333:2:oops|#444444:2:9,9");

        parsed.Select(s => s.ColorHex).ShouldBe(["#111111", "#444444"]);
    }

    [Fact]
    public void Distance_is_measured_to_the_segment_not_only_the_sampled_points()
    {
        // A long straight stroke recorded with just two samples.
        var stroke = Stroke("#000000", 1, (0, 0), (100, 0));

        // Halfway along it, five units away: nowhere near either recorded point.
        stroke.DistanceTo(new InkPoint(50, 5)).ShouldBe(5, 0.001);

        // Past the end, the distance falls back to the endpoint.
        stroke.DistanceTo(new InkPoint(103, 4)).ShouldBe(5, 0.001);
    }

    [Fact]
    public void The_eraser_removes_whole_strokes_it_touches_and_leaves_the_rest()
    {
        InkStroke[] strokes =
        [
            Stroke("#aaaaaa", 1, (0, 0), (100, 0)),
            Stroke("#bbbbbb", 1, (0, 200), (100, 200)),
        ];

        // Touch the first stroke near its middle.
        var survivors = InkSerializer.Erase(strokes, new InkPoint(50, 3), radius: 8);

        survivors.Count.ShouldBe(1);
        survivors[0].ColorHex.ShouldBe("#bbbbbb");
    }

    [Fact]
    public void The_eraser_leaves_everything_alone_when_it_touches_nothing()
    {
        InkStroke[] strokes = [Stroke("#aaaaaa", 1, (0, 0), (100, 0))];

        InkSerializer.Erase(strokes, new InkPoint(50, 400), radius: 8).Count.ShouldBe(1);
    }

    [Fact]
    public void A_drawing_block_exposes_and_replaces_its_strokes()
    {
        var block = ContentBlock.CreateDrawing(CardFace.Question, 0, [Stroke("#4C9AFF", 2, (1, 1), (2, 2))]);

        block.Kind.ShouldBe(ContentKind.Drawing);
        block.IsDrawing.ShouldBeTrue();
        block.Strokes.Count.ShouldBe(1);
        block.IsBlankDrawing.ShouldBeFalse();

        // The ink layer covers the whole canvas; the strokes carry their own coordinates.
        block.Bounds!.Value.Width.ShouldBe(CardCanvas.Width);
        block.Bounds!.Value.Height.ShouldBe(CardCanvas.Height);

        block.ReplaceStrokes([]);

        block.Strokes.ShouldBeEmpty();
        block.IsBlankDrawing.ShouldBeTrue();
    }
}

public sealed class BlockBoundsTests
{
    [Fact]
    public void An_element_dragged_past_the_edge_is_parked_at_the_edge()
    {
        var bounds = BlockBounds.Create(CardCanvas.Width + 500, -80, 200, 100);

        bounds.X.ShouldBe(CardCanvas.Width - 200);
        bounds.Y.ShouldBe(0);
        bounds.Width.ShouldBe(200);
        bounds.Height.ShouldBe(100);
    }

    [Fact]
    public void An_element_larger_than_the_canvas_is_capped_to_it()
    {
        var bounds = BlockBounds.Create(0, 0, CardCanvas.Width * 3, CardCanvas.Height * 3);

        bounds.Width.ShouldBe(CardCanvas.Width);
        bounds.Height.ShouldBe(CardCanvas.Height);
        bounds.X.ShouldBe(0);
        bounds.Y.ShouldBe(0);
    }

    [Fact]
    public void An_element_cannot_be_shrunk_to_nothing()
    {
        var bounds = new BlockBounds(10, 10, 1, 1).ClampToCanvas();

        bounds.Width.ShouldBe(CardCanvas.MinElementSize);
        bounds.Height.ShouldBe(CardCanvas.MinElementSize);
    }

    [Fact]
    public void A_zero_or_negative_size_is_rejected_outright()
    {
        Should.Throw<DomainException>(() => BlockBounds.Create(0, 0, 0, 100));
        Should.Throw<DomainException>(() => BlockBounds.Create(0, 0, 100, -5));
    }
}
