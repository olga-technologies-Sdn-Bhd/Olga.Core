using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Olga.Core.Infrastructure;

public static class PostgreSqlConfiguration
{
    public static DbContextOptionsBuilder UseOlgaPostgreSql(
        this DbContextOptionsBuilder options,
        string connectionString,
        int commandTimeoutSeconds = 15,
        bool enableRetryOnFailure = true)
    {
        options.UseNpgsql(connectionString, postgres =>
            {
                postgres.CommandTimeout(commandTimeoutSeconds);
                if (enableRetryOnFailure) postgres.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null);
            });
        return options.UseSnakeCaseNamingConvention();
    }

    public static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    public static bool IsForeignKeyViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation };

    // A database CHECK constraint or invariant trigger (SQLSTATE 23514) rejected the write.
    public static bool IsCheckViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.CheckViolation };

    public static bool IsUnavailable(Exception exception) =>
        exception is NpgsqlException { IsTransient: true } || exception.InnerException is NpgsqlException { IsTransient: true };
}
