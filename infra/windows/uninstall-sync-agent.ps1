param(
    [string]$InstallRoot = "C:\Program Files\PDVLocal",
    [string]$ServiceName = "PDV Local Sync Agent",
    [switch]$RemoveFiles,
    [switch]$RemoveTrayStartup
)

$ErrorActionPreference = "Stop"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Execute este desinstalador em PowerShell como Administrador."
    }
}

Assert-Administrator

function Add-SpecialFolderShortcutPath {
    param(
        [System.Collections.Generic.List[string]]$Paths,
        [string]$FolderName,
        [string]$RelativePath
    )

    $folderPath = [Environment]::GetFolderPath($FolderName)
    if (-not [string]::IsNullOrWhiteSpace($folderPath)) {
        $Paths.Add((Join-Path $folderPath $RelativePath)) | Out-Null
    }
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne "Stopped") {
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        $service.WaitForStatus("Stopped", "00:00:30")
    }

    & sc.exe delete $ServiceName | Out-Null
    Write-Host "Service removed: $ServiceName"
}

if ($RemoveTrayStartup) {
    $startupPaths = [System.Collections.Generic.List[string]]::new()
    Add-SpecialFolderShortcutPath -Paths $startupPaths -FolderName "CommonStartup" -RelativePath "PDV Local Sync Agent Tray.lnk"
    Add-SpecialFolderShortcutPath -Paths $startupPaths -FolderName "Startup" -RelativePath "PDV Local Sync Agent Tray.lnk"
    Add-SpecialFolderShortcutPath -Paths $startupPaths -FolderName "CommonDesktopDirectory" -RelativePath "PDV Local.lnk"
    Add-SpecialFolderShortcutPath -Paths $startupPaths -FolderName "Desktop" -RelativePath "PDV Local.lnk"
    Add-SpecialFolderShortcutPath -Paths $startupPaths -FolderName "CommonPrograms" -RelativePath "PDV Local\PDV Local.lnk"
    Add-SpecialFolderShortcutPath -Paths $startupPaths -FolderName "Programs" -RelativePath "PDV Local\PDV Local.lnk"

    foreach ($shortcutPath in $startupPaths) {
        if (Test-Path -LiteralPath $shortcutPath) {
            Remove-Item -LiteralPath $shortcutPath -Force
            Write-Host "Startup shortcut removed: $shortcutPath"
        }
    }
}

if ($RemoveFiles) {
    $resolvedInstallRoot = Resolve-Path -LiteralPath $InstallRoot -ErrorAction SilentlyContinue
    if ($resolvedInstallRoot -and $resolvedInstallRoot.Path -like "*\PDVLocal") {
        Remove-Item -LiteralPath $resolvedInstallRoot.Path -Recurse -Force
        Write-Host "Files removed: $($resolvedInstallRoot.Path)"
    }
    else {
        throw "Refusing to remove unexpected install root: $InstallRoot"
    }
}

Write-Host "Sync Agent uninstall completed. Database was not removed."
