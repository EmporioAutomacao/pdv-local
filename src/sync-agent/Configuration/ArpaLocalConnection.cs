namespace SyncAgent.Configuration;

/// <summary>
/// Conexao Arpa Control gerida localmente pelo dashboard do agente
/// (aba Configuracoes > Arpa). Substitui o ArpaControlConexao do ERP para esta
/// instalacao. Cada conexao aponta para um banco Arpa Control e e mapeada a uma
/// Loja/Estoque do ERP (<see cref="LojaCodigo"/>).
///
/// A lista e persistida cifrada por DPAPI LocalMachine em
/// <c>ArpaCollector:LocalConnectionsProtectedFile</c> (contem senhas).
/// </summary>
public sealed record ArpaLocalConnection
{
    public string Id { get; init; } = string.Empty;

    public string Nome { get; init; } = string.Empty;

    public string Host { get; init; } = string.Empty;

    public int Port { get; init; } = 5432;

    public string Database { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    /// <summary>
    /// Nome da Loja/Estoque no ERP. Vai como <c>loja_codigo</c> no payload dos
    /// eventos de estoque desta conexao (o ERP resolve/cria a Loja por nome em
    /// <c>domain_processor.resolve_estoque_for_venda</c>).
    /// </summary>
    public string LojaCodigo { get; init; } = string.Empty;

    public bool ControlaEstoque { get; init; }

    public bool SyncProdutos { get; init; } = true;

    public bool SyncClientes { get; init; } = true;

    public bool SyncEstoque { get; init; }

    public bool SyncVendas { get; init; }

    public bool SyncFinanceiro { get; init; }

    /// <summary>
    /// Contas bancarias de cobranca (<c>entity_type=cobranca</c>, contrato Sync
    /// 2.10.0). Requer a view <c>sync_export.cobranca</c> — ainda nao gerada pelo
    /// script padrao (ver <c>infra/arpa/sync-export-views-contract.sql</c>).
    /// </summary>
    public bool SyncCobranca { get; init; }

    public int BatchSize { get; init; } = 5000;

    public bool Enabled { get; init; } = true;
}
