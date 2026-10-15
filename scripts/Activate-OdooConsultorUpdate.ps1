[CmdletBinding()]
param(
    [string]$CandidatePath,
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')]
    [string]$ExpectedVersion = "1.0.0"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$binRoot = (Resolve-Path (Join-Path $root "consultant-host\bin")).Path
$launcher = Join-Path $root "consultant-host\OdooConsultor.ps1"
if ([string]::IsNullOrWhiteSpace($CandidatePath)) {
    $CandidatePath = Join-Path $binRoot "OdooConsultor-$ExpectedVersion.dll"
}
$candidate = (Resolve-Path -LiteralPath $CandidatePath).Path
$binPrefix = $binRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $candidate.StartsWith($binPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "El candidato debe estar dentro de consultant-host\bin."
}
if ([IO.Path]::GetExtension($candidate) -ne ".dll") {
    throw "La distribución local segura exige una biblioteca DLL. Los EXE sin firma ya no se activan."
}

$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($candidate)
if ($versionInfo.ProductVersion -ne $ExpectedVersion -and $versionInfo.FileVersion -ne $ExpectedVersion) {
    throw "El candidato no contiene metadatos de versión $ExpectedVersion."
}

$candidateHash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant()
$pendingManifest = Join-Path $binRoot "pending-update.json"
$pending = [ordered]@{
    schema_version = 1
    version = $ExpectedVersion
    candidate = [IO.Path]::GetFileName($candidate)
    sha256 = $candidateHash
    staged_at = [DateTimeOffset]::UtcNow.ToString("o", [Globalization.CultureInfo]::InvariantCulture)
}
[IO.File]::WriteAllText(
    $pendingManifest,
    ($pending | ConvertTo-Json -Depth 3) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false)
)

$target = Join-Path $binRoot "OdooConsultor.dll"
$activated = $false
$swap = Join-Path $binRoot ("OdooConsultor.activate." + $PID + ".dll")
try {
    Copy-Item -LiteralPath $candidate -Destination $swap -Force
    if ((Get-FileHash -LiteralPath $swap -Algorithm SHA256).Hash.ToLowerInvariant() -ne $candidateHash) {
        throw "La copia temporal no conservó el SHA-256 del candidato."
    }
    try {
        Move-Item -LiteralPath $swap -Destination $target -Force
        $activated = $true
    }
    catch {
        $activated = $false
    }
}
finally {
    if (Test-Path -LiteralPath $swap) { Remove-Item -LiteralPath $swap -Force }
}

if ($activated) {
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $candidateHash) {
        throw "La biblioteca activa no coincide con el candidato verificado."
    }
    [IO.File]::WriteAllText(
        "$target.sha256",
        "$candidateHash  OdooConsultor.dll$([Environment]::NewLine)",
        [Text.UTF8Encoding]::new($false)
    )
    Remove-Item -LiteralPath $pendingManifest -Force
}

[pscustomobject]@{
    activated = $activated
    staged = -not $activated
    runtime = $target
    launcher = $launcher
    version = $ExpectedVersion
    sha256 = $candidateHash
    pending_manifest = if ($activated) { $null } else { $pendingManifest }
    next_step = if ($activated) { "Reinicia Codex; cada proyecto se actualizará al iniciar su MCP." } else { "Reinicia Codex para activar automáticamente la actualización y actualizar el proyecto abierto." }
} | ConvertTo-Json -Depth 3
