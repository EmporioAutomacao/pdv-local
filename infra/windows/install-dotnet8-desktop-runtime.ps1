param(
    [string]$InstallerPath = "",
    [string]$MinimumMajorVersion = "8",
    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"

function Test-DotNetDesktopRuntimeInstalled {
    param([string]$MajorVersion)

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnet) {
        $runtimes = & $dotnet.Source --list-runtimes 2>$null
        if ($runtimes | Where-Object { $_ -match "^Microsoft\.WindowsDesktop\.App\s+$MajorVersion\." }) {
            return $true
        }
    }

    $registryRoots = @(
        "HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App",
        "HKLM:\SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App"
    )

    foreach ($root in $registryRoots) {
        if (Test-Path -LiteralPath $root) {
            $versions = Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue
            if ($versions | Where-Object { $_.PSChildName -like "$MajorVersion.*" }) {
                return $true
            }
        }
    }

    return $false
}

function Resolve-InstallerPath {
    param([string]$ExplicitPath)

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $candidates += $ExplicitPath
    }

    $candidates += @(
        (Join-Path $PSScriptRoot "..\payload\DotNet\windowsdesktop-runtime*win-x64.exe"),
        (Join-Path $PSScriptRoot "..\..\artifacts\dotnet\windowsdesktop-runtime*win-x64.exe"),
        (Join-Path $PSScriptRoot "..\..\..\artifacts\dotnet\windowsdesktop-runtime*win-x64.exe")
    )

    foreach ($candidate in $candidates) {
        $matches = Get-ChildItem -Path $candidate -ErrorAction SilentlyContinue | Sort-Object Name -Descending
        $match = $matches | Select-Object -First 1
        if ($match) {
            return $match.FullName
        }
    }

    throw "Instalador .NET Desktop Runtime nao encontrado. Esperado payload\DotNet\windowsdesktop-runtime*win-x64.exe."
}

$isInstalled = Test-DotNetDesktopRuntimeInstalled -MajorVersion $MinimumMajorVersion
if ($ValidateOnly) {
    $installer = Resolve-InstallerPath -ExplicitPath $InstallerPath
    Write-Host ".NET Desktop Runtime installer validation OK."
    Write-Host "Installed: $isInstalled"
    Write-Host "Installer: $installer"
    exit 0
}

if ($isInstalled) {
    Write-Host ".NET Desktop Runtime $MinimumMajorVersion.x ja instalado."
    exit 0
}

$installer = Resolve-InstallerPath -ExplicitPath $InstallerPath
Write-Host "Instalando .NET Desktop Runtime silenciosamente..."
$process = Start-Process -FilePath $installer -ArgumentList "/install", "/quiet", "/norestart" -Wait -PassThru
if ($process.ExitCode -ne 0 -and $process.ExitCode -ne 3010) {
    throw ".NET Desktop Runtime installer falhou. ExitCode=$($process.ExitCode)"
}

if (-not (Test-DotNetDesktopRuntimeInstalled -MajorVersion $MinimumMajorVersion)) {
    Write-Warning ".NET Desktop Runtime $MinimumMajorVersion.x nao foi detectado imediatamente apos instalacao. O instalador retornou sucesso; continuando a instalacao."
    exit 0
}

Write-Host ".NET Desktop Runtime instalado."
