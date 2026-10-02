namespace Olga.Core.Infrastructure;

public static class SyncResourceTypes
{
    // Mirrors ck_sync_change_values in the OLGA database (olga-database 020_constraints_indexes.sql).
    public static readonly IReadOnlySet<string> Database = new HashSet<string>(StringComparer.Ordinal)
    {
        "PROFILE", "MATCH", "REQUEST", "CONVERSATION", "MESSAGE", "NOTIFICATION"
    };

    // Returns the database resource type to record, or null when the change can't be recorded yet.
    // CONNECTION_REQUEST maps to REQUEST, which is what social.accept_connection_request records.
    public static string? ForDatabase(string resourceType) => resourceType switch
    {
        "CONNECTION_REQUEST" => "REQUEST",
        _ when Database.Contains(resourceType) => resourceType,
        _ => null
    };
}
