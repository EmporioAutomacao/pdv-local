using System.Text.Json.Nodes;
using SyncAgent.Normalizers;

namespace SyncAgent.Tests;

public sealed class ArpaPlanoHistoricoPayloadNormalizerTests
{
    private readonly ArpaPlanoHistoricoPayloadNormalizer _normalizer = new();

    [Fact]
    public void Normalize_prefers_codigo_over_fallback_keys()
    {
        var source = new JsonObject
        {
            ["codigo"] = "1.01",
            ["cod_historico"] = "nao-deveria-ser-usado",
        };

        var result = _normalizer.Normalize("PH1", source);

        Assert.Equal("1.01", result["codigo"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_falls_back_to_entity_key_when_no_codigo_present()
    {
        var source = new JsonObject();

        var result = _normalizer.Normalize("PH1", source);

        Assert.Equal("PH1", result["codigo"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_throws_when_no_codigo_and_no_entity_key()
    {
        var source = new JsonObject();

        Assert.Throws<InvalidOperationException>(() => _normalizer.Normalize(string.Empty, source));
    }
}
