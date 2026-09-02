param(
    [string]$InstallRoot = "C:\Program Files\AraraSuite.com.br",
    [string]$ServiceName = "AraraSuiteSync",
    [string]$ServiceDisplayName = "AraraSuite Sync",

    [string]$InstanceId,
    [string]$ErpTenantId,
    [string]$ErpApiBaseUrl,
    [string]$AgentVersion = "1.0.0",

    [string]$AccessToken = "",
    [string]$TokenEnvironmentVariable = "PDV_SYNC_ERP_ACCESS_TOKEN",
    [string]$ClientCertificateThumbprint = "",
    [string]$PfxPath = "",
    [securestring]$PfxPassword,
    # mTLS so quando o gateway do ERP exigir certificado cliente. O ERP padrao
    # (Traefik atras do Cloudflare) NAO faz mTLS - deixar desligado, senao a
    # sincronizacao para com "Client certificate is required".
    [switch]$RequireMutualTls,
    [switch]$EnablePostInstallActivation,
    [string]$ProvisioningProtectedFile = "",

    [string]$PostgresHost = "localhost",
    [int]$PostgresPort = 5432,
    [string]$PostgresAdminUser = "postgres",
    [securestring]$PostgresAdminPassword,
    [string]$DatabaseName = "pdv",
    [string]$DatabaseUser = "araras",
    [string]$DatabasePassword = "pdv_sync",
    [string]$PsqlPath = "psql",

    [switch]$EnableArpaCollector,
    [string]$ArpaConnectionString = "",
    [securestring]$ArpaPassword,
    [string]$ArpaPasswordProtectedFile = "",
    [int]$ArpaBatchSize = 100,
    [ValidateSet("None", "AnapolisInitialLoad")]
    [string]$ArpaCollectorPreset = "None",

    [switch]$PublishFromSource,
    [switch]$SkipDatabaseBootstrap,
    [switch]$SkipServiceStart,
    [switch]$SkipTrayStartup,
    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"

if ($IsWindows -or $env:OS -eq "Windows_NT") {
    Add-Type -AssemblyName System.Security -ErrorAction Stop
}

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Execute este instalador em PowerShell como Administrador."
    }
}

function ConvertFrom-SecureStringPlain {
    param([securestring]$Value)
    if (-not $Value) {
        return ""
    }

    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
    }
}

function Copy-DirectoryContents {
    param(
        [string]$Source,
        [string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source)) {
        throw "Origem nao encontrada: $Source"
    }

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item -Path (Join-Path $Source "*") -Destination $Destination -Recurse -Force
}

function Assert-CommandAvailable {
    param(
        [string]$Command,
        [string]$Message
    )

    if (-not (Get-Command $Command -ErrorAction SilentlyContinue)) {
        throw $Message
    }
}

function Resolve-PackageLayout {
    $packagePayloadRoot = Resolve-Path (Join-Path $PSScriptRoot "..\payload") -ErrorAction SilentlyContinue
    if ($packagePayloadRoot) {
        return [pscustomobject]@{
            Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
            AgentArtifact = (Join-Path $packagePayloadRoot.Path "Sync\Agent")
            TrayArtifact = (Join-Path $packagePayloadRoot.Path "Sync\Tray")
            PdvAppArtifact = (Join-Path $packagePayloadRoot.Path "PDV")
            InstallerArtifact = (Join-Path $packagePayloadRoot.Path "SyncAgentInstaller")
        }
    }

    $repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
    return [pscustomobject]@{
        Root = $repoRoot.Path
        AgentArtifact = (Join-Path $repoRoot.Path "artifacts\sync-agent\win-x64")
        TrayArtifact = (Join-Path $repoRoot.Path "artifacts\sync-agent-tray\win-x64")
        PdvAppArtifact = (Join-Path $repoRoot.Path "artifacts\pdv-app\win-x64")
        InstallerArtifact = (Join-Path $repoRoot.Path "artifacts\sync-agent-installer-ui\win-x64")
    }
}

