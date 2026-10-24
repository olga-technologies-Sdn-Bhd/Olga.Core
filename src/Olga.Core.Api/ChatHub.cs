using System.Security.Claims;
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
