param(
    [string]$PostgresHost = "",
    [int]$PostgresPort = 5432,
    [string]$DatabaseName = "",
    [string]$DatabaseUser = "",
    [string]$PasswordEnvironmentVariable = "ARPA_SYNC_READONLY_PASSWORD",
    [string]$PsqlPath = "psql",
    [string]$DockerContainer = "",
    [string]$Schema = "sync_export"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($PostgresHost) -or
    [string]::IsNullOrWhiteSpace($DatabaseName) -or
    [string]::IsNullOrWhiteSpace($DatabaseUser)) {
    throw "Informe -PostgresHost, -DatabaseName e -DatabaseUser."
}

if ([string]::IsNullOrWhiteSpace($DockerContainer) -and -not (Get-Command $PsqlPath -ErrorAction SilentlyContinue)) {
    $artifactPsql = Join-Path (Get-Location) "artifacts\postgresql-17\extracted\pgsql\bin\psql.exe"
    if (Test-Path -LiteralPath $artifactPsql) {
        $PsqlPath = $artifactPsql
    }
    else {
        throw "psql nao encontrado: $PsqlPath. Execute infra\windows\download-postgresql17-binaries.ps1 ou informe -PsqlPath."
    }
}

$env:PGPASSWORD = [Environment]::GetEnvironmentVariable($PasswordEnvironmentVariable)
if ([string]::IsNullOrWhiteSpace($env:PGPASSWORD)) {
    throw "Variavel de ambiente $PasswordEnvironmentVariable nao encontrada."
}

function Invoke-PsqlScalar {
    param([string]$Sql)

    $args = @(
        "-h", $PostgresHost,
        "-p", $PostgresPort,
        "-U", $DatabaseUser,
        "-d", $DatabaseName,
        "-X", "-q", "-t", "-A",
        "-v", "ON_ERROR_STOP=1",
        "-c", $Sql
    )

    if ([string]::IsNullOrWhiteSpace($DockerContainer)) {
        $result = & $PsqlPath @args
    }
    else {
        $result = & docker exec -e "PGPASSWORD=$env:PGPASSWORD" $DockerContainer psql @args
    }

    if ($LASTEXITCODE -ne 0) {
        throw "psql retornou codigo $LASTEXITCODE."
    }

    return ($result | Select-Object -First 1).Trim()
}

function Assert-False {
    param(
        [string]$Sql,
        [string]$FailureMessage
    )

    $value = Invoke-PsqlScalar -Sql $Sql
    if ($value -eq "t" -or $value -eq "true" -or $value -eq "1") {
        throw $FailureMessage
    }
}

Write-Host "Validando permissoes read-only do usuario $DatabaseUser em $DatabaseName ..."

Assert-False `
    -Sql "SELECT usesuper FROM pg_catalog.pg_user WHERE usename = current_user;" `
    -FailureMessage "Usuario Arpa nao pode ser superuser."

Assert-False `
    -Sql "SELECT has_schema_privilege(current_user, '$Schema', 'CREATE');" `
    -FailureMessage "Usuario Arpa nao pode ter CREATE no schema $Schema."

foreach ($table in @("produtos", "clientes")) {
    $qualified = "$Schema.$table"
    $exists = Invoke-PsqlScalar -Sql "SELECT to_regclass('$qualified') IS NOT NULL;"
    if ($exists -ne "t" -and $exists -ne "true" -and $exists -ne "1") {
        throw "Objeto obrigatorio nao encontrado: $qualified."
    }

    $canSelect = Invoke-PsqlScalar -Sql "SELECT has_table_privilege(current_user, '$qualified', 'SELECT');"
    if ($canSelect -ne "t" -and $canSelect -ne "true" -and $canSelect -ne "1") {
        throw "Usuario Arpa precisa de SELECT em $qualified."
    }

    Assert-False -Sql "SELECT has_table_privilege(current_user, '$qualified', 'INSERT');" -FailureMessage "Usuario Arpa nao pode ter INSERT em $qualified."
    Assert-False -Sql "SELECT has_table_privilege(current_user, '$qualified', 'UPDATE');" -FailureMessage "Usuario Arpa nao pode ter UPDATE em $qualified."
    Assert-False -Sql "SELECT has_table_privilege(current_user, '$qualified', 'DELETE');" -FailureMessage "Usuario Arpa nao pode ter DELETE em $qualified."
    Assert-False -Sql "SELECT has_table_privilege(current_user, '$qualified', 'TRUNCATE');" -FailureMessage "Usuario Arpa nao pode ter TRUNCATE em $qualified."
    Assert-False -Sql "SELECT has_table_privilege(current_user, '$qualified', 'TRIGGER');" -FailureMessage "Usuario Arpa nao pode ter TRIGGER em $qualified."
}

Write-Host "Permissoes read-only validadas com sucesso."
