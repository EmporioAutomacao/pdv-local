using SyncAgent.Heartbeat;

namespace SyncAgent.Update;

/// <summary>
/// Insere a confirmacao/timeout/agendamento na frente de
/// <see cref="SelfUpdater.ApplyIfNeededAsync"/>. Quando
/// <c>confirmation_mode</c> e "auto" (ausente, ou ERP/agente antigo), o
/// comportamento e identico ao de sempre: aplica na hora, sem aviso. Quando e
/// "confirm", so aplica quando o usuario confirmar (<see cref="ConfirmNowAsync"/>,
/// via bandeja), quando o horario agendado chegar, ou quando o prazo padrao de
/// 5 min (<c>deadline_at</c>, definido pelo ERP a partir do primeiro
/// <c>update:ack action=presented</c>) vencer.
/// </summary>
public sealed class PendingUpdateConfirmationGate
{
    private readonly ILogger<PendingUpdateConfirmationGate> _logger;
    private readonly SelfUpdater _selfUpdater;
    private readonly PendingUpdateConfirmationState _stateStore;
    private readonly ErpUpdateAckClient _ackClient;

    private PendingUpdateCommand? _lastCommand;

    public PendingUpdateConfirmationGate(
        ILogger<PendingUpdateConfirmationGate> logger,
        SelfUpdater selfUpdater,
        PendingUpdateConfirmationState stateStore,
        ErpUpdateAckClient ackClient)
    {
        _logger = logger;
        _selfUpdater = selfUpdater;
        _stateStore = stateStore;
        _ackClient = ackClient;
    }

    /// <summary>Ultimo estado calculado por <see cref="EvaluateAsync"/>, lido pelo
    /// LocalApi para expor a bandeja. Null quando nao ha nada aguardando escolha
    /// do usuario (modo auto, ou ja aplicado/consumido).</summary>
    public PendingUpdateConfirmationView? CurrentView { get; private set; }

    public async Task<bool> EvaluateAsync(PendingUpdateCommand pendingUpdate, CancellationToken cancellationToken)
    {
        _lastCommand = pendingUpdate;

        var mode = pendingUpdate.ConfirmationMode ?? "auto";
        if (!string.Equals(mode, "confirm", StringComparison.OrdinalIgnoreCase))
        {
            CurrentView = null;
            return await _selfUpdater.ApplyIfNeededAsync(pendingUpdate, cancellationToken);
        }

        var snapshot = _stateStore.LoadOrReset(pendingUpdate.Version);
        if (!snapshot.PresentedAckSent)
        {
            var now = DateTimeOffset.UtcNow;
            snapshot = snapshot with
            {
                PresentedAckSent = true,
                PresentedAtUtc = snapshot.PresentedAtUtc ?? now,
                LocalDeadlineUtc = snapshot.LocalDeadlineUtc ?? now.AddSeconds(300),
            };
            _stateStore.Save(snapshot);
            _ = _ackClient.AckAsync("presented", null, cancellationToken);
            _logger.LogInformation(
                "Atualizacao pendente v{Version} apresentada ao usuario local; prazo local ate {Deadline}.",
                pendingUpdate.Version, snapshot.LocalDeadlineUtc);
        }

        var effectiveDeadline = pendingUpdate.DeadlineAt ?? snapshot.LocalDeadlineUtc;
        var effectiveScheduled = pendingUpdate.ScheduledAt ?? snapshot.LocalScheduledAtUtc;
        var nowUtc = DateTimeOffset.UtcNow;

        var shouldApply = snapshot.UserChoice == "confirmed"
            || (effectiveScheduled is { } scheduledAt && nowUtc >= scheduledAt)
            || (effectiveScheduled is null && effectiveDeadline is { } deadline && nowUtc >= deadline);

        if (shouldApply)
        {
            CurrentView = null;
            return await _selfUpdater.ApplyIfNeededAsync(pendingUpdate, cancellationToken);
        }

        CurrentView = new PendingUpdateConfirmationView(
            pendingUpdate.Version, pendingUpdate.ReleaseNotes, effectiveDeadline, effectiveScheduled);
        return false;
    }

    /// <summary>Usuario clicou "Atualizar agora" na bandeja. Aplica imediatamente,
    /// sem esperar o proximo heartbeat.</summary>
    public async Task<bool> ConfirmNowAsync(CancellationToken cancellationToken)
    {
        if (_lastCommand is null)
        {
            return false;
        }

        var snapshot = _stateStore.LoadOrReset(_lastCommand.Version) with { UserChoice = "confirmed" };
        _stateStore.Save(snapshot);
        _ = _ackClient.AckAsync("confirmed", null, cancellationToken);
        CurrentView = null;
        return await _selfUpdater.ApplyIfNeededAsync(_lastCommand, cancellationToken);
    }

    /// <summary>Usuario escolheu um horario melhor na bandeja.</summary>
    public async Task<bool> ScheduleAsync(DateTimeOffset scheduledAt, CancellationToken cancellationToken)
    {
        if (_lastCommand is null)
        {
            return false;
        }

        var snapshot = _stateStore.LoadOrReset(_lastCommand.Version) with { LocalScheduledAtUtc = scheduledAt };
        _stateStore.Save(snapshot);
        await _ackClient.AckAsync("scheduled", scheduledAt, cancellationToken);

        if (CurrentView is not null)
        {
            CurrentView = CurrentView with { ScheduledAtUtc = scheduledAt };
        }

        return true;
    }
}

public sealed record PendingUpdateConfirmationView(
    string Version,
    string? ReleaseNotes,
    DateTimeOffset? DeadlineAtUtc,
    DateTimeOffset? ScheduledAtUtc);
