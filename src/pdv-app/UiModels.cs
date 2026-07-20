using System.Globalization;
using System.Text.Json.Serialization;
using PdvLocal.Core;

namespace PdvLocal.App;

internal sealed record SyncAgentStatusResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("provisioned")] bool Provisioned,
    [property: JsonPropertyName("needs_reactivation")] bool NeedsReactivation,
    [property: JsonPropertyName("instance_id")] string? InstanceId,
    [property: JsonPropertyName("erp_tenant_id")] string? ErpTenantId,
    [property: JsonPropertyName("erp_api_base_url")] string? ErpApiBaseUrl,
    [property: JsonPropertyName("database")] string? Database,
    [property: JsonPropertyName("pgvector_version")] string? PgVectorVersion,
    [property: JsonPropertyName("pending_outbox_events")] long PendingOutboxEvents,
    [property: JsonPropertyName("dead_letter_events")] long DeadLetterEvents,
    [property: JsonPropertyName("last_heartbeat_succeeded")] bool? LastHeartbeatSucceeded,
    [property: JsonPropertyName("last_heartbeat_connectivity")] string? LastHeartbeatConnectivity,
    [property: JsonPropertyName("runtime_status")] string? RuntimeStatus,
    [property: JsonPropertyName("last_error")] string? LastError);

internal sealed class UiProductSearchResult
{
    public UiProductSearchResult(PdvProduct product)
    {
        Product = product;
    }

    public PdvProduct Product { get; }
    public string DisplayCode => PdvUiFormatting.FirstNonEmpty(Product.Barcode, Product.Sku, Product.ExternalKey, Product.ProductId.ToString());
    public string ExternalKey => Product.ExternalKey;
    public string BarcodeText => PdvUiFormatting.FirstNonEmpty(Product.Barcode, "-");
    public string FactoryCodeText => PdvUiFormatting.FirstNonEmpty(Product.FactoryCode, "-");
    public string Name => Product.Name;
    public string Unit => Product.Unit;
    public string PriceText => Product.Price.ToString("C", CultureInfo.GetCultureInfo("pt-BR"));
}

internal sealed class UiSaleItem
{
    public Guid ProductId { get; init; }
    public string ExternalKey { get; init; } = string.Empty;
    public string? Sku { get; init; }
    public string? Barcode { get; init; }
    public string Name { get; init; } = string.Empty;
    public int LineNumber { get; set; }
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal DiscountAmount { get; init; }
    public Guid? DiscountSupervisorOperatorId { get; init; }
    public string? DiscountSupervisorLogin { get; init; }
    public string? DiscountReason { get; init; }
    public decimal TotalAmount => Quantity * UnitPrice - DiscountAmount;
    public string DisplayCode => PdvUiFormatting.FirstNonEmpty(Barcode, Sku, ExternalKey, ProductId.ToString());
    public string QuantityText => Quantity.ToString("0.####", CultureInfo.GetCultureInfo("pt-BR"));
    public string UnitPriceText => UnitPrice.ToString("C", CultureInfo.GetCultureInfo("pt-BR"));
    public string DiscountAmountText => DiscountAmount.ToString("C", CultureInfo.GetCultureInfo("pt-BR"));
    public string TotalAmountText => TotalAmount.ToString("C", CultureInfo.GetCultureInfo("pt-BR"));
}

internal sealed record SupervisorAuthorization(
    PdvOperator Supervisor,
    string Reason);

internal sealed class UiPayment
{
    public string Method { get; init; } = string.Empty;
    public string Condition { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public decimal ReceivedAmount { get; init; }
    public decimal ChangeAmount { get; init; }
    public string? AuthorizationCode { get; init; }
    public Guid? PaymentSpeciesId { get; init; }
    public string? PaymentSpeciesExternalKey { get; init; }
    public string? PaymentSpeciesKind { get; init; }
    public Guid? PaymentConditionId { get; init; }
    public string? PaymentConditionExternalKey { get; init; }
    public int? Installments { get; init; }
    public bool RequiresTef { get; init; }
    public bool AllowsChange { get; init; }
    public string? TefMetadataJson { get; init; }

    // Preenchidos apenas em pagamentos a prazo (condicao com parcelas ou
    // primeiro vencimento futuro); o plano pode ser editado pelo operador.
    public bool RequiresRegisteredCustomer { get; init; }
    public IReadOnlyList<PdvInstallmentPlanEntry>? InstallmentsPlan { get; set; }

    public string AmountText => Amount.ToString("C", CultureInfo.GetCultureInfo("pt-BR"));
    public string ChangeAmountText => ChangeAmount.ToString("C", CultureInfo.GetCultureInfo("pt-BR"));
}

internal sealed class UiInstallmentRow
{
    public int Number { get; init; }
    public string DueDateText { get; set; } = string.Empty;
    public decimal Amount { get; init; }
    public string AmountText => Amount.ToString("C", CultureInfo.GetCultureInfo("pt-BR"));
}

internal sealed record UiPaymentSpecies(
    Guid? PaymentSpeciesId,
    string ExternalKey,
    string Name,
    string Kind,
    bool RequiresTef,
    bool AllowsChange)
{
    public string DisplayName => RequiresTef
        ? $"{Name} (TEF)"
        : Name;

    public static UiPaymentSpecies FromModel(PdvPaymentSpecies species)
    {
        return new UiPaymentSpecies(
            species.PaymentSpeciesId,
            species.ExternalKey,
            species.Name,
            species.Kind,
            species.RequiresTef,
            species.AllowsChange);
    }

    public static UiPaymentSpecies FromTypedName(string name)
    {
        var kind = ResolveKind(name);
        return new UiPaymentSpecies(
            null,
            $"typed-{name}",
            name,
            kind,
            kind == "card",
            kind == "cash");
    }

    private static string ResolveKind(string name)
    {
        if (name.Contains("dinheiro", StringComparison.OrdinalIgnoreCase))
        {
            return "cash";
        }

        if (name.Contains("pix", StringComparison.OrdinalIgnoreCase))
        {
            return "pix";
        }

        if (name.Contains("tef", StringComparison.OrdinalIgnoreCase)
            || name.Contains("cartao", StringComparison.OrdinalIgnoreCase)
            || name.Contains("cartão", StringComparison.OrdinalIgnoreCase))
        {
            return "card";
        }

        return "other";
    }
}

internal sealed record SaleReceiptData(
    Guid SaleId,
    string SaleNumber,
    DateTimeOffset CompletedAt,
    string OperatorName,
    IReadOnlyList<UiSaleItem> Items,
    decimal SubtotalAmount,
    decimal ItemDiscountAmount,
    decimal SaleDiscountAmount,
    decimal TotalAmount,
    IReadOnlyList<UiPayment> Payments,
    decimal TotalChangeAmount);

internal sealed record UiPaymentCondition(
    Guid? PaymentConditionId,
    string ExternalKey,
    string Name,
    int Installments,
    int FirstDueDays,
    int IntervalDays)
{
    public string DisplayName => Installments <= 1
        ? Name
        : $"{Name} ({Installments}x)";

    public static UiPaymentCondition FromModel(PdvPaymentCondition condition)
    {
        return new UiPaymentCondition(
            condition.PaymentConditionId,
            condition.ExternalKey,
            condition.Name,
            condition.Installments,
            condition.FirstDueDays,
            condition.IntervalDays);
    }
}
