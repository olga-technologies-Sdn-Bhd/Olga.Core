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

public sealed record ProfileImageUploaded(
    [property: JsonPropertyName("image_id")] string ImageId,
    [property: JsonPropertyName("member_id")] string MemberId)
{
    public const string EventType = "ProfileImageUploaded.v1";

    public static bool TryParse(IntegrationEventEnvelope envelope, out ProfileImageUploaded? uploaded)
    {
        uploaded = null;
        if (!string.Equals(envelope.EventType, EventType, StringComparison.Ordinal)
            || !string.Equals(envelope.AggregateType, "PROFILE_IMAGE", StringComparison.Ordinal)) return false;
        try
        {
            uploaded = envelope.Data.Deserialize<ProfileImageUploaded>();
            return uploaded is not null
                && uploaded.ImageId.Length is > 0 and <= 64
                && uploaded.MemberId.Length is > 0 and <= 64
                && string.Equals(envelope.AggregateId, uploaded.ImageId, StringComparison.Ordinal);
        }
        catch (JsonException) { return false; }
    }
}

public sealed record ChatFileUploaded(
    [property: JsonPropertyName("file_asset_id")] string FileAssetId,
    [property: JsonPropertyName("conversation_id")] string ConversationId,
    [property: JsonPropertyName("owner_member_id")] string OwnerMemberId,
    [property: JsonPropertyName("container_name")] string ContainerName,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("size_bytes")] long SizeBytes)
{
    public const string EventType = "ChatFileUploaded.v1";

    public static bool TryParse(IntegrationEventEnvelope envelope, out ChatFileUploaded? uploaded)
    {
        uploaded = null;
        if (!string.Equals(envelope.EventType, EventType, StringComparison.Ordinal)
            || !string.Equals(envelope.AggregateType, "FILE_ASSET", StringComparison.Ordinal)) return false;
        try
        {
            uploaded = envelope.Data.Deserialize<ChatFileUploaded>();
            return uploaded is not null
                && uploaded.FileAssetId.Length is > 0 and <= 64
                && uploaded.ConversationId.Length is > 0 and <= 64
                && uploaded.OwnerMemberId.Length is > 0 and <= 64
                && uploaded.ContainerName == Olga.Core.Application.ChatFiles.ContainerName
                && uploaded.MediaType.Length is > 0 and <= 128
                && uploaded.SizeBytes > 0
                && string.Equals(envelope.AggregateId, uploaded.FileAssetId, StringComparison.Ordinal);
        }
        catch (JsonException) { return false; }
    }
}
