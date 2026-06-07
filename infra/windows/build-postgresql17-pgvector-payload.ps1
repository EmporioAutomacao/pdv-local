param(
    [string]$PgVectorVersion = "0.8.0",
    [string]$PgRoot = ".\artifacts\postgresql-17\extracted\pgsql",
    [string]$SourceRoot = ".\artifacts\pgvector-src\v0.8.0",
    [string]$OutputRoot = ".\artifacts\postgresql-17\pgvector",
    [string]$RepositoryUrl = "https://github.com/pgvector/pgvector.git",
    [string]$VcVars64Path = "",
    [switch]$SkipClone,
    [switch]$SkipBuild,
    [switch]$PrepareSourceOnly
)

$ErrorActionPreference = "Stop"

function Resolve-FullPath {
    param([string]$Path)
    return [IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Resolve-VcVars64 {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $full = [IO.Path]::GetFullPath($ExplicitPath)
        if (Test-Path -LiteralPath $full) {
            return $full
        }
        throw "vcvars64.bat nao encontrado: $full"
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vswhere) {
        $installPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if (-not [string]::IsNullOrWhiteSpace($installPath)) {
            $candidate = Join-Path $installPath "VC\Auxiliary\Build\vcvars64.bat"
            if (Test-Path -LiteralPath $candidate) {
                return $candidate
            }
        }
    }

    $candidates = @(
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    throw "Visual Studio C++ Build Tools nao encontrado. Instale 'Desktop development with C++' ou informe -VcVars64Path."
}

function Invoke-Cmd {
    param([string]$Command)

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = "cmd.exe"
    $startInfo.Arguments = "/d /s /c `"$Command`""
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true

    $process = [Diagnostics.Process]::Start($startInfo)
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()

    if ($stdout) { Write-Host $stdout.TrimEnd() }
    if ($stderr) { Write-Host $stderr.TrimEnd() }
    if ($process.ExitCode -ne 0) {
        throw "Comando falhou com ExitCode=$($process.ExitCode): $Command"
    }
}

$pgRootPath = Resolve-FullPath $PgRoot
$sourcePath = Resolve-FullPath $SourceRoot
$outputPath = Resolve-FullPath $OutputRoot

if (-not (Test-Path -LiteralPath (Join-Path $pgRootPath "bin\pg_config.exe"))) {
    throw "pg_config.exe nao encontrado em PGROOT: $pgRootPath"
}

$pgVersion = & (Join-Path $pgRootPath "bin\postgres.exe") --version
if ($LASTEXITCODE -ne 0 -or $pgVersion -notmatch "PostgreSQL\) 17\.|PostgreSQL 17\.") {
    throw "PGROOT deve apontar para PostgreSQL major 17. Saida: $pgVersion"
}

if (-not $SkipClone) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourcePath ".git"))) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $sourcePath) | Out-Null
        & git clone --branch "v$PgVectorVersion" --depth 1 $RepositoryUrl $sourcePath
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $sourcePath "Makefile.win"))) {
    throw "Fonte pgvector invalida. Makefile.win nao encontrado: $sourcePath"
}

if ($PrepareSourceOnly) {
    Write-Host "Fonte pgvector preparada em: $sourcePath"
    exit 0
}

if (-not $SkipBuild) {
    $vcVars64 = Resolve-VcVars64 -ExplicitPath $VcVars64Path
    $command = "call `"$vcVars64`" && set `"PGROOT=$pgRootPath`" && cd /d `"$sourcePath`" && nmake /F Makefile.win clean && nmake /F Makefile.win && nmake /F Makefile.win install"
    Invoke-Cmd -Command $command
}

$installedDll = Join-Path $pgRootPath "lib\vector.dll"
$installedControl = Join-Path $pgRootPath "share\extension\vector.control"
$installedSql = Join-Path $pgRootPath "share\extension\vector--$PgVectorVersion.sql"

if (-not (Test-Path -LiteralPath $installedDll)) {
    throw "Build nao gerou vector.dll em $installedDll"
}
if (-not (Test-Path -LiteralPath $installedControl)) {
    throw "Build nao gerou vector.control em $installedControl"
}
if (-not (Test-Path -LiteralPath $installedSql)) {
    throw "Build nao gerou vector--$PgVectorVersion.sql em $installedSql"
}

Remove-Item -LiteralPath $outputPath -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path $outputPath "lib") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $outputPath "share\extension") | Out-Null

Copy-Item -LiteralPath $installedDll -Destination (Join-Path $outputPath "lib") -Force
Copy-Item -LiteralPath $installedControl -Destination (Join-Path $outputPath "share\extension") -Force
Copy-Item -Path (Join-Path $pgRootPath "share\extension\vector*.sql") -Destination (Join-Path $outputPath "share\extension") -Force

& (Join-Path $PSScriptRoot "test-postgresql17-pgvector-payload.ps1") -PayloadRoot $outputPath -PgVectorVersion $PgVectorVersion
