[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$AssemblyPath,
    [Parameter(ValueFromRemainingArguments = $true, Position = 1)]
    [string[]]$CommandArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$assembly = (Resolve-Path -LiteralPath $AssemblyPath).Path
if ([IO.Path]::GetExtension($assembly) -ne ".dll") {
    throw "AssemblyPath debe ser una biblioteca DLL."
}
[void][Reflection.Assembly]::LoadFrom($assembly)
$exitCode = [OdooConsultor.Program]::Main($CommandArgs)
exit $exitCode
