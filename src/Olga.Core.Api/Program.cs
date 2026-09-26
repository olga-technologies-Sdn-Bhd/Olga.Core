using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Npgsql;
using Olga.Core.Api;
using Olga.Core.Application;
using Olga.Core.Contracts;
using Olga.Core.Domain;
using Olga.Core.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower; o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull; });
const string memberIdHeader = "X-Member-Id";
const string idempotencyKeyHeader = "Idempotency-Key";
const string ifMatchHeader = "If-Match";
const string adminKeyHeader = "X-Admin-Key";
var defaultMemberId = builder.Configuration["Mvp:DefaultMemberId"] ?? "A123";
var defaultCommunityId = builder.Configuration["Mvp:DefaultCommunityId"] ?? "olga";
var configuredAdminKey = builder.Configuration["Admin:ApiKey"];
var includeExceptionDetails = builder.Configuration.GetValue<bool>("Diagnostics:IncludeExceptionDetails");
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        // Resolve API calls against the origin that served Swagger, never Kestrel's internal HTTP endpoint.
        document.Servers = [new OpenApiServer { Url = "/" }];
        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, context, _) =>
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        if (metadata.OfType<MemberContextMetadata>().Any())
            AddHeaderParameter(operation, memberIdHeader, false, $"MVP caller member ID. Defaults to {defaultMemberId} when omitted.", 64);

        if (metadata.OfType<OptionalMemberContextMetadata>().Any())
            AddHeaderParameter(operation, memberIdHeader, false, "Optional MVP caller member ID. When supplied, each event includes is_registered for this member; no default member is applied.", 64);

        var method = context.Description.HttpMethod;
        if (context.Description.RelativePath?.StartsWith("v1/", StringComparison.OrdinalIgnoreCase) == true
            && method is "POST" or "PUT" or "PATCH" or "DELETE")
            AddHeaderParameter(operation, idempotencyKeyHeader, true, "Unique key for this logical mutation. Reuse the same key only when retrying the same request.", 128);

        if (metadata.OfType<AdminKeyMetadata>().Any())
            AddHeaderParameter(operation, adminKeyHeader, true, "Admin API key. Interim protection for admin routes until Entra sign-in is wired.");

        if (metadata.OfType<IfMatchMetadata>().Any())
            AddHeaderParameter(operation, ifMatchHeader, false, "ETag returned by GET /v1/me/profile. Required after the initial empty draft update.");

        return Task.CompletedTask;
    });
});
builder.Services.AddHealthChecks();
var connection = builder.Configuration.GetConnectionString("PostgreSql");
var local = string.IsNullOrWhiteSpace(connection);
if (local) builder.Services.AddDbContextPool<CoreDbContext>(o => o.UseInMemoryDatabase("olga-core-local"));
else builder.Services.AddDbContextPool<CoreDbContext>(o => o.UseOlgaPostgreSql(connection!));
var identityMasterKey = local
    ? System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)
    : ReadIdentityMasterKey(builder.Configuration);
builder.Services.AddSingleton<IIdentityProtector>(new AesIdentityProtector(identityMasterKey));
builder.Services.AddScoped<ICoreStore>(sp => sp.GetRequiredService<CoreDbContext>());
builder.Services.AddScoped<ICoreService, CoreService>();
builder.Services.AddScoped<IAdminEventService, AdminEventService>();
builder.Services.AddScoped<IAdminService, AdminService>();
// Local InMemory runs get a fixed dev key; deployed environments must supply Admin__ApiKey or admin routes stay closed.
var adminKey = string.IsNullOrWhiteSpace(configuredAdminKey) ? (local ? "local-admin-key" : null) : configuredAdminKey;

