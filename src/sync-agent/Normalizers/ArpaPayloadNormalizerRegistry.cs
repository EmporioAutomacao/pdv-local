using System.Text.Json.Nodes;

namespace SyncAgent.Normalizers;

public sealed class ArpaPayloadNormalizerRegistry
{
    private readonly IReadOnlyDictionary<string, IArpaPayloadNormalizer> _normalizers;

    public ArpaPayloadNormalizerRegistry(IEnumerable<IArpaPayloadNormalizer> normalizers)
    {
        _normalizers = normalizers.ToDictionary(
            normalizer => normalizer.EntityType,
            StringComparer.Ordinal);
    }

    public JsonObject Normalize(string entityType, string entityKey, JsonObject sourcePayload)
    {
        return _normalizers.TryGetValue(entityType, out var normalizer)
            ? normalizer.Normalize(entityKey, sourcePayload)
            : sourcePayload;
    }
}
