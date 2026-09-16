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

    // Default false de proposito: uma instalacao que nunca passou pelo
    // provisionamento de seguranca (provision-sync-agent-security.ps1, ou o
    // switch -RequireMutualTls de install-sync-agent.ps1) deve funcionar sem
    // exigir uma credencial que nao existe, em vez de nascer com
    // runtime_status=degraded. Quem precisa de fato de bearer token/mTLS liga
    // explicitamente (o instalador ja grava RequireBearerToken=true sempre,
    // por token ser o mecanismo padrao de toda ativacao). Ver incidente
    // "RequireMutualTls sem certificado provisionado" no runbook.
    public bool RequireBearerToken { get; set; } = false;

    public bool RequireMutualTls { get; set; } = false;
}
