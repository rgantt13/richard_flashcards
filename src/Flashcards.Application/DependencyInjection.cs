using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Application.Cards.Commands;
using Flashcards.Application.Cards.Queries;
using Flashcards.Application.Contracts;
using Flashcards.Application.Media.Commands;
using Flashcards.Application.Media.Queries;
using Flashcards.Application.Quiz.Commands;
using Flashcards.Application.Quiz.Queries;
using Flashcards.Application.Stats.Commands;
using Flashcards.Application.Stats.Queries;
using Flashcards.Application.Settings.Commands;
using Flashcards.Application.Settings.Queries;
using Flashcards.Application.Subjects.Commands;
using Flashcards.Application.Subjects.Queries;
using Flashcards.Application.Transfer;
using Microsoft.Extensions.DependencyInjection;

namespace Flashcards.Application;

/// <summary>
/// Explicit handler registration.
/// <para>
/// Assembly scanning would be shorter, but every handler you forget to write then fails at
/// runtime with a resolution error instead of at compile time. With a handful of features,
/// spelling them out means a missing handler is a red squiggle.
/// </para>
/// <para>
/// Grouped command-then-query per feature, mirroring the <c>Commands/</c> and <c>Queries/</c>
/// folders. The split is the point of CQRS here: command handlers take repositories and an
/// <see cref="IUnitOfWork"/> and write; query handlers take one of the read stores
/// and never open a transaction.
/// </para>
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<IDispatcher, Dispatcher>();
        services.AddSingleton<IClock, SystemClock>();

        // ---- validators ----
        services.AddSingleton<IValidator<SaveFlashcardCommand>, SaveFlashcardValidator>();

        // ---- cards: commands ----
        services.AddTransient<ICommandHandler<SaveFlashcardCommand, Guid>, SaveFlashcardHandler>();
        services.AddTransient<ICommandHandler<DeleteFlashcardsCommand, int>, DeleteFlashcardsHandler>();
        services.AddTransient<ICommandHandler<SetCardsSuspendedCommand, int>, SetCardsSuspendedHandler>();
        services.AddTransient<ICommandHandler<RetagCardsCommand, int>, RetagCardsHandler>();

        // ---- cards: queries ----
        services.AddTransient<IQueryHandler<SearchFlashcardsQuery, PagedResult<FlashcardSummary>>, SearchFlashcardsHandler>();
        services.AddTransient<IQueryHandler<GetFlashcardDetailQuery, FlashcardDetail?>, GetFlashcardDetailHandler>();

        // ---- subjects: commands ----
        services.AddTransient<ICommandHandler<EnsureSubjectCommand, Guid>, EnsureSubjectHandler>();
        services.AddTransient<ICommandHandler<CreateSubjectCommand, Guid>, CreateSubjectHandler>();
        services.AddTransient<ICommandHandler<MoveSubjectCommand, Unit>, MoveSubjectHandler>();
        services.AddTransient<ICommandHandler<RenameSubjectCommand, Unit>, RenameSubjectHandler>();
        services.AddTransient<ICommandHandler<DeleteSubjectCommand, Unit>, DeleteSubjectHandler>();

        // ---- subjects: queries ----
        services.AddTransient<IQueryHandler<GetSubjectsQuery, IReadOnlyList<SubjectSummary>>, GetSubjectsHandler>();
        services.AddTransient<IQueryHandler<GetSubjectDeletionBlockersQuery, IReadOnlyList<string>>, GetSubjectDeletionBlockersHandler>();

        // ---- quiz: commands ----
        services.AddTransient<ICommandHandler<RecordAnswerCommand, AnswerResult>, RecordAnswerHandler>();

        // ---- quiz: queries ----
        services.AddTransient<IQueryHandler<StartQuizSessionQuery, QuizSession>, StartQuizSessionHandler>();
        services.AddTransient<IQueryHandler<GetQuizCardQuery, QuizCard?>, GetQuizCardHandler>();

        // ---- stats: commands ----
        services.AddTransient<ICommandHandler<ClearCardHistoryCommand, int>, ClearCardHistoryHandler>();

        // ---- stats: queries ----
        services.AddTransient<IQueryHandler<GetOverallStatsQuery, OverallStats>, GetOverallStatsHandler>();
        services.AddTransient<IQueryHandler<GetSubjectStatsQuery, IReadOnlyList<SubjectStats>>, GetSubjectStatsHandler>();
        services.AddTransient<IQueryHandler<GetCardStatsQuery, CardStats>, GetCardStatsHandler>();

        // ---- media: commands ----
        services.AddTransient<ICommandHandler<SaveMediaCommand, MediaDescriptor>, SaveMediaHandler>();

        // ---- media: queries ----
        services.AddTransient<IQueryHandler<LoadMediaQuery, byte[]?>, LoadMediaHandler>();

        // ---- settings ----
        services.AddTransient<IQueryHandler<GetSettingsQuery, AppSettings>, GetSettingsHandler>();
        services.AddTransient<IQueryHandler<GetDataLocationQuery, DataLocation>, GetDataLocationHandler>();
        services.AddTransient<ICommandHandler<SaveSettingsCommand, Unit>, SaveSettingsHandler>();
        services.AddTransient<ICommandHandler<ClearAllHistoryCommand, int>, ClearAllHistoryHandler>();

        // ---- transfer: the deck file both ways ----
        services.AddTransient<IQueryHandler<BuildDeckExportQuery, DeckDocument>, BuildDeckExportHandler>();
        services.AddTransient<ICommandHandler<ImportDeckCommand, DeckImportResult>, ImportDeckHandler>();

        return services;
    }
}
