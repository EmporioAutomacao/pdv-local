using System.Text.Json.Nodes;
using SyncAgent.Normalizers;

namespace SyncAgent.Tests;

public sealed class ArpaPayloadNormalizerRegistryTests
{
    private static ArpaPayloadNormalizerRegistry BuildRegistry() => new(new IArpaPayloadNormalizer[]
    {
        new ArpaClientePayloadNormalizer(),
        new ArpaProdutoPayloadNormalizer(),
        new ArpaEstoquePayloadNormalizer(),
        new ArpaVendaPayloadNormalizer(),
        new ArpaCompraPayloadNormalizer(),
        new ArpaFinanceiroPayloadNormalizer(),
        new ArpaCobrancaPayloadNormalizer(),
        new ArpaPlanoHistoricoPayloadNormalizer(),
    });

    [Fact]
    public void Normalize_dispatches_to_normalizer_matching_entity_type()
    {
        var registry = BuildRegistry();
        var source = new JsonObject
        {
            ["codigo"] = "123",
            ["cnpj_cpf"] = "12.ABC.345/01DE-35",
        };

        var result = registry.Normalize("cliente", "123", source);

        Assert.Equal("12ABC34501DE35", result["documento"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_passes_through_unchanged_for_unknown_entity_type()
    {
        var registry = BuildRegistry();
        var source = new JsonObject { ["qualquer"] = "valor" };

        var result = registry.Normalize("entidade_desconhecida", "X1", source);

        Assert.Same(source, result);
    }
}
