[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$source = Join-Path (Join-Path $PSScriptRoot "..") `
    "onboarding\consultant-kit\OdooAiPortableCrypto.cs"
Add-Type -TypeDefinition (Get-Content -LiteralPath $source -Raw) `
    -ReferencedAssemblies @("System.Core.dll", "System.Security.dll")

function Convert-HexToBytes([string]$Hex) {
    $result = New-Object byte[] ($Hex.Length / 2)
    for ($i = 0; $i -lt $result.Length; $i++) {
        $result[$i] = [Convert]::ToByte($Hex.Substring($i * 2, 2), 16)
    }
    return $result
}

function Convert-BytesToHex([byte[]]$Bytes) {
    return (($Bytes | ForEach-Object { $_.ToString("x2") }) -join "")
}

$key = New-Object byte[] 32
$nonce = New-Object byte[] 12
$plaintext = New-Object byte[] 16
$ciphertext = $null
$tag = $null
[OdooAiPortableCrypto]::EncryptAes256Gcm(
    $key, $nonce, (New-Object byte[] 0), $plaintext,
    [ref]$ciphertext, [ref]$tag
)

$expectedCiphertext = "cea7403d4d606b6e074ec5d3baf39d18"
$expectedTag = "d0d1c8a799996bf0265b98b5d48ab919"
if ((Convert-BytesToHex $ciphertext) -ne $expectedCiphertext) {
    throw "AES-256-GCM no coincide con el vector NIST de ciphertext."
}
if ((Convert-BytesToHex $tag) -ne $expectedTag) {
    throw "AES-256-GCM no coincide con el vector NIST de autenticación."
}

$rsa = [Security.Cryptography.RSACng]::new()
try {
    $rsa.KeySize = 3072
    $parameters = $rsa.ExportParameters($false)
    function Convert-ToBase64Url([byte[]]$Value) {
        return [Convert]::ToBase64String($Value).TrimEnd("=").Replace("+", "-").Replace("/", "_")
    }
    $wrapped = [OdooAiPortableCrypto]::WrapKeyOaepSha256(
        (Convert-ToBase64Url $parameters.Modulus),
        (Convert-ToBase64Url $parameters.Exponent),
        $key
    )
    $unwrapped = $rsa.Decrypt($wrapped, [Security.Cryptography.RSAEncryptionPadding]::OaepSHA256)
    if (-not [Linq.Enumerable]::SequenceEqual([byte[]]$key, [byte[]]$unwrapped)) {
        throw "RSA-3072/OAEP-SHA256 no recuperó la clave de contenido."
    }
} finally {
    $rsa.Dispose()
    [Array]::Clear($key, 0, $key.Length)
    [Array]::Clear($plaintext, 0, $plaintext.Length)
}

Write-Host "Criptografía portátil validada: AES-256-GCM y RSA-3072/OAEP-SHA256."

