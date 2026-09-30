using System.Text.Json.Nodes;
using SyncAgent.Normalizers;

namespace SyncAgent.Tests;

public sealed class ArpaCobrancaPayloadNormalizerTests
{
    private readonly ArpaCobrancaPayloadNormalizer _normalizer = new();

    [Fact]
    public void Normalize_prefers_externo_id_over_other_keys()
    {
        var source = new JsonObject
        {
            ["externo_id"] = "EXT-1",
            ["codigo"] = "nao-deveria-ser-usado",
        };

        var result = _normalizer.Normalize("C1", source);

        Assert.Equal("EXT-1", result["externo_id"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_falls_back_to_entity_key_when_no_id_present()
    {
        var source = new JsonObject();

        var result = _normalizer.Normalize("C1", source);

        Assert.Equal("C1", result["externo_id"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_passes_through_other_fields_unchanged()
    {
        var source = new JsonObject
        {
            ["externo_id"] = "EXT-1",
            ["cedente_documento"] = "12.345.678/0001-99",
            ["banco_codigo"] = "341",
        };

        var result = _normalizer.Normalize("C1", source);

        Assert.Equal("12.345.678/0001-99", result["cedente_documento"]!.GetValue<string>());
        Assert.Equal("341", result["banco_codigo"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_throws_when_no_id_and_no_entity_key()
    {
        var source = new JsonObject();

        Assert.Throws<InvalidOperationException>(() => _normalizer.Normalize(string.Empty, source));
    }
}
