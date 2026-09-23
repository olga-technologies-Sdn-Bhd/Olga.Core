using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Olga.Core.Infrastructure;
using Olga.Core.Worker;

var builder = Host.CreateApplicationBuilder(args);
var connection = builder.Configuration.GetConnectionString("PostgreSql");
if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("ConnectionStrings:PostgreSql is required by the Core worker.");
builder.Services.AddDbContextPool<CoreDbContext>(o => o.UseOlgaPostgreSql(connection, enableRetryOnFailure: false));
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
builder.Services.AddScoped<NlpCompletionProjectionHandler>();
builder.Services.AddHostedService<OutboxPublisherWorker>();
builder.Services.AddHostedService<NlpCompletionConsumerWorker>();
await builder.Build().RunAsync();
