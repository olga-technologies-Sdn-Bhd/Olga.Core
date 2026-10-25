using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Olga.Core.Application;

namespace Olga.Core.Api;

// Server-to-client only: the worker sends committed chat events to the conversation's members.
// The class name is the Azure SignalR hub name (ChatRealtime.HubName).
public sealed class ChatHub : Hub;

// MVP identity for the realtime connection, like X-Member-Id on REST: the member ID from the header,
// or from the member_id query value where a WebSocket can't send headers. Replace with token validation with auth.
public sealed class RealtimeMemberMiddleware(RequestDelegate next)
{
    public const string AuthenticationType = "OlgaMvpMember";

    public Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(ChatRealtime.HubPath)) return next(context);
        var memberId = context.Request.Headers["X-Member-Id"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(memberId)) memberId = context.Request.Query["member_id"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(memberId) || memberId.Length > 64)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
        // The default user ID provider reads NameIdentifier, so Clients.User(memberId) reaches every device of the member.
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, memberId)], AuthenticationType));
        return next(context);
    }
}

// Sends TypingStarted.v1 through the hub to the other member only, in the outbox event envelope but never stored.
// Throttled per member and conversation; a SignalR failure is logged and the request still succeeds.
public sealed class SignalRChatTypingPublisher(IHubContext<ChatHub> hub, ChatTypingThrottle throttle, TimeProvider time, ILogger<SignalRChatTypingPublisher> logger) : IChatTypingPublisher
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task PublishAsync(string memberId, string recipientMemberId, string conversationId, CancellationToken ct)
    {
        if (!throttle.TryAcquire(memberId, conversationId)) return;
        var now = time.GetUtcNow();
        var envelope = JsonSerializer.SerializeToElement(new
        {
            EventId = $"typ_{Guid.NewGuid():N}",
            EventType = ChatRealtime.TypingStarted,
            AggregateType = "CONVERSATION",
            AggregateId = conversationId,
            OccurredAt = now,
            Data = new { ConversationId = conversationId, MemberId = memberId, ExpiresAt = now + ChatRealtime.TypingDuration }
        }, Json);
        try { await hub.Clients.User(recipientMemberId).SendAsync(ChatRealtime.ClientMethod, envelope, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Typing signal for conversation {ConversationId} was not delivered", conversationId);
        }
    }
}