var app = builder.Build();
if (app.Environment.IsProduction()) app.UseMiddleware<AzureIngressHstsMiddleware>();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Correlation-Id"] = context.TraceIdentifier;
    try
    {
        if (context.Request.ContentLength is > 256_000) { await Error(context, 413, "PAYLOAD_TOO_LARGE"); return; }
        var idempotencyKey = context.Request.Headers[idempotencyKeyHeader].ToString();
        if (context.Request.Path.StartsWithSegments("/v1") && context.Request.Method is not ("GET" or "HEAD" or "OPTIONS") && (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128))
        {
            await Error(context, 400, "IDEMPOTENCY_KEY_REQUIRED");
            return;
        }
        await next();
    }
    catch (DomainException ex) { await Error(context, ex.StatusCode, ex.Code); }
    catch (DbUpdateConcurrencyException) { await Error(context, 409, "RESOURCE_VERSION_CONFLICT"); }
    catch (DbUpdateException ex) when (PostgreSqlConfiguration.IsUniqueViolation(ex)) { await Error(context, 409, "RESOURCE_CONFLICT"); }
    catch (DbUpdateException ex) when (PostgreSqlConfiguration.IsForeignKeyViolation(ex)) { await Error(context, 409, "RESOURCE_REFERENCE_NOT_FOUND"); }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { await Error(context, 409, "IDEMPOTENCY_KEY_REUSED"); }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation) { await Error(context, 409, "RESOURCE_REFERENCE_NOT_FOUND"); }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation) { await Error(context, 409, "RESOURCE_STATE_CONFLICT"); }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege) { await Error(context, 403, "RESOURCE_FORBIDDEN"); }
    catch (PostgresException ex) when (ex.SqlState == "P0002") { await Error(context, 404, "RESOURCE_NOT_FOUND"); }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidParameterValue) { await Error(context, 400, "REQUEST_INVALID"); }
    catch (Exception ex) when (PostgreSqlConfiguration.IsUnavailable(ex)) { await Error(context, 503, "DATABASE_UNAVAILABLE"); }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled exception for {Method} {Path}; correlation ID {CorrelationId}", context.Request.Method, context.Request.Path, context.TraceIdentifier);
        await Error(context, 500, "INTERNAL_ERROR", ex, includeExceptionDetails);
    }
});
app.MapOpenApi("/swagger/{documentName}/swagger.json");
app.UseSwaggerUI(options =>
{
    options.RoutePrefix = "swagger";
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "OLGA Core API v1");
});
app.MapHealthChecks("/health");
app.MapGet("/ready", async (CoreDbContext db, CancellationToken ct) => await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503))
    .WithName("GetReadiness").WithTags("Operations").Produces(200).Produces(503);

var v1 = app.MapGroup("/v1");
var memberV1 = app.MapGroup("/v1").WithMetadata(new MemberContextMetadata());
v1.MapPost("/members", async (HttpContext c, MemberCreateRequest body, ICoreService s, CancellationToken ct) =>
{
    var value = await s.RegisterMemberAsync(defaultCommunityId, body, Idempotency(c), ct);
    c.Response.Headers.ETag = value.ETag;
    return Results.Created("/v1/me/profile", value);
})
    .WithName("RegisterMember").WithTags("Members").Produces<MemberRegistrationResponse>(201);
v1.MapPost("/members/lookup", async (HttpContext c, MemberLookupRequest body, ICoreService s, CancellationToken ct) =>
{
    var value = await s.LookupMemberByEmailAsync(body, ct);
    c.Response.Headers.ETag = value.ETag;
    return Results.Ok(value);
})
    .WithName("LookupMember").WithTags("Members").Produces<MemberLookupResponse>(200);
var profileV1 = memberV1.MapGroup("/me/profile");
profileV1.AddEndpointFilter(async (invocationContext, next) =>
{
    var context = invocationContext.HttpContext;
    await context.RequestServices.GetRequiredService<ICoreService>().ProvisionMemberAsync(Member(context), context.RequestAborted);
    return await next(invocationContext);
});
profileV1.MapGet("", async (HttpContext c, ICoreService s, CancellationToken ct) => { var id = Member(c); var value = await s.GetOwnProfileAsync(id, ct); c.Response.Headers.ETag = value.ETag; return Results.Ok(value); })
    .WithName("GetOwnProfile").WithTags("Members").Produces<ProfileResponse>(200);
