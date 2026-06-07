namespace SyncAgent.Configuration;

public sealed class ErpPdvSnapshotOptions
{
    public const string SectionName = "ErpPdvSnapshot";

    public bool Enabled { get; set; }

    public int TimeoutSeconds { get; set; } = 30;

    public int Limit { get; set; } = 5000;
}
