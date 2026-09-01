param(
    [string]$Version = "1.0.0",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$agentArtifact = Join-Path $repoRoot "artifacts\sync-agent\win-x64"
$trayArtifact = Join-Path $repoRoot "artifacts\sync-agent-tray\win-x64"
$pdvAppArtifact = Join-Path $repoRoot "artifacts\pdv-app\win-x64"
$postgresArtifact = Join-Path $repoRoot "artifacts\postgresql-17\extracted\pgsql"
$pgVectorArtifact = Join-Path $repoRoot "artifacts\postgresql-17\pgvector"
$dotNetArtifact = Join-Path $repoRoot "artifacts\dotnet"
$vcRuntimeArtifact = Join-Path $repoRoot "artifacts\vc-runtime"

$stagingRoot = Join-Path $repoRoot "artifacts\sync-agent-single-file\staging"
$installerPublishRoot = Join-Path $repoRoot "artifacts\sync-agent-single-file\publish"
$outputDir = Join-Path $repoRoot "artifacts\sync-agent-single-file"
$embeddedPayloadDir = Join-Path $repoRoot "src\sync-agent-installer\EmbeddedPayload"
$embeddedPayloadZip = Join-Path $embeddedPayloadDir "payload.zip"

if (-not $SkipPublish) {
    & dotnet publish (Join-Path $repoRoot "src\sync-agent\SyncAgent.csproj") -c Release -r win-x64 --self-contained false -o $agentArtifact
    & dotnet publish (Join-Path $repoRoot "src\sync-agent-tray\SyncAgent.Tray.csproj") -c Release -r win-x64 --self-contained false -o $trayArtifact
    & dotnet publish (Join-Path $repoRoot "src\pdv-app\PdvLocal.App.csproj") -c Release -r win-x64 --self-contained false -o $pdvAppArtifact
}

# Monta o payload completo (SyncAgent, Tray, PDV App, PostgreSQL, pgvector, .NET runtime,
# VC++ redist, scripts) sem o proprio instalador, para em seguida embuti-lo no unico .exe.
& (Join-Path $PSScriptRoot "assemble-sync-agent-payload.ps1") `
    -OutputRoot $stagingRoot `
    -Version $Version `
    -AgentArtifact $agentArtifact `
    -TrayArtifact $trayArtifact `
    -PdvAppArtifact $pdvAppArtifact `
    -PostgresArtifact $postgresArtifact `
    -PgVectorArtifact $pgVectorArtifact `
    -DotNetArtifact $dotNetArtifact `
    -VcRuntimeArtifact $vcRuntimeArtifact

if (Test-Path -LiteralPath $embeddedPayloadDir) {
    Remove-Item -LiteralPath $embeddedPayloadDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $embeddedPayloadDir | Out-Null
Compress-Archive -Path (Join-Path $stagingRoot "*") -DestinationPath $embeddedPayloadZip

try {
    # O csproj referencia EmbeddedPayload\*.zip como EmbeddedResource: o publish abaixo
    # ja gera um unico .exe contendo o payload.
    & dotnet publish (Join-Path $repoRoot "src\sync-agent-installer\SyncAgent.Installer.csproj") `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        -o $installerPublishRoot
}
finally {
    Remove-Item -LiteralPath $embeddedPayloadDir -Recurse -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$finalName = "PdvLocalInstaller-v$Version.exe"
$finalPath = Join-Path $outputDir $finalName
Copy-Item -LiteralPath (Join-Path $installerPublishRoot "SyncAgent.Installer.exe") -Destination $finalPath -Force

$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $finalPath).Hash
"$hash  $finalName" | Set-Content "$finalPath.sha256" -Encoding UTF8 -NoNewline

Write-Host "Instalador de arquivo unico criado em: $finalPath"
Write-Host "SHA256: $hash"
