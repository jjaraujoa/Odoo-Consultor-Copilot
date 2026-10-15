<#
.SYNOPSIS
    validar_matriz.ps1 - Auditor y Validador de Calidad para la Matriz de GAPs de Odoo
.DESCRIPTION
    Verifica exhaustivamente el libro Excel generado contra las reglas de consistencia:
    1. Catálogo estricto de Odoo Módulo (Columna E) - Módulos oficiales y rutas reales.
    2. Coherencia F vs G:K vs N vs P.
    3. Estructura de 2 párrafos y 'NO CUBRE' en Columna L cuando F < 100%.
    4. Formato de Columna M: 'FUENTE DE NEGOCIO:' obligatorio al 100%; 'OBSERVACIÓN:' opcional (sin 'Ninguna').
    5. Fórmulas dinámicas vinculadas y casillas en el Consolidado.
    6. Consecutivos y conteos reconciliados.
    7. Auditoría de cobertura de fuentes al 100% (-KnowledgeDir): verifica que cada HU, pregunta de
       checklist y solicitud de reporte esté citada en la Columna M.
#>

[CmdletBinding()]
param (
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$ExcelPath,

    [Parameter(Mandatory = $false)]
    [string]$KnowledgeDir
)

[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

if (-not (Test-Path $ExcelPath)) {
    Write-Error "No existe el archivo Excel: $ExcelPath"
    exit 1
}

$ExcelPath = [System.IO.Path]::GetFullPath($ExcelPath)

# Catálogo oficial de módulos base de Odoo
$modulosOficialesOdoo = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$listaModulos = @(
    "CRM", "Ventas", "Compras", "Inventario", "Inventarios", "Contabilidad", "Facturación",
    "Facturacion", "Fabricación", "Fabricacion", "MRP", "Punto de Venta", "PoS", "TPV",
    "Proyectos", "Proyecto", "Partes de Horas", "Servicio de Asistencia", "Helpdesk",
    "Reparaciones", "Calidad", "Mantenimiento", "Suscripciones", "Sitio Web",
    "Comercio Electrónico", "Comercio Electronico", "eCommerce", "Empleados",
    "Reclutamiento", "Selección", "Seleccion", "Ausencias", "Gastos", "Flota",
    "Documentos", "Firma", "Contactos", "Tableros", "Hojas de Cálculo", "Hojas de Calculo",
    "Ajustes", "Configuración", "Configuracion", "Studio", "Localización Regional",
    "Localizacion Regional", "Localización / Terceros", "Localizacion / Terceros", "Localización", "Localizacion", "Facturación Electrónica", "Facturacion Electronica"
)
foreach ($m in $listaModulos) { $null = $modulosOficialesOdoo.Add($m) }

# Submódulos o pestañas oficiales frecuentes de Odoo
$submodulosOficiales = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$listaSub = @(
    "Iniciativas", "Oportunidades", "Leads", "Pipeline", "Equipos de Ventas",
    "Presupuestos", "Pedidos", "Cotizaciones", "Tarifas", "Clientes", "Proveedores",
    "Solicitudes de Presupuesto", "Pedidos de Compra", "Acuerdos de Compra",
    "Operaciones", "Albaranes", "Recepciones", "Transferencias", "Ajustes de Inventario",
    "Productos", "Reglas de Abastecimiento", "Rutas", "Almacenes y Ubicaciones", "Desechos", "Lotes", "Series",
    "Facturas de Cliente", "Facturas de Proveedor", "Asientos Contables", "Conciliación Bancaria", "Conciliacion Bancaria",
    "Bancos", "Informes", "Informes Financieros", "Plan de Cuentas", "Activos", "Impuestos", "Centros de Costo", "Analítica", "Analitica",
    "Órdenes de Producción", "Ordenes de Produccion", "Listas de Materiales", "Centros de Trabajo", "Órdenes de Trabajo", "Ordenes de Trabajo",
    "Sesiones", "Tickets", "SLA", "Etapas", "Puntos de Control", "Alertas de Calidad",
    "Peticiones de Mantenimiento", "Equipamiento", "Planes", "Contratos", "Páginas", "Paginas", "Catálogo", "Catalogo",
    "Directorio", "Departamentos", "Puestos de Trabajo", "Solicitudes", "Peticiones",
    "Informes de Gastos", "Vehículos", "Vehiculos", "Servicios", "Espacios de Trabajo", "Plantillas",
    "Usuarios y Compañías", "Usuarios y Compañias", "Automatizaciones", "Acciones Planificadas", "Reglas de Acceso"
)
foreach ($s in $listaSub) { $null = $submodulosOficiales.Add($s) }

function Validar-ModuloOdoo {
    param ([string]$moduloTexto)
    if ([string]::IsNullOrWhiteSpace($moduloTexto)) { return $false }

    $partes = $moduloTexto -split '\s*/\s*'
    if ($partes.Count -eq 0) { return $false }

    $moduloPrincipal = $partes[0].Trim()
    if (-not $modulosOficialesOdoo.Contains($moduloPrincipal)) {
        return $false
    }

    if ($partes.Count -gt 1) {
        for ($i = 1; $i -lt $partes.Count; $i++) {
            $sub = $partes[$i].Trim()
            if (-not $modulosOficialesOdoo.Contains($sub) -and -not $submodulosOficiales.Contains($sub)) {
                $esValido = $false
                foreach ($valida in $submodulosOficiales) {
                    if ($sub -like "*$valida*") { $esValido = $true; break }
                }
                if (-not $esValido) { return $false }
            }
        }
    }
    return $true
}

$errores = [System.Collections.Generic.List[string]]::new()
$advertencias = [System.Collections.Generic.List[string]]::new()
$hojasAuditadas = @{}
$totalFuncionales = 0
$fuentesCitadasEnMatriz = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false

try {
    $wb = $excel.Workbooks.Open($ExcelPath)
    $sheetCount = $wb.Sheets.Count

    $hojasNombres = @()
    for ($i = 1; $i -le $sheetCount; $i++) {
        $hojasNombres += $wb.Sheets.Item($i).Name
    }

    $hojasFuncionales = $hojasNombres | Where-Object { $_ -ne "Consolidado" }

    foreach ($sname in $hojasFuncionales) {
        $ws = $wb.Sheets.Item($sname)
        $reqsHoja = 0
        $errHoja = 0
        $advHoja = 0

        $usedRows = $ws.UsedRange.Rows.Count
        for ($r = 5; $r -le [Math]::Max($usedRows, 5); $r++) {
            $valA = $ws.Range("A$r").Value2
            $valB = $ws.Range("B$r").Value2

            if ($null -eq $valA -and $null -eq $valB) { continue }
            if ([string]::IsNullOrWhiteSpace("$valA") -and [string]::IsNullOrWhiteSpace("$valB")) { continue }
            if ([string]::IsNullOrWhiteSpace("$valB")) { continue }

            $reqsHoja++
            $idxEsperado = $reqsHoja

            # 1. Consecutivo
            if ("$valA" -ne "$idxEsperado") {
                $advertencias.Add("[$sname] Fila $($r) - Consecutivo en Col A es '$valA', se esperaba $idxEsperado.")
                $advHoja++
            }

            # 2. Requerimiento (B)
            if ([string]::IsNullOrWhiteSpace("$valB")) {
                $errores.Add("[$sname] Fila $($r) - Columna B (Requerimiento) está vacía.")
                $errHoja++
            }

            # 3. Módulo Odoo (E) - Validación contra catálogo oficial
            $valE = [string]$ws.Range("E$r").Value2
            if ([string]::IsNullOrWhiteSpace($valE)) {
                $errores.Add("[$sname] Fila $($r) - Columna E (Odoo Módulo) está vacía.")
                $errHoja++
            } elseif (-not (Validar-ModuloOdoo $valE)) {
                $errores.Add("[$sname] Fila $($r) - Columna E ('$valE') no es un módulo o submódulo válido de Odoo. Debe ser una app oficial (ej. 'CRM', 'Ventas', 'Inventario') o ruta real (ej. 'CRM / Leads', 'Ventas / Tarifas').")
                $errHoja++
            }

            # 4. % Ajuste Nativo (F)
            $valF = $ws.Range("F$r").Value2
            $fFloat = 0.0
            try {
                $fFloat = [double]$valF
            } catch {
                $errores.Add("[$sname] Fila $($r) - Columna F (% Ajuste Nativo) no es numérico: '$valF'.")
                $errHoja++
            }

            # Alternativas G..K
            $valG = [bool]$ws.Range("G$r").Value2
            $valH = [bool]$ws.Range("H$r").Value2
            $valI = [bool]$ws.Range("I$r").Value2
            $valJ = [bool]$ws.Range("J$r").Value2
            $valK = [bool]$ws.Range("K$r").Value2

            $valL = [string]$ws.Range("L$r").Value2
            $valM = [string]$ws.Range("M$r").Value2
            $valN = [string]$ws.Range("N$r").Value2
            $valP = [string]$ws.Range("P$r").Value2

            # Coherencia F vs Alternativas
            if ($fFloat -ge 0.999) {
                # 100% nativo
                if ($valG -or $valH -or $valI -or $valJ -or $valK) {
                    $errores.Add("[$sname] Fila $($r) ('$valB') - F es 100% pero tiene alternativas marcadas en G:K.")
                    $errHoja++
                }
                if ($valN.ToLower() -notin @("no", "false")) {
                    $errores.Add("[$sname] Fila $($r) - F es 100% pero Columna N no es 'No' (valor actual: '$valN').")
                    $errHoja++
                }
                if (-not [string]::IsNullOrWhiteSpace($valP)) {
                    $advertencias.Add("[$sname] Fila $($r) - F es 100% pero Columna P tiene observaciones de desarrollo.")
                    $advHoja++
                }
            } else {
                # < 100% brecha
                if (-not ($valG -or $valH -or $valI -or $valJ -or $valK)) {
                    $errores.Add("[$sname] Fila $($r) ('$valB') - F es menor al 100% ($([Math]::Round($fFloat*100))%) pero no tiene ninguna alternativa marcada en G:K.")
                    $errHoja++
                }
                if ($valN.ToLower() -notin @("si", "sí", "true")) {
                    $errores.Add("[$sname] Fila $($r) - F es menor al 100% pero Columna N no es 'Si' (valor actual: '$valN').")
                    $errHoja++
                }
                if ($valL -notmatch '(?i)no cubre') {
                    $advertencias.Add("[$sname] Fila $($r) - F es menor al 100% pero Columna L no contiene la frase estándar 'NO CUBRE'.")
                    $advHoja++
                }
            }

            # Desarrollo (H) vs Col P
            if ($valH -and [string]::IsNullOrWhiteSpace($valP)) {
                $errores.Add("[$sname] Fila $($r) ('$valB') - Columna H (Desarrollo) está activa pero Columna P está vacía.")
                $errHoja++
            } elseif (-not $valH -and -not [string]::IsNullOrWhiteSpace($valP)) {
                $advertencias.Add("[$sname] Fila $($r) - Columna H está inactiva pero Columna P tiene texto.")
                $advHoja++
            }

            # Trazabilidad y Formato en M
            if ($valM -match '(?i)https?://|odoo\.com') {
                $errores.Add("[$sname] Fila $($r) - Columna M contiene enlaces web de Odoo (prohibido por estándar corporativo).")
                $errHoja++
            }

            if ($valM -match '(?i)observaci[oó]n:\s*(ninguna|n/a|no aplica|-)') {
                $advertencias.Add("[$sname] Fila $($r) - Columna M contiene 'OBSERVACIÓN: Ninguna/N/A'. Debe omitirse la palabra OBSERVACIÓN si no hay comentarios.")
                $advHoja++
            }

            if ($valM -notmatch '(?i)fuente de negocio:') {
                $errores.Add("[$sname] Fila $($r) - Columna M no contiene 'FUENTE DE NEGOCIO:' obligatorio.")
                $errHoja++
            } else {
                # Extraer IDs citados
                $matchesIds = [regex]::Matches($valM, '(?i)(HU-[A-Z0-9]+-\d+|REP-[A-Z0-9]+-\d+|[A-Z]{2,5}-\d{2,4})')
                foreach ($mId in $matchesIds) {
                    $null = $fuentesCitadasEnMatriz.Add($mId.Value.ToUpper())
                }
            }
        }

        $hojasAuditadas[$sname] = @{
            Requerimientos = $reqsHoja
            Errores = $errHoja
            Advertencias = $advHoja
        }
        $totalFuncionales += $reqsHoja
    }

    # Auditoría del Consolidado
    $reqsConsolidado = 0
    try {
        $wsCons = $wb.Sheets.Item("Consolidado")
        $usedCons = $wsCons.UsedRange.Rows.Count
        for ($cr = 5; $cr -le [Math]::Max($usedCons, 5); $cr++) {
            $valA = $wsCons.Range("A$cr").Value2
            $valB = $wsCons.Range("B$cr").Value2

            if ($null -eq $valA -and $null -eq $valB) { continue }
            if ([string]::IsNullOrWhiteSpace("$valA") -and [string]::IsNullOrWhiteSpace("$valB")) { continue }

            $reqsConsolidado++

            # Verificar fórmula en B
            $fmlaB = [string]$wsCons.Range("B$cr").Formula
            if (-not $fmlaB.StartsWith("=")) {
                $errores.Add("[Consolidado] Fila $($cr) - Columna B no es una fórmula vinculada (valor: '$fmlaB').")
            } elseif ($fmlaB -match '#REF!|#VALUE!|#NAME\?') {
                $errores.Add("[Consolidado] Fila $($cr) - Fórmula rota en Columna B ('$fmlaB').")
            }

            # Verificar fórmulas de casillas en G..K
            $colsLetras = @("G", "H", "I", "J", "K")
            foreach ($colL in $colsLetras) {
                $fmlaChk = [string]$wsCons.Range("$colL$cr").Formula
                if (-not $fmlaChk.StartsWith("=")) {
                    $errores.Add("[Consolidado] Fila $($cr), Col $colL - Casilla no está vinculada por fórmula ('$fmlaChk').")
                }
            }
        }

        if ($reqsConsolidado -ne $totalFuncionales) {
            $errores.Add("[Consolidado] Total de filas en Consolidado ($reqsConsolidado) no coincide con la suma de las hojas funcionales ($totalFuncionales).")
        }
    } catch {
        $errores.Add("No se encontró o no se pudo auditar la hoja obligatoria 'Consolidado'.")
    }

    $wb.Close($false)
} finally {
    $excel.Quit()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel) | Out-Null
    [System.GC]::Collect()
    [System.GC]::WaitForPendingFinalizers()
}

