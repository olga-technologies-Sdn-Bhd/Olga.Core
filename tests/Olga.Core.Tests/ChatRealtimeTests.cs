using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
    public async Task A_realtime_failure_does_not_fail_the_outbox_publish_but_a_Service_Bus_failure_does()
    {
        var publisher = new SignalRChatEventPublisher(new RecordingPublisher(), new RecordingSender { Fail = true }, NullLogger<SignalRChatEventPublisher>.Instance);
        await publisher.PublishAsync(Event("ConversationRead.v1", """{"reader_member_id":"B","sender_member_id":"A"}"""), default);

        var sender = new RecordingSender();
        var failing = new SignalRChatEventPublisher(new RecordingPublisher { Fail = true }, sender, NullLogger<SignalRChatEventPublisher>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.PublishAsync(Event("MessageCreated.v1", """{"sender_member_id":"A","recipient_member_id":"B"}"""), default));
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public void Typing_is_forwarded_at_most_once_per_member_and_conversation_every_three_seconds()
    {
        var time = new ManualTime();
        var throttle = new ChatTypingThrottle(time);

        Assert.True(throttle.TryAcquire("A", "c1"));
        Assert.False(throttle.TryAcquire("A", "c1"));
        Assert.True(throttle.TryAcquire("A", "c2"));
        Assert.True(throttle.TryAcquire("B", "c1"));
        time.Now += TimeSpan.FromSeconds(2.9);
        Assert.False(throttle.TryAcquire("A", "c1"));
        time.Now += TimeSpan.FromSeconds(0.1);
        Assert.True(throttle.TryAcquire("A", "c1"));
    }

    [Fact]
    public async Task Typing_goes_to_the_other_member_only_in_the_event_envelope_and_survives_a_SignalR_failure()
    {
        var time = new ManualTime();
        var clients = new RecordingHubClients();
        var publisher = new Olga.Core.Api.SignalRChatTypingPublisher(new RecordingHubContext(clients), new ChatTypingThrottle(time), time, NullLogger<Olga.Core.Api.SignalRChatTypingPublisher>.Instance);

        await publisher.PublishAsync("A", "B", "c1", default);
        await publisher.PublishAsync("A", "B", "c1", default);

        var (user, method, envelope) = Assert.Single(clients.Sent);
        Assert.Equal(("B", ChatRealtime.ClientMethod, "TypingStarted.v1", "CONVERSATION", "c1"), (user, method, envelope.GetProperty("event_type").GetString(), envelope.GetProperty("aggregate_type").GetString(), envelope.GetProperty("aggregate_id").GetString()));
        var data = envelope.GetProperty("data");
        Assert.Equal(("c1", "A"), (data.GetProperty("conversation_id").GetString(), data.GetProperty("member_id").GetString()));
        Assert.Equal(time.Now.AddSeconds(5), data.GetProperty("expires_at").GetDateTimeOffset());
        Assert.StartsWith("typ_", envelope.GetProperty("event_id").GetString());

        clients.Fail = true;
        await publisher.PublishAsync("A", "B", "c2", default);
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingHubContext(RecordingHubClients clients) : Microsoft.AspNetCore.SignalR.IHubContext<Olga.Core.Api.ChatHub>
    {
        public Microsoft.AspNetCore.SignalR.IHubClients Clients => clients;
        public Microsoft.AspNetCore.SignalR.IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class RecordingHubClients : Microsoft.AspNetCore.SignalR.IHubClients
    {
        public bool Fail { get; set; }
        public List<(string User, string Method, JsonElement Envelope)> Sent { get; } = [];
        public Microsoft.AspNetCore.SignalR.IClientProxy User(string userId) => new Proxy(this, userId);
        public Microsoft.AspNetCore.SignalR.IClientProxy All => throw new NotSupportedException();
        public Microsoft.AspNetCore.SignalR.IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public Microsoft.AspNetCore.SignalR.IClientProxy Client(string connectionId) => throw new NotSupportedException();
        public Microsoft.AspNetCore.SignalR.IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
        public Microsoft.AspNetCore.SignalR.IClientProxy Group(string groupName) => throw new NotSupportedException();
        public Microsoft.AspNetCore.SignalR.IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public Microsoft.AspNetCore.SignalR.IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
        public Microsoft.AspNetCore.SignalR.IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();

        private sealed class Proxy(RecordingHubClients owner, string user) : Microsoft.AspNetCore.SignalR.IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            {
                if (owner.Fail) throw new HttpRequestException("SignalR down");
                owner.Sent.Add((user, method, ((JsonElement)args[0]!).Clone()));
                return Task.CompletedTask;
            }
        }
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
    public ChatHubApiTests(WebApplicationFactory<Program> factory) => this.factory = factory.WithWebHostBuilder(b => b.UseEnvironment("Testing"));

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
    public async Task Typing_needs_no_idempotency_key()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/conversations/unknown/typing");
        request.Headers.Add("X-Member-Id", "A123");
        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("CONVERSATION_NOT_FOUND", await response.Content.ReadAsStringAsync());
    }
}
