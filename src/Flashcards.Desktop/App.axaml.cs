using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Flashcards.Application;
using Flashcards.Application.Contracts;
using Flashcards.Desktop.Services;
using Flashcards.Desktop.ViewModels.Design;
using Flashcards.Desktop.ViewModels.Manage;
using Flashcards.Desktop.ViewModels.Settings;
using Flashcards.Desktop.ViewModels.Shell;
using Flashcards.Desktop.ViewModels.Statistics;
using Flashcards.Desktop.ViewModels.Study;
using Flashcards.Desktop.Views.Shell;
using Flashcards.Infrastructure;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Flashcards.Desktop;

public partial class App : Avalonia.Application
{
    /// <summary>
    /// Exposed so the design-time XAML previewer and the view locator can resolve view models.
    /// In a desktop app this is the one place a service-locator is defensible.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Information));
        services.AddApplication();
        services.AddInfrastructure();

        services.AddSingleton<IImageCache, ImageCache>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IClipboardImageService, ClipboardImageService>();
        services.AddSingleton<IDeckFileService, DeckFileService>();
        services.AddSingleton<IShellService, ShellService>();

        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<CardEditorViewModel>();
        services.AddTransient<ManagementViewModel>();
        services.AddTransient<QuizViewModel>();
        services.AddTransient<StatisticsViewModel>();
        services.AddTransient<SettingsViewModel>();

        Services = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Schema first, sample data second, and only then show a window — the panels all
            // query on activation and an unmigrated database would greet you with an exception.
            //
            // Task.Run is load-bearing, not decoration. This method runs on the UI thread, and
            // blocking it with GetResult() while the work inside awaits is the classic
            // sync-over-async deadlock: each await captures Avalonia's SynchronizationContext and
            // posts its continuation back to the very thread sitting in GetResult(). It was
            // intermittent — roughly four launches in five — because an await that completes
            // synchronously never posts, and a small settings file usually reads in one go. On the
            // pool there is no UI context to capture, so every continuation has somewhere to run.
            var settings = Task.Run(LoadStartupStateAsync).GetAwaiter().GetResult();

            // Back on the UI thread for the one step that needs it: RequestedThemeVariant is an
            // Avalonia property and setting it off-thread is not safe.
            Services.GetRequiredService<IShellService>().ApplyTheme(settings.Theme);

            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainWindowViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Everything that has to happen before a window exists: the schema, the seed data, and
    /// reading the settings so the app opens in the colours you left it in rather than flashing
    /// the default and correcting itself a frame later.
    /// <para>
    /// Returns the settings rather than applying them. Every await here runs on the thread pool —
    /// see the call site — and applying the theme touches an Avalonia property, which belongs on
    /// the UI thread. Handing the value back keeps the split honest.
    /// </para>
    /// </summary>
    private static async Task<AppSettings> LoadStartupStateAsync()
    {
        await Services.GetRequiredService<DatabaseInitializer>().MigrateAsync();
        await Services.GetRequiredService<SeedData>().EnsureSeededAsync();

        return await Services.GetRequiredService<ISettingsStore>().LoadAsync(default);
    }
}
