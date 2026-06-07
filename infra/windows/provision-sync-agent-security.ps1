param(
    [string]$AccessToken = "",

    [string]$TokenEnvironmentVariable = "PDV_SYNC_ERP_ACCESS_TOKEN",

    [string]$PfxPath = "",

    [securestring]$PfxPassword,

    [string]$CertificateStoreLocation = "LocalMachine",

    [string]$CertificateStoreName = "My"
)

$ErrorActionPreference = "Stop"

if (-not [string]::IsNullOrWhiteSpace($AccessToken)) {
    [Environment]::SetEnvironmentVariable(
        $TokenEnvironmentVariable,
        $AccessToken,
        [EnvironmentVariableTarget]::Machine)

    Write-Host "Bearer token provisioned in machine environment variable '$TokenEnvironmentVariable'."
}

if (-not [string]::IsNullOrWhiteSpace($PfxPath)) {
    if (-not (Test-Path -LiteralPath $PfxPath)) {
        throw "PFX file not found: $PfxPath"
    }

    $certificate = Import-PfxCertificate `
        -FilePath $PfxPath `
        -CertStoreLocation "Cert:\$CertificateStoreLocation\$CertificateStoreName" `
        -Password $PfxPassword `
        -Exportable:$false

    Write-Host "Client certificate imported."
    Write-Host "Thumbprint: $($certificate.Thumbprint)"
    Write-Host "Configure ErpSecurity:ClientCertificateThumbprint with this value."
}

Write-Host "Sync Agent security provisioning completed."
