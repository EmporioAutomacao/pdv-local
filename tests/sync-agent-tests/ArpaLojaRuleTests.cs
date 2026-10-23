using SyncAgent.Provisioning;

namespace SyncAgent.Tests;

public class ArpaLojaRuleTests
{
    private static readonly IReadOnlyList<ArpaLoja> Lojas = [new(1, "Geral", "Empresa"), new(2, "Filial", "Empresa")];

    [Fact]
    public void EstoqueExistenteIgnorandoCaixaEEspacosPodeSalvar()
    {
        Assert.Null(ArpaLojaRule.ValidateForSave(" geral ", null, Lojas));
    }

    [Fact]
    public void SemEstoqueEscolhidoRecusa()
    {
        Assert.Contains("Escolha o estoque", ArpaLojaRule.ValidateForSave("", "Geral", Lojas));
    }

    [Fact]
    public void EstoqueQueNaoExisteNoErpRecusa()
    {
        // Antes era o "Outro (digitar manualmente)": nome que o ERP nao conhece.
        Assert.Contains("nao existe no ERP", ArpaLojaRule.ValidateForSave("Loja Nova", null, Lojas));
    }

    [Fact]
    public void ErpOfflineSoAceitaManterOEstoqueAtual()
    {
        Assert.Null(ArpaLojaRule.ValidateForSave("Geral", "geral", null));
        Assert.Contains("Nao foi possivel consultar", ArpaLojaRule.ValidateForSave("Filial", "Geral", null));
        Assert.Contains("Nao foi possivel consultar", ArpaLojaRule.ValidateForSave("Geral", null, null));
    }
}
