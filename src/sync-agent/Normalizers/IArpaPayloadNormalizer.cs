using System.Text.Json.Nodes;

namespace SyncAgent.Normalizers;

public interface IArpaPayloadNormalizer
{
    string EntityType { get; }

    JsonObject Normalize(string entityKey, JsonObject sourcePayload);
}
