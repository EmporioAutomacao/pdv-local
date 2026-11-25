using System.Globalization;
using System.Text.Json.Nodes;

namespace SyncAgent.Collectors;

/// <summary>
/// Reenvio "Sincronizar tudo" de uma conexao Arpa, gravado em
/// <c>collector.arpa.&lt;conn&gt;.resync_job</c> (agent_state). Le o periodo
/// <c>[FromUtc, ToUtc]</c> das entidades escolhidas com cursor proprio, sem mexer
/// no watermark da coleta incremental, e sai com a geracao propria no event_id
/// para o ERP reaplicar.
///
/// Enquanto o job existir a conexao nao aceita outro "Sincronizar tudo" (cada
/// clique gerava um lote novo e distinto dos mesmos dados). Ele so e apagado
/// quando a leitura terminou e nao ha mais evento pendente dessas entidades
/// capturado desde o inicio (dead-letter nao segura), ou pelo botao
/// "Cancelar reenvio".
/// </summary>
public sealed class ArpaResyncJob
{
    public required int Generation { get; init; }

    /// <summary>all | 3m | 1d | custom (so para mensagens).</summary>
    public required string Preset { get; init; }

    public required DateTimeOffset FromUtc { get; init; }

    public required DateTimeOffset ToUtc { get; init; }

