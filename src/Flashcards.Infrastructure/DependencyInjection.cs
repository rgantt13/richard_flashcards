using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Infrastructure.Media;
using Flashcards.Infrastructure.Persistence;
using Flashcards.Infrastructure.Persistence.ReadStores;
using Flashcards.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Flashcards.Infrastructure;

public sealed record StoragePaths(string RootDirectory)
{
    public string DatabasePath => Path.Combine(RootDirectory, "flashcards.db");

    public string MediaDirectory => Path.Combine(RootDirectory, "media");

    /// <summary>%APPDATA%\RichardFlashcards on Windows, ~/.config/RichardFlashcards elsewhere.</summary>
    public static StoragePaths Default => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "RichardFlashcards"));
}

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, StoragePaths? paths = null)
    {
        var storage = paths ?? StoragePaths.Default;
        Directory.CreateDirectory(storage.RootDirectory);
        Directory.CreateDirectory(storage.MediaDirectory);

        SqlMappings.Register();

        services.AddSingleton(storage);
        services.AddSingleton<IDbConnectionFactory>(_ => new SqliteConnectionFactory(storage.DatabasePath));
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<SeedData>();

        // Scoped: the dispatcher opens a DI scope per request, so one connection (and at most one
        // transaction) is shared by every repository the handler touches, then disposed.
        services.AddScoped<DbSession>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        services.AddScoped<ISubjectRepository, SubjectRepository>();
        services.AddScoped<IFlashcardRepository, FlashcardRepository>();
        services.AddScoped<IReviewLogRepository, ReviewLogRepository>();
        // One read store per concern, mirroring the query folders in the application layer.
        services.AddScoped<IFlashcardReadStore, FlashcardReadStore>();
        services.AddScoped<ISubjectReadStore, SubjectReadStore>();
        services.AddScoped<IStatsReadStore, StatsReadStore>();
        services.AddScoped<IQuizReadStore, QuizReadStore>();
        services.AddScoped<IMediaStore>(sp => new FileSystemMediaStore(
            sp.GetRequiredService<DbSession>(), storage.MediaDirectory));

        return services;
    }
}
