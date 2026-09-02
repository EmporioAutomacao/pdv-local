using System.Text.Json.Nodes;

namespace SyncAgent.Normalizers;

/// <summary>
/// A view <c>sync_export.vendas</c> ja entrega o <c>payload_json</c> no formato
/// consumido por <c>sync_api.domain_processor.apply_arpa_venda</c> (chaves
/// <c>codigo_venda_arpa</c>, <c>data</c>, <c>cliente_documento</c>,
/// <c>empresa_cnpj</c>, <c>loja_codigo</c>, <c>itens[]</c> com
/// <c>codigo_produto_arpa</c>...). O normalizador so garante a chave de
/// identidade e o array de itens.
/// </summary>
public sealed class ArpaVendaPayloadNormalizer : IArpaPayloadNormalizer
{
    public string EntityType => "venda";

    public JsonObject Normalize(string entityKey, JsonObject sourcePayload)
    {
        var normalized = sourcePayload.DeepClone().AsObject();

        var codigo = JsonPayloadReader.ReadFirstString(
            normalized, "codigo_venda_arpa", "codigo_arpa", "codigo", "venda_id", "pedido_id")
            ?? entityKey;
        if (string.IsNullOrWhiteSpace(codigo))
        {
            throw new InvalidOperationException("Venda payload requires codigo_venda_arpa or entity_key.");
        }

        normalized["codigo_venda_arpa"] = codigo;

        if (normalized["itens"] is not JsonArray)
        {
            normalized["itens"] = new JsonArray();
        }

        return normalized;
    }
}