    public required IReadOnlyList<string> EntityTypes { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public Dictionary<string, ArpaCollectionCursor> Cursors { get; init; } = new(StringComparer.Ordinal);

    public HashSet<string> DoneEntities { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Falhas seguidas de leitura por entidade (zera quando uma leitura da certo).</summary>
    public Dictionary<string, int> FailureCounts { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Entidades das quais o reenvio desistiu, com o ultimo erro (tambem em <see cref="DoneEntities"/>).</summary>
    public Dictionary<string, string> FailedEntities { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Inicio da sequencia atual de falhas por entidade.</summary>
    public Dictionary<string, DateTimeOffset> FirstFailureAtUtc { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Falhas seguidas de uma entidade ate o reenvio desistir dela e poder terminar...</summary>
    public const int MaxFailures = 3;

    /// <summary>...desde que a sequencia de falhas dure pelo menos isto: uma queda curta de
    /// rede com o Arpa (3 ciclos de ~30s) nao pode fazer o reenvio desistir.</summary>
    public static readonly TimeSpan MinFailureSpan = TimeSpan.FromMinutes(30);

    /// <summary>Registra uma falha da entidade; true quando o reenvio deve desistir dela.</summary>
    public bool RegisterFailure(string entityType, string reason, DateTimeOffset nowUtc)
    {
        var failures = FailureCounts.GetValueOrDefault(entityType) + 1;
        FailureCounts[entityType] = failures;
        if (!FirstFailureAtUtc.ContainsKey(entityType))
        {
            FirstFailureAtUtc[entityType] = nowUtc;
        }

        if (failures < MaxFailures || nowUtc - FirstFailureAtUtc[entityType] < MinFailureSpan)
        {
            return false;
        }

        DoneEntities.Add(entityType);
        FailedEntities[entityType] = reason;
        return true;
    }

    /// <summary>Leitura da entidade deu certo: zera a sequencia de falhas.</summary>
    public void RegisterSuccess(string entityType)
    {
        FailureCounts.Remove(entityType);
        FirstFailureAtUtc.Remove(entityType);
    }

    public DateTimeOffset? ReadingDoneAtUtc { get; set; }

    public bool ReadingDone => ReadingDoneAtUtc is not null;

    /// <summary>Posicao de leitura da entidade; comeca logo antes de <see cref="FromUtc"/>.</summary>
    public ArpaCollectionCursor CursorFor(string entityType) =>
        Cursors.TryGetValue(entityType, out var cursor) ? cursor : new ArpaCollectionCursor(FromUtc, string.Empty);

    /// <summary>Uma das datas de inicio do periodo. Preset fora da lista = "all".</summary>
    /// <param name="sinceDate">Data inicial (custom).</param>
    /// <param name="untilDate">Data final (custom), inclusiva.</param>
    /// <param name="timeZone">Fuso das datas do formulario (o da maquina do agente);
    /// coluna <c>date</c> do Arpa cai na meia-noite local.</param>
    public static (DateTimeOffset FromUtc, DateTimeOffset ToUtc) ResolvePeriod(
        string? preset,
        DateOnly? sinceDate,
        DateOnly? untilDate,
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone)
    {
        // Presets comecam na meia-noite local: as colunas date do Arpa caem nela, e uma
        // janela movel (agora - 1 dia) deixava de fora as vendas de ontem.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, timeZone).DateTime);
        switch (preset)
        {
            case "3m":
                return (LocalMidnightUtc(today.AddMonths(-3), timeZone), nowUtc);
            case "1d":
                return (LocalMidnightUtc(today.AddDays(-1), timeZone), nowUtc);
            case "custom":
                if (sinceDate is null || untilDate is null)
                {
                    throw new ArgumentException("Informe a data inicial e a data final.");
                }

                if (sinceDate > untilDate)
                {
                    throw new ArgumentException("A data inicial deve ser anterior ou igual a data final.");
                }

                var from = LocalMidnightUtc(sinceDate.Value, timeZone);
                if (from > nowUtc)
                {
                    throw new ArgumentException("A data inicial esta no futuro.");
                }

                // Fim inclusivo: ate o ultimo microssegundo do dia (precisao do timestamptz).
                var to = LocalMidnightUtc(untilDate.Value.AddDays(1), timeZone).AddTicks(-10);
                return (from, to > nowUtc ? nowUtc : to);
            default:
                return (DateTimeOffset.UnixEpoch, nowUtc);
        }
    }

    private static DateTimeOffset LocalMidnightUtc(DateOnly date, TimeZoneInfo timeZone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        // Fuso com horario de verao que comeca a meia-noite (o Brasil ate 2019): a
        // meia-noite nao existe nesse dia e ConvertTimeToUtc lancaria. O dia comeca
        // no primeiro instante valido.
        while (timeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero);
    }

    /// <summary>Texto curto do periodo para log e mensagens ("01/11/2026 a 10/11/2026").</summary>
    public string PeriodLabel(TimeZoneInfo timeZone)
    {
        if (Preset == "all")
        {
            return "todo o historico";
        }

        var from = TimeZoneInfo.ConvertTime(FromUtc, timeZone);
        var to = TimeZoneInfo.ConvertTime(ToUtc, timeZone);
        return $"{from:dd/MM/yyyy} a {to:dd/MM/yyyy}";
    }

    public JsonObject ToState()
    {
        var cursors = new JsonObject();
        foreach (var (entityType, cursor) in Cursors.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            cursors[entityType] = cursor.ToState();
        }

        return new JsonObject
        {
            ["generation"] = Generation,
            ["preset"] = Preset,
            ["from_utc"] = Format(FromUtc),
            ["to_utc"] = Format(ToUtc),
            ["entity_types"] = new JsonArray(EntityTypes.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()),
            ["started_at_utc"] = Format(StartedAtUtc),
            ["reading_done_at_utc"] = ReadingDoneAtUtc is null ? null : Format(ReadingDoneAtUtc.Value),
            ["cursors"] = cursors,
            ["done_entities"] = new JsonArray(DoneEntities.Order(StringComparer.Ordinal).Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()),
            ["failure_counts"] = ToObject(FailureCounts, v => JsonValue.Create(v)),
            ["failed_entities"] = ToObject(FailedEntities, v => JsonValue.Create(v)),
            ["first_failure_at_utc"] = ToObject(FirstFailureAtUtc, v => JsonValue.Create(Format(v))),
        };
    }

    /// <summary>Estado gravado, ou null quando ausente/ilegivel (inclusive campo com tipo
    /// errado: estado corrompido nao pode quebrar a coleta nem a tela).</summary>
    public static ArpaResyncJob? FromState(JsonObject? state)
    {
        try
        {
            return Parse(state);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static ArpaResyncJob? Parse(JsonObject? state)
    {
        if (state is null
            || state["generation"] is not JsonValue generationNode
            || !generationNode.TryGetValue<int>(out var generation)
            || !TryParse(state["from_utc"], out var fromUtc)
            || !TryParse(state["to_utc"], out var toUtc)
            || !TryParse(state["started_at_utc"], out var startedAtUtc))
        {
            return null;
        }

        var job = new ArpaResyncJob
        {
            Generation = generation,
            Preset = state["preset"]?.GetValue<string>() ?? "all",
            FromUtc = fromUtc,
            ToUtc = toUtc,
            StartedAtUtc = startedAtUtc,
            EntityTypes = Strings(state["entity_types"]),
            ReadingDoneAtUtc = TryParse(state["reading_done_at_utc"], out var done) ? done : null,
        };

        if (state["cursors"] is JsonObject cursors)
        {
            foreach (var (entityType, node) in cursors)
            {
                if (node is JsonObject cursorState)
                {
                    job.Cursors[entityType] = ArpaCollectionCursor.FromState(cursorState);
                }
            }
        }

        foreach (var entityType in Strings(state["done_entities"]))
        {
            job.DoneEntities.Add(entityType);
        }

        if (state["failure_counts"] is JsonObject failureCounts)
        {
            foreach (var (entityType, node) in failureCounts)
            {
                if (node is JsonValue v && v.TryGetValue<int>(out var count))
                {
                    job.FailureCounts[entityType] = count;
                }
            }
        }

        if (state["first_failure_at_utc"] is JsonObject firstFailures)
        {
            foreach (var (entityType, node) in firstFailures)
            {
                if (TryParse(node, out var at))
                {
                    job.FirstFailureAtUtc[entityType] = at;
                }
            }
        }

        if (state["failed_entities"] is JsonObject failed)
        {
            foreach (var (entityType, node) in failed)
            {
                if (node is JsonValue v && v.TryGetValue<string>(out var reason))
                {
                    job.FailedEntities[entityType] = reason;
                }
            }
        }

        return job;
    }

    private static JsonObject ToObject<T>(Dictionary<string, T> values, Func<T, JsonNode?> convert)
    {
        var result = new JsonObject();
        foreach (var (key, value) in values.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            result[key] = convert(value);
        }

        return result;
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static bool TryParse(JsonNode? node, out DateTimeOffset value)
    {
        value = default;
        if (node is not JsonValue v
            || !v.TryGetValue<string>(out var text)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return false;
        }

        value = parsed.ToUniversalTime();
        return true;
    }

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray array
            ? array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToList()
            : [];
}
