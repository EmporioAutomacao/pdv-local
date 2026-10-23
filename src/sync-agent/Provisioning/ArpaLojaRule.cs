namespace SyncAgent.Provisioning;

/// <summary>
/// A conexao Arpa sempre aponta para um estoque (Loja) que ja existe no ERP -
/// nao ha mais "digitar o nome a mao": um nome que nao bate com o cadastro
/// deixava a conexao sem estoque/empresa no ERP e o estoque e o preco vindos do
/// Arpa eram descartados.
/// </summary>
public static class ArpaLojaRule
{
    /// <summary>
    /// Erro para recusar o salvar, ou null quando pode salvar.
    /// <paramref name="lojasDoErp"/> null = ERP inacessivel agora: so aceita
    /// manter o estoque que a conexao ja tinha (editar os outros campos offline).
    /// </summary>
    public static string? ValidateForSave(string lojaCodigo, string? lojaCodigoAtual, IReadOnlyList<ArpaLoja>? lojasDoErp)
    {
        var escolhida = lojaCodigo.Trim();
        if (escolhida.Length == 0)
        {
            return "Escolha o estoque (Loja) do ERP desta conexao.";
        }

        if (lojasDoErp is null)
        {
            return string.Equals(escolhida, lojaCodigoAtual?.Trim(), StringComparison.OrdinalIgnoreCase)
                ? null
                : "Nao foi possivel consultar os estoques do ERP agora para confirmar a escolha. Tente novamente em instantes.";
        }

        return lojasDoErp.Any(l => string.Equals(l.Nome.Trim(), escolhida, StringComparison.OrdinalIgnoreCase))
            ? null
            : $"O estoque '{escolhida}' nao existe no ERP. Escolha um estoque da lista.";
    }
}
