param(
    [string]$InstallRoot = "C:\Program Files\PostgreSQL\17",
    [string]$DataDirectory = "C:\ProgramData\PDVLocal\PostgreSQL17\data",
    [string]$SourceRoot = "",
    [string]$PgVectorSourceRoot = "",
    [string]$ServiceName = "postgresql-x64-17-pdvlocal",
    [int]$Port = 5432,
    [string]$Superuser = "postgres",
    [securestring]$SuperuserPassword,
    [string]$PgVectorVersion = "0.8.0",
    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Execute como Administrador."
    }
}

function Convert-SecureStringToPlainText {
    param([securestring]$Value)

    if (-not $Value) {
        return ""
    }

    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
    }
}

function Resolve-PostgresSource {
    param([string]$ExplicitSource)

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($ExplicitSource)) {
        $candidates += $ExplicitSource
    }

    $candidates += @(
        (Join-Path $PSScriptRoot "..\payload\PostgreSQL17\pgsql"),
        (Join-Path $PSScriptRoot "..\..\artifacts\postgresql-17\extracted\pgsql"),
        (Join-Path $PSScriptRoot "..\..\..\artifacts\postgresql-17\extracted\pgsql")
    )

    foreach ($candidate in $candidates) {
        $full = [IO.Path]::GetFullPath($candidate)
        if (Test-Path -LiteralPath (Join-Path $full "bin\postgres.exe")) {
            return $full
        }
    }

    throw "Binarios PostgreSQL 17 nao encontrados. Informe -SourceRoot ou inclua payload\PostgreSQL17\pgsql no pacote."
}

function Invoke-PostgresTool {
    param(
        [string]$FileName,
        [string[]]$Arguments,
        [string]$WorkingDirectory = ""
    )

    $previousLocation = Get-Location
    try {
        if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
            Set-Location -LiteralPath $WorkingDirectory
        }

        $output = & $FileName @Arguments 2>&1
        $exitCode = $LASTEXITCODE
        if ($output) {
            $output | ForEach-Object { Write-Host $_ }
        }

        if ($exitCode -ne 0) {
            throw "$FileName falhou com ExitCode=$exitCode."
        }
    }
    finally {
        Set-Location -LiteralPath $previousLocation.Path
    }
}

function Test-PgVectorAvailable {
    param(
        [string]$PsqlPath,
        [string]$Password
    )

    $env:PGHOST = "localhost"
    $env:PGPORT = [string]$Port
    $env:PGUSER = $Superuser
    $env:PGPASSWORD = $Password
    try {
        $version = & $PsqlPath -d postgres -tAc "SELECT default_version FROM pg_available_extensions WHERE name = 'vector';" 2>$null
        if ($LASTEXITCODE -ne 0) {
            return $false
        }

        $version = $version | Select-Object -First 1
        if ($null -eq $version) {
            return $false
        }

        $version = $version.Trim()
        return $version -eq $PgVectorVersion
    }
    finally {
        Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    }
}

function Resolve-PgVectorSource {
    param([string]$ExplicitSource)

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($ExplicitSource)) {
        $candidates += $ExplicitSource
    }

    $candidates += @(
        (Join-Path $PSScriptRoot "..\payload\PostgreSQL17\pgvector"),
        (Join-Path $PSScriptRoot "..\..\artifacts\postgresql-17\pgvector"),
        (Join-Path $PSScriptRoot "..\..\..\artifacts\postgresql-17\pgvector")
    )

    foreach ($candidate in $candidates) {
        $full = [IO.Path]::GetFullPath($candidate)
        $controlFile = Join-Path $full "share\extension\vector.control"
        $sqlFile = Join-Path $full "share\extension\vector--$PgVectorVersion.sql"
        $dllFile = Join-Path $full "lib\vector.dll"
        if ((Test-Path -LiteralPath $controlFile) -and
            (Test-Path -LiteralPath $sqlFile) -and
            (Test-Path -LiteralPath $dllFile)) {
            $control = Get-Content -LiteralPath $controlFile -Raw
            if ($control -match "default_version\s*=\s*'$([regex]::Escape($PgVectorVersion))'") {
                return $full
            }
        }
    }

    return ""
}

function Install-PgVectorPayload {
    param(
        [string]$PayloadRoot,
        [string]$TargetRoot
    )

    if ([string]::IsNullOrWhiteSpace($PayloadRoot)) {
        return
    }

    Write-Host "Copiando payload pgvector..."
    Copy-Item -Path (Join-Path $PayloadRoot "lib\*") -Destination (Join-Path $TargetRoot "lib") -Recurse -Force
    Copy-Item -Path (Join-Path $PayloadRoot "share\extension\*") -Destination (Join-Path $TargetRoot "share\extension") -Recurse -Force
}

$sourceRoot = Resolve-PostgresSource -ExplicitSource $SourceRoot
$sourcePsql = Join-Path $sourceRoot "bin\psql.exe"
$sourcePostgres = Join-Path $sourceRoot "bin\postgres.exe"

