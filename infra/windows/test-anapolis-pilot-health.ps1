param(
    [string]$ErpBaseUrl = "http://127.0.0.1:8000",
    [string]$LocalStatusUrl = "http://127.0.0.1:47891",
    [string]$InstanceId = "anapolis-local-test-01",
    [string]$AccessTokenFile = "..\erp\.secrets\sync-agent\anapolis-local-test-01.token"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $AccessTokenFile)) {
    throw "Token do ERP nao encontrado: $AccessTokenFile"
}

$token = (Get-Content -LiteralPath $AccessTokenFile -Raw).Trim()

$erpStatus = Invoke-RestMethod `
    -Uri "$ErpBaseUrl/v1/sync/agents/$InstanceId/status" `
    -Headers @{ Authorization = "Bearer $token" } `
    -TimeoutSec 10

$localStatus = Invoke-RestMethod `
    -Uri "$LocalStatusUrl/status" `
    -TimeoutSec 10

$logsResponse = Invoke-WebRequest `
    -Uri "$LocalStatusUrl/logs" `
    -UseBasicParsing `
    -TimeoutSec 10

[pscustomobject]@{
    erp_instance_id = $erpStatus.instance_id
    erp_connectivity = $erpStatus.connectivity
    erp_queue_size = $erpStatus.queue_size
    erp_received_events_24h = $erpStatus.received_events_24h
    erp_rejected_events_24h = $erpStatus.rejected_events_24h
    erp_last_reconciliation_matched = $erpStatus.last_reconciliation_matched
    local_instance_id = $localStatus.instance_id
    local_runtime_status = $localStatus.runtime_status
    local_pending = $localStatus.pending_outbox_events
    local_dead_letter = $localStatus.dead_letter_events
    local_reconciliation_status = $localStatus.last_reconciliation_status
    local_logs_http_status = $logsResponse.StatusCode
}
