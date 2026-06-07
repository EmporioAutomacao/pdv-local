param(
    [string]$PsqlConnection,

    [string]$PostgresHost = "",
    [int]$PostgresPort = 5432,
    [string]$DatabaseName = "",
    [string]$DatabaseUser = "",
    [string]$PasswordEnvironmentVariable = "ARPA_SYNC_READONLY_PASSWORD",
    [string]$PsqlPath = "psql",
    [string]$DockerContainer = "",
    [string]$Schema = "sync_export",
    [string[]]$Views = @("produtos", "clientes")
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($PsqlConnection)) {
    if ([string]::IsNullOrWhiteSpace($PostgresHost) -or
        [string]::IsNullOrWhiteSpace($DatabaseName) -or
        [string]::IsNullOrWhiteSpace($DatabaseUser)) {
        throw "Informe -PsqlConnection ou -PostgresHost, -DatabaseName e -DatabaseUser."
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
}

function Invoke-PsqlScalar {
    param([string]$Sql)

    $psqlArgs = @("-X", "-q", "-t", "-A", "-v", "ON_ERROR_STOP=1", "-c", $Sql)
    if ([string]::IsNullOrWhiteSpace($PsqlConnection)) {
        $psqlArgs = @("-h", $PostgresHost, "-p", $PostgresPort, "-U", $DatabaseUser, "-d", $DatabaseName) + $psqlArgs
    }
    else {
        $psqlArgs = @($PsqlConnection) + $psqlArgs
    }

    if ([string]::IsNullOrWhiteSpace($DockerContainer)) {
        $result = & $PsqlPath @psqlArgs
    }
    else {
        $result = & docker exec -e "PGPASSWORD=$env:PGPASSWORD" $DockerContainer psql @psqlArgs
    }
    if ($LASTEXITCODE -ne 0) {
        throw "psql retornou codigo $LASTEXITCODE."
    }

    return ($result | Select-Object -First 1).Trim()
}

function Assert-Column {
    param(
        [string]$ViewName,
        [string]$ColumnName
    )

    $sql = @"
SELECT count(*)
FROM information_schema.columns
WHERE table_schema = '$Schema'
  AND table_name = '$ViewName'
  AND column_name = '$ColumnName';
"@

    $count = Invoke-PsqlScalar -Sql $sql
    if ($count -ne "1") {
        throw "View $Schema.$ViewName nao possui coluna obrigatoria '$ColumnName'."
    }
}

foreach ($view in $Views) {
    Write-Host "Validando $Schema.$view ..."

    $existsSql = @"
SELECT count(*)
FROM information_schema.views
WHERE table_schema = '$Schema'
  AND table_name = '$view';
"@

    $exists = Invoke-PsqlScalar -Sql $existsSql
    if ($exists -ne "1") {
        throw "View obrigatoria nao encontrada: $Schema.$view."
    }

    Assert-Column -ViewName $view -ColumnName "entity_key"
    Assert-Column -ViewName $view -ColumnName "occurred_at_utc"
    Assert-Column -ViewName $view -ColumnName "payload_json"

    $sampleSql = "SELECT entity_key, occurred_at_utc, payload_json FROM $Schema.$view ORDER BY occurred_at_utc DESC LIMIT 1;"
    $sampleArgs = @("-X", "-q", "-v", "ON_ERROR_STOP=1", "-c", $sampleSql)
    if ([string]::IsNullOrWhiteSpace($PsqlConnection)) {
        $sampleArgs = @("-h", $PostgresHost, "-p", $PostgresPort, "-U", $DatabaseUser, "-d", $DatabaseName) + $sampleArgs
    }
    else {
        $sampleArgs = @($PsqlConnection) + $sampleArgs
    }

    if ([string]::IsNullOrWhiteSpace($DockerContainer)) {
        & $PsqlPath @sampleArgs | Out-Host
    }
    else {
        & docker exec -e "PGPASSWORD=$env:PGPASSWORD" $DockerContainer psql @sampleArgs | Out-Host
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Falha ao consultar amostra de $Schema.$view."
    }
}

Write-Host "Preflight Arpa concluido com sucesso."
