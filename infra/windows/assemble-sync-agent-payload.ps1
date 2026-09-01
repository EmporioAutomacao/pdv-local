param(
    [Parameter(Mandatory)]
    [string]$OutputRoot,

    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$AgentArtifact,

    [Parameter(Mandatory)]
    [string]$TrayArtifact,

    [Parameter(Mandatory)]
    [string]$PdvAppArtifact,

    [string]$InstallerArtifact = "",

    [string]$PostgresArtifact = "",
    [string]$PgVectorArtifact = "",
    [string]$DotNetArtifact = "",
    [string]$VcRuntimeArtifact = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")

if (Test-Path -LiteralPath $OutputRoot) {
    Remove-Item -LiteralPath $OutputRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $OutputRoot "payload\Sync\Agent") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $OutputRoot "payload\Sync\Tray") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $OutputRoot "payload\PDV") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $OutputRoot "infra") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $OutputRoot "docs") | Out-Null

Get-ChildItem -Path $AgentArtifact | Where-Object { $_.Name -ne 'appsettings.json' } | Copy-Item -Destination (Join-Path $OutputRoot "payload\Sync\Agent") -Recurse -Force
# Incluir o script de auto-update no payload para que futuras versões o atualizem automaticamente
Copy-Item -Path (Join-Path $repoRoot "infra\windows\self-update.ps1") -Destination (Join-Path $OutputRoot "payload\Sync\Agent\self-update.ps1") -Force
Copy-Item -Path (Join-Path $TrayArtifact "*") -Destination (Join-Path $OutputRoot "payload\Sync\Tray") -Recurse -Force
Copy-Item -Path (Join-Path $PdvAppArtifact "*") -Destination (Join-Path $OutputRoot "payload\PDV") -Recurse -Force
Copy-Item -Path (Join-Path $repoRoot "infra\windows\*.ps1") -Destination (Join-Path $OutputRoot "infra") -Force
Copy-Item -Path (Join-Path $repoRoot "infra\postgres") -Destination (Join-Path $OutputRoot "infra") -Recurse -Force
Copy-Item -Path (Join-Path $repoRoot "infra\arpa") -Destination (Join-Path $OutputRoot "infra") -Recurse -Force
Copy-Item -Path (Join-Path $repoRoot "docs\*.md") -Destination (Join-Path $OutputRoot "docs") -Force

if (-not [string]::IsNullOrWhiteSpace($InstallerArtifact)) {
    New-Item -ItemType Directory -Force -Path (Join-Path $OutputRoot "payload\SyncAgentInstaller") | Out-Null
    Copy-Item -Path (Join-Path $InstallerArtifact "*") -Destination (Join-Path $OutputRoot "payload\SyncAgentInstaller") -Recurse -Force
}

if (-not [string]::IsNullOrWhiteSpace($PostgresArtifact) -and (Test-Path -LiteralPath (Join-Path $PostgresArtifact "bin\postgres.exe"))) {
    $postgresPayloadRoot = Join-Path $OutputRoot "payload\PostgreSQL17\pgsql"
    New-Item -ItemType Directory -Force -Path $postgresPayloadRoot | Out-Null
    foreach ($runtimeDirectory in @("bin", "lib", "share")) {
        Copy-Item -Path (Join-Path $PostgresArtifact $runtimeDirectory) -Destination $postgresPayloadRoot -Recurse -Force
    }
}

if (-not [string]::IsNullOrWhiteSpace($PgVectorArtifact) -and (Test-Path -LiteralPath $PgVectorArtifact)) {
    $pgVectorPreflight = Join-Path $repoRoot "infra\windows\test-postgresql17-pgvector-payload.ps1"
    & $pgVectorPreflight -PayloadRoot $PgVectorArtifact -PgVectorVersion "0.8.0" | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $OutputRoot "payload\PostgreSQL17") | Out-Null
    Copy-Item -Path $PgVectorArtifact -Destination (Join-Path $OutputRoot "payload\PostgreSQL17") -Recurse -Force
}

if (-not [string]::IsNullOrWhiteSpace($DotNetArtifact)) {
    $dotNetInstaller = Get-ChildItem -Path (Join-Path $DotNetArtifact "windowsdesktop-runtime*win-x64.exe") -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        Select-Object -First 1
    if ($dotNetInstaller) {
        New-Item -ItemType Directory -Force -Path (Join-Path $OutputRoot "payload\DotNet") | Out-Null
        Copy-Item -LiteralPath $dotNetInstaller.FullName -Destination (Join-Path $OutputRoot "payload\DotNet") -Force
    }
}

if (-not [string]::IsNullOrWhiteSpace($VcRuntimeArtifact)) {
    $vcRuntimeInstaller = Get-ChildItem -Path (Join-Path $VcRuntimeArtifact "vc_redist.x64.exe") -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($vcRuntimeInstaller) {
        New-Item -ItemType Directory -Force -Path (Join-Path $OutputRoot "payload\VcRuntime") | Out-Null
        Copy-Item -LiteralPath $vcRuntimeInstaller.FullName -Destination (Join-Path $OutputRoot "payload\VcRuntime") -Force
    }
}

# Gravar arquivo VERSION no payload (lido pelo self-update.ps1 para nomear backup)
$Version | Set-Content (Join-Path $OutputRoot "payload\Sync\Agent\VERSION") -Encoding UTF8 -NoNewline

Write-Host "Payload assembled at: $OutputRoot"
