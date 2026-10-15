[CmdletBinding()]
param(
    [switch]$Clean,
    [ValidatePattern('^OdooConsultor(?:-[0-9]+\.[0-9]+\.[0-9]+)?\.(?:dll|exe)$')]
    [string]$OutputName = "OdooConsultor.dll",
    [switch]$AllowUnsignedExecutable
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$sourceRoot = Join-Path $root "consultant-host\src"
$outputRoot = Join-Path $root "consultant-host\bin"
$output = Join-Path $outputRoot $OutputName
if ([IO.Path]::GetExtension($OutputName) -eq ".exe" -and -not $AllowUnsignedExecutable) {
    throw "La compilación EXE sin firma está deshabilitada por políticas de seguridad. Use la biblioteca DLL para ejecución segura con el lanzador."
}

if ($Clean -and (Test-Path -LiteralPath $outputRoot)) {
    $resolvedOutput = (Resolve-Path -LiteralPath $outputRoot).Path
    $expectedParent = (Resolve-Path -LiteralPath (Join-Path $root "consultant-host")).Path
    if (-not $resolvedOutput.StartsWith($expectedParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "La carpeta de salida queda fuera de consultant-host."
    }
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$sources = Get-ChildItem -LiteralPath $sourceRoot -Filter "*.cs" | Sort-Object Name | Select-Object -ExpandProperty FullName
if (-not $sources) { throw "No se encontraron fuentes C# en $sourceRoot." }
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Force }

$references = @(
    "System.dll",
    "System.Core.dll",
    "System.Web.Extensions.dll",
    "System.Xml.dll",
    "System.Windows.Forms.dll",
    "System.Drawing.dll",
    "System.Security.dll"
)

Add-Type `
    -Path $sources `
    -ReferencedAssemblies $references `
    -OutputAssembly $output `
    -OutputType $(if ([IO.Path]::GetExtension($OutputName) -eq ".dll") { "Library" } else { "ConsoleApplication" })

$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(
    "$output.sha256",
    "$hash  $OutputName$([Environment]::NewLine)",
    [Text.UTF8Encoding]::new($false)
)
[pscustomobject]@{
    executable = $output
    sha256 = $hash
    sha256_file = "$output.sha256"
    source_files = $sources.Count
} | ConvertTo-Json -Depth 3
