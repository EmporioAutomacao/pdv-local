param(
    [string]$ErpApiBaseUrl = "http://127.0.0.1:8000",
    [string]$InstanceId = "anapolis-local-test-01",
    [string]$TenantId = "piloto-anapolis",
    [string]$AccessTokenFile = "..\erp\.secrets\sync-agent\anapolis-local-test-01.token",
    [string]$ArpaPasswordFile = ".\.secrets\arpa\anapolis-runtime-password.txt",
    [string]$ArpaPasswordProtectedFile = ".\.secrets\arpa\anapolis-runtime-password.dpapi",
    [string]$LocalStatusPort = "47891",
    [int]$PollingIntervalSeconds = 300,
    [string]$Configuration = "Release",
    [switch]$StopExisting
)

$ErrorActionPreference = "Stop"

if ($StopExisting) {
    Get-CimInstance Win32_Process -Filter "name = 'dotnet.exe'" |
        Where-Object { $_.CommandLine -like "*src\sync-agent\SyncAgent.csproj*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
}

if (-not (Test-Path -LiteralPath $AccessTokenFile)) {
    throw "Token do ERP nao encontrado: $AccessTokenFile"
}

$useProtectedArpaPassword = Test-Path -LiteralPath $ArpaPasswordProtectedFile
if (-not $useProtectedArpaPassword -and -not (Test-Path -LiteralPath $ArpaPasswordFile)) {
    throw "Senha read-only Arpa nao encontrada. Esperado DPAPI em '$ArpaPasswordProtectedFile' ou fallback texto em '$ArpaPasswordFile'."
}

$artifactDir = Join-Path (Get-Location) "artifacts"
New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null

$stdout = Join-Path $artifactDir "sync-agent-anapolis-pilot.out.log"
$stderr = Join-Path $artifactDir "sync-agent-anapolis-pilot.err.log"
Remove-Item -LiteralPath $stdout, $stderr -ErrorAction SilentlyContinue

$env:ConnectionStrings__SyncAgentDb = "Host=localhost;Port=55432;Database=pdv_sync;Username=pdv_sync;Password=pdv_sync"
$env:SyncAgent__InstanceId = $InstanceId
$env:SyncAgent__ErpTenantId = $TenantId
$env:SyncAgent__ErpApiBaseUrl = $ErpApiBaseUrl
$env:SyncAgent__PollingIntervalSeconds = [string]$PollingIntervalSeconds
$env:SyncAgent__LocalStatusPort = $LocalStatusPort

$env:ArpaCollector__Enabled = "true"
$env:ArpaCollector__ConnectionString = "Host=192.168.0.4;Port=5432;Database=anapolis;Username=sync_agent_anapolis_ro"
Remove-Item Env:\ArpaCollector__PasswordFile -ErrorAction SilentlyContinue
Remove-Item Env:\ArpaCollector__PasswordProtectedFile -ErrorAction SilentlyContinue
if ($useProtectedArpaPassword) {
    $env:ArpaCollector__PasswordProtectedFile = (Resolve-Path -LiteralPath $ArpaPasswordProtectedFile).Path
}
else {
    $env:ArpaCollector__PasswordFile = (Resolve-Path -LiteralPath $ArpaPasswordFile).Path
    Write-Warning "Usando senha Arpa em texto como fallback. Gere o arquivo DPAPI com protect-sync-agent-secret.ps1."
}
$env:ArpaCollector__BatchSize = "5000"
$env:ArpaCollector__Entities__0__Name = "produtos_anapolis_initial_full"
$env:ArpaCollector__Entities__0__EntityType = "produto"
$env:ArpaCollector__Entities__0__Query = "SELECT entity_key, occurred_at_utc, payload_json, trace_id FROM sync_export.produtos WHERE occurred_at_utc > @watermark_utc ORDER BY occurred_at_utc, entity_key LIMIT @limit"
$env:ArpaCollector__Entities__1__Name = "clientes_anapolis_initial_full"
$env:ArpaCollector__Entities__1__EntityType = "cliente"
$env:ArpaCollector__Entities__1__Query = "SELECT entity_key, occurred_at_utc, payload_json, trace_id FROM sync_export.clientes WHERE occurred_at_utc > @watermark_utc ORDER BY occurred_at_utc, entity_key LIMIT @limit"

$env:ErpDispatcher__Enabled = "true"
$env:ErpDispatcher__BatchSize = "500"
$env:ErpDispatcher__TimeoutSeconds = "120"
$env:ErpDispatcher__InFlightRecoverySeconds = "900"
$env:ErpHeartbeat__Enabled = "true"
$env:ErpReconciliation__Enabled = "true"
$env:ErpReconciliation__TimeoutSeconds = "120"
$env:ErpPdvSnapshot__Enabled = "true"
$env:ErpPdvSnapshot__TimeoutSeconds = "120"
$env:ErpPdvSnapshot__Limit = "1000"
$env:ErpSecurity__RequireBearerToken = "true"
$env:ErpSecurity__RequireMutualTls = "false"
$env:PDV_SYNC_ERP_ACCESS_TOKEN = (Get-Content -LiteralPath $AccessTokenFile -Raw).Trim()

$process = Start-Process `
    -FilePath "dotnet" `
    -ArgumentList @("run", "--project", "src\sync-agent\SyncAgent.csproj", "--configuration", $Configuration, "--no-build") `
    -WorkingDirectory (Get-Location) `
    -PassThru `
    -RedirectStandardOutput $stdout `
    -RedirectStandardError $stderr `
    -WindowStyle Hidden

Start-Sleep -Seconds 8

$statusUrl = "http://127.0.0.1:$LocalStatusPort/status"
$logsUrl = "http://127.0.0.1:$LocalStatusPort/logs"

Invoke-WebRequest -Uri $statusUrl -UseBasicParsing -TimeoutSec 10 | Select-Object StatusCode, Content
Invoke-WebRequest -Uri $logsUrl -UseBasicParsing -TimeoutSec 10 | Select-Object StatusCode

Write-Host "SyncAgent Anapolis iniciado."
Write-Host "PID: $($process.Id)"
Write-Host "Dashboard: http://127.0.0.1:$LocalStatusPort/"
Write-Host "Logs: $logsUrl"
Write-Host "stdout: $stdout"
Write-Host "stderr: $stderr"
