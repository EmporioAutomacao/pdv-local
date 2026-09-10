using System.Text.Json.Nodes;

namespace SyncAgent.Normalizers;

/// <summary>
/// A view <c>sync_export.cobranca</c> entrega o <c>payload_json</c> com as chaves
/// cruas de uma conta bancaria de cobranca. O ERP
/// (<c>sync_api.domain_processor.apply_cobranca</c> -&gt;
/// <c>cobranca.services.arpa._normalize_arpa_cobranca_row</c>) infere a
/// identidade do banco, limpa digitos e monta o cedente. Este normalizador so
/// garante a chave de identidade (<c>externo_id</c>) — repassa o resto como veio.
/// Contrato Sync 2.10.0.
/// </summary>
public sealed class ArpaCobrancaPayloadNormalizer : IArpaPayloadNormalizer
{
    public string EntityType => "cobranca";

    public JsonObject Normalize(string entityKey, JsonObject sourcePayload)
    {
        var normalized = sourcePayload.DeepClone().AsObject();

        var externoId = JsonPayloadReader.ReadFirstString(
            normalized, "externo_id", "codigo", "codigo_arpa", "id")
            ?? entityKey;
        if (string.IsNullOrWhiteSpace(externoId))
        {
            throw new InvalidOperationException("Cobranca payload requires externo_id or entity_key.");
        }

        normalized["externo_id"] = externoId;
        return normalized;
    }
}
