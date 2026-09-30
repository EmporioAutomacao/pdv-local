using System.Text.Json.Nodes;
using SyncAgent.Normalizers;

namespace SyncAgent.Tests;

public sealed class ArpaClientePayloadNormalizerTests
{
    private readonly ArpaClientePayloadNormalizer _normalizer = new();

    [Fact]
    public void Normalize_preserves_letters_of_new_alphanumeric_cnpj()
    {
        var source = new JsonObject
        {
            ["codigo"] = "123",
            ["nome"] = "Cliente Alfanumerico",
            ["cnpj_cpf"] = "12.ABC.345/01DE-35",
        };

        var result = _normalizer.Normalize("123", source);

        Assert.Equal("12ABC34501DE35", result["documento"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_strips_punctuation_from_legacy_numeric_document()
    {
        var source = new JsonObject
        {
            ["codigo"] = "123",
            ["cnpj_cpf"] = "11.222.333/0001-81",
        };

        var result = _normalizer.Normalize("123", source);

        Assert.Equal("11222333000181", result["documento"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_uses_first_present_document_key_in_priority_order()
    {
        var source = new JsonObject
        {
            ["codigo"] = "123",
            ["cpf_cnpj"] = "12345678909",
            ["documento"] = "nao-deveria-ser-usado",
        };

        var result = _normalizer.Normalize("123", source);

        Assert.Equal("12345678909", result["documento"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_omits_documento_when_no_document_field_present()
    {
        var source = new JsonObject { ["codigo"] = "123" };

        var result = _normalizer.Normalize("123", source);

        Assert.False(result.ContainsKey("documento"));
    }

    [Fact]
    public void Normalize_falls_back_to_entity_key_when_codigo_missing()
    {
        var source = new JsonObject { ["nome"] = "Cliente sem codigo" };

        var result = _normalizer.Normalize("ARPACLI-42", source);

        Assert.Equal("ARPACLI-42", result["codigo_arpa"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_throws_when_neither_codigo_nor_entity_key_present()
    {
        var source = new JsonObject();

        Assert.Throws<InvalidOperationException>(() => _normalizer.Normalize(string.Empty, source));
    }

    [Fact]
    public void Normalize_strips_letters_from_telefone_but_not_from_documento()
    {
        var source = new JsonObject
        {
            ["codigo"] = "123",
            ["cnpj_cpf"] = "12.ABC.345/01DE-35",
            ["telefone"] = "(11) 98765-4321",
        };

        var result = _normalizer.Normalize("123", source);

        Assert.Equal("12ABC34501DE35", result["documento"]!.GetValue<string>());
        Assert.Equal("11987654321", result["telefone"]!.GetValue<string>());
    }
}