# Auditoría de Cobertura de Fuentes (-KnowledgeDir)
$fuentesEsperadas = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$fuentesFaltantes = [System.Collections.Generic.List[string]]::new()
$coberturaAuditada = $false

if ($KnowledgeDir -and (Test-Path $KnowledgeDir)) {
    $coberturaAuditada = $true
    Get-ChildItem -Path $KnowledgeDir -Recurse -Filter "*.md" | ForEach-Object {
        $txt = Get-Content -Raw -Encoding UTF8 $_.FullName

        # HUs
        $mHus = [regex]::Matches($txt, '##\s+(HU-[A-Z0-9]+-\d+)')
        foreach ($m in $mHus) { $null = $fuentesEsperadas.Add($m.Groups[1].Value.ToUpper()) }

        # Checklist
        $mChks = [regex]::Matches($txt, '##\s+([A-Z]{2,5}-\d{2,4})')
        foreach ($m in $mChks) {
            $val = $m.Groups[1].Value.ToUpper()
            if (-not $val.StartsWith("HU-") -and -not $val.StartsWith("REP-")) {
                $null = $fuentesEsperadas.Add($val)
            }
        }

        # Reportes
        $mReps = [regex]::Matches($txt, 'id:\s*(REP-[A-Z0-9]+-\d+)')
        foreach ($m in $mReps) { $null = $fuentesEsperadas.Add($m.Groups[1].Value.ToUpper()) }
    }

    foreach ($fId in $fuentesEsperadas) {
        if (-not $fuentesCitadasEnMatriz.Contains($fId)) {
            $fuentesFaltantes.Add($fId)
        }
    }

    if ($fuentesFaltantes.Count -gt 0) {
        $pctCob = [Math]::Round((($fuentesEsperadas.Count - $fuentesFaltantes.Count) / $fuentesEsperadas.Count) * 100, 1)
        $muestraFaltantes = ($fuentesFaltantes | Select-Object -First 10) -join ", "
        $extraStr = if ($fuentesFaltantes.Count -gt 10) { " ... y $($fuentesFaltantes.Count - 10) más" } else { "" }
        $errores.Add("COBERTURA DE FUENTES INCOMPLETA: Hay $($fuentesFaltantes.Count) requerimientos/preguntas de negocio sin mapear en la Matriz (Cobertura: $pctCob%). Faltantes: $muestraFaltantes$extraStr.")
    }
}