function Write-PdvAppConfig {
    param([string]$ConfigPath)

    $connectionString = "Host=$PostgresHost;Port=$PostgresPort;Database=$DatabaseName;Username=$DatabaseUser;Password=$DatabasePassword"
    $config = [ordered]@{
        ConnectionStrings = @{
            PdvLocalDb = $connectionString
        }
        SyncAgent = @{
            LocalApiBaseUrl = "http://127.0.0.1:47891"
        }
        Tef = @{
            Mode     = "Simulated"
            Provider = "simulated"
        }
    }

    $config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ConfigPath -Encoding UTF8
}

function Write-AgentConfig {
    param([string]$ConfigPath)

    $connectionString = "Host=$PostgresHost;Port=$PostgresPort;Database=$DatabaseName;Username=$DatabaseUser;Password=$DatabasePassword"
    $arpaPasswordProtectedPath = ""
    $arpaEntities = @()
    $effectiveInstanceId = $InstanceId
    $effectiveErpTenantId = $ErpTenantId
    $effectiveErpApiBaseUrl = $ErpApiBaseUrl
    $effectiveProvisioningProtectedFile = $ProvisioningProtectedFile
    $effectiveProvisioningSetupHintFile = ""

    if ($EnablePostInstallActivation) {
        if ([string]::IsNullOrWhiteSpace($effectiveInstanceId)) {
            $effectiveInstanceId = "not-provisioned"
        }

        if ([string]::IsNullOrWhiteSpace($effectiveErpTenantId)) {
            $effectiveErpTenantId = "not-provisioned"
        }

        if ([string]::IsNullOrWhiteSpace($effectiveErpApiBaseUrl)) {
            $effectiveErpApiBaseUrl = "http://127.0.0.1"
        }

        if ([string]::IsNullOrWhiteSpace($effectiveProvisioningProtectedFile)) {
            $effectiveProvisioningProtectedFile = Join-Path $secretsDir "sync-agent-provisioning.dpapi"
        }

        if ([string]::IsNullOrWhiteSpace($effectiveProvisioningSetupHintFile)) {
            $effectiveProvisioningSetupHintFile = Join-Path $secretsDir "sync-agent-setup-hint.json"
        }
    }

    if ($EnableArpaCollector) {
        if ([string]::IsNullOrWhiteSpace($ArpaConnectionString)) {
            throw "Informe -ArpaConnectionString quando usar -EnableArpaCollector."
        }

        if ($ArpaCollectorPreset -eq "None") {
            throw "Informe -ArpaCollectorPreset quando usar -EnableArpaCollector."
        }

        if ([string]::IsNullOrWhiteSpace($ArpaPasswordProtectedFile)) {
            $arpaPasswordProtectedPath = Join-Path $secretsDir "arpa-runtime-password.dpapi"
        }
        else {
            $arpaPasswordProtectedPath = $ArpaPasswordProtectedFile
        }

        if ($ArpaCollectorPreset -eq "AnapolisInitialLoad") {
            $arpaEntities = @(
                [ordered]@{
                    Name = "produtos_anapolis_initial_full"
                    EntityType = "produto"
                    Query = "SELECT entity_key, occurred_at_utc, payload_json, trace_id FROM sync_export.produtos WHERE occurred_at_utc > @watermark_utc ORDER BY occurred_at_utc, entity_key LIMIT @limit"
                },
                [ordered]@{
                    Name = "clientes_anapolis_initial_full"
                    EntityType = "cliente"
                    Query = "SELECT entity_key, occurred_at_utc, payload_json, trace_id FROM sync_export.clientes WHERE occurred_at_utc > @watermark_utc ORDER BY occurred_at_utc, entity_key LIMIT @limit"
                }
            )
        }
    }

    $config = [ordered]@{
        Logging = @{
            LogLevel = @{
                Default = "Information"
                "Microsoft.Hosting.Lifetime" = "Information"
            }
        }
        ConnectionStrings = @{
            SyncAgentDb = $connectionString
        }
        SyncAgent = @{
            InstanceId = $effectiveInstanceId
            ErpTenantId = $effectiveErpTenantId
            ErpApiBaseUrl = $effectiveErpApiBaseUrl
            AgentVersion = $AgentVersion
            PollingIntervalSeconds = 30
            LocalStatusPort = 47891
        }
        Provisioning = @{
            Enabled = [bool]$EnablePostInstallActivation
            ProtectedFile = $effectiveProvisioningProtectedFile
            SetupHintFile = $effectiveProvisioningSetupHintFile
            ActivationTimeoutSeconds = 30
        }
        ArpaCollector = @{
            Enabled = [bool]$EnableArpaCollector
            # As conexoes Arpa passam a ser geridas na aba Configuracoes > Arpa do
            # dashboard local (arquivo cifrado por DPAPI). A ConnectionString legada
            # abaixo, quando presente, e importada para o store no 1o start.
            LocalConnectionsProtectedFile = (Join-Path $secretsDir "arpa-connections.dpapi")
            ConnectionString = $ArpaConnectionString
            PasswordEnvironmentVariable = ""
            PasswordFile = ""
            PasswordProtectedFile = $arpaPasswordProtectedPath
            BatchSize = $ArpaBatchSize
            Entities = $arpaEntities
        }
        ErpDispatcher = @{
            Enabled = $true
            BatchSize = 50
            TimeoutSeconds = 30
            MaxAttempts = 8
            InitialBackoffSeconds = 60
            MaxBackoffSeconds = 3600
        }
        ErpHeartbeat = @{
            Enabled = $true
            TimeoutSeconds = 15
        }
        ErpPdvSnapshot = @{
            Enabled = $true
            TimeoutSeconds = 30
            Limit = 1000
        }
        ErpSecurity = @{
            AccessTokenEnvironmentVariable = $TokenEnvironmentVariable
            AccessToken = ""
            ClientCertificateThumbprint = $ClientCertificateThumbprint
            ClientCertificateStoreName = "My"
            ClientCertificateStoreLocation = "LocalMachine"
            ClientCertificatePath = ""
            ClientCertificatePasswordEnvironmentVariable = "PDV_SYNC_ERP_CERT_PASSWORD"
            ClientCertificatePassword = ""
            RequireBearerToken = $true
            RequireMutualTls = [bool]$RequireMutualTls -or -not [string]::IsNullOrWhiteSpace($ClientCertificateThumbprint)
        }
    }

    $config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $ConfigPath -Encoding UTF8
}

