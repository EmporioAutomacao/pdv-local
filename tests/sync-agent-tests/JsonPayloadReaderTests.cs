using System.Text.Json.Nodes;
using SyncAgent.Normalizers;

namespace SyncAgent.Tests;

public sealed class JsonPayloadReaderTests
{
    [Theory]
    [InlineData("123.456.789-09", "12345678909")]
    [InlineData("11.222.333/0001-81", "11222333000181")]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("12ABC34501DE35", "123450135")]
    public void OnlyDigits_strips_everything_but_digits(string? input, string? expected)
    {
        Assert.Equal(expected, JsonPayloadReader.OnlyDigits(input));
    }

    [Theory]
    [InlineData("12.ABC.345/01DE-35", "12ABC34501DE35")]
    [InlineData("12abc34501de35", "12ABC34501DE35")]
    [InlineData("11.222.333/0001-81", "11222333000181")]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    public void OnlyAlphanumericUpper_preserves_letters_strips_punctuation_and_uppercases(string? input, string? expected)
    {
        Assert.Equal(expected, JsonPayloadReader.OnlyAlphanumericUpper(input));
    }

    [Fact]
    public void ReadString_returns_null_when_property_absent()
    {
        var source = new JsonObject();
        Assert.Null(JsonPayloadReader.ReadString(source, "nome"));
    }

    [Fact]
    public void ReadString_trims_whitespace()
    {
        var source = new JsonObject { ["nome"] = "  Fulano  " };
        Assert.Equal("Fulano", JsonPayloadReader.ReadString(source, "nome"));
    }

    [Fact]
    public void ReadString_stringifies_numeric_values()
    {
        var source = new JsonObject { ["codigo"] = 42 };
        Assert.Equal("42", JsonPayloadReader.ReadString(source, "codigo"));
    }

    [Fact]
    public void ReadFirstString_returns_first_non_blank_candidate_in_order()
    {
        var source = new JsonObject
        {
            ["razao_social"] = "  ",
            ["nome"] = "Cliente Teste",
            ["fantasia"] = "Nao deveria ser usado",
        };

        Assert.Equal("Cliente Teste", JsonPayloadReader.ReadFirstString(source, "razao_social", "nome", "fantasia"));
    }

    [Fact]
    public void ReadFirstString_returns_null_when_no_candidate_present()
    {
        var source = new JsonObject();
        Assert.Null(JsonPayloadReader.ReadFirstString(source, "a", "b", "c"));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ReadBoolean_reads_native_json_boolean(bool value, bool expected)
    {
        var source = new JsonObject { ["ativo"] = value };
        Assert.Equal(expected, JsonPayloadReader.ReadBoolean(source, "ativo"));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void ReadBoolean_coerces_string_boolean(string value, bool expected)
    {
        var source = new JsonObject { ["ativo"] = value };
        Assert.Equal(expected, JsonPayloadReader.ReadBoolean(source, "ativo"));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void ReadBoolean_coerces_numeric_zero_or_nonzero(int value, bool expected)
    {
        var source = new JsonObject { ["ativo"] = value };
        Assert.Equal(expected, JsonPayloadReader.ReadBoolean(source, "ativo"));
    }

    [Fact]
    public void ReadBoolean_returns_null_when_absent()
    {
        var source = new JsonObject();
        Assert.Null(JsonPayloadReader.ReadBoolean(source, "ativo"));
    }

    [Theory]
    [InlineData("19.90", 19.90)]
    [InlineData("1234", 1234)]
    public void ReadDecimal_parses_invariant_culture_numbers(string value, decimal expected)
    {
        var source = new JsonObject { ["preco"] = value };
        Assert.Equal(expected, JsonPayloadReader.ReadDecimal(source, "preco"));
    }

    [Fact]
    public void ReadDecimal_returns_null_for_non_numeric_string()
    {
        var source = new JsonObject { ["preco"] = "nao-e-numero" };
        Assert.Null(JsonPayloadReader.ReadDecimal(source, "preco"));
    }

    [Fact]
    public void ReadDateTimeOffset_parses_iso_and_normalizes_to_utc()
    {
        var source = new JsonObject { ["ocorrido_em"] = "2026-03-05T10:00:00-03:00" };

        var parsed = JsonPayloadReader.ReadDateTimeOffset(source, "ocorrido_em");

        Assert.NotNull(parsed);
        Assert.Equal(new DateTimeOffset(2026, 3, 5, 13, 0, 0, TimeSpan.Zero), parsed);
    }

    [Fact]
    public void ReadDateTimeOffset_returns_null_for_unparseable_value()
    {
        var source = new JsonObject { ["ocorrido_em"] = "nao-e-uma-data" };
        Assert.Null(JsonPayloadReader.ReadDateTimeOffset(source, "ocorrido_em"));
    }

    [Fact]
    public void AddIfPresent_string_skips_null_and_whitespace()
    {
        var target = new JsonObject();
        JsonPayloadReader.AddIfPresent(target, "nome", (string?)null);
        JsonPayloadReader.AddIfPresent(target, "email", "   ");

        Assert.False(target.ContainsKey("nome"));
        Assert.False(target.ContainsKey("email"));
    }

    [Fact]
    public void AddIfPresent_string_adds_non_blank_value()
    {
        var target = new JsonObject();
        JsonPayloadReader.AddIfPresent(target, "nome", "Fulano");

        Assert.Equal("Fulano", target["nome"]!.GetValue<string>());
    }

    [Fact]
    public void AddIfPresent_decimal_skips_null_but_keeps_zero()
    {
        var target = new JsonObject();
        JsonPayloadReader.AddIfPresent(target, "quantidade", (decimal?)null);
        JsonPayloadReader.AddIfPresent(target, "minimo", 0m);

        Assert.False(target.ContainsKey("quantidade"));
        Assert.Equal(0m, target["minimo"]!.GetValue<decimal>());
    }
}
