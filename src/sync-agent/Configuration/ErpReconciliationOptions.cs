namespace SyncAgent.Configuration;

public sealed class ErpReconciliationOptions
{
    public const string SectionName = "ErpReconciliation";

    public bool Enabled { get; set; }

    public int TimeoutSeconds { get; set; } = 30;
}
