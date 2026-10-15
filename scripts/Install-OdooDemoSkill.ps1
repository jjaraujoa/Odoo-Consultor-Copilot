[CmdletBinding()]
param(
    [string]$DestinationRoot = (Join-Path $env:USERPROFILE ".codex\skills")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$source = (Resolve-Path (Join-Path $repositoryRoot "skills\odoo-preparar-demostracion")).Path
$destinationRootFull = [IO.Path]::GetFullPath($DestinationRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$target = [IO.Path]::GetFullPath((Join-Path $destinationRootFull "odoo-preparar-demostracion"))
$expectedPrefix = $destinationRootFull + [IO.Path]::DirectorySeparatorChar

if (-not $target.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "El destino de la skill queda fuera de la carpeta global indicada."
}

New-Item -ItemType Directory -Path $destinationRootFull -Force | Out-Null
$staging = Join-Path $destinationRootFull (".odoo-preparar-demostracion-" + [Guid]::NewGuid().ToString("N"))
try {
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    Copy-Item -Path (Join-Path $source "*") -Destination $staging -Recurse -Force
    if (-not (Test-Path -LiteralPath (Join-Path $staging "SKILL.md"))) {
        throw "La copia temporal no contiene SKILL.md."
    }
    if (Test-Path -LiteralPath $target) {
        $resolvedTarget = (Resolve-Path -LiteralPath $target).Path
        if (-not [string]::Equals($resolvedTarget, $target, [StringComparison]::OrdinalIgnoreCase)) {
            throw "El destino resuelto no coincide con la skill administrada."
        }
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
    }
    Move-Item -LiteralPath $staging -Destination $target
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}

$files = Get-ChildItem -LiteralPath $target -File -Recurse | Sort-Object FullName
$fingerprintInput = ($files | ForEach-Object { "{0}:{1}" -f $_.FullName.Substring($target.Length), (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }) -join "`n"
$sha = [Security.Cryptography.SHA256]::Create()
try {
    $bytes = [Text.Encoding]::UTF8.GetBytes($fingerprintInput)
    $fingerprint = ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace("-", "").ToLowerInvariant()
}
finally {
    $sha.Dispose()
}

[pscustomobject]@{
    installed = $true
    skill = "odoo-preparar-demostracion"
    target = $target
    files = $files.Count
    fingerprint = $fingerprint
    restart_required = $true
} | ConvertTo-Json -Depth 3
