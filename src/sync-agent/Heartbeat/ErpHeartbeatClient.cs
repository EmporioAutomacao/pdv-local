using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using SyncAgent.Configuration;
using SyncAgent.Persistence;
using SyncAgent.Provisioning;
using SyncAgent.Security;

namespace SyncAgent.Heartbeat;

public sealed class ErpHeartbeatClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<ErpHeartbeatClient> _logger;
    private readonly EffectiveSyncAgentConfigurationProvider _effectiveConfigProvider;
    private readonly IOptionsMonitor<ErpHeartbeatOptions> _heartbeatOptions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LocalSyncStore _localStore;
    private readonly ArpaConnectionsStore _arpaConnectionsStore;
    private readonly ErpCredentialProvider _credentialProvider;

    public ErpHeartbeatClient(
        ILogger<ErpHeartbeatClient> logger,
        EffectiveSyncAgentConfigurationProvider effectiveConfigProvider,
        IOptionsMonitor<ErpHeartbeatOptions> heartbeatOptions,
        IHttpClientFactory httpClientFactory,
        LocalSyncStore localStore,
        ArpaConnectionsStore arpaConnectionsStore,
        ErpCredentialProvider credentialProvider)
    {
        _logger = logger;
        _effectiveConfigProvider = effectiveConfigProvider;
        _heartbeatOptions = heartbeatOptions;
        _httpClientFactory = httpClientFactory;
        _localStore = localStore;
        _arpaConnectionsStore = arpaConnectionsStore;
        _credentialProvider = credentialProvider;
    }

    public async Task<HeartbeatSummary> SendAsync(
        LocalSyncStoreStatus storeStatus,
        string connectivity,
        CancellationToken cancellationToken)
    {
        var heartbeatOptions = _heartbeatOptions.CurrentValue;
        if (!heartbeatOptions.Enabled)
        {
            return HeartbeatSummary.Disabled;
        }

        var syncOptions = _effectiveConfigProvider.GetCurrent();
        var erpApiBaseUri = new Uri(syncOptions.ErpApiBaseUrl, UriKind.Absolute);
        ErpCredentialProvider.EnsureHttpsOutsideLocalDevelopment(erpApiBaseUri);
        _credentialProvider.ValidateProvisionedForRemoteEndpoint(erpApiBaseUri);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(heartbeatOptions.TimeoutSeconds));

        var arpaConnections = _arpaConnectionsStore.ReadAll();
        var request = new HeartbeatRequest(
            DateTimeOffset.UtcNow,
            syncOptions.AgentVersion,
            storeStatus.PendingOutboxEvents,
            storeStatus.OldestPendingAgeSeconds,
            connectivity,
            arpaConnections.Count == 0
                ? null
                : arpaConnections.Select(c => new HeartbeatArpaConnection(
                    c.Nome, c.Host, c.Port, c.Database, c.Username, c.LojaCodigo,
                    c.ControlaEstoque, c.SyncProdutos, c.SyncClientes, c.SyncEstoque,
                    c.SyncVendas, c.SyncFinanceiro, c.SyncCobranca, c.SyncPlanoHistorico,
                    c.Enabled)).ToList());

        var escapedInstanceId = Uri.EscapeDataString(syncOptions.InstanceId);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/v1/sync/agents/{escapedInstanceId}/heartbeat")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(request, JsonOptions),
                Encoding.UTF8,
                "application/json")
        };

        var authorization = _credentialProvider.CreateAuthorizationHeader();
        if (authorization is not null)
        {
            httpRequest.Headers.Authorization = authorization;
        }

        var client = _httpClientFactory.CreateClient(ErpHeartbeatHttpClient.Name);
        client.BaseAddress = erpApiBaseUri;
        client.Timeout = TimeSpan.FromSeconds(heartbeatOptions.TimeoutSeconds);

        try
        {
            using var response = await client.SendAsync(httpRequest, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var error = $"ERP heartbeat returned HTTP {(int)response.StatusCode}.";
                await _localStore.UpsertAgentHeartbeatAsync(
                    DateTimeOffset.UtcNow,
                    false,
                    connectivity,
                    (int)response.StatusCode,
                    error,
                    cancellationToken);

                return new HeartbeatSummary(true, false, null, error);
            }

            var heartbeatResponse = await response.Content.ReadFromJsonAsync<HeartbeatResponse>(
                cancellationToken: cancellationToken);

            if (heartbeatResponse is null || heartbeatResponse.Status != "ok")
            {
                const string error = "ERP heartbeat returned an invalid response.";
                await _localStore.UpsertAgentHeartbeatAsync(
                    DateTimeOffset.UtcNow,
                    false,
                    connectivity,
                    (int)response.StatusCode,
                    error,
                    cancellationToken);

                return new HeartbeatSummary(true, false, null, error);
            }

            await _localStore.UpsertAgentHeartbeatAsync(
                DateTimeOffset.UtcNow,
                true,
                connectivity,
                (int)response.StatusCode,
                null,
                cancellationToken);

            _logger.LogInformation(
                "ERP heartbeat completed. Connectivity={Connectivity}; QueueSize={QueueSize}; OldestPendingAgeSeconds={OldestPendingAgeSeconds}; NextPollSeconds={NextPollSeconds}; PendingUpdateVersion={PendingUpdateVersion}",
                connectivity,
                storeStatus.PendingOutboxEvents,
                storeStatus.OldestPendingAgeSeconds,
                heartbeatResponse.NextPollSeconds,
                heartbeatResponse.PendingUpdate?.Version);

            return new HeartbeatSummary(true, true, heartbeatResponse.NextPollSeconds, null, heartbeatResponse.PendingUpdate);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _localStore.UpsertAgentHeartbeatAsync(
                DateTimeOffset.UtcNow,
                false,
                connectivity,
                null,
                ex.GetType().Name,
                cancellationToken);

            _logger.LogWarning(ex, "ERP heartbeat failed.");
            return new HeartbeatSummary(true, false, null, ex.GetType().Name);
        }
    }

}

