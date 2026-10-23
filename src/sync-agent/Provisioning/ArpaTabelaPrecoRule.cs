using System.Collections.Concurrent;

namespace SyncAgent.Provisioning;

/// <summary>
/// Quando a conexao Arpa precisa informar a tabela de preco do ERP (contrato
/// Sync 2.17.0): com mais de uma tabela no ERP, gravar o preco em qualquer uma
/// seria chute - o ERP rejeita o produto e o agente nem coleta a conexao ate o
/// operador escolher. Com uma so (a "Geral" de todo tenant novo), ela e usada
/// automaticamente.
/// </summary>
public static class ArpaTabelaPrecoRule
{
    /// <summary>
    /// Motivo do bloqueio, ou null quando a conexao pode coletar.
    /// <paramref name="tabelasDoErp"/> null = lista desconhecida (ERP antigo,
    /// offline ou ainda nao consultado): nunca bloqueia por isso - o ERP rejeita
    /// do lado dele se for o caso.
    /// </summary>
    public static string? BlockReason(
        string nomeConexao,
        bool coletaProdutos,
        string tabelaPreco,
        IReadOnlyList<ArpaTabelaPreco>? tabelasDoErp)
    {
        if (!coletaProdutos || tabelasDoErp is null || tabelasDoErp.Count <= 1)
        {
            return null;
        }

        var informada = tabelaPreco.Trim();
        if (informada.Length == 0)
        {
            return $"A conexao Arpa \"{nomeConexao}\" precisa informar a tabela de preco: o ERP tem "
                + $"{tabelasDoErp.Count} tabelas ({string.Join(", ", tabelasDoErp.Select(t => t.Nome))}). "
                + "A sincronizacao desta conexao esta pausada ate escolher em Configuracoes > Arpa > Editar.";
        }

        if (!tabelasDoErp.Any(t => string.Equals(t.Nome.Trim(), informada, StringComparison.OrdinalIgnoreCase)))
        {
            return $"A tabela de preco \"{informada}\" da conexao Arpa \"{nomeConexao}\" nao existe mais no ERP. "
                + "A sincronizacao desta conexao esta pausada ate escolher outra em Configuracoes > Arpa > Editar.";
        }

        return null;
    }
}

/// <summary>
/// Conexoes Arpa pausadas pelo coletor (hoje: tabela de preco obrigatoria nao
/// informada). Lido por /status, pelo topo do dashboard e pela bandeja, que
/// fica avisando enquanto houver item aqui.
/// </summary>
public sealed class ArpaConnectionBlocks
{
    private readonly ConcurrentDictionary<string, ArpaConnectionBlock> _blocks = new();

    public IReadOnlyList<ArpaConnectionBlock> Snapshot =>
        _blocks.Values.OrderBy(b => b.ConnectionName, StringComparer.OrdinalIgnoreCase).ToList();

    public void Set(string connectionId, string connectionName, string reason) =>
        _blocks[connectionId] = new ArpaConnectionBlock(connectionId, connectionName, reason);

    public void Clear(string connectionId) => _blocks.TryRemove(connectionId, out _);

    /// <summary>Remove bloqueios de conexoes que nao existem mais (apagadas/desativadas).</summary>
    public void RetainOnly(IEnumerable<string> connectionIds)
    {
        var keep = connectionIds.ToHashSet(StringComparer.Ordinal);
        foreach (var id in _blocks.Keys.Where(id => !keep.Contains(id)))
        {
            _blocks.TryRemove(id, out _);
        }
    }
}

public sealed record ArpaConnectionBlock(string ConnectionId, string ConnectionName, string Reason);
