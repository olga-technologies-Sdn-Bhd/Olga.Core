using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Olga.Core.Application;

namespace Olga.Core.Api;

// Server-to-client only: the worker sends committed chat events to the conversation's members.
// The class name is the Azure SignalR hub name (ChatRealtime.HubName).
public sealed class ChatHub : Hub;

// Prefer the authenticated name identifier. The MVP header/query fallback is available only when
// explicitly enabled for a non-production environment.
public sealed class RealtimeMemberMiddleware(RequestDelegate next, IConfiguration configuration)
{
    public const string AuthenticationType = "OlgaMvpMember";

    public Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(ChatRealtime.HubPath)) return next(context);
        var memberId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var usedInsecureFallback = false;
        if (string.IsNullOrWhiteSpace(memberId) && configuration.GetValue<bool>("Mvp:AllowInsecureMemberHeader"))
        {
            memberId = context.Request.Headers["X-Member-Id"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(memberId)) memberId = context.Request.Query["member_id"].FirstOrDefault();
            usedInsecureFallback = !string.IsNullOrWhiteSpace(memberId);
        }
        if (string.IsNullOrWhiteSpace(memberId) || memberId.Length > 64)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
        // The default user ID provider reads NameIdentifier, so Clients.User(memberId) reaches every device of the member.
        if (usedInsecureFallback)
            context.User.AddIdentity(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, memberId)], AuthenticationType));
        return next(context);
    }
}
