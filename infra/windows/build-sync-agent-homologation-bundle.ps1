param(
    [string]$PackageRoot = ".\artifacts\sync-agent-installer",
    [string]$OutputDirectory = ".\artifacts\homologation",
    [string]$BundleName = "",
    [switch]$SkipPackageBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$packagePath = Join-Path $repoRoot $PackageRoot
$outputPath = Join-Path $repoRoot $OutputDirectory

if (-not $SkipPackageBuild) {
    & (Join-Path $PSScriptRoot "build-sync-agent-package.ps1")
}

if (-not (Test-Path -LiteralPath $packagePath)) {
    throw "Pacote do SyncAgent nao encontrado: $packagePath"
}

$installer = Join-Path $packagePath "infra\install-sync-agent.ps1"
if (-not (Test-Path -LiteralPath $installer)) {
    throw "Instalador nao encontrado no pacote: $installer"
}

& $installer -ValidateOnly | Out-Null

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

if ([string]::IsNullOrWhiteSpace($BundleName)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $BundleName = "sync-agent-homologation-$stamp"
}

$zipPath = Join-Path $outputPath "$BundleName.zip"
$manifestPath = Join-Path $outputPath "$BundleName.manifest.json"
$checksumPath = Join-Path $outputPath "$BundleName.sha256.txt"

Remove-Item -LiteralPath $zipPath, $manifestPath, $checksumPath -Force -ErrorAction SilentlyContinue

$files = Get-ChildItem -LiteralPath $packagePath -Recurse -File |
    Sort-Object FullName |
    ForEach-Object {
        $packageFullPath = (Resolve-Path -LiteralPath $packagePath).Path.TrimEnd("\")
        $relativePath = $_.FullName.Substring($packageFullPath.Length).TrimStart("\")
        $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
        [ordered]@{
            path = $relativePath.Replace("\", "/")
            size_bytes = $_.Length
            sha256 = $hash.Hash.ToLowerInvariant()
        }
    }

$manifest = [ordered]@{
    bundle_name = $BundleName
    generated_at_utc = [DateTimeOffset]::UtcNow
    package_root = (Resolve-Path -LiteralPath $packagePath).Path
    file_count = @($files).Count
    files = $files
    required_clean_install_validation = "infra/test-sync-agent-clean-install.ps1"
    required_release_readiness_validation = "infra/test-sync-agent-release-readiness.ps1"
}

$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

$packageItems = Get-ChildItem -LiteralPath $packagePath -Force
Compress-Archive -Path $packageItems.FullName -DestinationPath $zipPath -Force
$zipHash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
"$($zipHash.Hash.ToLowerInvariant())  $(Split-Path -Leaf $zipPath)" | Set-Content -LiteralPath $checksumPath -Encoding ASCII

Write-Host "Sync Agent homologation bundle created."
Write-Host "ZIP: $zipPath"
Write-Host "Manifest: $manifestPath"
Write-Host "SHA256: $checksumPath"
