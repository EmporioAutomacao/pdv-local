param(
    [string]$OutputDirectory = ".\artifacts\vc-runtime",
    [string]$Url = "https://aka.ms/vs/17/release/vc_redist.x64.exe",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$outputFile = Join-Path $OutputDirectory "vc_redist.x64.exe"

if ($Force -or -not (Test-Path -LiteralPath $outputFile)) {
    Write-Host "Baixando Microsoft Visual C++ Redistributable x64..."
    Invoke-WebRequest -Uri $Url -OutFile $outputFile
}

if (-not (Test-Path -LiteralPath $outputFile)) {
    throw "vc_redist.x64.exe nao encontrado apos download: $outputFile"
}

$hash = Get-FileHash -LiteralPath $outputFile -Algorithm SHA256
Write-Host "VC++ Redistributable disponivel em: $outputFile"
Write-Host "SHA256: $($hash.Hash.ToLowerInvariant())"
