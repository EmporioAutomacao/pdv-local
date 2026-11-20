using SyncAgent.Collectors;
using SyncAgent.Contracts;
using Xunit;

namespace SyncAgent.Tests;

public sealed class ArpaStandardEntitiesTests
{
    [Fact]
    public void Catalogo_vai_junto_com_vendas_ou_financeiro()
    {
        Assert.Contains(ArpaStandardEntities.Build(false, false, false, vendas: true), e => e.EntityType == "catalogo");
        Assert.Contains(ArpaStandardEntities.Build(false, false, false, financeiro: true), e => e.EntityType == "catalogo");
    }

    [Fact]
    public void Catalogo_nao_vai_sem_vendas_nem_financeiro()
    {
        Assert.DoesNotContain(ArpaStandardEntities.Build(true, true, true), e => e.EntityType == "catalogo");
    }

    [Fact]
    public void Catalogo_le_a_view_catalogos_e_esta_no_contrato()
    {
        var entity = Assert.Single(
            ArpaStandardEntities.Build(false, false, false, vendas: true),
            e => e.EntityType == "catalogo");
        Assert.Contains("FROM sync_export.catalogos", entity.Query);
        Assert.Contains("catalogo", SyncContractValues.EntityTypes);
    }
}
