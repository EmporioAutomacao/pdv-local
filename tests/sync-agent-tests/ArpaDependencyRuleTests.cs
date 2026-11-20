using SyncAgent.Provisioning;
using Xunit;

namespace SyncAgent.Tests;

/// <summary>Pausa/aviso por dependencia pendente (views sync_export e banco local).</summary>
public sealed class ArpaDependencyRuleTests
{
    private const string Embutida = "2026.11.21.1";
    private static readonly string[] Nenhum = [];
    private static readonly string[] Coletados = ["venda", "financeiro", "catalogo"];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("2026.11.20.1")]
    public void Views_sem_versao_ou_antigas_pausam_com_ERP_compativel(string? instalada)
    {
        var r = ArpaDependencyRule.Evaluate("Anapolis", instalada, Embutida, erpCompatible: true, Nenhum, Coletados);
        Assert.True(r.Pausar);
        Assert.Contains("Preparar views", r.Pausas[0]);
        Assert.Empty(r.Avisos);
    }

    [Theory]
    [InlineData("2026.11.21.1")]
    [InlineData("2026.12.01.1")]
    public void Views_na_versao_ou_mais_novas_nao_pausam(string instalada)
    {
        var r = ArpaDependencyRule.Evaluate("Anapolis", instalada, Embutida, true, Nenhum, Coletados);
        Assert.False(r.Pausar);
        Assert.Empty(r.Avisos);
    }

    [Fact]
    public void Views_antigas_com_ERP_antigo_so_avisam()
    {
        var r = ArpaDependencyRule.Evaluate("Anapolis", null, Embutida, erpCompatible: false, Nenhum, ["venda", "financeiro"]);
        Assert.False(r.Pausar);
        Assert.Single(r.Avisos);
        Assert.Contains("atualize o ERP", r.Avisos[0]);
    }

    [Fact]
    public void Banco_local_sem_tipo_coletado_pausa()
    {
        var r = ArpaDependencyRule.Evaluate("Anapolis", Embutida, Embutida, true, ["catalogo"], Coletados);
        Assert.True(r.Pausar);
        Assert.Contains("Corrigir outbox_events_entity_type_check", r.Pausas[0]);
    }

    [Fact]
    public void Banco_local_sem_catalogo_nao_pausa_quando_catalogo_nao_e_coletado()
    {
        // ERP antigo: o coletor nem coleta catalogo, entao o CHECK sem ele nao atrapalha.
        var r = ArpaDependencyRule.Evaluate("Anapolis", Embutida, Embutida, false, ["catalogo"], ["venda", "financeiro"]);
        Assert.False(r.Pausar);
    }

    [Fact]
    public void Sem_versao_embutida_nao_exige_views()
    {
        Assert.False(ArpaDependencyRule.ViewsDesatualizadas(null, null));
    }

    [Fact]
    public void Versao_do_script_embutido_e_encontrada_e_e_valida()
    {
        var embutida = ArpaDdlRunner.EmbeddedViewsVersion;
        Assert.False(string.IsNullOrWhiteSpace(embutida));
        Assert.True(Version.TryParse(embutida, out _));
    }

    [Fact]
    public void Le_a_versao_do_comentario_do_schema()
    {
        Assert.Equal("2026.11.21.1", ArpaDdlRunner.ParseViewsVersion("ararasuite:sync_export_versao=2026.11.21.1"));
        Assert.Null(ArpaDdlRunner.ParseViewsVersion("views antigas"));
        Assert.Null(ArpaDdlRunner.ParseViewsVersion(null));
    }

    [Fact]
    public void Bloqueio_por_dependencia_nao_e_confundido_com_tabela_de_preco()
    {
        var blocks = new ArpaConnectionBlocks();
        blocks.Set("a", "Alfa", "views", ArpaBlockKind.Dependencias);
        blocks.Set("b", "Beta", "tabela");
        blocks.SetWarning("c", "Gama", "aviso");

        Assert.False(blocks.Snapshot.Single(b => b.ConnectionId == "a").Kinds.HasFlag(ArpaBlockKind.TabelaPreco));
        Assert.True(blocks.Snapshot.Single(b => b.ConnectionId == "b").Kinds.HasFlag(ArpaBlockKind.TabelaPreco));
        Assert.Single(blocks.WarningsSnapshot);

        blocks.RetainOnly(["a"]);
        Assert.Empty(blocks.WarningsSnapshot);
        Assert.Single(blocks.Snapshot);
    }
}
