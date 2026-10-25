using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Azure.NotificationHubs;
using Microsoft.Azure.NotificationHubs.Messaging;
using Olga.Core.Application;

namespace Olga.Core.Worker;

// Push is off unless both Notification Hubs settings and the identity protection key (to decrypt tokens) are set.
public sealed class PushOptions
{
    public const string SectionName = "Push";
    public string? NotificationHubConnectionString { get; init; }
    public string? NotificationHubName { get; init; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(NotificationHubConnectionString) && !string.IsNullOrWhiteSpace(NotificationHubName);
}

public sealed record PushWork(string OutboxEventId, string EventType, string PayloadJson);

// Hands events from the outbox publish to the push worker without waiting for any push to be sent.
// Bounded: under a backlog new pushes are dropped rather than slowing the outbox.
public sealed class PushNotificationQueue
{
    private readonly Channel<PushWork> channel = Channel.CreateBounded<PushWork>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public bool TryEnqueue(PushWork work) => channel.Writer.TryWrite(work);
    public IAsyncEnumerable<PushWork> ReadAllAsync(CancellationToken ct) => channel.Reader.ReadAllAsync(ct);
}

// Publishes as before, then queues the events that can raise a push. The push never fails or delays the publish.
public sealed class PushOutboxEventPublisher(IOutboxEventPublisher inner, PushNotificationQueue queue, ILogger<PushOutboxEventPublisher> logger) : IOutboxEventPublisher
{
    public async Task PublishAsync(ClaimedOutboxEvent outboxEvent, CancellationToken cancellationToken)
    {
        await inner.PublishAsync(outboxEvent, cancellationToken);
        if (ChatPushDispatcher.Handles(outboxEvent.EventType) && !queue.TryEnqueue(new(outboxEvent.OutboxEventId, outboxEvent.EventType, outboxEvent.PayloadJson)))
            logger.LogWarning("Push queue full; no push for outbox event {OutboxEventId}", outboxEvent.OutboxEventId);
    }
}

public sealed class PushNotificationWorker(PushNotificationQueue queue, IServiceScopeFactory scopes, ILogger<PushNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var work in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ChatPushDispatcher>().DispatchAsync(work, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Push for outbox event {OutboxEventId} of type {EventType} failed", work.OutboxEventId, work.EventType);
            }
        }
    }
}

// Turns outbox events into pushes. Logs IDs only: never message text, names or tokens.
public sealed class ChatPushDispatcher(IChatPushStore store, IPushSender sender, IIdentityProtector identityProtector, TimeProvider time, ILogger<ChatPushDispatcher> logger)
{
    public static bool Handles(string eventType) => eventType is "MessageCreated.v1";
    // Next on the same path: CommitReceived (push to the Commit's recipient) and CommitAccepted (push to its sender).

    public Task DispatchAsync(PushWork work, CancellationToken ct) => work.EventType switch
    {
        "MessageCreated.v1" => MessageCreatedAsync(work.PayloadJson, ct),
        _ => Task.CompletedTask
    };

    private async Task MessageCreatedAsync(string payloadJson, CancellationToken ct)
    {
        using var payload = JsonDocument.Parse(payloadJson);
        if (Text(payload, "message_id") is not { } messageId || Text(payload, "conversation_id") is not { } conversationId || Text(payload, "recipient_member_id") is not { } recipientId) return;
        if (await store.GetMessageFactsAsync(messageId, recipientId, ct) is not { } facts) return;
        if (ChatPush.Plan(conversationId, facts, time.GetUtcNow()) is not { } content) return;
        foreach (var target in facts.Targets) await SendAsync(target, content, messageId, ct);
    }

    private async Task SendAsync(PushTarget target, PushContent content, string messageId, CancellationToken ct)
    {
        string token;
        try { token = identityProtector.Unprotect(target.DeviceId, target.Provider, target.TokenCiphertext); }
        catch (CryptographicException)
        {
            logger.LogWarning("Push token {PushTokenId} could not be decrypted; skipped", target.PushTokenId);
            return;
        }
        var result = await sender.SendAsync(target.Provider, token, content, ct);
        if (result == PushResult.Failed) logger.LogWarning("Push for message {MessageId} to token {PushTokenId} failed", messageId, target.PushTokenId);
        else await store.RecordResultAsync(target.PushTokenId, result == PushResult.Delivered, ct);
    }

    private static string? Text(JsonDocument payload, string name) =>
        payload.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
}

// Direct sends to one device token through Azure Notification Hubs (FCM v1 and APNs).
// The hub rejects a malformed token at once; tokens the provider later reports expired are not seen here.
public sealed class NotificationHubPushSender(INotificationHubClient hub) : IPushSender
{
    public async Task<PushResult> SendAsync(string provider, string token, PushContent content, CancellationToken ct)
    {
        Notification? notification = provider switch
        {
            "FCM" => new FcmV1Notification(JsonSerializer.Serialize(new
            {
                message = new { notification = new { title = content.Title, body = content.Body }, data = content.Data, android = new { priority = "high" } }
            })),
            "APNS" => new AppleNotification(JsonSerializer.Serialize(
                new Dictionary<string, object>(content.Data.Select(x => KeyValuePair.Create(x.Key, (object)x.Value)))
                {
                    ["aps"] = new { alert = new { title = content.Title, body = content.Body }, sound = "default" }
                }), new Dictionary<string, string> { ["apns-push-type"] = "alert", ["apns-priority"] = "10" }),
            _ => null
        };
        if (notification is null) return PushResult.Failed;
        try
        {
            await hub.SendDirectNotificationAsync(notification, token, ct);
            return PushResult.Delivered;
        }
        catch (BadRequestException) { return PushResult.TokenInvalid; }
        catch (MessagingException) { return PushResult.Failed; }
    }
}
