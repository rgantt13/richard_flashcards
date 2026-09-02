using Flashcards.Application.Contracts;

namespace Flashcards.Desktop.ViewModels.Statistics;

/// <summary>One subject that leads on some measure, with the figure that earned it the place.</summary>
public sealed record SubjectLeader(string Name, string? ColorHex, Guid Id, string Figure, string Detail);

/// <summary>
/// The subjects that stand out, across the whole library.
/// <para>
/// Derived from the same <see cref="SubjectStats"/> the subject tier already loads rather than
/// asked for separately. There are a handful of subjects, the figures are all present, and a second
/// query would be a second thing to keep in step with the first.
/// </para>
/// <para>
/// Every leader is nullable. A library with no answers in it has no best subject, and inventing one
/// — a subject at 0% "leading" on accuracy — would be worse than an honest blank.
/// </para>
/// </summary>
public sealed record SubjectHighlights(
    SubjectLeader? MostCards,
    SubjectLeader? MostAnswered,
    SubjectLeader? BestAccuracy,
    SubjectLeader? NeedsWork)
{
    public static SubjectHighlights Empty { get; } = new(null, null, null, null);

    public bool HasAnything => MostCards is not null || MostAnswered is not null;

    public static SubjectHighlights From(IReadOnlyList<SubjectStats> subjects)
    {
        if (subjects.Count == 0)
        {
            return Empty;
        }

        var withCards = subjects.Where(s => s.CardCount > 0).ToList();

        // Accuracy over a handful of answers is noise, so the two accuracy leaders only consider
        // subjects with a real record behind them. The count travels with the figure either way,
        // so a thin one is visible as thin rather than presented as a verdict.
        var practised = subjects.Where(s => s.Practice.Answered >= MinimumAnswers).ToList();

        return new SubjectHighlights(
            Best(withCards, s => s.CardCount, s => Leader(s, Cards(s.CardCount), "in this subject and everything under it")),
            Best(subjects.Where(s => s.Practice.Answered > 0).ToList(), s => s.Practice.Answered,
                s => Leader(s, Answers(s.Practice.Answered), $"{s.Practice.Correct} of them right")),
            Best(practised, s => s.Practice.Accuracy,
                s => Leader(s, $"{s.Practice.Accuracy:P0}", $"over {Answers(s.Practice.Answered)}")),
            Best(practised, s => -s.Practice.Accuracy,
                s => Leader(s, $"{s.Practice.Accuracy:P0}", $"over {Answers(s.Practice.Answered)}")));
    }

    /// <summary>Below this an accuracy is a coin toss dressed up as a statistic.</summary>
    private const int MinimumAnswers = 5;

    private static SubjectLeader? Best<TKey>(
        List<SubjectStats> candidates,
        Func<SubjectStats, TKey> rank,
        Func<SubjectStats, SubjectLeader> build)
        => candidates.Count == 0 ? null : build(candidates.OrderByDescending(rank).ThenBy(s => s.Name).First());

    private static SubjectLeader Leader(SubjectStats subject, string figure, string detail)
        => new(subject.Name, subject.ColorHex, subject.Id, figure, detail);

    private static string Cards(int count) => count == 1 ? "1 card" : $"{count} cards";

    private static string Answers(int count) => count == 1 ? "1 answer" : $"{count} answers";
}