# Imprimir reporte
$esValido = ($errores.Count -eq 0)

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host "REPORTE DE AUDITORÍA: MATRIZ DE GAPS ODOO" -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host "Archivo                 : $ExcelPath"
if ($esValido) {
    Write-Host "Estado General          : ✅ VÁLIDO (0 Errores Bloqueantes)" -ForegroundColor Green
} else {
    Write-Host "Estado General          : ❌ INVÁLIDO ($($errores.Count) Errores Bloqueantes)" -ForegroundColor Red
}
Write-Host "Requerimientos Totales  : $totalFuncionales"
Write-Host "Filas en Consolidado    : $reqsConsolidado"

if ($coberturaAuditada) {
    $pctCobFinal = if ($fuentesEsperadas.Count -gt 0) { [Math]::Round((($fuentesEsperadas.Count - $fuentesFaltantes.Count) / $fuentesEsperadas.Count) * 100, 1) } else { 100.0 }
    Write-Host "Fuentes de Negocio Totales: $($fuentesEsperadas.Count)"
    Write-Host "Fuentes Cubiertas en Matriz: $($fuentesCitadasEnMatriz.Count)"
    if ($fuentesFaltantes.Count -eq 0) {
        Write-Host "Cobertura de Fuentes    : ✅ 100% ($($fuentesEsperadas.Count)/$($fuentesEsperadas.Count) mapeadas)" -ForegroundColor Green
    } else {
        Write-Host "Cobertura de Fuentes    : ❌ $pctCobFinal% ($($fuentesFaltantes.Count) faltantes)" -ForegroundColor Red
    }
}

