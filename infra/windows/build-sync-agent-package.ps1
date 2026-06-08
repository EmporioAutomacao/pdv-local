param(
    [string]$PackageRoot = ".\artifacts\sync-agent-installer",
    [string]$Version = "1.0.0",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$packagePath = Join-Path $repoRoot $PackageRoot
$agentArtifact = Join-Path $repoRoot "artifacts\sync-agent\win-x64"
$trayArtifact = Join-Path $repoRoot "artifacts\sync-agent-tray\win-x64"
$pdvAppArtifact = Join-Path $repoRoot "artifacts\pdv-app\win-x64"
$installerArtifact = Join-Path $repoRoot "artifacts\sync-agent-installer-ui\win-x64"
$postgresArtifact = Join-Path $repoRoot "artifacts\postgresql-17\extracted\pgsql"
$pgVectorArtifact = Join-Path $repoRoot "artifacts\postgresql-17\pgvector"
$dotNetArtifact = Join-Path $repoRoot "artifacts\dotnet"
$vcRuntimeArtifact = Join-Path $repoRoot "artifacts\vc-runtime"

if (-not $SkipPublish) {
    & dotnet publish (Join-Path $repoRoot "src\sync-agent\SyncAgent.csproj") -c Release -r win-x64 --self-contained false -o $agentArtifact
    & dotnet publish (Join-Path $repoRoot "src\sync-agent-tray\SyncAgent.Tray.csproj") -c Release -r win-x64 --self-contained false -o $trayArtifact
    & dotnet publish (Join-Path $repoRoot "src\pdv-app\PdvLocal.App.csproj") -c Release -r win-x64 --self-contained false -o $pdvAppArtifact
    & dotnet publish (Join-Path $repoRoot "src\sync-agent-installer\SyncAgent.Installer.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $installerArtifact
}

if (Test-Path -LiteralPath $packagePath) {
    Remove-Item -LiteralPath $packagePath -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $packagePath | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $packagePath "payload\SyncAgent") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $packagePath "payload\SyncAgentTray") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $packagePath "payload\PDVApp") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $packagePath "payload\SyncAgentInstaller") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $packagePath "infra") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $packagePath "docs") | Out-Null

Copy-Item -Path (Join-Path $agentArtifact "*") -Destination (Join-Path $packagePath "payload\SyncAgent") -Recurse -Force
Copy-Item -Path (Join-Path $trayArtifact "*") -Destination (Join-Path $packagePath "payload\SyncAgentTray") -Recurse -Force
Copy-Item -Path (Join-Path $pdvAppArtifact "*") -Destination (Join-Path $packagePath "payload\PDVApp") -Recurse -Force
Copy-Item -Path (Join-Path $installerArtifact "*") -Destination (Join-Path $packagePath "payload\SyncAgentInstaller") -Recurse -Force
Copy-Item -Path (Join-Path $repoRoot "infra\windows\*.ps1") -Destination (Join-Path $packagePath "infra") -Force
Copy-Item -Path (Join-Path $repoRoot "infra\postgres") -Destination (Join-Path $packagePath "infra") -Recurse -Force
Copy-Item -Path (Join-Path $repoRoot "infra\arpa") -Destination (Join-Path $packagePath "infra") -Recurse -Force
Copy-Item -Path (Join-Path $repoRoot "docs\*.md") -Destination (Join-Path $packagePath "docs") -Force

if (Test-Path -LiteralPath (Join-Path $postgresArtifact "bin\postgres.exe")) {
    $postgresPayloadRoot = Join-Path $packagePath "payload\PostgreSQL17\pgsql"
    New-Item -ItemType Directory -Force -Path $postgresPayloadRoot | Out-Null
    foreach ($runtimeDirectory in @("bin", "lib", "share")) {
        Copy-Item -Path (Join-Path $postgresArtifact $runtimeDirectory) -Destination $postgresPayloadRoot -Recurse -Force
    }
}

$pgVectorPreflight = Join-Path $repoRoot "infra\windows\test-postgresql17-pgvector-payload.ps1"
if (Test-Path -LiteralPath $pgVectorArtifact) {
    & $pgVectorPreflight -PayloadRoot $pgVectorArtifact -PgVectorVersion "0.8.0" | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $packagePath "payload\PostgreSQL17") | Out-Null
    Copy-Item -Path $pgVectorArtifact -Destination (Join-Path $packagePath "payload\PostgreSQL17") -Recurse -Force
}

$dotNetInstaller = Get-ChildItem -Path (Join-Path $dotNetArtifact "windowsdesktop-runtime*win-x64.exe") -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending |
    Select-Object -First 1
if ($dotNetInstaller) {
    New-Item -ItemType Directory -Force -Path (Join-Path $packagePath "payload\DotNet") | Out-Null
    Copy-Item -LiteralPath $dotNetInstaller.FullName -Destination (Join-Path $packagePath "payload\DotNet") -Force
}

$vcRuntimeInstaller = Get-ChildItem -Path (Join-Path $vcRuntimeArtifact "vc_redist.x64.exe") -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($vcRuntimeInstaller) {
    New-Item -ItemType Directory -Force -Path (Join-Path $packagePath "payload\VcRuntime") | Out-Null
    Copy-Item -LiteralPath $vcRuntimeInstaller.FullName -Destination (Join-Path $packagePath "payload\VcRuntime") -Force
}

# Gravar arquivo VERSION no payload (lido pelo self-update.ps1 para nomear backup)
$Version | Set-Content (Join-Path $packagePath "payload\SyncAgent\VERSION") -Encoding UTF8 -NoNewline

# Empacotar como ZIP versionado e gerar SHA256
$zipName    = "pdv-local-v$Version.zip"
$zipPath    = Join-Path $repoRoot "artifacts\sync-agent-installer\$zipName"
$sha256Path = "$zipPath.sha256"

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $packagePath "*") -DestinationPath $zipPath

$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash
"$hash  $zipName" | Set-Content $sha256Path -Encoding UTF8 -NoNewline

Write-Host "Sync Agent installer package created at: $packagePath"
Write-Host "ZIP: $zipPath"
Write-Host "SHA256: $hash"
