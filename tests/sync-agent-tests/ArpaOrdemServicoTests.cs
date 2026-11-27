using System.Text.Json.Nodes;
using SyncAgent.Collectors;
using SyncAgent.Contracts;
using SyncAgent.Heartbeat;
using SyncAgent.Normalizers;
using Xunit;

namespace SyncAgent.Tests;

/// <summary>Ordens de Servico do Arpa (contrato Sync 2.23.0, entity_type=ordem_servico).</summary>
public sealed class ArpaOrdemServicoTests
{
    [Fact]
    public void Tipo_esta_no_contrato()
    {
        Assert.Contains("ordem_servico", SyncContractValues.EntityTypes);
    }

    [Fact]
    public void Toggle_le_a_view_ordens_servico()
    {
        var entity = Assert.Single(
            ArpaStandardEntities.Build(false, false, false, ordensServico: true),
            e => e.EntityType == "ordem_servico");
        Assert.Equal("ordens_servico", entity.Name);
        Assert.Contains("FROM sync_export.ordens_servico ", entity.Query);
    }

    [Fact]
    public void Sem_toggle_nao_coleta_OS()
    {
        var entities = ArpaStandardEntities.Build(true, true, true, vendas: true, financeiro: true);
        Assert.DoesNotContain(entities, e => e.EntityType == "ordem_servico");
    }

    [Fact]
    public void OS_bloqueada_com_ERP_que_nao_aceita_o_tipo()
    {
        Assert.False(ErpCompatibility.Evaluate(known: true, accepted: null, "ordem_servico"));
        Assert.False(ErpCompatibility.Evaluate(known: false, accepted: null, "ordem_servico"));
        Assert.False(ErpCompatibility.Evaluate(true, new[] { "venda", "catalogo" }, "ordem_servico"));
    }

    [Fact]
    public void OS_liberada_quando_o_ERP_declara()
    {
        Assert.True(ErpCompatibility.Evaluate(true, new[] { "venda", "ordem_servico" }, "ordem_servico"));
    }

    [Fact]
    public void Normalizador_preenche_codigo_pela_chave_e_garante_listas()
    {
        var source = new JsonObject
        {
            ["status"] = "A",
            ["pedidos"] = null,
            ["equipamentos"] = new JsonArray(new JsonObject { ["codigo"] = "770", ["itens"] = null }),
        };

        var result = new ArpaOrdemServicoPayloadNormalizer().Normalize("745", source);

        Assert.Equal("745", result["codigo_os_arpa"]!.GetValue<string>());
        Assert.IsType<JsonArray>(result["pedidos"]);
        var equipamento = Assert.IsType<JsonObject>(Assert.Single(result["equipamentos"]!.AsArray()));
        Assert.IsType<JsonArray>(equipamento["itens"]);
        Assert.Null(source["codigo_os_arpa"]); // nao altera o payload original
    }

    [Fact]
    public void Normalizador_mantem_codigo_da_view_e_aceita_OS_sem_equipamentos()
    {
        var source = new JsonObject { ["codigo_os_arpa"] = "12", ["equipamentos"] = "x" };

        var result = new ArpaOrdemServicoPayloadNormalizer().Normalize("outro", source);

        Assert.Equal("12", result["codigo_os_arpa"]!.GetValue<string>());
        Assert.Empty(result["equipamentos"]!.AsArray());
    }

    [Fact]
    public void Registry_despacha_ordem_servico()
    {
        var registry = new ArpaPayloadNormalizerRegistry(new IArpaPayloadNormalizer[] { new ArpaOrdemServicoPayloadNormalizer() });

        var result = registry.Normalize("ordem_servico", "9", new JsonObject());

        Assert.Equal("9", result["codigo_os_arpa"]!.GetValue<string>());
    }
}
