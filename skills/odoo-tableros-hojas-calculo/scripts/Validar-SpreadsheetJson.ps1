<#
.SYNOPSIS
    Validador estático de integridad para JSON de hojas de cálculo y tableros Odoo 18/19.

.DESCRIPTION
    Audita un archivo JSON de spreadsheet para asegurar el cumplimiento de las 4 reglas críticas:
    1. Enlaces odoo://view con estructura obligatoria action { xmlId, modelName, views, domain }.
    2. Dominios AST 2D [[campo, operador, valor]] sin aplanamiento.
    3. Ausencia de caracteres UTF-8 corruptos (Ã³, Ã­, etc.).
    4. Ausencia de nombres técnicos entre paréntesis en tablas y leyendas.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Error "No se encontró el archivo: $Path"
    exit 1
}

$rawJson = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)

$errors = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]

# 1. Verificación de caracteres UTF-8 corruptos
$brokenPatterns = @("Ã³", "Ã­", "Ã©", "Ã¡", "Ãº", "Ã±", "Ã“", "Ã", "Ã‰", "Ã", "Ãš", "Ã‘")
foreach ($pat in $brokenPatterns) {
    if ($rawJson.Contains($pat)) {
        $errors.Add("Caracteres corruptos detectados: se encontró la secuencia '$pat' en el JSON. Usa secuencias Unicode nativas (\u00f3, etc.).")
    }
}

# 2. Parseo de JSON
$json = $null
try {
    $json = $rawJson | ConvertFrom-Json
}
catch {
    Write-Error "El archivo no contiene un JSON válido: $_"
    exit 1
}

$linksChecked = 0
$figuresChecked = 0
$cellsChecked = 0

function Test-Domain2D([object]$domain, [string]$contextPath) {
    if ($null -eq $domain) { return }

    # Si es array plano o no es array
    if ($domain -is [string] -or $domain -is [int] -or $domain -is [bool]) {
        $errors.Add("[$contextPath] El dominio es un valor escalar, debe ser un arreglo 2D: [[campo, operador, valor]].")
        return
    }

    $domainArray = @($domain)
    if ($domainArray.Count -eq 0) { return }

    # Verificar si fue aplanado: ej. ["user_id", "=", 25] en lugar de [["user_id", "=", 25]]
    if ($domainArray.Count -eq 3 -and ($domainArray[0] -is [string]) -and ($domainArray[1] -is [string])) {
        $op = $domainArray[1]
        if ($op -match "^(=|!=|<|>|<=|>=|ilike|like|in|not in)$") {
            $errors.Add("[$contextPath] Dominio aplanado detectado: [$($domainArray -join ', ')]. Odoo fallará con InvalidDomainError (TypeError: elem.map is not a function at toAST). Debe ser [[$($domainArray -join ', ')]].")
            return
        }
    }

    # Verificar cada tupla
    for ($i = 0; $i -lt $domainArray.Count; $i++) {
        $criterion = $domainArray[$i]
        $critArray = @($criterion)
        if ($critArray.Count -ne 3) {
            $errors.Add("[$contextPath] Criterio #$i no tiene exactamente 3 elementos [campo, operador, valor]: ($($critArray -join ', ')).")
        }
    }
}

