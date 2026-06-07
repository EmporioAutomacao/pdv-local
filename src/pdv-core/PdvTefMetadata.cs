using System.Text.Json;

namespace PdvLocal.Core;

public static class PdvTefMetadata
{
    private static readonly string[] SensitiveKeyFragments =
    [
        "pan",
        "card_number",
        "numero_cartao",
        "cvv",
        "track",
        "trilha",
        "senha",
        "password",
        "holder_name",
        "nome_portador",
        "validade",
        "expiration"
    ];

    public static string? NormalizeMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(metadataJson);
        EnsureNoSensitiveKeys(document.RootElement);
        return JsonSerializer.Serialize(document.RootElement);
    }

    private static void EnsureNoSensitiveKeys(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (IsSensitiveKey(property.Name))
                    {
                        throw new ArgumentException($"Metadado TEF sensivel nao permitido: {property.Name}.");
                    }

                    EnsureNoSensitiveKeys(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    EnsureNoSensitiveKeys(item);
                }

                break;
        }
    }

    private static bool IsSensitiveKey(string key)
    {
        return SensitiveKeyFragments.Any(fragment =>
            key.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }
}
