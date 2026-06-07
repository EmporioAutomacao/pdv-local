param(
    [string]$InstallRoot = "C:\Program Files\PDVLocal",
    [string]$ServiceName = "PDV Local Sync Agent",
    [string]$PostgresInstallRoot = "C:\Program Files\PostgreSQL\17",
    [string]$PostgresDataDirectory = "C:\ProgramData\PDVLocal\PostgreSQL17\data",
    [string]$PostgresServiceName = "postgresql-x64-17-pdvlocal",
    [int]$PostgresPort = 5432,
    [string]$PostgresAdminUser = "postgres",
    [securestring]$PostgresAdminPassword,
    [string]$DatabaseName = "pdv_sync",
    [string]$DatabaseUser = "pdv_sync",
    [string]$DatabasePassword,
    [switch]$EnableArpaCollector,
    [string]$ArpaHost = "192.168.0.4",
    [int]$ArpaPort = 5432,
    [string]$ArpaDatabase = "anapolis",
    [string]$ArpaUsername = "sync_agent_anapolis_ro",
    [securestring]$ArpaPassword,
    [int]$ArpaBatchSize = 5000,
    [switch]$SkipErp,
    [string]$ErpApiBaseUrl = "",
    [string]$AccessTokenFile = "",
    [switch]$AllowExistingInstall,
    [string]$EvidenceOutput = ".\artifacts\sync-agent-clean-install-evidence.json"
)

$ErrorActionPreference = "Stop"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Execute em PowerShell como Administrador."
    }
}

function Assert-CleanTarget {
    if ($AllowExistingInstall) {
        return
    }

    $existingAgentService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existingAgentService) {
        throw "Servico existente encontrado: $ServiceName. Use -AllowExistingInstall somente se esta nao for uma homologacao limpa."
    }

    $existingPostgresService = Get-Service -Name $PostgresServiceName -ErrorAction SilentlyContinue
    if ($existingPostgresService) {
        throw "Servico PostgreSQL existente encontrado: $PostgresServiceName. Use -AllowExistingInstall somente se esta nao for uma homologacao limpa."
    }

    if (Test-Path -LiteralPath $InstallRoot) {
        throw "InstallRoot ja existe: $InstallRoot. Use maquina limpa ou remova artefatos antes da homologacao."
    }

    if (Test-Path -LiteralPath $PostgresDataDirectory) {
        throw "DataDirectory PostgreSQL ja existe: $PostgresDataDirectory. Use maquina limpa ou remova dados antes da homologacao."
    }
}

function Resolve-PackageRoot {
    $candidate = Resolve-Path (Join-Path $PSScriptRoot "..") -ErrorAction Stop
    if (-not (Test-Path -LiteralPath (Join-Path $candidate.Path "payload\SyncAgent\SyncAgent.exe"))) {
        throw "Execute este script a partir do pacote artifacts\sync-agent-installer ou do bundle extraido."
    }

    return $candidate.Path
}

Assert-Administrator

if (-not $PostgresAdminPassword) {
    throw "Informe -PostgresAdminPassword."
}
if ([string]::IsNullOrWhiteSpace($DatabasePassword)) {
    throw "Informe -DatabasePassword."
}
if ($EnableArpaCollector -and -not $ArpaPassword) {
    throw "Informe -ArpaPassword quando usar -EnableArpaCollector."
}

$packageRoot = Resolve-PackageRoot
Assert-CleanTarget

$vcRuntimeInstallScript = Join-Path $packageRoot "infra\install-vc-redist-x64.ps1"
$dotNetInstallScript = Join-Path $packageRoot "infra\install-dotnet8-desktop-runtime.ps1"
$postgresInstallScript = Join-Path $packageRoot "infra\install-postgresql17-local.ps1"
$syncInstallScript = Join-Path $packageRoot "infra\install-sync-agent.ps1"
$validationScript = Join-Path $packageRoot "infra\test-sync-agent-clean-install.ps1"
$psqlPath = Join-Path $PostgresInstallRoot "bin\psql.exe"

& $vcRuntimeInstallScript
& $dotNetInstallScript -MinimumMajorVersion 8

& $postgresInstallScript `
    -InstallRoot $PostgresInstallRoot `
    -DataDirectory $PostgresDataDirectory `
    -ServiceName $PostgresServiceName `
    -Port $PostgresPort `
    -Superuser $PostgresAdminUser `
    -SuperuserPassword $PostgresAdminPassword

$installArgs = @{
    InstallRoot = $InstallRoot
    ServiceName = $ServiceName
    EnablePostInstallActivation = $true
    PsqlPath = $psqlPath
    PostgresHost = "localhost"
    PostgresPort = $PostgresPort
    PostgresAdminUser = $PostgresAdminUser
    PostgresAdminPassword = $PostgresAdminPassword
    DatabaseName = $DatabaseName
    DatabaseUser = $DatabaseUser
    DatabasePassword = $DatabasePassword
}

if ($EnableArpaCollector) {
    $arpaConnectionString = "Host=$ArpaHost;Port=$ArpaPort;Database=$ArpaDatabase;Username=$ArpaUsername"
    $installArgs.EnableArpaCollector = $true
    $installArgs.ArpaConnectionString = $arpaConnectionString
    $installArgs.ArpaPassword = $ArpaPassword
    $installArgs.ArpaCollectorPreset = "AnapolisInitialLoad"
    $installArgs.ArpaBatchSize = $ArpaBatchSize
}

& $syncInstallScript @installArgs

$validationArgs = @{
    InstallRoot = $InstallRoot
    ServiceName = $ServiceName
    PsqlPath = $psqlPath
    PostgresHost = "localhost"
    PostgresPort = $PostgresPort
    DatabaseName = $DatabaseName
    DatabaseUser = $DatabaseUser
    DatabasePassword = $DatabasePassword
    EvidenceOutput = $EvidenceOutput
}

if ($SkipErp) {
    $validationArgs.SkipErp = $true
}
else {
    $validationArgs.ErpApiBaseUrl = $ErpApiBaseUrl
    $validationArgs.AccessTokenFile = $AccessTokenFile
}

& $validationScript @validationArgs

Write-Host "Homologacao de instalacao limpa concluida."
Write-Host "Evidencia: $EvidenceOutput"
