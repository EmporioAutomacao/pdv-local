using Npgsql;
using System.Text.Json.Nodes;
using SyncAgent.Configuration;
using SyncAgent.Contracts;
using SyncAgent.Normalizers;
using SyncAgent.Persistence;
using SyncAgent.Provisioning;
using SyncAgent.Runtime;
using SyncAgent.Utilities;

namespace SyncAgent.Collectors;

public sealed class ArpaCollector
{
    private static readonly DateTimeOffset InitialWatermark = DateTimeOffset.UnixEpoch;

    private readonly ILogger<ArpaCollector> _logger;
    private readonly EffectiveSyncAgentConfigurationProvider _effectiveConfigProvider;
    private readonly EffectiveArpaCollectorConfigurationProvider _effectiveCollectorConfigProvider;
    private readonly LocalSyncStore _localStore;
    private readonly ArpaPayloadNormalizerRegistry _normalizers;
    private readonly ArpaSyncRunLog _runLog;
    private readonly ArpaLojaListClient _lojaListClient;
    private readonly ArpaConnectionBlocks _connectionBlocks;
    private readonly SyncAgent.Heartbeat.ErpCompatibility _erpCompatibility;

    // A lista de tabelas de preco muda raramente; 5 min evita uma chamada ao ERP
    // por ciclo e ainda libera a conexao logo depois de o operador escolher.
    private static readonly TimeSpan TabelasPrecoMaxAge = TimeSpan.FromMinutes(5);

    public ArpaCollector(
        ILogger<ArpaCollector> logger,
        EffectiveSyncAgentConfigurationProvider effectiveConfigProvider,
        EffectiveArpaCollectorConfigurationProvider effectiveCollectorConfigProvider,
        LocalSyncStore localStore,
        ArpaPayloadNormalizerRegistry normalizers,
        ArpaSyncRunLog runLog,
        ArpaLojaListClient lojaListClient,
        ArpaConnectionBlocks connectionBlocks,
        SyncAgent.Heartbeat.ErpCompatibility erpCompatibility)
    {
        _erpCompatibility = erpCompatibility;
        _logger = logger;
        _effectiveConfigProvider = effectiveConfigProvider;
        _effectiveCollectorConfigProvider = effectiveCollectorConfigProvider;
        _localStore = localStore;
        _normalizers = normalizers;
        _runLog = runLog;
        _lojaListClient = lojaListClient;
        _connectionBlocks = connectionBlocks;
    }

    /// <summary>
    /// Motivo para nao coletar a conexao agora (registrado em
    /// <see cref="ArpaConnectionBlocks"/> para /status, dashboard e bandeja), ou
    /// null para seguir. Usado tambem pelo re-sync sob demanda.
    /// </summary>
    internal async Task<string?> EvaluateBlockAsync(EffectiveArpaCollectorConnection connection, CancellationToken cancellationToken)
    {
        var coletaProdutos = connection.Entities.Any(e => e.EntityType == "produto");
        var tabelas = coletaProdutos
            ? await _lojaListClient.GetTabelasPrecoAsync(TabelasPrecoMaxAge, cancellationToken)
            : null;
        var tabelaReason = ArpaTabelaPrecoRule.BlockReason(connection.Nome, coletaProdutos, connection.TabelaPreco, tabelas);
        var dependencias = await EvaluateDependenciesAsync(connection, cancellationToken);

        var motivos = new List<string>();
        var kinds = ArpaBlockKind.None;
        if (tabelaReason is not null)
        {
            motivos.Add(tabelaReason);
            kinds |= ArpaBlockKind.TabelaPreco;
        }

        if (dependencias.Pausar)
        {
            motivos.AddRange(dependencias.Pausas);
            kinds |= ArpaBlockKind.Dependencias;
        }

        if (dependencias.Avisos.Count > 0)
        {
            _connectionBlocks.SetWarning(connection.Id, connection.Nome, string.Join(" ", dependencias.Avisos));
        }
        else
        {
            _connectionBlocks.ClearWarning(connection.Id);
        }

        if (motivos.Count == 0)
        {
            _connectionBlocks.Clear(connection.Id);
            return null;
        }

        var reason = string.Join(" ", motivos);
        _connectionBlocks.Set(connection.Id, connection.Nome, reason, kinds);
        return reason;
    }

