using Npgsql;
using SyncAgent.Provisioning;

namespace SyncAgent.Tests;

public class PostgresErrorTextTests
{
    private static PostgresException Pg(string sqlState, string message) =>
        new(message, "FATAL", "FATAL", sqlState);

    [Fact]
    public void SenhaErradaComAcentosQuebradosViraTextoProprio()
    {
        // Como chega de um Postgres Windows pt_BR (WIN1252 lido como UTF-8).
        var texto = PostgresErrorText.Describe(Pg("28P01", "autentica��o do tipo senha falhou para o usu�rio \"postgres\""));

        Assert.Equal("28P01: Senha incorreta para o usuario informado (ou o servidor exige senha e nenhuma foi informada).", texto);
        Assert.DoesNotContain('�', texto);
    }

    [Theory]
    [InlineData("28000")]
    [InlineData("3D000")]
    [InlineData("57P03")]
    public void ErrosDeConexaoConhecidosNuncaRepassamOTextoDoServidor(string sqlState)
    {
        var texto = PostgresErrorText.Describe(Pg(sqlState, "texto do servidor com �"));

        Assert.StartsWith(sqlState + ": ", texto);
        Assert.DoesNotContain('�', texto);
    }

    [Fact]
    public void ErroDesconhecidoComAcentosQuebradosViraGenericoComCodigo()
    {
        var texto = PostgresErrorText.Describe(Pg("XX000", "falha na opera��o"));

        Assert.StartsWith("XX000: ", texto);
        Assert.DoesNotContain('�', texto);
    }

    [Fact]
    public void ErroDesconhecidoLegivelMantemOTextoDoServidor()
    {
        Assert.Equal("42P01: relation \"x\" does not exist", PostgresErrorText.Describe(Pg("42P01", "relation \"x\" does not exist")));
    }
}
