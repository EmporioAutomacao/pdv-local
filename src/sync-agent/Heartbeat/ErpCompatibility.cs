using System.Text.Json.Nodes;
using SyncAgent.Persistence;

namespace SyncAgent.Heartbeat;

/// <summary>
/// O que o ERP declarou aceitar no ultimo heartbeat (<c>accepted_entity_types</c>,
/// contrato Sync 2.19.0). Protege contra agente novo + ERP antigo:
/// <list type="bullet">
/// <item>tipo de entidade novo (<see cref="GatedEntityTypes"/>) so e coletado quando o
/// ERP o aceita - senao viraria dead letter no ERP a cada ciclo;</item>
/// <item>"Preparar views" so aplica as views novas quando o ERP aceita <c>catalogo</c>
/// - as views passam a mandar o codigo do Arpa em especie/condicao/conta, que o ERP
/// antigo gravaria como cadastro novo pelo nome ("17", "26"...).</item>
/// </list>
/// ERP antigo nao manda a lista: tratado como "nao aceita os tipos novos". Antes do
/// primeiro heartbeat bem-sucedido (sem estado gravado) tambem nao aceita.
/// <c>ordem_servico</c> (contrato 2.23.0) tambem e controlado: o ERP antigo recusaria
/// cada OS como entity_type desconhecido.
/// O estado fica no banco local para valer logo apos reiniciar o servico.
/// </summary>
public sealed class ErpCompatibility
{
    public const string StateKey = "erp.compatibility";

    /// <summary>Tipos que o ERP precisa declarar em <c>accepted_entity_types</c>.</summary>
    public static readonly IReadOnlySet<string> GatedEntityTypes =
        new HashSet<string>(StringComparer.Ordinal) { "catalogo", "ordem_servico" };

    private readonly LocalSyncStore _store;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _loaded;
    private bool _known;
    private HashSet<string>? _accepted;

    public ErpCompatibility(LocalSyncStore store)
    {
        _store = store;
    }

    /// <summary>Regra pura (testavel): pode coletar/usar <paramref name="entityType"/>?</summary>
    public static bool Evaluate(bool known, IReadOnlyCollection<string>? accepted, string entityType)
    {
        if (!GatedEntityTypes.Contains(entityType))
        {
            return true;
        }

        return known && accepted is not null && accepted.Contains(entityType);
    }

    public async Task RecordHeartbeatAsync(IReadOnlyList<string>? acceptedEntityTypes, CancellationToken cancellationToken)
    {
        var accepted = acceptedEntityTypes is null
            ? null
            : new HashSet<string>(acceptedEntityTypes.Where(t => !string.IsNullOrWhiteSpace(t)), StringComparer.Ordinal);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var changed = !_loaded || !_known || !SameSet(_accepted, accepted);
            _known = true;
            _accepted = accepted;
            _loaded = true;
            if (!changed)
            {
                return;
            }

            var state = new JsonObject
            {
                ["known"] = true,
                ["accepted_entity_types"] = accepted is null
                    ? null
                    : new JsonArray(accepted.OrderBy(t => t, StringComparer.Ordinal).Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
                ["updated_at_utc"] = DateTimeOffset.UtcNow.ToString("O"),
            };
            await _store.SetJsonStateAsync(StateKey, state, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> AcceptsAsync(string entityType, CancellationToken cancellationToken)
    {
        if (!GatedEntityTypes.Contains(entityType))
        {
            return true;
        }

        await EnsureLoadedAsync(cancellationToken);
        return Evaluate(_known, _accepted, entityType);
    }

    /// <summary>ERP traduz os codigos do Arpa pelos vinculos da Conexao Arpa (mesma release do catalogo).</summary>
    public Task<bool> SupportsArpaVinculosAsync(CancellationToken cancellationToken) =>
        AcceptsAsync("catalogo", cancellationToken);

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_loaded)
            {
                return;
            }

            var state = await _store.GetJsonStateAsync(StateKey, cancellationToken);
            _known = state?["known"]?.GetValue<bool>() ?? false;
            _accepted = state?["accepted_entity_types"] is JsonArray array
                ? new HashSet<string>(array.Select(n => n?.GetValue<string>() ?? string.Empty).Where(t => t.Length > 0), StringComparer.Ordinal)
                : null;
            _loaded = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static bool SameSet(HashSet<string>? a, HashSet<string>? b) =>
        (a is null && b is null) || (a is not null && b is not null && a.SetEquals(b));
}