    /// <summary>
    /// Views sync_export na versao do script embutido e banco local aceitando os
    /// entity_type que esta conexao vai coletar (ver <see cref="ArpaDependencyRule"/>).
    /// Falha ao ler a versao no Arpa (conexao recusada etc.) nao pausa por si: o erro
    /// normal da coleta continua aparecendo no log do ciclo.
    /// </summary>
    private async Task<ArpaDependencyResult> EvaluateDependenciesAsync(
        EffectiveArpaCollectorConnection connection,
        CancellationToken cancellationToken)
    {
        var erpCompatible = await _erpCompatibility.SupportsArpaVinculosAsync(cancellationToken);

        var coletados = new List<string>();
        foreach (var entity in connection.Entities)
        {
            if (await _erpCompatibility.AcceptsAsync(entity.EntityType, cancellationToken))
            {
                coletados.Add(entity.EntityType);
            }
        }

        var outdatedCheck = await _localStore.GetOutdatedEntityTypeCheckEntitiesAsync(cancellationToken);

        string? installed;
        var embedded = ArpaDdlRunner.EmbeddedViewsVersion;
        try
        {
            await using var dataSource = NpgsqlDataSource.Create(connection.ConnectionString);
            installed = await ArpaDdlRunner.GetInstalledViewsVersionAsync(dataSource, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Arpa collector: nao foi possivel ler a versao das views de '{Nome}' (ignorado).", connection.Nome);
            embedded = null; // sem leitura confiavel, nao exige versao neste ciclo
            installed = null;
        }

        return ArpaDependencyRule.Evaluate(connection.Nome, installed, embedded, erpCompatible, outdatedCheck, coletados);
    }

    private static string EntityLabel(string entityType) => entityType switch
    {
        "produto" => "Produtos",
        "cliente" => "Clientes",
        "estoque" => "Estoque",
        "venda" => "Vendas",
        "financeiro" => "Financeiro",
        "catalogo" => "Cadastros (especies/condicoes/contas)",
        _ => entityType,
    };

    private static string Summarize(Exception ex) => ex switch
    {
        NpgsqlException { InnerException: { } inner } => inner.Message,
        Npgsql.PostgresException pg => PostgresErrorText.Describe(pg),
        _ => ex.Message,
    };

    /// <param name="entityTypeFilter">Nulo (default) processa tudo que estiver com o
    /// toggle ligado em cada conexao, igual a sempre. Quando informado (disparado pelo
    /// botao "Sincronizar" com uma selecao especifica), so as entidades nele sao
    /// coletadas nesta chamada - as demais sao puladas sem tocar no watermark, ou
    /// seja, nao e um resync delas, so "nao colete isso agora". Vale so para esta
    /// chamada; nunca e persistido.</param>
    public async Task<ArpaCollectorRunSummary> CollectAsync(
        CancellationToken cancellationToken,
        IReadOnlySet<string>? entityTypeFilter = null)
    {
        var connections = await _effectiveCollectorConfigProvider.GetCurrentAsync(cancellationToken);
        _connectionBlocks.RetainOnly(connections.Select(c => c.Id));
        if (connections.Count == 0)
        {
            _runLog.Add("info", "Coletor Arpa desativado ou sem conexoes - nada a coletar.");
            return ArpaCollectorRunSummary.Disabled;
        }

        var totalCollected = 0;
        var totalInserted = 0;
        var failedEntities = 0;
        var totalEntities = 0;

        foreach (var connection in connections)
        {
            // Reenvio "Sincronizar tudo" ja lido: libera a trava quando o envio ao ERP drenar.
            // Falha aqui nao pode parar a coleta incremental desta nem das outras conexoes.
            ArpaResyncJob? resyncJob = null;
            try
            {
                resyncJob = await _localStore.GetArpaResyncJobAsync(connection.Id, cancellationToken);
                if (resyncJob is { ReadingDone: true })
                {
                    await TryFinishResyncJobAsync(connection, resyncJob, cancellationToken);
                    resyncJob = null;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                resyncJob = null;
                _runLog.Add("warn", $"Conexao \"{connection.Nome}\": Sincronizar tudo - falha ao ler o reenvio ({Summarize(ex)}); tenta de novo no proximo ciclo.");
                _logger.LogWarning(ex, "Arpa collector: falha ao ler o reenvio da conexao '{Nome}'.", connection.Nome);
            }

            // Sem watermark tocado: quando o operador corrigir, a conexao retoma
            // exatamente de onde parou, sem perder nada lido do Arpa.
            var blockReason = await EvaluateBlockAsync(connection, cancellationToken);
            if (blockReason is not null)
            {
                _runLog.Add("warn", $"Conexao \"{connection.Nome}\": PAUSADA - {blockReason}");
                _logger.LogWarning("Arpa collector: conexao '{Nome}' pausada. {Reason}", connection.Nome, blockReason);
                continue;
            }

            _runLog.Add("info", $"Conexao \"{connection.Nome}\": lendo do Arpa...");

            NpgsqlDataSource dataSource;
            try
            {
                dataSource = NpgsqlDataSource.Create(connection.ConnectionString);
            }
            catch (Exception ex)
            {
                failedEntities += connection.Entities.Count;
                totalEntities += connection.Entities.Count;
                _runLog.Add("warn", $"Conexao \"{connection.Nome}\": nao foi possivel abrir - {ex.Message}");
                _logger.LogWarning(ex, "Arpa collector: conexao '{Nome}' invalida e pulada nesta execucao.", connection.Nome);
                continue;
            }

            // Geracao fixa da coleta incremental: um "Sincronizar tudo" nao muda os
            // event_id dela (o job tem geracao propria).
            var resyncGeneration = await _localStore.GetArpaIncrementalGenerationAsync(connection.Id, cancellationToken);

            await using (dataSource)
            {
                foreach (var entity in connection.Entities)
                {
                    if (entityTypeFilter is not null && !entityTypeFilter.Contains(entity.EntityType))
                    {
                        _runLog.Add("info", $"  {EntityLabel(entity.EntityType)}: pulado (fora da selecao desta sincronizacao manual).");
                        continue;
                    }

                    // Agente novo + ERP antigo: tipo novo so e coletado quando o ERP
                    // declara que aceita (heartbeat accepted_entity_types, contrato
                    // 2.19.0) - senao o ERP rejeitaria e viraria dead letter.
                    if (!await _erpCompatibility.AcceptsAsync(entity.EntityType, cancellationToken))
                    {
                        _runLog.Add("info", $"  {EntityLabel(entity.EntityType)}: aguardando o ERP ser atualizado (contrato Sync 2.19.0) - nao coletado.");
                        continue;
                    }

                    totalEntities++;
                    try
                    {
                        var entitySummary = await CollectEntityAsync(dataSource, connection, entity, resyncGeneration, cancellationToken);
                        totalCollected += entitySummary.Collected;
                        totalInserted += entitySummary.Inserted;
                        _runLog.Add(
                            "info",
                            $"  {EntityLabel(entity.EntityType)}: {entitySummary.Collected} lido(s), {entitySummary.Inserted} novo(s)/alterado(s).");
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Uma entidade Arpa quebrada (view sync_export ausente, permissao
                        // negada, schema divergente) nao deve derrubar o ciclo inteiro -
                        // heartbeat, envio de vendas PDV e dispatcher precisam continuar.
                        failedEntities++;
                        _runLog.Add("warn", $"  {EntityLabel(entity.EntityType)}: falhou - {Summarize(ex)}");
                        _logger.LogWarning(
                            ex,
                            "Arpa collector: conexao '{Nome}' entidade '{EntityName}' falhou e foi pulada nesta execucao.",
                            connection.Nome,
                            entity.Name);
                    }
                }

                if (resyncJob is not null)
                {
                    try
                    {
                        var (collected, inserted) = await RunResyncJobAsync(dataSource, connection, resyncJob, entityTypeFilter, cancellationToken);
                        totalCollected += collected;
                        totalInserted += inserted;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _runLog.Add("warn", $"Conexao \"{connection.Nome}\": Sincronizar tudo falhou neste ciclo ({Summarize(ex)}); continua no proximo.");
                        _logger.LogWarning(ex, "Arpa collector: reenvio da conexao '{Nome}' falhou neste ciclo.", connection.Nome);
                    }
                }
            }
        }

        if (failedEntities > 0)
        {
            _logger.LogWarning(
                "Arpa collector: {Failed} de {Total} entidades falharam. Verifique as views sync_export e as permissoes do usuario read-only no Arpa.",
                failedEntities,
                totalEntities);
        }

        return new ArpaCollectorRunSummary(true, totalCollected, totalInserted);
    }

    /// <summary>
    /// Avanca o reenvio "Sincronizar tudo" da conexao: le o periodo do job a partir
    /// do cursor de cada entidade (ate <see cref="ArpaCollectionCursor.MaxPagesPerCycle"/>
    /// paginas por ciclo) e enfileira com a geracao do job. Nao toca no watermark da
    /// coleta incremental.
    /// </summary>
    private async Task<(int Collected, int Inserted)> RunResyncJobAsync(
        NpgsqlDataSource dataSource,
        EffectiveArpaCollectorConnection connection,
        ArpaResyncJob job,
        IReadOnlySet<string>? entityTypeFilter,
        CancellationToken cancellationToken)
    {
        var timeZone = TimeZoneInfo.Local;
        var syncOptions = _effectiveConfigProvider.GetCurrent();
        var totalCollected = 0;
        var totalInserted = 0;

        _runLog.Add("info", $"Conexao \"{connection.Nome}\": Sincronizar tudo ({job.PeriodLabel(timeZone)}) - reenviando...");

        foreach (var entityType in job.EntityTypes)
        {
            if (job.DoneEntities.Contains(entityType))
            {
                continue;
            }

            var entity = connection.Entities.FirstOrDefault(e => e.EntityType == entityType);
            if (entity is null)
            {
                // Entidade desligada nesta conexao: nada a reenviar. So registra no log
                // quando foi escolhida de proposito (nao no "todos os tipos" padrao).
                job.DoneEntities.Add(entityType);
                if (job.EntityTypes.Count < SyncAgent.Contracts.SyncContractValues.EntityTypes.Length)
                {
                    _runLog.Add("info", $"  Reenvio {EntityLabel(entityType)}: desligado nesta conexao - nada a reenviar.");
                }
            }
            else if (entityTypeFilter is not null && !entityTypeFilter.Contains(entityType))
            {
                _runLog.Add("info", $"  Reenvio {EntityLabel(entityType)}: pulado (fora da selecao desta sincronizacao manual).");
                continue;
            }
            else if (!await _erpCompatibility.AcceptsAsync(entityType, cancellationToken))
            {
                job.DoneEntities.Add(entityType);
                _runLog.Add("info", $"  Reenvio {EntityLabel(entityType)}: ERP ainda nao aceita este tipo - nao reenviado.");
            }
            else
            {
                try
                {
                    var result = await ArpaCollectionPager.RunRangeAsync(
                        job.CursorFor(entityType),
                        job.ToUtc,
                        connection.BatchSize,
                        (after, toUtc, toKey, limit, ct) => ReadPageAsync(dataSource, entity, after, toUtc, toKey, limit, ct),
                        row => EnqueueRowAsync(connection, entity, row, job.Generation, syncOptions, cancellationToken),
                        cancellationToken);

                    job.Cursors[entityType] = result.Position;
                    job.RegisterSuccess(entityType);
                    if (result.Finished)
                    {
                        job.DoneEntities.Add(entityType);
                    }

                    totalCollected += result.Collected;
                    totalInserted += result.Inserted;
                    _runLog.Add(
                        "info",
                        $"  Reenvio {EntityLabel(entityType)}: {result.Collected} lido(s), {result.Inserted} novo(s)"
                            + (result.Finished ? " - concluido." : " - continua no proximo ciclo."));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Tenta de novo no proximo ciclo, do mesmo cursor. Falha que se repete
                    // (ex.: view best-effort ausente nesse Arpa) nao pode segurar a trava do
                    // reenvio para sempre: apos ArpaResyncJob.MaxFailures seguidas, desiste
                    // dessa entidade e registra o motivo no job.
                    var gaveUp = job.RegisterFailure(entityType, Summarize(ex), DateTimeOffset.UtcNow);
                    var failures = job.FailureCounts[entityType];
                    _runLog.Add(
                        "warn",
                        gaveUp
                            ? $"  Reenvio {EntityLabel(entityType)}: falhou {failures} vez(es) em {ArpaResyncJob.MinFailureSpan.TotalMinutes:0} min ou mais - desistiu desta entidade ({Summarize(ex)})."
                            : $"  Reenvio {EntityLabel(entityType)}: falhou (tentativa {failures}) - tenta de novo no proximo ciclo. {Summarize(ex)}");

                    _logger.LogWarning(ex, "Arpa collector: reenvio da conexao '{Nome}' entidade '{EntityType}' falhou ({Failures}).", connection.Nome, entityType, failures);
                }
            }

            if (!await _localStore.SaveArpaResyncJobAsync(connection.Id, job, cancellationToken))
            {
                _runLog.Add("warn", $"  Reenvio cancelado durante a leitura - interrompido.");
                return (totalCollected, totalInserted);
            }
        }

        if (job.EntityTypes.All(job.DoneEntities.Contains))
        {
            job.ReadingDoneAtUtc = DateTimeOffset.UtcNow;
            if (await _localStore.SaveArpaResyncJobAsync(connection.Id, job, cancellationToken))
            {
                _runLog.Add("info", "  Reenvio: leitura do Arpa concluida; aguardando o envio dos eventos ao ERP.");
            }
        }

        return (totalCollected, totalInserted);
    }

    /// <summary>
    /// Leitura concluida: apaga o job (libera um novo "Sincronizar tudo") quando nao
    /// ha mais evento pendente dessas entidades desde o inicio do reenvio.
    /// </summary>
    private async Task TryFinishResyncJobAsync(
        EffectiveArpaCollectorConnection connection,
        ArpaResyncJob job,
        CancellationToken cancellationToken)
    {
        var pending = await _localStore.CountPendingArpaResyncEventsAsync(job.EntityTypes, job.StartedAtUtc, cancellationToken);
        var period = job.PeriodLabel(TimeZoneInfo.Local);
        if (pending > 0)
        {
            _runLog.Add("info", $"Conexao \"{connection.Nome}\": Sincronizar tudo ({period}) - {pending} evento(s) aguardando envio ao ERP.");
            return;
        }

        // Resultado final antes de apagar o job: o que nao foi reenviado continua
        // visivel na tela (o job some ao terminar).
        await _localStore.SaveArpaResyncLastResultAsync(connection.Id, job, DateTimeOffset.UtcNow, cancellationToken);
        if (await _localStore.DeleteArpaResyncJobAsync(connection.Id, cancellationToken, job.Generation))
        {
            if (job.FailedEntities.Count > 0)
            {
                var falhas = string.Join("; ", job.FailedEntities.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{EntityLabel(f.Key)}: {f.Value}"));
                _runLog.Add("warn", $"Conexao \"{connection.Nome}\": Sincronizar tudo ({period}) concluido COM FALHAS - nao reenviado(s): {falhas}.");
                _logger.LogWarning("Arpa collector: reenvio da conexao '{Nome}' (geracao {Generation}) concluido com falhas: {Falhas}.", connection.Nome, job.Generation, falhas);
            }
            else
            {
                _runLog.Add("info", $"Conexao \"{connection.Nome}\": Sincronizar tudo ({period}) concluido - todos os eventos enviados ao ERP.");
                _logger.LogInformation("Arpa collector: reenvio da conexao '{Nome}' (geracao {Generation}) concluido.", connection.Nome, job.Generation);
            }
        }
    }

    private async Task<ArpaEntityCollectorRunSummary> CollectEntityAsync(
        NpgsqlDataSource dataSource,
        EffectiveArpaCollectorConnection connection,
        ArpaEntityCollectorOptions entity,
        int resyncGeneration,
        CancellationToken cancellationToken)
    {
        var syncOptions = _effectiveConfigProvider.GetCurrent();
        var watermarkKey = $"collector.arpa.{connection.Id}.{entity.EntityType}.watermark";
        var nowUtc = DateTimeOffset.UtcNow;
        var saved = ArpaCollectionCursor.FromState(await _localStore.GetJsonStateAsync(watermarkKey, cancellationToken));

        if (saved.IsInFuture(nowUtc))
        {
            // Estado deixado pela view antiga (occurred_at = vencimento): tudo o que
            // aconteceu depois do salto ficou para tras. O pager rele a entidade
            // inteira uma vez; o que ja foi enviado com o mesmo event_id e ignorado.
            _runLog.Add(
                "warn",
                $"  {EntityLabel(entity.EntityType)}: marcador no futuro ({saved.OccurredAtUtc:yyyy-MM-dd HH:mm}Z) descartado - relendo desde o inicio.");
            _logger.LogWarning(
                "Arpa collector: conexao '{Nome}' entidade '{EntityType}' com watermark no futuro ({Watermark}); relendo desde o inicio.",
                connection.Nome,
                entity.EntityType,
                saved.OccurredAtUtc);
        }

        var result = await ArpaCollectionPager.RunAsync(
            saved,
            nowUtc,
            connection.BatchSize,
            (after, toUtc, toKey, limit, ct) => ReadPageAsync(dataSource, entity, after, toUtc, toKey, limit, ct),
            row => EnqueueRowAsync(connection, entity, row, resyncGeneration, syncOptions, cancellationToken),
            cancellationToken);

        if (result.PositionChanged)
        {
            await _localStore.SetJsonStateAsync(watermarkKey, result.Position.ToState(), cancellationToken);
        }

        var position = result.Position;
        var collected = result.Collected;
        var reread = result.Reread;
        var inserted = result.Inserted;
        var caughtUp = result.CaughtUp;

        _logger.LogInformation(
            "Arpa collector entity completed. Connection={Connection}; Entity={EntityName}; Type={EntityType}; Collected={Collected}; Reread={Reread}; Inserted={Inserted}; Watermark={Watermark}; Key={Key}; CaughtUp={CaughtUp}",
            connection.Nome,
            entity.Name,
            entity.EntityType,
            collected,
            reread,
            inserted,
            position.OccurredAtUtc,
            position.EntityKey,
            caughtUp);

        return new ArpaEntityCollectorRunSummary(collected, inserted);
    }

    internal static async Task<IReadOnlyList<ArpaCollectorRow>> ReadPageAsync(
        NpgsqlDataSource dataSource,
        ArpaEntityCollectorOptions entity,
        ArpaCollectionCursor after,
        DateTimeOffset toUtc,
        string? toKey,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(entity.Query);
        command.Parameters.AddWithValue("from_utc", after.OccurredAtUtc.ToUniversalTime());
        command.Parameters.Add(new NpgsqlParameter("from_key", NpgsqlTypes.NpgsqlDbType.Text) { Value = after.EntityKey });
        command.Parameters.AddWithValue("to_utc", toUtc.ToUniversalTime());
        command.Parameters.Add(new NpgsqlParameter("to_key", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)toKey ?? DBNull.Value });
        command.Parameters.AddWithValue("limit", limit);
        if (entity.Query.Contains("@watermark_utc", StringComparison.OrdinalIgnoreCase))
        {
            // Query antiga do appsettings (conexao estatica nao migrada): so tem o
            // limite inferior. O pager descarta o que passar do limite superior.
            command.Parameters.AddWithValue("watermark_utc", after.OccurredAtUtc.ToUniversalTime());
        }

        var rows = new List<ArpaCollectorRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadCollectorRow(reader));
        }

        return rows;
    }

    private async Task<bool> EnqueueRowAsync(
        EffectiveArpaCollectorConnection connection,
        ArpaEntityCollectorOptions entity,
        ArpaCollectorRow row,
        int resyncGeneration,
        EffectiveSyncAgentConfiguration syncOptions,
        CancellationToken cancellationToken)
    {
        var payload = JsonNode.Parse(row.PayloadJson)?.AsObject()
            ?? throw new InvalidOperationException($"Collector '{entity.Name}' returned invalid payload_json.");

        // Estoque, venda e compra sao por Loja: injeta o loja_codigo da
        // conexao (nome da Loja no ERP) quando a view sync_export nao o traz.
        // O catalogo tambem leva o loja_codigo: o ERP usa para achar a Conexao
        // Arpa certa quando o agente tem mais de uma conexao.
        if ((entity.EntityType == "estoque" || entity.EntityType == "venda" || entity.EntityType == "compra"
                || entity.EntityType == "catalogo")
            && !string.IsNullOrWhiteSpace(connection.LojaCodigo)
            && payload["loja_codigo"] is null)
        {
            payload["loja_codigo"] = connection.LojaCodigo;
        }

        // Empresa dona da Loja acima (obtida do ERP quando a Loja foi escolhida
        // no dropdown, nao digitada a mao) - sem isso o ERP so resolve a Empresa
        // pelo fallback de "empresa unica ativa" em apply_arpa_venda, que roda
        // depois da resolucao de estoque e pode deixar a Loja sem empresa
        // vinculada. Nunca sobrescreve um valor que a propria view ja trouxer.
        if (entity.EntityType is "estoque" or "venda" or "compra")
        {
            if (!string.IsNullOrWhiteSpace(connection.EmpresaCnpj) && payload["empresa_cnpj"] is null)
            {
                payload["empresa_cnpj"] = connection.EmpresaCnpj;
            }

            if (!string.IsNullOrWhiteSpace(connection.EmpresaNome) && payload["empresa_nome"] is null)
            {
                payload["empresa_nome"] = connection.EmpresaNome;
            }
        }

        var normalizedPayload = _normalizers.Normalize(entity.EntityType, row.EntityKey, payload);

        var eventId = ArpaEventIds.ForCollectedRow(
            syncOptions.InstanceId,
            entity.EntityType,
            row.EntityKey,
            row.OccurredAtUtc,
            normalizedPayload,
            resyncGeneration);

        var draft = new OutboxEventDraft(
            eventId,
            syncOptions.InstanceId,
            "arpa",
            entity.EntityType,
            row.EntityKey,
            "upsert",
            row.OccurredAtUtc,
            DateTimeOffset.UtcNow,
            SyncContractValues.CurrentSchemaVersion,
            normalizedPayload,
            row.TraceId);

        var result = await _localStore.EnqueueOutboxEventAsync(draft, cancellationToken);
        return result.Inserted;
    }

    private static ArpaCollectorRow ReadCollectorRow(NpgsqlDataReader reader)
    {
        var entityKey = reader.GetString(reader.GetOrdinal("entity_key"));
        var occurredAtUtc = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("occurred_at_utc"));
        var payloadJson = reader.GetString(reader.GetOrdinal("payload_json"));
        var traceIdOrdinal = TryGetOrdinal(reader, "trace_id");
        var traceId = traceIdOrdinal is null || reader.IsDBNull(traceIdOrdinal.Value)
            ? null
            : reader.GetString(traceIdOrdinal.Value);

        return new ArpaCollectorRow(entityKey, occurredAtUtc, payloadJson, traceId);
    }

    private static int? TryGetOrdinal(NpgsqlDataReader reader, string columnName)
    {
        try
        {
            return reader.GetOrdinal(columnName);
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }
}

public sealed record ArpaCollectorRunSummary(
    bool Enabled,
    int Collected,
    int Inserted)
{
    public static ArpaCollectorRunSummary Disabled { get; } = new(false, 0, 0);
}

public sealed record ArpaEntityCollectorRunSummary(
    int Collected,
    int Inserted);

internal sealed record ArpaCollectorRow(
    string EntityKey,
    DateTimeOffset OccurredAtUtc,
    string PayloadJson,
    string? TraceId);
