using SyncAgent;
using SyncAgent.Collectors;
using SyncAgent.Configuration;
using SyncAgent.Dispatching;
using SyncAgent.Heartbeat;
using SyncAgent.LocalApi;
using SyncAgent.Normalizers;
using SyncAgent.Pdv;
using SyncAgent.Persistence;
using SyncAgent.Provisioning;
using SyncAgent.Reconciliation;
using SyncAgent.Runtime;
using SyncAgent.Security;
using SyncAgent.Update;
using Microsoft.Extensions.Options;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);

// VERSION file is the authoritative source after self-updates; override appsettings if present.
var versionFilePath = Path.Combine(AppContext.BaseDirectory, "VERSION");
if (File.Exists(versionFilePath))
{
    var fileVersion = File.ReadAllText(versionFilePath).Trim();
    if (!string.IsNullOrWhiteSpace(fileVersion))
    {
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            { "SyncAgent:AgentVersion", fileVersion }
        });
    }
}

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "AraraSuiteSync";
});

builder.Services.Configure<SyncAgentOptions>(
    builder.Configuration.GetSection(SyncAgentOptions.SectionName));
builder.Services.Configure<ArpaCollectorOptions>(
    builder.Configuration.GetSection(ArpaCollectorOptions.SectionName));
builder.Services.Configure<ErpDispatcherOptions>(
    builder.Configuration.GetSection(ErpDispatcherOptions.SectionName));
builder.Services.Configure<ErpHeartbeatOptions>(
    builder.Configuration.GetSection(ErpHeartbeatOptions.SectionName));
builder.Services.Configure<ErpReconciliationOptions>(
    builder.Configuration.GetSection(ErpReconciliationOptions.SectionName));
builder.Services.Configure<ErpPdvSnapshotOptions>(
    builder.Configuration.GetSection(ErpPdvSnapshotOptions.SectionName));
builder.Services.Configure<PdvSalesPublisherOptions>(
    builder.Configuration.GetSection(PdvSalesPublisherOptions.SectionName));
builder.Services.Configure<ErpSecurityOptions>(
    builder.Configuration.GetSection(ErpSecurityOptions.SectionName));
builder.Services.Configure<SyncAgentProvisioningOptions>(
    builder.Configuration.GetSection(SyncAgentProvisioningOptions.SectionName));
builder.Services.AddSingleton<IValidateOptions<SyncAgentOptions>, SyncAgentOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<ArpaCollectorOptions>, ArpaCollectorOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<ErpDispatcherOptions>, ErpDispatcherOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<ErpHeartbeatOptions>, ErpHeartbeatOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<ErpReconciliationOptions>, ErpReconciliationOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<ErpPdvSnapshotOptions>, ErpPdvSnapshotOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<PdvSalesPublisherOptions>, PdvSalesPublisherOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<ErpSecurityOptions>, ErpSecurityOptionsValidator>();
builder.Services.AddSingleton(_ =>
{
    var connectionString = builder.Configuration.GetConnectionString("SyncAgentDb")
        ?? throw new InvalidOperationException("Connection string 'SyncAgentDb' is required.");

    return NpgsqlDataSource.Create(connectionString);
});
builder.Services.AddSingleton<LocalSyncStore>();
builder.Services.AddSingleton<IArpaPayloadNormalizer, ArpaProdutoPayloadNormalizer>();
builder.Services.AddSingleton<IArpaPayloadNormalizer, ArpaClientePayloadNormalizer>();
builder.Services.AddSingleton<IArpaPayloadNormalizer, ArpaEstoquePayloadNormalizer>();
builder.Services.AddSingleton<IArpaPayloadNormalizer, ArpaVendaPayloadNormalizer>();
builder.Services.AddSingleton<IArpaPayloadNormalizer, ArpaFinanceiroPayloadNormalizer>();
builder.Services.AddSingleton<ArpaPayloadNormalizerRegistry>();
builder.Services.AddSingleton<ArpaConnectionStringProvider>();
builder.Services.AddSingleton<ProvisioningStore>();
builder.Services.AddSingleton<EffectiveSyncAgentConfigurationProvider>();
builder.Services.AddSingleton<ErpActivationClient>();
builder.Services.AddSingleton<ArpaConnectionConfigClient>();
builder.Services.AddSingleton<ArpaRemoteConfigCache>();
builder.Services.AddSingleton<EffectiveArpaCollectorConfigurationProvider>();
builder.Services.AddSingleton<ArpaCollector>();
builder.Services.AddSingleton<ErpCredentialProvider>();
builder.Services.AddSingleton<ErpEventDispatcher>();
builder.Services.AddSingleton<ErpHeartbeatClient>();
builder.Services.AddSingleton<ErpReconciliationClient>();
builder.Services.AddSingleton<PdvOperatorSnapshotClient>();
builder.Services.AddSingleton<PdvProductSnapshotClient>();
builder.Services.AddSingleton<PdvPaymentMethodsSnapshotClient>();
builder.Services.AddSingleton<PdvCustomerSnapshotClient>();
builder.Services.AddSingleton<PdvSalesPublisher>();
builder.Services.AddSingleton<ErpLatestPackageClient>();
builder.Services.AddSingleton<UpdateProgressState>();
builder.Services.AddHttpClient(ErpEventDispatcherHttpClient.Name)
    .ConfigurePrimaryHttpMessageHandler(ErpHttpClientHandlerFactory.CreateHandler);
builder.Services.AddHttpClient(ErpHeartbeatHttpClient.Name)
    .ConfigurePrimaryHttpMessageHandler(ErpHttpClientHandlerFactory.CreateHandler);
builder.Services.AddHttpClient(ErpReconciliationHttpClient.Name)
    .ConfigurePrimaryHttpMessageHandler(ErpHttpClientHandlerFactory.CreateHandler);
builder.Services.AddHttpClient(PdvOperatorSnapshotHttpClient.Name)
    .ConfigurePrimaryHttpMessageHandler(ErpHttpClientHandlerFactory.CreateHandler);
builder.Services.AddHttpClient(PdvProductSnapshotHttpClient.Name)
    .ConfigurePrimaryHttpMessageHandler(ErpHttpClientHandlerFactory.CreateHandler);
builder.Services.AddHttpClient(PdvPaymentMethodsSnapshotHttpClient.Name)
    .ConfigurePrimaryHttpMessageHandler(ErpHttpClientHandlerFactory.CreateHandler);
builder.Services.AddHttpClient(PdvCustomerSnapshotHttpClient.Name)
    .ConfigurePrimaryHttpMessageHandler(ErpHttpClientHandlerFactory.CreateHandler);
builder.Services.AddHttpClient(ErpActivationHttpClient.Name);
builder.Services.AddHttpClient(ArpaConnectionConfigHttpClient.Name)
    .ConfigurePrimaryHttpMessageHandler(ErpHttpClientHandlerFactory.CreateHandler);
builder.Services.AddHttpClient(ErpLatestPackageHttpClient.Name)
    .ConfigurePrimaryHttpMessageHandler(ErpHttpClientHandlerFactory.CreateHandler);
builder.Services.AddSingleton<ManualSyncSignal>();
builder.Services.AddSingleton<SyncAgentRuntimeState>();
builder.Services.AddSingleton<SelfUpdater>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<LocalStatusServer>();

await builder.Build().RunAsync();