profileV1.MapPatch("", async (HttpContext c, ProfileUpdateRequest body, ICoreService s, CancellationToken ct) => { var value = await s.UpdateProfileAsync(Member(c), body, c.Request.Headers.IfMatch.FirstOrDefault(), ct); c.Response.Headers.ETag = value.ETag; return Results.Ok(value); })
    .WithMetadata(new IfMatchMetadata())
    .WithName("UpdateOwnProfile").WithTags("Members").Produces<ProfileResponse>(200);
memberV1.MapGet("/members/{memberId}", async (HttpContext c, string memberId, ICoreService s, CancellationToken ct) => Results.Ok(await s.GetVisibleProfileAsync(Member(c), memberId, ct)))
    .WithName("GetMemberProfile").WithTags("Members").Produces<ProfileResponse>(200);
memberV1.MapPost("/me/consents", async (HttpContext c, ConsentRequest body, ICoreService s, CancellationToken ct) => Results.Created("/v1/me/consents", await s.RecordConsentAsync(Member(c), body, ct)))
    .WithName("RecordConsent").WithTags("Members").Produces<ConsentResponse>(201);
v1.MapGet("/events", async (HttpContext c, ICoreService s, CancellationToken ct) => Results.Ok(await s.GetEventsAsync(OptionalMember(c), ct)))
    .WithMetadata(new OptionalMemberContextMetadata())
    .WithName("ListEvents").WithTags("Events").Produces<IReadOnlyList<EventResponse>>(200);
memberV1.MapPost("/events/{eventId}/register", async (HttpContext c, string eventId, ICoreService s, CancellationToken ct) => Results.Ok(await s.RegisterAsync(Member(c), eventId, ct)))
    .WithName("RegisterForEvent").WithTags("Events").Produces<RegistrationResponse>(200);
memberV1.MapPost("/events/{eventId}/live-mode", async (HttpContext c, string eventId, LiveModeRequest body, ICoreService s, CancellationToken ct) => Results.Ok(await s.StartLiveModeAsync(Member(c), eventId, body, ct)))
    .WithName("StartLiveMode").WithTags("Events").Produces<LiveModeResponse>(200);
memberV1.MapDelete("/events/{eventId}/live-mode", async (HttpContext c, string eventId, ICoreService s, CancellationToken ct) => { await s.StopLiveModeAsync(Member(c), eventId, ct); return Results.NoContent(); })
    .WithName("StopLiveMode").WithTags("Events").Produces(204);
memberV1.MapPost("/events/{eventId}/presence", async (HttpContext c, string eventId, PresenceRequest body, ICoreService s, CancellationToken ct) => { await s.RecordPresenceAsync(Member(c), eventId, body, ct); return Results.Accepted(); })
    .WithName("RecordPresence").WithTags("Events").Produces(202);
memberV1.MapPost("/connection-requests", async (HttpContext c, ConnectionRequestCreate body, ICoreService s, CancellationToken ct) => Results.Created("/v1/connection-requests", await s.CreateConnectionRequestAsync(Member(c), body, ct)))
    .WithName("CreateConnectionRequest").WithTags("Social").Produces<ConnectionRequestResponse>(201);
memberV1.MapPatch("/connection-requests/{requestId}", async (HttpContext c, string requestId, ConnectionDecisionRequest body, ICoreService s, CancellationToken ct) => Results.Ok(await s.DecideConnectionRequestAsync(Member(c), requestId, body, Idempotency(c), ct)))
    .WithName("DecideConnectionRequest").WithTags("Social").Produces<ConnectionResponse>(200);
memberV1.MapGet("/connections", async (HttpContext c, ICoreService s, CancellationToken ct) => Results.Ok(await s.GetConnectionsAsync(Member(c), ct)))
    .WithName("ListConnections").WithTags("Social").Produces<IReadOnlyList<ConnectionResponse>>(200);
