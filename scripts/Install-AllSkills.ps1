[CmdletBinding()]
param(
    [string]$DestinationRoot = (Join-Path $env:USERPROFILE ".gemini\config\skills")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$skillsRoot = (Resolve-Path (Join-Path $repositoryRoot "skills")).Path

$skills = Get-ChildItem -LiteralPath $skillsRoot -Directory

$results = foreach ($skillDir in $skills) {
    $skillName = $skillDir.Name
    $source = $skillDir.FullName
    $destinationRootFull = [IO.Path]::GetFullPath($DestinationRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $target = [IO.Path]::GetFullPath((Join-Path $destinationRootFull $skillName))

    # Asegurar directorio destino
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -Path (Join-Path $source "*") -Destination $target -Recurse -Force

    # También instalar en .codex\skills si existe
    $codexSkills = Join-Path $env:USERPROFILE ".codex\skills"
    if (Test-Path -LiteralPath $codexSkills) {
        $codexTarget = Join-Path $codexSkills $skillName
        New-Item -ItemType Directory -Path $codexTarget -Force | Out-Null
        Copy-Item -Path (Join-Path $source "*") -Destination $codexTarget -Recurse -Force
    }

    $files = Get-ChildItem -LiteralPath $target -File -Recurse
    [PSCustomObject]@{
        Skill     = $skillName
        Target    = $target
        FileCount = $files.Count
        Estado    = "Instalada OK"
    }
}

$results | Format-Table -AutoSize
