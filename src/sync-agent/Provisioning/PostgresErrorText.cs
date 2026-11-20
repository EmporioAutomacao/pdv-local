using Npgsql;

namespace SyncAgent.Provisioning;

/// <summary>
/// Texto de erro do Postgres para mostrar no dashboard.
///
/// Erros da fase de conexao (senha errada, usuario/banco inexistente, pg_hba)
/// saem do servidor antes de o client_encoding ser combinado: um Postgres em
/// Windows com lc_messages pt_BR manda o texto em WIN1252 e o Npgsql le como
/// UTF-8, entao os acentos ja chegam como U+FFFD ("autentica��o") e nao ha
/// como recuperar. Por isso esses erros usam texto proprio pelo SqlState, e
/// qualquer outro com U+FFFD cai numa mensagem generica com o codigo.
/// </summary>
public static class PostgresErrorText
{
    private const char Replacement = '�';

    public static string Describe(Exception ex) => ex switch
    {
        PostgresException pg => Describe(pg),
        NpgsqlException { InnerException: { } inner } => inner.Message,
        _ => ex.Message,
    };

    public static string Describe(PostgresException pg)
    {
        var known = pg.SqlState switch
        {
            "28P01" => "Senha incorreta para o usuario informado (ou o servidor exige senha e nenhuma foi informada).",
            "28000" => "O Postgres recusou o acesso deste usuario (usuario inexistente, ou o pg_hba.conf nao libera este acesso).",
            "3D000" => "O banco de dados informado nao existe neste servidor.",
            "42501" => "O usuario informado nao tem permissao para esta operacao.",
            "53300" => "O Postgres esta com o limite de conexoes esgotado. Tente de novo em instantes.",
            "57P03" => "O Postgres ainda esta iniciando. Tente de novo em instantes.",
            _ => null,
        };

        if (known is not null)
        {
            return $"{pg.SqlState}: {known}";
        }

        return pg.MessageText.Contains(Replacement)
            ? $"{pg.SqlState}: erro do Postgres (o texto original veio com acentos ilegiveis; consulte o codigo {pg.SqlState})."
            : $"{pg.SqlState}: {pg.MessageText}";
    }
}
