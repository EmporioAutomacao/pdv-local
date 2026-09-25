using System.Text.Json.Nodes;

namespace SyncAgent.Normalizers;

/// <summary>
/// A view <c>sync_export.compras</c> ja entrega o <c>payload_json</c> no
/// formato consumido por <c>sync_api.domain_processor.apply_compra</c> (chaves
/// <c>codigo_compra_arpa</c>, <c>fornecedor_documento</c>/<c>fornecedor_codigo</c>,
/// <c>empresa_cnpj</c>, <c>loja_codigo</c>, <c>itens[]</c> com
/// <c>codigo_produto_arpa</c>...). O normalizador so garante a chave de
/// identidade e o array de itens.
/// </summary>
public sealed class ArpaCompraPayloadNormalizer : IArpaPayloadNormalizer
{
    public string EntityType => "compra";

    public JsonObject Normalize(string entityKey, JsonObject sourcePayload)
    {
        var normalized = sourcePayload.DeepClone().AsObject();

        var codigo = JsonPayloadReader.ReadFirstString(
            normalized, "codigo_compra_arpa", "codigo_arpa", "codigo")
            ?? entityKey;
        if (string.IsNullOrWhiteSpace(codigo))
        {
            throw new InvalidOperationException("Compra payload requires codigo_compra_arpa or entity_key.");
        }

        normalized["codigo_compra_arpa"] = codigo;

        if (normalized["itens"] is not JsonArray)
        {
            normalized["itens"] = new JsonArray();
        }

        return normalized;
    }
}
