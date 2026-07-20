using Npgsql;

namespace PdvLocal.Core;

public sealed class PdvProductRepository
{
    private const string ManualSourceSystem = "pdv_manual";
    private readonly string _connectionString;

    public PdvProductRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            ALTER TABLE pdv.products ADD COLUMN IF NOT EXISTS factory_code text NULL;
            UPDATE pdv.products
            SET factory_code = NULLIF(trim(payload ->> 'codigo_fabrica'), '')
            WHERE factory_code IS NULL
              AND payload ? 'codigo_fabrica';
            CREATE INDEX IF NOT EXISTS ix_products_factory_code
                ON pdv.products (factory_code)
                WHERE factory_code IS NOT NULL;
            CREATE TABLE IF NOT EXISTS pdv.product_units (
                product_unit_id uuid PRIMARY KEY,
                product_id uuid NOT NULL REFERENCES pdv.products (product_id) ON DELETE CASCADE,
                external_key text NOT NULL,
                label text NOT NULL,
                name text NULL,
                factor numeric(14, 6) NOT NULL CHECK (factor > 0),
                price numeric(14, 4) NULL CHECK (price IS NULL OR price >= 0),
                fractional boolean NOT NULL DEFAULT false,
                is_native boolean NOT NULL DEFAULT false,
                active boolean NOT NULL DEFAULT true,
                updated_at_utc timestamptz NOT NULL DEFAULT now(),
                UNIQUE (product_id, external_key)
            );
            CREATE INDEX IF NOT EXISTS ix_product_units_product
                ON pdv.product_units (product_id);
            """;
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Guid> UpsertManualProductAsync(
        UpsertManualProductCommand command,
        CancellationToken cancellationToken)
    {
        ValidateManualProduct(command);

        const string sql = """
            INSERT INTO pdv.products (
                product_id,
                source_system,
                external_key,
                sku,
                name,
                price,
                active,
                updated_at_utc
            )
            VALUES (
                @product_id,
                @source_system,
                @external_key,
                @sku,
                @name,
                @price,
                true,
                now()
            )
            ON CONFLICT (source_system, external_key) DO UPDATE
            SET
                sku = EXCLUDED.sku,
                name = EXCLUDED.name,
                price = EXCLUDED.price,
                active = true,
                updated_at_utc = now()
            RETURNING product_id
            """;

        var productId = CreateDeterministicGuid(ManualSourceSystem, command.ExternalKey);
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var dbCommand = new NpgsqlCommand(sql, connection);
        dbCommand.Parameters.AddWithValue("product_id", productId);
        dbCommand.Parameters.AddWithValue("source_system", ManualSourceSystem);
        dbCommand.Parameters.AddWithValue("external_key", command.ExternalKey.Trim());
        dbCommand.Parameters.AddWithValue("sku", command.ExternalKey.Trim());
        dbCommand.Parameters.AddWithValue("name", command.Name.Trim());
        dbCommand.Parameters.AddWithValue("price", command.Price);

        return (Guid)(await dbCommand.ExecuteScalarAsync(cancellationToken) ?? productId);
    }

    public async Task<IReadOnlyList<PdvProduct>> SearchForSaleAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limite deve ser maior que zero.");
        }

        const string sql = """
            SELECT
                product_id,
                source_system,
                external_key,
                sku,
                barcode,
                name,
                unit,
                price,
                active,
                factory_code
            FROM pdv.products
            WHERE active = true
              AND (
                    product_id::text = @query_lower
                 OR external_key = @query
                 OR sku = @query
                 OR barcode = @query
                 OR factory_code = @query
                 OR name ILIKE @name_pattern
              )
            ORDER BY
                CASE
                    WHEN product_id::text = @query_lower THEN 0
                    WHEN barcode = @query THEN 1
                    WHEN external_key = @query THEN 2
                    WHEN sku = @query THEN 3
                    WHEN factory_code = @query THEN 4
                    WHEN lower(name) = @query_lower THEN 5
                    ELSE 6
                END,
                name
            LIMIT @limit
            """;

        var normalizedQuery = query.Trim();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var dbCommand = new NpgsqlCommand(sql, connection);
        dbCommand.Parameters.AddWithValue("query", normalizedQuery);
        dbCommand.Parameters.AddWithValue("query_lower", normalizedQuery.ToLowerInvariant());
        dbCommand.Parameters.AddWithValue("name_pattern", $"%{EscapeLike(normalizedQuery)}%");
        dbCommand.Parameters.AddWithValue("limit", limit);

        var products = new List<PdvProduct>();
        await using var reader = await dbCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            products.Add(new PdvProduct(
                ProductId: reader.GetGuid(0),
                SourceSystem: reader.GetString(1),
                ExternalKey: reader.GetString(2),
                Sku: reader.IsDBNull(3) ? null : reader.GetString(3),
                Barcode: reader.IsDBNull(4) ? null : reader.GetString(4),
                Name: reader.GetString(5),
                Unit: reader.GetString(6),
                Price: reader.GetDecimal(7),
                Active: reader.GetBoolean(8),
                FactoryCode: reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return products;
    }

    public async Task<PdvProduct?> FindBestForSaleAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var products = await SearchForSaleAsync(query, limit: 1, cancellationToken);
        return products.Count == 0 ? null : products[0];
    }

    /// <summary>
    /// Unidades de medida ativas do produto (nativa primeiro). Lista vazia
    /// significa que o ERP nao enviou unidades (contrato antigo) — o PDV usa a
    /// unidade escalar do produto.
    /// </summary>
    public async Task<IReadOnlyList<PdvProductUnit>> GetActiveUnitsAsync(
        Guid productId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT product_unit_id, external_key, label, name, factor, price, fractional, is_native
            FROM pdv.product_units
            WHERE product_id = @product_id
              AND active
            ORDER BY is_native DESC, factor, label
            """;

        var units = new List<PdvProductUnit>();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("product_id", productId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            units.Add(new PdvProductUnit(
                ProductUnitId: reader.GetGuid(0),
                ExternalKey: reader.GetString(1),
                Label: reader.GetString(2),
                Name: reader.IsDBNull(3) ? null : reader.GetString(3),
                Factor: reader.GetDecimal(4),
                Price: reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                Fractional: reader.GetBoolean(6),
                IsNative: reader.GetBoolean(7)));
        }

        return units;
    }

    /// <summary>
    /// Preco de venda ao escolher uma unidade: o preco proprio da unidade
    /// quando cadastrado; senao o preco do produto (unidade nativa).
    /// </summary>
    public static decimal ResolveUnitPrice(PdvProduct product, PdvProductUnit unit)
    {
        return unit.Price ?? product.Price;
    }

    public static void ValidateManualProduct(UpsertManualProductCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.ExternalKey))
        {
            throw new ArgumentException("Codigo do produto e obrigatorio.", nameof(command));
        }

        if (string.IsNullOrWhiteSpace(command.Name))
        {
            throw new ArgumentException("Nome do produto e obrigatorio.", nameof(command));
        }

        if (command.Price < 0)
        {
            throw new ArgumentException("Preco do produto nao pode ser negativo.", nameof(command));
        }
    }

    private static Guid CreateDeterministicGuid(string sourceSystem, string externalKey)
    {
        var input = $"{sourceSystem}\u001f{externalKey.Trim()}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }

    private static string EscapeLike(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }
}
