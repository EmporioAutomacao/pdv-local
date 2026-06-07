namespace SyncAgent.Configuration;

public sealed class ArpaCollectorOptions
{
    public const string SectionName = "ArpaCollector";

    public bool Enabled { get; init; }

    public string? ConnectionString { get; init; }

    public string? PasswordEnvironmentVariable { get; init; }

    public string? PasswordFile { get; init; }

    public string? PasswordProtectedFile { get; init; }

    public int BatchSize { get; init; } = 100;

    public List<ArpaEntityCollectorOptions> Entities { get; init; } = [];
}

public sealed class ArpaEntityCollectorOptions
{
    public string Name { get; init; } = string.Empty;

    public string EntityType { get; init; } = string.Empty;

    public string Query { get; init; } = string.Empty;
}
