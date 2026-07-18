using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SyncAgent.Configuration;
using SyncAgent.Persistence;
using SyncAgent.Provisioning;
using SyncAgent.Runtime;

namespace SyncAgent.LocalApi;

public sealed class LocalStatusServer : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private const int MaxSetupFormBytes = 8192;
    private const string DashboardPath = "/";
    private const string SetupPath = "/setup";
    private const string LogsPath = "/logs";
    private const string HelpPath = "/help";
    private const string StatusPath = "/status";

    private readonly ILogger<LocalStatusServer> _logger;
    private readonly IOptionsMonitor<SyncAgentOptions> _options;
    private readonly EffectiveSyncAgentConfigurationProvider _effectiveConfigProvider;
    private readonly ErpActivationClient _erpActivationClient;
    private readonly LocalSyncStore _localStore;
    private readonly ManualSyncSignal _manualSyncSignal;
    private readonly SyncAgentRuntimeState _runtimeState;

    public LocalStatusServer(
        ILogger<LocalStatusServer> logger,
        IOptionsMonitor<SyncAgentOptions> options,
        EffectiveSyncAgentConfigurationProvider effectiveConfigProvider,
        ErpActivationClient erpActivationClient,
        LocalSyncStore localStore,
        ManualSyncSignal manualSyncSignal,
        SyncAgentRuntimeState runtimeState)
    {
        _logger = logger;
        _options = options;
        _effectiveConfigProvider = effectiveConfigProvider;
        _erpActivationClient = erpActivationClient;
        _localStore = localStore;
        _manualSyncSignal = manualSyncSignal;
        _runtimeState = runtimeState;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var listener = new HttpListener();
        var prefix = $"http://127.0.0.1:{_options.CurrentValue.LocalStatusPort}/";
        listener.Prefixes.Add(prefix);

        listener.Start();
        _logger.LogInformation("Local status API listening on {Prefix}", prefix);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(stoppingToken);
                _ = Task.Run(() => HandleRequestAsync(context, stoppingToken), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/")
            {
                await WriteDashboardAsync(context.Response, cancellationToken);
                return;
            }

            if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/status")
            {
                await WriteStatusAsync(context.Response, cancellationToken);
                return;
            }

            if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/setup")
            {
                await WriteSetupAsync(context.Response, null, cancellationToken);
                return;
            }

            if (context.Request.HttpMethod == "POST" && context.Request.Url?.AbsolutePath == "/setup/activate")
            {
                await HandleSetupActivationAsync(context, cancellationToken);
                return;
            }

            if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/help")
            {
                await WriteHelpAsync(context.Response, cancellationToken);
                return;
            }

            if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/logs")
            {
                await WriteLogsAsync(context.Response, cancellationToken);
                return;
            }

            if (context.Request.HttpMethod == "POST" && context.Request.Url?.AbsolutePath == "/sync-now")
            {
                var accepted = _manualSyncSignal.TrySignal();

                await WriteJsonAsync(
                    context.Response,
                    accepted ? HttpStatusCode.Accepted : HttpStatusCode.Conflict,
                    new
                    {
                        accepted,
                        message = accepted
                            ? "Manual sync signal accepted."
                            : "Manual sync signal queue is full."
                    },
                    cancellationToken);
                return;
            }

            if (context.Request.HttpMethod == "POST" && context.Request.Url?.AbsolutePath == "/check-update")
            {
                var accepted = _manualSyncSignal.TrySignal();

                await WriteJsonAsync(
                    context.Response,
                    accepted ? HttpStatusCode.Accepted : HttpStatusCode.Conflict,
                    new
                    {
                        accepted,
                        message = accepted
                            ? "Verificacao de atualizacao solicitada. O agente consultara o ERP no proximo ciclo."
                            : "Ja existe uma sincronizacao em andamento. Tente novamente em instantes."
                    },
                    cancellationToken);
                return;
            }

            if (context.Request.HttpMethod == "POST" && context.Request.Url?.AbsolutePath == "/pdv-sales/reprocess")
            {
                await HandlePdvSaleReprocessAsync(context, cancellationToken);
                return;
            }

            await WriteJsonAsync(
                context.Response,
                HttpStatusCode.NotFound,
                new { error = "not_found" },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Local status API request failed.");

            if (context.Response.OutputStream.CanWrite)
            {
                await WriteJsonAsync(
                    context.Response,
                    HttpStatusCode.InternalServerError,
                    new { error = "internal_error" },
                    cancellationToken);
            }
        }
    }

    private async Task WriteStatusAsync(HttpListenerResponse response, CancellationToken cancellationToken)
    {
        var effectiveOptions = _effectiveConfigProvider.GetCurrent();
        var storeStatus = await _localStore.GetStatusAsync(cancellationToken);
        var runtimeState = _runtimeState.Snapshot;

        await WriteJsonAsync(
            response,
            HttpStatusCode.OK,
            new
            {
                status = "ok",
                timestamp_utc = DateTimeOffset.UtcNow,
                provisioning_enabled = effectiveOptions.IsProvisioningEnabled,
                provisioned = effectiveOptions.IsProvisioned,
                instance_id = effectiveOptions.InstanceId,
                erp_tenant_id = effectiveOptions.TenantId,
                erp_api_base_url = effectiveOptions.ErpApiBaseUrl,
                agent_version = effectiveOptions.AgentVersion,
                database = storeStatus.DatabaseName,
                pgvector_version = storeStatus.PgVectorVersion,
                pending_outbox_events = storeStatus.PendingOutboxEvents,
                dead_letter_events = storeStatus.DeadLetterEvents,
                oldest_pending_age_seconds = storeStatus.OldestPendingAgeSeconds,
                last_heartbeat_at_utc = storeStatus.LastHeartbeatAtUtc,
                last_heartbeat_succeeded = storeStatus.LastHeartbeatSucceeded,
                last_heartbeat_connectivity = storeStatus.LastHeartbeatConnectivity,
                last_reconciliation_id = storeStatus.LastReconciliationId,
                last_reconciliation_status = storeStatus.LastReconciliationStatus,
                last_reconciliation_completed_at_utc = storeStatus.LastReconciliationCompletedAtUtc,
                last_reconciliation_summary = storeStatus.LastReconciliationSummary,
                pdv_sales_summary = storeStatus.PdvSalesSummary,
                runtime_status = runtimeState.CurrentStatus,
                last_cycle_completed_at_utc = runtimeState.LastCycleCompletedAtUtc,
                last_cycle_trigger = runtimeState.LastCycleTrigger,
                last_error = runtimeState.LastError
            },
            cancellationToken);
    }

    private async Task WriteDashboardAsync(HttpListenerResponse response, CancellationToken cancellationToken)
    {
        var effectiveOptions = _effectiveConfigProvider.GetCurrent();
        var storeStatus = await _localStore.GetStatusAsync(cancellationToken);
        var rejectedSales = await _localStore.GetRejectedPdvSalesAsync(20, cancellationToken);
        var runtimeState = _runtimeState.Snapshot;
        var activationStatus = effectiveOptions.IsProvisioned ? "provisioned" : "not_provisioned";
        var activationClass = effectiveOptions.IsProvisioned ? "ok" : "warn";
        var rejectedSalesHtml = BuildRejectedSalesHtml(rejectedSales);

        var html = $$"""
            <!doctype html>
            <html lang="pt-BR">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta http-equiv="refresh" content="15">
              <title>PDV Local Sync Agent</title>
              <style>
                body { font-family: Segoe UI, Arial, sans-serif; margin: 32px; color: #1f2937; background: #f8fafc; }
                main { max-width: 960px; margin: 0 auto; }
                h1 { margin-bottom: 4px; font-size: 28px; }
                .muted { color: #64748b; margin-top: 0; }
                {{BaseStyles}}
                .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 12px; margin-top: 24px; }
                .card { background: white; border: 1px solid #e2e8f0; border-radius: 8px; padding: 16px; }
                .label { color: #64748b; font-size: 13px; margin-bottom: 8px; }
                .value { font-size: 20px; font-weight: 600; overflow-wrap: anywhere; }
                .ok { color: #047857; }
                .warn { color: #b45309; }
                button { margin-top: 24px; padding: 10px 14px; border: 0; border-radius: 6px; background: #2563eb; color: white; cursor: pointer; }
                button:hover { background: #1d4ed8; }
                .inline-button { margin: 0; padding: 7px 10px; font-size: 12px; }
                table { width: 100%; border-collapse: collapse; }
                th, td { border-bottom: 1px solid #e2e8f0; padding: 8px; text-align: left; vertical-align: top; font-size: 14px; }
                th { color: #475569; font-size: 13px; }
                pre { background: #0f172a; color: #e2e8f0; border-radius: 8px; padding: 16px; overflow: auto; }
              </style>
            </head>
            <body>
              <main>
                <h1>PDV Local Sync Agent</h1>
                <p class="muted">Dashboard local. Atualiza automaticamente a cada 15 segundos.</p>
                {{LocalNavHtml(DashboardPath)}}
                <div class="grid">
                  <section class="card"><div class="label">Status</div><div class="value ok">{{Html(runtimeState.CurrentStatus)}}</div></section>
                  <section class="card"><div class="label">Ativacao</div><div class="value {{activationClass}}">{{Html(activationStatus)}}</div></section>
                  <section class="card"><div class="label">Tenant ERP</div><div class="value">{{Html(effectiveOptions.TenantId)}}</div></section>
                  <section class="card"><div class="label">Instalacao</div><div class="value">{{Html(effectiveOptions.InstanceId)}}</div></section>
                  <section class="card"><div class="label">Pendentes</div><div class="value">{{storeStatus.PendingOutboxEvents}}</div></section>
                  <section class="card"><div class="label">Dead-letter</div><div class="value warn">{{storeStatus.DeadLetterEvents}}</div></section>
                  <section class="card"><div class="label">Vendas PDV pendentes</div><div class="value">{{ReadJsonLong(storeStatus.PdvSalesSummary, "pending_sync")}}</div></section>
                  <section class="card"><div class="label">Vendas PDV enviadas</div><div class="value">{{ReadJsonLong(storeStatus.PdvSalesSummary, "sent")}}</div></section>
                  <section class="card"><div class="label">Vendas PDV aceitas</div><div class="value ok">{{ReadJsonLong(storeStatus.PdvSalesSummary, "accepted")}}</div></section>
                  <section class="card"><div class="label">Vendas PDV rejeitadas</div><div class="value warn">{{ReadJsonLong(storeStatus.PdvSalesSummary, "rejected")}}</div></section>
                  <section class="card"><div class="label">Heartbeat</div><div class="value">{{Html(FormatHeartbeat(storeStatus))}}</div></section>
                  <section class="card"><div class="label">Reconciliacao</div><div class="value">{{Html(FormatReconciliation(storeStatus))}}</div></section>
                  <section class="card"><div class="label">Evento mais antigo</div><div class="value">{{storeStatus.OldestPendingAgeSeconds}}s</div></section>
                  <section class="card"><div class="label">pgvector</div><div class="value">{{Html(storeStatus.PgVectorVersion)}}</div></section>
                  <section class="card"><div class="label">Ultimo ciclo</div><div class="value">{{Html(runtimeState.LastCycleTrigger ?? "-")}}</div></section>
                  <section class="card"><div class="label">Ultimo erro</div><div class="value warn">{{Html(runtimeState.LastError ?? "-")}}</div></section>
                </div>
                <section class="card" style="margin-top: 12px;">
                  <div class="label">Resumo da ultima reconciliacao</div>
                  <pre>{{Html(storeStatus.LastReconciliationSummary.ToJsonString(JsonOptions))}}</pre>
                </section>
                {{rejectedSalesHtml}}
                <button onclick="fetch('/sync-now', { method: 'POST' }).then(() => location.reload())">Sincronizar agora</button>
              </main>
            </body>
            </html>
            """;

        var bytes = Encoding.UTF8.GetBytes(html);
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/html; charset=utf-8";
        AddNoStoreHeaders(response);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
        response.Close();
    }

    private async Task WriteHelpAsync(HttpListenerResponse response, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var effectiveOptions = _effectiveConfigProvider.GetCurrent();
        var storeStatus = await _localStore.GetStatusAsync(cancellationToken);
        var runtimeState = _runtimeState.Snapshot;

        var html = $$"""
            <!doctype html>
            <html lang="pt-BR">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Ajuda - PDV Local Sync Agent</title>
              <style>
                body { font-family: Segoe UI, Arial, sans-serif; margin: 32px; color: #1f2937; background: #f8fafc; line-height: 1.5; }
                main { max-width: 980px; margin: 0 auto; }
                h1 { margin-bottom: 4px; font-size: 28px; }
                h2 { margin-top: 28px; font-size: 20px; }
                h3 { margin-top: 20px; font-size: 16px; }
                {{BaseStyles}}
                .muted { color: #64748b; margin-top: 0; }
                .panel { background: white; border: 1px solid #e2e8f0; border-radius: 8px; padding: 18px; margin: 16px 0; }
                .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); gap: 12px; }
                .label { color: #64748b; font-size: 13px; }
                .value { font-weight: 600; overflow-wrap: anywhere; }
                code { background: #e2e8f0; border-radius: 4px; padding: 2px 5px; }
                pre { background: #0f172a; color: #e2e8f0; border-radius: 8px; padding: 14px; overflow: auto; }
                table { width: 100%; border-collapse: collapse; background: white; }
                th, td { border-bottom: 1px solid #e2e8f0; padding: 10px; text-align: left; vertical-align: top; }
                th { color: #475569; font-size: 13px; }
                .ok { color: #047857; }
                .warn { color: #b45309; }
              </style>
            </head>
            <body>
              <main>
                <h1>Ajuda do Sync Agent</h1>
                <p class="muted">Guia operacional local para diagnostico e suporte em campo.</p>
                {{LocalNavHtml(HelpPath)}}

                <section class="panel">
                  <h2>Resumo desta instalacao</h2>
                  <div class="grid">
                    <div><div class="label">Instalacao</div><div class="value">{{Html(effectiveOptions.InstanceId)}}</div></div>
                    <div><div class="label">Tenant ERP</div><div class="value">{{Html(effectiveOptions.TenantId)}}</div></div>
                    <div><div class="label">ERP API</div><div class="value">{{Html(effectiveOptions.ErpApiBaseUrl)}}</div></div>
                    <div><div class="label">Versao</div><div class="value">{{Html(options.AgentVersion)}}</div></div>
                    <div><div class="label">Status runtime</div><div class="value ok">{{Html(runtimeState.CurrentStatus)}}</div></div>
                    <div><div class="label">Pendentes</div><div class="value">{{storeStatus.PendingOutboxEvents}}</div></div>
                    <div><div class="label">Dead-letter</div><div class="value warn">{{storeStatus.DeadLetterEvents}}</div></div>
                    <div><div class="label">Vendas PDV aceitas</div><div class="value ok">{{ReadJsonLong(storeStatus.PdvSalesSummary, "accepted")}}</div></div>
                    <div><div class="label">Vendas PDV rejeitadas</div><div class="value warn">{{ReadJsonLong(storeStatus.PdvSalesSummary, "rejected")}}</div></div>
                    <div><div class="label">Heartbeat</div><div class="value">{{Html(FormatHeartbeat(storeStatus))}}</div></div>
                    <div><div class="label">Reconciliacao</div><div class="value">{{Html(FormatReconciliation(storeStatus))}}</div></div>
                  </div>
                </section>

                <section class="panel">
                  <h2>Como validar rapidamente</h2>
                  <pre>Invoke-RestMethod -Uri "http://127.0.0.1:{{options.LocalStatusPort}}/status" -Method Get
            Invoke-RestMethod -Uri "http://127.0.0.1:{{options.LocalStatusPort}}/sync-now" -Method Post
            Invoke-RestMethod -Uri "http://127.0.0.1:{{options.LocalStatusPort}}/check-update" -Method Post
            Get-Service "PDV Local Sync Agent"</pre>
                </section>

                <section class="panel">
                  <h2>O que cada indicador significa</h2>
                  <table>
                    <tr><th>Indicador</th><th>Significado</th><th>Acao sugerida</th></tr>
                    <tr><td><code>runtime_status</code></td><td>Estado atual do Worker.</td><td>Se estiver <code>degraded</code>, consulte <code>last_error</code> em <a href="/status">/status</a>.</td></tr>
                    <tr><td><code>pending_outbox_events</code></td><td>Eventos ainda nao confirmados pelo ERP.</td><td>Se crescer continuamente, validar internet, token, certificado e endpoint ERP.</td></tr>
                    <tr><td><code>dead_letter_events</code></td><td>Eventos que nao serao reenviados automaticamente.</td><td>Abrir suporte tecnico para analisar motivo e decidir reprocessamento.</td></tr>
                    <tr><td><code>oldest_pending_age_seconds</code></td><td>Idade do evento pendente mais antigo.</td><td>Se ultrapassar o SLA operacional, investigar conectividade e resposta do ERP.</td></tr>
                    <tr><td><code>last_heartbeat_succeeded</code></td><td>Resultado do ultimo heartbeat para o ERP.</td><td>Se falhar, validar autenticacao e disponibilidade da API ERP.</td></tr>
                    <tr><td><code>last_reconciliation_summary</code></td><td>Resumo local da janela reconciliada nas ultimas 24h.</td><td>Use para localizar crescimento de pendentes, rejeicoes e dead-letter por entidade.</td></tr>
                    <tr><td><code>pdv_sales_summary</code></td><td>Contagem das vendas PDV por status local: <code>pending_sync</code>, <code>sent</code>, <code>accepted</code>, <code>rejected</code>.</td><td><code>rejected</code> exige analise do dead-letter; <code>sent</code> prolongado indica envio ainda nao confirmado.</td></tr>
                  </table>
                </section>

                <section class="panel">
                  <h2>Fluxo de sincronizacao</h2>
                  <pre>Arpa local -> Collector -> Normalizers -> Outbox PostgreSQL -> Dispatcher HTTPS -> ERP Sync API</pre>
                  <p>O agente nunca precisa receber conexoes de entrada da internet. Toda comunicacao normal e de saida para o ERP.</p>
                </section>

                <section class="panel">
                  <h2>Credenciais e seguranca</h2>
                  <ul>
                    <li>Bearer token deve vir da variavel de ambiente de maquina <code>PDV_SYNC_ERP_ACCESS_TOKEN</code>.</li>
                    <li>Certificado cliente deve ficar no Windows Certificate Store, referenciado por thumbprint.</li>
                    <li>Fora de <code>localhost</code>, a API ERP deve usar HTTPS.</li>
                    <li>Token, senha de PFX e payload sensivel nao devem ser enviados em prints ou logs de suporte.</li>
                  </ul>
                </section>

                <section class="panel">
                  <h2>Banco local</h2>
                  <p>O banco local e PostgreSQL 17 com pgvector 0.8.0, timezone <code>America/Sao_Paulo</code>.</p>
                  <pre>psql -U pdv_sync -d pdv_sync -c "SHOW timezone;"
            psql -U pdv_sync -d pdv_sync -c "SELECT extname, extversion FROM pg_extension WHERE extname = 'vector';"</pre>
                </section>

                <section class="panel">
                  <h2>Atualizacao automatica</h2>
                  <p>O operador do ERP pode enviar uma atualizacao remota sem acesso direto a esta maquina.</p>
                  <table>
                    <tr><th>Etapa</th><th>Descricao</th></tr>
                    <tr><td>1. Pacote</td><td>O build script registra o pacote automaticamente em <strong>API de Sincronizacao &gt; Pacotes de atualizacao</strong> ao gerar o ZIP.</td></tr>
                    <tr><td>2. ERP Admin</td><td>Em <strong>API de Sincronizacao &gt; Instalacoes do PDV</strong>, selecione a instalacao, acao <em>Solicitar atualizacao</em>, escolha o pacote no dropdown e clique <em>Agendar atualizacao</em>.</td></tr>
                    <tr><td>3. Heartbeat</td><td>Em ate 30s, o agente recebe <code>pending_update</code> na resposta do ERP.</td></tr>
                    <tr><td>4. Download</td><td>O agente baixa o ZIP e verifica o SHA256 antes de prosseguir.</td></tr>
                    <tr><td>5. Atualizacao</td><td><code>self-update.ps1</code> faz backup, substitui binarios e reinicia o servico.</td></tr>
                    <tr><td>6. Rollback</td><td>Se o servico nao iniciar, o script restaura o backup automaticamente.</td></tr>
                  </table>
                  <p style="margin-top:10px;">O PDV App encerra durante a atualizacao. O tempo de interrupcao e inferior a 60 segundos.</p>
                  <p>Para solicitar verificacao imediata sem esperar o proximo ciclo:</p>
                  <pre>Invoke-RestMethod -Uri "http://127.0.0.1:{{options.LocalStatusPort}}/check-update" -Method Post</pre>
                  <p>Ou use o botao <strong>Verificar atualizacao</strong> em Detalhes Tecnicos no PDV App.</p>
                  <p>Log de atualizacao no Windows Event Log:</p>
                  <pre>Get-EventLog -LogName Application -Source "PDV Local Self-Update" -Newest 20</pre>
                </section>

                <section class="panel">
                  <h2>Arquivos e servico Windows</h2>
                  <table>
                    <tr><th>Item</th><th>Caminho/valor</th></tr>
                    <tr><td>Servico</td><td><code>PDV Local Sync Agent</code></td></tr>
                    <tr><td>Instalacao padrao</td><td><code>C:\Program Files\PDVLocal</code></td></tr>
                    <tr><td>API local</td><td><code>http://127.0.0.1:{{options.LocalStatusPort}}</code></td></tr>
                    <tr><td>Script de atualizacao</td><td><code>C:\Program Files\PDVLocal\SyncAgent\self-update.ps1</code></td></tr>
                    <tr><td>Backup de versoes</td><td><code>C:\Program Files\PDVLocal\Backups\&lt;versao&gt;\</code></td></tr>
                    <tr><td>Versao instalada</td><td><code>C:\Program Files\PDVLocal\SyncAgent\VERSION</code></td></tr>
                    <tr><td>Documentacao tecnica</td><td><code>docs/sync-agent-sprint-1-manual.md</code></td></tr>
                  </table>
                </section>
              </main>
            </body>
            </html>
            """;

        var bytes = Encoding.UTF8.GetBytes(html);
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/html; charset=utf-8";
        AddNoStoreHeaders(response);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
        response.Close();
    }

    private async Task WriteLogsAsync(HttpListenerResponse response, CancellationToken cancellationToken)
    {
        var entries = await _localStore.GetRecentTaskLogsAsync(60, cancellationToken);
        var rows = new StringBuilder();

        foreach (var entry in entries)
        {
            var statusClass = entry.Status.Contains("accepted", StringComparison.OrdinalIgnoreCase)
                || entry.Status.Contains("completed", StringComparison.OrdinalIgnoreCase)
                ? "ok"
                : "warn";

            rows.AppendLine($"""
                <tr>
                  <td>{Html(entry.OccurredAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"))}</td>
                  <td>{Html(FormatTaskType(entry.TaskType))}</td>
                  <td>{Html(entry.Title)}</td>
                  <td class="{statusClass}">{Html(entry.Status)}</td>
                  <td>{FormatTaskDetails(entry)}</td>
                </tr>
                """);
        }

        var emptyState = entries.Count == 0
            ? "<p class=\"muted\">Nenhuma tarefa registrada ainda.</p>"
            : string.Empty;

        var html = $$"""
            <!doctype html>
            <html lang="pt-BR">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta http-equiv="refresh" content="15">
              <title>Logs - PDV Local Sync Agent</title>
              <style>
                body { font-family: Segoe UI, Arial, sans-serif; margin: 32px; color: #1f2937; background: #f8fafc; }
                main { max-width: 1180px; margin: 0 auto; }
                h1 { margin-bottom: 4px; font-size: 28px; }
                .muted { color: #64748b; margin-top: 0; }
                {{BaseStyles}}
                .panel { background: white; border: 1px solid #e2e8f0; border-radius: 8px; padding: 16px; }
                table { width: 100%; border-collapse: collapse; }
                th, td { border-bottom: 1px solid #e2e8f0; padding: 10px; text-align: left; vertical-align: top; }
                th { color: #475569; font-size: 13px; white-space: nowrap; }
                td { font-size: 14px; }
                .ok { color: #047857; font-weight: 600; }
                .warn { color: #b45309; font-weight: 600; }
                .summary { display: flex; gap: 8px; flex-wrap: wrap; margin-bottom: 10px; }
                .pill { background: #e2e8f0; color: #334155; border-radius: 999px; padding: 3px 8px; font-size: 12px; font-weight: 600; }
                .records { margin-top: 8px; max-height: 260px; overflow: auto; border: 1px solid #e2e8f0; border-radius: 8px; }
                .records table { font-size: 12px; }
                .records th, .records td { padding: 6px 8px; }
                pre { margin: 0; max-width: 440px; max-height: 180px; overflow: auto; white-space: pre-wrap; overflow-wrap: anywhere; background: #0f172a; color: #e2e8f0; border-radius: 8px; padding: 10px; font-size: 12px; }
                @media (max-width: 760px) {
                  body { margin: 16px; }
                  table, thead, tbody, th, td, tr { display: block; }
                  thead { display: none; }
                  tr { border-bottom: 1px solid #e2e8f0; padding: 10px 0; }
                  td { border: 0; padding: 6px 0; }
                  pre { max-width: none; }
                  .records table, .records thead, .records tbody, .records th, .records td, .records tr { display: revert; }
                }
              </style>
            </head>
            <body>
              <main>
                <h1>Logs</h1>
                <p class="muted">Ultimas tarefas registradas no banco local. Atualiza automaticamente a cada 15 segundos.</p>
                {{LocalNavHtml(LogsPath)}}
                <section class="panel">
                  {{emptyState}}
                  <table>
                    <thead>
                      <tr><th>Horario</th><th>Tarefa</th><th>Referencia</th><th>Status</th><th>Detalhes</th></tr>
                    </thead>
                    <tbody>
                      {{rows}}
                    </tbody>
                  </table>
                </section>
              </main>
            </body>
            </html>
            """;

        var bytes = Encoding.UTF8.GetBytes(html);
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/html; charset=utf-8";
        AddNoStoreHeaders(response);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
        response.Close();
    }

    private async Task HandleSetupActivationAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var form = await ReadFormAsync(context.Request, cancellationToken);
        var erpApiBaseUrl = form.GetValueOrDefault("erp_api_base_url", string.Empty);
        var activationCode = form.GetValueOrDefault("activation_code", string.Empty);

        var result = await _erpActivationClient.ActivateAsync(erpApiBaseUrl, activationCode, cancellationToken);
        if (result.Succeeded)
        {
            _manualSyncSignal.TrySignal();
        }

        await WriteSetupAsync(context.Response, result, cancellationToken);
    }

    private async Task HandlePdvSaleReprocessAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var form = await ReadFormAsync(context.Request, cancellationToken);
        var rawSaleId = form.GetValueOrDefault("sale_id", string.Empty);
        if (!Guid.TryParse(rawSaleId, out var saleId))
        {
            await WriteJsonAsync(
                context.Response,
                HttpStatusCode.BadRequest,
                new { error = "invalid_sale_id" },
                cancellationToken);
            return;
        }

        var requeued = await _localStore.RequeueRejectedPdvSaleAsync(saleId, cancellationToken);
        if (requeued)
        {
            _manualSyncSignal.TrySignal();
        }

        await WriteJsonAsync(
            context.Response,
            requeued ? HttpStatusCode.Accepted : HttpStatusCode.NotFound,
            new
            {
                requeued,
                sale_id = saleId,
                message = requeued
                    ? "Venda reenfileirada para reprocessamento."
                    : "Venda rejeitada nao encontrada para reprocessamento."
            },
            cancellationToken);
    }

    private async Task WriteSetupAsync(
        HttpListenerResponse response,
        ActivationResult? activationResult,
        CancellationToken cancellationToken)
    {
        var effectiveOptions = _effectiveConfigProvider.GetCurrent();
        var messageHtml = activationResult is null
            ? string.Empty
            : $"""<div class="message {(activationResult.Succeeded ? "okbox" : "warnbox")}">{Html(activationResult.Message)}</div>""";

        var formHtml = effectiveOptions.IsProvisioned
            ? $$"""
                <section class="panel">
                  <h2>Instalacao ativada</h2>
                  <div class="grid">
                    <div><div class="label">Instalacao</div><div class="value">{{Html(effectiveOptions.InstanceId)}}</div></div>
                    <div><div class="label">Tenant ERP</div><div class="value">{{Html(effectiveOptions.TenantId)}}</div></div>
                    <div><div class="label">ERP API</div><div class="value">{{Html(effectiveOptions.ErpApiBaseUrl)}}</div></div>
                    <div><div class="label">Token expira em</div><div class="value">{{Html(effectiveOptions.AccessTokenExpiresAtUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "-")}}</div></div>
                  </div>
                </section>
                """
            : """
                <section class="panel">
                  <h2>Ativar conexao com o ERP</h2>
                  <form method="post" action="/setup/activate">
                    <label for="erp_api_base_url">URL do ERP</label>
                    <input id="erp_api_base_url" name="erp_api_base_url" type="url" required placeholder="https://erp.exemplo.com">
                    <label for="activation_code">Codigo de ativacao</label>
                    <input id="activation_code" name="activation_code" type="text" required autocomplete="off">
                    <button type="submit">Conectar</button>
                  </form>
                </section>
                """;

        var html = $$"""
            <!doctype html>
            <html lang="pt-BR">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Ativacao - PDV Local Sync Agent</title>
              <style>
                body { font-family: Segoe UI, Arial, sans-serif; margin: 32px; color: #1f2937; background: #f8fafc; }
                main { max-width: 760px; margin: 0 auto; }
                h1 { margin-bottom: 4px; font-size: 28px; }
                h2 { margin-top: 0; font-size: 20px; }
                .muted { color: #64748b; margin-top: 0; }
                {{BaseStyles}}
                .panel { background: white; border: 1px solid #e2e8f0; border-radius: 8px; padding: 18px; margin: 16px 0; }
                .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 12px; }
                .label, label { color: #64748b; font-size: 13px; display: block; margin-bottom: 6px; }
                .value { font-weight: 600; overflow-wrap: anywhere; }
                input { width: 100%; box-sizing: border-box; padding: 10px; border: 1px solid #cbd5e1; border-radius: 6px; margin-bottom: 14px; font-size: 15px; }
                button { padding: 10px 14px; border: 0; border-radius: 6px; background: #2563eb; color: white; cursor: pointer; }
                button:hover { background: #1d4ed8; }
                .message { border-radius: 8px; padding: 12px; margin: 16px 0; font-weight: 600; }
                .okbox { background: #dcfce7; color: #166534; }
                .warnbox { background: #fef3c7; color: #92400e; }
                code { background: #e2e8f0; border-radius: 4px; padding: 2px 5px; }
              </style>
            </head>
            <body>
              <main>
                <h1>Ativacao do Sync Agent</h1>
                <p class="muted">Conecte esta instalacao ao ERP usando um codigo de ativacao.</p>
                {{LocalNavHtml(SetupPath)}}
                {{messageHtml}}
                {{formHtml}}
                <section class="panel">
                  <h2>Regra de seguranca</h2>
                  <p>O SyncAgent nao armazena usuario e senha do ERP. O codigo de ativacao e usado uma unica vez para emitir credenciais tecnicas desta maquina.</p>
                  <p>Enquanto a instalacao estiver <code>not_provisioned</code>, a sincronizacao permanece bloqueada.</p>
                </section>
              </main>
            </body>
            </html>
            """;

        var bytes = Encoding.UTF8.GetBytes(html);
        response.StatusCode = (int)(activationResult?.Succeeded == false ? HttpStatusCode.BadRequest : HttpStatusCode.OK);
        response.ContentType = "text/html; charset=utf-8";
        AddNoStoreHeaders(response);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
        response.Close();
    }

    private static async Task<Dictionary<string, string>> ReadFormAsync(
        HttpListenerRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength64 > MaxSetupFormBytes)
        {
            throw new InvalidOperationException("Setup form body exceeded the maximum accepted size.");
        }

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        var body = await reader.ReadToEndAsync(cancellationToken);
        if (Encoding.UTF8.GetByteCount(body) > MaxSetupFormBytes)
        {
            throw new InvalidOperationException("Setup form body exceeded the maximum accepted size.");
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var keyValue = part.Split('=', 2);
            var key = WebUtility.UrlDecode(keyValue[0].Replace("+", " ", StringComparison.Ordinal));
            var value = keyValue.Length > 1
                ? WebUtility.UrlDecode(keyValue[1].Replace("+", " ", StringComparison.Ordinal))
                : string.Empty;

            if (!string.IsNullOrWhiteSpace(key))
            {
                values[key] = value;
            }
        }

        return values;
    }

    private static async Task WriteJsonAsync(
        HttpListenerResponse response,
        HttpStatusCode statusCode,
        object body,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(body, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json; charset=utf-8";
        AddNoStoreHeaders(response);
        response.ContentLength64 = bytes.Length;

        await response.OutputStream.WriteAsync(bytes, cancellationToken);
        response.Close();
    }

    private static void AddNoStoreHeaders(HttpListenerResponse response)
    {
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["Pragma"] = "no-cache";
        response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    private static string BaseStyles => """
                .local-nav { display: flex; gap: 8px; margin: 16px 0 24px; flex-wrap: wrap; align-items: center; }
                .local-nav a { display: inline-flex; align-items: center; min-height: 34px; padding: 8px 12px; border: 1px solid #cbd5e1; border-radius: 8px; background: #ffffff; color: #334155; text-decoration: none; font-size: 14px; font-weight: 700; line-height: 1; box-shadow: 0 1px 1px rgba(15, 23, 42, 0.04); }
                .local-nav a:hover { background: #eff6ff; border-color: #93c5fd; color: #1d4ed8; text-decoration: none; }
                .local-nav a.active { background: #2563eb; border-color: #2563eb; color: #ffffff; }
                .local-nav a.active:hover { background: #1d4ed8; border-color: #1d4ed8; color: #ffffff; }
                @media (max-width: 520px) {
                  .local-nav { gap: 6px; }
                  .local-nav a { flex: 1 1 calc(50% - 6px); justify-content: center; }
                }
                """;

    private static string LocalNavHtml(string activePath)
    {
        return $"""
                <nav class="local-nav" aria-label="Navegacao local">
                  {LocalNavLink(DashboardPath, "Dashboard", activePath)}
                  {LocalNavLink(SetupPath, "Ativacao", activePath)}
                  {LocalNavLink(LogsPath, "Logs", activePath)}
                  {LocalNavLink(HelpPath, "Ajuda", activePath)}
                  {LocalNavLink(StatusPath, "JSON tecnico", activePath)}
                </nav>
                """;
    }

    private static string LocalNavLink(string path, string label, string activePath)
    {
        var isActive = string.Equals(path, activePath, StringComparison.OrdinalIgnoreCase);
        var activeClass = isActive ? " class=\"active\" aria-current=\"page\"" : string.Empty;
        return $"<a href=\"{Html(path)}\"{activeClass}>{Html(label)}</a>";
    }

    private static string Html(string value)
    {
        return WebUtility.HtmlEncode(value);
    }

    private static string FormatHeartbeat(LocalSyncStoreStatus storeStatus)
    {
        if (storeStatus.LastHeartbeatAtUtc is null)
        {
            return "-";
        }

        var status = storeStatus.LastHeartbeatSucceeded == true ? "ok" : "falhou";
        return $"{status} / {storeStatus.LastHeartbeatConnectivity ?? "-"}";
    }

    private static string FormatReconciliation(LocalSyncStoreStatus storeStatus)
    {
        if (storeStatus.LastReconciliationId is null)
        {
            return "-";
        }

        var completedAt = storeStatus.LastReconciliationCompletedAtUtc?.ToLocalTime().ToString("dd/MM HH:mm") ?? "-";
        return $"{storeStatus.LastReconciliationStatus ?? "-"} / {completedAt}";
    }

    private static string FormatTaskType(string taskType)
    {
        return taskType switch
        {
            "dispatcher" => "Envio ERP",
            "reconciliation" => "Reconciliacao",
            "pdv_operator_snapshot" => "Operadores PDV",
            "pdv_product_snapshot" => "Produtos PDV",
            "pdv_payment_methods_snapshot" => "Pagamentos PDV",
            _ => taskType
        };
    }

    private static string FormatTaskDetails(LocalTaskLogEntry entry)
    {
        if (entry.TaskType != "dispatcher")
        {
            return $"<pre>{Html(entry.Details.ToJsonString(JsonOptions))}</pre>";
        }

        var details = entry.Details.AsObject();
        var summary = new StringBuilder();
        summary.Append("<div class=\"summary\">");
        AppendPill(summary, "Eventos", details["events"]);
        AppendPill(summary, "Aceitos", details["accepted"]);
        AppendPill(summary, "Rejeitados", details["rejected"]);
        AppendPill(summary, "HTTP", details["http_status"]);
        summary.Append("</div>");

        var records = details["affected_records"]?.AsArray();
        if (records is null || records.Count == 0)
        {
            summary.Append("<p class=\"muted\">Sem registros afetados gravados para esta operacao.</p>");
            return summary.ToString();
        }

        var limit = details["affected_records_limit"]?.GetValue<int?>() ?? records.Count;
        summary.Append($"""
            <div class="muted">Amostra dos registros alterados nesta operacao. Limite exibido: {limit}.</div>
            <div class="records">
              <table>
                <thead>
                  <tr><th>Entidade</th><th>Chave</th><th>Evento</th><th>Status</th><th>Ocorrido em</th><th>Trace</th></tr>
                </thead>
                <tbody>
            """);

        foreach (var recordNode in records)
        {
            var record = recordNode?.AsObject();
            if (record is null)
            {
                continue;
            }

            summary.Append($"""
                <tr>
                  <td>{Html(ReadJsonString(record, "entity_type"))}</td>
                  <td>{Html(ReadJsonString(record, "entity_key"))}</td>
                  <td>{Html(ReadJsonString(record, "event_type"))}</td>
                  <td>{Html(ReadJsonString(record, "status"))}</td>
                  <td>{Html(FormatOptionalDate(ReadJsonString(record, "occurred_at_utc")))}</td>
                  <td>{Html(ReadJsonString(record, "trace_id"))}</td>
                </tr>
                """);
        }

        summary.Append("""
                </tbody>
              </table>
            </div>
            """);

        return summary.ToString();
    }

    private static string BuildRejectedSalesHtml(IReadOnlyList<PdvRejectedSaleRecord> rejectedSales)
    {
        if (rejectedSales.Count == 0)
        {
            return string.Empty;
        }

        var rows = new StringBuilder();
        foreach (var sale in rejectedSales)
        {
            rows.AppendLine($"""
                <tr>
                  <td>{Html(sale.SaleNumber)}</td>
                  <td>{Html(sale.SaleId.ToString())}</td>
                  <td>{Html(sale.TotalAmount.ToString("0.00", CultureInfo.InvariantCulture))}</td>
                  <td>{Html(sale.UpdatedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"))}</td>
                  <td>{Html(string.IsNullOrWhiteSpace(sale.RejectionReason) ? "-" : sale.RejectionReason)}</td>
                  <td>
                    <form method="post" action="/pdv-sales/reprocess">
                      <input type="hidden" name="sale_id" value="{Html(sale.SaleId.ToString())}">
                      <button class="inline-button" type="submit">Reprocessar</button>
                    </form>
                  </td>
                </tr>
                """);
        }

        return $$"""
            <section class="card" style="margin-top: 12px;">
              <div class="label">Vendas PDV rejeitadas para reprocessamento</div>
              <table>
                <thead>
                  <tr><th>Venda</th><th>ID</th><th>Total</th><th>Atualizada em</th><th>Motivo</th><th>Acao</th></tr>
                </thead>
                <tbody>
                  {{rows}}
                </tbody>
              </table>
            </section>
            """;
    }

    private static void AppendPill(StringBuilder builder, string label, JsonNode? value)
    {
        var text = value is null || value.GetValueKind() == JsonValueKind.Null
            ? "-"
            : value.ToJsonString();

        builder.Append($"<span class=\"pill\">{Html(label)}: {Html(text.Trim('\"'))}</span>");
    }

    private static string ReadJsonString(JsonObject record, string key)
    {
        var value = record[key];
        if (value is null || value.GetValueKind() == JsonValueKind.Null)
        {
            return "-";
        }

        return value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : value.ToJsonString();
    }

    private static long ReadJsonLong(JsonNode node, string key)
    {
        var value = node.AsObject()[key];
        if (value is null || value.GetValueKind() == JsonValueKind.Null)
        {
            return 0;
        }

        try
        {
            return value.GetValueKind() == JsonValueKind.Number
                ? value.GetValue<long>()
                : 0;
        }
        catch (FormatException)
        {
            return 0;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private static string FormatOptionalDate(string value)
    {
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss")
            : value;
    }
}
