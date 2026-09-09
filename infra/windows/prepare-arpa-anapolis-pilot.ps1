param(
    [string]$PostgresHost = "192.168.0.4",
    [int]$PostgresPort = 5432,
    [string]$DatabaseName = "anapolis",
    [string]$ExpectedDatabaseName = "anapolis",
    [string]$DbaUser = "",
    [string]$DbaPasswordEnvironmentVariable = "ARPA_ANAPOLIS_DBA_PASSWORD",
    [string]$RuntimeUser = "sync_agent_anapolis_ro",
    [string]$RuntimePasswordEnvironmentVariable = "ARPA_SYNC_READONLY_PASSWORD",
    [string]$GeneratedRuntimePasswordFile = ".\.secrets\arpa\anapolis-runtime-password.txt",
    [string]$ViewsSqlFile = ".\infra\arpa\sync-export-views.sql",
    [string]$PsqlPath = "psql",
    [switch]$AllowEmptyDbaPassword,
    [switch]$GenerateRuntimePassword,
    [switch]$ConfirmApply
)

$ErrorActionPreference = "Stop"

if (-not $ConfirmApply) {
    throw "Preparacao bloqueada. Reexecute com -ConfirmApply apos aprovacao DBA/seguranca."
}

if ([string]::IsNullOrWhiteSpace($DbaUser)) {
    throw "Informe -DbaUser. Nao use o usuario runtime/read-only para DDL."
}

if ($DbaUser -match "(?i)sync|agent|readonly|read_only|leitura") {
    throw "DbaUser parece usuario runtime/read-only. Use usuario DBA/admin apenas para preparacao."
}

if ($DatabaseName -ne $ExpectedDatabaseName) {
    throw "DatabaseName '$DatabaseName' difere de ExpectedDatabaseName '$ExpectedDatabaseName'."
}

if (-not (Test-Path -LiteralPath $ViewsSqlFile)) {
    throw "Arquivo SQL de views nao encontrado: $ViewsSqlFile"
}

if (-not (Get-Command $PsqlPath -ErrorAction SilentlyContinue)) {
    $artifactPsql = Join-Path (Get-Location) "artifacts\postgresql-17\extracted\pgsql\bin\psql.exe"
    if (Test-Path -LiteralPath $artifactPsql) {
        $PsqlPath = $artifactPsql
    }
    else {
        throw "psql nao encontrado: $PsqlPath. Instale o PostgreSQL client, extraia os binarios oficiais em artifacts\postgresql-17\extracted ou informe -PsqlPath com o caminho completo do psql.exe."
    }
}

$dbaPassword = [Environment]::GetEnvironmentVariable($DbaPasswordEnvironmentVariable)
if ([string]::IsNullOrWhiteSpace($dbaPassword) -and -not $AllowEmptyDbaPassword) {
    throw "Variavel de ambiente $DbaPasswordEnvironmentVariable nao encontrada."
}

function New-StrongPassword {
    param([int]$Length = 32)

    $alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#%+=?"
    $bytes = [byte[]]::new($Length)
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($bytes)
    }
    finally {
        $rng.Dispose()
    }

    $chars = New-Object char[] $Length
    for ($i = 0; $i -lt $Length; $i++) {
        $chars[$i] = $alphabet[$bytes[$i] % $alphabet.Length]
    }

    return -join $chars
}

$runtimePassword = ""
if ($GenerateRuntimePassword) {
    $runtimePassword = New-StrongPassword
}
else {
    $runtimePassword = [Environment]::GetEnvironmentVariable($RuntimePasswordEnvironmentVariable)
}

if ([string]::IsNullOrWhiteSpace($runtimePassword)) {
    throw "Variavel de ambiente $RuntimePasswordEnvironmentVariable nao encontrada."
}

function Invoke-PsqlDba {
    param(
        [string[]]$Arguments,
        [string]$Password
    )

    $previousPassword = $env:PGPASSWORD
    try {
        if ([string]::IsNullOrWhiteSpace($Password)) {
            Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
        }
        else {
            $env:PGPASSWORD = $Password
        }
        & $PsqlPath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "psql retornou codigo $LASTEXITCODE."
        }
    }
    finally {
        $env:PGPASSWORD = $previousPassword
    }
}

