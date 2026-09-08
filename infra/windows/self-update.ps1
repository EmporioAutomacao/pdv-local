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

function Copy-TreeWithRetry {
    # A copia de PDV\ e Sync\Tray\ pode falhar por handle preso (bandeja/PDV
    # recem-encerrados, antivirus varrendo a DLL). Em vez de uma tentativa
    # unica com Start-Sleep fixo, tenta varias vezes com folga entre elas.
    param(
        [Parameter(Mandatory)] [string] $Source,
        [Parameter(Mandatory)] [string] $Destination,
        [Parameter(Mandatory)] [string] $Label,
        [int] $MaxAttempts = 8,
        [int] $DelaySeconds = 3
    )
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            Copy-Item "$Source\*" $Destination -Recurse -Force -ErrorAction Stop
            if ($attempt -gt 1) { Write-Log "Copia de '$Label' concluida na tentativa $attempt." }
            return
        } catch {
            if ($attempt -ge $MaxAttempts) { throw }
            Write-Log "Copia de '$Label' falhou (tentativa $attempt/$MaxAttempts): $($_.Exception.Message). Nova tentativa em ${DelaySeconds}s." 'Error'
            Start-Sleep -Seconds $DelaySeconds
        }
    }
}

function Stop-ClientApps {
    # Encerra PDV App e bandeja e espera os handles serem liberados, repetindo
    # ate os processos sumirem de fato (o antigo Start-Sleep 4 fixo nao dava
    # conta e a copia da Tray falhava com o arquivo em uso).
    #
    # IMPORTANTE: este script roda com $ErrorActionPreference='Stop'. Chamar
    # `taskkill.exe /IM <nome>` para um processo que NAO existe (o PDV App
    # geralmente nao esta aberto) faz o taskkill escrever no stderr, o que sob
    # 'Stop' vira erro terminante e mata o script no passo 3. Por isso:
    #   - forcamos 'Continue' localmente;
    #   - matamos por PID (sempre valido, vindo de $alive) e nao por /IM;
    #   - engolimos qualquer saida/erro do taskkill.
    $prevEA = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $procMatch = { $_.ProcessName -in @('PdvLocal.App', 'SyncAgent.Tray') -or $_.ProcessName -like 'SYNCAG~*' -or $_.ProcessName -like 'PDVLOC~*' }

        for ($round = 1; $round -le 15; $round++) {
            $alive = @(Get-Process -ErrorAction SilentlyContinue | Where-Object $procMatch)
            if ($alive.Count -eq 0) {
                if ($round -gt 1) { Write-Log "PDV App e bandeja encerrados (verificacao $round)." }
                Start-Sleep -Seconds 2   # folga final para o antivirus soltar o handle da DLL
                return
            }

            foreach ($proc in $alive) {
                try { cmd.exe /c "taskkill /F /PID $($proc.Id) >nul 2>&1" | Out-Null } catch { }
                try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
            }
            Start-Sleep -Seconds 2
        }

        Write-Log "PDV App/bandeja ainda em execucao apos ~30s - a copia sera tentada mesmo assim (com retry)." 'Error'
    }
    finally {
        $ErrorActionPreference = $prevEA
    }
}

