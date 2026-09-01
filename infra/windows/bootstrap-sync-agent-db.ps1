param(
    [string]$PostgresHost = "localhost",
    [int]$PostgresPort = 5432,
    [string]$AdminUser = "postgres",
    [securestring]$AdminPassword,
    [string]$DatabaseName = "pdv",
    [string]$DatabaseUser = "araras",
    [string]$DatabasePassword = "pdv_sync",
    [string]$PsqlPath = "psql",
    [string]$PgVectorVersion = "0.8.0"
)

$ErrorActionPreference = "Stop"

$packageInitDir = Resolve-Path (Join-Path $PSScriptRoot "postgres\init") -ErrorAction SilentlyContinue
if ($packageInitDir) {
    $initDir = $packageInitDir.Path
}
else {
    $repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
    $initDir = Join-Path $repoRoot "infra\postgres\init"
}

$createDatabaseSql = @"
DO `$`$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '$DatabaseUser') THEN
        CREATE ROLE $DatabaseUser LOGIN PASSWORD '$DatabasePassword';
    ELSE
        ALTER ROLE $DatabaseUser LOGIN PASSWORD '$DatabasePassword';
    END IF;
END
`$`$;

SELECT 'CREATE DATABASE $DatabaseName OWNER $DatabaseUser'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = '$DatabaseName')\gexec
"@

$databaseUserIdentifier = '"' + $DatabaseUser.Replace('"', '""') + '"'
$grantSyncAgentSql = @"
GRANT USAGE ON SCHEMA sync_agent TO $databaseUserIdentifier;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA sync_agent TO $databaseUserIdentifier;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA sync_agent TO $databaseUserIdentifier;
ALTER DEFAULT PRIVILEGES IN SCHEMA sync_agent
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO $databaseUserIdentifier;
ALTER DEFAULT PRIVILEGES IN SCHEMA sync_agent
    GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO $databaseUserIdentifier;
"@

$grantPdvSql = @"
GRANT USAGE ON SCHEMA pdv TO $databaseUserIdentifier;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA pdv TO $databaseUserIdentifier;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA pdv TO $databaseUserIdentifier;
ALTER DEFAULT PRIVILEGES IN SCHEMA pdv
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO $databaseUserIdentifier;
ALTER DEFAULT PRIVILEGES IN SCHEMA pdv
    GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO $databaseUserIdentifier;
"@

$env:PGHOST = $PostgresHost
$env:PGPORT = [string]$PostgresPort
$env:PGUSER = $AdminUser
if ($AdminPassword) {
    $adminPasswordPtr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($AdminPassword)
    try {
        $env:PGPASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($adminPasswordPtr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($adminPasswordPtr)
    }
}

function Invoke-PsqlInput {
    param(
        [string]$Step,
        [string]$Database,
        [string]$Sql
    )

    $Sql = "SET client_min_messages TO warning;`n" + $Sql
    $output = $Sql | & $PsqlPath -d $Database -v ON_ERROR_STOP=1 2>&1
    $exitCode = $LASTEXITCODE
    if ($output) {
        $output | ForEach-Object { Write-Host $_ }
    }

    if ($exitCode -ne 0) {
        throw "$Step falhou. psql retornou codigo $exitCode."
    }
}

function Invoke-PsqlScalar {
    param(
        [string]$Step,
        [string]$Database,
        [string]$Sql
    )

    $output = & $PsqlPath -d $Database -tAc $Sql 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        if ($output) {
            $output | ForEach-Object { Write-Host $_ }
        }
        throw "$Step falhou. psql retornou codigo $exitCode."
    }

    return $output | Select-Object -First 1
}

Write-Host "Creating role/database when missing..."
Invoke-PsqlInput -Step "Criacao de role/database" -Database "postgres" -Sql $createDatabaseSql

Write-Host "Applying pgvector extension and sync_agent schema..."
Invoke-PsqlInput `
    -Step "Aplicacao de timezone" `
    -Database $DatabaseName `
    -Sql (Get-Content -LiteralPath (Join-Path $initDir "00-set-timezone.sql") -Raw)

Invoke-PsqlInput `
    -Step "Criacao da extensao pgvector" `
    -Database $DatabaseName `
    -Sql (Get-Content -LiteralPath (Join-Path $initDir "01-create-vector-extension.sql") -Raw)

Invoke-PsqlInput `
    -Step "Criacao do schema sync_agent" `
    -Database $DatabaseName `
    -Sql (Get-Content -LiteralPath (Join-Path $initDir "02-create-sync-agent-schema.sql") -Raw)

Invoke-PsqlInput `
    -Step "Concessao de permissoes no schema sync_agent" `
    -Database $DatabaseName `
    -Sql $grantSyncAgentSql

Invoke-PsqlInput `
    -Step "Criacao do schema pdv" `
    -Database $DatabaseName `
    -Sql (Get-Content -LiteralPath (Join-Path $initDir "03-create-pdv-schema.sql") -Raw)

Invoke-PsqlInput `
    -Step "Concessao de permissoes no schema pdv" `
    -Database $DatabaseName `
    -Sql $grantPdvSql

$detectedPgVectorVersion = Invoke-PsqlScalar `
    -Step "Validacao da extensao pgvector" `
    -Database $DatabaseName `
    -Sql "SELECT COALESCE((SELECT extversion FROM pg_extension WHERE extname = 'vector'), 'missing');"
if ($null -eq $detectedPgVectorVersion) {
    $detectedPgVectorVersion = "missing"
}
else {
    $detectedPgVectorVersion = $detectedPgVectorVersion.Trim()
}
if ($detectedPgVectorVersion -ne $PgVectorVersion) {
    throw "pgvector invalido. Esperado $PgVectorVersion, detectado $detectedPgVectorVersion."
}

if ($AdminPassword) {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
}

Write-Host "Sync Agent database bootstrap completed."
