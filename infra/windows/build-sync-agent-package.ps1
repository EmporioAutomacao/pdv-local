param(
    [string]$PackageRoot    = ".\artifacts\sync-agent-installer",
    [string]$Version        = "1.0.0",
    [string]$DownloadBaseUrl = "http://192.168.0.31:8099",
    [string]$ErpContainer   = "arara-integrated-dev-erp_cliente_web-1",
    [switch]$SkipPublish,
    [switch]$SkipErpRegister
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

& (Join-Path $PSScriptRoot "assemble-sync-agent-payload.ps1") `
    -OutputRoot $packagePath `
    -Version $Version `
    -AgentArtifact $agentArtifact `
    -TrayArtifact $trayArtifact `
    -PdvAppArtifact $pdvAppArtifact `
    -InstallerArtifact $installerArtifact `
    -PostgresArtifact $postgresArtifact `
    -PgVectorArtifact $pgVectorArtifact `
    -DotNetArtifact $dotNetArtifact `
    -VcRuntimeArtifact $vcRuntimeArtifact

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

# Registrar pacote no ERP automaticamente
if (-not $SkipErpRegister) {
    $downloadUrl = "$DownloadBaseUrl/$zipName"
    $running = docker ps --filter "name=$ErpContainer" --format "{{.Names}}" 2>$null
    if ($running -eq $ErpContainer) {
        Write-Host "`nRegistrando v$Version no ERP ($ErpContainer)..."
        docker exec $ErpContainer python manage.py register_sync_package `
            --pkg-version $Version `
            --url $downloadUrl `
            --sha256 $hash 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Pacote disponivel em: $downloadUrl"
        } else {
            Write-Warning "Falha ao registrar no ERP. Cadastre manualmente em /admin/sync_api/syncpackage/"
        }
    } else {
        Write-Warning "Container ERP '$ErpContainer' nao encontrado. Cadastre o pacote manualmente em /admin/sync_api/syncpackage/"
        Write-Host "  Versao:  $Version"
        Write-Host "  URL:     $downloadUrl"
        Write-Host "  SHA256:  $hash"
    }
}
