# LEGADO. O caminho padrao agora e o botao "Preparar views" (Configuracoes >
# Arpa), que roda infra/arpa/sync-export-views.sql - um unico script
# introspectivo, generico e incremental, aplicado por conexao com credencial
# DBA transitoria. Este gerador manual so serve para depuracao / casos em que a
# introspeccao automatica nao cobre o schema.

param(
    [Parameter(Mandatory = $true)]
    [string]$DiagnosticsFile,

    [string]$OutputFile = ".\infra\arpa\sync-export-views.generated.sql",
    [switch]$AllowInitialLoadOnly
)

$ErrorActionPreference = "Stop"

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

function Resolve-OccurredAt {
    param(
        [string]$EntityName,
        [string]$UpdatedColumn
    )

    if (-not [string]::IsNullOrWhiteSpace($UpdatedColumn)) {
        return "$(Quote-Identifier $UpdatedColumn)::timestamptz"
    }

    if (-not $AllowInitialLoadOnly) {
        throw "$EntityName nao tem coluna temporal detectada. Use -AllowInitialLoadOnly apenas para carga inicial controlada."
    }

    return "TIMESTAMPTZ '2000-01-01 00:00:00+00'"
}

function Product-ActiveExpression {
    param([object]$Produto)

    if (-not ($Produto.all_columns -contains "ativo")) {
        return "true"
    }

    $col = Quote-Identifier "ativo"
    return "CASE WHEN UPPER(CAST($col AS TEXT)) IN ('0', 'FALSE', 'F', 'A', 'ATIVO') THEN true WHEN UPPER(CAST($col AS TEXT)) IN ('1', 'TRUE', 'T', 'I', 'INATIVO') THEN false ELSE true END"
}

function Customer-ActiveExpression {
    param([object]$Cliente)

    if ($Cliente.all_columns -contains "status") {
        $col = Quote-Identifier "status"
        return "CASE WHEN UPPER(CAST($col AS TEXT)) IN ('A', 'ATIVO', '1', 'TRUE', 'T', 'SIM', 'S') THEN true WHEN UPPER(CAST($col AS TEXT)) IN ('I', 'INATIVO', '0', 'FALSE', 'F', 'NAO', 'N') THEN false ELSE true END"
    }

    if ($Cliente.all_columns -contains "ativo") {
        $col = Quote-Identifier "ativo"
        return "CASE WHEN UPPER(CAST($col AS TEXT)) IN ('1', 'TRUE', 'T', 'A', 'ATIVO', 'SIM', 'S') THEN true WHEN UPPER(CAST($col AS TEXT)) IN ('0', 'FALSE', 'F', 'I', 'INATIVO', 'NAO', 'N') THEN false ELSE true END"
    }

    return "true"
}

$diagnostics = Get-Content -LiteralPath $DiagnosticsFile -Raw | ConvertFrom-Json
$produto = $diagnostics.detected.produto
$cliente = $diagnostics.detected.cliente

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("-- SQL candidato gerado de diagnostico Arpa.")
$lines.Add("-- Arquivo de origem: $DiagnosticsFile")
$lines.Add("-- Revisar antes de aplicar no Arpa real.")
if ($AllowInitialLoadOnly) {
    $lines.Add("-- MODO CARGA INICIAL: entidade sem coluna temporal usa timestamp fixo.")
    $lines.Add("-- Nao usar para sincronizacao continua sem definir uma fonte de alteracao.")
}
$lines.Add("")
$lines.Add("CREATE SCHEMA IF NOT EXISTS sync_export;")
$lines.Add("")

if ([string]::IsNullOrWhiteSpace($produto.table) -or
    [string]::IsNullOrWhiteSpace($produto.code_column) -or
    [string]::IsNullOrWhiteSpace($produto.name_column)) {
    throw "Diagnostico de produto incompleto: table/code_column/name_column sao obrigatorios."
}

$produtoOccurred = Resolve-OccurredAt -EntityName "produto" -UpdatedColumn $produto.updated_column
$produtoActive = Product-ActiveExpression -Produto $produto
$lines.Add("CREATE OR REPLACE VIEW sync_export.produtos AS")
$lines.Add("SELECT")
$lines.Add("    $(Quote-Identifier $produto.code_column)::text AS entity_key,")
$lines.Add("    $produtoOccurred AS occurred_at_utc,")
$lines.Add("    jsonb_build_object(")
$lines.Add("        'codigo', $(Quote-Identifier $produto.code_column),")
$lines.Add("        'descricao', $(Quote-Identifier $produto.name_column),")
$lines.Add("        'codigodefabrica', $(Sql-ValueOrNull 'codigodefabrica'),")
$lines.Add("        'cod_ncm', $(Sql-ValueOrNull 'cod_ncm'),")
$lines.Add("        'codigodebarras', $(Sql-ValueOrNull $produto.barcode_column),")
$lines.Add("        'ativo', $produtoActive")
$lines.Add("    )::text AS payload_json,")
$lines.Add("    concat('arpa-produto-', $(Quote-Identifier $produto.code_column))::text AS trace_id")
$lines.Add("FROM public.$(Quote-Identifier $produto.table)")
$lines.Add("WHERE $(Quote-Identifier $produto.code_column) IS NOT NULL;")
$lines.Add("")

if ([string]::IsNullOrWhiteSpace($cliente.table) -or
    [string]::IsNullOrWhiteSpace($cliente.code_column) -or
    [string]::IsNullOrWhiteSpace($cliente.name_column)) {
    throw "Diagnostico de cliente incompleto: table/code_column/name_column sao obrigatorios."
}

$clienteOccurred = Resolve-OccurredAt -EntityName "cliente" -UpdatedColumn $cliente.updated_column
$clienteActive = Customer-ActiveExpression -Cliente $cliente
$lines.Add("CREATE OR REPLACE VIEW sync_export.clientes AS")
$lines.Add("SELECT")
$lines.Add("    $(Quote-Identifier $cliente.code_column)::text AS entity_key,")
$lines.Add("    $clienteOccurred AS occurred_at_utc,")
$lines.Add("    jsonb_build_object(")
$lines.Add("        'codigo', $(Quote-Identifier $cliente.code_column),")
$lines.Add("        'nome', $(Quote-Identifier $cliente.name_column),")
$lines.Add("        'documento', $(Sql-ValueOrNull $cliente.document_column),")
$lines.Add("        'email', $(Sql-ValueOrNull 'email'),")
$lines.Add("        'telefone', $(Sql-ValueOrNull 'fone'),")
$lines.Add("        'ativo', $clienteActive")
$lines.Add("    )::text AS payload_json,")
$lines.Add("    concat('arpa-cliente-', $(Quote-Identifier $cliente.code_column))::text AS trace_id")
$lines.Add("FROM public.$(Quote-Identifier $cliente.table)")
$lines.Add("WHERE $(Quote-Identifier $cliente.code_column) IS NOT NULL;")
$lines.Add("")

$outputDir = Split-Path -Parent $OutputFile
if (-not [string]::IsNullOrWhiteSpace($outputDir)) {
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
}

$lines | Set-Content -LiteralPath $OutputFile -Encoding UTF8

Write-Host "SQL candidato gerado em: $OutputFile"
if ($AllowInitialLoadOnly) {
    Write-Host "ATENCAO: gerado em modo carga inicial; revisar watermark antes de sync continuo."
}
