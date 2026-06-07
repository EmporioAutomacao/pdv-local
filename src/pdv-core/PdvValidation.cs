namespace PdvLocal.Core;

public static class PdvValidation
{
    private static readonly HashSet<string> AllowedOperatorRoles = new(StringComparer.Ordinal)
    {
        "admin",
        "supervisor",
        "operator"
    };

    private static readonly HashSet<string> AllowedCashMovementTypes = new(StringComparer.Ordinal)
    {
        "supply",
        "withdrawal"
    };

    public static void ValidateOperatorRole(string role)
    {
        if (!AllowedOperatorRoles.Contains(role))
        {
            throw new ArgumentException($"Papel de operador invalido: {role}.", nameof(role));
        }
    }

    public static bool IsSupervisorRole(string role)
    {
        return role.Equals("admin", StringComparison.Ordinal)
            || role.Equals("supervisor", StringComparison.Ordinal);
    }

    public static void ValidateOperationAudit(PdvOperationAuditCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.OperationType))
        {
            throw new ArgumentException("Tipo da operacao auditada e obrigatorio.", nameof(command));
        }

        if (command.OperatorId == Guid.Empty)
        {
            throw new ArgumentException("Operador da operacao e obrigatorio.", nameof(command));
        }

        if (command.SupervisorOperatorId == Guid.Empty)
        {
            throw new ArgumentException("Supervisor autorizador e obrigatorio.", nameof(command));
        }
    }

    public static void ValidateOpenCashSession(OpenCashSessionCommand command)
    {
        if (command.OperatorId == Guid.Empty)
        {
            throw new ArgumentException("Operador e obrigatorio.", nameof(command));
        }

        if (command.OpeningAmount < 0)
        {
            throw new ArgumentException("Valor de abertura nao pode ser negativo.", nameof(command));
        }
    }

    public static void ValidateCloseCashSession(CloseCashSessionCommand command)
    {
        if (command.CashSessionId == Guid.Empty)
        {
            throw new ArgumentException("Caixa e obrigatorio.", nameof(command));
        }

        if (command.ClosingAmount < 0)
        {
            throw new ArgumentException("Valor de fechamento nao pode ser negativo.", nameof(command));
        }
    }

    public static void ValidateCashMovement(PdvCashMovementCommand command)
    {
        if (command.CashSessionId == Guid.Empty)
        {
            throw new ArgumentException("Caixa e obrigatorio.", nameof(command));
        }

        if (command.OperatorId == Guid.Empty)
        {
            throw new ArgumentException("Operador e obrigatorio.", nameof(command));
        }

        if (command.SupervisorOperatorId == Guid.Empty)
        {
            throw new ArgumentException("Supervisor autorizador e obrigatorio.", nameof(command));
        }

        if (!AllowedCashMovementTypes.Contains(command.MovementType))
        {
            throw new ArgumentException($"Tipo de movimento de caixa invalido: {command.MovementType}.", nameof(command));
        }

        if (command.Amount <= 0)
        {
            throw new ArgumentException("Valor do movimento de caixa deve ser maior que zero.", nameof(command));
        }

        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            throw new ArgumentException("Motivo do movimento de caixa e obrigatorio.", nameof(command));
        }
    }

    public static void ValidateCompletedSale(CompletedSaleCommand command)
    {
        if (command.CashSessionId == Guid.Empty)
        {
            throw new ArgumentException("Caixa e obrigatorio.", nameof(command));
        }

        if (command.OperatorId == Guid.Empty)
        {
            throw new ArgumentException("Operador e obrigatorio.", nameof(command));
        }

        if (string.IsNullOrWhiteSpace(command.SaleNumber))
        {
            throw new ArgumentException("Numero da venda e obrigatorio.", nameof(command));
        }

        if (command.DiscountAmount < 0)
        {
            throw new ArgumentException("Desconto da venda nao pode ser negativo.", nameof(command));
        }

        if (command.Items.Count == 0)
        {
            throw new ArgumentException("Venda deve possuir ao menos um item.", nameof(command));
        }

        if (command.Payments.Count == 0)
        {
            throw new ArgumentException("Venda deve possuir ao menos um pagamento.", nameof(command));
        }

        foreach (var item in command.Items)
        {
            if (item.ProductId == Guid.Empty)
            {
                throw new ArgumentException("Produto do item e obrigatorio.", nameof(command));
            }

            if (item.LineNumber < 1)
            {
                throw new ArgumentException("Numero da linha deve iniciar em 1.", nameof(command));
            }

            if (item.Quantity <= 0)
            {
                throw new ArgumentException("Quantidade deve ser maior que zero.", nameof(command));
            }

            if (item.UnitPrice < 0 || item.DiscountAmount < 0)
            {
                throw new ArgumentException("Valores do item nao podem ser negativos.", nameof(command));
            }

            if (item.DiscountAmount > item.Quantity * item.UnitPrice)
            {
                throw new ArgumentException("Desconto do item nao pode ser maior que o total bruto do item.", nameof(command));
            }
        }

        var duplicatedLine = command.Items
            .GroupBy(item => item.LineNumber)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicatedLine is not null)
        {
            throw new ArgumentException($"Linha duplicada na venda: {duplicatedLine.Key}.", nameof(command));
        }

        foreach (var payment in command.Payments)
        {
            if (string.IsNullOrWhiteSpace(payment.PaymentMethod))
            {
                throw new ArgumentException("Forma de pagamento e obrigatoria.", nameof(command));
            }

            if (payment.Amount <= 0)
            {
                throw new ArgumentException("Pagamento deve ser maior que zero.", nameof(command));
            }

            if (payment.PaymentSpeciesId == Guid.Empty)
            {
                throw new ArgumentException("Especie de pagamento invalida.", nameof(command));
            }

            if (payment.PaymentConditionId == Guid.Empty)
            {
                throw new ArgumentException("Condicao de pagamento invalida.", nameof(command));
            }

            if (payment.Installments is not null and < 1)
            {
                throw new ArgumentException("Quantidade de parcelas do pagamento deve ser maior que zero.", nameof(command));
            }

            _ = PdvTefMetadata.NormalizeMetadata(payment.TefMetadataJson);
        }

        foreach (var auditEvent in command.AuditEvents ?? [])
        {
            if (string.IsNullOrWhiteSpace(auditEvent.OperationType))
            {
                throw new ArgumentException("Tipo da auditoria da venda e obrigatorio.", nameof(command));
            }

            if (auditEvent.SupervisorOperatorId == Guid.Empty)
            {
                throw new ArgumentException("Supervisor da auditoria da venda e obrigatorio.", nameof(command));
            }
        }

        var total = CalculateSaleTotal(command);
        if (total < 0)
        {
            throw new ArgumentException("Total da venda nao pode ser negativo.", nameof(command));
        }

        var paid = command.Payments.Sum(payment => payment.Amount);
        if (paid != total)
        {
            throw new ArgumentException("Total pago deve ser igual ao total da venda.", nameof(command));
        }
    }

    public static decimal CalculateSaleTotal(CompletedSaleCommand command)
    {
        var subtotal = command.Items.Sum(item => item.Quantity * item.UnitPrice);
        var itemDiscounts = command.Items.Sum(item => item.DiscountAmount);
        return subtotal - itemDiscounts - command.DiscountAmount;
    }
}