function Restore-Backup {
    param([string]$BackupDir)
    Write-ProgressStatus -StepNumber 9 -StepName 'restoring_backup' -Message "Restaurando backup de $BackupDir..." -Status 'in_progress'
    foreach ($comp in $components) {
        $src = Join-Path $BackupDir $comp.Relative
        $dst = Join-Path $InstallRoot $comp.Relative
        if (Test-Path $src) {
            try {
                Copy-TreeWithRetry -Source $src -Destination $dst -Label "restore $($comp.Name)" -MaxAttempts 5 -DelaySeconds 3
            } catch {
                Write-Log "Falha ao restaurar '$($comp.Name)' do backup: $($_.Exception.Message)" 'Error'
            }
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

# Rede de seguranca: qualquer erro nao tratado (ex.: uma linha que escapou do
# $ErrorActionPreference='Stop') NAO pode deixar o servico parado com a
# recuperacao automatica desabilitada (passo 4) - isso e o "servico Stopped
# para sempre". O trap religa a recuperacao, tenta subir o servico e marca
# rolled_back antes de sair.
$script:backupDir = $null
trap {
    Write-Log "ERRO nao tratado no auto-update: $_" 'Error'
    try { cmd.exe /c "sc.exe failure ""$ServiceName"" reset= 86400 actions= restart/60000/restart/60000/restart/300000 >nul 2>&1" | Out-Null } catch { }
    if ($script:backupDir -and (Test-Path $script:backupDir)) {
        try { Restore-Backup $script:backupDir } catch { }
    }
    try { cmd.exe /c "sc.exe start ""$ServiceName"" >nul 2>&1" | Out-Null } catch { }
    try {
        $payload = [ordered]@{ status='failed'; step='rolled_back'; step_number=10; total_steps=$totalSteps
            message="Auto-update abortado por erro inesperado. Versao anterior mantida."; version=$Version
            timestamp_utc=(Get-Date).ToUniversalTime().ToString('o') }
        $payload | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statusFile -Encoding UTF8 -Force
    } catch { }
    try { Start-TrayIfNotRunning } catch { }
    exit 1
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

# --- 3. Encerrar PDV App e SyncAgent Tray e aguardar os handles serem liberados ---
Write-ProgressStatus -StepNumber 3 -StepName 'stopping_apps' -Message "Encerrando PDV App e bandeja..."
Stop-ClientApps

# --- 4. Desabilitar recuperacao automatica temporariamente ---
# cmd.exe /c "... >nul 2>&1" engole qualquer stderr do sc.exe: sob
# $ErrorActionPreference='Stop', um native command escrevendo no stderr vira
# erro terminante e derruba o script.
Write-ProgressStatus -StepNumber 4 -StepName 'disabling_recovery' -Message "Desabilitando recuperacao automatica do servico..."
cmd.exe /c "sc.exe failure ""$ServiceName"" reset= 0 actions= """" >nul 2>&1" | Out-Null

# --- 5. Backup dos binarios atuais ---
$oldVersion = 'unknown'
$versionFile = Join-Path $InstallRoot 'Sync\Agent\VERSION'
if (Test-Path $versionFile) { $oldVersion = (Get-Content $versionFile -Raw).Trim() }

$backupRoot = Join-Path $InstallRoot 'Backups'
$backupDir  = Join-Path $backupRoot $oldVersion
$script:backupDir = $backupDir
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

Write-ProgressStatus -StepNumber 5 -StepName 'backing_up' -Message "Fazendo backup da versao '$oldVersion' em $backupDir..."
foreach ($comp in $components) {
    $src = Join-Path $InstallRoot $comp.Relative
    $dst = Join-Path $backupDir $comp.Relative
    if (Test-Path $src) {
        New-Item -ItemType Directory -Force -Path $dst | Out-Null
        Copy-TreeWithRetry -Source $src -Destination $dst -Label "backup $($comp.Name)" -MaxAttempts 5 -DelaySeconds 3
    }
}

# --- 6. Extrair pacote novo ---
$extractDir = Join-Path $env:TEMP "pdv-extract\$Version"
if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue }
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
            Copy-TreeWithRetry -Source $src -Destination $dst -Label $comp.Name
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
cmd.exe /c "sc.exe failure ""$ServiceName"" reset= 86400 actions= restart/60000/restart/60000/restart/300000 >nul 2>&1" | Out-Null

# --- 9. Iniciar servico ---
function Start-ServiceWithRetry {
    param([string]$Name, [int]$TimeoutSeconds = 60)
    cmd.exe /c "sc.exe start ""$Name"" >nul 2>&1" | Out-Null
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
        cmd.exe /c "sc.exe stop ""$ServiceName"" >nul 2>&1" | Out-Null
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
