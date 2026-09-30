using System.Text.Json.Nodes;
using SyncAgent.Normalizers;

namespace SyncAgent.Tests;

public sealed class ArpaFinanceiroPayloadNormalizerTests
{
    private readonly ArpaFinanceiroPayloadNormalizer _normalizer = new();

    [Fact]
    public void Normalize_defaults_natureza_to_receber_when_absent()
    {
        var source = new JsonObject { ["titulo_externo_id"] = "T1" };

        var result = _normalizer.Normalize("T1", source);

        Assert.Equal("receber", result["natureza"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_preserves_natureza_pagar_when_view_provides_it()
    {
        var source = new JsonObject
        {
            ["titulo_externo_id"] = "T1",
            ["natureza"] = "pagar",
        };

        var result = _normalizer.Normalize("T1", source);

        Assert.Equal("pagar", result["natureza"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_does_not_default_natureza_when_tipo_is_present()
    {
        // NB: "tipo" so evita o default "receber" - o normalizador nao copia o
        // valor de "tipo" para a chave "natureza" (comportamento existente,
        // nao alterado por este teste).
        var source = new JsonObject
        {
            ["titulo_externo_id"] = "T1",
            ["tipo"] = "pagar",
        };

        var result = _normalizer.Normalize("T1", source);

        Assert.False(result.ContainsKey("natureza"));
        Assert.Equal("pagar", result["tipo"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_falls_back_through_titulo_id_key_chain()
    {
        var source = new JsonObject { ["documento"] = "DOC-1" };

        var result = _normalizer.Normalize("T1", source);

        Assert.Equal("DOC-1", result["titulo_externo_id"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_falls_back_to_entity_key_when_no_titulo_id_present()
    {
        var source = new JsonObject();

        var result = _normalizer.Normalize("T1", source);

        Assert.Equal("T1", result["titulo_externo_id"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_throws_when_no_titulo_id_and_no_entity_key()
    {
        var source = new JsonObject();

        Assert.Throws<InvalidOperationException>(() => _normalizer.Normalize(string.Empty, source));
    }
}
