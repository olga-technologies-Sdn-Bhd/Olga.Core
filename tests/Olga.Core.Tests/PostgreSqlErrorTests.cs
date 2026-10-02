using Microsoft.EntityFrameworkCore;
using Npgsql;
using Olga.Core.Infrastructure;

namespace Olga.Core.Tests;

public sealed class PostgreSqlErrorTests
{
    private static DbUpdateException Wrap(string sqlState) =>
        new("save failed", new PostgresException("rejected", "ERROR", "ERROR", sqlState));

    [Fact]
    public void Check_violations_are_recognised_separately_from_other_constraint_errors()
    {
        Assert.True(PostgreSqlConfiguration.IsCheckViolation(Wrap(PostgresErrorCodes.CheckViolation)));
        Assert.False(PostgreSqlConfiguration.IsCheckViolation(Wrap(PostgresErrorCodes.UniqueViolation)));
        Assert.False(PostgreSqlConfiguration.IsCheckViolation(Wrap(PostgresErrorCodes.ForeignKeyViolation)));
        Assert.False(PostgreSqlConfiguration.IsCheckViolation(new DbUpdateException("no inner exception")));
    }
}
