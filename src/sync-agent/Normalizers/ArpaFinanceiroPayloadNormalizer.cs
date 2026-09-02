using System.Text.Json.Nodes;

namespace SyncAgent.Normalizers;

/// <summary>
/// A view <c>sync_export.financeiro</c> ja entrega o <c>payload_json</c> no
/// formato consumido por <c>sync_api.domain_processor.apply_financeiro</c>
/// (chaves <c>titulo_externo_id</c>, <c>natureza</c>, <c>codigo_venda_arpa</c>,
/// <c>valor_base</c>, <c>vencimento</c>, <c>valor_recebido</c>...). O
/// normalizador so garante a chave de identidade e <c>natureza=receber</c>
/// (o contrato v1 so aceita titulos a receber).
/// </summary>
public sealed class ArpaFinanceiroPayloadNormalizer : IArpaPayloadNormalizer
{
    public string EntityType => "financeiro";

    public JsonObject Normalize(string entityKey, JsonObject sourcePayload)
    {
        var normalized = sourcePayload.DeepClone().AsObject();

        var tituloId = JsonPayloadReader.ReadFirstString(
            normalized, "titulo_externo_id", "codigo_titulo_arpa", "codigo_arpa", "titulo_id", "documento", "numero")
            ?? entityKey;
        if (string.IsNullOrWhiteSpace(tituloId))
        {
            throw new InvalidOperationException("Financeiro payload requires titulo_externo_id or entity_key.");
        }

        normalized["titulo_externo_id"] = tituloId;

        if (string.IsNullOrWhiteSpace(JsonPayloadReader.ReadFirstString(normalized, "natureza", "tipo")))
        {
            normalized["natureza"] = "receber";
        }

        return normalized;
    }
}
