param(
    [string]$PackageRoot = ".\artifacts\sync-agent-installer",
    [string]$LocalStatusUrl = "http://127.0.0.1:47891",
    [string]$ContractRoot = "..\sync",
    [string]$ErpApiBaseUrl = "",
    [string]$InstanceId = "",
    [string]$AccessToken = "",
    [string]$AccessTokenFile = "",
    [int]$MaxPendingEvents = 0,
    [switch]$AllowDeadLetter,
    [switch]$SkipErp,
    [string]$EvidenceOutput = ".\artifacts\sync-agent-release-readiness.json"
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

function Invoke-Json {
    param(
        [string]$Uri,
        [string]$Method = "Get",
        [hashtable]$Headers = @{}
    )

    Invoke-RestMethod -Uri $Uri -Method $Method -Headers $Headers -TimeoutSec 15
}

function Resolve-SecretValue {
    param(
        [string]$Value,
        [string]$File
    )

    if (-not [string]::IsNullOrWhiteSpace($Value)) {
        return $Value
    }

    if (-not [string]::IsNullOrWhiteSpace($File)) {
        if (-not (Test-Path -LiteralPath $File)) {
            throw "Arquivo de token nao encontrado: $File"
        }

        return (Get-Content -LiteralPath $File -Raw).Trim()
    }

    return ""
}

$checks = [System.Collections.Generic.List[object]]::new()
$baseUrl = $LocalStatusUrl.TrimEnd("/")
$evidence = [ordered]@{
    generated_at_utc = [DateTimeOffset]::UtcNow
    machine_name = $env:COMPUTERNAME
    package_root = $PackageRoot
    local_status_url = $baseUrl
    contract_root = $ContractRoot
    checks = $checks
    local_status = $null
    erp_status = $null
}

try {
    $installer = Join-Path $PackageRoot "infra\install-sync-agent.ps1"
    if (-not (Test-Path -LiteralPath $installer)) {
        Add-Check $checks "package_layout" $false "Instalador nao encontrado em $installer."
    }
    else {
        & $installer -ValidateOnly | Out-Null
        Add-Check $checks "package_layout" $true "Pacote validado com -ValidateOnly."
    }
}
catch {
    Add-Check $checks "package_layout" $false $_.Exception.Message
}

try {
    $contractReadme = Join-Path $ContractRoot "README.md"
    $openApi = Join-Path $ContractRoot "openapi\erp-api-v1.yaml"
    $contractOk = (Test-Path -LiteralPath $contractReadme) `
        -and (Test-Path -LiteralPath $openApi) `
        -and ((Select-String -LiteralPath $contractReadme -Pattern "1.4.0" -Quiet) -or (Select-String -LiteralPath $openApi -Pattern "1.4.0" -Quiet)) `
        -and (Select-String -LiteralPath $openApi -SimpleMatch "/v1/sync/activation:validate" -Quiet) `
        -and (Select-String -LiteralPath $openApi -SimpleMatch "/v1/sync/agents/{instanceId}/token:refresh" -Quiet)

    Add-Check $checks "contract_1_4_0" $contractOk "Contrato Sync 1.4.0 e endpoints de ativacao/refresh verificados."
}
catch {
    Add-Check $checks "contract_1_4_0" $false $_.Exception.Message
}

try {
    $status = Invoke-Json -Uri "$baseUrl/status"
    $evidence.local_status = [ordered]@{
        status = $status.status
        runtime_status = $status.runtime_status
        provisioning_enabled = $status.provisioning_enabled
        provisioned = $status.provisioned
        instance_id = $status.instance_id
        erp_tenant_id = $status.erp_tenant_id
        agent_version = $status.agent_version
        pending_outbox_events = $status.pending_outbox_events
        dead_letter_events = $status.dead_letter_events
        last_reconciliation_status = $status.last_reconciliation_status
        last_heartbeat_succeeded = $status.last_heartbeat_succeeded
    }

    Add-Check $checks "local_status_ok" ($status.status -eq "ok") "GET /status retornou status=$($status.status)."
    Add-Check $checks "local_runtime_not_degraded" ($status.runtime_status -ne "degraded") "runtime_status=$($status.runtime_status)."
    Add-Check $checks "local_provisioned" ([bool]$status.provisioned) "provisioned=$($status.provisioned)."
    Add-Check $checks "local_pending_within_limit" ([int64]$status.pending_outbox_events -le $MaxPendingEvents) "pending_outbox_events=$($status.pending_outbox_events), limite=$MaxPendingEvents."
    Add-Check $checks "local_dead_letter" ($AllowDeadLetter -or [int64]$status.dead_letter_events -eq 0) "dead_letter_events=$($status.dead_letter_events)."

    if ([string]::IsNullOrWhiteSpace($InstanceId)) {
        $InstanceId = $status.instance_id
    }
}
catch {
    Add-Check $checks "local_status_ok" $false $_.Exception.Message
}

try {
    $logs = Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/logs" -TimeoutSec 15
    Add-Check $checks "local_logs" ($logs.StatusCode -eq 200) "GET /logs HTTP $($logs.StatusCode)."
}
catch {
    Add-Check $checks "local_logs" $false $_.Exception.Message
}

try {
    $setup = Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/setup" -TimeoutSec 15
    Add-Check $checks "local_no_store_headers" ($setup.Headers["Cache-Control"] -eq "no-store") "Cache-Control=$($setup.Headers["Cache-Control"])."
}
catch {
    Add-Check $checks "local_no_store_headers" $false $_.Exception.Message
}

try {
    Invoke-Json -Uri "$baseUrl/sync-now" -Method "Post" | Out-Null
    Add-Check $checks "manual_sync_signal" $true "POST /sync-now aceito."
}
catch {
    $response = $_.Exception.Response
    if ($response -and [int]$response.StatusCode -eq 409) {
        Add-Check $checks "manual_sync_signal" $true "POST /sync-now retornou HTTP 409 aceito porque ja havia sinal pendente."
    }
    else {
        Add-Check $checks "manual_sync_signal" $false $_.Exception.Message
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

        if ([string]::IsNullOrWhiteSpace($InstanceId)) {
            throw "InstanceId nao informado e nao retornado pelo status local."
        }

        $token = Resolve-SecretValue -Value $AccessToken -File $AccessTokenFile
        if ([string]::IsNullOrWhiteSpace($token)) {
            throw "Informe -AccessToken ou -AccessTokenFile para validar o ERP."
        }

        $erpBaseUrl = $ErpApiBaseUrl.TrimEnd("/")
        $erpStatus = Invoke-Json `
            -Uri "$erpBaseUrl/v1/sync/agents/$InstanceId/status" `
            -Headers @{ Authorization = "Bearer $token" }

        $evidence.erp_status = [ordered]@{
            instance_id = $erpStatus.instance_id
            connectivity = $erpStatus.connectivity
            queue_size = $erpStatus.queue_size
            received_events_24h = $erpStatus.received_events_24h
            rejected_events_24h = $erpStatus.rejected_events_24h
            last_reconciliation_matched = $erpStatus.last_reconciliation_matched
        }

        Add-Check $checks "erp_connectivity_online" ($erpStatus.connectivity -eq "online") "ERP connectivity=$($erpStatus.connectivity)."
        Add-Check $checks "erp_queue_within_limit" ([int64]$erpStatus.queue_size -le $MaxPendingEvents) "ERP queue_size=$($erpStatus.queue_size), limite=$MaxPendingEvents."
        Add-Check $checks "erp_no_rejections_24h" ([int64]$erpStatus.rejected_events_24h -eq 0) "ERP rejected_events_24h=$($erpStatus.rejected_events_24h)."
        Add-Check $checks "erp_reconciliation_matched" ([bool]$erpStatus.last_reconciliation_matched) "ERP last_reconciliation_matched=$($erpStatus.last_reconciliation_matched)."
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
    Write-Host "Sync Agent release readiness OK."
    Write-Host "Evidencia: $EvidenceOutput"
    exit 0
}

Write-Host "Sync Agent release readiness BLOQUEADO."
Write-Host "Evidencia: $EvidenceOutput"
$checks | Where-Object { -not $_.passed } | ForEach-Object {
    Write-Host "FAIL: $($_.name) - $($_.message)"
}
exit 1
