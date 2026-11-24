using System.Globalization;
using System.Text.Json.Nodes;

namespace SyncAgent.Collectors;

/// <summary>
/// Posicao de leitura de uma entidade do coletor Arpa: o ultimo
/// <c>(occurred_at_utc, entity_key)</c> ja lido. Fica gravada na mesma chave de
/// estado do watermark antigo (<c>collector.arpa.&lt;conn&gt;.&lt;entidade&gt;.watermark</c>),
/// com o mesmo campo <c>value</c> e mais o campo <c>key</c> - quem so le
/// <c>value</c> (painel, "Sincronizar a partir de") continua funcionando.
///
/// Regras (incidente "financeiro nao chega nas vendas do Arpa", 2026-11):
/// <list type="bullet">
/// <item>Nunca le linha com <c>occurred_at_utc</c> no futuro (fica para quando a hora
/// chegar) e nunca grava posicao no futuro. Antes, uma parcela com vencimento em 2028
/// levava o watermark do financeiro para 2028 e todo titulo novo ou baixa ficava para tras
/// para sempre.</item>
/// <item>Pagina por <c>(occurred_at_utc, entity_key)</c>: linhas com o mesmo instante
/// (colunas <c>date</c> do Arpa = meia-noite, ou o timestamp fixo da carga inicial) nao
/// se perdem na virada do lote.</item>
/// <item>Depois de alcancar o fim, rele a janela dos ultimos <see cref="Overlap"/>
/// ate a posicao: pega linha lancada mais tarde com a mesma data. O <c>event_id</c> e
/// deterministico e o outbox ignora repetido, entao a releitura nao reenvia nada.</item>
/// <item>Posicao gravada no futuro (estado deixado pelo bug acima) e descartada e a
/// entidade e relida desde o inicio, uma vez.</item>
/// </list>
/// </summary>
public sealed record ArpaCollectionCursor(DateTimeOffset OccurredAtUtc, string EntityKey)
{
    public static readonly ArpaCollectionCursor Initial = new(DateTimeOffset.UnixEpoch, string.Empty);

    /// <summary>Janela relida a cada ciclo depois de alcancar o fim.</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromDays(2);

    /// <summary>Paginas lidas por entidade em um ciclo (alcance e releitura, cada um).
    /// O restante continua no proximo ciclo, a partir da posicao gravada.</summary>
    public const int MaxPagesPerCycle = 10;

    public static ArpaCollectionCursor FromState(JsonObject? state)
    {
        if (state is null)
        {
            return Initial;
        }

        var text = state["value"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var occurred))
        {
            return Initial;
        }

        var key = state["key"]?.GetValue<string>() ?? string.Empty;
        return new ArpaCollectionCursor(occurred.ToUniversalTime(), key);
    }

    public JsonObject ToState() => new()
    {
        ["value"] = OccurredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        ["key"] = EntityKey,
    };

    public bool IsInFuture(DateTimeOffset nowUtc) => OccurredAtUtc > nowUtc;

    /// <summary>Inicio da releitura, ou null quando a posicao ja e mais antiga que a
    /// janela (ai tudo o que esta na janela e mais novo que a posicao e o proprio
    /// alcance le).</summary>
    public ArpaCollectionCursor? OverlapStart(DateTimeOffset nowUtc)
    {
        var from = nowUtc - Overlap;
        return OccurredAtUtc > from ? new ArpaCollectionCursor(from, string.Empty) : null;
    }
}
