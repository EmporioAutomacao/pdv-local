namespace SyncAgent.Configuration;

public sealed class SyncAgentOptions
{
    public const string SectionName = "SyncAgent";

    public string InstanceId { get; init; } = "local-dev-agent-01";

    public string ErpTenantId { get; init; } = "local-dev";

    public string ErpApiBaseUrl { get; init; } = "https://localhost:5001";

    public string AgentVersion { get; init; } = "0.1.0-dev";

    public int PollingIntervalSeconds { get; init; } = 30;

    public int LocalStatusPort { get; init; } = 47891;
}
