using System.Text.Json.Nodes;
using SyncAgent.Normalizers;

namespace SyncAgent.Tests;

public sealed class ArpaVendaPayloadNormalizerTests
{
    private readonly ArpaVendaPayloadNormalizer _normalizer = new();

    [Fact]
    public void Normalize_preserves_existing_fields_and_normalizes_codigo_key()
    {
        var source = new JsonObject
        {
            ["codigo"] = "789",
            ["cliente_documento"] = "12.ABC.345/01DE-35",
            ["empresa_cnpj"] = "11.222.333/0001-81",
        };

        var result = _normalizer.Normalize("789", source);

        Assert.Equal("789", result["codigo_venda_arpa"]!.GetValue<string>());
        Assert.Equal("12.ABC.345/01DE-35", result["cliente_documento"]!.GetValue<string>());
        Assert.Equal("11.222.333/0001-81", result["empresa_cnpj"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_defaults_itens_to_empty_array_when_absent()
    {
        var source = new JsonObject { ["codigo_venda_arpa"] = "789" };

        var result = _normalizer.Normalize("789", source);

        var itens = Assert.IsType<JsonArray>(result["itens"]);
        Assert.Empty(itens);
    }

    [Fact]
    public void Normalize_keeps_existing_itens_array_untouched()
    {
        var source = new JsonObject
        {
            ["codigo_venda_arpa"] = "789",
            ["itens"] = new JsonArray { new JsonObject { ["codigo_produto_arpa"] = "1" } },
        };

        var result = _normalizer.Normalize("789", source);

        var itens = Assert.IsType<JsonArray>(result["itens"]);
        Assert.Single(itens);
    }

    [Fact]
    public void Normalize_falls_back_through_codigo_key_chain()
    {
        var source = new JsonObject { ["pedido_id"] = "PED-1" };

        var result = _normalizer.Normalize("789", source);

        Assert.Equal("PED-1", result["codigo_venda_arpa"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_falls_back_to_entity_key_when_no_codigo_present()
    {
        var source = new JsonObject();

        var result = _normalizer.Normalize("789", source);

        Assert.Equal("789", result["codigo_venda_arpa"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_throws_when_no_codigo_and_no_entity_key()
    {
        var source = new JsonObject();

        Assert.Throws<InvalidOperationException>(() => _normalizer.Normalize(string.Empty, source));
    }
}
