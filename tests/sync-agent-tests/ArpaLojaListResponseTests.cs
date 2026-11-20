using System.Text.Json;
using SyncAgent.Provisioning;

namespace SyncAgent.Tests;

/// <summary>
/// Contrato Sync 2.18.0: <c>lojas[].empresa_id</c> (so exibicao, "01 - Emporio"
/// no formulario de conexao Arpa). ERP antigo nao manda o campo.
/// </summary>
public class ArpaLojaListResponseTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void LeEmpresaIdQuandoPresente()
    {
        var resposta = JsonSerializer.Deserialize<ArpaLojaListResponse>(
            """{"status":"ok","lojas":[{"id":3,"nome":"Matriz","empresa_id":1,"empresa_nome":"Emporio","empresa_cnpj":"11222333000144","em_uso_por":null}]}""",
            Options)!;

        var loja = Assert.Single(resposta.Lojas!);
        Assert.Equal(1, loja.EmpresaId);
        Assert.Equal("Emporio", loja.EmpresaNome);
    }

    [Fact]
    public void EmpresaIdNuloComErpAntigoOuLojaSemEmpresa()
    {
        var antiga = JsonSerializer.Deserialize<ArpaLojaListResponse>(
            """{"status":"ok","lojas":[{"id":3,"nome":"Matriz","empresa_nome":"Emporio","empresa_cnpj":""}]}""",
            Options)!;
        var semEmpresa = JsonSerializer.Deserialize<ArpaLojaListResponse>(
            """{"status":"ok","lojas":[{"id":4,"nome":"Avulsa","empresa_id":null,"empresa_nome":"","empresa_cnpj":""}]}""",
            Options)!;

        Assert.Null(Assert.Single(antiga.Lojas!).EmpresaId);
        Assert.Null(Assert.Single(semEmpresa.Lojas!).EmpresaId);
    }
}
