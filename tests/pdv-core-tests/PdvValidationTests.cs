using PdvLocal.Core;

namespace PdvLocal.Core.Tests;

public sealed class PdvValidationTests
{
    [Fact]
    public void ValidateOperatorRole_accepts_contract_roles()
    {
        PdvValidation.ValidateOperatorRole("admin");
        PdvValidation.ValidateOperatorRole("supervisor");
        PdvValidation.ValidateOperatorRole("operator");
    }

    [Fact]
    public void ValidateOperatorRole_rejects_unknown_role()
    {
        Assert.Throws<ArgumentException>(() => PdvValidation.ValidateOperatorRole("owner"));
    }

    [Fact]
    public void IsSupervisorRole_accepts_admin_and_supervisor_only()
    {
        Assert.True(PdvValidation.IsSupervisorRole("admin"));
        Assert.True(PdvValidation.IsSupervisorRole("supervisor"));
        Assert.False(PdvValidation.IsSupervisorRole("operator"));
    }

    [Fact]
    public void ValidateOperationAudit_rejects_missing_supervisor()
    {
        var command = new PdvOperationAuditCommand(
            "sale_discount_applied",
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            Guid.Empty,
            "Desconto autorizado");

        Assert.Throws<ArgumentException>(() => PdvValidation.ValidateOperationAudit(command));
    }

    [Fact]
    public void NormalizePayload_returns_empty_object_for_blank_payload()
    {
        Assert.Equal("{}", PdvOperationAuditRepository.NormalizePayload(""));
    }

    [Fact]
    public void ValidateOpenCashSession_rejects_negative_opening_amount()
    {
        var command = new OpenCashSessionCommand(Guid.NewGuid(), -1, null);

        Assert.Throws<ArgumentException>(() => PdvValidation.ValidateOpenCashSession(command));
    }

    [Fact]
    public void ValidateCloseCashSession_rejects_empty_cash_session()
    {
        var command = new CloseCashSessionCommand(Guid.Empty, 10, null);

        Assert.Throws<ArgumentException>(() => PdvValidation.ValidateCloseCashSession(command));
    }

    [Fact]
    public void ValidateCashMovement_accepts_supply_and_withdrawal()
    {
        PdvValidation.ValidateCashMovement(BuildCashMovementCommand("supply", 10, "Troco inicial"));
        PdvValidation.ValidateCashMovement(BuildCashMovementCommand("withdrawal", 10, "Retirada parcial"));
    }

    [Fact]
    public void ValidateCashMovement_rejects_missing_supervisor()
    {
        var command = BuildCashMovementCommand("supply", 10, "Troco inicial") with
        {
            SupervisorOperatorId = Guid.Empty
        };

        Assert.Throws<ArgumentException>(() => PdvValidation.ValidateCashMovement(command));
    }

    [Fact]
    public void ValidateCashMovement_rejects_non_positive_amount()
    {
        var command = BuildCashMovementCommand("withdrawal", 0, "Retirada parcial");

        Assert.Throws<ArgumentException>(() => PdvValidation.ValidateCashMovement(command));
    }

    [Fact]
    public void ValidateCashMovement_rejects_missing_reason()
    {
        var command = BuildCashMovementCommand("withdrawal", 10, "");

        Assert.Throws<ArgumentException>(() => PdvValidation.ValidateCashMovement(command));
    }

    [Fact]
    public void ValidateCashMovement_rejects_invalid_type()
    {
        var command = BuildCashMovementCommand("transfer", 10, "Movimento invalido");

        Assert.Throws<ArgumentException>(() => PdvValidation.ValidateCashMovement(command));
    }

    [Fact]
    public void NormalizeTefMetadata_accepts_non_sensitive_metadata()
    {
        var normalized = PdvTefMetadata.NormalizeMetadata("""
            {
                "provider": "simulated",
                "transaction_id": "tx-001",
                "nsu": "123456",
                "authorization_code": "AUTH-001",
                "installments": 1,
                "simulated": true
            }
            """);

        Assert.Contains("\"authorization_code\"", normalized);
        Assert.Contains("\"simulated\":true", normalized);
    }

    [Fact]
    public void NormalizeTefMetadata_rejects_sensitive_card_keys()
    {
        Assert.Throws<ArgumentException>(() => PdvTefMetadata.NormalizeMetadata("""
            {
                "provider": "simulated",
                "pan": "4111111111111111"
            }
            """));
    }

    [Fact]
    public void ValidateCompletedSale_accepts_balanced_sale()
    {
        var command = BuildSaleCommand(totalPayment: 19);

        PdvValidation.ValidateCompletedSale(command);

        Assert.Equal(19, PdvValidation.CalculateSaleTotal(command));
    }

    [Fact]
    public void ValidateCompletedSale_rejects_sale_without_items()
    {
        var command = BuildSaleCommand(totalPayment: 19) with
        {
            Items = []
        };

        Assert.Throws<ArgumentException>(() => PdvValidation.ValidateCompletedSale(command));
    }

