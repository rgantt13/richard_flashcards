using Flashcards.Application;
using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Infrastructure;
using Flashcards.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Flashcards.Integration.Tests;

/// <summary>
/// Spins up the real container against a throwaway SQLite file in the temp directory.
/// <para>
/// Nothing here is mocked. The whole point of these tests is to catch the things unit tests
/// cannot: SQL that does not parse, a cascade that does not fire because the pragma was missed,
/// a Dapper mapping that quietly returns default. An in-memory fake would catch none of them.
/// </para>
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly string _root;

    private TestHost(ServiceProvider provider, string root)
    {
        _provider = provider;
        _root = root;
    }

    public IDispatcher Dispatcher => _provider.GetRequiredService<IDispatcher>();

    public IServiceProvider Services => _provider;

    public static async Task<TestHost> CreateAsync(bool seed = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "flashcards-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddApplication();
        services.AddInfrastructure(new StoragePaths(root));

        var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<DatabaseInitializer>().MigrateAsync();

        if (seed)
        {
            await provider.GetRequiredService<SeedData>().EnsureSeededAsync();
        }

        return new TestHost(provider, root);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();

        // SQLite keeps the file handle in the connection pool; clearing it releases the lock.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leaked handle on Windows should not fail the test run.
        }
    }
}
