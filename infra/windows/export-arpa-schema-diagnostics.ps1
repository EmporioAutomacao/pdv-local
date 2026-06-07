param(
    [string]$PostgresHost = "",
    [int]$PostgresPort = 5432,
    [string]$DatabaseName = "",
    [string]$DatabaseUser = "",
    [string]$PasswordEnvironmentVariable = "ARPA_SYNC_READONLY_PASSWORD",
    [string]$PsqlPath = "psql",
    [string]$DockerContainer = "",
    [string]$OutputFile = ".\artifacts\arpa-schema-diagnostics.json"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($PostgresHost) -or
    [string]::IsNullOrWhiteSpace($DatabaseName) -or
    [string]::IsNullOrWhiteSpace($DatabaseUser)) {
    throw "Informe -PostgresHost, -DatabaseName e -DatabaseUser."
}

$env:PGPASSWORD = [Environment]::GetEnvironmentVariable($PasswordEnvironmentVariable)
if ([string]::IsNullOrWhiteSpace($env:PGPASSWORD)) {
    throw "Variavel de ambiente $PasswordEnvironmentVariable nao encontrada."
}

function Invoke-PsqlTsv {
    param([string]$Sql)

    $args = @(
        "-h", $PostgresHost,
        "-p", $PostgresPort,
        "-U", $DatabaseUser,
        "-d", $DatabaseName,
        "-X", "-q", "-t", "-A",
        "-v", "ON_ERROR_STOP=1",
        "-F", "`t",
        "-c", $Sql
    )

    if ([string]::IsNullOrWhiteSpace($DockerContainer)) {
        $rows = & $PsqlPath @args
    }
    else {
        $rows = & docker exec -e "PGPASSWORD=$env:PGPASSWORD" $DockerContainer psql @args
    }

    if ($LASTEXITCODE -ne 0) {
        throw "psql retornou codigo $LASTEXITCODE."
    }

    return @($rows | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Get-TableColumns {
    param([string[]]$Tables)

    $tableList = ($Tables | ForEach-Object { "'" + $_.Replace("'", "''") + "'" }) -join ", "
    $sql = @"
SELECT table_name, column_name, data_type, is_nullable
FROM information_schema.columns
WHERE table_schema = 'public'
  AND table_name IN ($tableList)
ORDER BY table_name, ordinal_position;
"@

    $rows = Invoke-PsqlTsv -Sql $sql
    return @($rows | ForEach-Object {
        $parts = $_ -split "`t", 4
        [ordered]@{
            table = $parts[0]
            column = $parts[1]
            data_type = $parts[2]
            is_nullable = $parts[3]
        }
    })
}

function Get-ExistingTables {
    $sql = @"
SELECT table_name
FROM information_schema.tables
WHERE table_schema = 'public'
  AND table_type IN ('BASE TABLE', 'VIEW')
ORDER BY table_name;
"@

    return [string[]](Invoke-PsqlTsv -Sql $sql)
}

function Select-FirstExisting {
    param(
        [string[]]$Columns,
        [string[]]$Candidates
    )

    foreach ($candidate in $Candidates) {
        if ($Columns -contains $candidate) {
            return $candidate
        }
    }
    return ""
}

$tables = Get-ExistingTables
$targetTables = @("produtos", "produto", "clientes", "cliente")
$columns = Get-TableColumns -Tables $targetTables

$columnsByTable = @{}
foreach ($row in $columns) {
    if (-not $columnsByTable.ContainsKey($row.table)) {
        $columnsByTable[$row.table] = New-Object System.Collections.Generic.List[string]
    }
    $columnsByTable[$row.table].Add($row.column)
}

$produtoTable = @("produtos", "produto") | Where-Object { $columnsByTable.ContainsKey($_) } | Select-Object -First 1
$clienteTable = @("clientes", "cliente") | Where-Object { $columnsByTable.ContainsKey($_) } | Select-Object -First 1

$produtoColumns = if ($produtoTable) { [string[]]$columnsByTable[$produtoTable] } else { @() }
$clienteColumns = if ($clienteTable) { [string[]]$columnsByTable[$clienteTable] } else { @() }

$diagnostics = [ordered]@{
    generated_at_utc = (Get-Date).ToUniversalTime().ToString("o")
    database = $DatabaseName
    host = $PostgresHost
    port = $PostgresPort
    inspected_tables = $targetTables
    existing_public_tables_count = $tables.Count
    detected = [ordered]@{
        produto = [ordered]@{
            table = $produtoTable
            code_column = Select-FirstExisting $produtoColumns @("codigo", "cod_produto", "id")
            name_column = Select-FirstExisting $produtoColumns @("descricao", "nome", "descricao_produto")
            updated_column = Select-FirstExisting $produtoColumns @("updated_at_utc", "atualizado_em", "updated_at", "data_alteracao", "dt_alteracao", "data_cadastro", "datacad", "created_at_utc", "created_at")
            barcode_column = Select-FirstExisting $produtoColumns @("codigodebarras", "codigo_barras", "codigobarras", "codbarra", "ean", "gtin", "ean13", "barcode")
            all_columns = $produtoColumns
        }
        cliente = [ordered]@{
            table = $clienteTable
            code_column = Select-FirstExisting $clienteColumns @("codigo", "id", "cod_cliente")
            name_column = Select-FirstExisting $clienteColumns @("nome", "razao_social", "razao", "cliente", "fantasia")
            document_column = Select-FirstExisting $clienteColumns @("cpf_cnpj", "cnpj_cpf", "cnpjcpf", "cnpj", "cpf", "documento", "doc")
            updated_column = Select-FirstExisting $clienteColumns @("updated_at_utc", "atualizado_em", "updated_at", "data_alteracao", "dt_alteracao", "data_cadastro", "datacad", "created_at_utc", "created_at")
            all_columns = $clienteColumns
        }
    }
    columns = $columns
}

$outputDir = Split-Path -Parent $OutputFile
if (-not [string]::IsNullOrWhiteSpace($outputDir)) {
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
}

$diagnostics | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputFile -Encoding UTF8
Write-Host "Diagnostico de schema Arpa gerado em: $OutputFile"
Write-Host "Produto: table=$produtoTable columns=$($produtoColumns.Count)"
Write-Host "Cliente: table=$clienteTable columns=$($clienteColumns.Count)"