    [Fact]
    public void ValidateCompletedSale_rejects_unbalanced_payment()
    {
        var command = BuildSaleCommand(totalPayment: 18);

        Assert.Throws<ArgumentException>(() => PdvValidation.ValidateCompletedSale(command));
    }

    [Fact]
    public void ValidateCompletedSale_rejects_invalid_quantity()
    {
        var command = BuildSaleCommand(totalPayment: 19) with
        {
            Items =
            [
                new CompletedSaleItemCommand(Guid.NewGuid(), 1, 0, 10)
            ]
        };

        Assert.Throws<ArgumentException>(() => PdvValidation.ValidateCompletedSale(command));
    }

    [Fact]
    public void ValidateCompletedSale_rejects_item_discount_greater_than_item_gross()
    {
        var command = BuildSaleCommand(totalPayment: 1) with
        {
            Items =
            [
                new CompletedSaleItemCommand(Guid.NewGuid(), 1, 1, 10, 11)
            ]
        };

        Assert.Throws<ArgumentException>(() => PdvValidation.ValidateCompletedSale(command));
    }

    [Fact]
    public void ValidateCompletedSale_rejects_duplicate_line_number()
    {
        var command = BuildSaleCommand(totalPayment: 30) with
        {
            Items =
            [
                new CompletedSaleItemCommand(Guid.NewGuid(), 1, 1, 10),
                new CompletedSaleItemCommand(Guid.NewGuid(), 1, 1, 20)
            ]
        };

        Assert.Throws<ArgumentException>(() => PdvValidation.ValidateCompletedSale(command));
    }

    [Fact]
    public void ValidateCompletedSale_rejects_total_discount_greater_than_sale_total()
    {
        var command = BuildSaleCommand(totalPayment: 1) with
        {
            DiscountAmount = 100
        };

        Assert.Throws<ArgumentException>(() => PdvValidation.ValidateCompletedSale(command));
    }

    [Fact]
    public void ValidateCompletedSale_rejects_invalid_payment_condition_installments()
    {
        var command = BuildSaleCommand(totalPayment: 19) with
        {
            Payments =
            [
                new CompletedSalePaymentCommand("dinheiro", 19, Installments: 0)
            ]
        };

        Assert.Throws<ArgumentException>(() => PdvValidation.ValidateCompletedSale(command));
    }

    [Fact]
    public void ValidateManualProduct_rejects_missing_code()
    {
        var command = new UpsertManualProductCommand("", "Produto", 10);

        Assert.Throws<ArgumentException>(() => PdvProductRepository.ValidateManualProduct(command));
    }

    [Fact]
    public void ValidateManualProduct_rejects_negative_price()
    {
        var command = new UpsertManualProductCommand("P-001", "Produto", -1);

        Assert.Throws<ArgumentException>(() => PdvProductRepository.ValidateManualProduct(command));
    }

    [Fact]
    public void ValidateCompletedSale_accepts_sale_with_valid_audit_event()
    {
        var command = BuildSaleCommand(totalPayment: 19) with
        {
            AuditEvents =
            [
                new CompletedSaleAuditCommand("sale_discount_applied", Guid.NewGuid(), "Desconto autorizado")
            ]
        };

        PdvValidation.ValidateCompletedSale(command);
    }

    [Fact]
    public void ValidateCompletedSale_rejects_audit_event_with_empty_operation_type()
    {
        var command = BuildSaleCommand(totalPayment: 19) with
        {
            AuditEvents =
            [
                new CompletedSaleAuditCommand("", Guid.NewGuid(), "Motivo")
            ]
        };

        Assert.Throws<ArgumentException>(() => PdvValidation.ValidateCompletedSale(command));
    }

    [Fact]
    public void ValidateCompletedSale_rejects_audit_event_with_empty_supervisor()
    {
        var command = BuildSaleCommand(totalPayment: 19) with
        {
            AuditEvents =
            [
                new CompletedSaleAuditCommand("sale_discount_applied", Guid.Empty, "Motivo")
            ]
        };

        Assert.Throws<ArgumentException>(() => PdvValidation.ValidateCompletedSale(command));
    }

    private static CompletedSaleCommand BuildSaleCommand(decimal totalPayment)
    {
        return new CompletedSaleCommand(
            CashSessionId: Guid.NewGuid(),
            OperatorId: Guid.NewGuid(),
            CustomerId: null,
            SaleNumber: "SALE-001",
            Items:
            [
                new CompletedSaleItemCommand(
                    ProductId: Guid.NewGuid(),
                    LineNumber: 1,
                    Quantity: 2,
                    UnitPrice: 10,
                    DiscountAmount: 1)
            ],
            Payments:
            [
                new CompletedSalePaymentCommand("dinheiro", totalPayment)
            ]);
    }

    private static PdvCashMovementCommand BuildCashMovementCommand(
        string movementType,
        decimal amount,
        string reason)
    {
        return new PdvCashMovementCommand(
            CashSessionId: Guid.NewGuid(),
            OperatorId: Guid.NewGuid(),
            SupervisorOperatorId: Guid.NewGuid(),
            MovementType: movementType,
            Amount: amount,
            Reason: reason);
    }
}
