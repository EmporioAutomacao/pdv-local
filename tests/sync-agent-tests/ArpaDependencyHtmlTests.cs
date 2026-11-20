using SyncAgent.LocalApi;
using Xunit;

namespace SyncAgent.Tests;

/// <summary>Selo de dependencia pendente na lista de conexoes (Configuracoes &gt; Arpa).</summary>
public sealed class ArpaDependencyHtmlTests
{
    private static readonly Dictionary<string, string> Vazio = new(StringComparer.Ordinal);

    [Fact]
    public void Pausa_por_views_mostra_preparar_views()
    {
        var html = ArpaDependencyHtml.Badge("a",
            new Dictionary<string, string> { ["a"] = "precisa de \"Preparar views\": desatualizadas" }, Vazio);
        Assert.Contains("Preparar views (pausada)", html);
        Assert.Contains("depbadge pausa", html);
    }

    [Fact]
    public void Pausa_so_do_banco_local_mostra_corrigir_banco_local_e_nao_pede_DBA()
    {
        const string motivo = "o banco local do agente nao aceita catalogo. Rode a rotina \"Corrigir outbox_events_entity_type_check\"";
        var html = ArpaDependencyHtml.Badge("a", new Dictionary<string, string> { ["a"] = motivo }, Vazio);
        Assert.Contains("Corrigir banco local (pausada)", html);
        Assert.False(ArpaDependencyHtml.PrecisaPrepararViews(motivo));
    }

    [Fact]
    public void Aviso_de_ERP_antigo_nao_diz_pausada()
    {
        var html = ArpaDependencyHtml.Badge("a", Vazio, new Dictionary<string, string> { ["a"] = "atualize o ERP" });
        Assert.Contains("depbadge aviso", html);
        Assert.DoesNotContain("pausada", html);
    }

    [Fact]
    public void Sem_pendencia_nao_mostra_nada()
    {
        Assert.Equal(string.Empty, ArpaDependencyHtml.Badge("a", Vazio, Vazio));
    }

    [Fact]
    public void Texto_Preparar_views_vira_link_para_o_bloco_DBA_da_conexao()
    {
        var html = ArpaDependencyHtml.LinkPrepararViews("precisa de &quot;Preparar views&quot;: desatualizadas", "e869c02b");
        Assert.Contains("<a href=\"/config/arpa?preparar=e869c02b#dbadetails\"", html);
        Assert.Contains(">Preparar views</a>", html);
        Assert.StartsWith("precisa de &quot;", html);
        Assert.EndsWith("&quot;: desatualizadas", html);
    }

    [Fact]
    public void So_a_primeira_ocorrencia_vira_link()
    {
        var html = ArpaDependencyHtml.LinkPrepararViews("Preparar views ... use Preparar views", "a");
        Assert.Equal(1, html.Split("<a ").Length - 1);
    }

    [Fact]
    public void Motivo_sem_Preparar_views_fica_igual()
    {
        Assert.Equal("Corrigir outbox", ArpaDependencyHtml.LinkPrepararViews("Corrigir outbox", "a"));
    }

    [Fact]
    public void Id_da_conexao_e_escapado_no_link()
    {
        Assert.Equal("/config/arpa?preparar=a%20b%26c#dbadetails", ArpaDependencyHtml.PrepararViewsPath("a b&c"));
        var html = ArpaDependencyHtml.LinkPrepararViews("Preparar views", "x\"><script>");
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public void Motivo_e_escapado_no_title()
    {
        var html = ArpaDependencyHtml.Badge("a",
            new Dictionary<string, string> { ["a"] = "Preparar views <script>\"x\"</script>" }, Vazio);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&quot;x&quot;", html);
    }
}