function Protect-SecretToFile {
    param(
        [securestring]$Secret,
        [string]$OutputFile
    )

    if (-not $Secret) {
        throw "Informe -ArpaPassword quando usar -EnableArpaCollector."
    }

    $plainSecret = ConvertFrom-SecureStringPlain -Value $Secret
    if ([string]::IsNullOrWhiteSpace($plainSecret)) {
        throw "-ArpaPassword nao pode ser vazio quando usar -EnableArpaCollector."
    }

    $secretDirectory = Split-Path -Parent $OutputFile
    if (-not [string]::IsNullOrWhiteSpace($secretDirectory)) {
        New-Item -ItemType Directory -Force -Path $secretDirectory | Out-Null
    }

    try {
        $plainBytes = [Text.Encoding]::UTF8.GetBytes($plainSecret)
        $protectedBytes = [System.Security.Cryptography.ProtectedData]::Protect(
            $plainBytes,
            $null,
            [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
        [Convert]::ToBase64String($protectedBytes) | Set-Content -LiteralPath $OutputFile -NoNewline -Encoding ASCII
    }
    finally {
        $plainSecret = $null
    }
}

function Install-OrUpdateService {
    param(
        [string]$ExecutablePath,
        [string]$WorkingDirectory
    )

    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service) {
        if ($service.Status -ne "Stopped") {
            Stop-Service -Name $ServiceName -Force -ErrorAction Stop
            $service.WaitForStatus("Stopped", "00:00:30")
        }

        & sc.exe config $ServiceName binPath= "`"$ExecutablePath`"" start= auto DisplayName= "`"$ServiceDisplayName`"" | Out-Null
    }
    else {
        & sc.exe create $ServiceName binPath= "`"$ExecutablePath`"" start= auto DisplayName= "`"$ServiceDisplayName`"" | Out-Null
    }

    & sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
    & sc.exe description $ServiceName "Sincronizacao offline-first da AraraSuite com o ERP em nuvem." | Out-Null
}

function Install-TrayStartupShortcut {
    param([string]$TrayExecutablePath)

    $startupDir = [Environment]::GetFolderPath("CommonStartup")
    if ([string]::IsNullOrWhiteSpace($startupDir)) {
        $startupDir = [Environment]::GetFolderPath("Startup")
    }

    New-Item -ItemType Directory -Force -Path $startupDir | Out-Null
    $shortcutPath = Join-Path $startupDir "AraraSuite Sync Tray.lnk"

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $TrayExecutablePath
    $shortcut.WorkingDirectory = Split-Path -Parent $TrayExecutablePath
    $shortcut.Description = "AraraSuite Sync Tray"
    $shortcut.Save()
}

function New-Shortcut {
    param(
        [string]$ShortcutPath,
        [string]$TargetPath,
        [string]$WorkingDirectory,
        [string]$Description
    )

    $shortcutDirectory = Split-Path -Parent $ShortcutPath
    if (-not [string]::IsNullOrWhiteSpace($shortcutDirectory)) {
        New-Item -ItemType Directory -Force -Path $shortcutDirectory | Out-Null
    }

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $TargetPath
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.Description = $Description
    $shortcut.Save()
}

function Install-PdvAppShortcuts {
    param([string]$PdvAppExecutablePath)

    $pdvAppDirectory = Split-Path -Parent $PdvAppExecutablePath
    $desktopDirectory = [Environment]::GetFolderPath("CommonDesktopDirectory")
    if ([string]::IsNullOrWhiteSpace($desktopDirectory)) {
        $desktopDirectory = [Environment]::GetFolderPath("Desktop")
    }

    $programsDirectory = [Environment]::GetFolderPath("CommonPrograms")
    if ([string]::IsNullOrWhiteSpace($programsDirectory)) {
        $programsDirectory = [Environment]::GetFolderPath("Programs")
    }

    New-Shortcut `
        -ShortcutPath (Join-Path $desktopDirectory "AraraSuite PDV.lnk") `
        -TargetPath $PdvAppExecutablePath `
        -WorkingDirectory $pdvAppDirectory `
        -Description "AraraSuite PDV"

    New-Shortcut `
        -ShortcutPath (Join-Path (Join-Path $programsDirectory "AraraSuite") "AraraSuite PDV.lnk") `
        -TargetPath $PdvAppExecutablePath `
        -WorkingDirectory $pdvAppDirectory `
        -Description "AraraSuite PDV"
}

$isWindowsPlatform = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
$layout = Resolve-PackageLayout

if ($EnableArpaCollector -and -not $isWindowsPlatform) {
    throw "-EnableArpaCollector com senha protegida requer Windows DPAPI."
}

$syncInstallDir = Join-Path $InstallRoot "Sync"
$syncAgentInstallDir = Join-Path $syncInstallDir "Agent"
$trayInstallDir = Join-Path $syncInstallDir "Tray"
$secretsDir = Join-Path $syncInstallDir "Secrets"
$pdvAppInstallDir = Join-Path $InstallRoot "PDV"
$syncAgentArtifactDir = $layout.AgentArtifact
$trayArtifactDir = $layout.TrayArtifact
$pdvAppArtifactDir = $layout.PdvAppArtifact

if ($ValidateOnly) {
    if (-not (Test-Path -LiteralPath (Join-Path $syncAgentArtifactDir "SyncAgent.exe"))) {
        throw "Payload Sync Agent invalido ou incompleto: $syncAgentArtifactDir"
    }

    if (-not (Test-Path -LiteralPath (Join-Path $trayArtifactDir "SyncAgent.Tray.exe"))) {
        throw "Payload Sync Tray invalido ou incompleto: $trayArtifactDir"
    }

    if (-not (Test-Path -LiteralPath (Join-Path $pdvAppArtifactDir "PdvLocal.App.exe"))) {
        throw "Payload PDV invalido ou incompleto: $pdvAppArtifactDir"
    }

    if (-not (Test-Path -LiteralPath (Join-Path $layout.InstallerArtifact "SyncAgent.Installer.exe"))) {
        throw "Payload SyncAgentInstaller invalido ou incompleto: $($layout.InstallerArtifact)"
    }

    $dotNetInstaller = Get-ChildItem -Path (Join-Path $layout.Root "payload\DotNet\windowsdesktop-runtime*win-x64.exe") -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $dotNetInstaller) {
        throw "Payload .NET Desktop Runtime invalido ou incompleto: $(Join-Path $layout.Root 'payload\DotNet')"
    }

    $vcRuntimeInstaller = Get-ChildItem -Path (Join-Path $layout.Root "payload\VcRuntime\vc_redist.x64.exe") -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $vcRuntimeInstaller) {
        throw "Payload Microsoft Visual C++ Redistributable x64 invalido ou incompleto: $(Join-Path $layout.Root 'payload\VcRuntime')"
    }

    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot "bootstrap-sync-agent-db.ps1"))) {
        throw "Script de bootstrap nao encontrado no pacote."
    }

    Write-Host "AraraSuite installer validation OK."
    Write-Host "Layout root: $($layout.Root)"
    Write-Host "Sync Agent payload: $syncAgentArtifactDir"
    Write-Host "Sync Tray payload: $trayArtifactDir"
    Write-Host "PDV payload: $pdvAppArtifactDir"
    Write-Host "Installer payload: $($layout.InstallerArtifact)"
    Write-Host ".NET payload: $($dotNetInstaller.FullName)"
    Write-Host "VC++ payload: $($vcRuntimeInstaller.FullName)"
    exit 0
}

