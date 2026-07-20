using Npgsql;
using NpgsqlTypes;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PdvLocal.Core;

public sealed class PdvSaleRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _connectionString;

    public PdvSaleRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            ALTER TABLE pdv.sales ADD COLUMN IF NOT EXISTS customer_document text NULL;
            ALTER TABLE pdv.sale_items ADD COLUMN IF NOT EXISTS unit_label text NULL;
            ALTER TABLE pdv.sale_items ADD COLUMN IF NOT EXISTS unit_external_key text NULL;
            ALTER TABLE pdv.sale_items ADD COLUMN IF NOT EXISTS unit_factor numeric(14, 6) NULL;
            ALTER TABLE pdv.cash_sessions ADD COLUMN IF NOT EXISTS closing_counts jsonb NULL
            """;
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Guid> CreateCompletedSaleAsync(
        CompletedSaleCommand command,
        CancellationToken cancellationToken)
    {
        PdvValidation.ValidateCompletedSale(command);

        var saleId = Guid.NewGuid();
        var subtotal = command.Items.Sum(item => item.Quantity * item.UnitPrice);
        var itemDiscounts = command.Items.Sum(item => item.DiscountAmount);
        var discountAmount = itemDiscounts + command.DiscountAmount;
        var total = subtotal - discountAmount;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await EnsureOpenCashSessionAsync(connection, transaction, command, cancellationToken);
        await InsertSaleAsync(connection, transaction, saleId, command, subtotal, discountAmount, total, cancellationToken);
        await InsertItemsAsync(connection, transaction, saleId, command.Items, cancellationToken);
        await InsertPaymentsAsync(connection, transaction, saleId, command.Payments, cancellationToken);
        await InsertAuditsAsync(connection, transaction, saleId, command, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return saleId;
    }

    public async Task SaveDraftAsync(
        Guid cashSessionId,
        Guid operatorId,
        IReadOnlyList<PdvDraftItemCommand> items,
        decimal saleDiscount,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            await DeleteDraftAsync(cashSessionId, cancellationToken);
            return;
        }

        var draftId = Guid.NewGuid();
        var subtotal = items.Sum(i => i.Quantity * i.UnitPrice);
        var itemDiscounts = items.Sum(i => i.DiscountAmount);
        var total = Math.Max(0, subtotal - itemDiscounts - saleDiscount);
        var saleNumber = $"RASCUNHO-{cashSessionId.ToString("N")[..8]}";

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string deleteSql = """
            DELETE FROM pdv.sales WHERE cash_session_id = @cash_session_id AND status = 'draft'
            """;
        await using var deleteCmd = new NpgsqlCommand(deleteSql, connection, transaction);
        deleteCmd.Parameters.AddWithValue("cash_session_id", cashSessionId);
        await deleteCmd.ExecuteNonQueryAsync(cancellationToken);

        const string insertSql = """
            INSERT INTO pdv.sales (
                sale_id, cash_session_id, operator_id, sale_number,
                status, subtotal_amount, discount_amount, total_amount, sync_status)
            VALUES (
                @sale_id, @cash_session_id, @operator_id, @sale_number,
                'draft', @subtotal, @discount, @total, 'not_published')
            """;
        await using var insertCmd = new NpgsqlCommand(insertSql, connection, transaction);
        insertCmd.Parameters.AddWithValue("sale_id", draftId);
        insertCmd.Parameters.AddWithValue("cash_session_id", cashSessionId);
        insertCmd.Parameters.AddWithValue("operator_id", operatorId);
        insertCmd.Parameters.AddWithValue("sale_number", saleNumber);
        insertCmd.Parameters.AddWithValue("subtotal", subtotal);
        insertCmd.Parameters.AddWithValue("discount", saleDiscount);
        insertCmd.Parameters.AddWithValue("total", total);
        await insertCmd.ExecuteNonQueryAsync(cancellationToken);

        await InsertItemsAsync(connection, transaction, draftId,
            items.Select(i => new CompletedSaleItemCommand(
                i.ProductId, i.LineNumber, i.Quantity, i.UnitPrice, i.DiscountAmount,
                i.UnitLabel, i.UnitExternalKey, i.UnitFactor)).ToArray(),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<PdvDraftSale?> GetDraftAsync(Guid cashSessionId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.sale_id, s.discount_amount AS sale_discount,
                   si.product_id, p.external_key, p.sku, p.barcode, p.name, p.unit,
                   si.line_number, si.quantity, si.unit_price,
                   si.discount_amount AS item_discount,
                   si.unit_label, si.unit_external_key, si.unit_factor
            FROM pdv.sales s
            JOIN pdv.sale_items si ON si.sale_id = s.sale_id
            JOIN pdv.products   p  ON p.product_id = si.product_id
            WHERE s.cash_session_id = @cash_session_id AND s.status = 'draft'
            ORDER BY si.line_number
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("cash_session_id", cashSessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        Guid? saleId = null;
        decimal saleDiscount = 0;
        var items = new List<PdvDraftItem>();

        while (await reader.ReadAsync(cancellationToken))
        {
            saleId ??= reader.GetGuid(reader.GetOrdinal("sale_id"));
            saleDiscount = reader.GetDecimal(reader.GetOrdinal("sale_discount"));
            items.Add(new PdvDraftItem(
                ProductId: reader.GetGuid(reader.GetOrdinal("product_id")),
                ExternalKey: reader.GetString(reader.GetOrdinal("external_key")),
                Sku: reader.IsDBNull(reader.GetOrdinal("sku")) ? null : reader.GetString(reader.GetOrdinal("sku")),
                Barcode: reader.IsDBNull(reader.GetOrdinal("barcode")) ? null : reader.GetString(reader.GetOrdinal("barcode")),
                Name: reader.GetString(reader.GetOrdinal("name")),
                Unit: reader.GetString(reader.GetOrdinal("unit")),
                LineNumber: reader.GetInt32(reader.GetOrdinal("line_number")),
                Quantity: reader.GetDecimal(reader.GetOrdinal("quantity")),
                UnitPrice: reader.GetDecimal(reader.GetOrdinal("unit_price")),
                DiscountAmount: reader.GetDecimal(reader.GetOrdinal("item_discount")),
                UnitLabel: reader.IsDBNull(reader.GetOrdinal("unit_label")) ? null : reader.GetString(reader.GetOrdinal("unit_label")),
                UnitExternalKey: reader.IsDBNull(reader.GetOrdinal("unit_external_key")) ? null : reader.GetString(reader.GetOrdinal("unit_external_key")),
                UnitFactor: reader.IsDBNull(reader.GetOrdinal("unit_factor")) ? null : reader.GetDecimal(reader.GetOrdinal("unit_factor"))));
        }

        return saleId.HasValue ? new PdvDraftSale(saleId.Value, saleDiscount, items) : null;
    }

    public async Task DeleteDraftAsync(Guid cashSessionId, CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM pdv.sales WHERE cash_session_id = @cash_session_id AND status = 'draft'
            """;
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("cash_session_id", cashSessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> GetSyncStatusAsync(Guid saleId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT sync_status FROM pdv.sales WHERE sale_id = @sale_id";
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("sale_id", saleId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result as string;
    }

    public async Task<IReadOnlyList<PdvSaleSummary>> GetByCashSessionAsync(
        Guid cashSessionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                s.sale_id,
                s.sale_number,
                s.completed_at_utc,
                s.total_amount,
                s.sync_status,
                s.status,
                COUNT(DISTINCT si.sale_item_id)::int           AS item_count,
                COALESCE(STRING_AGG(DISTINCT p.payment_method, ', '), '-') AS payment_methods
            FROM pdv.sales s
            LEFT JOIN pdv.sale_items si ON si.sale_id = s.sale_id
            LEFT JOIN pdv.payments   p  ON p.sale_id  = s.sale_id
            WHERE s.cash_session_id = @cash_session_id
              AND s.status != 'draft'
            GROUP BY s.sale_id, s.sale_number, s.completed_at_utc, s.total_amount, s.sync_status, s.status
            ORDER BY s.completed_at_utc DESC
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("cash_session_id", cashSessionId);

        var results = new List<PdvSaleSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new PdvSaleSummary(
                SaleId: reader.GetGuid(reader.GetOrdinal("sale_id")),
                SaleNumber: reader.GetString(reader.GetOrdinal("sale_number")),
                CompletedAtUtc: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("completed_at_utc")),
                ItemCount: reader.GetInt32(reader.GetOrdinal("item_count")),
                TotalAmount: reader.GetDecimal(reader.GetOrdinal("total_amount")),
                PaymentMethods: reader.GetString(reader.GetOrdinal("payment_methods")),
                SyncStatus: reader.GetString(reader.GetOrdinal("sync_status")),
                Status: reader.GetString(reader.GetOrdinal("status"))));
        }

        return results;
    }

    public async Task CancelSaleAsync(
        Guid saleId,
        Guid cashSessionId,
        Guid operatorId,
        Guid supervisorOperatorId,
        string reason,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string updateSql = """
            UPDATE pdv.sales
            SET status          = 'cancelled',
                cancelled_at_utc = @now,
                sync_status      = 'pending_sync',
                updated_at_utc   = @now
            WHERE sale_id = @sale_id
              AND status  = 'completed'
            """;

        await using var updateCmd = new NpgsqlCommand(updateSql, connection, transaction);
        updateCmd.Parameters.AddWithValue("sale_id", saleId);
        updateCmd.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        var rows = await updateCmd.ExecuteNonQueryAsync(cancellationToken);
        if (rows == 0)
            throw new InvalidOperationException("Venda nao encontrada ou ja cancelada.");

        const string auditSql = """
            INSERT INTO pdv.operation_audits (
                audit_id, operation_type, cash_session_id, sale_id,
                operator_id, supervisor_operator_id, reason, payload, occurred_at_utc)
            VALUES (
                @audit_id, 'cancel_sale', @cash_session_id, @sale_id,
                @operator_id, @supervisor_operator_id, @reason, @payload, @occurred_at_utc)
            """;

        await using var auditCmd = new NpgsqlCommand(auditSql, connection, transaction);
        auditCmd.Parameters.AddWithValue("audit_id", Guid.NewGuid());
        auditCmd.Parameters.AddWithValue("cash_session_id", cashSessionId);
        auditCmd.Parameters.AddWithValue("sale_id", saleId);
        auditCmd.Parameters.AddWithValue("operator_id", operatorId);
        auditCmd.Parameters.AddWithValue("supervisor_operator_id", supervisorOperatorId);
        auditCmd.Parameters.AddWithValue("reason", reason.Trim());
        auditCmd.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, "{}");
        auditCmd.Parameters.AddWithValue("occurred_at_utc", DateTimeOffset.UtcNow);
        await auditCmd.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task EnsureOpenCashSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompletedSaleCommand command,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM pdv.cash_sessions
                WHERE cash_session_id = @cash_session_id
                  AND operator_id = @operator_id
                  AND status = 'open'
            )
            """;

        await using var dbCommand = new NpgsqlCommand(sql, connection, transaction);
        dbCommand.Parameters.AddWithValue("cash_session_id", command.CashSessionId);
        dbCommand.Parameters.AddWithValue("operator_id", command.OperatorId);
        var exists = (bool)(await dbCommand.ExecuteScalarAsync(cancellationToken) ?? false);
        if (!exists)
        {
            throw new InvalidOperationException("Caixa aberto nao encontrado para este operador.");
        }
    }

    private static async Task InsertSaleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid saleId,
        CompletedSaleCommand command,
        decimal subtotal,
        decimal discountAmount,
        decimal total,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO pdv.sales (
                sale_id,
                cash_session_id,
                operator_id,
                customer_id,
                customer_document,
                sale_number,
                status,
                subtotal_amount,
                discount_amount,
                total_amount,
                completed_at_utc,
                sync_status
            )
            VALUES (
                @sale_id,
                @cash_session_id,
                @operator_id,
                @customer_id,
                @customer_document,
                @sale_number,
                'completed',
                @subtotal_amount,
                @discount_amount,
                @total_amount,
                @completed_at_utc,
                'pending_sync'
            )
            """;

        await using var insertCommand = new NpgsqlCommand(sql, connection, transaction);
        insertCommand.Parameters.AddWithValue("sale_id", saleId);
        insertCommand.Parameters.AddWithValue("cash_session_id", command.CashSessionId);
        insertCommand.Parameters.AddWithValue("operator_id", command.OperatorId);
        insertCommand.Parameters.AddWithValue("customer_id", (object?)command.CustomerId ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("customer_document",
            string.IsNullOrWhiteSpace(command.CustomerDocument) ? DBNull.Value : command.CustomerDocument);
        insertCommand.Parameters.AddWithValue("sale_number", command.SaleNumber);
        insertCommand.Parameters.AddWithValue("subtotal_amount", subtotal);
        insertCommand.Parameters.AddWithValue("discount_amount", discountAmount);
        insertCommand.Parameters.AddWithValue("total_amount", total);
        insertCommand.Parameters.AddWithValue("completed_at_utc", DateTimeOffset.UtcNow);
        await insertCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid saleId,
        IReadOnlyList<CompletedSaleItemCommand> items,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO pdv.sale_items (
                sale_item_id,
                sale_id,
                product_id,
                line_number,
                quantity,
                unit_price,
                discount_amount,
                total_amount,
                unit_label,
                unit_external_key,
                unit_factor
            )
            VALUES (
                @sale_item_id,
                @sale_id,
                @product_id,
                @line_number,
                @quantity,
                @unit_price,
                @discount_amount,
                @total_amount,
                @unit_label,
                @unit_external_key,
                @unit_factor
            )
            """;

        foreach (var item in items)
        {
            await using var insertCommand = new NpgsqlCommand(sql, connection, transaction);
            insertCommand.Parameters.AddWithValue("sale_item_id", Guid.NewGuid());
            insertCommand.Parameters.AddWithValue("sale_id", saleId);
            insertCommand.Parameters.AddWithValue("product_id", item.ProductId);
            insertCommand.Parameters.AddWithValue("line_number", item.LineNumber);
            insertCommand.Parameters.AddWithValue("quantity", item.Quantity);
            insertCommand.Parameters.AddWithValue("unit_price", item.UnitPrice);
            insertCommand.Parameters.AddWithValue("discount_amount", item.DiscountAmount);
            insertCommand.Parameters.AddWithValue("total_amount", (item.Quantity * item.UnitPrice) - item.DiscountAmount);
            insertCommand.Parameters.AddWithValue("unit_label", (object?)item.UnitLabel ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("unit_external_key", (object?)item.UnitExternalKey ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("unit_factor", (object?)item.UnitFactor ?? DBNull.Value);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertPaymentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid saleId,
        IReadOnlyList<CompletedSalePaymentCommand> payments,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO pdv.payments (
                payment_id,
                sale_id,
                payment_method,
                amount,
                status,
                authorization_code,
                payload
            )
            VALUES (
                @payment_id,
                @sale_id,
                @payment_method,
                @amount,
                'captured',
                @authorization_code,
                @payload
            )
            """;

        foreach (var payment in payments)
        {
            var payloadObject = new JsonObject
            {
                ["payment_species_id"] = payment.PaymentSpeciesId?.ToString(),
                ["payment_species_external_key"] = payment.PaymentSpeciesExternalKey,
                ["payment_species_kind"] = payment.PaymentSpeciesKind,
                ["payment_condition_id"] = payment.PaymentConditionId?.ToString(),
                ["payment_condition_external_key"] = payment.PaymentConditionExternalKey,
                ["installments"] = payment.Installments,
                ["requires_tef"] = payment.RequiresTef,
                ["allows_change"] = payment.AllowsChange
            };

            if (PdvTefMetadata.NormalizeMetadata(payment.TefMetadataJson) is { Length: > 0 } tefMetadataJson)
            {
                payloadObject["tef_metadata"] = JsonNode.Parse(tefMetadataJson);
            }

            if (payment.InstallmentsPlan is { Count: > 0 } plan)
            {
                var planArray = new JsonArray();
                foreach (var entry in plan)
                {
                    planArray.Add(new JsonObject
                    {
                        ["number"] = entry.Number,
                        ["due_date"] = entry.DueDate.ToString("yyyy-MM-dd"),
                        ["amount"] = entry.Amount
                    });
                }

                payloadObject["installments_plan"] = planArray;
            }

            var payload = payloadObject.ToJsonString(JsonOptions);

            await using var insertCommand = new NpgsqlCommand(sql, connection, transaction);
            insertCommand.Parameters.AddWithValue("payment_id", Guid.NewGuid());
            insertCommand.Parameters.AddWithValue("sale_id", saleId);
            insertCommand.Parameters.AddWithValue("payment_method", payment.PaymentMethod);
            insertCommand.Parameters.AddWithValue("amount", payment.Amount);
            insertCommand.Parameters.AddWithValue("authorization_code", (object?)payment.AuthorizationCode ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertAuditsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid saleId,
        CompletedSaleCommand command,
        CancellationToken cancellationToken)
    {
        if (command.AuditEvents is null || command.AuditEvents.Count == 0)
        {
            return;
        }

        const string sql = """
            INSERT INTO pdv.operation_audits (
                audit_id,
                operation_type,
                cash_session_id,
                sale_id,
                operator_id,
                supervisor_operator_id,
                reason,
                payload,
                occurred_at_utc
            )
            VALUES (
                @audit_id,
                @operation_type,
                @cash_session_id,
                @sale_id,
                @operator_id,
                @supervisor_operator_id,
                @reason,
                @payload,
                @occurred_at_utc
            )
            """;

        foreach (var audit in command.AuditEvents)
        {
            await using var insertCommand = new NpgsqlCommand(sql, connection, transaction);
            insertCommand.Parameters.AddWithValue("audit_id", Guid.NewGuid());
            insertCommand.Parameters.AddWithValue("operation_type", audit.OperationType.Trim());
            insertCommand.Parameters.AddWithValue("cash_session_id", command.CashSessionId);
            insertCommand.Parameters.AddWithValue("sale_id", saleId);
            insertCommand.Parameters.AddWithValue("operator_id", command.OperatorId);
            insertCommand.Parameters.AddWithValue("supervisor_operator_id", audit.SupervisorOperatorId);
            insertCommand.Parameters.AddWithValue("reason", (object?)audit.Reason?.Trim() ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, PdvOperationAuditRepository.NormalizePayload(audit.PayloadJson));
            insertCommand.Parameters.AddWithValue("occurred_at_utc", DateTimeOffset.UtcNow);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
