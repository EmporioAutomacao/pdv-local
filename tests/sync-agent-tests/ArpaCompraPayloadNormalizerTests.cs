using System.Text.Json.Nodes;
using SyncAgent.Normalizers;

namespace SyncAgent.Tests;

public sealed class ArpaCompraPayloadNormalizerTests
{
    private readonly ArpaCompraPayloadNormalizer _normalizer = new();

    [Fact]
    public void Normalize_prefers_codigo_compra_arpa_over_fallback_keys()
    {
        var source = new JsonObject
        {
            ["codigo_compra_arpa"] = "COMPRA-1",
            ["codigo_arpa"] = "nao-deveria-ser-usado",
        };

        var result = _normalizer.Normalize("321", source);

        Assert.Equal("COMPRA-1", result["codigo_compra_arpa"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_falls_back_to_entity_key_when_no_codigo_present()
    {
        var source = new JsonObject();

        var result = _normalizer.Normalize("321", source);

        Assert.Equal("321", result["codigo_compra_arpa"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_defaults_itens_to_empty_array_when_absent()
    {
        var source = new JsonObject { ["codigo_compra_arpa"] = "COMPRA-1" };

        var result = _normalizer.Normalize("321", source);

        var itens = Assert.IsType<JsonArray>(result["itens"]);
        Assert.Empty(itens);
    }

    [Fact]
    public void Normalize_throws_when_no_codigo_and_no_entity_key()
    {
        var source = new JsonObject();

        Assert.Throws<InvalidOperationException>(() => _normalizer.Normalize(string.Empty, source));
    }
}
