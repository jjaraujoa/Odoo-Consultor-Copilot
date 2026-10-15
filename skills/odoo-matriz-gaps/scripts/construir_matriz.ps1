<#
.SYNOPSIS
    construir_matriz.ps1 - Compilador de Matriz de GAPs con Excel COM nativo (Estándar de Consultoría)
.DESCRIPTION
    Toma un archivo JSON estructurado y genera el libro Excel (.xlsx) utilizando
    el motor oficial de Microsoft Excel para garantizar:
    - Casillas de verificación nativas interactivas (Excel 365) en G:K (sin degrado a texto)
    - Casillas nativas vinculadas por fórmula en el Consolidado
    - Preservación perfecta de estilos, estructura y fórmulas
    - Ajuste automático de alturas de fila
#>

[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)]
    [string]$InputJson,

    [Parameter(Mandatory = $false)]
    [string]$TemplatePath,

    [Parameter(Mandatory = $false)]
    [string]$OutputPath
)

[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

if (-not (Test-Path $InputJson)) {
    Write-Error "No se encuentra el archivo JSON de entrada: $InputJson"
    exit 1
}

$data = Get-Content -Raw -Encoding UTF8 $InputJson | ConvertFrom-Json

$cliente = if ($data.cliente) { [string]$data.cliente } else { "CLIENTE" }
$fechaStr = if ($data.fecha) { [string]$data.fecha } else { (Get-Date).ToString("yyyy-MM-dd") }
$version = if ($data.version) { [string]$data.version } else { "1" }
$hojasData = $data.hojas

# Resolver plantilla base
if (-not $TemplatePath) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }
    $baseDir = Split-Path $scriptDir -Parent
    $TemplatePath = Join-Path $baseDir "assets\FormatoMatrizDeGaps-Estandar-V1.xlsx"
}

if (-not (Test-Path $TemplatePath)) {
    Write-Error "No se encontró la plantilla en: $TemplatePath"
    exit 1
}

# Resolver ruta de salida de forma segura
if (-not $OutputPath) {
    $fechaStamp = (Get-Date).ToString("yyyyMMdd")
    $cliClean = $cliente -replace '[^a-zA-Z0-9_-]', ''
    $filename = "FormatoMatrizDeGaps-$cliClean-$fechaStamp-V$version.xlsx"
    $outDir = Join-Path (Get-Location) "outputs"
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
    $OutputPath = Join-Path $outDir $filename
} else {
    $outDir = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($OutputPath))
    if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
}

$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$TemplatePath = [System.IO.Path]::GetFullPath($TemplatePath)

Write-Host "Iniciando compilación nativa de Matriz de GAPs con Excel..."
Write-Host "  Plantilla: $TemplatePath"
Write-Host "  Salida   : $OutputPath"

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false

