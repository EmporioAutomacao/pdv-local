using System.Text.Json.Nodes;

namespace SyncAgent.Normalizers;

public sealed class ArpaFinanceiroPayloadNormalizer : IArpaPayloadNormalizer
{
    public string EntityType => "financeiro";

    public JsonObject Normalize(string entityKey, JsonObject sourcePayload)
    {
        var tituloId = JsonPayloadReader.ReadFirstString(sourcePayload, "titulo_id", "parcela_id", "documento", "numero")
            ?? entityKey;
        if (string.IsNullOrWhiteSpace(tituloId))
        {
            throw new InvalidOperationException("Financeiro payload requires a titulo id or entity_key.");
        }

        var normalized = new JsonObject
        {
            ["titulo_id"] = tituloId
        };

        JsonPayloadReader.AddIfPresent(
            normalized,
            "venda_id",
            JsonPayloadReader.ReadFirstString(sourcePayload, "venda_id", "pedido_id", "ordem_id"));
        JsonPayloadReader.AddIfPresent(
            normalized,
            "cliente_codigo",
            JsonPayloadReader.ReadFirstString(sourcePayload, "cliente_codigo", "codcliente", "codigo_cliente"));
        JsonPayloadReader.AddIfPresent(
            normalized,
            "tipo",
            JsonPayloadReader.ReadFirstString(sourcePayload, "tipo", "natureza"));
        JsonPayloadReader.AddIfPresent(
            normalized,
            "numero_parcela",
            JsonPayloadReader.ReadDecimal(sourcePayload, "numero_parcela"));
        JsonPayloadReader.AddIfPresent(
            normalized,
            "status",
            JsonPayloadReader.ReadFirstString(sourcePayload, "status", "situacao"));
        JsonPayloadReader.AddIfPresent(
            normalized,
            "vencimento_utc",
            JsonPayloadReader.ReadDateTimeOffset(sourcePayload, "vencimento_utc", "vencimento", "data_vencimento")?.ToString("O"));
        JsonPayloadReader.AddIfPresent(
            normalized,
            "pagamento_utc",
            JsonPayloadReader.ReadDateTimeOffset(sourcePayload, "pagamento_utc", "pagamento", "data_pagamento")?.ToString("O"));
        JsonPayloadReader.AddIfPresent(
            normalized,
            "valor",
            JsonPayloadReader.ReadDecimal(sourcePayload, "valor"));
        JsonPayloadReader.AddIfPresent(
            normalized,
            "valor_pago",
            JsonPayloadReader.ReadDecimal(sourcePayload, "valor_pago"));
        JsonPayloadReader.AddIfPresent(
            normalized,
            "forma_pagamento",
            JsonPayloadReader.ReadFirstString(sourcePayload, "forma_pagamento", "especie_pagamento", "especie"));

        return normalized;
    }
}
