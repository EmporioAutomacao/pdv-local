using System.Net;

namespace SyncAgent.LocalApi;

/// <summary>
/// Pedacos de HTML do painel para dependencia pendente de uma conexao Arpa
/// (ver <see cref="Provisioning.ArpaDependencyRule"/>). Separado do
/// <see cref="LocalStatusServer"/> para ser testavel.
/// </summary>
public static class ArpaDependencyHtml
{
    /// <summary>O motivo pede "Preparar views" (credencial DBA), e nao so a rotina do banco local.</summary>
    public static bool PrecisaPrepararViews(string reason) =>
        reason.Contains("Preparar views", StringComparison.Ordinal);

    /// <summary>Caminho que abre a conexao em Configuracoes &gt; Arpa com o bloco "Preparar banco Arpa" aberto.</summary>
    public static string PrepararViewsPath(string connectionId) =>
        $"/config/arpa?preparar={Uri.EscapeDataString(connectionId)}#dbadetails";

    /// <summary>
    /// Recebe o motivo JA escapado em HTML e transforma a primeira ocorrencia do texto
    /// "Preparar views" num link para o bloco DBA da conexao. Sem a ocorrencia,
    /// devolve o texto como veio.
    /// </summary>
    public static string LinkPrepararViews(string encodedReason, string connectionId)
    {
        const string texto = "Preparar views";
        var i = encodedReason.IndexOf(texto, StringComparison.Ordinal);
        if (i < 0)
        {
            return encodedReason;
        }

        var href = WebUtility.HtmlEncode(PrepararViewsPath(connectionId));
        return encodedReason[..i]
            + $"<a href=\"{href}\" title=\"Abrir Preparar banco Arpa desta conexao\">{texto}</a>"
            + encodedReason[(i + texto.Length)..];
    }

    /// <summary>Selo ao lado do nome da conexao na lista de Configuracoes &gt; Arpa.</summary>
    public static string Badge(
        string connectionId,
        IReadOnlyDictionary<string, string> depBlocks,
        IReadOnlyDictionary<string, string> depWarns)
    {
        if (depBlocks.TryGetValue(connectionId, out var reason))
        {
            var label = PrecisaPrepararViews(reason) ? "&#9888; Preparar views" : "&#9888; Corrigir banco local";
            return $" <span class=\"depbadge pausa\" title=\"{WebUtility.HtmlEncode(reason)}\">{label} (pausada)</span>";
        }

        return depWarns.TryGetValue(connectionId, out var aviso)
            ? $" <span class=\"depbadge aviso\" title=\"{WebUtility.HtmlEncode(aviso)}\">Views desatualizadas (atualize o ERP)</span>"
            : string.Empty;
    }
}
