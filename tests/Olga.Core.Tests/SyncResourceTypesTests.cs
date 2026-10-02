using Olga.Core.Infrastructure;

namespace Olga.Core.Tests;

public sealed class SyncResourceTypesTests
{
    [Theory]
    [InlineData("PROFILE", "PROFILE")]
    [InlineData("MATCH", "MATCH")]
    [InlineData("REQUEST", "REQUEST")]
    [InlineData("CONVERSATION", "CONVERSATION")]
    [InlineData("MESSAGE", "MESSAGE")]
    [InlineData("NOTIFICATION", "NOTIFICATION")]
    [InlineData("CONNECTION_REQUEST", "REQUEST")]
    public void Database_supported_types_are_recorded(string coreType, string databaseType) =>
        Assert.Equal(databaseType, SyncResourceTypes.ForDatabase(coreType));

    [Theory]
    [InlineData("CONSENT")]
    [InlineData("EVENT")]
    [InlineData("EVENT_REGISTRATION")]
    [InlineData("LIVE_MODE")]
    [InlineData("PRIVACY_REQUEST")]
    [InlineData("CONNECTION")]
    [InlineData("profile")]
    public void Types_the_database_rejects_are_skipped(string coreType) =>
        Assert.Null(SyncResourceTypes.ForDatabase(coreType));
}
