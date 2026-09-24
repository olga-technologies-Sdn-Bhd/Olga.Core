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
var defaultMemberId = builder.Configuration["Mvp:DefaultMemberId"] ?? "A123";
var defaultCommunityId = builder.Configuration["Mvp:DefaultCommunityId"] ?? "olga";
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

        var method = context.Description.HttpMethod;
        if (context.Description.RelativePath?.StartsWith("v1/", StringComparison.OrdinalIgnoreCase) == true
            && method is "POST" or "PUT" or "PATCH" or "DELETE")
            AddHeaderParameter(operation, idempotencyKeyHeader, true, "Unique key for this logical mutation. Reuse the same key only when retrying the same request.", 128);

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
builder.Services.AddScoped<ICoreStore>(sp => sp.GetRequiredService<CoreDbContext>());
builder.Services.AddScoped<ICoreService, CoreService>();

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
app.MapGet("/ready", async (CoreDbContext db, CancellationToken ct) => await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));

var v1 = app.MapGroup("/v1");
var memberV1 = app.MapGroup("/v1").WithMetadata(new MemberContextMetadata());
v1.MapPost("/members", async (HttpContext c, ICoreService s, CancellationToken ct) =>
{
    var value = await s.RegisterMemberAsync(defaultCommunityId, Idempotency(c), ct);
    return Results.Created("/v1/me/profile", value);
});
var profileV1 = memberV1.MapGroup("/me/profile");
profileV1.AddEndpointFilter(async (invocationContext, next) =>
{
    var context = invocationContext.HttpContext;
    await context.RequestServices.GetRequiredService<ICoreService>().ProvisionMemberAsync(Member(context), context.RequestAborted);
    return await next(invocationContext);
});
profileV1.MapGet("", async (HttpContext c, ICoreService s, CancellationToken ct) => { var id = Member(c); var value = await s.GetOwnProfileAsync(id, ct); c.Response.Headers.ETag = value.ETag; return Results.Ok(value); });
profileV1.MapPatch("", async (HttpContext c, ProfileUpdateRequest body, ICoreService s, CancellationToken ct) => { var value = await s.UpdateProfileAsync(Member(c), body, c.Request.Headers.IfMatch.FirstOrDefault(), ct); c.Response.Headers.ETag = value.ETag; return Results.Ok(value); })
    .WithMetadata(new IfMatchMetadata());
memberV1.MapGet("/members/{memberId}", async (HttpContext c, string memberId, ICoreService s, CancellationToken ct) => Results.Ok(await s.GetVisibleProfileAsync(Member(c), memberId, ct)));
memberV1.MapPost("/me/consents", async (HttpContext c, ConsentRequest body, ICoreService s, CancellationToken ct) => Results.Created("/v1/me/consents", await s.RecordConsentAsync(Member(c), body, ct)));
v1.MapGet("/events", async (ICoreService s, CancellationToken ct) => Results.Ok(await s.GetEventsAsync(ct)));
memberV1.MapPost("/events/{eventId}/register", async (HttpContext c, string eventId, ICoreService s, CancellationToken ct) => Results.Ok(await s.RegisterAsync(Member(c), eventId, ct)));
memberV1.MapPost("/events/{eventId}/live-mode", async (HttpContext c, string eventId, LiveModeRequest body, ICoreService s, CancellationToken ct) => Results.Ok(await s.StartLiveModeAsync(Member(c), eventId, body, ct)));
memberV1.MapDelete("/events/{eventId}/live-mode", async (HttpContext c, string eventId, ICoreService s, CancellationToken ct) => { await s.StopLiveModeAsync(Member(c), eventId, ct); return Results.NoContent(); });
memberV1.MapPost("/events/{eventId}/presence", async (HttpContext c, string eventId, PresenceRequest body, ICoreService s, CancellationToken ct) => { await s.RecordPresenceAsync(Member(c), eventId, body, ct); return Results.Accepted(); });
memberV1.MapPost("/connection-requests", async (HttpContext c, ConnectionRequestCreate body, ICoreService s, CancellationToken ct) => Results.Created("/v1/connection-requests", await s.CreateConnectionRequestAsync(Member(c), body, ct)));
memberV1.MapPatch("/connection-requests/{requestId}", async (HttpContext c, string requestId, ConnectionDecisionRequest body, ICoreService s, CancellationToken ct) => Results.Ok(await s.DecideConnectionRequestAsync(Member(c), requestId, body, Idempotency(c), ct)));
memberV1.MapGet("/connections", async (HttpContext c, ICoreService s, CancellationToken ct) => Results.Ok(await s.GetConnectionsAsync(Member(c), ct)));
memberV1.MapPost("/members/block", async (HttpContext c, BlockRequest body, ICoreService s, CancellationToken ct) => { await s.BlockAsync(Member(c), body, ct); return Results.NoContent(); });
memberV1.MapGet("/conversations/{conversationId}/messages", async (HttpContext c, string conversationId, long? after, int? limit, ICoreService s, CancellationToken ct) => Results.Ok(await s.GetMessagesAsync(Member(c), conversationId, after ?? 0, limit ?? 50, ct)));
memberV1.MapPost("/conversations/{conversationId}/messages", async (HttpContext c, string conversationId, MessageCreateRequest body, ICoreService s, CancellationToken ct) => Results.Created($"/v1/conversations/{conversationId}/messages/{body.MessageId}", await s.SendMessageAsync(Member(c), conversationId, body, Idempotency(c), ct)));
memberV1.MapPut("/messages/{messageId}/receipt", async (HttpContext c, string messageId, MessageReceiptRequest body, ICoreService s, CancellationToken ct) => Results.Ok(await s.SaveMessageReceiptAsync(Member(c), messageId, body, Idempotency(c), ct)));
memberV1.MapPatch("/me/notification-preferences", async (HttpContext c, NotificationPreferenceRequest body, ICoreService s, CancellationToken ct) => Results.Ok(await s.SetNotificationPreferenceAsync(Member(c), body, ct)));
memberV1.MapPost("/me/privacy-requests", async (HttpContext c, PrivacyRequestCreate body, ICoreService s, CancellationToken ct) => Results.Accepted("/v1/me/privacy-requests", await s.CreatePrivacyRequestAsync(Member(c), body, ct)));
memberV1.MapGet("/sync/changes", async (HttpContext c, string? cursor, int? limit, ICoreService s, CancellationToken ct) => Results.Ok(await s.GetChangesAsync(Member(c), DecodeCursor(cursor), limit ?? 100, ct)));

if (local) await LocalDevelopmentSeeder.SeedAsync(app.Services, CancellationToken.None);
app.Run();

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
        "MEMBER_NOT_REGISTERED" => "The member must be registered by the identity service before profile onboarding.",
        "RESOURCE_REFERENCE_NOT_FOUND" => "A referenced resource does not exist.",
        "RESOURCE_VERSION_CONFLICT" => "The resource changed since it was read.",
        "INTERNAL_ERROR" => "The request could not be completed.",
        _ => "The request is invalid or cannot be completed in its current state."
    };
    await context.Response.WriteAsJsonAsync(new ApiError(code, message, context.TraceIdentifier, StackTrace: includeStackTrace ? exception?.ToString() : null));
}

public partial class Program { }
internal sealed class MemberContextMetadata { }
internal sealed class IfMatchMetadata { }
