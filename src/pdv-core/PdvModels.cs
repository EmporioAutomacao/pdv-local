namespace PdvLocal.Core;

public sealed record PdvOperator(
    Guid OperatorId,
    string ExternalOperatorId,
    string Login,
    string DisplayName,
    string Role,
    bool Active);

public sealed record PdvCashSession(
    Guid CashSessionId,
    Guid OperatorId,
    string Status,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    decimal OpeningAmount,
    decimal? ClosingAmount,
    string? Notes);

public sealed record PdvProduct(
    Guid ProductId,
    string SourceSystem,
    string ExternalKey,
    string? Sku,
    string? Barcode,
    string Name,
    string Unit,
    decimal Price,
    bool Active,
    string? FactoryCode = null);

public sealed record PdvPaymentSpecies(
    Guid PaymentSpeciesId,
    string SourceSystem,
    string ExternalKey,
    string Name,
    string Kind,
    bool RequiresTef,
    bool AllowsChange,
    bool Active);

public sealed record PdvPaymentCondition(
    Guid PaymentConditionId,
    string SourceSystem,
    string ExternalKey,
    string Name,
    int Installments,
    int FirstDueDays,
    int IntervalDays,
    bool Active);

public sealed record UpsertManualProductCommand(
    string ExternalKey,
    string Name,
    decimal Price);

public sealed record OpenCashSessionCommand(
    Guid OperatorId,
    decimal OpeningAmount,
    string? Notes);

public sealed record CloseCashSessionCommand(
    Guid CashSessionId,
    decimal ClosingAmount,
    string? Notes,
    IReadOnlyList<PdvClosingCountEntry>? ClosingCounts = null);

// Contagem cega de fechamento: o operador informa o contado por especie sem
// ver o esperado; esperado e diferenca sao resolvidos no fechamento.
public sealed record PdvClosingCount(
    string SpeciesName,
    string Kind,
    decimal CountedAmount);

public sealed record PdvClosingCountEntry(
    string SpeciesName,
    string Kind,
    decimal CountedAmount,
    decimal ExpectedAmount)
{
    public decimal Difference => CountedAmount - ExpectedAmount;
}

public sealed record PdvCashMovement(
    Guid MovementId,
    Guid CashSessionId,
    Guid OperatorId,
    Guid SupervisorOperatorId,
    string MovementType,
    decimal Amount,
    string Reason,
    DateTimeOffset OccurredAtUtc);

public sealed record PdvCashMovementCommand(
    Guid CashSessionId,
    Guid OperatorId,
    Guid SupervisorOperatorId,
    string MovementType,
    decimal Amount,
    string Reason,
    string? Observation = null);

public sealed record PdvCashMovementRecord(
    Guid MovementId,
    string MovementType,
    decimal Amount,
    string Reason,
    DateTimeOffset OccurredAtUtc);

public sealed record PdvPaymentSpeciesSummary(
    string SpeciesName,
    string Kind,
    decimal Amount);

public sealed record PdvCashSessionSummary(
    Guid CashSessionId,
    decimal OpeningAmount,
    decimal CashSalesAmount,
    decimal SupplyAmount,
    decimal WithdrawalAmount,
    decimal ExpectedCashAmount,
    decimal TotalSalesAmount,
    long SaleCount,
    IReadOnlyList<PdvPaymentSpeciesSummary> PaymentSpecies);

public sealed record CompletedSaleCommand(
    Guid CashSessionId,
    Guid OperatorId,
    Guid? CustomerId,
    string SaleNumber,
    IReadOnlyList<CompletedSaleItemCommand> Items,
    IReadOnlyList<CompletedSalePaymentCommand> Payments,
    decimal DiscountAmount = 0,
    IReadOnlyList<CompletedSaleAuditCommand>? AuditEvents = null,
    string? CustomerDocument = null);

public sealed record PdvCustomer(
    Guid CustomerId,
    string Name,
    string? Document,
    string? ExternalKey = null);

public sealed record CompletedSaleItemCommand(
    Guid ProductId,
    int LineNumber,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount = 0,
    string? UnitLabel = null,
    string? UnitExternalKey = null,
    decimal? UnitFactor = null);

public sealed record PdvProductUnit(
    Guid ProductUnitId,
    string ExternalKey,
    string Label,
    string? Name,
    decimal Factor,
    decimal? Price,
    bool Fractional,
    bool IsNative);

public sealed record CompletedSalePaymentCommand(
    string PaymentMethod,
    decimal Amount,
    string? AuthorizationCode = null,
    Guid? PaymentSpeciesId = null,
    string? PaymentSpeciesExternalKey = null,
    string? PaymentSpeciesKind = null,
    Guid? PaymentConditionId = null,
    string? PaymentConditionExternalKey = null,
    int? Installments = null,
    bool RequiresTef = false,
    bool AllowsChange = false,
    string? TefMetadataJson = null,
    IReadOnlyList<PdvInstallmentPlanEntry>? InstallmentsPlan = null);

public sealed record CompletedSaleAuditCommand(
    string OperationType,
    Guid SupervisorOperatorId,
    string? Reason,
    string? PayloadJson = null);

public sealed record PdvDraftItemCommand(
    Guid ProductId,
    int LineNumber,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    string? UnitLabel = null,
    string? UnitExternalKey = null,
    decimal? UnitFactor = null);

public sealed record PdvDraftItem(
    Guid ProductId,
    string ExternalKey,
    string? Sku,
    string? Barcode,
    string Name,
    string Unit,
    int LineNumber,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    string? UnitLabel = null,
    string? UnitExternalKey = null,
    decimal? UnitFactor = null);

public sealed record PdvDraftSale(
    Guid SaleId,
    decimal SaleDiscount,
    IReadOnlyList<PdvDraftItem> Items);

public sealed record PdvSaleSummary(
    Guid SaleId,
    string SaleNumber,
    DateTimeOffset CompletedAtUtc,
    int ItemCount,
    decimal TotalAmount,
    string PaymentMethods,
    string SyncStatus,
    string Status);

public sealed record PdvSaleDetail(
    Guid SaleId,
    string SaleNumber,
    string Status,
    string SyncStatus,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? CancelledAtUtc,
    string OperatorName,
    string? CustomerName,
    string? CustomerDocument,
    decimal SubtotalAmount,
    decimal DiscountAmount,
    decimal TotalAmount,
    IReadOnlyList<PdvSaleDetailItem> Items,
    IReadOnlyList<PdvSaleDetailPayment> Payments);

public sealed record PdvSaleDetailItem(
    Guid ProductId,
    string ExternalKey,
    string? Sku,
    string? Barcode,
    string Name,
    string Unit,
    int LineNumber,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal TotalAmount,
    string? UnitLabel);

public sealed record PdvSaleDetailPayment(
    string PaymentMethod,
    decimal Amount,
    string Status,
    string? AuthorizationCode,
    int? Installments,
    string? InstallmentsPlanJson);

public sealed record PdvOperationAuditCommand(
    string OperationType,
    Guid? CashSessionId,
    Guid? SaleId,
    Guid OperatorId,
    Guid SupervisorOperatorId,
    string? Reason,
    string? PayloadJson = null);