Write-Host "`nDesglose por Hoja Funcional:"
foreach ($kv in $hojasAuditadas.GetEnumerator()) {
    $h = $kv.Key
    $d = $kv.Value
    Write-Host ("  - {0,-15}: {1,3} reqs | {2} errores | {3} advertencias" -f $h, $d.Requerimientos, $d.Errores, $d.Advertencias)
}

if ($errores.Count -gt 0) {
    Write-Host "`n❌ ERRORES BLOQUEANTES ($($errores.Count)):" -ForegroundColor Red
    foreach ($err in $errores) {
        Write-Host "  • $err" -ForegroundColor Red
    }
}

if ($advertencias.Count -gt 0) {
    Write-Host "`n⚠️ ADVERTENCIAS ($($advertencias.Count)):" -ForegroundColor Yellow
    $muestraAdv = $advertencias | Select-Object -First 15
    foreach ($adv in $muestraAdv) {
        Write-Host "  • $adv" -ForegroundColor Yellow
    }
    if ($advertencias.Count -gt 15) {
        Write-Host "  ... y $($advertencias.Count - 15) advertencias más." -ForegroundColor Yellow
    }
}

Write-Host "=================================================================" -ForegroundColor Cyan

if (-not $esValido) { exit 1 } else { exit 0 }
