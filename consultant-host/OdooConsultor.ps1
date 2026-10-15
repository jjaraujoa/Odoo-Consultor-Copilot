[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$CommandArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$binRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "bin"))
$assemblyPath = Join-Path $binRoot "OdooConsultor.dll"
$pendingManifest = Join-Path $binRoot "pending-update.json"

function Install-PendingOdooConsultorUpdate {
    if (-not (Test-Path -LiteralPath $pendingManifest)) { return $false }

    $mutex = [Threading.Mutex]::new($false, "Local\OdooConsultorUpdate")
    $locked = $false
    try {
        $locked = $mutex.WaitOne(10000)
        if (-not $locked) { return $false }
        if (-not (Test-Path -LiteralPath $pendingManifest)) { return $false }

        $manifest = Get-Content -Raw -LiteralPath $pendingManifest | ConvertFrom-Json
        $version = [string]$manifest.version
        $expectedHash = ([string]$manifest.sha256).ToLowerInvariant()
        $candidateName = [string]$manifest.candidate
        if ($manifest.schema_version -ne 1 -or $version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or
            $expectedHash -notmatch '^[a-f0-9]{64}$' -or
            $candidateName -ne "OdooConsultor-$version.dll") {
            throw "El manifiesto de actualización pendiente no es válido."
        }

        $candidate = [IO.Path]::GetFullPath((Join-Path $binRoot $candidateName))
        $binPrefix = $binRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $candidate.StartsWith($binPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $candidate)) {
            throw "La actualización pendiente no está contenida en consultant-host\bin."
        }
        $candidateVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($candidate)
        if ($candidateVersion.ProductVersion -ne $version -and $candidateVersion.FileVersion -ne $version) {
            throw "La versión de la actualización pendiente no coincide con su manifiesto."
        }
        if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) {
            throw "El SHA-256 de la actualización pendiente no coincide."
        }

        $swap = Join-Path $binRoot ("OdooConsultor.swap." + $PID + ".dll")
        try {
            Copy-Item -LiteralPath $candidate -Destination $swap -Force
            if ((Get-FileHash -LiteralPath $swap -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) {
                throw "La copia temporal de la actualización no conservó su SHA-256."
            }
            try { Move-Item -LiteralPath $swap -Destination $assemblyPath -Force }
            catch { return $false }
        }
        finally {
            if (Test-Path -LiteralPath $swap) { Remove-Item -LiteralPath $swap -Force }
        }

        if ((Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) {
            throw "La biblioteca activa no coincide con la actualización verificada."
        }
        [IO.File]::WriteAllText(
            "$assemblyPath.sha256",
            "$expectedHash  OdooConsultor.dll$([Environment]::NewLine)",
            [Text.UTF8Encoding]::new($false)
        )
        Remove-Item -LiteralPath $pendingManifest -Force
        return $true
    }
    finally {
        if ($locked) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

[void](Install-PendingOdooConsultorUpdate)
if (-not (Test-Path -LiteralPath $assemblyPath)) {
    throw "No existe consultant-host\bin\OdooConsultor.dll. Compila o activa el motor antes de usarlo."
}

$assemblyPath = (Resolve-Path -LiteralPath $assemblyPath).Path
[void][Reflection.Assembly]::LoadFrom($assemblyPath)

# Cada carpeta cliente actualiza únicamente el bloque administrado y su MCP la
# primera vez que arranca con una versión nueva. El conocimiento del cliente no
# se sobrescribe.
if ($CommandArgs.Count -gt 0 -and $CommandArgs[0] -eq "mcp") {
    $projectIndex = [Array]::IndexOf($CommandArgs, "--proyecto")
    if ($projectIndex -ge 0 -and $projectIndex + 1 -lt $CommandArgs.Count) {
        $projectRoot = [IO.Path]::GetFullPath($CommandArgs[$projectIndex + 1])
        $markerDirectory = Join-Path $projectRoot ".codex"
        $markerPath = Join-Path $markerDirectory "odoo-consultor.version"
        $runtimeVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($assemblyPath).ProductVersion
        $installedVersion = if (Test-Path -LiteralPath $markerPath) { (Get-Content -Raw -LiteralPath $markerPath).Trim() } else { "" }
        if ($installedVersion -ne $runtimeVersion) {
            $settings = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
            [void][OdooConsultor.ProjectStore]::Upgrade($projectRoot, $assemblyPath, $settings)
            [IO.Directory]::CreateDirectory($markerDirectory) | Out-Null
            [IO.File]::WriteAllText($markerPath, $runtimeVersion + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
        }
    }
}

$exitCode = [OdooConsultor.Program]::Main($CommandArgs)
exit $exitCode
