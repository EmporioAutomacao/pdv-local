namespace SyncAgent.Configuration;

public sealed class PdvSalesPublisherOptions
{
    public const string SectionName = "PdvSalesPublisher";

    public bool Enabled { get; set; } = true;

    public int BatchSize { get; set; } = 50;
}
