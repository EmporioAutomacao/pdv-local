using System.Globalization;

namespace PdvLocal.Core;

public static class PdvValidation
{
    /// <summary>
    /// Interpreta o atalho de PDV "quantidade*codigo" (ex.: "3*1187" ou "1,5*7891234").
    /// Retorna false quando a entrada nao segue o formato — nesse caso ela deve ser
    /// tratada como busca normal de produto.
    /// </summary>
    public static bool TryParseQuantityCodeShortcut(string? input, out decimal quantity, out string productQuery)
    {
        quantity = 0;
        productQuery = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var separatorIndex = input.IndexOf('*');
        if (separatorIndex <= 0 || separatorIndex >= input.Length - 1)
        {
            return false;
        }

        var quantityText = input[..separatorIndex].Trim();
        var queryText = input[(separatorIndex + 1)..].Trim();
        if (quantityText.Length == 0 || queryText.Length == 0 || queryText.Contains('*'))
        {
            return false;
        }

        var decimalCulture = quantityText.LastIndexOf('.') > quantityText.LastIndexOf(',')
            ? CultureInfo.InvariantCulture
            : CultureInfo.GetCultureInfo("pt-BR");
        if (!decimal.TryParse(quantityText, NumberStyles.Number, decimalCulture, out quantity))
        {
            return false;
        }

        if (quantity <= 0)
        {
            return false;
        }

        productQuery = queryText;
        return true;
    }

    /// <summary>
    /// Escolhe o produto para venda direta quando a consulta casa EXATAMENTE
    /// (id, codigo de barras, sku, chave externa ou codigo de fabrica) com um
    /// unico produto do resultado — mesmo que o texto tambem case parcialmente
    /// com nomes de outros produtos. Retorna null quando nao ha match exato ou
    /// quando mais de um produto casa exatamente (ambiguidade → operador decide).
    /// </summary>
    public static PdvProduct? TryPickExactMatch(IReadOnlyList<PdvProduct> products, string? query)
    {
        if (products.Count == 0 || string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var normalized = query.Trim();
        PdvProduct? exact = null;
        foreach (var product in products)
        {
            if (!IsExactCodeMatch(product, normalized))
            {
                continue;
            }

            if (exact is not null)
            {
                return null;
            }

            exact = product;
        }

        return exact;
    }

    private static bool IsExactCodeMatch(PdvProduct product, string query)
    {
        return string.Equals(product.ProductId.ToString(), query, StringComparison.OrdinalIgnoreCase)
            || string.Equals(product.Barcode, query, StringComparison.OrdinalIgnoreCase)
            || string.Equals(product.Sku, query, StringComparison.OrdinalIgnoreCase)
            || string.Equals(product.ExternalKey, query, StringComparison.OrdinalIgnoreCase)
            || string.Equals(product.FactoryCode, query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Regra de negocio: condicao a prazo (mais de uma parcela ou primeiro
    /// vencimento futuro) exige cliente CADASTRADO na venda — nao basta o
    /// CPF/CNPJ digitado de um consumidor sem cadastro.
    /// </summary>
    public static bool RequiresRegisteredCustomer(int installments, int firstDueDays)
    {
        return installments > 1 || firstDueDays > 0;
    }

    /// <summary>
    /// Classifica o texto digitado no campo de cliente do pagamento: 11/14
    /// digitos (com ou sem pontuacao de CPF/CNPJ) = documento; 1 a 10 digitos
    /// puros = codigo interno do ERP; qualquer outra coisa = invalido.
    /// </summary>
    public static CustomerLookupInputKind ClassifyCustomerLookupInput(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return CustomerLookupInputKind.Invalid;
        }

        var trimmed = input.Trim();
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        var punctuationOnly = trimmed.All(ch => char.IsDigit(ch) || ch is '.' or '-' or '/' or ' ');

        if (punctuationOnly && digits.Length is 11 or 14)
        {
            return CustomerLookupInputKind.Document;
        }

        if (trimmed.All(char.IsDigit) && trimmed.Length is >= 1 and <= 10)
        {
            return CustomerLookupInputKind.InternalCode;
        }

        return CustomerLookupInputKind.Invalid;
    }

    /// <summary>
    /// Normaliza um CPF/CNPJ digitado (aceita pontuacao) para somente digitos e
    /// valida os digitos verificadores. Retorna false para documentos invalidos.
    /// </summary>
    public static bool TryNormalizeCustomerDocument(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var digits = new string(input.Where(char.IsDigit).ToArray());
        var isValid = digits.Length switch
        {
            11 => IsValidCpf(digits),
            14 => IsValidCnpj(digits),
            _ => false
        };

        if (!isValid)
        {
            return false;
        }

        normalized = digits;
        return true;
    }

    private static bool IsValidCpf(string digits)
    {
        if (digits.Distinct().Count() == 1)
        {
            return false;
        }

        var firstCheck = CalculateDocumentCheckDigit(digits, 9, weightStart: 10);
        var secondCheck = CalculateDocumentCheckDigit(digits, 10, weightStart: 11);
        return digits[9] - '0' == firstCheck && digits[10] - '0' == secondCheck;
    }

    private static int CalculateDocumentCheckDigit(string digits, int length, int weightStart)
    {
        var sum = 0;
        for (var index = 0; index < length; index++)
        {
            sum += (digits[index] - '0') * (weightStart - index);
        }

        var remainder = sum % 11;
        return remainder < 2 ? 0 : 11 - remainder;
    }

    private static bool IsValidCnpj(string digits)
    {
        if (digits.Distinct().Count() == 1)
        {
            return false;
        }

        int[] firstWeights = [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
        int[] secondWeights = [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
        return digits[12] - '0' == CalculateCnpjCheckDigit(digits, firstWeights)
            && digits[13] - '0' == CalculateCnpjCheckDigit(digits, secondWeights);
    }

    private static int CalculateCnpjCheckDigit(string digits, int[] weights)
    {
        var sum = 0;
        for (var index = 0; index < weights.Length; index++)
        {
            sum += (digits[index] - '0') * weights[index];
        }

        var remainder = sum % 11;
        return remainder < 2 ? 0 : 11 - remainder;
    }

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

        if (!string.IsNullOrWhiteSpace(command.CustomerDocument)
            && !TryNormalizeCustomerDocument(command.CustomerDocument, out _))
        {
            throw new ArgumentException("CPF/CNPJ do cliente invalido.", nameof(command));
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

public enum CustomerLookupInputKind
{
    Document,
    InternalCode,
    Invalid
}