function Quote-SqlLiteral {
    param([string]$Value)
    return "'" + $Value.Replace("'", "''") + "'"
}

$baseArgs = @(
    "-h", $PostgresHost,
    "-p", $PostgresPort,
    "-U", $DbaUser,
    "-d", $DatabaseName,
    "-X",
    "-v", "ON_ERROR_STOP=1"
)

Write-Host "Preparando piloto Arpa Anapolis."
Write-Host "Host: $PostgresHost"
Write-Host "Database: $DatabaseName"
Write-Host "DBA user: $DbaUser"
Write-Host "Runtime user: $RuntimeUser"
if ($GenerateRuntimePassword) {
    Write-Host "Senha runtime sera gerada e gravada apos preparacao bem-sucedida em: $GeneratedRuntimePasswordFile"
}
if ([string]::IsNullOrWhiteSpace($dbaPassword)) {
    Write-Warning "DBA sem senha habilitado por -AllowEmptyDbaPassword. Use apenas para preparacao controlada; nunca use postgres como runtime do SyncAgent."
}

Invoke-PsqlDba -Password $dbaPassword -Arguments ($baseArgs + @("-f", (Resolve-Path -LiteralPath $ViewsSqlFile)))

$runtimeUserLiteral = Quote-SqlLiteral -Value $RuntimeUser
$runtimeUserIdentifier = '"' + $RuntimeUser.Replace('"', '""') + '"'
$databaseIdentifier = '"' + $DatabaseName.Replace('"', '""') + '"'
$runtimePasswordLiteral = Quote-SqlLiteral -Value $runtimePassword

$roleSql = @"
DO `$`$
DECLARE
    runtime_user text := $runtimeUserLiteral;
    runtime_password text := $runtimePasswordLiteral;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = runtime_user) THEN
        EXECUTE format('CREATE ROLE %I LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION', runtime_user, runtime_password);
    ELSE
        EXECUTE format('ALTER ROLE %I WITH LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION', runtime_user, runtime_password);
    END IF;
END
`$`$;

GRANT CONNECT ON DATABASE $databaseIdentifier TO $runtimeUserIdentifier;
GRANT USAGE ON SCHEMA sync_export TO $runtimeUserIdentifier;
GRANT SELECT ON sync_export.produtos TO $runtimeUserIdentifier;
GRANT SELECT ON sync_export.clientes TO $runtimeUserIdentifier;

REVOKE CREATE ON SCHEMA sync_export FROM $runtimeUserIdentifier;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON sync_export.produtos FROM $runtimeUserIdentifier;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON sync_export.clientes FROM $runtimeUserIdentifier;
REVOKE CREATE ON SCHEMA public FROM $runtimeUserIdentifier;
REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA public FROM $runtimeUserIdentifier;
"@

Invoke-PsqlDba -Password $dbaPassword -Arguments ($baseArgs + @("-c", $roleSql))

if ($GenerateRuntimePassword) {
    $secretDirectory = Split-Path -Parent $GeneratedRuntimePasswordFile
    if (-not [string]::IsNullOrWhiteSpace($secretDirectory)) {
        New-Item -ItemType Directory -Force -Path $secretDirectory | Out-Null
    }

    Set-Content -LiteralPath $GeneratedRuntimePasswordFile -Value $runtimePassword -NoNewline
    Write-Host "Senha runtime gerada e gravada em: $GeneratedRuntimePasswordFile"
}

Write-Host "Preparacao aplicada. Agora rode:"
Write-Host ".\infra\windows\test-arpa-readonly-permissions.ps1 -PostgresHost $PostgresHost -DatabaseName $DatabaseName -DatabaseUser $RuntimeUser"
Write-Host ".\infra\windows\test-arpa-export-preflight.ps1 -PostgresHost $PostgresHost -DatabaseName $DatabaseName -DatabaseUser $RuntimeUser"
