[CmdletBinding()]
param(
    [string]$DestinationRoot = (Join-Path $env:USERPROFILE ".codex\skills")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$source = (Resolve-Path (Join-Path $repositoryRoot "skills\odoo-replicar-personalizacion")).Path
$destinationRootFull = [IO.Path]::GetFullPath($DestinationRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$target = [IO.Path]::GetFullPath((Join-Path $destinationRootFull "odoo-replicar-personalizacion"))
$expectedPrefix = $destinationRootFull + [IO.Path]::DirectorySeparatorChar

if (-not $target.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "El destino de la skill queda fuera de la carpeta global indicada."
}

New-Item -ItemType Directory -Path $destinationRootFull -Force | Out-Null
$staging = Join-Path $destinationRootFull (".odoo-replicar-personalizacion-" + [Guid]::NewGuid().ToString("N"))
try {
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    Copy-Item -Path (Join-Path $source "*") -Destination $staging -Recurse -Force
    if (-not (Test-Path -LiteralPath (Join-Path $staging "SKILL.md"))) { throw "La copia temporal no contiene SKILL.md." }
    if (Test-Path -LiteralPath $target) {
        $resolvedTarget = (Resolve-Path -LiteralPath $target).Path
        if (-not [string]::Equals($resolvedTarget, $target, [StringComparison]::OrdinalIgnoreCase)) { throw "El destino resuelto no coincide con la skill administrada." }
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
    }
    Move-Item -LiteralPath $staging -Destination $target
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}

[pscustomobject]@{
    installed = $true
    skill = "odoo-replicar-personalizacion"
    target = $target
    restart_required = $true
} | ConvertTo-Json -Depth 3
