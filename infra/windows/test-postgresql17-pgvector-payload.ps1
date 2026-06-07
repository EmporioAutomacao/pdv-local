param(
    [string]$PayloadRoot = ".\artifacts\postgresql-17\pgvector",
    [string]$PgVectorVersion = "0.8.0"
)

$ErrorActionPreference = "Stop"

$payloadPath = [IO.Path]::GetFullPath($PayloadRoot)
$controlFile = Join-Path $payloadPath "share\extension\vector.control"
$sqlFile = Join-Path $payloadPath "share\extension\vector--$PgVectorVersion.sql"
$dllFile = Join-Path $payloadPath "lib\vector.dll"

if (-not (Test-Path -LiteralPath $dllFile)) {
    throw "vector.dll nao encontrado: $dllFile"
}

if (-not (Test-Path -LiteralPath $controlFile)) {
    throw "vector.control nao encontrado: $controlFile"
}

if (-not (Test-Path -LiteralPath $sqlFile)) {
    throw "SQL da extensao nao encontrado: $sqlFile"
}

$control = Get-Content -LiteralPath $controlFile -Raw
if ($control -notmatch "default_version\s*=\s*'$([regex]::Escape($PgVectorVersion))'") {
    throw "vector.control nao declara default_version '$PgVectorVersion'."
}

$hashes = @($dllFile, $controlFile, $sqlFile) | ForEach-Object {
    $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256
    [ordered]@{
        path = $_
        size_bytes = (Get-Item -LiteralPath $_).Length
        sha256 = $hash.Hash.ToLowerInvariant()
    }
}

[ordered]@{
    status = "ok"
    pgvector_version = $PgVectorVersion
    payload_root = $payloadPath
    files = $hashes
} | ConvertTo-Json -Depth 5
