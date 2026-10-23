using Azure.Identity;
using Azure.Messaging.ServiceBus;
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
builder.Services.AddSingleton<IOutboxEventPublisher, ServiceBusOutboxEventPublisher>();
builder.Services.AddScoped<NlpCompletionProjectionHandler>();
builder.Services.AddScoped<ProfileImageProcessor>();
builder.Services.AddHostedService<OutboxPublisherWorker>();
builder.Services.AddHostedService<NlpCompletionConsumerWorker>();
builder.Services.AddHostedService<ProfileImageConsumerWorker>();
await builder.Build().RunAsync();
