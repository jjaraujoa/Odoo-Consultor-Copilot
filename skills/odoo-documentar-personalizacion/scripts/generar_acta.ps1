<#
.SYNOPSIS
    Lanzador PowerShell para generar el Acta de Entrega / Documentación en Word (.docx).
.DESCRIPTION
    Detecta el entorno Python adecuado (con python-docx preinstalado) y ejecuta generar_acta_word.py
    con codificación UTF-8 sin BOM para evitar mojibake y caracteres corruptos.
.PARAMETER JsonPath
    Ruta absoluta o relativa al archivo JSON con los datos estructurados.
.PARAMETER OutputPath
    Ruta absoluta o relativa donde se guardará el archivo .docx generado.
.PARAMETER TemplatePath
    (Opcional) Ruta a una plantilla Word alternativa. Si se omite, usa resources/plantilla_acta_entrega_estandar.docx.
#>
param (
    [Parameter(Mandatory=$true)]
    [string]$JsonPath,

    [Parameter(Mandatory=$true)]
    [string]$OutputPath,

    [Parameter(Mandatory=$false)]
    [string]$TemplatePath,

    [Parameter(Mandatory=$false)]
    [string]$MarkdownPath
)

[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::InputEncoding  = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding           = [System.Text.UTF8Encoding]::new($false)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$pythonScript = Join-Path $scriptDir "generar_acta_word.py"

# Buscar binario de Python con python-docx
$userProfile = if ($env:USERPROFILE) { $env:USERPROFILE } else { $HOME }
$candidatePythons = @(
    (Join-Path $userProfile ".cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe"),
    (Get-Command python -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
    (Get-Command python3 -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
    (Get-Command py -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)
)

$selectedPython = $null
foreach ($py in $candidatePythons) {
    if ($py -and (Test-Path $py)) {
        & $py -c "import docx" 2>$null
        if ($LASTEXITCODE -eq 0) {
            $selectedPython = $py
            break
        }
    }
}

if (-not $selectedPython) {
    Write-Error "No se encontró un intérprete de Python con la librería 'python-docx' disponible."
    exit 1
}

$cmdArgs = @(
    "`"$pythonScript`"",
    "--json", "`"$JsonPath`"",
    "--output", "`"$OutputPath`""
)

if ($TemplatePath) {
    $cmdArgs += @("--template", "`"$TemplatePath`"")
}

if ($MarkdownPath) {
    $cmdArgs += @("--markdown", "`"$MarkdownPath`"")
}

& $selectedPython @cmdArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "Falló la generación del documento Word con código de salida: $LASTEXITCODE"
    exit $LASTEXITCODE
}
