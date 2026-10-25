using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Olga.Core.Application;
using Olga.Core.Worker;

namespace Olga.Core.Tests;

public sealed class ChatRealtimeTests
{
    [Fact]
    public void Worker_sends_to_the_hub_the_API_hosts() => Assert.Equal(nameof(Olga.Core.Api.ChatHub), ChatRealtime.HubName);

    [Theory]
    [InlineData("MessageCreated.v1", """{"message_id":"m","sender_member_id":"A","recipient_member_id":"B","server_sequence":1}""", "A,B")]
    [InlineData("MessageDeleted.v1", """{"message_id":"m","sender_member_id":"A","recipient_member_id":"B"}""", "A,B")]
    [InlineData("ConversationRead.v1", """{"conversation_id":"c","reader_member_id":"B","sender_member_id":"A"}""", "B,A")]
    [InlineData("MemberBlocked.v1", """{"actor_member_id":"A","target_member_id":"B"}""", "")]
    [InlineData("chat.message.created", """{"messageId":"m","conversationId":"c"}""", "")]
    public void Only_chat_events_reach_and_only_their_two_members(string eventType, string payload, string expected)
    {
        Assert.Equal(expected, string.Join(',', ChatRealtime.Recipients(eventType, payload)));
    }

    [Fact]
    public async Task Chat_events_go_to_Service_Bus_then_to_the_members_with_the_full_envelope()
    {
        var inner = new RecordingPublisher();
        var sender = new RecordingSender();
        var publisher = new SignalRChatEventPublisher(inner, sender, NullLogger<SignalRChatEventPublisher>.Instance);

        await publisher.PublishAsync(Event("MessageCreated.v1", """{"message_id":"m1","sender_member_id":"A","recipient_member_id":"B","server_sequence":7}"""), default);
        await publisher.PublishAsync(Event("MemberBlocked.v1", """{"actor_member_id":"A","target_member_id":"B"}"""), default);

        Assert.Equal(2, inner.Published.Count);
        var (members, envelope) = Assert.Single(sender.Sent);
        Assert.Equal(["A", "B"], members);
        Assert.Equal(("evt-MessageCreated.v1", "MessageCreated.v1", "MESSAGE"), (envelope.GetProperty("event_id").GetString(), envelope.GetProperty("event_type").GetString(), envelope.GetProperty("aggregate_type").GetString()));
        Assert.Equal(7, envelope.GetProperty("data").GetProperty("server_sequence").GetInt64());
    }

    [Fact]
    public async Task A_realtime_failure_is_best_effort_but_a_Service_Bus_failure_leaves_the_publish_incomplete()
    {
        var inner = new RecordingPublisher();
        var publisher = new SignalRChatEventPublisher(inner, new RecordingSender { Fail = true }, NullLogger<SignalRChatEventPublisher>.Instance);
        await publisher.PublishAsync(Event("ConversationRead.v1", """{"reader_member_id":"B","sender_member_id":"A"}"""), default);
        Assert.Single(inner.Published);

        var sender = new RecordingSender();
        var failing = new SignalRChatEventPublisher(new RecordingPublisher { Fail = true }, sender, NullLogger<SignalRChatEventPublisher>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.PublishAsync(Event("MessageCreated.v1", """{"sender_member_id":"A","recipient_member_id":"B"}"""), default));
        Assert.Empty(sender.Sent);
    }

    private static ClaimedOutboxEvent Event(string type, string payload) =>
        new($"evt-{type}", type.StartsWith("Conversation") ? "CONVERSATION" : "MESSAGE", "agg", type, payload, DateTimeOffset.UtcNow, 1, "lease", DateTimeOffset.UtcNow.AddMinutes(1));

    private sealed class RecordingPublisher : IOutboxEventPublisher
    {
        public bool Fail { get; init; }
        public List<ClaimedOutboxEvent> Published { get; } = [];
        public Task PublishAsync(ClaimedOutboxEvent outboxEvent, CancellationToken cancellationToken)
        {
            if (Fail) throw new InvalidOperationException("Service Bus down");
            Published.Add(outboxEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSender : IChatRealtimeSender
    {
        public bool Fail { get; init; }
        public List<(IReadOnlyList<string> Members, JsonElement Envelope)> Sent { get; } = [];
        public Task SendAsync(IReadOnlyList<string> memberIds, JsonElement envelope, CancellationToken cancellationToken)
        {
            if (Fail) throw new HttpRequestException("SignalR down");
            Sent.Add((memberIds, envelope.Clone()));
            return Task.CompletedTask;
        }
    }
}

public sealed class ChatHubApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> factory;
    public ChatHubApiTests(WebApplicationFactory<Program> factory) => this.factory = factory.WithWebHostBuilder(b =>
    {
        b.UseEnvironment("Testing");
        b.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Mvp:AllowInsecureMemberHeader"] = "true" }));
    });

    [Fact]
    public async Task Negotiate_needs_a_member_but_no_idempotency_key()
    {
        var client = factory.CreateClient();

        var anonymous = await client.PostAsync($"{ChatRealtime.HubPath}/negotiate?negotiateVersion=1", null);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ChatRealtime.HubPath}/negotiate?negotiateVersion=1");
        request.Headers.Add("X-Member-Id", "A123");
        var member = await client.SendAsync(request);
        var byQuery = await client.PostAsync($"{ChatRealtime.HubPath}/negotiate?negotiateVersion=1&member_id=A123", null);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, member.StatusCode);
        Assert.Contains("connectionToken", await member.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, byQuery.StatusCode);
    }

    [Fact]
    public async Task Negotiate_rejects_the_MVP_identity_fallback_when_it_is_disabled()
    {
        using var securedFactory = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Mvp:AllowInsecureMemberHeader"] = "false" })));
        using var client = securedFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ChatRealtime.HubPath}/negotiate?negotiateVersion=1&member_id=A123");
        request.Headers.Add("X-Member-Id", "A123");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