$versionOutput = & $sourcePostgres --version
if ($LASTEXITCODE -ne 0 -or $versionOutput -notmatch "PostgreSQL\) 17\.|PostgreSQL 17\.") {
    throw "Fonte PostgreSQL invalida. Esperado PostgreSQL major 17. Saida: $versionOutput"
}

if ($ValidateOnly) {
    $pgVectorPayload = Resolve-PgVectorSource -ExplicitSource $PgVectorSourceRoot
    if ([string]::IsNullOrWhiteSpace($pgVectorPayload)) {
        throw "Payload pgvector $PgVectorVersion para PostgreSQL 17 nao encontrado ou invalido. Esperado lib\vector.dll, share\extension\vector.control com default_version '$PgVectorVersion' e share\extension\vector--$PgVectorVersion.sql."
    }

    Write-Host "PostgreSQL 17 source validation OK."
    Write-Host "Source: $sourceRoot"
    Write-Host "psql: $sourcePsql"
    Write-Host "pgvector payload: $pgVectorPayload"
    exit 0
}

Assert-Administrator

if (-not $SuperuserPassword) {
    throw "Informe -SuperuserPassword."
}

$plainPassword = Convert-SecureStringToPlainText -Value $SuperuserPassword
if ([string]::IsNullOrWhiteSpace($plainPassword)) {
    throw "Senha do superuser PostgreSQL nao pode ser vazia para instalacao automatica."
}

New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $DataDirectory) | Out-Null

Write-Host "Copiando binarios PostgreSQL 17..."
Copy-Item -Path (Join-Path $sourceRoot "*") -Destination $InstallRoot -Recurse -Force

$pgVectorPayload = Resolve-PgVectorSource -ExplicitSource $PgVectorSourceRoot
if ([string]::IsNullOrWhiteSpace($pgVectorPayload)) {
    throw "Payload pgvector $PgVectorVersion para PostgreSQL 17 nao encontrado ou invalido. Instale/empacote pgvector antes de instalar o SyncAgent automaticamente."
}
Install-PgVectorPayload -PayloadRoot $pgVectorPayload -TargetRoot $InstallRoot

$binDir = Join-Path $InstallRoot "bin"
$initDb = Join-Path $binDir "initdb.exe"
$pgCtl = Join-Path $binDir "pg_ctl.exe"
$psql = Join-Path $binDir "psql.exe"

if ((Test-Path -LiteralPath $DataDirectory) -and -not (Test-Path -LiteralPath (Join-Path $DataDirectory "PG_VERSION"))) {
    throw "DataDirectory PostgreSQL existe, mas nao parece inicializado: $DataDirectory. Remova esse diretorio parcial ou informe outro DataDirectory."
}

if (-not (Test-Path -LiteralPath $DataDirectory)) {
    $passwordFile = Join-Path ([IO.Path]::GetTempPath()) ("pdvlocal-pg-" + [Guid]::NewGuid() + ".pwd")
    try {
        Set-Content -LiteralPath $passwordFile -Value $plainPassword -Encoding ASCII -NoNewline
        Invoke-PostgresTool -FileName $initDb -Arguments @(
            "-D", $DataDirectory,
            "-U", $Superuser,
            "-A", "scram-sha-256",
            "--pwfile", $passwordFile,
            "-E", "UTF8"
        )
    }
    finally {
        Remove-Item -LiteralPath $passwordFile -Force -ErrorAction SilentlyContinue
    }
}

if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
    Write-Host "Registrando servico PostgreSQL: $ServiceName"
    Invoke-PostgresTool -FileName $pgCtl -Arguments @(
        "register",
        "-N", $ServiceName,
        "-D", $DataDirectory,
        "-S", "auto",
        "-o", "-p $Port"
    )
}

Write-Host "Iniciando servico PostgreSQL..."
Start-Service -Name $ServiceName

$deadline = [DateTimeOffset]::Now.AddSeconds(45)
do {
    Start-Sleep -Seconds 2
    $ready = & (Join-Path $binDir "pg_isready.exe") -h localhost -p $Port -U $Superuser 2>$null
    $readyExitCode = $LASTEXITCODE
    if ($readyExitCode -eq 0) {
        break
    }
} while ([DateTimeOffset]::Now -lt $deadline)

if ($readyExitCode -ne 0) {
    throw "PostgreSQL nao ficou pronto na porta $Port."
}

if (-not (Test-PgVectorAvailable -PsqlPath $psql -Password $plainPassword)) {
    throw "pgvector $PgVectorVersion nao encontrado no PostgreSQL instalado. Inclua pgvector $PgVectorVersion nos binarios antes de instalar o SyncAgent."
}

Write-Host "PostgreSQL 17 instalado e pgvector $PgVectorVersion validado."
Write-Host "PsqlPath=$psql"

