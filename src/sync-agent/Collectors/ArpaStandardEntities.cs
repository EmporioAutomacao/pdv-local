using SyncAgent.Configuration;

namespace SyncAgent.Collectors;

/// <summary>
/// Monta as entidades/queries padrao do coletor a partir dos toggles de uma
/// conexao local (Produtos / Clientes / Estoque). Porta C# do
/// <c>conexoes/services/arpa_produtos.py::build_sync_agent_entities</c> do ERP:
/// todas as views seguem o contrato <c>entity_key, occurred_at_utc,
/// payload_json, trace_id</c>, entao a query so repassa essas colunas com filtro
/// de watermark.
/// </summary>
public static class ArpaStandardEntities
{
    private const string QueryTemplate =
        "SELECT entity_key, occurred_at_utc, payload_json, trace_id " +
        "FROM sync_export.{0} " +
        "WHERE occurred_at_utc > @watermark_utc " +
        "ORDER BY occurred_at_utc, entity_key LIMIT @limit";

    public static IReadOnlyList<ArpaEntityCollectorOptions> Build(
        bool produtos,
        bool clientes,
        bool estoque,
        bool vendas = false,
        bool financeiro = false,
        bool cobranca = false,
        bool planoHistorico = false)
    {
        var entities = new List<ArpaEntityCollectorOptions>();

        if (produtos)
        {
            entities.Add(Make("produtos", "produto"));
        }

        if (clientes)
        {
            entities.Add(Make("clientes", "cliente"));
        }

        if (estoque)
        {
            entities.Add(Make("estoque", "estoque"));
        }

        if (vendas)
        {
            entities.Add(Make("vendas", "venda"));
        }

        if (financeiro)
        {
            entities.Add(Make("financeiro", "financeiro"));
        }

        if (cobranca)
        {
            entities.Add(Make("cobranca", "cobranca"));
        }

        if (planoHistorico)
        {
            entities.Add(Make("plano_historico", "plano_historico"));
        }

        return entities;
    }

    private static ArpaEntityCollectorOptions Make(string view, string entityType) => new()
    {
        Name = view,
        EntityType = entityType,
        Query = string.Format(QueryTemplate, view),
    };
}
