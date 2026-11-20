namespace SyncAgent.Provisioning;

/// <summary>
/// Dependencias que precisam estar em dia para uma conexao Arpa coletar:
/// <list type="bullet">
/// <item>views <c>sync_export</c> na versao do script embutido (marca gravada pelo
/// "Preparar views"; ver <see cref="ArpaDdlRunner.EmbeddedViewsVersion"/>);</item>
/// <item>banco local aceitando todos os entity_type que a conexao vai coletar
/// (constraint <c>outbox_events_entity_type_check</c>).</item>
/// </list>
/// Pendencia vira <b>pausa</b> da coleta da conexao (sem tocar no watermark - retoma de
/// onde parou). Excecao: views desatualizadas com o ERP ainda antigo viram so
/// <b>aviso</b>, porque o "Preparar views" fica bloqueado ate o ERP ser atualizado e as
/// views atuais continuam no formato que o ERP antigo entende.
/// </summary>
public static class ArpaDependencyRule
{
    public static ArpaDependencyResult Evaluate(
        string nomeConexao,
        string? installedViewsVersion,
        string? embeddedViewsVersion,
        bool erpCompatible,
        IReadOnlyCollection<string> outdatedCheckEntityTypes,
        IReadOnlyCollection<string> entityTypesColetados)
    {
        var pausas = new List<string>();
        var avisos = new List<string>();

        if (ViewsDesatualizadas(installedViewsVersion, embeddedViewsVersion))
        {
            var instalada = string.IsNullOrWhiteSpace(installedViewsVersion) ? "sem versao" : installedViewsVersion;
            if (erpCompatible)
            {
                pausas.Add(
                    $"A conexao Arpa \"{nomeConexao}\" precisa de \"Preparar views\": as views sync_export do banco "
                    + $"Arpa estao desatualizadas (instalada: {instalada}; necessaria: {embeddedViewsVersion}). "
                    + "A sincronizacao desta conexao esta pausada ate preparar em Configuracoes > Arpa > Editar > "
                    + "Preparar banco Arpa (credencial DBA).");
            }
            else
            {
                avisos.Add(
                    $"A conexao Arpa \"{nomeConexao}\" tem views sync_export desatualizadas (instalada: {instalada}; "
                    + $"necessaria: {embeddedViewsVersion}), mas o ERP ainda nao foi atualizado (0.0.164 ou mais "
                    + "novo). A sincronizacao continua no formato atual; atualize o ERP e depois use \"Preparar "
                    + "views\" em Configuracoes > Arpa.");
            }
        }

        var faltando = entityTypesColetados
            .Where(t => outdatedCheckEntityTypes.Contains(t, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
        if (faltando.Count > 0)
        {
            pausas.Add(
                $"A conexao Arpa \"{nomeConexao}\" esta pausada: o banco local do agente nao aceita "
                + $"{string.Join(", ", faltando)}. Rode a rotina \"Corrigir outbox_events_entity_type_check\" em "
                + "Configuracoes > Manutencao do banco local.");
        }

        return new ArpaDependencyResult(pausas, avisos);
    }

    /// <summary>Instalada ausente, ilegivel ou menor que a embutida = desatualizada.</summary>
    public static bool ViewsDesatualizadas(string? installed, string? embedded)
    {
        if (!Version.TryParse(embedded, out var necessaria))
        {
            return false; // sem versao embutida conhecida nao ha o que exigir
        }

        return !Version.TryParse(installed, out var atual) || atual < necessaria;
    }
}

public sealed record ArpaDependencyResult(IReadOnlyList<string> Pausas, IReadOnlyList<string> Avisos)
{
    public bool Pausar => Pausas.Count > 0;
}
