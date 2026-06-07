namespace SyncAgent.Configuration;

public sealed class ErpSecurityOptions
{
    public const string SectionName = "ErpSecurity";

    public string AccessTokenEnvironmentVariable { get; set; } = "PDV_SYNC_ERP_ACCESS_TOKEN";

    public string AccessToken { get; set; } = string.Empty;

    public string ClientCertificateThumbprint { get; set; } = string.Empty;

    public string ClientCertificateStoreName { get; set; } = "My";

    public string ClientCertificateStoreLocation { get; set; } = "LocalMachine";

    public string ClientCertificatePath { get; set; } = string.Empty;

    public string ClientCertificatePasswordEnvironmentVariable { get; set; } = "PDV_SYNC_ERP_CERT_PASSWORD";

    public string ClientCertificatePassword { get; set; } = string.Empty;

    public bool RequireBearerToken { get; set; } = true;

    public bool RequireMutualTls { get; set; } = true;
}
