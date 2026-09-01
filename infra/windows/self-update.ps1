#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Substitui os binarios da AraraSuite (PDV e Sync) pelo pacote baixado pelo
    Sync Agent. Executado automaticamente pelo Sync Agent apos download e
    verificacao de hash.
.PARAMETER ZipPath
    Caminho completo para o .zip ja baixado e verificado pelo SyncAgent.
.PARAMETER Version
    Versao alvo (ex: 1.1.0). Usada para nomear o diretorio de backup.
.PARAMETER InstallRoot
    Raiz da instalacao (default: C:\Program Files\AraraSuite.com.br).
.PARAMETER ServiceName
    Nome do servico Windows do SyncAgent (default: AraraSuiteSync).
#>
param(
    [Parameter(Mandatory)] [string] $ZipPath,
    [Parameter(Mandatory)] [string] $Version,
    [string] $InstallRoot  = 'C:\Program Files\AraraSuite.com.br',
    [string] $ServiceName  = 'AraraSuiteSync'
)

$ErrorActionPreference = 'Stop'
$logSource = 'AraraSuite Sync Update'
$totalSteps = 10
$statusFile = Join-Path $InstallRoot 'update-status.json'

# Cada componente e identificado pelo caminho relativo a InstallRoot (e,
# dentro do pacote baixado, ao mesmo caminho relativo dentro de "payload").
$components = @(
    @{ Name = 'PDV';        Relative = 'PDV' },
    @{ Name = 'Sync.Agent'; Relative = 'Sync\Agent' },
    @{ Name = 'Sync.Tray';  Relative = 'Sync\Tray' }
)

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

# Grava o progresso em InstallRoot\update-status.json (fora de PDV\/Sync\, so
# sobrevive a troca de binarios). E o arquivo que a bandeja/instalador leem
# direto do disco enquanto o servico (e a API local) ficam fora do ar.
function Write-ProgressStatus {
    param(
        [int]$StepNumber,
        [string]$StepName,
        [string]$Message,
        [string]$Status = 'in_progress'
    )
    $payload = [ordered]@{
        status       = $Status
        step         = $StepName
        step_number  = $StepNumber
        total_steps  = $totalSteps
        message      = $Message
        version      = $Version
        timestamp_utc = (Get-Date).ToUniversalTime().ToString('o')
    }
    try {
        $payload | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statusFile -Encoding UTF8 -Force
    } catch {}
    Write-Log $Message
}

function Restore-Backup {
    param([string]$BackupDir)
    Write-ProgressStatus -StepNumber 9 -StepName 'restoring_backup' -Message "Restaurando backup de $BackupDir..." -Status 'in_progress'
    foreach ($comp in $components) {
        $src = Join-Path $BackupDir $comp.Relative
        $dst = Join-Path $InstallRoot $comp.Relative
        if (Test-Path $src) {
            Copy-Item "$src\*" $dst -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-InteractiveLogonUser {
    # self-update.ps1 roda como LocalSystem (Sessao 0), que nao consegue
    # lancar um app grafico visivel na sessao de um usuario logado
    # (isolamento de sessao do Windows). Usa o processo explorer.exe do
    # usuario logado para descobrir "DOMINIO\usuario" e delegar o
    # lancamento via schtasks /IT, que usa o token interativo ja existente
    # dessa sessao - sem precisar de senha armazenada.
    try {
        $proc = Get-Process -Name 'explorer' -IncludeUserName -ErrorAction Stop | Select-Object -First 1
        if ($proc -and $proc.UserName) {
            return $proc.UserName
        }
    } catch {}
    return $null
}

function Get-ShortPathSafe {
    # schtasks /TR nao lida de forma confiavel com caminhos com espaco
    # (ex.: "C:\Program Files\..."), mesmo entre aspas - falha com
    # ERROR_FILE_NOT_FOUND ao executar a tarefa, apesar do arquivo existir e
    # o caminho aparecer correto em "schtasks /Query". O caminho curto 8.3
    # (ex.: C:\PROGRA~1\...) nao tem espacos e nao precisa de aspas,
    # contornando o problema. Se o volume nao gerar nomes 8.3, retorna o
    # caminho original sem modificar.
    param([string]$Path)
    try {
        $fso = New-Object -ComObject Scripting.FileSystemObject
        return $fso.GetFile($Path).ShortPath
    } catch {
        return $Path
    }
}

function Start-TrayIfNotRunning {
    $trayExe = Join-Path $InstallRoot 'Sync\Tray\SyncAgent.Tray.exe'
    if (-not (Test-Path -LiteralPath $trayExe)) { return }

    # Quando relancada via schtasks com o caminho curto 8.3, a bandeja pode
    # aparecer no Get-Process com o nome truncado (ex.: "SYNCAG~1") em vez
    # de "SyncAgent.Tray" - por isso o filtro abaixo cobre as duas formas.
    if (Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'SyncAgent.Tray*' -or $_.ProcessName -like 'SYNCAG~*' }) {
        return
    }

    $logonUser = Get-InteractiveLogonUser
    if (-not $logonUser) {
        Write-Log "Nenhuma sessao interativa com usuario logado encontrada - a bandeja sera aberta no proximo login."
        return
    }

    $trayExeForTask = Get-ShortPathSafe -Path $trayExe
    if ($trayExeForTask -match '\s') {
        Write-Log "Nao foi possivel obter caminho curto (8.3) sem espacos para '$trayExe' - a bandeja sera aberta no proximo login." 'Error'
        return
    }

    $taskName = "AraraSuiteSyncTrayRelaunch-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    try {
        & schtasks.exe /Create /TN $taskName /TR $trayExeForTask /SC ONCE /ST 23:59 /RU $logonUser /IT /F 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "schtasks /Create retornou codigo $LASTEXITCODE"
        }

        & schtasks.exe /Run /TN $taskName 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "schtasks /Run retornou codigo $LASTEXITCODE"
        }

        Start-Sleep -Seconds 2
        Write-Log "Bandeja relancada na sessao de '$logonUser' apos atualizacao."
    } catch {
        Write-Log "Falha ao relancar a bandeja via schtasks: $_" 'Error'
    } finally {
        & schtasks.exe /Delete /TN $taskName /F 2>&1 | Out-Null
    }
}

