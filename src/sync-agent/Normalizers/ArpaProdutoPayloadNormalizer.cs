using System.Text.Json.Nodes;

namespace SyncAgent.Normalizers;

public sealed class ArpaProdutoPayloadNormalizer : IArpaPayloadNormalizer
{
    public string EntityType => "produto";

    public JsonObject Normalize(string entityKey, JsonObject sourcePayload)
    {
        var codigoArpa = JsonPayloadReader.ReadString(sourcePayload, "codigo") ?? entityKey;
        if (string.IsNullOrWhiteSpace(codigoArpa))
        {
            throw new InvalidOperationException("Produto payload requires 'codigo' or entity_key.");
        }

        var normalized = new JsonObject
        {
            ["codigo_arpa"] = codigoArpa
        };

        JsonPayloadReader.AddIfPresent(normalized, "nome", JsonPayloadReader.ReadString(sourcePayload, "descricao"));
        JsonPayloadReader.AddIfPresent(normalized, "codigo_fabrica", JsonPayloadReader.ReadString(sourcePayload, "codigodefabrica"));
        JsonPayloadReader.AddIfPresent(normalized, "ncm", JsonPayloadReader.OnlyDigits(JsonPayloadReader.ReadString(sourcePayload, "cod_ncm")));
        JsonPayloadReader.AddIfPresent(
            normalized,
            "codigo_barras",
            JsonPayloadReader.ReadFirstString(sourcePayload, "codigodebarras", "codigo_barras", "ean", "gtin"));
        JsonPayloadReader.AddIfPresent(normalized, "ativo", JsonPayloadReader.ReadBoolean(sourcePayload, "ativo"));

        return normalized;
    }
}
