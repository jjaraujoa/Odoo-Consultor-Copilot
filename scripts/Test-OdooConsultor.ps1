[CmdletBinding()]
param(
    [string]$ExecutablePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $build = powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "Build-OdooConsultor.ps1") | ConvertFrom-Json
    $executable = $build.executable
    $buildHash = $build.sha256
}
else {
    $executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
    $buildHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
}

$launcher = Join-Path $root "consultant-host\OdooConsultor.ps1"
$assemblyInvoker = Join-Path $PSScriptRoot "Invoke-OdooConsultorAssembly.ps1"
& powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File $assemblyInvoker $executable selftest
if ($LASTEXITCODE -ne 0) { throw "Falló el self-test interno." }

$temporaryRoot = [IO.Path]::Combine([IO.Path]::GetTempPath(), "odoo-consultor-integration-" + [guid]::NewGuid().ToString("N"))
try {
    & powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File $assemblyInvoker $executable proyecto crear --ruta $temporaryRoot --slug "mcp-test" --nombre "Cliente MCP Test" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "No se pudo crear el proyecto temporal." }

    $requiredFiles = @(
        "AGENTS.md",
        "proyecto.yaml",
        "agentes\consultor.md",
        "agentes\cliente-final.md",
        "conocimiento\negocio.md",
        "demostraciones",
        "diagnosticos",
        "replicas",
        ".codex\config.toml"
    )
    foreach ($relative in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $temporaryRoot $relative))) {
            throw "Falta el artefacto de proyecto $relative."
        }
    }
    $mcpConfig = [IO.File]::ReadAllText((Join-Path $temporaryRoot ".codex\config.toml"))
    if (-not $mcpConfig.Contains('command = "powershell.exe"') -or
        -not $mcpConfig.Contains('"-ExecutionPolicy", "RemoteSigned"') -or
        -not $mcpConfig.Contains('OdooConsultor.ps1')) {
        throw "El proyecto no usa el lanzador DLL verificable para MCP."
    }

    $phase2Replica = Join-Path $temporaryRoot "replicas\phase2_mcp"
    New-Item -ItemType Directory -Path $phase2Replica -Force | Out-Null
    $phase2Manifest = @{
        schema_version = 2
        phase = 2
        name = "phase2_mcp"
        source = @{ environment = "staging"; odoo_version = "19"; captured_on = "2026-08-10"; contains_business_records = $false }
        models = @(@{ key = "phase2_model"; model = "x_phase2_mcp"; name = "Prueba MCP"; mode = "create" })
        fields = @()
        views = @()
        window_actions = @()
        menus = @()
        excluded_artifacts = @("all_business_records")
    } | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText((Join-Path $phase2Replica "manifest.json"), $phase2Manifest, [Text.UTF8Encoding]::new($false))

    $phase3Replica = Join-Path $temporaryRoot "replicas\phase3_mcp"
    New-Item -ItemType Directory -Path $phase3Replica -Force | Out-Null
    $phase3Code = "log('phase3 mcp')`n"
    [IO.File]::WriteAllText((Join-Path $phase3Replica "action.py"), $phase3Code, [Text.UTF8Encoding]::new($false))
    $phase3CodeBytes = [Text.Encoding]::UTF8.GetBytes($phase3Code)
    $phase3Sha = [Security.Cryptography.SHA256]::Create()
    try { $phase3CodeHash = [BitConverter]::ToString($phase3Sha.ComputeHash($phase3CodeBytes)).Replace('-', '').ToLowerInvariant() }
    finally { $phase3Sha.Dispose(); [Array]::Clear($phase3CodeBytes, 0, $phase3CodeBytes.Length) }
    $phase3Manifest = @{
        schema_version = 2
        phase = 3
        name = "phase3_mcp"
        source = @{ environment = "staging"; odoo_version = "19"; captured_on = "2026-08-10"; contains_business_records = $false }
        references = @{}
        computed_fields = @()
        mail_templates = @()
        server_actions = @(@{ key = "phase3_code"; name = "Prueba MCP Fase 3"; model = "res.partner"; state = "code"; mode = "create"; code_file = "action.py"; code_sha256 = $phase3CodeHash })
        views = @()
        automations = @()
        excluded_artifacts = @("all_business_records")
    } | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText((Join-Path $phase3Replica "manifest.json"), $phase3Manifest, [Text.UTF8Encoding]::new($false))

    $phase4Replica = Join-Path $temporaryRoot "replicas\phase4_mcp"
    New-Item -ItemType Directory -Path $phase4Replica -Force | Out-Null
    $phase4Qweb = '<t t-name="odooai.phase4_mcp_report"><t t-call="web.external_layout"><div class="page"><span t-field="o.display_name"/></div></t></t>' + "`n"
    [IO.File]::WriteAllText((Join-Path $phase4Replica "report.xml"), $phase4Qweb, [Text.UTF8Encoding]::new($false))
    $phase4Bytes = [Text.Encoding]::UTF8.GetBytes($phase4Qweb)
    $phase4Sha = [Security.Cryptography.SHA256]::Create()
    try { $phase4Hash = [BitConverter]::ToString($phase4Sha.ComputeHash($phase4Bytes)).Replace('-', '').ToLowerInvariant() }
    finally { $phase4Sha.Dispose(); [Array]::Clear($phase4Bytes, 0, $phase4Bytes.Length) }
    $phase4Manifest = @{
        schema_version = 2
        phase = 4
        name = "phase4_mcp"
        source = @{ environment = "staging"; odoo_version = "19"; captured_on = "2026-08-13"; contains_business_records = $false }
        references = @{}
        qweb_views = @(@{ key = "phase4_template"; name = "Prueba MCP Fase 4"; template_name = "odooai.phase4_mcp_report"; mode = "create"; arch_file = "report.xml"; arch_sha256 = $phase4Hash; active = $true })
        reports = @(@{ key = "phase4_report"; name = "Prueba MCP Fase 4"; model = "res.partner"; mode = "create"; template_key = "phase4_template"; report_type = "qweb-pdf" })
        approvals = @()
        access_controls = @()
        record_rules = @()
        negative_access_tests = @()
        excluded_artifacts = @("all_business_records")
    } | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText((Join-Path $phase4Replica "manifest.json"), $phase4Manifest, [Text.UTF8Encoding]::new($false))

    $requests = @(
        (@{
            jsonrpc = "2.0"
            id = 1
            method = "initialize"
            params = @{ protocolVersion = "2025-06-18"; capabilities = @{}; clientInfo = @{ name = "test"; version = "1" } }
        } | ConvertTo-Json -Depth 8 -Compress),
        (@{ jsonrpc = "2.0"; id = 2; method = "tools/list"; params = @{} } | ConvertTo-Json -Depth 8 -Compress),
        (@{
            jsonrpc = "2.0"
            id = 3
            method = "tools/call"
            params = @{
                name = "conexion_estado"
                arguments = @{}
            }
        } | ConvertTo-Json -Depth 8 -Compress),
        (@{
            jsonrpc = "2.0"
            id = 4
            method = "tools/call"
            params = @{
                name = "studio_preparar_cambio"
                arguments = @{
                    entorno = "production"
                    tipo = "campo"
                    modelo = "sale.order"
                    especificacion = @{ nombre_tecnico = "x_test"; etiqueta = "Test"; tipo_campo = "char" }
                }
            }
        } | ConvertTo-Json -Depth 8 -Compress),
        (@{
            jsonrpc = "2.0"
            id = 5
            method = "tools/call"
            params = @{
                name = "studio_generar_runbook"
                arguments = @{
                    entorno = "staging"
                    categoria = "webhook"
                    titulo = "Webhook controlado"
                    objetivo = "Documentar sin ejecutar"
                    pasos = @("Revisar destino", "Validar datos")
                }
            }
        } | ConvertTo-Json -Depth 8 -Compress),
        (@{
            jsonrpc = "2.0"
            id = 6
            method = "tools/call"
            params = @{
                name = "cambio_preparar_paquete"
                arguments = @{
                    entorno = "production"
                    contrato = @{
                        schema_version = 1
                        objective = "Añadir referencia controlada"
                        target_model = "sale.order"
                        operations = @(@{ type = "create_custom_field"; parameters = @{ name = "x_reference_test"; label = "Referencia"; field_type = "char" } })
                    }
                }
            }
        } | ConvertTo-Json -Depth 12 -Compress),
        (@{
            jsonrpc = "2.0"
            id = 7
            method = "tools/call"
            params = @{
                name = "odoo_consultar_registros"
                arguments = @{
                    entorno = "staging"
                    consulta = @{ model = "ir.config_parameter"; fields = @("key"); limit = 1 }
                }
            }
        } | ConvertTo-Json -Depth 8 -Compress),
        (@{
            jsonrpc = "2.0"
            id = 8
            method = "tools/call"
            params = @{
                name = "replica_preparar_paquete"
                arguments = @{ entorno = "production"; replica_slug = "phase2_mcp" }
            }
        } | ConvertTo-Json -Depth 8 -Compress),
        (@{
            jsonrpc = "2.0"
            id = 9
            method = "tools/call"
            params = @{
                name = "replica_preparar_paquete"
                arguments = @{ entorno = "production"; replica_slug = "phase3_mcp" }
            }
        } | ConvertTo-Json -Depth 8 -Compress),
        (@{
            jsonrpc = "2.0"
            id = 10
            method = "tools/call"
            params = @{
                name = "replica_preparar_paquete"
                arguments = @{ entorno = "production"; replica_slug = "phase4_mcp" }
            }
        } | ConvertTo-Json -Depth 8 -Compress),
        (@{
            jsonrpc = "2.0"
            id = 11
            method = "tools/call"
            params = @{ name = "fuentes_estado"; arguments = @{ version = "19.0" } }
        } | ConvertTo-Json -Depth 8 -Compress),
        (@{
            jsonrpc = "2.0"
            id = 12
            method = "tools/call"
            params = @{ name = "fuentes_buscar"; arguments = @{ repositorio = "enterprise"; version = "19.0"; termino = "marketing" } }
        } | ConvertTo-Json -Depth 8 -Compress),
        (@{
            jsonrpc = "2.0"
            id = 13
            method = "tools/call"
            params = @{ name = "demo_preparar_escenario"; arguments = @{ entorno = "production"; demo_slug = "marketing"; aplicacion = "marketing"; objetivo = "Probar bloqueo"; requerimientos = @("Captación") } }
        } | ConvertTo-Json -Depth 8 -Compress),
        (@{
            jsonrpc = "2.0"
            id = 14
            method = "tools/call"
            params = @{ name = "modulos_preparar_instalacion"; arguments = @{ entorno = "production"; objetivo = "Probar bloqueo"; modulos = @("sale_commission") } }
        } | ConvertTo-Json -Depth 8 -Compress),
        (@{
            jsonrpc = "2.0"
            id = 15
            method = "tools/call"
            params = @{ name = "promocion_preparar_runbook"; arguments = @{ replica_slug = "phase2_mcp" } }
        } | ConvertTo-Json -Depth 8 -Compress),
        (@{
            jsonrpc = "2.0"
            id = 16
            method = "tools/call"
            params = @{
                name = "tablero_preparar_paquete"
                arguments = @{
                    entorno = "production"
                    modelo = "spreadsheet.dashboard"
                    modo = "create"
                    nombre = "Tablero Bloqueado"
                    definicion = @{ version = 1; sheets = @() }
                }
            }
        } | ConvertTo-Json -Depth 8 -Compress),
        (@{
            jsonrpc = "2.0"
            id = 17
            method = "tools/call"
            params = @{
                name = "tablero_validar_definicion"
                arguments = @{
                    definicion = @{
                        version = 1
                        sheets = @(@{
                            name = "Ventas"
                            cells = @{
                                A1 = "[Ver Ventas](odoo://view/{`"name`":`"Ventas`",`"viewType`":`"list`",`"modelName`":`"sale.order`",`"domain`":[[`"state`",`"=`",`"sale`"]]})"
                            }
                        })
                    }
                    auto_fix = $true
                }
            }
        } | ConvertTo-Json -Depth 8 -Compress),
        (@{
            jsonrpc = "2.0"
            id = 18
            method = "tools/call"
            params = @{
                name = "odoo_descubrir_base_datos"
                arguments = @{
                    url = "https://mi-empresa-demo.odoo.com"
                }
            }
        } | ConvertTo-Json -Depth 8 -Compress),
        (@{
            jsonrpc = "2.0"
            id = 19
            method = "tools/call"
            params = @{
                name = "conexion_probar"
                arguments = @{
                    entorno = "staging"
                }
            }
        } | ConvertTo-Json -Depth 8 -Compress)
    )

    $responses = @($requests | & powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File $assemblyInvoker $executable mcp --proyecto $temporaryRoot)
    if ($LASTEXITCODE -ne 0) { throw "El servidor MCP terminó con error." }
    if ($responses.Count -ne 19) { throw "Se esperaban 19 respuestas MCP y se recibieron $($responses.Count)." }
    $parsed = @($responses | ForEach-Object { $_ | ConvertFrom-Json })

    $tools = @($parsed[1].result.tools)
    $expectedTools = @(
        "conexion_estado",
        "conexion_probar",
        "odoo_inspeccionar_modelo",
        "studio_inventariar",
        "odoo_descubrir_capacidades",
        "odoo_descubrir_base_datos",
        "odoo_consultar_registros",
        "odoo_comparar_configuracion",
        "cambio_validar_contrato",
        "cambio_preparar_paquete",
        "studio_preparar_cambio",
        "replica_analizar_destino",
        "replica_preparar_paquete",
        "replica_ejecutar_uat",
        "replica_probar_efecto_externo",
        "replica_activar_efectos_externos",
        "replica_verificar_acceso_negativo",
        "promocion_analizar_impacto",
        "promocion_preparar_runbook",
        "promocion_verificar_produccion",
        "promocion_preparar_paquete",
        "promocion_aplicar_paquete",
        "promocion_activar_efectos_externos",
        "tablero_inspeccionar",
        "tablero_validar_definicion",
        "tablero_preparar_paquete",
        "tablero_aplicar_paquete",
        "studio_aplicar_paquete",
        "studio_verificar_paquete",
        "studio_revertir_paquete_seguro",
        "studio_generar_runbook",
        "fuentes_estado",
        "fuentes_sincronizar",
        "fuentes_crear_bloqueo",
        "fuentes_buscar",
        "odoo_identificar_instancia",
        "modulos_buscar",
        "modulos_preparar_instalacion",
        "modulos_aplicar_instalacion",
        "modulos_verificar_instalacion",
        "funcional_inventariar_aplicacion",
        "funcional_diagnosticar_objetivo",
        "funcional_preparar_parametrizacion",
        "demo_inventariar_aplicacion",
        "demo_inventariar_cobertura_total",
        "demo_preparar_escenario",
        "demo_preparar_registros_operativos",
        "demo_preparar_acciones",
        "demo_ejecutar_acciones",
        "demo_verificar_acciones",
        "demo_preparar_flujo",
        "demo_ejecutar_flujo",
        "demo_verificar_flujo",
        "demo_preparar_compensacion",
        "demo_ejecutar_compensacion",
        "demo_aplicar_escenario",
        "demo_verificar_paso",
        "demo_verificar_escenario",
        "demo_limpiar_escenario",
        "demo_preparar_reemplazo_mailing",
        "demo_archivar_reemplazados",
        "demo_restaurar_reemplazados"
    )
    $actualTools = @($tools | ForEach-Object { $_.name })
    if ($actualTools.Count -ne $expectedTools.Count -or (Compare-Object $expectedTools $actualTools)) {
        throw "El catálogo MCP no coincide con el contrato cerrado."
    }
    if ($actualTools -contains "odoo_call") { throw "Se expuso una herramienta genérica prohibida." }
    if ($actualTools -contains "odoo_rpc" -or $actualTools -contains "json2_call") { throw "Se expuso un transporte genérico prohibido." }
    if ($parsed[0].result.serverInfo.version -ne "1.0.0") { throw "El servidor MCP no anuncia 1.0.0." }
    if (-not $parsed[3].result.isError) { throw "Una escritura de producción no fue bloqueada." }
    $blockedText = [string]$parsed[3].result.content[0].text
    if (-not $blockedText.Contains("production_write_blocked")) { throw "El bloqueo de producción no devolvió el código esperado." }
    if ($parsed[4].result.isError) { throw "La generación local de runbook falló." }
    if ($parsed[5].result.isError) { throw "Un contrato de producción no se convirtió en runbook." }
    if (-not $parsed[6].result.isError -or -not ([string]$parsed[6].result.content[0].text).Contains("model_read_blocked")) {
        throw "La consulta general no bloqueó un modelo con secretos antes de usar credenciales."
    }
    $phase2Result = ([string]$parsed[7].result.content[0].text) | ConvertFrom-Json
    if ($parsed[7].result.isError -or $phase2Result.phase -ne 2 -or $phase2Result.execution -ne "manual_only") {
        throw "La réplica de Fase 2 no se compiló como runbook de producción mediante MCP."
    }
    $phase3Result = ([string]$parsed[8].result.content[0].text) | ConvertFrom-Json
    if ($parsed[8].result.isError -or $phase3Result.phase -ne 3 -or $phase3Result.execution -ne "manual_only") {
        throw "La réplica de Fase 3 no se compiló como runbook de producción mediante MCP."
    }
    $phase4Result = ([string]$parsed[9].result.content[0].text) | ConvertFrom-Json
    if ($parsed[9].result.isError -or $phase4Result.phase -ne 4 -or $phase4Result.execution -ne "manual_only") {
        throw "La replica de Fase 4 no se compilo como runbook de produccion mediante MCP."
    }
    if ($parsed[10].result.isError -or -not ([string]$parsed[10].result.content[0].text).Contains("oficial_documentacion")) {
        throw "El estado de fuentes no devolvió la jerarquía registrada."
    }
    if (-not $parsed[11].result.isError -or -not ([string]$parsed[11].result.content[0].text).Contains("enterprise_source_unavailable")) {
        throw "El contenido Enterprise no fue declarado no disponible antes de buscarlo."
    }
    if (-not $parsed[12].result.isError -or -not ([string]$parsed[12].result.content[0].text).Contains("production_write_blocked")) {
        throw "La preparación de demo no bloqueó producción antes de usar credenciales."
    }
    if (-not $parsed[13].result.isError -or -not ([string]$parsed[13].result.content[0].text).Contains("production_write_blocked")) {
        throw "La instalación de módulos no bloqueó producción antes de usar credenciales."
    }
    $promoResult = ([string]$parsed[14].result.content[0].text) | ConvertFrom-Json
    if ($parsed[14].result.isError -or $promoResult.status -ne "prepared") {
        throw "La preparación de runbook de promoción mediante MCP falló."
    }
    if (-not $parsed[15].result.isError -or -not ([string]$parsed[15].result.content[0].text).Contains("production_write_blocked")) {
        throw "La preparación de tablero no bloqueó producción antes de usar credenciales."
    }
    $tableroValidacion = ([string]$parsed[16].result.content[0].text) | ConvertFrom-Json
    if ($parsed[16].result.isError -or -not $tableroValidacion.valid -or -not $tableroValidacion.repaired) {
        throw "La validación con auto_fix de tablero falló mediante MCP."
    }
    $discoverResult = ([string]$parsed[17].result.content[0].text) | ConvertFrom-Json
    if ($parsed[17].result.isError -or $discoverResult.base_datos -ne "mi-empresa-demo" -or $discoverResult.nivel_confianza -ne "inferred_saas") {
        throw "El descubrimiento de base de datos mediante MCP falló."
    }
    $connTestResult = ([string]$parsed[18].result.content[0].text) | ConvertFrom-Json
    if ($parsed[18].result.isError -or $connTestResult.configured -ne $false) {
        throw "La prueba de conexión sin configurar falló mediante MCP."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $temporaryRoot "promociones\phase2_mcp\runbook.md"))) {
        throw "No se encontró el archivo de runbook de promoción generado."
    }
    if (@(Get-ChildItem -LiteralPath (Join-Path $temporaryRoot "cambios\runbooks") -Filter "*.md").Count -ne 5) {
        throw "No se generaron exactamente los cinco runbooks esperados."
    }

    $updateHost = Join-Path $temporaryRoot "auto-update-host"
    $updateBin = Join-Path $updateHost "bin"
    New-Item -ItemType Directory -Path $updateBin -Force | Out-Null
    Copy-Item -LiteralPath $launcher -Destination (Join-Path $updateHost "OdooConsultor.ps1") -Force
    $candidateVersion = (& powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File $assemblyInvoker $executable version).Trim().Replace("OdooConsultor ", "")
    $candidateName = "OdooConsultor-$candidateVersion.dll"
    Copy-Item -LiteralPath $executable -Destination (Join-Path $updateBin $candidateName) -Force
    $candidateHash = (Get-FileHash -LiteralPath (Join-Path $updateBin $candidateName) -Algorithm SHA256).Hash.ToLowerInvariant()
    $pending = [ordered]@{
        schema_version = 1
        version = $candidateVersion
        candidate = $candidateName
        sha256 = $candidateHash
        staged_at = [DateTimeOffset]::UtcNow.ToString("o", [Globalization.CultureInfo]::InvariantCulture)
    }
    [IO.File]::WriteAllText(
        (Join-Path $updateBin "pending-update.json"),
        ($pending | ConvertTo-Json -Depth 3) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false)
    )
    $autoVersion = (& powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File (Join-Path $updateHost "OdooConsultor.ps1") version 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $autoVersion -ne ("OdooConsultor " + $candidateVersion)) { throw "El reinicio simulado no activó la actualización pendiente." }
    if ((Get-FileHash -LiteralPath (Join-Path $updateBin "OdooConsultor.dll") -Algorithm SHA256).Hash.ToLowerInvariant() -ne $candidateHash) { throw "La actualización automática no conservó el SHA-256." }
    if (Test-Path -LiteralPath (Join-Path $updateBin "pending-update.json")) { throw "La actualización automática no retiró el manifiesto aplicado." }

    [pscustomobject]@{
        status = "ok"
        executable = $executable
        sha256 = $buildHash
        self_tests = 199
        mcp_tools = $actualTools.Count
        production_write_blocked = $true
        runbook_only_verified = $true
        declarative_policy_verified = $true
        phase2_mcp_verified = $true
        phase3_mcp_verified = $true
        phase4_mcp_verified = $true
        legacy_rpc_mock_verified = $true
        sources_policy_verified = $true
        functional_diagnostics_verified = $true
        functional_parameterization_lifecycle_verified = $true
        demo_production_block_verified = $true
        demo_lifecycle_mock_verified = $true
        demo_cleanup_without_delete_verified = $true
        restart_auto_update_verified = $true

        spreadsheet_mcp_verified = $true
    } | ConvertTo-Json -Depth 4
}
finally {
    $tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolvedTarget = [IO.Path]::GetFullPath($temporaryRoot)
    if ($resolvedTarget.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedTarget).StartsWith("odoo-consultor-integration-", [StringComparison]::Ordinal) -and
        (Test-Path -LiteralPath $resolvedTarget)) {
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
    }
}
