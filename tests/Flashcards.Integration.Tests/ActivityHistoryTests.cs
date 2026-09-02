using Dapper;
using Flashcards.Application.Cards.Commands;
using Flashcards.Application.Contracts;
using Flashcards.Application.Stats.Queries;
using Flashcards.Domain.Cards;
using Flashcards.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Flashcards.Integration.Tests;

/// <summary>
/// The answer history laid out along a calendar — what the heatmap and the streak counters read.
/// <para>
/// The rows are written straight into <c>review_log</c> rather than through
/// <c>RecordAnswerCommand</c>, because every one of these tests is about a timestamp in the past
/// and the command stamps them with the clock. What is being tested is the grouping: that a
/// timestamp lands on the local day a person would say it happened on, and that days with nothing
/// in them still come back.
/// </para>
/// </summary>
public sealed class ActivityHistoryTests
{
    [Fact]
    public async Task A_window_with_no_answers_is_all_zeroes_rather_than_empty()
    {
        await using var host = await TestHost.CreateAsync();

        var history = await host.Dispatcher.QueryAsync(new GetActivityHistoryQuery(30));

        history.Days.Count.ShouldBe(30);
        history.Days.ShouldAllBe(d => d.Answered == 0);
        history.HasAnything.ShouldBeFalse();
        history.CurrentStreak.ShouldBe(0);
        history.LongestStreak.ShouldBe(0);
        history.To.ShouldBe(DateOnly.FromDateTime(DateTime.Today));
    }

    [Fact]
    public async Task Answers_land_on_the_local_day_they_happened()
    {
        await using var host = await TestHost.CreateAsync();
        var card = await CreateCardAsync(host);

        // Local times, converted to UTC on the way in exactly as the app stores them. Late evening
        // is the case that matters: in anything east of Greenwich it is already tomorrow in UTC,
        // and the day it belongs to is still today.
        await LogAsync(host, card, Local(DateTime.Today.AddHours(23).AddMinutes(45)), correct: true);
        await LogAsync(host, card, Local(DateTime.Today.AddHours(0).AddMinutes(20)), correct: false);

        var history = await host.Dispatcher.QueryAsync(new GetActivityHistoryQuery(7));
        var today = history.Days.Single(d => d.Day == DateOnly.FromDateTime(DateTime.Today));

        today.Answered.ShouldBe(2);
        today.Correct.ShouldBe(1);
        today.Wrong.ShouldBe(1);
    }

    [Fact]
    public async Task Each_day_is_counted_separately_and_in_order()
    {
        await using var host = await TestHost.CreateAsync();
        var card = await CreateCardAsync(host);

        await LogAsync(host, card, Local(DateTime.Today.AddDays(-2).AddHours(9)), correct: true);
        await LogAsync(host, card, Local(DateTime.Today.AddDays(-2).AddHours(21)), correct: true);
        await LogAsync(host, card, Local(DateTime.Today.AddDays(-1).AddHours(12)), correct: false);

        var history = await host.Dispatcher.QueryAsync(new GetActivityHistoryQuery(5));

        history.Days.Select(d => d.Answered).ShouldBe([0, 0, 2, 1, 0]);
        history.Answered.ShouldBe(3);
        history.Correct.ShouldBe(2);
        history.DaysStudied.ShouldBe(2);
        history.BusiestDay.ShouldBe(2);
    }

    /// <summary>
    /// A run that reaches yesterday is still live: the day is not over. Counting it as broken
    /// would tell someone at breakfast that they had lost a streak they still have all day to keep.
    /// </summary>
    [Fact]
    public async Task A_run_ending_yesterday_still_counts_as_current()
    {
        await using var host = await TestHost.CreateAsync();
        var card = await CreateCardAsync(host);

        foreach (var back in new[] { 3, 2, 1 })
        {
            await LogAsync(host, card, Local(DateTime.Today.AddDays(-back).AddHours(10)), correct: true);
        }

        var history = await host.Dispatcher.QueryAsync(new GetActivityHistoryQuery(10));

        history.CurrentStreak.ShouldBe(3);
        history.LongestStreak.ShouldBe(3);
    }

    [Fact]
    public async Task Answering_today_extends_the_run_rather_than_starting_a_second_one()
    {
        await using var host = await TestHost.CreateAsync();
        var card = await CreateCardAsync(host);

        foreach (var back in new[] { 2, 1, 0 })
        {
            await LogAsync(host, card, Local(DateTime.Today.AddDays(-back).AddHours(10)), correct: true);
        }

        (await host.Dispatcher.QueryAsync(new GetActivityHistoryQuery(10))).CurrentStreak.ShouldBe(3);
    }

    [Fact]
    public async Task A_gap_breaks_the_run_but_the_longest_one_is_remembered()
    {
        await using var host = await TestHost.CreateAsync();
        var card = await CreateCardAsync(host);

        // Five in a row, a day off, then two.
        foreach (var back in new[] { 10, 9, 8, 7, 6, 4, 3 })
        {
            await LogAsync(host, card, Local(DateTime.Today.AddDays(-back).AddHours(10)), correct: true);
        }

        var history = await host.Dispatcher.QueryAsync(new GetActivityHistoryQuery(20));

        history.LongestStreak.ShouldBe(5);

        // Nothing yesterday or today, so the run that ended three days ago is over.
        history.CurrentStreak.ShouldBe(0);
    }

    [Fact]
    public async Task Answers_older_than_the_window_are_left_out()
    {
        await using var host = await TestHost.CreateAsync();
        var card = await CreateCardAsync(host);

        await LogAsync(host, card, Local(DateTime.Today.AddDays(-40).AddHours(10)), correct: true);
        await LogAsync(host, card, Local(DateTime.Today.AddDays(-2).AddHours(10)), correct: true);

        var history = await host.Dispatcher.QueryAsync(new GetActivityHistoryQuery(7));

        history.Days.Count.ShouldBe(7);
        history.Answered.ShouldBe(1);
    }

    // ---- helpers -----------------------------------------------------------

    private static DateTimeOffset Local(DateTime localTime)
        => new DateTimeOffset(localTime, DateTimeOffset.Now.Offset).ToUniversalTime();

    private static Task<Guid> CreateCardAsync(TestHost host)
        => host.Dispatcher.SendAsync(new SaveFlashcardCommand
        {
            SubjectNames = ["History"],
            Name = "A card to answer",
            CardType = CardType.Standard,
            Blocks =
            [
                new(Guid.Empty, CardFace.Question, 0, ContentKind.PlainText, "Q", null, null, ImageStretch.Uniform, null, null),
                new(Guid.Empty, CardFace.Answer, 0, ContentKind.PlainText, "A", null, null, ImageStretch.Uniform, null, null),
            ],
        });

    /// <summary>
    /// Writes one answer at a chosen moment.
    /// <para>
    /// Bound through Dapper rather than as hand-written strings so the id and the timestamp are
    /// formatted exactly as the app formats them — ids are stored upper case, and a row written any
    /// other way fails the foreign key rather than quietly testing nothing.
    /// </para>
    /// </summary>
    private static async Task LogAsync(TestHost host, Guid cardId, DateTimeOffset whenUtc, bool correct)
    {
        var factory = host.Services.GetRequiredService<IDbConnectionFactory>();

        await using var connection = await factory.OpenAsync(CancellationToken.None);

        await connection.ExecuteAsync(
            """
            INSERT INTO review_log (card_id, reviewed_utc, was_correct, elapsed_ms)
            VALUES (@CardId, @When, @Correct, 1000);
            """,
            new { CardId = cardId, When = whenUtc, Correct = correct ? 1 : 0 });
    }
}
