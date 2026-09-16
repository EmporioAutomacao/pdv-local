namespace SyncAgent.Configuration;

/// <summary>
/// Deteccao proativa de inconsistencias em <see cref="ErpSecurityOptions"/> que
/// SO se manifestam quando o agente tenta de fato falar com o ERP
/// (<see cref="Security.ErpCredentialProvider.ValidateProvisionedForRemoteEndpoint"/>
/// lanca <c>AuthenticationException</c> nesse momento, nao antes).
///
/// Existe porque essa exata inconsistencia (RequireMutualTls=true sem
/// certificado) ja aconteceu em producao: a estacao ficava com
/// runtime_status=degraded / last_error com a mensagem da excecao, so
/// descoberta quando alguem olhava o log ou clicava em "Atualizar App" (que
/// virava um 500 generico antes do agente 1.6.30 - ver ErpLatestPackageClient).
/// Essas checagens rodam a cada requisicao de <c>GET /status</c> (o proprio
/// runtime state fica sempre atualizado, sem custo de rede) para que o
/// problema apareca imediatamente em <c>config_warnings</c>, mesmo antes do
/// primeiro ciclo de sincronizacao tentar falar com o ERP e falhar.
///
/// So cobre o que da pra saber sem tocar a rede (presenca de config, nao se a
/// credencial e valida) - "nao configurado" e um erro de instalacao; "invalido"
/// so o ERP pode dizer.
/// </summary>
public static class ErpSecurityConfigDiagnostics
{
    public static IReadOnlyList<string> EvaluateWarnings(ErpSecurityOptions options)
    {
        var warnings = new List<string>();

        if (options.RequireMutualTls
            && string.IsNullOrWhiteSpace(options.ClientCertificateThumbprint)
            && string.IsNullOrWhiteSpace(options.ClientCertificatePath))
        {
            warnings.Add(
                "ErpSecurity:RequireMutualTls esta habilitado, mas nenhum certificado foi provisionado "
                + "(ClientCertificateThumbprint e ClientCertificatePath vazios). Toda chamada ao ERP vai "
                + "falhar com 'Client certificate is required...'. Corrija editando appsettings.json "
                + "(elevado, secao ErpSecurity: RequireMutualTls=false se este cliente nao usa mTLS, ou "
                + "provisione um certificado via provision-sync-agent-security.ps1 -PfxPath) e reinicie "
                + "o servico AraraSuiteSync.");
        }

        if (options.RequireBearerToken
            && string.IsNullOrWhiteSpace(options.AccessToken)
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(options.AccessTokenEnvironmentVariable)))
        {
            warnings.Add(
                $"ErpSecurity:RequireBearerToken esta habilitado, mas nao ha token configurado (nem em "
                + $"ErpSecurity:AccessToken, nem na variavel de ambiente '{options.AccessTokenEnvironmentVariable}'). "
                + "Toda chamada ao ERP vai falhar com 'Bearer token is required...'.");
        }

        return warnings;
    }
}
