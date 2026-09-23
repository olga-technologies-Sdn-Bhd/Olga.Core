using System.Text.Json;
using Olga.Core.Contracts;
using Olga.Core.Worker;

namespace Olga.Core.Tests;

public sealed class NlpIntegrationContractTests
{
    [Fact]
    public void Core_to_nlp_eligibility_views_keep_the_v1_shape()
    {
        Assert.Equal("nlp.vw_member_context_eligibility", NlpEligibilityContractV1.MemberContextView);
        Assert.Equal(
            new[] { "member_id", "context_id", "is_live", "is_visible", "has_consent", "is_suspended", "is_deleted", "community_id" },
            NlpEligibilityContractV1.MemberContextColumns);
        Assert.Equal("nlp.vw_member_relationship", NlpEligibilityContractV1.MemberRelationshipView);
        Assert.Equal(
            new[] { "member_id", "other_member_id", "context_id", "is_blocked", "is_connected" },
            NlpEligibilityContractV1.MemberRelationshipColumns);
    }

    [Fact]
    public void Nlp_match_completion_v1_payload_is_accepted_by_core_consumer()
    {
        using var data = JsonDocument.Parse("""
            {
              "request_id": "request-001",
              "candidate_count": 42,
              "model_version": "azure-text-embedding-3-small-1536-v1",
              "preprocessing_version": "normalizer-v1",
              "ranking_version": "ranking-v1"
            }
            """);
        var envelope = new IntegrationEventEnvelope(
            "event-001",
            NlpMatchRequestCompleted.EventType,
            "MATCH_REQUEST",
            "request-001",
            DateTimeOffset.Parse("2026-09-23T00:00:00Z"),
            data.RootElement.Clone());

        Assert.True(NlpMatchRequestCompleted.TryParse(envelope, out var completion));
        Assert.Equal("request-001", completion!.RequestId);
        Assert.Equal(42, completion.CandidateCount);
    }

    [Fact]
    public void Nlp_match_completion_rejects_an_envelope_for_a_different_request()
    {
        var envelope = new IntegrationEventEnvelope(
            "event-003",
            NlpMatchRequestCompleted.EventType,
            "MATCH_REQUEST",
            "request-other",
            DateTimeOffset.Parse("2026-09-23T00:00:00Z"),
            JsonSerializer.SerializeToElement(new
            {
                request_id = "request-001",
                candidate_count = 1,
                model_version = "model-v1",
                preprocessing_version = "normalizer-v1",
                ranking_version = "ranking-v1"
            }));

        Assert.False(NlpMatchRequestCompleted.TryParse(envelope, out _));
    }

    [Fact]
    public void Core_outbox_envelope_is_versioned_and_contains_no_raw_content()
    {
        var envelope = new IntegrationEventEnvelope(
            "event-002",
            "MemberProfileChanged.v1",
            "MEMBER",
            "member-001",
            DateTimeOffset.Parse("2026-09-23T00:00:00Z"),
            JsonSerializer.SerializeToElement(new { member_id = "member-001", profile_version = 2 }));

        var json = JsonSerializer.Serialize(envelope);
        Assert.Contains("\"event_type\":\"MemberProfileChanged.v1\"", json);
        Assert.DoesNotContain("biography", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("intent_text", json, StringComparison.OrdinalIgnoreCase);
    }
}