# --- 1. Verificar hash SHA256 (dupla verificacao) ---
Write-ProgressStatus -StepNumber 1 -StepName 'starting' -Message "Iniciando auto-update para versao $Version."
Write-Log "Pacote: $ZipPath"

# --- 2. Aguardar servico parar (o SyncAgent para a si mesmo apos lancar este script) ---
Write-ProgressStatus -StepNumber 2 -StepName 'waiting_service_stop' -Message "Aguardando servico '$ServiceName' parar..."
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
Write-ProgressStatus -StepNumber 3 -StepName 'stopping_apps' -Message "Encerrando PDV App e bandeja..."
Get-Process -Name 'PdvLocal.App','SyncAgent.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 4  # aguardar liberacao de file handles

# --- 4. Desabilitar recuperacao automatica temporariamente ---
Write-ProgressStatus -StepNumber 4 -StepName 'disabling_recovery' -Message "Desabilitando recuperacao automatica do servico..."
& sc.exe failure $ServiceName reset= 0 actions= "" | Out-Null

# --- 5. Backup dos binarios atuais ---
$oldVersion = 'unknown'
$versionFile = Join-Path $InstallRoot 'Sync\Agent\VERSION'
if (Test-Path $versionFile) { $oldVersion = (Get-Content $versionFile -Raw).Trim() }

$backupRoot = Join-Path $InstallRoot 'Backups'
$backupDir  = Join-Path $backupRoot $oldVersion
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

Write-ProgressStatus -StepNumber 5 -StepName 'backing_up' -Message "Fazendo backup da versao '$oldVersion' em $backupDir..."
foreach ($comp in $components) {
    $src = Join-Path $InstallRoot $comp.Relative
    $dst = Join-Path $backupDir $comp.Relative
    if (Test-Path $src) {
        New-Item -ItemType Directory -Force -Path $dst | Out-Null
        Copy-Item "$src\*" $dst -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# --- 6. Extrair pacote novo ---
$extractDir = Join-Path $env:TEMP "pdv-extract\$Version"
if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force }
Write-ProgressStatus -StepNumber 6 -StepName 'extracting' -Message "Extraindo pacote..."
Expand-Archive -Path $ZipPath -DestinationPath $extractDir -Force

# --- 7. Copiar binarios novos ---
$payloadDir = Join-Path $extractDir 'payload'
$success = $true
Write-ProgressStatus -StepNumber 7 -StepName 'copying_files' -Message "Copiando binarios novos..."
try {
    foreach ($comp in $components) {
        $src = Join-Path $payloadDir $comp.Relative
        $dst = Join-Path $InstallRoot $comp.Relative
        if (Test-Path $src) {
            Copy-Item "$src\*" $dst -Recurse -Force
            Write-Log "Copiado: $($comp.Name)"
        }
    }

    # Atualizar arquivo VERSION
    $Version | Set-Content (Join-Path $InstallRoot 'Sync\Agent\VERSION') -Encoding UTF8 -Force
} catch {
    Write-Log "Erro ao copiar binarios: $_" 'Error'
    Restore-Backup $backupDir
    $success = $false
}

# --- 8. Re-habilitar recuperacao automatica ---
Write-ProgressStatus -StepNumber 8 -StepName 'enabling_recovery' -Message "Reabilitando recuperacao automatica do servico..."
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
    Write-ProgressStatus -StepNumber 9 -StepName 'starting_service' -Message "Iniciando servico '$ServiceName'..."
    if (Start-ServiceWithRetry $ServiceName 60) {
        Write-ProgressStatus -StepNumber 10 -StepName 'completed' -Message "Auto-update concluido com sucesso. Versao instalada: $Version." -Status 'completed'
        Start-TrayIfNotRunning
    } else {
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        Write-Log "Servico nao iniciou em 60s (status=$($svc.Status)). Restaurando backup." 'Error'
        & sc.exe stop $ServiceName 2>&1 | Out-Null
        Start-Sleep -Seconds 5
        Restore-Backup $backupDir
        Start-ServiceWithRetry $ServiceName 30 | Out-Null
        Write-ProgressStatus -StepNumber 10 -StepName 'rolled_back' -Message "Falha ao iniciar a versao $Version. Backup restaurado (versao $oldVersion)." -Status 'failed'
        Start-TrayIfNotRunning
    }
} else {
    Write-ProgressStatus -StepNumber 9 -StepName 'restoring_backup' -Message "Update abortado. Servico sera reiniciado com versao anterior." -Status 'in_progress'
    Start-ServiceWithRetry $ServiceName 30 | Out-Null
    Write-ProgressStatus -StepNumber 10 -StepName 'rolled_back' -Message "Update abortado durante a copia de arquivos. Versao anterior restaurada." -Status 'failed'
    Start-TrayIfNotRunning
}

# --- 10. Limpeza ---
Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $ZipPath -Force -ErrorAction SilentlyContinue
