using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;
using SyncAgent.Configuration;
using SyncAgent.Contracts;
using SyncAgent.Persistence;
using SyncAgent.Provisioning;

namespace SyncAgent.Pdv;

public sealed class PdvSalesPublisher
{
    private readonly ILogger<PdvSalesPublisher> _logger;
    private readonly IOptionsMonitor<PdvSalesPublisherOptions> _options;
    private readonly EffectiveSyncAgentConfigurationProvider _effectiveConfigProvider;
    private readonly LocalSyncStore _localStore;

    public PdvSalesPublisher(
        ILogger<PdvSalesPublisher> logger,
        IOptionsMonitor<PdvSalesPublisherOptions> options,
        EffectiveSyncAgentConfigurationProvider effectiveConfigProvider,
        LocalSyncStore localStore)
    {
        _logger = logger;
        _options = options;
        _effectiveConfigProvider = effectiveConfigProvider;
        _localStore = localStore;
    }

    public async Task<PdvSalesPublishSummary> PublishAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            return PdvSalesPublishSummary.Disabled;
        }

        var effectiveOptions = _effectiveConfigProvider.GetCurrent();
        var sales = await _localStore.GetPendingPdvSalesAsync(options.BatchSize, cancellationToken);
        if (sales.Count == 0)
        {
            return new PdvSalesPublishSummary(true, 0, 0, 0, null);
        }

        var published = 0;
        var skipped = 0;
        string? lastError = null;

        foreach (var sale in sales)
        {
            try
            {
                var draft = new OutboxEventDraft(
                    EventId: sale.SaleId,
                    SourceInstanceId: effectiveOptions.InstanceId,
                    SourceSystem: "pdv_local",
                    EntityType: "venda",
                    EntityKey: sale.SaleId.ToString(),
                    EventType: "upsert",
                    OccurredAtUtc: sale.OccurredAtUtc,
                    CapturedAtUtc: DateTimeOffset.UtcNow,
                    SchemaVersion: SyncContractValues.CurrentSchemaVersion,
                    Payload: BuildPayload(sale),
                    TraceId: $"pdv-sale-{sale.SaleId}");

                await _localStore.EnqueueOutboxEventAsync(draft, cancellationToken);
                await _localStore.MarkPdvSalePublishedAsync(sale.SaleId, cancellationToken);
                published++;
            }
            catch (Exception ex)
            {
                skipped++;
                lastError = ex.GetType().Name;
                _logger.LogWarning(ex, "PDV sale publish failed. SaleId={SaleId}", sale.SaleId);
            }
        }

        _logger.LogInformation(
            "PDV sales publish completed. Pending={Pending}; Published={Published}; Skipped={Skipped}",
            sales.Count,
            published,
            skipped);

        return new PdvSalesPublishSummary(true, sales.Count, published, skipped, lastError);
    }

    private static JsonObject BuildPayload(PdvSalePendingPublishRecord sale)
    {
        return new JsonObject
        {
            ["sale_id"] = sale.SaleId.ToString(),
            ["sale_number"] = sale.SaleNumber,
            ["cash_session_id"] = sale.CashSessionId.ToString(),
            ["operator_id"] = sale.OperatorId.ToString(),
            ["operator_external_id"] = sale.OperatorExternalId,
            ["customer_id"] = sale.CustomerId?.ToString(),
            ["occurred_at_utc"] = sale.OccurredAtUtc.ToString("O"),
            ["status"] = sale.Status,
            ["currency"] = "BRL",
            ["subtotal_amount"] = sale.SubtotalAmount,
            ["discount_amount"] = sale.DiscountAmount,
            ["total_amount"] = sale.TotalAmount,
            ["items"] = sale.Items,
            ["payments"] = sale.Payments
        };
    }
}

public sealed record PdvSalesPublishSummary(
    bool Enabled,
    int Pending,
    int Published,
    int Skipped,
    string? LastError)
{
    public static PdvSalesPublishSummary Disabled { get; } = new(false, 0, 0, 0, null);
}
