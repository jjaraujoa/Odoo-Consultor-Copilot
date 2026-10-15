[CmdletBinding(DefaultParameterSetName = "Store")]
param(
    [Parameter(Mandatory, ParameterSetName = "Store")][string]$Thumbprint,
    [Parameter(Mandatory, ParameterSetName = "Pfx")][string]$Pfx,
    [Parameter(ParameterSetName = "Pfx")][Security.SecureString]$PfxPassword,
    [string]$TimestampServer = "http://timestamp.digicert.com"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$script = Join-Path $root "onboarding\consultant-kit\Crear-PaqueteCliente.ps1"

if ($PSCmdlet.ParameterSetName -eq "Store") {
    $certificate = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Thumbprint -eq $Thumbprint -and $_.HasPrivateKey } |
        Select-Object -First 1
} else {
    if (-not $PfxPassword) {
        $PfxPassword = Read-Host "Contraseña del certificado PFX" -AsSecureString
    }
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        (Resolve-Path -LiteralPath $Pfx).Path,
        $PfxPassword,
        [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
    )
}
if (-not $certificate) { throw "No se encontró un certificado de firma con clave privada." }
if (-not ($certificate.EnhancedKeyUsageList.ObjectId.Value -contains "1.3.6.1.5.5.7.3.3")) {
    throw "El certificado no declara el uso de firma de código."
}

$signature = Set-AuthenticodeSignature `
    -FilePath $script `
    -Certificate $certificate `
    -HashAlgorithm SHA256 `
    -TimestampServer $TimestampServer
if ($signature.Status -ne "Valid") {
    throw "La firma no quedó válida: $($signature.StatusMessage)"
}
Write-Host "Kit firmado correctamente: $script"

