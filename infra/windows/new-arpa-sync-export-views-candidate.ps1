param(
    [string]$PostgresHost = "",
    [int]$PostgresPort = 5432,
    [string]$DatabaseName = "",
    [string]$DatabaseUser = "",
    [string]$PasswordEnvironmentVariable = "ARPA_SYNC_READONLY_PASSWORD",
    [string]$PsqlPath = "psql",
    [string]$DockerContainer = "",
    [string]$OutputFile = ".\infra\arpa\sync-export-views.generated.sql"
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

function Invoke-PsqlRows {
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

function Get-Columns {
    param([string]$TableName)

    $sql = @"
SELECT column_name
FROM information_schema.columns
WHERE table_schema = 'public'
  AND table_name = '$TableName'
ORDER BY ordinal_position;
"@

    return [string[]](Invoke-PsqlRows -Sql $sql)
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

function Quote-Identifier {
    param([string]$Name)
    return '"' + $Name.Replace('"', '""') + '"'
}

function Sql-ValueOrNull {
    param([string]$Column)
    if ([string]::IsNullOrWhiteSpace($Column)) {
        return "NULL"
    }
    return Quote-Identifier -Name $Column
}

function Product-ActiveExpression {
    param([string[]]$Columns)
    if (-not ($Columns -contains "ativo")) {
        return "true"
    }
    return "CASE WHEN UPPER(CAST(""ativo"" AS TEXT)) IN ('0', 'FALSE', 'F', 'A', 'ATIVO') THEN true WHEN UPPER(CAST(""ativo"" AS TEXT)) IN ('1', 'TRUE', 'T', 'I', 'INATIVO') THEN false ELSE true END"
}

function Customer-ActiveExpression {
    param([string[]]$Columns)
    if ($Columns -contains "status") {
        return "CASE WHEN UPPER(CAST(""status"" AS TEXT)) IN ('A', 'ATIVO', '1', 'TRUE', 'T', 'SIM', 'S') THEN true WHEN UPPER(CAST(""status"" AS TEXT)) IN ('I', 'INATIVO', '0', 'FALSE', 'F', 'NAO', 'N') THEN false ELSE true END"
    }
    if ($Columns -contains "ativo") {
        return "CASE WHEN UPPER(CAST(""ativo"" AS TEXT)) IN ('1', 'TRUE', 'T', 'A', 'ATIVO', 'SIM', 'S') THEN true WHEN UPPER(CAST(""ativo"" AS TEXT)) IN ('0', 'FALSE', 'F', 'I', 'INATIVO', 'NAO', 'N') THEN false ELSE true END"
    }
    return "true"
}

$productTable = ""
$productColumns = @()
foreach ($table in @("produtos", "produto")) {
    $cols = Get-Columns -TableName $table
    if ($cols.Count -gt 0) {
        $productTable = $table
        $productColumns = $cols
        break
    }
}

$customerTable = ""
$customerColumns = @()
foreach ($table in @("clientes", "cliente")) {
    $cols = Get-Columns -TableName $table
    if ($cols.Count -gt 0) {
        $customerTable = $table
        $customerColumns = $cols
        break
    }
}

if ([string]::IsNullOrWhiteSpace($productTable) -and [string]::IsNullOrWhiteSpace($customerTable)) {
    throw "Nenhuma tabela candidata encontrada em public.produtos/produto ou public.clientes/cliente."
}

$productCode = Select-FirstExisting $productColumns @("codigo", "cod_produto", "id")
$productName = Select-FirstExisting $productColumns @("descricao", "nome", "descricao_produto")
$productUpdated = Select-FirstExisting $productColumns @("updated_at_utc", "atualizado_em", "updated_at", "data_alteracao", "dt_alteracao", "data_cadastro", "datacad", "created_at_utc", "created_at")
$productFactory = Select-FirstExisting $productColumns @("codigodefabrica", "codigo_fabrica", "referencia", "ref")
$productNcm = Select-FirstExisting $productColumns @("cod_ncm", "ncm")
$productBarcode = Select-FirstExisting $productColumns @("codigodebarras", "codigo_barras", "codigobarras", "codbarra", "ean", "gtin", "ean13", "barcode")
$productActive = Select-FirstExisting $productColumns @("ativo", "status", "situacao")

$customerCode = Select-FirstExisting $customerColumns @("codigo", "id", "cod_cliente")
$customerName = Select-FirstExisting $customerColumns @("nome", "razao_social", "razao", "cliente", "fantasia")
$customerUpdated = Select-FirstExisting $customerColumns @("updated_at_utc", "atualizado_em", "updated_at", "data_alteracao", "dt_alteracao", "data_cadastro", "datacad", "created_at_utc", "created_at")
$customerDoc = Select-FirstExisting $customerColumns @("cpf_cnpj", "cnpj_cpf", "cnpjcpf", "cnpj", "cpf", "documento", "doc")
$customerEmail = Select-FirstExisting $customerColumns @("email", "email_principal", "e_mail", "mail")
$customerPhone = Select-FirstExisting $customerColumns @("telefone", "fone", "celular", "whatsapp", "tel", "telefone1", "fone1")
$customerActive = Select-FirstExisting $customerColumns @("ativo", "status", "situacao")

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("-- SQL candidato gerado por infra/windows/new-arpa-sync-export-views-candidate.ps1")
$lines.Add("-- Revisar antes de aplicar no Arpa real.")
$lines.Add("")
$lines.Add("CREATE SCHEMA IF NOT EXISTS sync_export;")
$lines.Add("")

if (-not [string]::IsNullOrWhiteSpace($productTable)) {
    if ([string]::IsNullOrWhiteSpace($productCode) -or [string]::IsNullOrWhiteSpace($productName)) {
        $lines.Add("-- Produto: tabela candidata encontrada, mas faltam colunas minimas codigo/nome.")
        $lines.Add("-- Tabela: public.$productTable")
    }
    else {
        $productOccurred = if ($productUpdated) { (Quote-Identifier $productUpdated) + "::timestamptz" } else { "now()::timestamptz" }
        $productActiveSql = Product-ActiveExpression -Columns $productColumns
        $lines.Add("CREATE OR REPLACE VIEW sync_export.produtos AS")
        $lines.Add("SELECT")
        $lines.Add("    $(Quote-Identifier $productCode)::text AS entity_key,")
        $lines.Add("    $productOccurred AS occurred_at_utc,")
        $lines.Add("    jsonb_build_object(")
        $lines.Add("        'codigo', $(Quote-Identifier $productCode),")
        $lines.Add("        'descricao', $(Quote-Identifier $productName),")
        $lines.Add("        'codigodefabrica', $(Sql-ValueOrNull $productFactory),")
        $lines.Add("        'cod_ncm', $(Sql-ValueOrNull $productNcm),")
        $lines.Add("        'codigodebarras', $(Sql-ValueOrNull $productBarcode),")
        $lines.Add("        'ativo', $productActiveSql")
        $lines.Add("    )::text AS payload_json,")
        $lines.Add("    concat('arpa-produto-', $(Quote-Identifier $productCode))::text AS trace_id")
        $lines.Add("FROM public.$(Quote-Identifier $productTable)")
        $lines.Add("WHERE $(Quote-Identifier $productCode) IS NOT NULL;")
    }
    $lines.Add("")
}

if (-not [string]::IsNullOrWhiteSpace($customerTable)) {
    if ([string]::IsNullOrWhiteSpace($customerCode) -or [string]::IsNullOrWhiteSpace($customerName)) {
        $lines.Add("-- Cliente: tabela candidata encontrada, mas faltam colunas minimas codigo/nome.")
        $lines.Add("-- Tabela: public.$customerTable")
    }
    else {
        $customerOccurred = if ($customerUpdated) { (Quote-Identifier $customerUpdated) + "::timestamptz" } else { "now()::timestamptz" }
        $customerActiveSql = Customer-ActiveExpression -Columns $customerColumns
        $lines.Add("CREATE OR REPLACE VIEW sync_export.clientes AS")
        $lines.Add("SELECT")
        $lines.Add("    $(Quote-Identifier $customerCode)::text AS entity_key,")
        $lines.Add("    $customerOccurred AS occurred_at_utc,")
        $lines.Add("    jsonb_build_object(")
        $lines.Add("        'codigo', $(Quote-Identifier $customerCode),")
        $lines.Add("        'nome', $(Quote-Identifier $customerName),")
        $lines.Add("        'documento', $(Sql-ValueOrNull $customerDoc),")
        $lines.Add("        'email', $(Sql-ValueOrNull $customerEmail),")
        $lines.Add("        'telefone', $(Sql-ValueOrNull $customerPhone),")
        $lines.Add("        'ativo', $customerActiveSql")
        $lines.Add("    )::text AS payload_json,")
        $lines.Add("    concat('arpa-cliente-', $(Quote-Identifier $customerCode))::text AS trace_id")
        $lines.Add("FROM public.$(Quote-Identifier $customerTable)")
        $lines.Add("WHERE $(Quote-Identifier $customerCode) IS NOT NULL;")
    }
    $lines.Add("")
}

$outputPath = Resolve-Path -Path (Split-Path -Parent $OutputFile) -ErrorAction SilentlyContinue
if ($outputPath -eq $null) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputFile) | Out-Null
}

$lines | Set-Content -LiteralPath $OutputFile -Encoding UTF8

Write-Host "SQL candidato gerado em: $OutputFile"
Write-Host "Produto: table=$productTable code=$productCode name=$productName updated=$productUpdated"
Write-Host "Cliente: table=$customerTable code=$customerCode name=$customerName updated=$customerUpdated"
Write-Host "Revise o arquivo antes de aplicar no Arpa real."
