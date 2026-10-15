[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$binRoot = (Resolve-Path (Join-Path $root "consultant-host\bin")).Path
$removed = @()
foreach ($name in @("OdooConsultor.exe", "OdooConsultor.exe.sha256")) {
    $target = [IO.Path]::GetFullPath((Join-Path $binRoot $name))
    if ([IO.Path]::GetDirectoryName($target) -ne $binRoot) {
        throw "El objetivo calculado queda fuera de consultant-host\bin."
    }
    if (Test-Path -LiteralPath $target) {
        try {
            Remove-Item -LiteralPath $target -Force
            $removed += $name
        }
        catch {
            throw "No se pudo retirar $name. Cierra completamente Codex, permite que Kaspersky resuelva la detección y vuelve a ejecutar este script."
        }
    }
}

[pscustomobject]@{
    removed = $removed
    active_runtime = (Join-Path $binRoot "OdooConsultor.dll")
    executable_remaining = (Test-Path -LiteralPath (Join-Path $binRoot "OdooConsultor.exe"))
} | ConvertTo-Json -Depth 3
