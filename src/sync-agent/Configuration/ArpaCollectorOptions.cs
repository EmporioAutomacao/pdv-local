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

    /// <summary>
    /// Quando verdadeiro, a conexao e as entidades/queries sao obtidas de
    /// GET /v1/sync/agents/{instanceId}/arpa-connection (ArpaControlConexao
    /// cadastrada no ERP) em vez de ConnectionString/Entities configurados
    /// localmente. Requer a capability `arpa_collector` concedida na ativacao.
    /// </summary>
    public bool UseRemoteConfig { get; init; }

    /// <summary>
    /// Arquivo protegido por DPAPI onde a ultima configuracao remota obtida
    /// com sucesso fica em cache, para permitir operacao mesmo se o ERP
    /// estiver temporariamente inacessivel. Obrigatorio quando UseRemoteConfig=true.
    /// </summary>
    public string? RemoteConfigCacheProtectedFile { get; init; }

    public int RemoteConfigRefreshMinutes { get; init; } = 60;

    public int RemoteConfigTimeoutSeconds { get; init; } = 20;
}

public sealed class ArpaEntityCollectorOptions
{
    public string Name { get; init; } = string.Empty;

    public string EntityType { get; init; } = string.Empty;

    public string Query { get; init; } = string.Empty;
}
