namespace SyncAgent.Configuration;

public sealed class SyncAgentProvisioningOptions
{
    public const string SectionName = "Provisioning";

    public bool Enabled { get; init; }

    public string ProtectedFile { get; init; } = ".secrets/sync-agent/provisioning.dpapi";

    public int ActivationTimeoutSeconds { get; init; } = 30;
}