Assert-Administrator

if (-not $EnablePostInstallActivation) {
    if ([string]::IsNullOrWhiteSpace($InstanceId)) { throw "Informe -InstanceId ou use -EnablePostInstallActivation." }
    if ([string]::IsNullOrWhiteSpace($ErpTenantId)) { throw "Informe -ErpTenantId ou use -EnablePostInstallActivation." }
    if ([string]::IsNullOrWhiteSpace($ErpApiBaseUrl)) { throw "Informe -ErpApiBaseUrl ou use -EnablePostInstallActivation." }
}

if ($PublishFromSource) {
    Assert-CommandAvailable -Command "dotnet" -Message "dotnet SDK/runtime nao encontrado no PATH. Instale .NET 8 ou execute sem -PublishFromSource usando artefatos ja publicados."
    $repoAgentProject = Join-Path $layout.Root "src\sync-agent\SyncAgent.csproj"
    $repoTrayProject = Join-Path $layout.Root "src\sync-agent-tray\SyncAgent.Tray.csproj"
    $repoPdvAppProject = Join-Path $layout.Root "src\pdv-app\PdvLocal.App.csproj"
    if (-not (Test-Path -LiteralPath $repoAgentProject) -or -not (Test-Path -LiteralPath $repoTrayProject) -or -not (Test-Path -LiteralPath $repoPdvAppProject)) {
        throw "-PublishFromSource requer execucao a partir do repositorio, nao do pacote de instalacao."
    }

    & dotnet publish $repoAgentProject -c Release -r win-x64 --self-contained false -o $syncAgentArtifactDir
    & dotnet publish $repoTrayProject -c Release -r win-x64 --self-contained false -o $trayArtifactDir
    & dotnet publish $repoPdvAppProject -c Release -r win-x64 --self-contained false -o $pdvAppArtifactDir
}

