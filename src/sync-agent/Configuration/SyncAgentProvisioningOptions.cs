namespace SyncAgent.Configuration;

public sealed class SyncAgentProvisioningOptions
{
    public const string SectionName = "Provisioning";

    public bool Enabled { get; init; }

    public string ProtectedFile { get; init; } = ".secrets/sync-agent/provisioning.dpapi";

    /// <summary>
    /// Arquivo texto (nao segredo) onde a tela /setup guarda a ultima URL do ERP
    /// informada, para pre-preencher o formulario de ativacao entre reinicios.
    /// Vazio => derivado do diretorio de <see cref="ProtectedFile"/>.
    /// O codigo de ativacao nunca e gravado aqui.
    /// </summary>
    public string SetupHintFile { get; init; } = string.Empty;

    public int ActivationTimeoutSeconds { get; init; } = 30;
}
