param(
    [string]$LocalStatusUrl = "http://127.0.0.1:47891",
    [string]$ErpApiBaseUrl = "",
    [string]$InstanceId = "",
    [string]$AccessToken = "",
    [switch]$SkipErp
)

$ErrorActionPreference = "Stop"

function Invoke-Json {
    param(
        [string]$Uri,
        [string]$Method = "Get",
        [hashtable]$Headers = @{}
    )

    Invoke-RestMethod -Uri $Uri -Method $Method -Headers $Headers -TimeoutSec 15
}

function Assert-Value {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

$baseUrl = $LocalStatusUrl.TrimEnd("/")

Write-Host "Validando SyncAgent local em $baseUrl ..."
$status = Invoke-Json -Uri "$baseUrl/status"

Assert-Value -Condition ($status.status -eq "ok") -Message "GET /status nao retornou status=ok."
Assert-Value -Condition (-not [string]::IsNullOrWhiteSpace($status.instance_id)) -Message "GET /status nao retornou instance_id."

Write-Host "Local OK: instance_id=$($status.instance_id), tenant=$($status.erp_tenant_id), pending=$($status.pending_outbox_events), dead_letter=$($status.dead_letter_events)"

try {
    $syncNow = Invoke-Json -Uri "$baseUrl/sync-now" -Method "Post"
    if ($syncNow.status) {
        Write-Host "Sync manual solicitada: $($syncNow.status)"
    }
    else {
        Write-Host "Sync manual solicitada com sucesso."
    }
}
catch {
    $response = $_.Exception.Response
    if ($response -and [int]$response.StatusCode -eq 409) {
        Write-Host "Sync manual ja estava em execucao ou pendente: HTTP 409 aceito."
    }
    else {
        throw
    }
}

if ($SkipErp) {
    Write-Host "Validacao ERP ignorada por -SkipErp."
    exit 0
}

if ([string]::IsNullOrWhiteSpace($ErpApiBaseUrl)) {
    throw "Informe -ErpApiBaseUrl ou use -SkipErp."
}

if ([string]::IsNullOrWhiteSpace($InstanceId)) {
    $InstanceId = $status.instance_id
}

if ([string]::IsNullOrWhiteSpace($AccessToken)) {
    throw "Informe -AccessToken para validar o status central do ERP."
}

$erpBaseUrl = $ErpApiBaseUrl.TrimEnd("/")
$headers = @{
    Authorization = "Bearer $AccessToken"
}

Write-Host "Validando status central no ERP para instance_id=$InstanceId ..."
$erpStatus = Invoke-Json -Uri "$erpBaseUrl/v1/sync/agents/$InstanceId/status" -Headers $headers

Assert-Value -Condition (-not [string]::IsNullOrWhiteSpace($erpStatus.connectivity)) -Message "ERP nao retornou connectivity."

Write-Host "ERP OK: connectivity=$($erpStatus.connectivity), queue_size=$($erpStatus.queue_size), last_reconciliation_matched=$($erpStatus.last_reconciliation_matched)"
