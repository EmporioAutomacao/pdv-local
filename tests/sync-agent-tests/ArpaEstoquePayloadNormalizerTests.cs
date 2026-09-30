using System.Text.Json.Nodes;
using SyncAgent.Normalizers;

namespace SyncAgent.Tests;

public sealed class ArpaEstoquePayloadNormalizerTests
{
    private readonly ArpaEstoquePayloadNormalizer _normalizer = new();

    [Fact]
    public void Normalize_maps_view_fields_to_contract_fields()
    {
        var source = new JsonObject
        {
            ["codigo_produto"] = "P1",
            ["loja_codigo"] = "L1",
            ["quantidade"] = "10.5",
            ["estoqueminimo"] = "2",
            ["estoquemaximo"] = "100",
            ["localizacao"] = "Corredor 3",
        };

        var result = _normalizer.Normalize("P1", source);

        Assert.Equal("P1", result["codigo_produto_arpa"]!.GetValue<string>());
        Assert.Equal("L1", result["loja_codigo"]!.GetValue<string>());
        Assert.Equal(10.5m, result["quantidade"]!.GetValue<decimal>());
        Assert.Equal(2m, result["minimo"]!.GetValue<decimal>());
        Assert.Equal(100m, result["maximo"]!.GetValue<decimal>());
        Assert.Equal("Corredor 3", result["local"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_falls_back_to_entity_key_when_no_codigo_present()
    {
        var source = new JsonObject();

        var result = _normalizer.Normalize("P1", source);

        Assert.Equal("P1", result["codigo_produto_arpa"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_omits_optional_fields_when_absent()
    {
        var source = new JsonObject { ["codigo_produto"] = "P1" };

        var result = _normalizer.Normalize("P1", source);

        Assert.False(result.ContainsKey("quantidade"));
        Assert.False(result.ContainsKey("minimo"));
        Assert.False(result.ContainsKey("maximo"));
        Assert.False(result.ContainsKey("local"));
    }

    [Fact]
    public void Normalize_throws_when_no_codigo_and_no_entity_key()
    {
        var source = new JsonObject();

        Assert.Throws<InvalidOperationException>(() => _normalizer.Normalize(string.Empty, source));
    }
}
