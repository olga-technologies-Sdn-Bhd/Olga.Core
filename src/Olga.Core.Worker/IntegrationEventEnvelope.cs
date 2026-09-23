using System.Text.Json;
using System.Text.Json.Serialization;
using Olga.Core.Domain;

namespace Olga.Core.Worker;

public sealed record IntegrationEventEnvelope(
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("aggregate_type")] string AggregateType,
    [property: JsonPropertyName("aggregate_id")] string AggregateId,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("data")] JsonElement Data)
{
    public static IntegrationEventEnvelope FromOutbox(OutboxEvent row)
    {
        using var payload = JsonDocument.Parse(row.PayloadJson);
        return new(row.OutboxEventId, row.EventType, row.AggregateType, row.AggregateId, row.OccurredAt, payload.RootElement.Clone());
    }
}

public sealed record NlpMatchRequestCompleted(
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("candidate_count")] int CandidateCount,
    [property: JsonPropertyName("model_version")] string ModelVersion,
    [property: JsonPropertyName("preprocessing_version")] string PreprocessingVersion,
    [property: JsonPropertyName("ranking_version")] string RankingVersion)
{
    public const string EventType = "NlpMatchRequestCompleted.v1";

    public static bool TryParse(IntegrationEventEnvelope envelope, out NlpMatchRequestCompleted? completion)
    {
        completion = null;
        if (string.IsNullOrWhiteSpace(envelope.EventId)
            || envelope.EventId.Length > 64
            || !string.Equals(envelope.EventType, EventType, StringComparison.Ordinal)) return false;
        try
        {
            completion = envelope.Data.Deserialize<NlpMatchRequestCompleted>();
            return completion is not null
                && string.Equals(envelope.AggregateType, "MATCH_REQUEST", StringComparison.Ordinal)
                && string.Equals(envelope.AggregateId, completion.RequestId, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(completion.RequestId)
                && completion.RequestId.Length <= 64
                && completion.CandidateCount >= 0
                && !string.IsNullOrWhiteSpace(completion.ModelVersion)
                && completion.ModelVersion.Length <= 128
                && !string.IsNullOrWhiteSpace(completion.PreprocessingVersion)
                && completion.PreprocessingVersion.Length <= 128
                && !string.IsNullOrWhiteSpace(completion.RankingVersion)
                && completion.RankingVersion.Length <= 128;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
