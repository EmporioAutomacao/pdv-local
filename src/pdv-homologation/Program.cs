using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using PdvLocal.Core;

var connectionString = ReadArgument(args, "--connection-string")
    ?? Environment.GetEnvironmentVariable("PDV_LOCAL_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Informe --connection-string ou PDV_LOCAL_CONNECTION_STRING.");
    return 2;
}

var saleRepository = new PdvSaleRepository(connectionString);
var cashSessionRepository = new PdvCashSessionRepository(connectionString);
var paymentCatalogRepository = new PdvPaymentCatalogRepository(connectionString);
await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync();

var operatorRecord = await ReadFirstOperatorAsync(connection);
var products = await ReadProductsAsync(connection, limit: 2);
if (products.Count < 2)
{
    throw new InvalidOperationException("Catalogo local precisa de ao menos 2 produtos ativos com preco para homologacao.");
}

var species = await paymentCatalogRepository.GetActiveSpeciesAsync(CancellationToken.None);
var conditions = await paymentCatalogRepository.GetActiveConditionsAsync(CancellationToken.None);
var cashSpecies = species.FirstOrDefault(item => item.Kind == "cash" || item.AllowsChange)
    ?? throw new InvalidOperationException("Especie de dinheiro ativa nao encontrada.");
var electronicSpecies = species.FirstOrDefault(item => item.RequiresTef && item.PaymentSpeciesId != cashSpecies.PaymentSpeciesId)
    ?? species.FirstOrDefault(item => item.Kind == "card" && item.PaymentSpeciesId != cashSpecies.PaymentSpeciesId)
    ?? species.FirstOrDefault(item => item.Kind is "pix" or "card" && item.PaymentSpeciesId != cashSpecies.PaymentSpeciesId)
    ?? species.FirstOrDefault(item => item.PaymentSpeciesId != cashSpecies.PaymentSpeciesId)
    ?? throw new InvalidOperationException("Segunda especie de pagamento ativa nao encontrada.");
var condition = conditions.FirstOrDefault(item => item.Installments == 1 && item.FirstDueDays == 0)
    ?? conditions.FirstOrDefault()
    ?? throw new InvalidOperationException("Condicao de pagamento ativa nao encontrada.");

var cashSession = await cashSessionRepository.FindOpenByOperatorAsync(operatorRecord.OperatorId, CancellationToken.None);
if (cashSession is null)
{
    var cashSessionId = await cashSessionRepository.OpenAsync(
        new OpenCashSessionCommand(operatorRecord.OperatorId, 0, "Aberto por homologacao tecnica automatizada"),
        CancellationToken.None);
    cashSession = await cashSessionRepository.FindOpenByOperatorAsync(operatorRecord.OperatorId, CancellationToken.None)
        ?? throw new InvalidOperationException($"Caixa aberto {cashSessionId} nao foi encontrado apos abertura.");
}

var saleNumber = $"PDV-HML-TEF-{DateTimeOffset.Now:yyyyMMddHHmmss}";
var items = products
    .Select((product, index) => new CompletedSaleItemCommand(
        product.ProductId,
        index + 1,
        1,
        Math.Round(product.Price, 2),
        0))
    .ToArray();
var total = items.Sum(item => item.Quantity * item.UnitPrice);
var firstPayment = Math.Round(total / 2, 2);
var secondPayment = total - firstPayment;
var saleId = await saleRepository.CreateCompletedSaleAsync(
    new CompletedSaleCommand(
        CashSessionId: cashSession.CashSessionId,
        OperatorId: operatorRecord.OperatorId,
        CustomerId: null,
        SaleNumber: saleNumber,
        Items: items,
        Payments:
        [
            BuildPayment(cashSpecies, condition, firstPayment),
            BuildPayment(electronicSpecies, condition, secondPayment)
        ],
        DiscountAmount: 0),
    CancellationToken.None);

var result = new
{
    sale_id = saleId,
    sale_number = saleNumber,
    cash_session_id = cashSession.CashSessionId,
    operator_login = operatorRecord.Login,
    total_amount = total,
    products = products.Select(product => new
    {
        product_id = product.ProductId,
        product.ExternalKey,
        product.Name,
        price = Math.Round(product.Price, 2)
    }),
    payments = new[]
    {
        new { species = cashSpecies.Name, cashSpecies.Kind, amount = firstPayment, condition = condition.Name },
        new { species = electronicSpecies.Name, electronicSpecies.Kind, amount = secondPayment, condition = condition.Name }
    }
};

Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
}));
return 0;

static CompletedSalePaymentCommand BuildPayment(
    PdvPaymentSpecies species,
    PdvPaymentCondition condition,
    decimal amount)
{
    return new CompletedSalePaymentCommand(
        PaymentMethod: species.Name,
        Amount: amount,
        AuthorizationCode: species.RequiresTef ? $"TEF-HML-{DateTimeOffset.Now:yyyyMMddHHmmss}" : null,
        PaymentSpeciesId: species.PaymentSpeciesId,
        PaymentSpeciesExternalKey: species.ExternalKey,
        PaymentSpeciesKind: species.Kind,
        PaymentConditionId: condition.PaymentConditionId,
        PaymentConditionExternalKey: condition.ExternalKey,
        Installments: condition.Installments,
        RequiresTef: species.RequiresTef,
        AllowsChange: species.AllowsChange,
        TefMetadataJson: BuildTefMetadata(species, condition));
}

static string? BuildTefMetadata(
    PdvPaymentSpecies species,
    PdvPaymentCondition condition)
{
    if (!species.RequiresTef && species.Kind != "card")
    {
        return null;
    }

    var timestamp = DateTimeOffset.UtcNow;
    var nsu = timestamp.ToUnixTimeMilliseconds().ToString()[^6..];
    return JsonSerializer.Serialize(new
    {
        provider = "homologation",
        mode = "simulated",
        transaction_id = $"TEF-HML-{timestamp:yyyyMMddHHmmssfff}",
        nsu,
        authorization_code = $"HML{nsu}",
        acquirer = "homologation",
        card_brand = species.Name.Contains("debito", StringComparison.OrdinalIgnoreCase) ? "debito" : "credito",
        installments = condition.Installments,
        receipt_reference = $"TEF-HML-{timestamp:yyyyMMddHHmmssfff}",
        simulated = true
    });
}

static async Task<PdvOperator> ReadFirstOperatorAsync(NpgsqlConnection connection)
{
    const string sql = """
        SELECT operator_id, COALESCE(external_operator_id, ''), login, display_name, role, active
        FROM pdv.operators
        WHERE active = true
        ORDER BY
            CASE WHEN login = 'admin' THEN 0 ELSE 1 END,
            login
        LIMIT 1
        """;

    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        throw new InvalidOperationException("Operador ativo nao encontrado no PDV local.");
    }

    return new PdvOperator(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetBoolean(5));
}

static async Task<IReadOnlyList<PdvProduct>> ReadProductsAsync(NpgsqlConnection connection, int limit)
{
    const string sql = """
        SELECT product_id, source_system, external_key, sku, barcode, name, unit, price, active
        FROM pdv.products
        WHERE active = true
          AND price > 0
        ORDER BY name
        LIMIT @limit
        """;

    var products = new List<PdvProduct>();
    await using var command = new NpgsqlCommand(sql, connection);
    command.Parameters.AddWithValue("limit", limit);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        products.Add(new PdvProduct(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetDecimal(7),
            reader.GetBoolean(8)));
    }

    return products;
}

static string? ReadArgument(string[] args, string name)
{
    for (var index = 0; index < args.Length - 1; index++)
    {
        if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return args[index + 1];
        }
    }

    return null;
}
