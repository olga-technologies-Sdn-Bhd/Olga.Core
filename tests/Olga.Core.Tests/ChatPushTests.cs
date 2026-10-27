using Microsoft.Extensions.Logging.Abstractions;
using Olga.Core.Application;
using Olga.Core.Infrastructure;
using Olga.Core.Worker;

namespace Olga.Core.Tests;

public sealed class ChatPushTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
    private static readonly AesIdentityProtector Protector = new(Enumerable.Repeat((byte)5, 32).ToArray());

    [Fact]
    public void A_new_message_pushes_the_sender_name_and_never_the_text()
    {
        var push = ChatPush.Plan("c1", Facts(), Now)!;

        Assert.Equal(("Asha", "New message"), (push.Title, push.Body));
        Assert.Equal(new Dictionary<string, string> { ["type"] = "chat_message", ["conversation_id"] = "c1" }, push.Data);
        Assert.Equal("OLGA", ChatPush.Plan("c1", Facts() with { SenderDisplayName = " " }, Now)!.Title);
    }

    [Theory]
    [InlineData("image/heic", "Sent a photo")]
    [InlineData("audio/mp4", "Sent a voice note")]
    [InlineData("application/pdf", "Sent a file")]
    [InlineData(null, "Sent a file")]
    public void A_file_message_says_what_was_sent(string? mediaType, string body)
    {
        Assert.Equal(body, ChatPush.Plan("c1", Facts() with { MessageType = "FILE", AttachmentMediaType = mediaType }, Now)!.Body);
    }

    [Fact]
    public void No_push_when_muted_turned_off_blocked_closed_deleted_or_without_a_device()
    {
        Assert.Null(ChatPush.Plan("c1", Facts() with { MutedUntil = Now.AddHours(1) }, Now));
        Assert.Null(ChatPush.Plan("c1", Facts() with { ChatPushEnabled = false }, Now));
        Assert.Null(ChatPush.Plan("c1", Facts() with { Blocked = true }, Now));
        Assert.Null(ChatPush.Plan("c1", Facts() with { ConversationOpen = false }, Now));
        Assert.Null(ChatPush.Plan("c1", Facts() with { Deleted = true }, Now));
        Assert.Null(ChatPush.Plan("c1", Facts() with { Targets = [] }, Now));
        Assert.NotNull(ChatPush.Plan("c1", Facts() with { MutedUntil = Now.AddMinutes(-1), ChatPushEnabled = true }, Now));
    }

    [Fact]
    public async Task Each_device_is_pushed_and_a_rejected_token_becomes_invalid()
    {
        var store = new FakeStore(Facts() with
        {
            Targets =
            [
                Target(1, "d1", "FCM", "fcm-token"),
                Target(2, "d2", "APNS", "rejected-token"),
                new PushTarget(3, "d3", "FCM", [1, 2, 3])
            ]
        });
        var sender = new FakeSender();
        var dispatcher = new ChatPushDispatcher(store, sender, Protector, new FixedTime(), NullLogger<ChatPushDispatcher>.Instance);

        await dispatcher.DispatchAsync(new("e1", "MessageCreated.v1", """{"message_id":"m1","conversation_id":"c1","sender_member_id":"A","recipient_member_id":"B","server_sequence":3}"""), default);
        await dispatcher.DispatchAsync(new("e2", "ConversationRead.v1", """{"conversation_id":"c1"}"""), default);

        Assert.Equal(("m1", "B"), Assert.Single(store.Asked));
        Assert.Equal([("FCM", "fcm-token"), ("APNS", "rejected-token")], sender.Sent.Select(x => (x.Provider, x.Token)));
        Assert.Equal([(1L, true), (2L, false)], store.Results);
    }

    [Fact]
    public async Task Pushes_are_queued_only_after_a_successful_publish_and_only_for_chat_messages()
    {
        var queue = new PushNotificationQueue();
        var publisher = new PushOutboxEventPublisher(new StubPublisher(), queue, NullLogger<PushOutboxEventPublisher>.Instance);
        var failing = new PushOutboxEventPublisher(new StubPublisher { Fail = true }, queue, NullLogger<PushOutboxEventPublisher>.Instance);

        await publisher.PublishAsync(Event("e1", "MessageCreated.v1"), default);
        await publisher.PublishAsync(Event("e2", "MemberBlocked.v1"), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.PublishAsync(Event("e3", "MessageCreated.v1"), default));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var queued = new List<string>();
        try { await foreach (var work in queue.ReadAllAsync(cts.Token)) queued.Add(work.OutboxEventId); }
        catch (OperationCanceledException) { }
        Assert.Equal(["e1"], queued);
    }

    private static ChatPushFacts Facts() => new("TEXT", false, null, null, null, false, true, "Asha", [Target(1, "d1", "FCM", "token")]);
    private static PushTarget Target(long id, string deviceId, string provider, string token) => new(id, deviceId, provider, Protector.ProtectPushToken(deviceId, provider, token).SubjectCiphertext);
    private static ClaimedOutboxEvent Event(string id, string type) => new(id, "MESSAGE", "agg", type, "{}", Now, 1, "lease", Now.AddMinutes(1));

    private sealed class FixedTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    private sealed class FakeStore(ChatPushFacts facts) : IChatPushStore
    {
        public List<(string MessageId, string RecipientId)> Asked { get; } = [];
        public List<(long Id, bool Delivered)> Results { get; } = [];
        public Task<ChatPushFacts?> GetMessageFactsAsync(string messageId, string recipientMemberId, CancellationToken ct) { Asked.Add((messageId, recipientMemberId)); return Task.FromResult<ChatPushFacts?>(facts); }
        public Task RecordResultAsync(long pushTokenId, bool delivered, CancellationToken ct) { Results.Add((pushTokenId, delivered)); return Task.CompletedTask; }
    }

    private sealed class FakeSender : IPushSender
    {
        public List<(string Provider, string Token, PushContent Content)> Sent { get; } = [];
        public Task<PushResult> SendAsync(string provider, string token, PushContent content, CancellationToken ct)
        {
            Sent.Add((provider, token, content));
            return Task.FromResult(token == "rejected-token" ? PushResult.TokenInvalid : PushResult.Delivered);
        }
    }

    private sealed class StubPublisher : IOutboxEventPublisher
    {
        public bool Fail { get; init; }
        public Task PublishAsync(ClaimedOutboxEvent outboxEvent, CancellationToken cancellationToken) => Fail ? throw new InvalidOperationException("Service Bus down") : Task.CompletedTask;
    }
}
