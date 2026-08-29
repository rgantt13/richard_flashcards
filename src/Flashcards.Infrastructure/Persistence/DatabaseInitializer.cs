using System.Reflection;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Flashcards.Infrastructure.Persistence;

/// <summary>
/// A minimal forward-only migrator. Migration scripts are embedded resources named
/// <c>Migration###_Name.sql</c>; the number is the version. Applied versions are recorded in
/// <c>schema_migrations</c> and each script runs inside its own transaction.
/// <para>
/// [T-SQL] Note that SQLite <b>does</b> support transactional DDL — a failed CREATE TABLE inside
/// a transaction rolls back cleanly, which is the same as SQL Server and unlike MySQL/Oracle.
/// </para>
/// </summary>
public sealed class DatabaseInitializer(IDbConnectionFactory factory, ILogger<DatabaseInitializer> logger)
{
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version     INTEGER NOT NULL PRIMARY KEY,
                name        TEXT    NOT NULL,
                applied_utc TEXT    NOT NULL
            );
            """);

        var applied = (await connection.QueryAsync<long>("SELECT version FROM schema_migrations;")).ToHashSet();

        foreach (var (version, name, sql) in LoadScripts())
        {
            if (applied.Contains(version))
            {
                continue;
            }

            logger.LogInformation("Applying migration {Version:000} {Name}", version, name);

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            try
            {
                await connection.ExecuteAsync(sql, transaction: transaction);
                await connection.ExecuteAsync(
                    "INSERT INTO schema_migrations (version, name, applied_utc) VALUES (@Version, @Name, @Applied);",
                    new { Version = version, Name = name, Applied = DateTimeOffset.UtcNow.ToString("O") },
                    transaction);

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        // ANALYZE refreshes the query planner's statistics. Cheap on a database this size and
        // it makes the read-store queries pick the right indexes from the first run.
        await connection.ExecuteAsync("ANALYZE;");
    }

    private static IEnumerable<(long Version, string Name, string Sql)> LoadScripts()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var resources = assembly
            .GetManifestResourceNames()
            .Where(n => n.Contains(".Migrations.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal);

        foreach (var resource in resources)
        {
            var fileName = resource[(resource.LastIndexOf(".Migrations.", StringComparison.Ordinal) + ".Migrations.".Length)..];
            var digits = new string(fileName.SkipWhile(char.IsLetter).TakeWhile(char.IsDigit).ToArray());

            if (!long.TryParse(digits, out var version))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);

            yield return (version, fileName, reader.ReadToEnd());
        }
    }
}