public static class ErpHeartbeatHttpClient
{
    public const string Name = "ErpHeartbeat";
}

public sealed record HeartbeatSummary(
    bool Enabled,
    bool Succeeded,
    int? NextPollSeconds,
    string? LastError,
    PendingUpdateCommand? PendingUpdate = null)
{
    public static HeartbeatSummary Disabled { get; } = new(false, false, null, null);
}

public sealed record HeartbeatRequest(
    [property: JsonPropertyName("timestamp_utc")] DateTimeOffset TimestampUtc,
    [property: JsonPropertyName("agent_version")] string AgentVersion,
    [property: JsonPropertyName("queue_size")] long QueueSize,
    [property: JsonPropertyName("oldest_pending_age_seconds")] int OldestPendingAgeSeconds,
    [property: JsonPropertyName("connectivity")] string Connectivity,
    [property: JsonPropertyName("arpa_connections")] IReadOnlyList<HeartbeatArpaConnection>? ArpaConnections = null);

public sealed record HeartbeatArpaConnection(
    [property: JsonPropertyName("nome")] string Nome,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("database")] string Database,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("loja_codigo")] string LojaCodigo,
    [property: JsonPropertyName("controla_estoque")] bool ControlaEstoque,
    [property: JsonPropertyName("sync_produtos")] bool SyncProdutos,
    [property: JsonPropertyName("sync_clientes")] bool SyncClientes,
    [property: JsonPropertyName("sync_estoque")] bool SyncEstoque,
    [property: JsonPropertyName("sync_vendas")] bool SyncVendas,
    [property: JsonPropertyName("sync_financeiro")] bool SyncFinanceiro,
    [property: JsonPropertyName("sync_cobranca")] bool SyncCobranca,
    [property: JsonPropertyName("sync_plano_historico")] bool SyncPlanoHistorico,
    [property: JsonPropertyName("enabled")] bool Enabled);

public sealed record HeartbeatResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("next_poll_seconds")] int? NextPollSeconds,
    [property: JsonPropertyName("pending_update")] PendingUpdateCommand? PendingUpdate = null);

public sealed record PendingUpdateCommand(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("download_url")] string DownloadUrl,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("release_notes")] string? ReleaseNotes = null);
