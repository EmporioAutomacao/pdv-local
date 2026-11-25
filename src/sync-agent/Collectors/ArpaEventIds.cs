using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SyncAgent.Utilities;

namespace SyncAgent.Collectors;

/// <summary>
/// event_id de uma linha lida pelo coletor Arpa. O outbox (PK event_id, ON CONFLICT
/// DO NOTHING) e o ERP (event_id unico) descartam o que ja viram.
///
/// O hash do payload entra no seed: reler a mesma linha sem mudanca gera o mesmo
/// event_id (nao reenvia), mas uma mudanca com o mesmo occurred_at (ex.: titulo
/// quitado no mesmo dia da venda; colunas date do Arpa) gera evento novo. Sem o
/// hash, essa mudanca era descartada como repetida e nunca chegava ao ERP.
/// </summary>
internal static class ArpaEventIds
{
    public static Guid ForCollectedRow(
        string instanceId,
        string entityType,
        string entityKey,
        DateTimeOffset occurredAtUtc,
        JsonObject normalizedPayload,
        int resyncGeneration)
    {
        var parts = new List<string>
        {
            "sync-agent:v1",
            "arpa",
            instanceId,
            entityType,
            entityKey,
            occurredAtUtc.ToString("O"),
            "payload-" + PayloadHash(normalizedPayload),
        };

        // Geracao > 0 entra no seed, igual ao resync sob demanda (ArpaResyncProcessor).
        // O job de "Sincronizar tudo" usa uma geracao nova a cada pedido: sem isso
        // re-leria o periodo com o mesmo event_id do envio anterior e o outbox
        // descartaria sem reenviar. A coleta incremental usa uma geracao fixa
        // (LocalSyncStore.GetArpaIncrementalGenerationAsync), que nao muda com o reenvio.
        if (resyncGeneration > 0)
        {
            parts.Add($"resync-gen-{resyncGeneration}");
        }

        return DeterministicGuid.Create([.. parts]);
    }

    private static string PayloadHash(JsonObject payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToJsonString())));
}
