using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdvLocal.App;

internal interface ITefPaymentProvider
{
    Task<TefAuthorizationResult> AuthorizeAsync(
        TefAuthorizationRequest request,
        CancellationToken cancellationToken);
}

internal sealed class ConfiguredTefPaymentProvider : ITefPaymentProvider
{
    private readonly TefAppConfiguration _configuration;

    public ConfiguredTefPaymentProvider(TefAppConfiguration configuration)
    {
        _configuration = configuration;
    }

    public Task<TefAuthorizationResult> AuthorizeAsync(
        TefAuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var mode = NormalizeMode(_configuration.Mode);
        return mode switch
        {
            "disabled" => throw new InvalidOperationException("TEF esta desabilitado nesta instalacao."),
            "simulated" => Task.FromResult(BuildSimulatedAuthorization(request)),
            "provider" => throw new InvalidOperationException(
                $"Provider TEF real '{_configuration.Provider}' ainda nao possui adaptador instalado no PDV Local."),
            _ => throw new InvalidOperationException($"Modo TEF invalido: {_configuration.Mode}.")
        };
    }

    private TefAuthorizationResult BuildSimulatedAuthorization(TefAuthorizationRequest request)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var transactionId = $"TEF-SIM-{timestamp:yyyyMMddHHmmssfff}";
        var nsu = timestamp.ToUnixTimeMilliseconds().ToString()[^6..];
        var authorizationCode = $"SIM{nsu}";
        var provider = string.IsNullOrWhiteSpace(_configuration.Provider)
            ? "simulated"
            : _configuration.Provider.Trim();

        var metadata = new TefMetadata(
            Provider: provider,
            Mode: "simulated",
            TransactionId: transactionId,
            Nsu: nsu,
            AuthorizationCode: authorizationCode,
            Acquirer: "simulated",
            CardBrand: ResolveCardBrand(request.PaymentMethod),
            Installments: request.Installments,
            ReceiptReference: transactionId,
            Simulated: true);

        return new TefAuthorizationResult(
            Status: "approved",
            AuthorizationCode: authorizationCode,
            MetadataJson: JsonSerializer.Serialize(metadata, TefJsonOptions.Options));
    }

    private static string NormalizeMode(string mode)
    {
        return string.IsNullOrWhiteSpace(mode)
            ? "simulated"
            : mode.Trim().ToLowerInvariant();
    }

    private static string ResolveCardBrand(string paymentMethod)
    {
        return paymentMethod.Contains("debito", StringComparison.OrdinalIgnoreCase)
            || paymentMethod.Contains("débito", StringComparison.OrdinalIgnoreCase)
            ? "debito"
            : "credito";
    }
}

internal sealed record TefAuthorizationRequest(
    string PaymentMethod,
    decimal Amount,
    int Installments,
    string PaymentCondition,
    string? PaymentSpeciesExternalKey,
    string? PaymentConditionExternalKey);

internal sealed record TefAuthorizationResult(
    string Status,
    string AuthorizationCode,
    string MetadataJson);

internal sealed record TefMetadata(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("transaction_id")] string TransactionId,
    [property: JsonPropertyName("nsu")] string Nsu,
    [property: JsonPropertyName("authorization_code")] string AuthorizationCode,
    [property: JsonPropertyName("acquirer")] string Acquirer,
    [property: JsonPropertyName("card_brand")] string CardBrand,
    [property: JsonPropertyName("installments")] int Installments,
    [property: JsonPropertyName("receipt_reference")] string ReceiptReference,
    [property: JsonPropertyName("simulated")] bool Simulated);

internal static class TefJsonOptions
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
