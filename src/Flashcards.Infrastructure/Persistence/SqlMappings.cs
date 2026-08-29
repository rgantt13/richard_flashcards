using System.Data;
using Dapper;

namespace Flashcards.Infrastructure.Persistence;

/// <summary>
/// Dapper type handlers.
/// <para>
/// This is the friction point when moving from SQL Server to SQLite: with
/// <c>Microsoft.Data.Sqlite</c> there is no uniqueidentifier and no datetimeoffset.
/// </para>
/// <para>
/// <b>Guid is not actually handled here, despite <see cref="GuidHandler"/> below.</b> Dapper keeps
/// <c>Guid</c> in its built-in type map and resolves it there before consulting custom handlers, so
/// the handler is never asked to bind a parameter. What actually reaches SQLite is
/// Microsoft.Data.Sqlite's own <c>DbType.Guid</c> formatting: TEXT, in <b>upper case</b> — verified
/// by <c>GuidStorageTests</c>. That is self-consistent (the provider reads back what it wrote), but
/// it matters the moment you hand-write SQL, because SQLite compares TEXT case-sensitively and a
/// lower-case id simply will not match.
/// </para>
/// <para>
/// Making the handler take effect would need <c>SqlMapper.RemoveTypeMap(typeof(Guid))</c> — and
/// would then write lower case, orphaning every id already on disk. It is left alone deliberately;
/// the handler's <c>Parse</c> still earns its keep as a tolerant reader.
/// </para>
/// <para>
/// <see cref="DateTimeOffsetHandler"/> is different: DateTimeOffset is <i>not</i> in Dapper's
/// built-in map, so that handler genuinely runs and is what pins timestamps to round-trip "O" strings.
/// </para>
/// </summary>
public static class SqlMappings
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;

        // Column names are snake_case, properties are PascalCase.
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        SqlMapper.AddTypeHandler(new GuidHandler());
        SqlMapper.AddTypeHandler(new NullableGuidHandler());
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
        SqlMapper.AddTypeHandler(new NullableDateTimeOffsetHandler());
    }

    private sealed class GuidHandler : SqlMapper.TypeHandler<Guid>
    {
        public override Guid Parse(object value) => value switch
        {
            string s => Guid.Parse(s),
            byte[] b => new Guid(b),
            Guid g => g,
            _ => throw new DataException($"Cannot convert {value.GetType()} to Guid."),
        };

        public override void SetValue(IDbDataParameter parameter, Guid value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = value.ToString("D");
        }
    }

    private sealed class NullableGuidHandler : SqlMapper.TypeHandler<Guid?>
    {
        public override Guid? Parse(object value) => value switch
        {
            null or DBNull => null,
            string s => string.IsNullOrEmpty(s) ? null : Guid.Parse(s),
            byte[] b => new Guid(b),
            Guid g => g,
            _ => throw new DataException($"Cannot convert {value.GetType()} to Guid?."),
        };

        public override void SetValue(IDbDataParameter parameter, Guid? value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = value?.ToString("D") ?? (object)DBNull.Value;
        }
    }

    /// <summary>
    /// Round-trip format "O" — 2026-08-25T14:03:11.1234567+00:00. It sorts lexicographically in
    /// the same order it sorts chronologically, which is what makes <c>due_utc &lt;= @now</c> work
    /// on a TEXT column without any conversion.
    /// </summary>
    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override DateTimeOffset Parse(object value) => value switch
        {
            string s => DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind),
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            DateTimeOffset dto => dto,
            _ => throw new DataException($"Cannot convert {value.GetType()} to DateTimeOffset."),
        };

        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = value.ToUniversalTime().ToString("O");
        }
    }

    private sealed class NullableDateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset?>
    {
        public override DateTimeOffset? Parse(object value) => value switch
        {
            null or DBNull => null,
            string s => string.IsNullOrEmpty(s)
                ? null
                : DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind),
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            DateTimeOffset dto => dto,
            _ => throw new DataException($"Cannot convert {value.GetType()} to DateTimeOffset?."),
        };

        public override void SetValue(IDbDataParameter parameter, DateTimeOffset? value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = value?.ToUniversalTime().ToString("O") ?? (object)DBNull.Value;
        }
    }

    /// <summary>Helper used in raw SQL parameters where a handler is not in play.</summary>
    public static string ToSqlite(this DateTimeOffset value) => value.ToUniversalTime().ToString("O");
}
