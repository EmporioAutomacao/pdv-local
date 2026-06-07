using System.Text.Json.Nodes;

namespace SyncAgent.Normalizers;

public sealed class ArpaEstoquePayloadNormalizer : IArpaPayloadNormalizer
{
    public string EntityType => "estoque";

    public JsonObject Normalize(string entityKey, JsonObject sourcePayload)
    {
        var codigoProdutoArpa = JsonPayloadReader.ReadFirstString(sourcePayload, "codigo_produto", "produto_codigo", "codigo", "codigo_arpa")
            ?? entityKey;
        if (string.IsNullOrWhiteSpace(codigoProdutoArpa))
        {
            throw new InvalidOperationException("Estoque payload requires product codigo or entity_key.");
        }

        var normalized = new JsonObject
        {
            ["codigo_produto_arpa"] = codigoProdutoArpa
        };

        JsonPayloadReader.AddIfPresent(
            normalized,
            "loja_codigo",
            JsonPayloadReader.ReadFirstString(sourcePayload, "loja_codigo", "codigo_loja", "loja", "estoque_codigo"));
        JsonPayloadReader.AddIfPresent(
            normalized,
            "quantidade",
            JsonPayloadReader.ReadDecimal(sourcePayload, "quantidade"));
        JsonPayloadReader.AddIfPresent(
            normalized,
            "minimo",
            JsonPayloadReader.ReadDecimal(sourcePayload, "estoqueminimo"));
        JsonPayloadReader.AddIfPresent(
            normalized,
            "maximo",
            JsonPayloadReader.ReadDecimal(sourcePayload, "estoquemaximo"));
        JsonPayloadReader.AddIfPresent(
            normalized,
            "local",
            JsonPayloadReader.ReadString(sourcePayload, "localizacao"));

        return normalized;
    }
}
