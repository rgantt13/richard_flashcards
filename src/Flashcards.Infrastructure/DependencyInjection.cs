using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Infrastructure.Media;
using Flashcards.Infrastructure.Persistence;
using Flashcards.Infrastructure.Persistence.ReadStores;
using Flashcards.Infrastructure.Persistence.Repositories;
using Flashcards.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Flashcards.Infrastructure;

/// <summary>
/// Where everything lives: the database, the images, and the preferences file beside them.
/// <para>
/// One folder, so "back up my library" and "move it to another machine" are both a folder copy.
/// </para>
/// </summary>
public sealed record StoragePaths(string RootDirectory)
{
    /// <summary>
    /// Set this to run against a different library entirely.
    /// <para>
    /// Written for the case where you want to try something destructive — an import, clearing
    /// history, a timed drill that records real answers — without doing it to the cards you
    /// actually care about. Point it at an empty folder and the app builds a fresh library there,
    /// seed data and all, leaving the real one untouched.
    /// </para>
    /// </summary>
    public const string OverrideVariable = "FLASHCARDS_DATA_DIR";

    public string DatabasePath => Path.Combine(RootDirectory, "flashcards.db");

    public string MediaDirectory => Path.Combine(RootDirectory, "media");

    /// <summary>Preferences, deliberately beside the library rather than inside it.</summary>
    public string SettingsPath => Path.Combine(RootDirectory, "settings.json");

    /// <summary>
    /// Whether this came from <see cref="OverrideVariable"/> rather than the per-user default.
    /// Surfaced on the settings panel, because a running app that has quietly moved its library
    /// somewhere else is a confusing thing to debug.
    /// </summary>
    public bool IsOverridden { get; private init; }

    /// <summary>
    /// The override if one is set and usable, otherwise
    /// %APPDATA%\RichardFlashcards on Windows and ~/.config/RichardFlashcards elsewhere.
    /// </summary>
    public static StoragePaths Default => Resolve(Environment.GetEnvironmentVariable(OverrideVariable));

    /// <summary>
    /// The resolution rules, separated from reading the environment so they can be tested without
    /// setting a process-wide variable that would leak into every other test in the run.
    /// <para>
    /// A value that cannot be turned into a path — blank, or full of characters a path may not
    /// contain — falls back to the default rather than refusing to start. That is safe precisely
    /// because the settings panel shows the resolved folder and says where it came from: a typo
    /// is visible rather than silent.
    /// </para>
    /// </summary>
    public static StoragePaths Resolve(string? overrideValue)
    {
        if (string.IsNullOrWhiteSpace(overrideValue))
        {
            return new StoragePaths(DefaultRoot);
        }

        try
        {
            // Expanded so "%USERPROFILE%\decks" works, and made absolute so a relative value does
            // not resolve against a working directory nobody set deliberately.
            var expanded = Environment.ExpandEnvironmentVariables(overrideValue.Trim());

            return new StoragePaths(Path.GetFullPath(expanded)) { IsOverridden = true };
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new StoragePaths(DefaultRoot);
        }
    }

    private static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "RichardFlashcards");
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

        // Singleton, and not scoped like the repositories: it touches a file rather than the
        // database session, and the theme is read before any request scope exists.
        services.AddSingleton<ISettingsStore>(_ => new JsonSettingsStore(storage));

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
