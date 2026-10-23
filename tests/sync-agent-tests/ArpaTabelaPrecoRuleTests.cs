using System.Text.Json;
using SyncAgent.Provisioning;

namespace SyncAgent.Tests;

public class ArpaTabelaPrecoRuleTests
{
    private static readonly IReadOnlyList<ArpaTabelaPreco> Uma = [new(1, "Geral")];
    private static readonly IReadOnlyList<ArpaTabelaPreco> Duas = [new(1, "Geral"), new(2, "Atacado")];

    [Fact]
    public void UnicaTabelaNoErpNaoExigeEscolha()
    {
        Assert.Null(ArpaTabelaPrecoRule.BlockReason("Loja", coletaProdutos: true, "", Uma));
    }

    [Fact]
    public void ListaDesconhecidaNaoBloqueia()
    {
        // ERP antigo (sem tabelas_preco) ou nunca consultado: o ERP decide do lado dele.
        Assert.Null(ArpaTabelaPrecoRule.BlockReason("Loja", coletaProdutos: true, "", null));
    }

    [Fact]
    public void VariasTabelasSemEscolhaBloqueiaListandoAsOpcoes()
    {
        var reason = ArpaTabelaPrecoRule.BlockReason("Loja Centro", coletaProdutos: true, "  ", Duas);

        Assert.NotNull(reason);
        Assert.Contains("Loja Centro", reason);
        Assert.Contains("Geral, Atacado", reason);
    }

    [Fact]
    public void VariasTabelasComEscolhaValidaIgnorandoCaixaLibera()
    {
        Assert.Null(ArpaTabelaPrecoRule.BlockReason("Loja", coletaProdutos: true, " atacado ", Duas));
    }

    [Fact]
    public void TabelaEscolhidaQueSumiuDoErpBloqueia()
    {
        var reason = ArpaTabelaPrecoRule.BlockReason("Loja", coletaProdutos: true, "Varejo", Duas);

        Assert.NotNull(reason);
        Assert.Contains("nao existe mais", reason);
    }

    [Fact]
    public void ConexaoSemProdutosNuncaBloqueia()
    {
        // Sem produtos nao ha preco a gravar: clientes/estoque/vendas seguem.
        Assert.Null(ArpaTabelaPrecoRule.BlockReason("Loja", coletaProdutos: false, "", Duas));
    }

    [Fact]
    public void BlocksGuardaUmPorConexaoERemoveAsQueSumiram()
    {
        var blocks = new ArpaConnectionBlocks();
        blocks.Set("b", "Beta", "motivo b");
        blocks.Set("a", "Alfa", "motivo a");
        blocks.Set("a", "Alfa", "motivo a2");

        Assert.Equal(["Alfa", "Beta"], blocks.Snapshot.Select(b => b.ConnectionName));
        Assert.Equal("motivo a2", blocks.Snapshot[0].Reason);

        blocks.RetainOnly(["a"]);
        Assert.Single(blocks.Snapshot);

        blocks.Clear("a");
        Assert.Empty(blocks.Snapshot);
    }

    [Fact]
    public void RespostaDeLojasLeTabelasPrecoQuandoPresente()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var nova = JsonSerializer.Deserialize<ArpaLojaListResponse>(
            """{"status":"ok","lojas":[],"tabelas_preco":[{"id":1,"nome":"Geral","empresa_nome":"Empresa"}]}""",
            options)!;
        var antiga = JsonSerializer.Deserialize<ArpaLojaListResponse>("""{"status":"ok","lojas":[]}""", options)!;

        Assert.Equal("Geral", Assert.Single(nova.TabelasPreco!).Nome);
        Assert.Null(antiga.TabelasPreco);
    }
}
