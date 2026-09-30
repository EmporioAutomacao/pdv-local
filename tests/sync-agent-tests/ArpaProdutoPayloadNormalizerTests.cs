using System.Text.Json.Nodes;
using SyncAgent.Normalizers;

namespace SyncAgent.Tests;

public sealed class ArpaProdutoPayloadNormalizerTests
{
    private readonly ArpaProdutoPayloadNormalizer _normalizer = new();

    [Fact]
    public void Normalize_maps_view_fields_to_contract_fields()
    {
        var source = new JsonObject
        {
            ["codigo"] = "P1",
            ["descricao"] = "Arroz 5kg",
            ["codigodefabrica"] = "FAB-1",
            ["cod_ncm"] = "1006.30.21",
            ["codigodebarras"] = "7891000000010",
            ["ativo"] = true,
            ["precocusto"] = "10.00",
            ["precovenda"] = "19.90",
            ["unidade"] = "UN",
        };

        var result = _normalizer.Normalize("P1", source);

        Assert.Equal("P1", result["codigo_arpa"]!.GetValue<string>());
        Assert.Equal("Arroz 5kg", result["nome"]!.GetValue<string>());
        Assert.Equal("FAB-1", result["codigo_fabrica"]!.GetValue<string>());
        Assert.Equal("10063021", result["ncm"]!.GetValue<string>());
        Assert.Equal("7891000000010", result["codigo_barras"]!.GetValue<string>());
        Assert.True(result["ativo"]!.GetValue<bool>());
        Assert.Equal(10.00m, result["custo"]!.GetValue<decimal>());
        Assert.Equal(19.90m, result["preco_venda"]!.GetValue<decimal>());
        Assert.Equal("UN", result["unidade"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_falls_back_through_codigo_barras_key_chain()
    {
        var source = new JsonObject
        {
            ["codigo"] = "P1",
            ["ean"] = "7891000000010",
        };

        var result = _normalizer.Normalize("P1", source);

        Assert.Equal("7891000000010", result["codigo_barras"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_falls_back_to_entity_key_when_codigo_missing()
    {
        var source = new JsonObject();

        var result = _normalizer.Normalize("P1", source);

        Assert.Equal("P1", result["codigo_arpa"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_throws_when_no_codigo_and_no_entity_key()
    {
        var source = new JsonObject();

        Assert.Throws<InvalidOperationException>(() => _normalizer.Normalize(string.Empty, source));
    }
}
