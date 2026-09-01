param(
    [string]$InstallRoot = "C:\Program Files\AraraSuite.com.br",
    [string]$ServiceName = "AraraSuiteSync",
    [string]$LocalStatusUrl = "http://127.0.0.1:47891",
    [string]$PsqlPath = "psql",
    [string]$PostgresHost = "localhost",
    [int]$PostgresPort = 5432,
    [string]$DatabaseName = "pdv",
    [string]$DatabaseUser = "araras",
    [string]$DatabasePassword = "",
    [string]$ErpApiBaseUrl = "",
    [string]$InstanceId = "",
    [string]$AccessTokenFile = "",
    [switch]$SkipDatabase,
    [switch]$SkipErp,
    [string]$EvidenceOutput = ".\artifacts\sync-agent-clean-install-evidence.json"
)

$ErrorActionPreference = "Stop"

function Add-Check {
    param(
        [System.Collections.Generic.List[object]]$Checks,
        [string]$Name,
        [bool]$Passed,
        [string]$Message
    )

    $Checks.Add([ordered]@{
        name = $Name
        passed = $Passed
        message = $Message
    }) | Out-Null
}

function Invoke-PsqlScalar {
    param([string]$Sql)

    if ([string]::IsNullOrWhiteSpace($DatabasePassword)) {
        throw "Informe -DatabasePassword ou use -SkipDatabase."
    }

    if (-not (Get-Command $PsqlPath -ErrorAction SilentlyContinue)) {
        $artifactPsql = Join-Path (Get-Location) "artifacts\postgresql-17\extracted\pgsql\bin\psql.exe"
        if (Test-Path -LiteralPath $artifactPsql) {
            $PsqlPath = $artifactPsql
        }
        else {
            throw "psql nao encontrado: $PsqlPath."
        }
    }

    $previousPassword = $env:PGPASSWORD
    try {
        $env:PGPASSWORD = $DatabasePassword
        $result = & $PsqlPath `
            -h $PostgresHost `
            -p $PostgresPort `
            -U $DatabaseUser `
            -d $DatabaseName `
            -X -q -t -A `
            -v ON_ERROR_STOP=1 `
            -c $Sql

        if ($LASTEXITCODE -ne 0) {
            throw "psql retornou codigo $LASTEXITCODE."
        }

        return ($result | Select-Object -First 1).Trim()
    }
    finally {
        $env:PGPASSWORD = $previousPassword
    }
}

function Get-SpecialFolderPathOrFallback {
    param(
        [string]$Primary,
        [string]$Fallback
    )

    $path = [Environment]::GetFolderPath($Primary)
    if ([string]::IsNullOrWhiteSpace($path)) {
        $path = [Environment]::GetFolderPath($Fallback)
    }

    return $path
}

$checks = [System.Collections.Generic.List[object]]::new()
$baseUrl = $LocalStatusUrl.TrimEnd("/")
$evidence = [ordered]@{
    generated_at_utc = [DateTimeOffset]::UtcNow
    machine_name = $env:COMPUTERNAME
    install_root = $InstallRoot
    service_name = $ServiceName
    local_status_url = $baseUrl
    checks = $checks
    local_status = $null
    database = $null
}

try {
    $service = Get-Service -Name $ServiceName -ErrorAction Stop
    Add-Check $checks "service_exists" $true "Servico encontrado: $($service.Name)."
    Add-Check $checks "service_running" ($service.Status -eq "Running") "Status do servico: $($service.Status)."
}
catch {
    Add-Check $checks "service_exists" $false $_.Exception.Message
}

try {
    $agentPath = Join-Path $InstallRoot "Sync\Agent\SyncAgent.exe"
    $trayPath = Join-Path $InstallRoot "Sync\Tray\SyncAgent.Tray.exe"
    $pdvAppPath = Join-Path $InstallRoot "PDV\PdvLocal.App.exe"
    $pdvCorePath = Join-Path $InstallRoot "PDV\PdvLocal.Core.dll"
    $configPath = Join-Path $InstallRoot "Sync\Agent\appsettings.json"
    $pdvAppConfigPath = Join-Path $InstallRoot "PDV\appsettings.json"
    $desktopDirectory = Get-SpecialFolderPathOrFallback -Primary "CommonDesktopDirectory" -Fallback "Desktop"
    $programsDirectory = Get-SpecialFolderPathOrFallback -Primary "CommonPrograms" -Fallback "Programs"
    $desktopShortcutPath = Join-Path $desktopDirectory "AraraSuite PDV.lnk"
    $programsShortcutPath = Join-Path (Join-Path $programsDirectory "AraraSuite") "AraraSuite PDV.lnk"

    Add-Check $checks "agent_payload" (Test-Path -LiteralPath $agentPath) "SyncAgent.exe em $agentPath."
    Add-Check $checks "tray_payload" (Test-Path -LiteralPath $trayPath) "SyncAgent.Tray.exe em $trayPath."
    Add-Check $checks "pdv_app_payload" (Test-Path -LiteralPath $pdvAppPath) "PdvLocal.App.exe em $pdvAppPath."
    Add-Check $checks "pdv_core_payload" (Test-Path -LiteralPath $pdvCorePath) "PdvLocal.Core.dll em $pdvCorePath."
    Add-Check $checks "appsettings_exists" (Test-Path -LiteralPath $configPath) "appsettings.json em $configPath."
    Add-Check $checks "pdv_appsettings_exists" (Test-Path -LiteralPath $pdvAppConfigPath) "appsettings.json em $pdvAppConfigPath."
    Add-Check $checks "pdv_desktop_shortcut" (Test-Path -LiteralPath $desktopShortcutPath) "Atalho Desktop em $desktopShortcutPath."
    Add-Check $checks "pdv_start_menu_shortcut" (Test-Path -LiteralPath $programsShortcutPath) "Atalho Menu Iniciar em $programsShortcutPath."

    if (Test-Path -LiteralPath $configPath) {
        $configText = Get-Content -LiteralPath $configPath -Raw
        Add-Check $checks "no_erp_secret_in_appsettings" (-not ($configText -match '"AccessToken"\s*:\s*"[^"]{8,}"')) "AccessToken nao deve ficar preenchido no appsettings."
        Add-Check $checks "erp_pdv_snapshot_configured" ($configText -match '"ErpPdvSnapshot"') "Configuracao de importacao de operadores PDV presente."
    }

    if (Test-Path -LiteralPath $pdvAppConfigPath) {
        $pdvAppConfigText = Get-Content -LiteralPath $pdvAppConfigPath -Raw
        Add-Check $checks "pdv_app_config_has_local_api" ($pdvAppConfigText -match '"LocalApiBaseUrl"\s*:\s*"http://127\.0\.0\.1:47891"') "PDV App aponta para API local do SyncAgent."
        Add-Check $checks "pdv_app_config_has_tef" ($pdvAppConfigText -match '"Tef"') "PDV App tem secao Tef no appsettings."
    }
}
catch {
    Add-Check $checks "installed_files" $false $_.Exception.Message
}

try {
    $status = Invoke-RestMethod -Uri "$baseUrl/status" -TimeoutSec 15
    $evidence.local_status = [ordered]@{
        status = $status.status
        runtime_status = $status.runtime_status
        provisioned = $status.provisioned
        provisioning_enabled = $status.provisioning_enabled
        instance_id = $status.instance_id
        erp_tenant_id = $status.erp_tenant_id
        pending_outbox_events = $status.pending_outbox_events
        dead_letter_events = $status.dead_letter_events
        last_reconciliation_status = $status.last_reconciliation_status
    }

    Add-Check $checks "local_status_ok" ($status.status -eq "ok") "GET /status retornou status=$($status.status)."
    Add-Check $checks "local_runtime_known" (-not [string]::IsNullOrWhiteSpace($status.runtime_status)) "runtime_status=$($status.runtime_status)."
    Add-Check $checks "local_pending_zero" ([int64]$status.pending_outbox_events -eq 0) "pending_outbox_events=$($status.pending_outbox_events)."
    Add-Check $checks "local_dead_letter_zero" ([int64]$status.dead_letter_events -eq 0) "dead_letter_events=$($status.dead_letter_events)."

    if ([string]::IsNullOrWhiteSpace($InstanceId)) {
        $InstanceId = $status.instance_id
    }
}
catch {
    Add-Check $checks "local_status_ok" $false $_.Exception.Message
}

try {
    $setup = Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/setup" -TimeoutSec 15
    Add-Check $checks "setup_available" ($setup.StatusCode -eq 200) "GET /setup HTTP $($setup.StatusCode)."
    Add-Check $checks "setup_no_store" ($setup.Headers["Cache-Control"] -eq "no-store") "Cache-Control=$($setup.Headers["Cache-Control"])."
}
catch {
    Add-Check $checks "setup_available" $false $_.Exception.Message
}

if ($SkipDatabase) {
    Add-Check $checks "database" $true "Validacao de banco ignorada por -SkipDatabase."
}
else {
    try {
        $timezone = Invoke-PsqlScalar -Sql "SHOW timezone;"
        $pgvector = Invoke-PsqlScalar -Sql "SELECT COALESCE((SELECT extversion FROM pg_extension WHERE extname = 'vector'), 'missing');"
        $syncAgentTableCount = Invoke-PsqlScalar -Sql "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'sync_agent';"
        $pdvTableCount = Invoke-PsqlScalar -Sql "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'pdv';"

        $evidence.database = [ordered]@{
            timezone = $timezone
            pgvector = $pgvector
            sync_agent_table_count = [int]$syncAgentTableCount
            pdv_table_count = [int]$pdvTableCount
        }

        Add-Check $checks "database_timezone" ($timezone -eq "America/Sao_Paulo") "timezone=$timezone."
        Add-Check $checks "database_pgvector" ($pgvector -eq "0.8.0") "pgvector=$pgvector."
        Add-Check $checks "database_sync_agent_schema" ([int]$syncAgentTableCount -ge 6) "sync_agent tables=$syncAgentTableCount."
        Add-Check $checks "database_pdv_schema" ([int]$pdvTableCount -ge 8) "pdv tables=$pdvTableCount."
    }
    catch {
        Add-Check $checks "database" $false $_.Exception.Message
    }
}

if ($SkipErp) {
    Add-Check $checks "erp_status" $true "Validacao ERP ignorada por -SkipErp."
}
else {
    try {
        if ([string]::IsNullOrWhiteSpace($ErpApiBaseUrl)) {
            throw "Informe -ErpApiBaseUrl ou use -SkipErp."
        }
        if ([string]::IsNullOrWhiteSpace($AccessTokenFile) -or -not (Test-Path -LiteralPath $AccessTokenFile)) {
            throw "Informe -AccessTokenFile valido ou use -SkipErp."
        }
        if ([string]::IsNullOrWhiteSpace($InstanceId)) {
            throw "InstanceId nao informado e nao retornado pelo status local."
        }

        $token = (Get-Content -LiteralPath $AccessTokenFile -Raw).Trim()
        $erpBaseUrl = $ErpApiBaseUrl.TrimEnd("/")
        $erpStatus = Invoke-RestMethod `
            -Uri "$erpBaseUrl/v1/sync/agents/$InstanceId/status" `
            -Headers @{ Authorization = "Bearer $token" } `
            -TimeoutSec 15

        Add-Check $checks "erp_connectivity" ($erpStatus.connectivity -eq "online") "ERP connectivity=$($erpStatus.connectivity)."
        Add-Check $checks "erp_queue_zero" ([int64]$erpStatus.queue_size -eq 0) "ERP queue_size=$($erpStatus.queue_size)."
        Add-Check $checks "erp_reconciliation" ([bool]$erpStatus.last_reconciliation_matched) "ERP last_reconciliation_matched=$($erpStatus.last_reconciliation_matched)."
    }
    catch {
        Add-Check $checks "erp_status" $false $_.Exception.Message
    }
}

$allPassed = -not ($checks | Where-Object { -not $_.passed })
$evidence.ready = $allPassed

$outputDirectory = Split-Path -Parent $EvidenceOutput
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

$evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $EvidenceOutput -Encoding UTF8

if ($allPassed) {
    Write-Host "Sync Agent clean install validation OK."
    Write-Host "Evidencia: $EvidenceOutput"
    exit 0
}

Write-Host "Sync Agent clean install validation BLOQUEADA."
Write-Host "Evidencia: $EvidenceOutput"
$checks | Where-Object { -not $_.passed } | ForEach-Object {
    Write-Host "FAIL: $($_.name) - $($_.message)"
}
exit 1
