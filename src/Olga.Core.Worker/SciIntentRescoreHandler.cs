using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Olga.Core.Infrastructure;

namespace Olga.Core.Worker;

public sealed record NlpIntentMatchReady(
    [property: JsonPropertyName("intent_id")] string IntentId,
    [property: JsonPropertyName("member_id")] string MemberId,
    [property: JsonPropertyName("status")] string Status)
{
    public const string EventType = "NlpIntentMatchReady.v1";

    public static bool TryParse(IntegrationEventEnvelope envelope, out NlpIntentMatchReady? value)
    {
        value = null;
        if (!string.Equals(envelope.EventType, EventType, StringComparison.Ordinal)
            || !string.Equals(envelope.AggregateType, "INTENT", StringComparison.Ordinal)) return false;
        try
        {
            value = envelope.Data.Deserialize<NlpIntentMatchReady>();
            return value is not null
                && value.IntentId.Length is > 0 and <= 64
                && value.MemberId.Length is > 0 and <= 64
                && value.Status == "MATCH_READY"
                && string.Equals(envelope.AggregateId, value.IntentId, StringComparison.Ordinal);
        }
        catch (JsonException) { return false; }
    }
}

// Core owns relationship visibility, so inbound SCI fan-out stays out of the NLP worker.
public sealed class SciIntentRescoreHandler(CoreDbContext db)
{
    public async Task HandleAsync(NlpIntentMatchReady intent, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close) await connection.OpenAsync(ct);
        try
        {
            await using (var stateCommand = new NpgsqlCommand("""
                SELECT vis.status
                FROM nlp.nlp_intent i
                JOIN nlp.venture_intent_set vis ON vis.venture_intent_set_pk=i.venture_intent_set_pk
                WHERE i.intent_id=@intent_id AND i.member_id=@contact_member_id
                """, connection))
            {
                Add(stateCommand, "intent_id", intent.IntentId);
                Add(stateCommand, "contact_member_id", intent.MemberId);
                var state = await stateCommand.ExecuteScalarAsync(ct) as string;
                if (state is null || state is not ("PROCESSING" or "ACTIVE")) return;
                // The match-ready outbox row is committed just before activation. Retry this
                // delivery if it wins that short race so inbound owners are never missed.
                if (state == "PROCESSING") throw new InvalidOperationException("VENTURE_INTENT_SET_NOT_ACTIVE");
            }

            await using var command = new NpgsqlCommand("""
                SELECT nlp.enqueue_sci_scoring_job(
                    'sci_' || substr(md5(vis.intent_set_id || ':' || l.owner_member_id || ':' || v.venture_id),1,32),
                    l.owner_member_id,
                    v.venture_id,
                    @contact_member_id,
                    'CONTACT_INTENT_READY',
                    'contact-intent-ready:' || vis.intent_set_id || ':' || l.owner_member_id || ':' || v.venture_id,
                    CURRENT_TIMESTAMP)
                FROM nlp.nlp_intent i
                JOIN nlp.venture_intent_set vis
                  ON vis.venture_intent_set_pk=i.venture_intent_set_pk
                 AND vis.status='ACTIVE'
                JOIN social.contact_link l
                  ON l.contact_member_id=vis.owner_member_id
                 AND l.lifecycle_state='CONNECTED'
                 AND l.consent_at IS NOT NULL
                JOIN core.venture v
                  ON v.owner_member_id=l.owner_member_id
                 AND v.status='ACTIVE'
                WHERE i.intent_id=@intent_id AND i.member_id=@contact_member_id
                """, connection);
            Add(command, "intent_id", intent.IntentId);
            Add(command, "contact_member_id", intent.MemberId);
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (close) await connection.CloseAsync();
        }
    }

    private static void Add(NpgsqlCommand command, string name, string value) =>
        command.Parameters.Add(name, NpgsqlDbType.Varchar).Value = value;
}
