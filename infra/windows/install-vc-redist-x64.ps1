param(
    [string]$InstallerPath = "",
    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"

function Test-VcRuntimeInstalled {
    $systemRuntime = Join-Path ([Environment]::SystemDirectory) "vcruntime140.dll"
    $systemRuntimeV1 = Join-Path ([Environment]::SystemDirectory) "vcruntime140_1.dll"
    return (Test-Path -LiteralPath $systemRuntime) -and (Test-Path -LiteralPath $systemRuntimeV1)
}

if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $candidates = @(
        (Join-Path $PSScriptRoot "..\payload\VcRuntime\vc_redist.x64.exe"),
        (Join-Path $PSScriptRoot "..\..\payload\VcRuntime\vc_redist.x64.exe"),
        (Join-Path (Get-Location) "payload\VcRuntime\vc_redist.x64.exe"),
        (Join-Path (Get-Location) "artifacts\sync-agent-installer\payload\VcRuntime\vc_redist.x64.exe")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            $InstallerPath = (Resolve-Path -LiteralPath $candidate).Path
            break
        }
    }
}

if ([string]::IsNullOrWhiteSpace($InstallerPath) -or -not (Test-Path -LiteralPath $InstallerPath)) {
    throw "Instalador Microsoft Visual C++ Redistributable x64 nao encontrado. Esperado payload\VcRuntime\vc_redist.x64.exe."
}

if ($ValidateOnly) {
    Write-Host "VC++ Redistributable installer validation OK."
    Write-Host "Installed: $(Test-VcRuntimeInstalled)"
    Write-Host "Installer: $InstallerPath"
    return
}

if (Test-VcRuntimeInstalled) {
    Write-Host "Microsoft Visual C++ Runtime ja instalado."
    return
}

Write-Host "Instalando Microsoft Visual C++ Redistributable x64 silenciosamente..."
$process = Start-Process -FilePath $InstallerPath -ArgumentList "/install", "/quiet", "/norestart" -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0 -and $process.ExitCode -ne 3010) {
    throw "Microsoft Visual C++ Redistributable installer falhou. ExitCode=$($process.ExitCode)"
}

if (-not (Test-VcRuntimeInstalled)) {
    throw "Microsoft Visual C++ Runtime nao foi detectado apos instalacao."
}

Write-Host "Microsoft Visual C++ Runtime instalado."
