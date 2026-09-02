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
using SyncAgent.Update;

namespace SyncAgent.LocalApi;

public sealed class LocalStatusServer : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private const int MaxSetupFormBytes = 8192;
    private const int MaxConfigFormBytes = 16384;
    private const string DashboardPath = "/";
    private const string SetupPath = "/setup";
    private const string ConfigPath = "/config";
    private const string ConfigArpaPath = "/config/arpa";
    private const string LogsPath = "/logs";
    private const string HelpPath = "/help";
    private const string StatusPath = "/status";

    private static readonly string[] PlaceholderErpUrls =
    {
        "https://localhost:5001",
        "http://127.0.0.1",
        "http://localhost",
    };

    private static readonly HashSet<string> TerminalActivationErrorCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "activation_code_used",
        "activation_code_expired",
        "activation_code_revoked",
        "activation_code_not_found",
        "already_provisioned",
        "activation_code_required",
        "tenant_invalid",
    };

    private readonly ILogger<LocalStatusServer> _logger;
    private readonly IOptionsMonitor<SyncAgentOptions> _options;
    private readonly IOptionsMonitor<SyncAgentProvisioningOptions> _provisioningOptions;
    private readonly IOptionsMonitor<ArpaCollectorOptions> _arpaOptions;
    private readonly EffectiveSyncAgentConfigurationProvider _effectiveConfigProvider;
    private readonly ErpActivationClient _erpActivationClient;
    private readonly ArpaConnectionsStore _arpaConnectionsStore;
    private readonly ArpaDdlRunner _arpaDdlRunner;
    private readonly LocalSyncStore _localStore;
    private readonly ManualSyncSignal _manualSyncSignal;
    private readonly SyncAgentRuntimeState _runtimeState;
    private readonly SelfUpdater _selfUpdater;
    private readonly UpdateProgressState _updateProgress;

    // Pre-preenchimento da tela /setup entre tentativas. A URL do ERP nao e segredo
    // e tambem e persistida em disco; o codigo de ativacao fica apenas aqui em
    // memoria e some no sucesso ou em erro terminal de codigo.
    private readonly object _setupHintLock = new();
    private string? _lastErpApiBaseUrlHint;
    private string? _pendingActivationCodeHint;

    public LocalStatusServer(
        ILogger<LocalStatusServer> logger,
        IOptionsMonitor<SyncAgentOptions> options,
        IOptionsMonitor<SyncAgentProvisioningOptions> provisioningOptions,
        IOptionsMonitor<ArpaCollectorOptions> arpaOptions,
        EffectiveSyncAgentConfigurationProvider effectiveConfigProvider,
        ErpActivationClient erpActivationClient,
        ArpaConnectionsStore arpaConnectionsStore,
        ArpaDdlRunner arpaDdlRunner,
        LocalSyncStore localStore,
        ManualSyncSignal manualSyncSignal,
        SyncAgentRuntimeState runtimeState,
        SelfUpdater selfUpdater,
        UpdateProgressState updateProgress)
    {
        _logger = logger;
        _options = options;
        _provisioningOptions = provisioningOptions;
        _arpaOptions = arpaOptions;
        _effectiveConfigProvider = effectiveConfigProvider;
        _erpActivationClient = erpActivationClient;
        _arpaConnectionsStore = arpaConnectionsStore;
        _arpaDdlRunner = arpaDdlRunner;
        _localStore = localStore;
        _manualSyncSignal = manualSyncSignal;
        _runtimeState = runtimeState;
        _selfUpdater = selfUpdater;
        _updateProgress = updateProgress;
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

            if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == ConfigPath)
            {
                await WriteConfigIndexAsync(context.Response, cancellationToken);
                return;
            }

            if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == ConfigArpaPath)
            {
                await WriteConfigArpaAsync(context.Response, context.Request.Url.Query, null, cancellationToken);
                return;
            }

            if (context.Request.HttpMethod == "POST"
                && context.Request.Url?.AbsolutePath is { } p
                && p.StartsWith("/config/arpa/", StringComparison.Ordinal))
            {
                await HandleConfigArpaPostAsync(context, p["/config/arpa/".Length..], cancellationToken);
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

            if (context.Request.HttpMethod == "POST" && context.Request.Url?.AbsolutePath == "/update-now")
            {
                if (_updateProgress.IsInProgress)
                {
                    await WriteJsonAsync(
                        context.Response,
                        HttpStatusCode.Conflict,
                        new { accepted = false, message = "Ja existe uma atualizacao em andamento." },
                        cancellationToken);
                    return;
                }

                _ = Task.Run(() => _selfUpdater.CheckAndApplyLatestAsync(CancellationToken.None));

                await WriteJsonAsync(
                    context.Response,
                    HttpStatusCode.Accepted,
                    new { accepted = true, message = "Busca pela versao mais recente iniciada." },
                    cancellationToken);
                return;
            }

            if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/update-status")
            {
                var snapshot = _updateProgress.Snapshot();
                await WriteJsonAsync(
                    context.Response,
                    HttpStatusCode.OK,
                    new
                    {
                        status = snapshot.Status.ToString(),
                        percent = snapshot.Percent,
                        message = snapshot.Message,
                        target_version = snapshot.TargetVersion,
                        error = snapshot.Error,
                        updated_at_utc = snapshot.UpdatedAtUtc
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
                needs_reactivation = effectiveOptions.NeedsReactivation,
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
        var activationStatus = effectiveOptions.NeedsReactivation
            ? "reconexao_necessaria"
            : effectiveOptions.IsProvisioned ? "provisioned" : "not_provisioned";
        var activationClass = effectiveOptions.IsProvisioned && !effectiveOptions.NeedsReactivation ? "ok" : "warn";
        var rejectedSalesHtml = BuildRejectedSalesHtml(rejectedSales);

        var html = $$"""
            <!doctype html>
            <html lang="pt-BR">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta http-equiv="refresh" content="15">
              <title>AraraSuite Sync</title>
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
                <h1>AraraSuite Sync</h1>
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
              <title>Ajuda - AraraSuite Sync</title>
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
            Get-Service "AraraSuiteSync"</pre>
                </section>

                <section class="panel">
                  <h2>Ativacao com o ERP e erros comuns</h2>
                  <p>A ativacao e feita em <a href="/setup">/setup</a>: informe a <strong>URL do ERP</strong> (ex.: <code>https://cliente.ararasuite.com.br</code>) e o <strong>codigo de ativacao</strong> gerado no ERP em <em>API de Sincronizacao &gt; Codigos de Ativacao &gt; Gerar</em>. O codigo e de uso unico e validade curta.</p>
                  <table>
                    <tr><th>Mensagem / code</th><th>Causa</th><th>Correcao</th></tr>
                    <tr><td><code>Aguardando ativacao com o ERP</code> (<code>not_provisioned</code>)</td><td>Estado normal antes de ativar.</td><td>Concluir a ativacao em <a href="/setup">/setup</a>.</td></tr>
                    <tr><td><code>Configure o ID do cliente no CP...</code> (na tela do ERP)</td><td>O ERP nao tem o vinculo com o Control Plane.</td><td>No CP, botao <strong>Aplicar Configuracoes</strong> no cliente. Requer imagem ERP &ge; 0.0.94.</td></tr>
                    <tr><td><code>invalid_response</code> — "must use HTTPS outside local development"</td><td>ERP antigo atras do proxy devolvia <code>http://</code> na URL da API.</td><td>Atualizar o ERP para &ge; 0.0.96. O codigo ja foi consumido: no ERP, apagar a instalacao orfa e gerar um novo.</td></tr>
                    <tr><td><code>activation_code_used</code></td><td>Codigo ja consumido (tentativa anterior, ou envio duplo do formulario).</td><td>Gerar novo. Se sobrou instalacao orfa (Visto por ultimo vazio) no ERP e for reusar o mesmo instance_id, apaga-la antes.</td></tr>
                    <tr><td><code>activation_code_expired</code> / <code>activation_code_revoked</code></td><td>Codigo fora da validade ou revogado.</td><td>Gerar um novo e usar em seguida.</td></tr>
                    <tr><td><code>activation_code_not_found</code></td><td>Codigo incompleto, ou URL do ERP aponta para outro cliente.</td><td>Conferir o codigo inteiro e a URL do ERP.</td></tr>
                    <tr><td><code>invalid_erp_url</code></td><td>URL nao e <code>https://</code> fora de localhost, ou malformada.</td><td>Corrigir a URL (fora da rede local exige HTTPS).</td></tr>
                    <tr><td><code>erp_unreachable</code> / <code>activation_timeout</code></td><td>A maquina nao alcanca o ERP (rede, firewall, DNS, URL errada) ou ele nao respondeu a tempo.</td><td>Testar <code>curl</code>/navegador ate a URL do ERP a partir desta maquina.</td></tr>
                    <tr><td><code>tenant_invalid</code></td><td>Codigo gerado antes do CP resolver o cliente.</td><td>No CP, Aplicar Configuracoes; depois gerar novo codigo.</td></tr>
                    <tr><td><code>http_404</code> / <code>http_5xx</code></td><td>Rota <code>/v1/sync/...</code> ausente nesse dominio, ou ERP com erro.</td><td>Conferir se a URL e a do ERP do cliente e se ele esta no ar.</td></tr>
                    <tr><td><code>Client certificate is required</code> (apos ativar, <code>runtime_status=degraded</code>)</td><td><code>ErpSecurity:RequireMutualTls=true</code> mas o ERP nao exige certificado cliente.</td><td>No <code>appsettings.json</code> do agente, <code>"RequireMutualTls": false</code> na secao <code>ErpSecurity</code>, e reiniciar <code>AraraSuiteSync</code>.</td></tr>
                    <tr><td><code>23505 ... operators_login_key</code> (import de operadores)</td><td>Banco local reaproveitado entre clientes diferentes.</td><td>Agente &ge; 1.3.1 (import resiliente). Instalacao nova nao apresenta isso.</td></tr>
                  </table>
                  <p style="margin-top:10px;">Reativar uma instalacao ja ativada: parar o servico, apagar <code>C:\Program Files\AraraSuite.com.br\Sync\Secrets\sync-agent-provisioning.dpapi</code>, reiniciar e ativar de novo em <a href="/setup">/setup</a>.</p>
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
                  <p><strong>Coletor Arpa</strong> (quando habilitado): le as views <code>sync_export.{produtos,clientes,estoque,vendas,financeiro}</code> no(s) banco(s) Arpa Control do cliente, com um usuario <em>read-only</em>. As conexoes sao configuradas em <a href="/config/arpa">Configuracoes &rsaquo; Arpa</a> (uma por Loja/Estoque do ERP; toggles por entidade; botoes de Testar, Preparar views e Criar usuario read-only). Erros comuns no log <code>SyncAgent.Collectors.ArpaCollector</code>:</p>
                  <table>
                    <tr><th>Erro</th><th>Causa</th><th>Correcao</th></tr>
                    <tr><td><code>42P01: relation "sync_export.produtos" does not exist</code></td><td>As views <code>sync_export</code> nunca foram criadas nesse banco Arpa.</td><td>DBA cria o schema/views (<code>infra/arpa/apply-arpa-sync-export-views.ps1</code>, geradas do diagnostico do schema real) + grants read-only.</td></tr>
                    <tr><td><code>42501: permission denied for relation ...</code></td><td>Usuario read-only do agente sem <code>GRANT SELECT</code> nas views.</td><td>Aplicar os grants de <code>sync-export-readonly-user-*.template.sql</code>.</td></tr>
                    <tr><td><code>42703: column "..." does not exist</code></td><td>A view <code>sync_export</code> referencia colunas que nao existem no Arpa daquele cliente.</td><td>Regenerar a view do diagnostico real do schema.</td></tr>
                  </table>
                  <p>Se o cliente <strong>nao usa Arpa Control</strong>, o coletor deve estar desligado (<code>ArpaCollector:Enabled=false</code> no <code>appsettings.json</code>). A partir de 1.3.3 uma entidade Arpa quebrada e <strong>pulada</strong> e nao derruba o ciclo (heartbeat / vendas PDV / dispatcher continuam). A partir de 1.4.0 o agente suporta varias conexoes Arpa, geridas localmente na aba Configuracoes.</p>
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
                  <h2>Atualizacao self-service (botao na bandeja)</h2>
                  <p>O usuario pode atualizar a maquina para a versao mais recente publicada a qualquer momento, sem depender do ERP agendar nada.</p>
                  <table>
                    <tr><th>Etapa</th><th>Descricao</th></tr>
                    <tr><td>1. Bandeja</td><td>Clique com o botao direito no icone da bandeja e escolha <strong>Atualizar App</strong>.</td></tr>
                    <tr><td>2. Busca</td><td>O agente consulta o ERP pela versao mais recente publicada (<code>is_current</code>), independente de qualquer agendamento administrativo pendente.</td></tr>
                    <tr><td>3. Progresso</td><td>Uma janela com barra de progresso acompanha download, verificacao de SHA256 e aplicacao em tempo real.</td></tr>
                    <tr><td>4. Aplicacao</td><td><code>self-update.ps1</code> faz backup, substitui binarios e reinicia o servico (mesmo mecanismo do fluxo administrativo, incluindo rollback automatico).</td></tr>
                    <tr><td>5. Retorno</td><td>Ao concluir, a bandeja reabre sozinha e mostra um aviso "Atualizacao concluida".</td></tr>
                  </table>
                  <p style="margin-top:10px;">Se a versao instalada ja for a mais recente, o agente informa e nao baixa nada.</p>
                  <p>Endpoints locais usados por esse fluxo:</p>
                  <pre>Invoke-RestMethod -Uri "http://127.0.0.1:{{options.LocalStatusPort}}/update-now" -Method Post
            Invoke-RestMethod -Uri "http://127.0.0.1:{{options.LocalStatusPort}}/update-status" -Method Get</pre>
                </section>

                <section class="panel">
                  <h2>Atualizacao administrativa (agendada pelo ERP)</h2>
                  <p>O operador do ERP pode enviar uma atualizacao remota para uma versao especifica, sem acesso direto a esta maquina.</p>
                  <table>
                    <tr><th>Etapa</th><th>Descricao</th></tr>
                    <tr><td>1. Pacote</td><td>O build script registra o pacote automaticamente em <strong>API de Sincronizacao &gt; Pacotes de atualizacao</strong> ao gerar o ZIP.</td></tr>
                    <tr><td>2. ERP Admin</td><td>Em <strong>API de Sincronizacao &gt; Instalacoes do PDV</strong>, selecione a instalacao, acao <em>Solicitar atualizacao</em>, escolha o pacote no dropdown e clique <em>Agendar atualizacao</em>.</td></tr>
                    <tr><td>3. Heartbeat</td><td>Em ate 30s, o agente recebe <code>pending_update</code> na resposta do ERP.</td></tr>
                    <tr><td>4. Download</td><td>O agente baixa o ZIP e verifica o SHA256 antes de prosseguir.</td></tr>
                    <tr><td>5. Atualizacao</td><td><code>self-update.ps1</code> faz backup, substitui binarios e reinicia o servico.</td></tr>
                    <tr><td>6. Rollback</td><td>Se o servico nao iniciar, o script restaura o backup automaticamente.</td></tr>
                  </table>
                  <p style="margin-top:10px;">O PDV App e a bandeja encerram durante a atualizacao (qualquer um dos dois fluxos). O tempo de interrupcao e inferior a 60 segundos. A bandeja reabre sozinha ao final.</p>
                  <p>Para solicitar verificacao imediata do agendamento administrativo (sem esperar o proximo ciclo, e sem buscar a ultima versao publicada):</p>
                  <pre>Invoke-RestMethod -Uri "http://127.0.0.1:{{options.LocalStatusPort}}/check-update" -Method Post</pre>
                  <p>Ou use o botao <strong>Verificar atualizacao</strong> em Detalhes Tecnicos no PDV App.</p>
                  <p>Log de atualizacao no Windows Event Log:</p>
                  <pre>Get-EventLog -LogName Application -Source "AraraSuite Sync Update" -Newest 20</pre>
                </section>

                <section class="panel">
                  <h2>Atualizacao manual (sem rede ate o ERP)</h2>
                  <p>O mesmo instalador usado para instalar do zero (<code>PdvLocalInstaller-vX.Y.Z.exe</code>) detecta uma instalacao existente e abre em modo <strong>Atualizar</strong>, aplicando o pacote embutido nele mesmo (util para levar por pendrive quando a maquina nao tem acesso ao ERP no momento, ou quando o servico nao esta rodando).</p>
                </section>

                <section class="panel">
                  <h2>Arquivos e servico Windows</h2>
                  <table>
                    <tr><th>Item</th><th>Caminho/valor</th></tr>
                    <tr><td>Servico (Get-Service)</td><td><code>AraraSuiteSync</code></td></tr>
                    <tr><td>Instalacao padrao</td><td><code>C:\Program Files\AraraSuite.com.br</code></td></tr>
                    <tr><td>API local</td><td><code>http://127.0.0.1:{{options.LocalStatusPort}}</code></td></tr>
                    <tr><td>Script de atualizacao</td><td><code>C:\Program Files\AraraSuite.com.br\Sync\Agent\self-update.ps1</code></td></tr>
                    <tr><td>Backup de versoes</td><td><code>C:\Program Files\AraraSuite.com.br\Backups\&lt;versao&gt;\</code></td></tr>
                    <tr><td>Versao instalada</td><td><code>C:\Program Files\AraraSuite.com.br\Sync\Agent\VERSION</code></td></tr>
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
              <title>Logs - AraraSuite Sync</title>
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
        var erpApiBaseUrl = form.GetValueOrDefault("erp_api_base_url", string.Empty).Trim();
        var activationCode = form.GetValueOrDefault("activation_code", string.Empty).Trim();

        var result = await _erpActivationClient.ActivateAsync(erpApiBaseUrl, activationCode, cancellationToken);

        // A URL do ERP nao e segredo: guarda em memoria e em disco para as proximas
        // tentativas (inclusive apos reinicio do servico).
        if (!string.IsNullOrWhiteSpace(erpApiBaseUrl))
        {
            lock (_setupHintLock)
            {
                _lastErpApiBaseUrlHint = erpApiBaseUrl;
            }

            PersistErpUrlHint(erpApiBaseUrl);
        }

        var codeIsTerminal = !string.IsNullOrWhiteSpace(result.Code)
            && TerminalActivationErrorCodes.Contains(result.Code);

        lock (_setupHintLock)
        {
            _pendingActivationCodeHint = result.Succeeded || codeIsTerminal ? null : activationCode;
        }

        if (result.Succeeded)
        {
            _manualSyncSignal.TrySignal();
        }

        var echoUrl = result.Succeeded ? null : erpApiBaseUrl;
        var echoCode = result.Succeeded || codeIsTerminal ? null : activationCode;
        await WriteSetupAsync(context.Response, result, echoUrl, echoCode, cancellationToken);
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
        => await WriteSetupAsync(response, activationResult, null, null, cancellationToken);

    private async Task WriteSetupAsync(
        HttpListenerResponse response,
        ActivationResult? activationResult,
        string? submittedErpApiBaseUrl,
        string? submittedActivationCode,
        CancellationToken cancellationToken)
    {
        var effectiveOptions = _effectiveConfigProvider.GetCurrent();
        var messageHtml = activationResult is null
            ? string.Empty
            : BuildActivationMessageHtml(activationResult);

        var estadoAtual = effectiveOptions.NeedsReactivation
            ? "reconexao necessaria"
            : effectiveOptions.IsProvisioned ? "ativado" : "aguardando ativacao";

        var prefillUrl = ResolvePrefillErpUrl(submittedErpApiBaseUrl, effectiveOptions);
        string prefillCode;
        lock (_setupHintLock)
        {
            prefillCode = submittedActivationCode ?? _pendingActivationCodeHint ?? string.Empty;
        }

        var reactivationNoticeHtml = effectiveOptions.IsProvisioned && effectiveOptions.NeedsReactivation
            ? $$"""
                <div class="message warnbox">
                  A conexao desta instalacao com o ERP expirou (token de renovacao vencido) e a sincronizacao esta parada
                  desde entao — operadores, produtos e vendas nao sao mais atualizados automaticamente.
                  Peca a um administrador do ERP para gerar um novo codigo em
                  <strong>API de Sincronizacao &gt; Codigos de Ativacao &gt; Gerar codigo de ativacao</strong>
                  e informe-o abaixo para reconectar esta instalacao (instancia anterior: <code>{{Html(effectiveOptions.InstanceId)}}</code>).
                </div>
                """
            : string.Empty;

        var formHtml = effectiveOptions.IsProvisioned && !effectiveOptions.NeedsReactivation
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
            : $$"""
                <section class="panel">
                  <h2>{{(effectiveOptions.IsProvisioned ? "Reconectar ao ERP" : "Ativar conexao com o ERP")}}</h2>
                  <form method="post" action="/setup/activate">
                    <label for="erp_api_base_url">URL do ERP</label>
                    <input id="erp_api_base_url" name="erp_api_base_url" type="url" required placeholder="https://erp.exemplo.com" value="{{Html(prefillUrl)}}">
                    <label for="activation_code">Codigo de ativacao</label>
                    <input id="activation_code" name="activation_code" type="text" required autocomplete="off" value="{{Html(prefillCode)}}">
                    <button type="submit">{{(effectiveOptions.IsProvisioned ? "Reconectar" : "Conectar")}}</button>
                  </form>
                  <p class="muted">A URL do ERP fica salva para a proxima vez. O codigo de ativacao e de uso unico e nunca e gravado em disco.</p>
                </section>
                """;

        var html = $$"""
            <!doctype html>
            <html lang="pt-BR">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Ativacao - AraraSuite Sync</title>
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
                <p class="muted">Estado atual: <strong>{{Html(estadoAtual)}}</strong></p>
                {{LocalNavHtml(SetupPath)}}
                {{messageHtml}}
                {{reactivationNoticeHtml}}
                {{formHtml}}
                <section class="panel">
                  <h2>Regra de seguranca</h2>
                  <p>O SyncAgent nao armazena usuario e senha do ERP. O codigo de ativacao e usado uma unica vez para emitir credenciais tecnicas desta maquina.</p>
                  <p>Enquanto a instalacao estiver <code>not_provisioned</code> ou precisar de reconexao, a sincronizacao permanece bloqueada.</p>
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

    private string ResolvePrefillErpUrl(string? submitted, EffectiveSyncAgentConfiguration effectiveOptions)
    {
        if (!string.IsNullOrWhiteSpace(submitted))
        {
            return submitted.Trim();
        }

        lock (_setupHintLock)
        {
            if (!string.IsNullOrWhiteSpace(_lastErpApiBaseUrlHint))
            {
                return _lastErpApiBaseUrlHint!;
            }
        }

        var fromDisk = ReadErpUrlHintFromDisk();
        if (!string.IsNullOrWhiteSpace(fromDisk))
        {
            return fromDisk!;
        }

        if (effectiveOptions.IsProvisioned && !string.IsNullOrWhiteSpace(effectiveOptions.ErpApiBaseUrl))
        {
            return effectiveOptions.ErpApiBaseUrl;
        }

        var configured = _options.CurrentValue.ErpApiBaseUrl;
        if (!string.IsNullOrWhiteSpace(configured) && !IsPlaceholderErpUrl(configured))
        {
            return configured;
        }

        return string.Empty;
    }

    private static bool IsPlaceholderErpUrl(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        foreach (var placeholder in PlaceholderErpUrls)
        {
            if (string.Equals(placeholder, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private string? ResolveSetupHintFilePath()
    {
        var configured = _provisioningOptions.CurrentValue.SetupHintFile;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var protectedFile = _provisioningOptions.CurrentValue.ProtectedFile;
        if (string.IsNullOrWhiteSpace(protectedFile))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(protectedFile));
        return string.IsNullOrWhiteSpace(directory)
            ? "setup-hint.json"
            : Path.Combine(directory, "setup-hint.json");
    }

    private string? ReadErpUrlHintFromDisk()
    {
        try
        {
            var path = ResolveSetupHintFilePath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            var node = JsonNode.Parse(File.ReadAllText(path));
            var url = node?["erpApiBaseUrl"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(url) ? null : url;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao ler o hint da URL do ERP para a tela de ativacao.");
            return null;
        }
    }

    private void PersistErpUrlHint(string erpApiBaseUrl)
    {
        try
        {
            var path = ResolveSetupHintFilePath();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(new { erpApiBaseUrl = erpApiBaseUrl.Trim() }, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao gravar o hint da URL do ERP para a tela de ativacao.");
        }
    }

    private static string BuildActivationMessageHtml(ActivationResult result)
    {
        if (result.Succeeded)
        {
            return "<div class=\"message okbox\">&#9989; Ativacao aceita pelo ERP. Sincronizacao iniciada."
                + $"<br><span style=\"font-weight:400\">{Html(result.Message)}</span></div>";
        }

        var codePrefix = string.IsNullOrWhiteSpace(result.Code)
            ? string.Empty
            : $"<code>{Html(result.Code)}</code> &mdash; ";
        var hint = ActivationErrorHint(result.Code);
        var hintHtml = string.IsNullOrEmpty(hint)
            ? string.Empty
            : $"<br><span style=\"font-weight:400\">{Html(hint)}</span>";
        return $"<div class=\"message warnbox\">{codePrefix}{Html(result.Message)}{hintHtml}</div>";
    }

    private static string ActivationErrorHint(string? code)
    {
        if (code is null)
        {
            return string.Empty;
        }

        return code switch
        {
            "activation_code_used" => "Esse codigo ja foi consumido. Gere um novo em API de Sincronizacao > Codigos de Ativacao no ERP e, se sobrar uma instalacao orfa, remova-a antes.",
            "activation_code_expired" => "O codigo expirou. Gere um novo no ERP e use em seguida.",
            "activation_code_revoked" => "O codigo foi revogado. Gere um novo no ERP.",
            "activation_code_not_found" => "Codigo nao encontrado neste ERP. Confira se copiou o codigo inteiro e se a URL do ERP e a do cliente certo.",
            "already_provisioned" => "Esta instalacao ja esta ativada. Reconexao exige acao administrativa de reprovisionamento.",
            "invalid_erp_url" => "Confira o endereco do ERP (fora de localhost precisa comecar com https://).",
            "erp_unreachable" => "O agente nao alcancou o ERP. Verifique rede, firewall e se a URL esta correta.",
            "activation_timeout" => "O ERP demorou para responder. Verifique a conexao e tente de novo.",
            "invalid_response" => "O ERP respondeu em formato inesperado. Confira a versao do ERP.",
            "tenant_invalid" => "O ERP nao tem o ID do cliente do CP configurado. Ajuste em Configuracoes > Plano / Aplicar Configuracoes no CP.",
            _ when code.StartsWith("http_", StringComparison.OrdinalIgnoreCase)
                => "URL do ERP correta? Pode faltar a rota /v1/sync neste dominio.",
            _ => string.Empty,
        };
    }

    private async Task WriteHtmlAsync(HttpListenerResponse response, string html, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/html; charset=utf-8";
        AddNoStoreHeaders(response);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
        response.Close();
    }

    private static string? ReadQueryParam(string? queryString, string name)
    {
        if (string.IsNullOrEmpty(queryString))
        {
            return null;
        }

        foreach (var part in queryString.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (WebUtility.UrlDecode(kv[0]) == name)
            {
                return kv.Length > 1 ? WebUtility.UrlDecode(kv[1]) : string.Empty;
            }
        }

        return null;
    }

    private async Task WriteConfigIndexAsync(HttpListenerResponse response, CancellationToken cancellationToken)
    {
        var arpaEnabled = _arpaOptions.CurrentValue.Enabled;
        var arpaCount = _arpaConnectionsStore.ReadAll().Count;
        var arpaLine = arpaEnabled
            ? $"{arpaCount} conexao(oes) configurada(s)."
            : "Coletor desligado (ArpaCollector:Enabled=false no appsettings.json).";

        var html = $$"""
            <!doctype html>
            <html lang="pt-BR">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Configuracoes - AraraSuite Sync</title>
              <style>
                body { font-family: Segoe UI, Arial, sans-serif; margin: 32px; color: #1f2937; background: #f8fafc; }
                main { max-width: 860px; margin: 0 auto; }
                h1 { margin-bottom: 4px; font-size: 28px; }
                h2 { margin: 0 0 6px; font-size: 18px; }
                .muted { color: #64748b; margin: 0; }
                {{BaseStyles}}
                .panel { background: white; border: 1px solid #e2e8f0; border-radius: 8px; padding: 18px; margin: 16px 0; }
                a.cardlink { display: block; text-decoration: none; color: inherit; }
                a.cardlink:hover .panel { border-color: #93c5fd; }
              </style>
            </head>
            <body>
              <main>
                <h1>Configuracoes</h1>
                <p class="muted">Configuracao local deste agente.</p>
                {{LocalNavHtml(ConfigPath)}}
                <a class="cardlink" href="/config/arpa">
                  <section class="panel">
                    <h2>Arpa Control</h2>
                    <p class="muted">Conexoes com bancos Arpa Control para importar produtos, clientes e estoque. {{Html(arpaLine)}}</p>
                  </section>
                </a>
              </main>
            </body>
            </html>
            """;

        await WriteHtmlAsync(response, html, cancellationToken);
    }

    private async Task WriteConfigArpaAsync(
        HttpListenerResponse response,
        string? queryString,
        string? flashMessage,
        CancellationToken cancellationToken)
    {
        var enabled = _arpaOptions.CurrentValue.Enabled;
        var connections = _arpaConnectionsStore.ReadAll();

        var msg = flashMessage ?? ReadQueryParam(queryString, "msg");

        var rows = new StringBuilder();
        if (connections.Count == 0)
        {
            rows.Append("<tr><td colspan=\"6\" class=\"muted\">Nenhuma conexao. Use o formulario abaixo para adicionar.</td></tr>");
        }

        foreach (var c in connections)
        {
            var toggles = string.Join(" ", new[]
            {
                c.SyncProdutos ? "Produtos" : null,
                c.SyncClientes ? "Clientes" : null,
                c.SyncEstoque ? "Estoque" : null,
                c.SyncVendas ? "Vendas" : null,
                c.SyncFinanceiro ? "Financeiro" : null,
            }.Where(t => t is not null));

            rows.Append($$"""
                <tr>
                  <td>{{Html(c.Nome)}}{{(c.Enabled ? "" : " <span class=\"muted\">(inativa)</span>")}}</td>
                  <td>{{Html($"{c.Host}:{c.Port}/{c.Database}")}}</td>
                  <td>{{Html(string.IsNullOrWhiteSpace(c.LojaCodigo) ? "-" : c.LojaCodigo)}}</td>
                  <td>{{Html(toggles)}}</td>
                  <td>{{Html(c.Username)}}</td>
                  <td style="white-space:nowrap">
                    <button type="button" class="mini" onclick="editConn('{{Html(c.Id)}}')">Editar</button>
                    <button type="button" class="mini" onclick="postAct('sync-now','{{Html(c.Id)}}')">Sincronizar</button>
                    <button type="button" class="mini danger" onclick="if(confirm('Remover a conexao {{Html(c.Nome)}}?'))postAct('delete','{{Html(c.Id)}}')">Remover</button>
                  </td>
                </tr>
                """);
        }

        // Senha NAO vai para o navegador; ao editar, o campo fica vazio e o save
        // mantem a senha atual quando enviado em branco.
        var connectionsJson = JsonSerializer.Serialize(
            connections.ToDictionary(
                c => c.Id,
                c => new
                {
                    id = c.Id,
                    nome = c.Nome,
                    host = c.Host,
                    port = c.Port,
                    database = c.Database,
                    username = c.Username,
                    lojaCodigo = c.LojaCodigo,
                    controlaEstoque = c.ControlaEstoque,
                    syncProdutos = c.SyncProdutos,
                    syncClientes = c.SyncClientes,
                    syncEstoque = c.SyncEstoque,
                    syncVendas = c.SyncVendas,
                    syncFinanceiro = c.SyncFinanceiro,
                    batchSize = c.BatchSize,
                    enabled = c.Enabled,
                }),
            JsonOptions);
        var msgHtml = string.IsNullOrWhiteSpace(msg)
            ? string.Empty
            : $"""<div class="message okbox">{Html(msg!)}</div>""";
        var disabledNote = enabled
            ? string.Empty
            : """<div class="message warnbox">O coletor Arpa esta desligado. Ative com <code>ArpaCollector:Enabled=true</code> no appsettings.json e reinicie o servico para as conexoes abaixo passarem a coletar.</div>""";

        var html = $$"""
            <!doctype html>
            <html lang="pt-BR">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Configuracoes - Arpa - AraraSuite Sync</title>
              <style>
                body { font-family: Segoe UI, Arial, sans-serif; margin: 32px; color: #1f2937; background: #f8fafc; }
                main { max-width: 960px; margin: 0 auto; }
                h1 { margin-bottom: 4px; font-size: 26px; }
                h2 { font-size: 18px; }
                .muted { color: #64748b; }
                {{BaseStyles}}
                .panel { background: white; border: 1px solid #e2e8f0; border-radius: 8px; padding: 18px; margin: 16px 0; }
                table { width: 100%; border-collapse: collapse; }
                th, td { border-bottom: 1px solid #e2e8f0; padding: 8px; text-align: left; font-size: 14px; vertical-align: top; }
                th { color: #475569; font-size: 13px; }
                label { display: block; color: #64748b; font-size: 13px; margin: 10px 0 4px; }
                input[type=text], input[type=password], input[type=number] { width: 100%; box-sizing: border-box; padding: 8px; border: 1px solid #cbd5e1; border-radius: 6px; font-size: 14px; }
                .row2 { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
                .row3 { display: grid; grid-template-columns: 2fr 1fr 1fr; gap: 12px; }
                .checks { display: flex; gap: 16px; margin-top: 10px; flex-wrap: wrap; align-items: center; }
                .checks label { display: inline-flex; align-items: center; gap: 6px; margin: 0; color: #1f2937; }
                button { padding: 9px 13px; border: 0; border-radius: 6px; background: #2563eb; color: white; cursor: pointer; font-size: 14px; }
                button:hover { background: #1d4ed8; }
                button.secondary { background: #64748b; }
                button.mini { padding: 5px 8px; font-size: 12px; background: #eef2ff; color: #3730a3; }
                button.mini.danger { background: #fee2e2; color: #991b1b; }
                button.danger { background: #dc2626; }
                .message { border-radius: 8px; padding: 12px; margin: 14px 0; font-weight: 600; }
                .okbox { background: #dcfce7; color: #166534; }
                .warnbox { background: #fef3c7; color: #92400e; }
                #status { margin-top: 10px; font-weight: 600; white-space: pre-wrap; }
                details { margin-top: 14px; border-top: 1px solid #e2e8f0; padding-top: 10px; }
                summary { cursor: pointer; font-weight: 600; color: #334155; }
              </style>
            </head>
            <body>
              <main>
                <h1>Configuracoes &rsaquo; Arpa</h1>
                <p class="muted">Cada conexao aponta para um banco Arpa Control e e mapeada a uma Loja/Estoque do ERP. As conexoes ficam salvas nesta maquina (cifradas por DPAPI).</p>
                {{LocalNavHtml(ConfigPath)}}
                {{msgHtml}}
                {{disabledNote}}

                <section class="panel">
                  <h2>Conexoes</h2>
                  <table>
                    <tr><th>Nome</th><th>Banco</th><th>Loja</th><th>Sincroniza</th><th>Usuario</th><th></th></tr>
                    {{rows}}
                  </table>
                </section>

                <section class="panel">
                  <h2 id="formtitle">Adicionar conexao</h2>
                  <form id="connform">
                    <input type="hidden" name="id" id="f_id">
                    <div class="row2">
                      <div><label for="f_nome">Nome</label><input type="text" id="f_nome" name="nome" required placeholder="Ex.: Loja Centro"></div>
                      <div><label for="f_loja">Loja/Estoque (nome no ERP)</label><input type="text" id="f_loja" name="loja_codigo" placeholder="Ex.: Centro"></div>
                    </div>
                    <div class="row3">
                      <div><label for="f_host">Host</label><input type="text" id="f_host" name="host" required placeholder="127.0.0.1"></div>
                      <div><label for="f_port">Porta</label><input type="number" id="f_port" name="port" value="5432" min="1" max="65535"></div>
                      <div><label for="f_db">Database</label><input type="text" id="f_db" name="database" required placeholder="control"></div>
                    </div>
                    <div class="row3">
                      <div><label for="f_user">Usuario (read-only)</label><input type="text" id="f_user" name="username" required placeholder="ararasuite_sync_ro"></div>
                      <div><label for="f_pass">Senha</label><input type="password" id="f_pass" name="password" autocomplete="off"></div>
                      <div><label for="f_batch">Batch size</label><input type="number" id="f_batch" name="batch_size" value="5000" min="1" max="50000"></div>
                    </div>
                    <div class="checks">
                      <label><input type="checkbox" id="f_produtos" name="sync_produtos" checked> Produtos</label>
                      <label><input type="checkbox" id="f_clientes" name="sync_clientes" checked> Clientes</label>
                      <label><input type="checkbox" id="f_estoque" name="sync_estoque"> Estoque</label>
                      <label><input type="checkbox" id="f_vendas" name="sync_vendas"> Vendas</label>
                      <label><input type="checkbox" id="f_financeiro" name="sync_financeiro"> Financeiro</label>
                      <label><input type="checkbox" id="f_controla" name="controla_estoque"> Controla o estoque desta Loja</label>
                      <label><input type="checkbox" id="f_enabled" name="enabled" checked> Ativa</label>
                    </div>

                    <div style="margin-top:16px; display:flex; gap:10px; flex-wrap:wrap">
                      <button type="button" onclick="save()">Salvar</button>
                      <button type="button" class="secondary" onclick="act('test')">Testar conexao</button>
                      <button type="button" class="secondary" onclick="resetForm()">Limpar</button>
                    </div>

                    <details>
                      <summary>Preparar banco Arpa (requer credencial DBA)</summary>
                      <p class="muted">Usa uma credencial de administrador do Postgres do Arpa apenas para este comando. Nao e gravada.</p>
                      <div class="row2">
                        <div><label for="f_dbauser">Usuario DBA</label><input type="text" id="f_dbauser" name="dba_user" autocomplete="off" placeholder="postgres"></div>
                        <div><label for="f_dbapass">Senha DBA</label><input type="password" id="f_dbapass" name="dba_password" autocomplete="off"></div>
                      </div>
                      <div style="margin-top:10px; display:flex; gap:10px; flex-wrap:wrap">
                        <button type="button" class="secondary" onclick="act('prepare-views')">Preparar views sync_export</button>
                      </div>
                      <div class="row2" style="margin-top:12px">
                        <div><label for="f_newrole">Novo usuario read-only</label><input type="text" id="f_newrole" name="new_role" autocomplete="off" placeholder="ararasuite_sync_ro"></div>
                        <div><label for="f_newrolepass">Senha do novo usuario</label><input type="password" id="f_newrolepass" name="new_role_password" autocomplete="off"></div>
                      </div>
                      <div style="margin-top:10px"><button type="button" class="secondary" onclick="act('create-user')">Criar usuario read-only</button></div>
                    </details>
                  </form>
                  <div id="status"></div>
                </section>
              </main>
              <script>
                var CONNS = {{connectionsJson}};
                function fd(){ return new URLSearchParams(new FormData(document.getElementById('connform'))); }
                function setStatus(t, ok){ var s=document.getElementById('status'); s.textContent=t; s.style.color = ok ? '#166534' : '#92400e'; }
                function resetForm(){ document.getElementById('connform').reset(); document.getElementById('f_id').value=''; document.getElementById('formtitle').textContent='Adicionar conexao'; setStatus(''); }
                function editConn(id){
                  var c = CONNS[id]; if(!c) return;
                  document.getElementById('f_id').value = c.id;
                  document.getElementById('f_nome').value = c.nome || '';
                  document.getElementById('f_loja').value = c.lojaCodigo || '';
                  document.getElementById('f_host').value = c.host || '';
                  document.getElementById('f_port').value = c.port || 5432;
                  document.getElementById('f_db').value = c.database || '';
                  document.getElementById('f_user').value = c.username || '';
                  document.getElementById('f_pass').value = c.password || '';
                  document.getElementById('f_batch').value = c.batchSize || 5000;
                  document.getElementById('f_produtos').checked = !!c.syncProdutos;
                  document.getElementById('f_clientes').checked = !!c.syncClientes;
                  document.getElementById('f_estoque').checked = !!c.syncEstoque;
                  document.getElementById('f_vendas').checked = !!c.syncVendas;
                  document.getElementById('f_financeiro').checked = !!c.syncFinanceiro;
                  document.getElementById('f_controla').checked = !!c.controlaEstoque;
                  document.getElementById('f_enabled').checked = !!c.enabled;
                  document.getElementById('formtitle').textContent = 'Editar conexao: ' + (c.nome||'');
                  window.scrollTo(0, document.getElementById('formtitle').offsetTop - 20);
                }
                function save(){
                  setStatus('Salvando...', true);
                  fetch('/config/arpa/save', { method:'POST', body: fd() })
                    .then(function(r){ return r.json(); })
                    .then(function(j){ if(j.ok){ location.href = '/config/arpa?msg=' + encodeURIComponent(j.message || 'Conexao salva.'); } else { setStatus(j.message || 'Falha ao salvar.', false); } })
                    .catch(function(e){ setStatus(String(e), false); });
                }
                function act(a){
                  setStatus('Executando ' + a + '...', true);
                  fetch('/config/arpa/' + a, { method:'POST', body: fd() })
                    .then(function(r){ return r.json(); })
                    .then(function(j){ setStatus(j.message || (j.ok ? 'OK.' : 'Falha.'), !!j.ok); })
                    .catch(function(e){ setStatus(String(e), false); });
                }
                function postAct(a, id){
                  var b = new URLSearchParams(); b.set('id', id);
                  fetch('/config/arpa/' + a, { method:'POST', body: b })
                    .then(function(r){ return r.json(); })
                    .then(function(j){ location.href = '/config/arpa?msg=' + encodeURIComponent(j.message || 'OK.'); })
                    .catch(function(e){ setStatus(String(e), false); });
                }
              </script>
            </body>
            </html>
            """;

        await WriteHtmlAsync(response, html, cancellationToken);
    }

    private async Task HandleConfigArpaPostAsync(HttpListenerContext context, string action, CancellationToken cancellationToken)
    {
        Dictionary<string, string> form;
        try
        {
            form = await ReadFormAsync(context.Request, MaxConfigFormBytes, cancellationToken);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { ok = false, message = ex.Message }, cancellationToken);
            return;
        }

        string V(string k) => form.GetValueOrDefault(k, string.Empty).Trim();
        bool B(string k) => form.ContainsKey(k) && form[k] is "on" or "true" or "1";
        int I(string k, int fallback) => int.TryParse(V(k), out var n) ? n : fallback;

        switch (action)
        {
            case "save":
            {
                if (string.IsNullOrWhiteSpace(V("nome")) || string.IsNullOrWhiteSpace(V("host")) || string.IsNullOrWhiteSpace(V("database")))
                {
                    await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { ok = false, message = "Nome, host e database sao obrigatorios." }, cancellationToken);
                    return;
                }

                var existing = string.IsNullOrWhiteSpace(V("id")) ? null : _arpaConnectionsStore.Get(V("id"));
                var connection = new ArpaLocalConnection
                {
                    Id = V("id"),
                    Nome = V("nome"),
                    Host = V("host"),
                    Port = I("port", 5432),
                    Database = V("database"),
                    Username = V("username"),
                    // senha em branco no form de edicao mantem a atual
                    Password = string.IsNullOrEmpty(V("password")) && existing is not null ? existing.Password : V("password"),
                    LojaCodigo = V("loja_codigo"),
                    ControlaEstoque = B("controla_estoque"),
                    SyncProdutos = B("sync_produtos"),
                    SyncClientes = B("sync_clientes"),
                    SyncEstoque = B("sync_estoque"),
                    SyncVendas = B("sync_vendas"),
                    SyncFinanceiro = B("sync_financeiro"),
                    BatchSize = I("batch_size", 5000),
                    Enabled = B("enabled"),
                };

                var id = _arpaConnectionsStore.Upsert(connection);
                _manualSyncSignal.TrySignal();
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { ok = true, id, message = "Conexao salva." }, cancellationToken);
                return;
            }

            case "delete":
            {
                var removed = _arpaConnectionsStore.Delete(V("id"));
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { ok = removed, message = removed ? "Conexao removida." : "Conexao nao encontrada." }, cancellationToken);
                return;
            }

            case "sync-now":
            {
                var accepted = _manualSyncSignal.TrySignal();
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { ok = true, accepted, message = accepted ? "Sincronizacao solicitada." : "Ja existe uma sincronizacao pendente." }, cancellationToken);
                return;
            }

            case "test":
            {
                var result = await _arpaDdlRunner.TestConnectionAsync(
                    V("host"), I("port", 5432), V("database"), V("username"), V("password"),
                    B("sync_produtos"), B("sync_clientes"), B("sync_estoque"), B("sync_vendas"), B("sync_financeiro"),
                    cancellationToken);
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { ok = result.Ok, message = result.Message }, cancellationToken);
                return;
            }

            case "prepare-views":
            {
                if (string.IsNullOrWhiteSpace(V("dba_user")) || string.IsNullOrEmpty(V("dba_password")))
                {
                    await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { ok = false, message = "Informe usuario e senha DBA." }, cancellationToken);
                    return;
                }

                var result = await _arpaDdlRunner.PrepareViewsAsync(
                    V("host"), I("port", 5432), V("database"), V("dba_user"), V("dba_password"), cancellationToken);
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { ok = result.Ok, message = result.Message }, cancellationToken);
                return;
            }

            case "create-user":
            {
                if (string.IsNullOrWhiteSpace(V("dba_user")) || string.IsNullOrEmpty(V("dba_password")))
                {
                    await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { ok = false, message = "Informe usuario e senha DBA." }, cancellationToken);
                    return;
                }

                var result = await _arpaDdlRunner.CreateReadonlyUserAsync(
                    V("host"), I("port", 5432), V("database"), V("dba_user"), V("dba_password"),
                    V("new_role"), V("new_role_password"), cancellationToken);
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { ok = result.Ok, message = result.Message }, cancellationToken);
                return;
            }

            default:
                await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new { ok = false, message = "Acao desconhecida." }, cancellationToken);
                return;
        }
    }

    private static Task<Dictionary<string, string>> ReadFormAsync(
        HttpListenerRequest request,
        CancellationToken cancellationToken)
        => ReadFormAsync(request, MaxSetupFormBytes, cancellationToken);

    private static async Task<Dictionary<string, string>> ReadFormAsync(
        HttpListenerRequest request,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength64 > maxBytes)
        {
            throw new InvalidOperationException("Form body exceeded the maximum accepted size.");
        }

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        var body = await reader.ReadToEndAsync(cancellationToken);
        if (Encoding.UTF8.GetByteCount(body) > maxBytes)
        {
            throw new InvalidOperationException("Form body exceeded the maximum accepted size.");
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
                  {LocalNavLink(ConfigPath, "Configuracoes", activePath)}
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