memberV1.MapPost("/members/block", async (HttpContext c, BlockRequest body, ICoreService s, CancellationToken ct) => { await s.BlockAsync(Member(c), body, ct); return Results.NoContent(); })
    .WithName("BlockMember").WithTags("Social").Produces(204);
memberV1.MapGet("/conversations/{conversationId}/messages", async (HttpContext c, string conversationId, long? after, int? limit, ICoreService s, CancellationToken ct) => Results.Ok(await s.GetMessagesAsync(Member(c), conversationId, after ?? 0, limit ?? 50, ct)))
    .WithName("ListMessages").WithTags("Chat").Produces<IReadOnlyList<MessageResponse>>(200);
memberV1.MapPost("/conversations/{conversationId}/messages", async (HttpContext c, string conversationId, MessageCreateRequest body, ICoreService s, CancellationToken ct) => Results.Created($"/v1/conversations/{conversationId}/messages/{body.MessageId}", await s.SendMessageAsync(Member(c), conversationId, body, Idempotency(c), ct)))
    .WithName("SendMessage").WithTags("Chat").Produces<MessageResponse>(201);
memberV1.MapPut("/messages/{messageId}/receipt", async (HttpContext c, string messageId, MessageReceiptRequest body, ICoreService s, CancellationToken ct) => Results.Ok(await s.SaveMessageReceiptAsync(Member(c), messageId, body, Idempotency(c), ct)))
    .WithName("SaveMessageReceipt").WithTags("Chat").Produces<MessageReceiptResponse>(200);
memberV1.MapPatch("/me/notification-preferences", async (HttpContext c, NotificationPreferenceRequest body, ICoreService s, CancellationToken ct) => Results.Ok(await s.SetNotificationPreferenceAsync(Member(c), body, ct)))
    .WithName("SetNotificationPreference").WithTags("Preferences and privacy").Produces<NotificationPreferenceResponse>(200);
memberV1.MapPost("/me/privacy-requests", async (HttpContext c, PrivacyRequestCreate body, ICoreService s, CancellationToken ct) => Results.Accepted("/v1/me/privacy-requests", await s.CreatePrivacyRequestAsync(Member(c), body, ct)))
    .WithName("CreatePrivacyRequest").WithTags("Preferences and privacy").Produces<PrivacyRequestResponse>(202);
memberV1.MapGet("/sync/changes", async (HttpContext c, string? cursor, int? limit, ICoreService s, CancellationToken ct) => Results.Ok(await s.GetChangesAsync(Member(c), DecodeCursor(cursor), limit ?? 100, ct)))
    .WithName("GetSyncChanges").WithTags("Offline sync").Produces<SyncResponse>(200);

var adminV1 = app.MapGroup("/v1/admin").WithMetadata(new AdminKeyMetadata());
adminV1.AddEndpointFilter(async (invocationContext, next) =>
{
    if (adminKey is null) throw new DomainException("ADMIN_NOT_CONFIGURED", 503);
    var supplied = invocationContext.HttpContext.Request.Headers[adminKeyHeader].ToString();
    if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(adminKey)))
        throw new DomainException("ADMIN_KEY_INVALID", 401);
    return await next(invocationContext);
});
adminV1.MapGet("/events", async (string? status, IAdminEventService s, CancellationToken ct) => Results.Ok(await s.GetEventsAsync(status, ct)))
    .WithName("AdminListEvents").WithTags("Admin events").Produces<IReadOnlyList<AdminEventResponse>>(200);
adminV1.MapGet("/events/{eventId}", async (string eventId, IAdminEventService s, CancellationToken ct) => Results.Ok(await s.GetEventAsync(eventId, ct)))
    .WithName("AdminGetEvent").WithTags("Admin events").Produces<AdminEventResponse>(200);
adminV1.MapPost("/events", async (HttpContext c, AdminEventCreateRequest body, IAdminEventService s, CancellationToken ct) => { var value = await s.CreateEventAsync(defaultCommunityId, body, Idempotency(c), ct); return Results.Created($"/v1/admin/events/{value.EventId}", value); })
    .WithName("AdminCreateEvent").WithTags("Admin events").Produces<AdminEventResponse>(201);
