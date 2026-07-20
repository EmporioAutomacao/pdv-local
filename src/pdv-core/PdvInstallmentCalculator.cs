namespace PdvLocal.Core;

public sealed record PdvInstallmentPlanEntry(int Number, DateOnly DueDate, decimal Amount);

/// <summary>
/// Calculo e validacao do plano de parcelas de um pagamento a prazo, a partir
/// da condicao de pagamento (installments/first_due_days/interval_days).
/// Logica pura, sem I/O, para ser testavel e reutilizavel na UI e na gravacao.
/// </summary>
public static class PdvInstallmentCalculator
{
    /// <summary>
    /// Monta o plano padrao: rateio do total em N parcelas com 2 casas
    /// decimais e o residuo do arredondamento na ultima parcela; vencimentos a
    /// partir da data da venda (saleDate + firstDueDays, depois somando
    /// intervalDays por parcela).
    /// </summary>
    public static IReadOnlyList<PdvInstallmentPlanEntry> BuildPlan(
        decimal totalAmount,
        int installments,
        int firstDueDays,
        int intervalDays,
        DateOnly saleDate)
    {
        if (totalAmount <= 0)
        {
            throw new ArgumentException("Total do pagamento deve ser maior que zero.", nameof(totalAmount));
        }

        if (installments < 1)
        {
            throw new ArgumentException("Quantidade de parcelas deve ser maior que zero.", nameof(installments));
        }

        if (firstDueDays < 0 || intervalDays < 0)
        {
            throw new ArgumentException("Dias de vencimento nao podem ser negativos.");
        }

        var baseAmount = Math.Round(totalAmount / installments, 2, MidpointRounding.ToEven);
        var entries = new List<PdvInstallmentPlanEntry>(installments);
        var accumulated = 0m;

        for (var number = 1; number <= installments; number++)
        {
            var amount = number == installments
                ? totalAmount - accumulated
                : baseAmount;
            accumulated += amount;

            var dueDate = saleDate.AddDays(firstDueDays + (number - 1) * intervalDays);
            entries.Add(new PdvInstallmentPlanEntry(number, dueDate, amount));
        }

        return entries;
    }

    /// <summary>
    /// Valida um plano (possivelmente editado pelo operador): numeracao 1..N,
    /// soma igual ao total do pagamento, valores positivos e datas nao
    /// decrescentes. Lanca ArgumentException com mensagem em pt-BR.
    /// </summary>
    public static void ValidatePlan(
        IReadOnlyList<PdvInstallmentPlanEntry> plan,
        decimal expectedTotal)
    {
        if (plan.Count == 0)
        {
            throw new ArgumentException("Plano de parcelas vazio.");
        }

        var sum = 0m;
        DateOnly? previousDueDate = null;
        for (var index = 0; index < plan.Count; index++)
        {
            var entry = plan[index];
            if (entry.Number != index + 1)
            {
                throw new ArgumentException($"Numeracao de parcelas invalida na posicao {index + 1}.");
            }

            if (entry.Amount <= 0)
            {
                throw new ArgumentException($"Valor da parcela {entry.Number} deve ser maior que zero.");
            }

            if (previousDueDate is { } previous && entry.DueDate < previous)
            {
                throw new ArgumentException($"Vencimento da parcela {entry.Number} nao pode ser anterior ao da parcela {entry.Number - 1}.");
            }

            previousDueDate = entry.DueDate;
            sum += entry.Amount;
        }

        if (sum != expectedTotal)
        {
            throw new ArgumentException(
                $"Soma das parcelas ({sum:0.00}) difere do valor do pagamento ({expectedTotal:0.00}).");
        }
    }
}
