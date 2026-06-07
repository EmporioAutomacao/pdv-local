using System.Text.Json.Nodes;

namespace SyncAgent.Normalizers;

public sealed class ArpaClientePayloadNormalizer : IArpaPayloadNormalizer
{
    public string EntityType => "cliente";

    public JsonObject Normalize(string entityKey, JsonObject sourcePayload)
    {
        var codigoArpa = JsonPayloadReader.ReadFirstString(sourcePayload, "codigo", "codcliente", "cliente_codigo")
            ?? entityKey;
        if (string.IsNullOrWhiteSpace(codigoArpa))
        {
            throw new InvalidOperationException("Cliente payload requires a codigo or entity_key.");
        }

        var normalized = new JsonObject
        {
            ["codigo_arpa"] = codigoArpa
        };

        JsonPayloadReader.AddIfPresent(
            normalized,
            "nome",
            JsonPayloadReader.ReadFirstString(sourcePayload, "nome", "razao_social", "cliente", "fantasia"));
        JsonPayloadReader.AddIfPresent(
            normalized,
            "documento",
            JsonPayloadReader.OnlyDigits(JsonPayloadReader.ReadFirstString(sourcePayload, "cnpj_cpf", "cpf_cnpj", "cnpj", "cpf", "documento")));
        JsonPayloadReader.AddIfPresent(
            normalized,
            "email",
            JsonPayloadReader.ReadFirstString(sourcePayload, "email", "email_principal"));
        JsonPayloadReader.AddIfPresent(
            normalized,
            "telefone",
            JsonPayloadReader.OnlyDigits(JsonPayloadReader.ReadFirstString(sourcePayload, "telefone", "fone", "celular", "whatsapp")));
        JsonPayloadReader.AddIfPresent(normalized, "ativo", JsonPayloadReader.ReadBoolean(sourcePayload, "ativo"));

        return normalized;
    }
}
