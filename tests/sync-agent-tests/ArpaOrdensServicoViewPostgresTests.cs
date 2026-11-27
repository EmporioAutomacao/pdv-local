using System.Text.Json;
using Npgsql;
using SyncAgent.Provisioning;
using Xunit;

namespace SyncAgent.Tests;

/// <summary>
/// sync_export.ordens_servico (contrato 2.23.0) contra um Postgres de verdade, com as
/// tabelas de OS do Arpa de Anapolis (levantadas em 2026-11-26). So roda com
/// SYNCAGENT_TEST_PG (o usuario precisa poder criar banco); cada caso cria e apaga
/// um banco proprio.
/// </summary>
public sealed class ArpaOrdensServicoViewPostgresTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("SYNCAGENT_TEST_PG");

    // Colunas reais de Anapolis (subconjunto usado pela view + algumas a mais).
    private const string AnapolisDdl = """
        CREATE TABLE clientes(codigo int, nome varchar(100), cpf_cnpj varchar(20));
        CREATE TABLE vendedores(codigo int, nome varchar(100), cpf varchar(14), status boolean);
        CREATE TABLE servicos(codigo int, responsavel varchar(30), entrega date, status varchar(1), cliente int,
            abertura date, operador int, usuario_cancelamento int, hora_entrega time, hora_abertura time, obs int);
        CREATE TABLE objetos_servicos(codigo int, cod_servicos int, objeto varchar(100), problema varchar(5000),
            tecnico int, marca varchar(50), modelo varchar(50), serie varchar(30), cor varchar(20), patrimonio varchar(20),
            id varchar(20), constatado varchar(5000), km varchar(20), hora_recebida time, hora_entrega time,
            situacao varchar(20), data_entrega date, acessorios varchar(1000), situacao_anterior varchar(20),
            data_hora_finalizacao timestamp);
        CREATE TABLE os_servicos(codigo int, cd_objeto int, cd_produto int, cd_vendedor int, descricao varchar(120),
            unitario numeric, quantidade numeric, comissao numeric, precocusto numeric, data_hora_lancamento timestamp);
        CREATE TABLE os_servicos_pecas(cd_item int, desconto numeric, garantia varchar(50), tipo_desconto varchar(1), acrescimo numeric);
        CREATE TABLE pos_venda_os(codigo int, cod_item int, data timestamp, usuario int, ndias int);
        CREATE TABLE os_transformada(cod_pedido int, cod_ordem int);
        CREATE TABLE os_autorizada(os int, usuario int, data date);

        INSERT INTO clientes VALUES (1817, 'MOTA COMERCIO', '36.831.485/0001-02'), (2396, 'SUPER VALLE', '69.555.459/0001-58');
        INSERT INTO vendedores VALUES (46, 'ALEXANDRE DIAS', '', true), (5, 'TECNICO CINCO', '', true);
        -- 745: faturada, 1 equipamento, 2 pedidos, garantia por pos_venda_os
        INSERT INTO servicos VALUES (745, NULL, '2026-11-26', 'F', 1817, '2026-11-26', 46, NULL, '12:36:18', '11:00:37', NULL);
        INSERT INTO objetos_servicos VALUES (770, 745, 'PRIX 5 32KG', 'TECLADO FALHANDO', 46, 'TOLEDO', 'PRIX 5E/6', '13308071',
            'PRETO', '', '', 'TROCA DE TECLADO', '0', '10:57:52', '00:00', 'CONCLUÍDO', NULL, 'MARCA REMOVIDA', 'ORÇAMENTO',
            '2026-11-26 12:36:06');
        INSERT INTO os_servicos VALUES (1856, 770, 701, 46, 'TECLADO', 300, 1, 0, 217.81, '2026-11-26 11:01:26'),
                                       (1859, 770, 545, 46, 'SERVICO', 200, 2, 10, 20, '2026-11-26 11:03:23');
        INSERT INTO os_servicos_pecas VALUES (1856, 0, '', 'P', 0), (1859, 10, '3 MESES', 'P', 5);
        INSERT INTO pos_venda_os VALUES (598, 1856, '2026-11-26 11:03:23', 46, 365);
        INSERT INTO os_transformada VALUES (1966, 745), (1967, 745);
        -- 746: aberta, 2 equipamentos (um sem item), desconto em valor, aprovada
        INSERT INTO servicos VALUES (746, 'JOAO', NULL, 'A', 2396, '2026-11-20', 5, NULL, NULL, '08:00', NULL);
        INSERT INTO objetos_servicos VALUES (771, 746, 'PDV01', 'NAO LIGA', 5, '', '', '', '', 'PAT-9', '', '', '0',
            '07:59:00', '00:00', 'EM ANDAMENTO', NULL, '', 'ORÇAMENTO', NULL),
            (772, 746, 'IMPRESSORA', 'SEM PAPEL', 46, 'ELGIN', 'I9', 'X1', '', '', '', '', '0', '08:01:00', '00:00',
            'ORÇAMENTO', NULL, '', '', NULL);
        INSERT INTO os_servicos VALUES (1860, 771, 914, 5, 'DESLOCAMENTO', 220, 1, 0, 60, '2026-11-20 08:10:00');
        INSERT INTO os_servicos_pecas VALUES (1860, 15, '', 'V', 0);
        INSERT INTO os_autorizada VALUES (746, 1, '2026-11-21');
        """;

    private static string ViewsScript()
    {
        using var stream = typeof(ArpaDdlRunner).Assembly.GetManifestResourceStream("SyncAgent.Arpa.sync-export-views.sql")
            ?? throw new InvalidOperationException("script de views nao embutido");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task<Dictionary<string, (DateTime Occurred, JsonElement Payload)>> OrdensAsync(string ddl, List<string> notices)
    {
        var db = "views_os_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = new NpgsqlConnection(ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {db}", admin);
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            var csb = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = db, Pooling = false };
            await using var conn = new NpgsqlConnection(csb.ConnectionString);
            conn.Notice += (_, e) => notices.Add(e.Notice.MessageText);
            await conn.OpenAsync();
            await using (var cmd = new NpgsqlCommand(ddl, conn)) await cmd.ExecuteNonQueryAsync();
            await using (var cmd = new NpgsqlCommand(ViewsScript(), conn)) await cmd.ExecuteNonQueryAsync();

            var result = new Dictionary<string, (DateTime, JsonElement)>();
            await using var exists = new NpgsqlCommand("SELECT to_regclass('sync_export.ordens_servico') IS NOT NULL", conn);
            if (!(bool)(await exists.ExecuteScalarAsync() ?? false)) return result;

            await using var read = new NpgsqlCommand("SELECT entity_key, occurred_at_utc, payload_json FROM sync_export.ordens_servico", conn);
            await using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                using var doc = JsonDocument.Parse(reader.GetString(2));
                result[reader.GetString(0)] = (reader.GetDateTime(1).ToUniversalTime(), doc.RootElement.Clone());
            }
            return result;
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {db}", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static DateTimeOffset Brt(int d, int h, int m, int s = 0) => new(2026, 11, d, h, m, s, TimeSpan.FromHours(-3));

    [Fact]
    public async Task OS_faturada_traz_equipamento_itens_garantia_e_pedidos()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;

        var notices = new List<string>();
        var os = await OrdensAsync(AnapolisDdl, notices);

        Assert.Contains(notices, n => n.StartsWith("sync_export.ordens_servico criada"));
        Assert.Equal(2, os.Count);

        var p = os["745"].Payload;
        Assert.Equal("745", p.GetProperty("codigo_os_arpa").GetString());
        Assert.Equal("F", p.GetProperty("status").GetString());
        Assert.Equal("36.831.485/0001-02", p.GetProperty("cliente_documento").GetString());
        Assert.Equal("ALEXANDRE DIAS", p.GetProperty("operador_nome").GetString());
        Assert.Equal(new[] { "1966", "1967" }, p.GetProperty("pedidos").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(Brt(26, 11, 0, 37), DateTimeOffset.Parse(p.GetProperty("data_abertura").GetString()!));
        Assert.Equal(Brt(26, 12, 36, 6), DateTimeOffset.Parse(p.GetProperty("data_conclusao").GetString()!));
        Assert.Equal(JsonValueKind.Null, p.GetProperty("data_aprovacao").ValueKind);

        var eq = Assert.Single(p.GetProperty("equipamentos").EnumerateArray());
        Assert.Equal("TOLEDO", eq.GetProperty("marca").GetString());
        Assert.Equal("13308071", eq.GetProperty("serie").GetString());
        Assert.Equal("ALEXANDRE DIAS", eq.GetProperty("tecnico_nome").GetString());
        Assert.Equal(Brt(26, 10, 57, 52), DateTimeOffset.Parse(eq.GetProperty("data_recebimento").GetString()!));

        var itens = eq.GetProperty("itens").EnumerateArray().ToList();
        Assert.Equal(2, itens.Count);
        Assert.Equal("701", itens[0].GetProperty("codigo_produto_arpa").GetString());
        Assert.Equal(365, itens[0].GetProperty("garantia_dias").GetInt32());       // pos_venda_os
        Assert.Equal(90, itens[1].GetProperty("garantia_dias").GetInt32());        // "3 MESES"
        Assert.Equal(40m, itens[1].GetProperty("desconto_valor").GetDecimal());    // 10% de 2 x 200
        Assert.Equal(5m, itens[1].GetProperty("acrescimo").GetDecimal());

        // o mais recente entre abertura, entrega, finalizacao e lancamento: entrega 12:36:18
        Assert.Equal(Brt(26, 12, 36, 18).UtcDateTime, os["745"].Occurred);
    }

    [Fact]
    public async Task OS_aberta_com_dois_equipamentos_e_reenviada_no_dia()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;

        var os = await OrdensAsync(AnapolisDdl, new List<string>());

        var p = os["746"].Payload;
        Assert.Equal("A", p.GetProperty("status").GetString());
        Assert.Equal("EM ANDAMENTO", p.GetProperty("situacao").GetString()); // equipamento principal
        Assert.Equal("JOAO", p.GetProperty("entregue_por").GetString());
        Assert.Empty(p.GetProperty("pedidos").EnumerateArray());
        Assert.Equal(Brt(21, 0, 0), DateTimeOffset.Parse(p.GetProperty("data_aprovacao").GetString()!));

        var eqs = p.GetProperty("equipamentos").EnumerateArray().ToList();
        Assert.Equal(2, eqs.Count);
        Assert.Equal("PAT-9", eqs[0].GetProperty("patrimonio").GetString());
        Assert.Equal(15m, Assert.Single(eqs[0].GetProperty("itens").EnumerateArray()).GetProperty("desconto_valor").GetDecimal());
        Assert.Empty(eqs[1].GetProperty("itens").EnumerateArray());

        // aberta: pelo menos o inicio do dia de hoje (situacao muda sem data no Arpa)
        Assert.True(os["746"].Occurred >= DateTime.UtcNow.Date.AddHours(-3).AddDays(-1));
    }

    [Fact]
    public async Task Sem_tabelas_de_OS_a_view_e_pulada_sem_erro()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;

        var notices = new List<string>();
        var os = await OrdensAsync("CREATE TABLE clientes(codigo int, nome varchar(100));", notices);

        Assert.Empty(os);
        Assert.Contains(notices, n => n.StartsWith("sync_export.ordens_servico pulada"));
    }

    [Fact]
    public async Task Sem_tabelas_opcionais_a_OS_ainda_sai()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;

        var ddl = """
            CREATE TABLE servicos(codigo int, cliente int, abertura date, status varchar(1));
            CREATE TABLE objetos_servicos(codigo int, cod_servicos int);
            CREATE TABLE os_servicos(codigo int, cd_objeto int, cd_produto int);
            INSERT INTO servicos VALUES (1, 10, '2026-11-01', 'C');
            INSERT INTO objetos_servicos VALUES (5, 1);
            INSERT INTO os_servicos VALUES (9, 5, 100);
            """;
        var os = await OrdensAsync(ddl, new List<string>());

        var p = os["1"].Payload;
        Assert.Equal("C", p.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, p.GetProperty("cliente_documento").ValueKind);
        var item = Assert.Single(Assert.Single(p.GetProperty("equipamentos").EnumerateArray()).GetProperty("itens").EnumerateArray());
        Assert.Equal("100", item.GetProperty("codigo_produto_arpa").GetString());
        Assert.Equal(Brt(1, 0, 0).UtcDateTime, os["1"].Occurred);
    }
}
