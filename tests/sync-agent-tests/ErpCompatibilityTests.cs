using SyncAgent.Heartbeat;
using Xunit;

namespace SyncAgent.Tests;

/// <summary>Protecao agente novo + ERP antigo (contrato Sync 2.19.0).</summary>
public sealed class ErpCompatibilityTests
{
    [Fact]
    public void Tipo_antigo_sempre_liberado_mesmo_com_ERP_antigo()
    {
        Assert.True(ErpCompatibility.Evaluate(known: false, accepted: null, "venda"));
        Assert.True(ErpCompatibility.Evaluate(known: true, accepted: null, "financeiro"));
    }

    [Fact]
    public void Catalogo_bloqueado_com_ERP_antigo_que_nao_manda_a_lista()
    {
        Assert.False(ErpCompatibility.Evaluate(known: true, accepted: null, "catalogo"));
    }

    [Fact]
    public void Catalogo_bloqueado_antes_do_primeiro_heartbeat()
    {
        Assert.False(ErpCompatibility.Evaluate(known: false, accepted: null, "catalogo"));
    }

    [Fact]
    public void Catalogo_bloqueado_se_a_lista_do_ERP_nao_tem_o_tipo()
    {
        Assert.False(ErpCompatibility.Evaluate(true, new[] { "venda", "financeiro" }, "catalogo"));
    }

    [Fact]
    public void Catalogo_liberado_quando_o_ERP_declara()
    {
        Assert.True(ErpCompatibility.Evaluate(true, new[] { "venda", "catalogo" }, "catalogo"));
    }
}
