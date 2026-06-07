param(
    [string]$DestinationRoot = ".\artifacts\dotnet",
    [string]$Version = "",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$releaseMetadataUrl = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json"
New-Item -ItemType Directory -Force -Path $DestinationRoot | Out-Null

$metadata = Invoke-RestMethod -Uri $releaseMetadataUrl -UseBasicParsing
$release = if ([string]::IsNullOrWhiteSpace($Version)) {
    $metadata.releases | Select-Object -First 1
}
else {
    $metadata.releases | Where-Object { $_."release-version" -eq $Version } | Select-Object -First 1
}

if (-not $release) {
    throw "Release .NET 8 nao encontrada. Version=$Version"
}

$desktopRuntime = $release."windowsdesktop"
if (-not $desktopRuntime) {
    throw "Release $($release.'release-version') nao possui Windows Desktop Runtime."
}

$file = $desktopRuntime.files |
    Where-Object { $_.rid -eq "win-x64" -and $_.name -like "windowsdesktop-runtime*win-x64.exe" } |
    Select-Object -First 1

if (-not $file) {
    throw "Instalador Windows Desktop Runtime win-x64 nao encontrado na release $($release.'release-version')."
}

$outputFile = Join-Path $DestinationRoot $file.name
if ($Force -or -not (Test-Path -LiteralPath $outputFile)) {
    Write-Host "Baixando .NET Desktop Runtime $($release.'release-version')..."
    Invoke-WebRequest -Uri $file.url -OutFile $outputFile -UseBasicParsing
}
else {
    Write-Host "Instalador ja existe: $outputFile"
}

$hash = Get-FileHash -LiteralPath $outputFile -Algorithm SHA512
if ($hash.Hash.ToLowerInvariant() -ne $file.hash.ToLowerInvariant()) {
    throw "SHA512 invalido para $outputFile."
}

[ordered]@{
    status = "ok"
    release_version = $release."release-version"
    file = (Resolve-Path -LiteralPath $outputFile).Path
    sha512 = $hash.Hash.ToLowerInvariant()
    source = $file.url
} | ConvertTo-Json -Depth 4