if (-not $SkipDatabaseBootstrap) {
    $vcRuntimeInstallScript = Join-Path $PSScriptRoot "install-vc-redist-x64.ps1"
    if (Test-Path -LiteralPath $vcRuntimeInstallScript) {
        & $vcRuntimeInstallScript
    }

    Assert-CommandAvailable -Command $PsqlPath -Message "psql nao encontrado. Instale PostgreSQL 17 com pgvector e informe -PsqlPath, ou execute com -SkipDatabaseBootstrap apos preparar o banco."

    & (Join-Path $PSScriptRoot "bootstrap-sync-agent-db.ps1") `
        -PostgresHost $PostgresHost `
        -PostgresPort $PostgresPort `
        -AdminUser $PostgresAdminUser `
        -AdminPassword $PostgresAdminPassword `
        -DatabaseName $DatabaseName `
        -DatabaseUser $DatabaseUser `
        -DatabasePassword $DatabasePassword `
        -PsqlPath $PsqlPath
}

if (-not [string]::IsNullOrWhiteSpace($AccessToken) -or -not [string]::IsNullOrWhiteSpace($PfxPath)) {
    $securityArgs = @{
        AccessToken = $AccessToken
        TokenEnvironmentVariable = $TokenEnvironmentVariable
        PfxPath = $PfxPath
    }
    if ($PfxPassword) {
        $securityArgs.PfxPassword = $PfxPassword
    }

    & (Join-Path $PSScriptRoot "provision-sync-agent-security.ps1") @securityArgs
}

