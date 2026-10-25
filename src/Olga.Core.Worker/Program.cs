using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.NotificationHubs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Olga.Core.Infrastructure;
using Olga.Core.Application;
using Olga.Core.Worker;

var builder = Host.CreateApplicationBuilder(args);
var connection = builder.Configuration.GetConnectionString("PostgreSql");
if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("ConnectionStrings:PostgreSql is required by the Core worker.");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connection));
builder.Services.AddDbContextPool<CoreDbContext>((services, options) =>
    options.UseOlgaPostgreSql(services.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton<IProfileImageStorage, AzureProfileImageStorage>();
builder.Services.AddSingleton(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var serviceBusConnection = configuration.GetConnectionString("ServiceBus");
    if (!string.IsNullOrWhiteSpace(serviceBusConnection)) return new ServiceBusClient(serviceBusConnection);

    var fullyQualifiedNamespace = configuration["ServiceBus:FullyQualifiedNamespace"];
    if (string.IsNullOrWhiteSpace(fullyQualifiedNamespace))
        throw new InvalidOperationException("ServiceBus:FullyQualifiedNamespace or ConnectionStrings:ServiceBus is required by the Core worker.");
    var managedIdentityClientId = configuration["ServiceBus:ManagedIdentityClientId"];
    if (string.IsNullOrWhiteSpace(managedIdentityClientId))
        managedIdentityClientId = configuration["AZURE_CLIENT_ID"];
    var credentialOptions = new DefaultAzureCredentialOptions
    {
        ManagedIdentityClientId = managedIdentityClientId
    };
    return new ServiceBusClient(fullyQualifiedNamespace, new DefaultAzureCredential(credentialOptions));
});
builder.Services.AddOptions<OutboxPublisherOptions>()
    .Bind(builder.Configuration.GetSection(OutboxPublisherOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<OutboxPublisherOptions>, OutboxPublisherOptionsValidator>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IOutboxRepository, NpgsqlOutboxRepository>();
builder.Services.AddSingleton<IOutboxFailureClassifier, OutboxFailureClassifier>();
builder.Services.AddSingleton<IOutboxJitterSource, RandomOutboxJitterSource>();
builder.Services.AddSingleton<IOutboxRetryCalculator, OutboxRetryCalculator>();
builder.Services.AddSingleton<ServiceBusOutboxEventPublisher>();
// With SignalR:Endpoint set, chat events also go to the conversation's members' devices.
var signalREndpoint = builder.Configuration["SignalR:Endpoint"];
var signalRRequired = builder.Configuration.GetValue<bool>("SignalR:Required");
if (signalRRequired && string.IsNullOrWhiteSpace(signalREndpoint))
    throw new InvalidOperationException("SignalR:Endpoint is required when SignalR:Required is enabled.");
if (!string.IsNullOrWhiteSpace(signalREndpoint)
    && (!Uri.TryCreate(signalREndpoint, UriKind.Absolute, out var signalRUri) || signalRUri.Scheme != Uri.UriSchemeHttps))
    throw new InvalidOperationException("SignalR:Endpoint must be an absolute HTTPS URI.");
var realtime = !string.IsNullOrWhiteSpace(signalREndpoint);
if (realtime) builder.Services.AddSingleton<IChatRealtimeSender, AzureSignalRChatSender>();
// Push needs Notification Hubs and the identity protection key that decrypts the stored push tokens.
var push = builder.Configuration.GetSection(PushOptions.SectionName).Get<PushOptions>() ?? new PushOptions();
var tokenKey = ReadIdentityKey(builder.Configuration["IdentityProtection:MasterKeyBase64"]);
var pushEnabled = push.IsConfigured && tokenKey is not null;
if (pushEnabled)
{
    builder.Services.AddSingleton<IIdentityProtector>(new AesIdentityProtector(tokenKey!));
    builder.Services.AddSingleton<INotificationHubClient>(NotificationHubClient.CreateClientFromConnectionString(push.NotificationHubConnectionString, push.NotificationHubName));
    builder.Services.AddSingleton<IPushSender, NotificationHubPushSender>();
    builder.Services.AddSingleton<IChatPushStore, NpgsqlChatPushStore>();
    builder.Services.AddSingleton<PushNotificationQueue>();
    builder.Services.AddScoped<ChatPushDispatcher>();
    builder.Services.AddHostedService<PushNotificationWorker>();
}
builder.Services.AddSingleton<IOutboxEventPublisher>(sp =>
{
    IOutboxEventPublisher publisher = sp.GetRequiredService<ServiceBusOutboxEventPublisher>();
    if (realtime) publisher = new SignalRChatEventPublisher(publisher, sp.GetRequiredService<IChatRealtimeSender>(), sp.GetRequiredService<ILogger<SignalRChatEventPublisher>>());
    if (pushEnabled) publisher = new PushOutboxEventPublisher(publisher, sp.GetRequiredService<PushNotificationQueue>(), sp.GetRequiredService<ILogger<PushOutboxEventPublisher>>());
    return publisher;
});
builder.Services.AddScoped<NlpCompletionProjectionHandler>();
builder.Services.AddScoped<ProfileImageProcessor>();
builder.Services.AddHostedService<OutboxPublisherWorker>();
builder.Services.AddHostedService<NlpCompletionConsumerWorker>();
builder.Services.AddHostedService<ProfileImageConsumerWorker>();
var host = builder.Build();
if (!pushEnabled) host.Services.GetRequiredService<ILogger<Program>>().LogInformation("Push notifications are off: Push:NotificationHubConnectionString, Push:NotificationHubName and IdentityProtection:MasterKeyBase64 are all needed.");
await host.RunAsync();

static byte[]? ReadIdentityKey(string? encoded)
{
    try { return Convert.FromBase64String(encoded ?? "") is { Length: 32 } key ? key : null; }
    catch (FormatException) { return null; }
}

// SignalR brings in ASP.NET Core, whose generator would make Program public and clash with the API's Program in tests.
internal partial class Program;
