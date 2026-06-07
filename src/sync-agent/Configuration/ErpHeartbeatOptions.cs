namespace SyncAgent.Configuration;

public sealed class ErpHeartbeatOptions
{
    public const string SectionName = "ErpHeartbeat";

    public bool Enabled { get; set; }

    public int TimeoutSeconds { get; set; } = 15;
}
