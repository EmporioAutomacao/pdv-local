namespace SyncAgent.Contracts;

public static class SyncContractValues
{
    public static readonly string[] SourceSystems = ["arpa", "pdv_local"];

    public static readonly string[] EntityTypes =
    [
        "cliente",
        "produto",
        "estoque",
        "venda",
        "financeiro",
        "cobranca",
        "plano_historico"
    ];

    public static readonly string[] EventTypes =
    [
        "upsert",
        "delete_logico",
        "status_update"
    ];

    public const string CurrentSchemaVersion = "v1.0";
}