adminV1.MapPut("/events/{eventId}", async (string eventId, AdminEventUpdateRequest body, IAdminEventService s, CancellationToken ct) => Results.Ok(await s.UpdateEventAsync(eventId, body, ct)))
    .WithName("AdminUpdateEvent").WithTags("Admin events").Produces<AdminEventResponse>(200);
adminV1.MapPost("/events/{eventId}/publish", async (string eventId, IAdminEventService s, CancellationToken ct) => Results.Ok(await s.PublishEventAsync(eventId, ct)))
    .WithName("AdminPublishEvent").WithTags("Admin events").Produces<AdminEventResponse>(200);
adminV1.MapPost("/events/{eventId}/cancel", async (string eventId, IAdminEventService s, CancellationToken ct) => Results.Ok(await s.CancelEventAsync(eventId, ct)))
    .WithName("AdminCancelEvent").WithTags("Admin events").Produces<AdminEventResponse>(200);
adminV1.MapGet("/stats", async (IAdminService s, CancellationToken ct) => Results.Ok(await s.GetStatsAsync(ct)))
    .WithName("AdminGetStats").WithTags("Admin stats").Produces<AdminStatsResponse>(200);
adminV1.MapGet("/members", async (string? status, string? search, IAdminService s, CancellationToken ct) => Results.Ok(await s.GetMembersAsync(status, search, ct)))
    .WithName("AdminListMembers").WithTags("Admin members").Produces<IReadOnlyList<AdminMemberResponse>>(200);
adminV1.MapPatch("/members/{memberId}", async (string memberId, AdminMemberStatusRequest body, IAdminService s, CancellationToken ct) => Results.Ok(await s.SetMemberStatusAsync(memberId, body, ct)))
    .WithName("AdminSetMemberStatus").WithTags("Admin members").Produces<AdminMemberResponse>(200);
adminV1.MapGet("/reports", async (string? status, IAdminService s, CancellationToken ct) => Results.Ok(await s.GetReportsAsync(status, ct)))
    .WithName("AdminListReports").WithTags("Admin moderation").Produces<IReadOnlyList<AdminReportResponse>>(200);
adminV1.MapPatch("/reports/{reportId}", async (string reportId, AdminStatusRequest body, IAdminService s, CancellationToken ct) => Results.Ok(await s.SetReportStatusAsync(reportId, body, ct)))
    .WithName("AdminSetReportStatus").WithTags("Admin moderation").Produces<AdminReportResponse>(200);
adminV1.MapGet("/privacy-requests", async (string? status, IAdminService s, CancellationToken ct) => Results.Ok(await s.GetPrivacyRequestsAsync(status, ct)))
    .WithName("AdminListPrivacyRequests").WithTags("Admin privacy").Produces<IReadOnlyList<AdminPrivacyRequestResponse>>(200);
adminV1.MapPatch("/privacy-requests/{privacyRequestId}", async (string privacyRequestId, AdminStatusRequest body, IAdminService s, CancellationToken ct) => Results.Ok(await s.SetPrivacyRequestStatusAsync(privacyRequestId, body, ct)))
    .WithName("AdminSetPrivacyRequestStatus").WithTags("Admin privacy").Produces<AdminPrivacyRequestResponse>(200);
adminV1.MapGet("/events/{eventId}/attendees", async (string eventId, IAdminEventService s, CancellationToken ct) => Results.Ok(await s.GetAttendeesAsync(eventId, ct)))
    .WithName("AdminListEventAttendees").WithTags("Admin events").Produces<IReadOnlyList<AdminAttendeeResponse>>(200);
adminV1.MapGet("/venues", async (IAdminEventService s, CancellationToken ct) => Results.Ok(await s.GetVenuesAsync(ct)))
    .WithName("AdminListVenues").WithTags("Admin venues").Produces<IReadOnlyList<AdminVenueResponse>>(200);
