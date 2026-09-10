using System.Text.Json.Nodes;

namespace SyncAgent.Normalizers;

/// <summary>
/// A view <c>sync_export.plano_historico</c> entrega o <c>payload_json</c> com o
/// catalogo de planos de historico financeiro (chave natural
/// <c>codigo</c>+<c>analitico</c>). O ERP
/// (<c>sync_api.domain_processor.apply_plano_historico</c> -&gt;
/// <c>financeiro.planos_historico.upsert_plano_historico_arpa</c>) grava
/// <c>PlanoHistoricoFinanceiro</c> e recalcula <c>codigo_erp</c>. Este
/// normalizador so garante a chave de identidade (<c>codigo</c>).
/// Contrato Sync 2.11.0.
/// </summary>
public sealed class ArpaPlanoHistoricoPayloadNormalizer : IArpaPayloadNormalizer
{
    public string EntityType => "plano_historico";

    public JsonObject Normalize(string entityKey, JsonObject sourcePayload)
    {
        var normalized = sourcePayload.DeepClone().AsObject();

        var codigo = JsonPayloadReader.ReadFirstString(normalized, "codigo", "cod_historico", "id", "historico")
            ?? entityKey;
        if (string.IsNullOrWhiteSpace(codigo))
        {
            throw new InvalidOperationException("Plano historico payload requires codigo or entity_key.");
        }

        normalized["codigo"] = codigo;
        return normalized;
    }
}