try {
    $wb = $excel.Workbooks.Open($TemplatePath)

    $consecutivoGlobal = 1
    $consolidadoRow = 5
    $consolidadoWs = $null

    try {
        $consolidadoWs = $wb.Sheets.Item("Consolidado")
    } catch {}

    # Limpiar filas existentes en Consolidado y hojas base para eliminar ejemplos ('EJM')
    $hojasBaseLimpiar = @("Contabilidad", "Inventarios", "Inventario", "CRM", "Ventas", "Compras", "Consolidado")
    foreach ($hBase in $hojasBaseLimpiar) {
        try {
            $wsB = $wb.Sheets.Item($hBase)
            $usedB = $wsB.UsedRange.Rows.Count
            if ($usedB -ge 5) {
                $wsB.Range("A5:P$($usedB + 50)").ClearContents()
            }
            $wsB.Range("P2").Value = $version
            $wsB.Range("P3").Value = $fechaStr
        } catch {}
    }

    $nombresHojas = $hojasData | Get-Member -MemberType NoteProperty | Select-Object -ExpandProperty Name

    foreach ($sname in $nombresHojas) {
        if ($sname -eq "Consolidado") { continue }

        $targetSheetName = $sname
        $ws = $null
        try {
            $ws = $wb.Sheets.Item($targetSheetName)
        } catch {
            if ($targetSheetName -eq "Inventario") {
                try {
                    $ws = $wb.Sheets.Item("Inventarios")
                    $targetSheetName = "Inventarios"
                } catch {}
            } elseif ($targetSheetName -eq "Inventarios") {
                try {
                    $ws = $wb.Sheets.Item("Inventario")
                    $targetSheetName = "Inventario"
                } catch {}
            }
        }

        if (-not $ws) {
            $refSheet = $wb.Sheets.Item(1)
            $ws = $wb.Worksheets.Add([System.Type]::Missing, $refSheet)
            $ws.Name = $targetSheetName
        }

        # Actualizar metadatos
        $ws.Range("P2").Value = $version
        $ws.Range("P3").Value = $fechaStr

        # Limpiar filas previas
        $usedRows = $ws.UsedRange.Rows.Count
        if ($usedRows -ge 5) {
            $ws.Range("A5:P$($usedRows + 20)").ClearContents()
        }

        $reqs = $hojasData.$sname
        $currentRow = 5

        for ($i = 0; $i -lt $reqs.Count; $i++) {
            $req = $reqs[$i]
            $r = $currentRow

            $reqNombre = [string]$req.requerimiento
            $reqCia = if ($req.compania) { [string]$req.compania } else { $cliente }
            $reqFecha = if ($req.fecha) { [string]$req.fecha } else { $fechaStr }
            $reqModulo = if ($req.odoo_modulo) { [string]$req.odoo_modulo } else { $targetSheetName }
            $reqF = [double]$req.porcentaje_ajuste_nativo

            $reqG = [bool]$req.localizacion_regional
            $reqH = [bool]$req.desarrollo
            $reqI = [bool]$req.studio
            $reqJ = [bool]$req.permisos
            $reqK = [bool]$req.accion_automatica

            $reqL = [string]$req.como_cumple_odoo
            $reqM = [string]$req.observaciones
            $reqN = if ($reqF -ge 1.0) { "No" } else { "Si" }
            $reqO = if ($req.prioridad) { [string]$req.prioridad } else { "Media" }
            $reqP = if ($reqH) { [string]$req.observaciones_desarrollo } else { "" }

            # Coherencia lógica obligatoria
            if ($reqF -ge 1.0) {
                $reqG = $reqH = $reqI = $reqJ = $reqK = $false
                $reqN = "No"
                $reqP = ""
            } else {
                $reqN = "Si"
                if (-not ($reqG -or $reqH -or $reqI -or $reqJ -or $reqK)) {
                    $reqI = $true # Default Studio
                }
            }

            if (-not $reqH) { $reqP = "" }

            # Si r > 5, copiar formatos de fila 5 para casillas y estilos
            if ($r -gt 5) {
                $ws.Range("A5:P5").Copy() | Out-Null
                $ws.Range("A$r:P$r").PasteSpecial(-4122) | Out-Null # xlPasteFormats
            }

            # Asignar valores compatibles con Excel COM Interop
            $ws.Range("A$r").Value2 = [string]($i + 1)
            $ws.Range("B$r").Value2 = [string]$reqNombre
            $ws.Range("C$r").Value2 = [string]$reqCia
            $ws.Range("D$r").Value2 = [string]$reqFecha
            $ws.Range("E$r").Value2 = [string]$reqModulo
            $ws.Range("F$r").Value2 = "$([Math]::Round($reqF * 100))%"

            # Casillas interactivas nativas
            $ws.Range("G$r").Value2 = [bool]$reqG
            $ws.Range("H$r").Value2 = [bool]$reqH
            $ws.Range("I$r").Value2 = [bool]$reqI
            $ws.Range("J$r").Value2 = [bool]$reqJ
            $ws.Range("K$r").Value2 = [bool]$reqK

            $ws.Range("L$r").Value2 = [string]$reqL
            $ws.Range("M$r").Value2 = [string]$reqM
            $ws.Range("N$r").Value2 = [string]$reqN
            $ws.Range("O$r").Value2 = [string]$reqO
            $ws.Range("P$r").Value2 = [string]$reqP

            # Calcular altura de fila proporcional
            $maxLen = [Math]::Max([Math]::Max($reqL.Length, $reqM.Length), $reqP.Length)
            $saltos = [Math]::Max(($reqL -split "`n").Count, ($reqM -split "`n").Count)
            $lineas = [Math]::Max([int]($maxLen / 40) + 1, $saltos + 1)
            $altura = [Math]::Max(45.0, [Math]::Min(350.0, $lineas * 18.0))
            $ws.Range("A$r").EntireRow.RowHeight = $altura

            # Replicar en Consolidado mediante fórmulas dinámicas y casillas interactivas
            if ($consolidadoWs) {
                $cr = $consolidadoRow

                if ($cr -gt 5) {
                    $consolidadoWs.Range("A5:P5").Copy() | Out-Null
                    $consolidadoWs.Range("A$cr:P$cr").PasteSpecial(-4122) | Out-Null
                }

                $consolidadoWs.Range("A$cr").Value = [string]$consecutivoGlobal
                $consolidadoWs.Range("B$cr").Formula = "='$targetSheetName'!B$r"
                $consolidadoWs.Range("C$cr").Formula = "='$targetSheetName'!C$r"
                $consolidadoWs.Range("D$cr").Formula = "='$targetSheetName'!D$r"
                $consolidadoWs.Range("E$cr").Formula = "='$targetSheetName'!E$r"
                $consolidadoWs.Range("F$cr").Formula = "='$targetSheetName'!F$r"
                $consolidadoWs.Range("F$cr").NumberFormat = "0%"

                # Casillas nativas en Consolidado vinculadas por fórmula
                $ws.Range("G$r:K$r").Copy() | Out-Null
                $consolidadoWs.Range("G$cr:K$cr").PasteSpecial(-4122) | Out-Null
                $consolidadoWs.Range("G$cr").Formula = "='$targetSheetName'!G$r"
                $consolidadoWs.Range("H$cr").Formula = "='$targetSheetName'!H$r"
                $consolidadoWs.Range("I$cr").Formula = "='$targetSheetName'!I$r"
                $consolidadoWs.Range("J$cr").Formula = "='$targetSheetName'!J$r"
                $consolidadoWs.Range("K$cr").Formula = "='$targetSheetName'!K$r"

                $consolidadoWs.Range("L$cr").Formula = "='$targetSheetName'!L$r"
                $consolidadoWs.Range("M$cr").Formula = "='$targetSheetName'!M$r"
                $consolidadoWs.Range("N$cr").Formula = "='$targetSheetName'!N$r"
                $consolidadoWs.Range("O$cr").Formula = "='$targetSheetName'!O$r"
                $consolidadoWs.Range("P$cr").Formula = "='$targetSheetName'!P$r"

                $consolidadoWs.Range("A$cr").EntireRow.RowHeight = $altura

                $consecutivoGlobal++
                $consolidadoRow++
            }

            $currentRow++
        }
    }

    # Guardar archivo compilado
    if (Test-Path $OutputPath) {
        Remove-Item -Path $OutputPath -Force
    }
    $wb.SaveAs($OutputPath)
    $wb.Close($false)

    Write-Host "✅ Matriz de GAPs compilada exitosamente con casillas interactivas!"
    Write-Host "  Ruta: $OutputPath"
    Write-Host "  Total Requerimientos: $($consecutivoGlobal - 1)"
} catch {
    Write-Error "ERROR al construir la matriz: $_ en linea $($_.InvocationInfo.ScriptLineNumber)"
    exit 1
} finally {
    $excel.Quit()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel) | Out-Null
    [System.GC]::Collect()
    [System.GC]::WaitForPendingFinalizers()
}
