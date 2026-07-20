namespace PdvLocal.Core;

/// <summary>
/// Fechamento cego de caixa: casa a contagem do operador (por especie, sem ver
/// o esperado) com o resumo da sessao e resolve esperado/diferenca por
/// especie. Logica pura, sem I/O.
/// </summary>
public static class PdvCashClosing
{
    /// <summary>
    /// Une a contagem do operador com o resumo da sessao. Especies de dinheiro
    /// (kind cash) tem como esperado o dinheiro fisico do caixa (abertura +
    /// vendas em dinheiro + suprimentos - sangrias); as demais, o total vendido
    /// naquela especie. Especies vendidas e nao contadas entram com contado 0.
    /// </summary>
    public static IReadOnlyList<PdvClosingCountEntry> BuildClosingCounts(
        IReadOnlyList<PdvClosingCount> counted,
        PdvCashSessionSummary summary)
    {
        var entries = new List<PdvClosingCountEntry>();
        var matchedSummaryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cashExpectedAssigned = false;

        foreach (var count in counted)
        {
            if (string.IsNullOrWhiteSpace(count.SpeciesName))
            {
                throw new ArgumentException("Nome da especie na contagem de fechamento e obrigatorio.");
            }

            if (count.CountedAmount < 0)
            {
                throw new ArgumentException($"Contagem da especie {count.SpeciesName} nao pode ser negativa.");
            }

            decimal expected;
            if (IsCashKind(count.Kind))
            {
                // O dinheiro fisico esperado e um so para o caixa; se o
                // operador contar mais de uma especie cash, o esperado fica na
                // primeira e as demais comparam contra zero.
                expected = cashExpectedAssigned ? 0 : summary.ExpectedCashAmount;
                cashExpectedAssigned = true;
                foreach (var species in summary.PaymentSpecies)
                {
                    if (IsCashKind(species.Kind))
                    {
                        matchedSummaryNames.Add(species.SpeciesName.Trim());
                    }
                }
            }
            else
            {
                var match = summary.PaymentSpecies.FirstOrDefault(species =>
                    string.Equals(species.SpeciesName.Trim(), count.SpeciesName.Trim(), StringComparison.OrdinalIgnoreCase));
                expected = match?.Amount ?? 0;
                if (match is not null)
                {
                    matchedSummaryNames.Add(match.SpeciesName.Trim());
                }
            }

            entries.Add(new PdvClosingCountEntry(
                count.SpeciesName.Trim(),
                NormalizeKind(count.Kind),
                count.CountedAmount,
                expected));
        }

        // Especies com venda registrada que o operador nao contou: diferenca
        // integral revelada no relatorio (contado 0).
        foreach (var species in summary.PaymentSpecies)
        {
            if (matchedSummaryNames.Contains(species.SpeciesName.Trim()))
            {
                continue;
            }

            entries.Add(new PdvClosingCountEntry(
                species.SpeciesName.Trim(),
                NormalizeKind(species.Kind),
                0,
                IsCashKind(species.Kind) && !cashExpectedAssigned
                    ? summary.ExpectedCashAmount
                    : species.Amount));
        }

        return entries;
    }

    private static bool IsCashKind(string? kind)
    {
        return string.Equals(kind?.Trim(), "cash", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeKind(string? kind)
    {
        return string.IsNullOrWhiteSpace(kind) ? "other" : kind.Trim().ToLowerInvariant();
    }
}
