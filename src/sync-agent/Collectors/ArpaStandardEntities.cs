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
        bool compras = false,
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

        // Financeiro (parcelas/titulos) e obrigatorio sempre que Vendas estiver
        // habilitado: o ERP nao gera titulo a partir do evento de venda do Arpa
        // (apply_arpa_venda so cria Venda/VendaItem) - quem cria TituloReceber/
        // TituloPagar e o evento entity_type=financeiro, casado a venda por
        // codigo_venda_arpa (domain_processor.apply_financeiro).
        //
        // Compras NAO segue o mesmo acoplamento: diferente de Vendas, o ERP ja
        // cria TituloPagar a partir do proprio evento financeiro (natureza=
        // pagar) independente de Compras existir ou nao - e o inverso disso
        // (TituloPagar.compra) e resolvido nos dois sentidos pelo ERP
        // (resolve_compra_for_financeiro / vincular_titulos_pagar_soltos), sem
        // depender de ordem de chegada. Por isso SyncCompra fica como toggle
        // independente, sem forcar nem ser forcado por SyncFinanceiro.
        if (financeiro || vendas)
        {
            entities.Add(Make("financeiro", "financeiro"));
        }

        if (compras)
        {
            entities.Add(Make("compras", "compra"));
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