# 3. Inspección de Hojas y Celdas
if ($json.sheets) {
    foreach ($sheet in $json.sheets) {
        $sheetName = $sheet.name
        if ($sheet.cells) {
            $pNames = $sheet.cells | Get-Member -MemberType NoteProperty | Select-Object -ExpandProperty Name
            foreach ($cellCoord in $pNames) {
                $cellsChecked++
                $cellObj = $sheet.cells.$cellCoord
                if ($cellObj -and $cellObj.content) {
                    $content = [string]$cellObj.content

                    # Detección de nombres técnicos entre paréntesis
                    if ($content -match '\(([a-z_]{4,})\)') {
                        $warnings.Add("[$sheetName : $cellCoord] Posible nombre técnico entre paréntesis: '$($Matches[0])'. Usa nombres amigables orientados al usuario final.")
                    }

                    # Detección de enlaces odoo://view
                    $viewIdx = $content.IndexOf('odoo://view/')
                    if ($viewIdx -ge 0) {
                        $linksChecked++
                        $payloadRaw = $content.Substring($viewIdx + 12)
                        if ($payloadRaw.EndsWith(')')) {
                            $payloadRaw = $payloadRaw.Substring(0, $payloadRaw.Length - 1)
                        }
                        if ($payloadRaw.EndsWith('>')) {
                            $payloadRaw = $payloadRaw.Substring(0, $payloadRaw.Length - 1)
                        }

                        try {
                            $decodedPayload = [System.Uri]::UnescapeDataString($payloadRaw)
                            $viewObj = $decodedPayload | ConvertFrom-Json

                            # Validar que exista el objeto action
                            if (-not ($viewObj.PSObject.Properties['action'] -and $viewObj.action)) {
                                $errors.Add("[$sheetName : $cellCoord] Enlace odoo://view sin objeto 'action'. Odoo fallara con: TypeError: Cannot read properties of undefined (reading 'xmlId').")
                            } else {
                                $act = $viewObj.action
                                if (-not $act.PSObject.Properties['xmlId'] -or [string]::IsNullOrWhiteSpace($act.xmlId)) {
                                    $errors.Add("[$sheetName : $cellCoord] El objeto action no contiene 'xmlId' valido.")
                                }
                                if (-not $act.PSObject.Properties['modelName'] -or [string]::IsNullOrWhiteSpace($act.modelName)) {
                                    $errors.Add("[$sheetName : $cellCoord] El objeto action no contiene 'modelName' valido.")
                                }
                                if ($act.PSObject.Properties['domain'] -and $act.domain) {
                                    Test-Domain2D $act.domain "$sheetName : $cellCoord : action.domain"
                                }
                            }
                        }
                        catch {
                            $errors.Add("[$sheetName : $cellCoord] Error al parsear JSON del enlace odoo://view: $_")
                        }
                    }
                }
            }
        }

        # 4. Inspeccion de Figuras y Graficos
        if ($sheet.PSObject.Properties['figures'] -and $sheet.figures) {
            foreach ($fig in $sheet.figures) {
                $figuresChecked++
                if ($fig.PSObject.Properties['data'] -and $fig.data) {
                    if ($fig.data.PSObject.Properties['searchParams'] -and $fig.data.searchParams) {
                        if ($fig.data.searchParams.PSObject.Properties['domain'] -and $fig.data.searchParams.domain) {
                            Test-Domain2D $fig.data.searchParams.domain "$sheetName : Figura $($fig.id) : searchParams.domain"
                        }
                    }
                }
            }
        }
    }
}

Write-Host '=============================================' -ForegroundColor Cyan
Write-Host ('Auditoria de Hoja de Calculo Odoo: ' + $Path) -ForegroundColor Cyan
Write-Host '=============================================' -ForegroundColor Cyan
Write-Host ('Celdas inspeccionadas: ' + $cellsChecked)
Write-Host ('Enlaces odoo://view validados: ' + $linksChecked)
Write-Host ('Figuras y graficos revisados: ' + $figuresChecked)

if ($warnings.Count -gt 0) {
    Write-Host ("`nAdvertencias (" + $warnings.Count + '):') -ForegroundColor Yellow
    foreach ($w in $warnings) {
        Write-Host ('  [WARN] ' + $w) -ForegroundColor Yellow
    }
}

if ($errors.Count -gt 0) {
    Write-Host ("`nErrores Criticos (" + $errors.Count + '):') -ForegroundColor Red
    foreach ($e in $errors) {
        Write-Host ('  [ERROR] ' + $e) -ForegroundColor Red
    }
    Write-Host "`nRESULTADO: FALLO LA AUDITORIA." -ForegroundColor Red
    exit 1
} else {
    Write-Host "`nRESULTADO: AUDITORIA APROBADA (0 errores criticos)." -ForegroundColor Green
    exit 0
}
