using System.Text.Json.Nodes;

namespace SyncAgent.Normalizers;

public sealed class ArpaVendaPayloadNormalizer : IArpaPayloadNormalizer
{
    public string EntityType => "venda";

    public JsonObject Normalize(string entityKey, JsonObject sourcePayload)
    {
        var vendaId = JsonPayloadReader.ReadFirstString(sourcePayload, "pedido_id", "venda_id", "ordem_id", "numero", "codigo")
            ?? entityKey;
        if (string.IsNullOrWhiteSpace(vendaId))
        {
            throw new InvalidOperationException("Venda payload requires a venda id or entity_key.");
        }

        var normalized = new JsonObject
        {
            ["venda_id"] = vendaId
        };

        JsonPayloadReader.AddIfPresent(
            normalized,
            "cliente_codigo",
            JsonPayloadReader.ReadFirstString(sourcePayload, "cliente_codigo", "codcliente", "codigo_cliente"));
        JsonPayloadReader.AddIfPresent(
            normalized,
            "vendedor_codigo",
            JsonPayloadReader.ReadFirstString(sourcePayload, "vendedor_codigo", "codvendedor", "codigo_vendedor"));
        JsonPayloadReader.AddIfPresent(
            normalized,
            "loja_codigo",
            JsonPayloadReader.ReadFirstString(sourcePayload, "loja_codigo", "codigo_loja", "loja", "estoque_codigo"));
        JsonPayloadReader.AddIfPresent(
            normalized,
            "status",
            JsonPayloadReader.ReadFirstString(sourcePayload, "status", "situacao"));
        JsonPayloadReader.AddIfPresent(
            normalized,
            "data_venda_utc",
            JsonPayloadReader.ReadDateTimeOffset(sourcePayload, "data_venda_utc", "dataordem", "data_venda")?.ToString("O"));
        JsonPayloadReader.AddIfPresent(
            normalized,
            "subtotal",
            JsonPayloadReader.ReadDecimal(sourcePayload, "subtotal"));
        JsonPayloadReader.AddIfPresent(
            normalized,
            "desconto_total",
            JsonPayloadReader.ReadDecimal(sourcePayload, "desconto_total"));
        JsonPayloadReader.AddIfPresent(
            normalized,
            "total",
            JsonPayloadReader.ReadDecimal(sourcePayload, "total"));
        JsonPayloadReader.AddIfPresent(
            normalized,
            "itens",
            NormalizeItens(JsonPayloadReader.ReadArray(sourcePayload, "itens")));

        return normalized;
    }

    private static JsonArray? NormalizeItens(JsonArray? itens)
    {
        if (itens is null)
        {
            return null;
        }

        var normalizedItens = new JsonArray();
        foreach (var itemNode in itens)
        {
            if (itemNode is not JsonObject item)
            {
                continue;
            }

            var normalized = new JsonObject();
            JsonPayloadReader.AddIfPresent(
                normalized,
                "produto_codigo",
                JsonPayloadReader.ReadFirstString(item, "produto_codigo", "codigo_produto", "codigo", "codigo_arpa"));
            JsonPayloadReader.AddIfPresent(normalized, "descricao", JsonPayloadReader.ReadString(item, "descricao"));
            JsonPayloadReader.AddIfPresent(normalized, "quantidade", JsonPayloadReader.ReadDecimal(item, "quantidade"));
            JsonPayloadReader.AddIfPresent(normalized, "valor_unitario", JsonPayloadReader.ReadDecimal(item, "valor_unitario"));
            JsonPayloadReader.AddIfPresent(normalized, "desconto", JsonPayloadReader.ReadDecimal(item, "desconto"));
            JsonPayloadReader.AddIfPresent(normalized, "total", JsonPayloadReader.ReadDecimal(item, "total"));

            normalizedItens.Add(normalized);
        }

        return normalizedItens;
    }
}
