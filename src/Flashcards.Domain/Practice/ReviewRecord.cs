using Flashcards.Domain.Common;

namespace Flashcards.Domain.Practice;

/// <summary>
/// One answer, as it happened. Append-only.
/// <para>
/// This replaced the spaced-repetition state that used to live alongside each card. The app no
/// longer decides when something is due — you study what you feel like, when you feel like it — so
/// there is nothing to schedule and nothing to carry forward between sittings. What remains worth
/// keeping is the plain history of what was asked and whether it was answered correctly, because
/// that is what the statistics are computed from.
/// </para>
/// <para>
/// Correctness is a boolean rather than a graded scale. The old four-point Again/Hard/Good/Easy
/// scale existed to feed an ease factor into the SM-2 interval calculation; with that gone it had
/// no consumer, and "how often do I get this right" only ever needed two answers.
/// </para>
/// </summary>
public sealed record ReviewRecord
{
    private ReviewRecord(Guid cardId, DateTimeOffset reviewedUtc, bool wasCorrect, TimeSpan elapsed)
    {
        CardId = cardId;
        ReviewedUtc = reviewedUtc;
        WasCorrect = wasCorrect;
        Elapsed = elapsed;
    }

    public Guid CardId { get; }

    public DateTimeOffset ReviewedUtc { get; }

    public bool WasCorrect { get; }

    /// <summary>How long the card was on screen before it was answered.</summary>
    public TimeSpan Elapsed { get; }

    public static ReviewRecord Create(Guid cardId, bool wasCorrect, TimeSpan elapsed, DateTimeOffset reviewedUtc)
    {
        if (cardId == Guid.Empty)
        {
            throw new DomainException("A review has to belong to a card.");
        }

        // A clock that jumped, or a card left open overnight, should not poison the timing stats.
        var clamped = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;

        return new ReviewRecord(cardId, reviewedUtc, wasCorrect, clamped);
    }
}
