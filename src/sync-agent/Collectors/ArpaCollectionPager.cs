namespace SyncAgent.Collectors;

/// <summary>
/// Le uma pagina da view entre <c>after</c> (exclusivo) e o limite superior
/// (<c>toUtc</c>, <c>toKey</c>; <c>toKey</c> nulo = qualquer chave em <c>toUtc</c>).
/// </summary>
internal delegate Task<IReadOnlyList<ArpaCollectorRow>> ArpaPageReader(
    ArpaCollectionCursor after,
    DateTimeOffset toUtc,
    string? toKey,
    int limit,
    CancellationToken cancellationToken);

internal sealed record ArpaCollectionPagerResult(
    ArpaCollectionCursor Position,
    bool PositionChanged,
    bool DiscardedFutureState,
    bool CaughtUp,
    int Collected,
    int Reread,
    int Inserted);

/// <summary>
/// Laco de coleta de uma entidade do Arpa (alcance + releitura), sem banco, para
/// poder ser testado. Regras em <see cref="ArpaCollectionCursor"/>.
/// </summary>
internal static class ArpaCollectionPager
{
    public static async Task<ArpaCollectionPagerResult> RunAsync(
        ArpaCollectionCursor saved,
        DateTimeOffset nowUtc,
        int batchSize,
        ArpaPageReader readPage,
        Func<ArpaCollectorRow, Task<bool>> enqueue,
        CancellationToken cancellationToken)
    {
        if (batchSize <= 0)
        {
            batchSize = 1;
        }

        var discardedFuture = saved.IsInFuture(nowUtc);
        var position = discardedFuture ? ArpaCollectionCursor.Initial : saved;
        var collected = 0;
        var inserted = 0;

        // 1. Alcance: da posicao ate agora, por paginas.
        var caughtUp = false;
        for (var page = 0; page < ArpaCollectionCursor.MaxPagesPerCycle; page++)
        {
            var rows = await readPage(position, nowUtc, null, batchSize, cancellationToken);
            var beyond = false;
            foreach (var row in rows)
            {
                // Defesa para query que nao respeita os limites (ex.: query antiga
                // de appsettings so com @watermark_utc): linha fora da janela nao e
                // enviada nem move a posicao. Como a leitura e ordenada, o resto da
                // pagina tambem esta fora.
                if (IsAfter(row, nowUtc, null))
                {
                    beyond = true;
                    break;
                }

                collected++;
                if (await enqueue(row))
                {
                    inserted++;
                }

                position = new ArpaCollectionCursor(row.OccurredAtUtc, row.EntityKey);
            }

            if (beyond || rows.Count < batchSize)
            {
                caughtUp = true;
                break;
            }
        }

        // 2. Releitura da janela recente ate a posicao (linha lancada depois com a
        // mesma data e chave menor). So depois de alcancar o fim.
        var reread = 0;
        var overlapStart = caughtUp ? position.OverlapStart(nowUtc) : null;
        if (overlapStart is not null)
        {
            var from = overlapStart;
            for (var page = 0; page < ArpaCollectionCursor.MaxPagesPerCycle; page++)
            {
                var rows = await readPage(from, position.OccurredAtUtc, position.EntityKey, batchSize, cancellationToken);
                var beyond = false;
                foreach (var row in rows)
                {
                    if (IsAfter(row, position.OccurredAtUtc, position.EntityKey))
                    {
                        beyond = true;
                        break;
                    }

                    reread++;
                    if (await enqueue(row))
                    {
                        inserted++;
                    }

                    from = new ArpaCollectionCursor(row.OccurredAtUtc, row.EntityKey);
                }

                if (beyond || rows.Count < batchSize)
                {
                    break;
                }
            }
        }

        return new ArpaCollectionPagerResult(
            position,
            position != saved,
            discardedFuture,
            caughtUp,
            collected,
            reread,
            inserted);
    }

    // Ordinal = COLLATE "C" da query (ArpaStandardEntities): mesma ordem do banco,
    // seja qual for a collation dele. Sem isso a ordem divergiria e o laco travaria.
    private static bool IsAfter(ArpaCollectorRow row, DateTimeOffset toUtc, string? toKey)
    {
        var cmp = row.OccurredAtUtc.CompareTo(toUtc);
        return cmp > 0 || (cmp == 0 && toKey is not null && string.CompareOrdinal(row.EntityKey, toKey) > 0);
    }
}
