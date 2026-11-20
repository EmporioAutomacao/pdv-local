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
/// Conexoes Arpa pausadas pelo coletor (tabela de preco obrigatoria nao informada e/ou
/// dependencia pendente - views sync_export desatualizadas, banco local sem o
/// entity_type) e avisos que nao pausam (ex.: views desatualizadas com ERP antigo).
/// Lido por /status, pelo topo do dashboard e pela bandeja, que fica avisando
/// enquanto houver bloqueio.
/// </summary>
public sealed class ArpaConnectionBlocks
{
    private readonly ConcurrentDictionary<string, ArpaConnectionBlock> _blocks = new();
    private readonly ConcurrentDictionary<string, ArpaConnectionBlock> _warnings = new();

    public IReadOnlyList<ArpaConnectionBlock> Snapshot =>
        _blocks.Values.OrderBy(b => b.ConnectionName, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Avisos que nao pausam a conexao.</summary>
    public IReadOnlyList<ArpaConnectionBlock> WarningsSnapshot =>
        _warnings.Values.OrderBy(b => b.ConnectionName, StringComparer.OrdinalIgnoreCase).ToList();

    public void Set(string connectionId, string connectionName, string reason,
        ArpaBlockKind kinds = ArpaBlockKind.TabelaPreco) =>
        _blocks[connectionId] = new ArpaConnectionBlock(connectionId, connectionName, reason, kinds);

    public void Clear(string connectionId) => _blocks.TryRemove(connectionId, out _);

    public void SetWarning(string connectionId, string connectionName, string reason,
        ArpaBlockKind kinds = ArpaBlockKind.Dependencias) =>
        _warnings[connectionId] = new ArpaConnectionBlock(connectionId, connectionName, reason, kinds);

    public void ClearWarning(string connectionId) => _warnings.TryRemove(connectionId, out _);

    /// <summary>Remove bloqueios/avisos de conexoes que nao existem mais (apagadas/desativadas).</summary>
    public void RetainOnly(IEnumerable<string> connectionIds)
    {
        var keep = connectionIds.ToHashSet(StringComparer.Ordinal);
        foreach (var id in _blocks.Keys.Where(id => !keep.Contains(id)))
        {
            _blocks.TryRemove(id, out _);
        }

        foreach (var id in _warnings.Keys.Where(id => !keep.Contains(id)))
        {
            _warnings.TryRemove(id, out _);
        }
    }
}

[Flags]
public enum ArpaBlockKind
{
    None = 0,
    /// <summary>Tabela de preco obrigatoria nao informada (coluna "Tabela" mostra "Informar!").</summary>
    TabelaPreco = 1,
    /// <summary>Dependencia pendente: views sync_export / banco local (ArpaDependencyRule).</summary>
    Dependencias = 2,
}

public sealed record ArpaConnectionBlock(
    string ConnectionId,
    string ConnectionName,
    string Reason,
    ArpaBlockKind Kinds = ArpaBlockKind.TabelaPreco);
