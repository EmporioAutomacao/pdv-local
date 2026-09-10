using Microsoft.Extensions.Options;
using SyncAgent.Collectors;
using SyncAgent.Configuration;
using SyncAgent.Dispatching;
using SyncAgent.Heartbeat;
using SyncAgent.Pdv;
using SyncAgent.Persistence;
using SyncAgent.Provisioning;
using SyncAgent.Reconciliation;
using SyncAgent.Runtime;
using SyncAgent.Update;

namespace SyncAgent;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IOptionsMonitor<SyncAgentOptions> _options;
    private readonly EffectiveSyncAgentConfigurationProvider _effectiveConfigProvider;
    private readonly LocalSyncStore _localStore;
    private readonly ArpaCollector _arpaCollector;
    private readonly ErpEventDispatcher _erpEventDispatcher;
    private readonly ErpHeartbeatClient _erpHeartbeatClient;
    private readonly ErpReconciliationClient _erpReconciliationClient;
    private readonly PdvOperatorSnapshotClient _pdvOperatorSnapshotClient;
    private readonly PdvProductSnapshotClient _pdvProductSnapshotClient;
    private readonly PdvPaymentMethodsSnapshotClient _pdvPaymentMethodsSnapshotClient;
    private readonly PdvCustomerSnapshotClient _pdvCustomerSnapshotClient;
    private readonly PdvSalesPublisher _pdvSalesPublisher;
    private readonly ErpActivationClient _erpActivationClient;
    private readonly ManualSyncSignal _manualSyncSignal;
    private readonly SyncAgentRuntimeState _runtimeState;
    private readonly ArpaSyncRunLog _arpaSyncRunLog;
    private readonly SelfUpdater _selfUpdater;
    private readonly ArpaResyncProcessor _arpaResyncProcessor;
    private readonly ErpResyncAckClient _erpResyncAckClient;

    public Worker(
        ILogger<Worker> logger,
        IOptionsMonitor<SyncAgentOptions> options,
        EffectiveSyncAgentConfigurationProvider effectiveConfigProvider,
        LocalSyncStore localStore,
        ArpaCollector arpaCollector,
        ErpEventDispatcher erpEventDispatcher,
        ErpHeartbeatClient erpHeartbeatClient,
        ErpReconciliationClient erpReconciliationClient,
        PdvOperatorSnapshotClient pdvOperatorSnapshotClient,
        PdvProductSnapshotClient pdvProductSnapshotClient,
        PdvPaymentMethodsSnapshotClient pdvPaymentMethodsSnapshotClient,
        PdvCustomerSnapshotClient pdvCustomerSnapshotClient,
        PdvSalesPublisher pdvSalesPublisher,
        ErpActivationClient erpActivationClient,
        ManualSyncSignal manualSyncSignal,
        SyncAgentRuntimeState runtimeState,
        ArpaSyncRunLog arpaSyncRunLog,
        SelfUpdater selfUpdater,
        ArpaResyncProcessor arpaResyncProcessor,
        ErpResyncAckClient erpResyncAckClient)
    {
        _logger = logger;
        _options = options;
        _effectiveConfigProvider = effectiveConfigProvider;
        _localStore = localStore;
        _arpaCollector = arpaCollector;
        _erpEventDispatcher = erpEventDispatcher;
        _erpHeartbeatClient = erpHeartbeatClient;
        _erpReconciliationClient = erpReconciliationClient;
        _pdvOperatorSnapshotClient = pdvOperatorSnapshotClient;
        _pdvProductSnapshotClient = pdvProductSnapshotClient;
        _pdvPaymentMethodsSnapshotClient = pdvPaymentMethodsSnapshotClient;
        _pdvCustomerSnapshotClient = pdvCustomerSnapshotClient;
        _pdvSalesPublisher = pdvSalesPublisher;
        _erpActivationClient = erpActivationClient;
        _manualSyncSignal = manualSyncSignal;
        _runtimeState = runtimeState;
        _arpaSyncRunLog = arpaSyncRunLog;
        _selfUpdater = selfUpdater;
        _arpaResyncProcessor = arpaResyncProcessor;
        _erpResyncAckClient = erpResyncAckClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Sync agent started.");
        var trigger = "startup";

        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _options.CurrentValue;
            var interval = TimeSpan.FromSeconds(Math.Max(5, options.PollingIntervalSeconds));

            try
            {
                await RunSyncCycleAsync(options, trigger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _runtimeState.MarkCycleFailed(DateTimeOffset.UtcNow, trigger, ex.Message);
                _arpaSyncRunLog.EndRun($"Sincronizacao interrompida: {ex.Message}");
                _logger.LogWarning(ex, "Sync cycle failed. The agent will retry on the next interval.");
            }

            trigger = await _manualSyncSignal.WaitAsync(interval, stoppingToken)
                ? "manual"
                : "scheduled";
        }
    }

    private async Task RunSyncCycleAsync(
        SyncAgentOptions options,
        string trigger,
        CancellationToken cancellationToken)
    {
        _runtimeState.MarkCycleStarted(trigger);
        var effectiveOptions = _effectiveConfigProvider.GetCurrent();
        if (effectiveOptions.IsProvisioningEnabled && !effectiveOptions.IsProvisioned)
        {
            _runtimeState.MarkNotProvisioned(DateTimeOffset.UtcNow, trigger);
            _logger.LogInformation("Sync agent is not provisioned. Waiting for local setup activation.");
            return;
        }

        var tokenRefresh = await _erpActivationClient.RefreshTokenIfNeededAsync(cancellationToken);
        if (tokenRefresh.Error is not null)
        {
            _runtimeState.MarkCycleFailed(DateTimeOffset.UtcNow, trigger, tokenRefresh.Error);
            _logger.LogWarning("Sync agent token refresh failed. Error={Error}", tokenRefresh.Error);
            return;
        }

        await _localStore.UpsertAgentIdentityAsync(
            effectiveOptions.InstanceId,
            effectiveOptions.TenantId,
            effectiveOptions.ErpApiBaseUrl,
            effectiveOptions.AgentVersion,
            cancellationToken);

        _arpaSyncRunLog.BeginRun(trigger);
        var collectorSummary = await _arpaCollector.CollectAsync(cancellationToken);
        var pdvSalesPublishSummary = await _pdvSalesPublisher.PublishAsync(cancellationToken);
        if (pdvSalesPublishSummary.Enabled && pdvSalesPublishSummary.Published > 0)
        {
            _arpaSyncRunLog.Add("info", $"Vendas do PDV enviadas: {pdvSalesPublishSummary.Published}.");
        }

        var dispatchSummary = await _erpEventDispatcher.DispatchAsync(cancellationToken);
        if (dispatchSummary.Enabled)
        {
            _arpaSyncRunLog.Add(
                "info",
                $"Envio ao ERP: {dispatchSummary.Sent} enviado(s), {dispatchSummary.Accepted} aceito(s), "
                + $"{dispatchSummary.Rejected} rejeitado(s), {dispatchSummary.Failed} com falha.");
        }

        var reconciliationWindowEnd = DateTimeOffset.UtcNow;
        var reconciliationWindowStart = reconciliationWindowEnd.AddHours(-24);
        var reconciliationSummary = await _localStore.RunLocalReconciliationAsync(
            reconciliationWindowStart,
            reconciliationWindowEnd,
            cancellationToken);
        var remoteReconciliationSummary = await _erpReconciliationClient.SendAsync(
            reconciliationSummary.ReconciliationId,
            reconciliationWindowStart,
            reconciliationWindowEnd,
            cancellationToken);
        var pdvOperatorSnapshotSummary = await _pdvOperatorSnapshotClient.ImportAsync(cancellationToken);
        var pdvProductSnapshotSummary = await _pdvProductSnapshotClient.ImportAsync(cancellationToken);
        var pdvPaymentMethodsSnapshotSummary = await _pdvPaymentMethodsSnapshotClient.ImportAsync(cancellationToken);
        var pdvCustomerSnapshotSummary = await _pdvCustomerSnapshotClient.ImportAsync(cancellationToken);

        AppendSnapshotLine("Operadores do PDV (do ERP)", pdvOperatorSnapshotSummary.Enabled, pdvOperatorSnapshotSummary.Imported, pdvOperatorSnapshotSummary.Succeeded);
        AppendSnapshotLine("Produtos do PDV (do ERP)", pdvProductSnapshotSummary.Enabled, pdvProductSnapshotSummary.Imported, pdvProductSnapshotSummary.Succeeded);
        AppendSnapshotLine("Formas de pagamento do PDV (do ERP)", pdvPaymentMethodsSnapshotSummary.Enabled, pdvPaymentMethodsSnapshotSummary.Imported, pdvPaymentMethodsSnapshotSummary.Succeeded);
        AppendSnapshotLine("Clientes do PDV (do ERP)", true, pdvCustomerSnapshotSummary.Imported, pdvCustomerSnapshotSummary.Succeeded);

        var storeStatus = await _localStore.GetStatusAsync(cancellationToken);
        var connectivity = ResolveConnectivity(dispatchSummary, storeStatus);
        var heartbeatSummary = await _erpHeartbeatClient.SendAsync(
            storeStatus,
            connectivity,
            cancellationToken);

        if (heartbeatSummary.PendingResyncs is { Count: > 0 } pendingResyncs)
        {
            var items = pendingResyncs
                .Select(r => new ResyncItem(r.Id, r.SourceSystem, r.EntityType, r.EntityKey, r.KeyField))
                .ToList();
            var resyncResults = await _arpaResyncProcessor.ProcessAsync(items, cancellationToken);
            await _erpResyncAckClient.AckAsync(resyncResults, cancellationToken);
            var done = resyncResults.Count(r => r.Status == "done");
            _arpaSyncRunLog.Add("info", $"Re-sync sob demanda: {done}/{items.Count} registro(s) re-emitido(s).");
            if (done > 0)
            {
                _manualSyncSignal.TrySignal();
            }
        }

        if (heartbeatSummary.PendingUpdate is { } pendingUpdate)
        {
            var triggered = await _selfUpdater.ApplyIfNeededAsync(pendingUpdate, cancellationToken);
            if (triggered)
            {
                _arpaSyncRunLog.EndRun("Atualizacao do aplicativo iniciada - a sincronizacao continua apos o reinicio.");
                return;
            }
        }

        _logger.LogInformation(
            "Sync cycle completed. Trigger={Trigger}; Instance={InstanceId}; Tenant={ErpTenantId}; AgentVersion={AgentVersion}; ERP API={ErpApiBaseUrl}; CollectorEnabled={CollectorEnabled}; Collected={Collected}; Inserted={Inserted}; PdvSalesPublisherEnabled={PdvSalesPublisherEnabled}; PdvSalesPending={PdvSalesPending}; PdvSalesPublished={PdvSalesPublished}; PdvSalesSkipped={PdvSalesSkipped}; DispatcherEnabled={DispatcherEnabled}; Dispatched={Dispatched}; Accepted={Accepted}; Rejected={Rejected}; DispatchFailed={DispatchFailed}; ReconciliationId={ReconciliationId}; ReconciliationStatus={ReconciliationStatus}; RemoteReconciliationEnabled={RemoteReconciliationEnabled}; RemoteReconciliationSucceeded={RemoteReconciliationSucceeded}; RemoteReconciliationMatched={RemoteReconciliationMatched}; PdvOperatorSnapshotEnabled={PdvOperatorSnapshotEnabled}; PdvOperatorsReceived={PdvOperatorsReceived}; PdvOperatorsImported={PdvOperatorsImported}; PdvOperatorSnapshotSucceeded={PdvOperatorSnapshotSucceeded}; PdvProductSnapshotEnabled={PdvProductSnapshotEnabled}; PdvProductsReceived={PdvProductsReceived}; PdvProductsImported={PdvProductsImported}; PdvProductSnapshotSucceeded={PdvProductSnapshotSucceeded}; PdvPaymentMethodsSnapshotEnabled={PdvPaymentMethodsSnapshotEnabled}; PdvPaymentMethodsReceived={PdvPaymentMethodsReceived}; PdvPaymentMethodsImported={PdvPaymentMethodsImported}; PdvPaymentMethodsSnapshotSucceeded={PdvPaymentMethodsSnapshotSucceeded}; PdvCustomersReceived={PdvCustomersReceived}; PdvCustomersImported={PdvCustomersImported}; PdvCustomerSnapshotSucceeded={PdvCustomerSnapshotSucceeded}; HeartbeatEnabled={HeartbeatEnabled}; HeartbeatSucceeded={HeartbeatSucceeded}; Connectivity={Connectivity}; Database={DatabaseName}; pgvector={PgVectorVersion}; Pending={PendingOutboxEvents}; DeadLetter={DeadLetterEvents}; OldestPendingAgeSeconds={OldestPendingAgeSeconds}",
            trigger,
            effectiveOptions.InstanceId,
            effectiveOptions.TenantId,
            effectiveOptions.AgentVersion,
            effectiveOptions.ErpApiBaseUrl,
            collectorSummary.Enabled,
            collectorSummary.Collected,
            collectorSummary.Inserted,
            pdvSalesPublishSummary.Enabled,
            pdvSalesPublishSummary.Pending,
            pdvSalesPublishSummary.Published,
            pdvSalesPublishSummary.Skipped,
            dispatchSummary.Enabled,
            dispatchSummary.Sent,
            dispatchSummary.Accepted,
            dispatchSummary.Rejected,
            dispatchSummary.Failed,
            reconciliationSummary.ReconciliationId,
            reconciliationSummary.Status,
            remoteReconciliationSummary.Enabled,
            remoteReconciliationSummary.Succeeded,
            remoteReconciliationSummary.Matched,
            pdvOperatorSnapshotSummary.Enabled,
            pdvOperatorSnapshotSummary.Received,
            pdvOperatorSnapshotSummary.Imported,
            pdvOperatorSnapshotSummary.Succeeded,
            pdvProductSnapshotSummary.Enabled,
            pdvProductSnapshotSummary.Received,
            pdvProductSnapshotSummary.Imported,
            pdvProductSnapshotSummary.Succeeded,
            pdvPaymentMethodsSnapshotSummary.Enabled,
            pdvPaymentMethodsSnapshotSummary.Received,
            pdvPaymentMethodsSnapshotSummary.Imported,
            pdvPaymentMethodsSnapshotSummary.Succeeded,
            pdvCustomerSnapshotSummary.Received,
            pdvCustomerSnapshotSummary.Imported,
            pdvCustomerSnapshotSummary.Succeeded,
            heartbeatSummary.Enabled,
            heartbeatSummary.Succeeded,
            connectivity,
            storeStatus.DatabaseName,
            storeStatus.PgVectorVersion,
            storeStatus.PendingOutboxEvents,
            storeStatus.DeadLetterEvents,
            storeStatus.OldestPendingAgeSeconds);

        var lastError = dispatchSummary.LastError
            ?? remoteReconciliationSummary.LastError
            ?? pdvOperatorSnapshotSummary.LastError
            ?? pdvProductSnapshotSummary.LastError
            ?? pdvPaymentMethodsSnapshotSummary.LastError
            ?? pdvCustomerSnapshotSummary.LastError
            ?? pdvSalesPublishSummary.LastError
            ?? heartbeatSummary.LastError;
        if (lastError is null)
        {
            _runtimeState.MarkCycleSucceeded(DateTimeOffset.UtcNow, trigger);
            _arpaSyncRunLog.EndRun(
                $"Sincronizacao concluida. {storeStatus.PendingOutboxEvents} evento(s) ainda pendente(s) de confirmacao do ERP.");
        }
        else
        {
            _runtimeState.MarkCycleFailed(DateTimeOffset.UtcNow, trigger, lastError);
            _arpaSyncRunLog.EndRun($"Sincronizacao terminou com erro: {lastError}");
        }
    }

    private void AppendSnapshotLine(string label, bool enabled, int imported, bool succeeded)
    {
        if (!enabled)
        {
            return;
        }

        _arpaSyncRunLog.Add(
            succeeded ? "info" : "warn",
            succeeded
                ? $"{label}: {imported} atualizado(s)."
                : $"{label}: falhou.");
    }

    private static string ResolveConnectivity(
        DispatchSummary dispatchSummary,
        LocalSyncStoreStatus storeStatus)
    {
        if (dispatchSummary.LastError is not null || storeStatus.DeadLetterEvents > 0)
        {
            return "degraded";
        }

        return "online";
    }
}
