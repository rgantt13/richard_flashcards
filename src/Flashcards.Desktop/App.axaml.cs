using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Flashcards.Application;
using Flashcards.Desktop.Services;
using Flashcards.Desktop.ViewModels.Design;
using Flashcards.Desktop.ViewModels.Manage;
using Flashcards.Desktop.ViewModels.Shell;
using Flashcards.Desktop.ViewModels.Statistics;
using Flashcards.Desktop.ViewModels.Study;
using Flashcards.Desktop.Views.Shell;
using Flashcards.Infrastructure;
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

        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<CardEditorViewModel>();
        services.AddTransient<ManagementViewModel>();
        services.AddTransient<QuizViewModel>();
        services.AddTransient<StatisticsViewModel>();

        Services = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Schema first, sample data second, and only then show a window — the panels all
            // query on activation and an unmigrated database would greet you with an exception.
            RunStartupWork().GetAwaiter().GetResult();

            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainWindowViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task RunStartupWork()
    {
        await Services.GetRequiredService<DatabaseInitializer>().MigrateAsync();
        await Services.GetRequiredService<SeedData>().EnsureSeededAsync();
    }
}
