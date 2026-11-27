using System.Text.Json.Nodes;

namespace SyncAgent.Normalizers;

/// <summary>
/// A view <c>sync_export.ordens_servico</c> (contrato Sync 2.23.0) ja entrega o
/// <c>payload_json</c> no formato do ERP (<c>codigo_os_arpa</c>, <c>status</c>,
/// <c>cliente_documento</c>, <c>pedidos[]</c>, <c>equipamentos[]</c> com
/// <c>itens[]</c>). O normalizador garante a chave de identidade e que as listas
/// sejam sempre arrays (o ERP itera sem checar o tipo).
/// </summary>
public sealed class ArpaOrdemServicoPayloadNormalizer : IArpaPayloadNormalizer
{
    public string EntityType => "ordem_servico";

    public JsonObject Normalize(string entityKey, JsonObject sourcePayload)
    {
        var normalized = sourcePayload.DeepClone().AsObject();

        var codigo = JsonPayloadReader.ReadFirstString(normalized, "codigo_os_arpa", "codigo")
            ?? entityKey;
        if (string.IsNullOrWhiteSpace(codigo))
        {
            throw new InvalidOperationException("Ordem de servico payload requires codigo_os_arpa or entity_key.");
        }

        normalized["codigo_os_arpa"] = codigo;

        if (normalized["pedidos"] is not JsonArray)
        {
            normalized["pedidos"] = new JsonArray();
        }

        if (normalized["equipamentos"] is not JsonArray equipamentos)
        {
            normalized["equipamentos"] = new JsonArray();
            return normalized;
        }

        foreach (var equipamento in equipamentos.OfType<JsonObject>())
        {
            if (equipamento["itens"] is not JsonArray)
            {
                equipamento["itens"] = new JsonArray();
            }
        }

        return normalized;
    }
}