Copy-DirectoryContents -Source $syncAgentArtifactDir -Destination $syncAgentInstallDir
Copy-DirectoryContents -Source $trayArtifactDir -Destination $trayInstallDir
Copy-DirectoryContents -Source $pdvAppArtifactDir -Destination $pdvAppInstallDir

if ($EnableArpaCollector) {
    $resolvedArpaPasswordProtectedFile = $ArpaPasswordProtectedFile
    if ([string]::IsNullOrWhiteSpace($resolvedArpaPasswordProtectedFile)) {
        $resolvedArpaPasswordProtectedFile = Join-Path $secretsDir "arpa-runtime-password.dpapi"
    }

    Protect-SecretToFile -Secret $ArpaPassword -OutputFile $resolvedArpaPasswordProtectedFile
    $ArpaPasswordProtectedFile = $resolvedArpaPasswordProtectedFile
}

Write-AgentConfig -ConfigPath (Join-Path $syncAgentInstallDir "appsettings.json")
Write-PdvAppConfig -ConfigPath (Join-Path $pdvAppInstallDir "appsettings.json")

# Deploy self-update script e arquivo VERSION (usados pelo mecanismo de auto-update)
$selfUpdateScript = Join-Path $PSScriptRoot "self-update.ps1"
if (Test-Path $selfUpdateScript) {
    Copy-Item $selfUpdateScript -Destination (Join-Path $syncAgentInstallDir "self-update.ps1") -Force
}
$AgentVersion | Set-Content (Join-Path $syncAgentInstallDir "VERSION") -Encoding UTF8 -NoNewline

$serviceExe = Join-Path $syncAgentInstallDir "SyncAgent.exe"
$trayExe = Join-Path $trayInstallDir "SyncAgent.Tray.exe"
$pdvAppExe = Join-Path $pdvAppInstallDir "PdvLocal.App.exe"
Install-OrUpdateService -ExecutablePath $serviceExe -WorkingDirectory $syncAgentInstallDir
Install-PdvAppShortcuts -PdvAppExecutablePath $pdvAppExe

if (-not $SkipTrayStartup) {
    Install-TrayStartupShortcut -TrayExecutablePath $trayExe
}

if (-not $SkipServiceStart) {
    Start-Service -Name $ServiceName
}

Write-Host "Sync Agent installed."
Write-Host "Service: $ServiceName"
Write-Host "Status URL: http://127.0.0.1:47891/status"