adminV1.MapPost("/venues", async (HttpContext c, AdminVenueCreateRequest body, IAdminEventService s, CancellationToken ct) => { var value = await s.CreateVenueAsync(body, Idempotency(c), ct); return Results.Created($"/v1/admin/venues/{value.VenueId}", value); })
    .WithName("AdminCreateVenue").WithTags("Admin venues").Produces<AdminVenueResponse>(201);

if (local) await LocalDevelopmentSeeder.SeedAsync(app.Services, CancellationToken.None);
app.Run();

// For public routes that personalise the response only when the caller identifies itself.
static string? OptionalMember(HttpContext context)
{
    var id = context.Request.Headers[memberIdHeader].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(id)) return null;
    if (id.Length > 64) throw new DomainException("MEMBER_ID_INVALID");
    return id;
}

string Member(HttpContext context)
{
    var id = context.Request.Headers[memberIdHeader].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(id)) return defaultMemberId;
    if (id.Length > 64) throw new DomainException("MEMBER_ID_INVALID");
    return id;
}

string Idempotency(HttpContext context) => context.Request.Headers[idempotencyKeyHeader].ToString();

static void AddHeaderParameter(OpenApiOperation operation, string name, bool required, string description, int? maxLength = null)
{
    operation.Parameters ??= [];
    if (operation.Parameters.Any(parameter =>
            parameter.In == ParameterLocation.Header
            && string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase))) return;

    operation.Parameters.Add(new OpenApiParameter
    {
        Name = name,
        In = ParameterLocation.Header,
        Required = required,
        Description = description,
        Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = maxLength }
    });
}

static long DecodeCursor(string? cursor)
{
    if (string.IsNullOrWhiteSpace(cursor)) return 0;
    try
    {
        var value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        return long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var sequence) && sequence >= 0
            ? sequence
            : throw new FormatException();
    }
    catch (FormatException) { throw new DomainException("SYNC_CURSOR_INVALID"); }
}

static async Task Error(HttpContext context, int status, string code, Exception? exception = null, bool includeStackTrace = false)
{
    if (context.Response.HasStarted) return;
    context.Response.StatusCode = status; context.Response.ContentType = "application/problem+json";
    var message = exception?.GetBaseException().Message ?? code switch
    {
        "IDEMPOTENCY_KEY_REQUIRED" => "An Idempotency-Key header is required for every mutation.",
        "IF_MATCH_REQUIRED" => "An If-Match header is required.",
        "ADMIN_KEY_INVALID" => "A valid X-Admin-Key header is required.",
        "ADMIN_NOT_CONFIGURED" => "Admin routes are disabled because no admin API key is configured.",
        "MEMBER_NOT_REGISTERED" => "The member must be registered by the identity service before profile onboarding.",
        "RESOURCE_REFERENCE_NOT_FOUND" => "A referenced resource does not exist.",
        "RESOURCE_VERSION_CONFLICT" => "The resource changed since it was read.",
        "INTERNAL_ERROR" => "The request could not be completed.",
        _ => "The request is invalid or cannot be completed in its current state."
    };
    await context.Response.WriteAsJsonAsync(new ApiError(code, message, context.TraceIdentifier, StackTrace: includeStackTrace ? exception?.ToString() : null));
}

static byte[] ReadIdentityMasterKey(IConfiguration configuration)
{
    var encoded = configuration["IdentityProtection:MasterKeyBase64"];
    try
    {
        var key = Convert.FromBase64String(encoded ?? "");
        if (key.Length == 32) return key;
    }
    catch (FormatException) { }
    throw new InvalidOperationException("IdentityProtection__MasterKeyBase64 must contain a Base64-encoded 32-byte key.");
}

public partial class Program { }
internal sealed class MemberContextMetadata { }
internal sealed class OptionalMemberContextMetadata { }
internal sealed class IfMatchMetadata { }
internal sealed class AdminKeyMetadata { }
