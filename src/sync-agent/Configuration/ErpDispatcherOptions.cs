namespace SyncAgent.Configuration;

public sealed class ErpDispatcherOptions
{
    public const string SectionName = "ErpDispatcher";

    public bool Enabled { get; set; }

    public int BatchSize { get; set; } = 50;

    public int TimeoutSeconds { get; set; } = 30;

    public int MaxAttempts { get; set; } = 8;

    public int InitialBackoffSeconds { get; set; } = 60;

    public int MaxBackoffSeconds { get; set; } = 3600;

    public int InFlightRecoverySeconds { get; set; } = 900;
}
