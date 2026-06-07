param(
    [string]$Version = "17.10-1",
    [string]$DownloadUrl = "",
    [string]$DestinationRoot = ".\artifacts\postgresql-17",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($DownloadUrl)) {
    $DownloadUrl = "https://get.enterprisedb.com/postgresql/postgresql-$Version-windows-x64-binaries.zip"
}

$zipFile = Join-Path $DestinationRoot "postgresql-$Version-windows-x64-binaries.zip"
$extractRoot = Join-Path $DestinationRoot "extracted"
$psqlPath = Join-Path $extractRoot "pgsql\bin\psql.exe"

New-Item -ItemType Directory -Force -Path $DestinationRoot | Out-Null

if ($Force -or -not (Test-Path -LiteralPath $zipFile)) {
    Write-Host "Baixando PostgreSQL binaries $Version..."
    Invoke-WebRequest -Uri $DownloadUrl -OutFile $zipFile -UseBasicParsing
}
else {
    Write-Host "ZIP ja existe: $zipFile"
}

if ($Force -or -not (Test-Path -LiteralPath $psqlPath)) {
    Write-Host "Extraindo PostgreSQL binaries..."
    New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
    Expand-Archive -LiteralPath $zipFile -DestinationPath $extractRoot -Force
}
else {
    Write-Host "Binarios ja extraidos: $extractRoot"
}

if (-not (Test-Path -LiteralPath $psqlPath)) {
    throw "psql.exe nao encontrado apos extracao: $psqlPath"
}

& $psqlPath --version
Write-Host "psql disponivel em: $psqlPath"
