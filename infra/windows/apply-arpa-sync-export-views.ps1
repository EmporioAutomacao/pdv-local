param(
    [Parameter(Mandatory = $true)]
    [string]$SqlFile,

    [string]$PostgresHost = "",
    [int]$PostgresPort = 5432,
    [string]$DatabaseName = "",
    [string]$DatabaseUser = "",
    [string]$ExpectedDatabaseName = "",
    [string]$PasswordEnvironmentVariable = "ARPA_SYNC_READONLY_PASSWORD",
    [string]$PsqlPath = "psql",
    [string]$DockerContainer = "",
    [switch]$ConfirmApply
)

$ErrorActionPreference = "Stop"

if (-not $ConfirmApply) {
    throw "Aplicacao bloqueada. Reexecute com -ConfirmApply apos escolher a conexao Arpa correta."
}

if ($DatabaseUser -match "(?i)sync|agent|readonly|read_only|leitura") {
    throw "Nao use o usuario runtime/read-only do SyncAgent para aplicar DDL. Use um usuario DBA apenas para criar as views e depois valide permissoes read-only."
}

if (-not (Test-Path -LiteralPath $SqlFile)) {
    throw "Arquivo SQL nao encontrado: $SqlFile"
}

if ([string]::IsNullOrWhiteSpace($PostgresHost) -or
    [string]::IsNullOrWhiteSpace($DatabaseName) -or
    [string]::IsNullOrWhiteSpace($DatabaseUser)) {
    throw "Informe -PostgresHost, -DatabaseName e -DatabaseUser."
}

if (-not [string]::IsNullOrWhiteSpace($ExpectedDatabaseName) -and $DatabaseName -ne $ExpectedDatabaseName) {
    throw "DatabaseName '$DatabaseName' difere de ExpectedDatabaseName '$ExpectedDatabaseName'."
}

$env:PGPASSWORD = [Environment]::GetEnvironmentVariable($PasswordEnvironmentVariable)
if ([string]::IsNullOrWhiteSpace($env:PGPASSWORD)) {
    throw "Variavel de ambiente $PasswordEnvironmentVariable nao encontrada."
}

$resolvedSqlFile = Resolve-Path -LiteralPath $SqlFile

Write-Host "Aplicando views sync_export."
Write-Host "Host: $PostgresHost"
Write-Host "Port: $PostgresPort"
Write-Host "Database: $DatabaseName"
Write-Host "User: $DatabaseUser"
Write-Host "SQL: $resolvedSqlFile"

$argsList = @(
    "-h", $PostgresHost,
    "-p", $PostgresPort,
    "-U", $DatabaseUser,
    "-d", $DatabaseName,
    "-X",
    "-v", "ON_ERROR_STOP=1",
    "-f", $resolvedSqlFile
)

if ([string]::IsNullOrWhiteSpace($DockerContainer)) {
    & $PsqlPath @argsList
}
else {
    & docker exec -e "PGPASSWORD=$env:PGPASSWORD" $DockerContainer psql @argsList
}

if ($LASTEXITCODE -ne 0) {
    throw "Falha ao aplicar views sync_export. psql retornou codigo $LASTEXITCODE."
}

Write-Host "Views sync_export aplicadas. Execute o preflight antes de habilitar o collector."
