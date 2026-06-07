param(
    [string]$InputFile = "",
    [string]$OutputFile = "",
    [string]$EnvironmentVariable = "",
    [switch]$RemoveInputFile
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($OutputFile)) {
    throw "Informe -OutputFile."
}

$secret = ""
if (-not [string]::IsNullOrWhiteSpace($EnvironmentVariable)) {
    $secret = [Environment]::GetEnvironmentVariable($EnvironmentVariable)
}
elseif (-not [string]::IsNullOrWhiteSpace($InputFile)) {
    if (-not (Test-Path -LiteralPath $InputFile)) {
        throw "InputFile nao encontrado: $InputFile"
    }

    $secret = Get-Content -LiteralPath $InputFile -Raw
}
else {
    throw "Informe -InputFile ou -EnvironmentVariable."
}

if ([string]::IsNullOrWhiteSpace($secret)) {
    throw "Segredo vazio ou nao encontrado."
}

try {
    Add-Type -AssemblyName System.Security
}
catch {
    # Assembly can already be loaded depending on the host.
}

$plainBytes = [System.Text.Encoding]::UTF8.GetBytes($secret.Trim())
$protectedBytes = [System.Security.Cryptography.ProtectedData]::Protect(
    $plainBytes,
    $null,
    [System.Security.Cryptography.DataProtectionScope]::LocalMachine)

$protectedText = [Convert]::ToBase64String($protectedBytes)
$outputDirectory = Split-Path -Parent $OutputFile
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

Set-Content -LiteralPath $OutputFile -Value $protectedText -NoNewline

if ($RemoveInputFile -and -not [string]::IsNullOrWhiteSpace($InputFile)) {
    Remove-Item -LiteralPath $InputFile -Force
}

Write-Host "Segredo protegido com DPAPI LocalMachine."
Write-Host "OutputFile: $OutputFile"
