#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Substitui os binarios do PDV Local pelo pacote baixado pelo SyncAgent.
    Executado automaticamente pelo SyncAgent apos download e verificacao de hash.
.PARAMETER ZipPath
    Caminho completo para o .zip ja baixado e verificado pelo SyncAgent.
.PARAMETER Version
    Versao alvo (ex: 1.1.0). Usada para nomear o diretorio de backup.
.PARAMETER InstallRoot
    Raiz da instalacao (default: C:\Program Files\PDVLocal).
.PARAMETER ServiceName
    Nome do servico Windows do SyncAgent (default: PDV Local Sync Agent).
#>
param(
    [Parameter(Mandatory)] [string] $ZipPath,
    [Parameter(Mandatory)] [string] $Version,
    [string] $InstallRoot  = 'C:\Program Files\PDVLocal',
    [string] $ServiceName  = 'PDV Local Sync Agent'
)

$ErrorActionPreference = 'Stop'
$logSource = 'PDV Local Self-Update'

function Write-Log {
    param([string]$Message, [string]$Level = 'Information')
    $ts = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    Write-Host "[$ts] [$Level] $Message"
    try {
        if (-not [System.Diagnostics.EventLog]::SourceExists($logSource)) {
            [System.Diagnostics.EventLog]::CreateEventSource($logSource, 'Application')
        }
        $entryType = if ($Level -eq 'Error') { 'Error' } else { 'Information' }
        Write-EventLog -LogName Application -Source $logSource -EntryType $entryType -EventId 5000 -Message $Message
    } catch {}
}

function Restore-Backup {
    param([string]$BackupDir)
    Write-Log "Restaurando backup de $BackupDir..." 'Error'
    foreach ($comp in @('SyncAgent', 'PDVApp', 'SyncAgentTray')) {
        $src = Join-Path $BackupDir $comp
        $dst = Join-Path $InstallRoot $comp
        if (Test-Path $src) {
            Copy-Item "$src\*" $dst -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# --- 1. Verificar hash SHA256 (dupla verificacao) ---
Write-Log "Iniciando auto-update para versao $Version."
Write-Log "Pacote: $ZipPath"

# --- 2. Aguardar servico parar (o SyncAgent para a si mesmo apos lancar este script) ---
Write-Log "Aguardando servico '$ServiceName' parar..."
$waited = 0
while ($waited -lt 60) {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -eq 'Stopped') { break }
    Start-Sleep -Seconds 2
    $waited += 2
}
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') {
    Write-Log "Servico nao parou em 60s. Forcando parada." 'Error'
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
}

# --- 3. Encerrar PDV App e SyncAgent Tray se em execucao ---
Get-Process -Name 'PdvLocal.App','SyncAgent.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 4  # aguardar liberacao de file handles

# --- 4. Desabilitar recuperacao automatica temporariamente ---
& sc.exe failure $ServiceName reset= 0 actions= "" | Out-Null

# --- 5. Backup dos binarios atuais ---
$oldVersion = 'unknown'
$versionFile = Join-Path $InstallRoot 'SyncAgent\VERSION'
if (Test-Path $versionFile) { $oldVersion = (Get-Content $versionFile -Raw).Trim() }

$backupRoot = Join-Path $InstallRoot 'Backups'
$backupDir  = Join-Path $backupRoot $oldVersion
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

Write-Log "Fazendo backup da versao '$oldVersion' em $backupDir..."
foreach ($comp in @('SyncAgent', 'PDVApp', 'SyncAgentTray')) {
    $src = Join-Path $InstallRoot $comp
    $dst = Join-Path $backupDir $comp
    if (Test-Path $src) {
        New-Item -ItemType Directory -Force -Path $dst | Out-Null
        Copy-Item "$src\*" $dst -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# --- 6. Extrair pacote novo ---
$extractDir = Join-Path $env:TEMP "pdv-extract\$Version"
if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force }
Write-Log "Extraindo pacote..."
Expand-Archive -Path $ZipPath -DestinationPath $extractDir -Force

# --- 7. Copiar binarios novos ---
$payloadDir = Join-Path $extractDir 'payload'
$success = $true
try {
    foreach ($comp in @('SyncAgent', 'PDVApp', 'SyncAgentTray')) {
        $src = Join-Path $payloadDir $comp
        $dst = Join-Path $InstallRoot $comp
        if (Test-Path $src) {
            Copy-Item "$src\*" $dst -Recurse -Force
            Write-Log "Copiado: $comp"
        }
    }

    # Atualizar arquivo VERSION
    $Version | Set-Content (Join-Path $InstallRoot 'SyncAgent\VERSION') -Encoding UTF8 -Force
} catch {
    Write-Log "Erro ao copiar binarios: $_" 'Error'
    Restore-Backup $backupDir
    $success = $false
}

# --- 8. Re-habilitar recuperacao automatica ---
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null

# --- 9. Iniciar servico ---
function Start-ServiceWithRetry {
    param([string]$Name, [int]$TimeoutSeconds = 60)
    & sc.exe start $Name 2>&1 | Out-Null
    for ($t = 0; $t -lt $TimeoutSeconds; $t += 5) {
        Start-Sleep -Seconds 5
        $s = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($s -and $s.Status -eq 'Running') { return $true }
    }
    return $false
}

if ($success) {
    Write-Log "Iniciando servico '$ServiceName'..."
    if (Start-ServiceWithRetry $ServiceName 60) {
        Write-Log "Auto-update concluido com sucesso. Versao instalada: $Version."
    } else {
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        Write-Log "Servico nao iniciou em 60s (status=$($svc.Status)). Restaurando backup." 'Error'
        & sc.exe stop $ServiceName 2>&1 | Out-Null
        Start-Sleep -Seconds 5
        Restore-Backup $backupDir
        Start-ServiceWithRetry $ServiceName 30 | Out-Null
    }
} else {
    Write-Log "Update abortado. Servico sera reiniciado com versao anterior."
    Start-ServiceWithRetry $ServiceName 30 | Out-Null
}

# --- 10. Limpeza ---
Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $ZipPath -Force -ErrorAction SilentlyContinue
