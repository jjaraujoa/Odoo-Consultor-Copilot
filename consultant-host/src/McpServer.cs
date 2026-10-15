using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace OdooConsultor
{
    public sealed class McpServer
    {
        private readonly ProjectStore project;
        private readonly StudioService studio;
        private readonly GeneralistService generalist;
        private readonly ReplicaService replicas;
        private readonly SourceService sources;
        private readonly DemoService demos;
        private readonly FunctionalService functional;
        private readonly ModuleService modules;
        private readonly DemoActionService demoActions;
        private readonly InstanceContractService instanceContracts;
        private readonly DemoCompensationService demoCompensation;
        private readonly SpreadsheetService spreadsheet;
        private readonly PromotionService promotions;

        public McpServer(ProjectStore projectStore)
        {
            project = projectStore;
            studio = new StudioService(projectStore);
            generalist = new GeneralistService(projectStore, studio);
            replicas = new ReplicaService(projectStore);
            sources = new SourceService(projectStore);
            demos = new DemoService(projectStore);
            functional = new FunctionalService(projectStore);
            modules = new ModuleService(projectStore);
            demoActions = new DemoActionService(projectStore);
            instanceContracts = new InstanceContractService(projectStore);
            demoCompensation = new DemoCompensationService(projectStore);
            spreadsheet = new SpreadsheetService(projectStore);
            promotions = new PromotionService(projectStore);
        }

        public int Run()
        {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = new UTF8Encoding(false);
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    Dictionary<string, object> request = JsonUtil.DeserializeObject(line);
                    Handle(request);
                }
                catch (Exception error)
                {
                    Write(JsonRpcError(null, -32700, "Solicitud JSON-RPC inválida: " + Redactor.Clean(error.Message)));
                }
            }
            return 0;
        }

        private void Handle(IDictionary<string, object> request)
        {
            object id = request.ContainsKey("id") ? request["id"] : null;
            string method = request.ContainsKey("method") ? Convert.ToString(request["method"], CultureInfo.InvariantCulture) : string.Empty;
            if (method == "notifications/initialized" || method == "notifications/cancelled") return;
            if (method == "initialize")
            {
                Dictionary<string, object> parameters = request.ContainsKey("params") ? request["params"] as Dictionary<string, object> : null;
                string requestedVersion = parameters != null && parameters.ContainsKey("protocolVersion")
                    ? Convert.ToString(parameters["protocolVersion"], CultureInfo.InvariantCulture)
                    : "2025-06-18";
                Write(JsonRpcResult(id, JsonUtil.Dictionary(
                    "protocolVersion", requestedVersion,
                    "capabilities", JsonUtil.Dictionary("tools", JsonUtil.Dictionary("listChanged", false)),
                    "serverInfo", JsonUtil.Dictionary("name", ProductInfo.McpServerName, "version", ProductInfo.Version),
                    "instructions", ProductInfo.McpInstructions)));
                return;
            }
            if (method == "ping")
            {
                Write(JsonRpcResult(id, new Dictionary<string, object>()));
                return;
            }
            if (method == "tools/list")
            {
                Write(JsonRpcResult(id, JsonUtil.Dictionary("tools", ToolDefinitions().ToArray())));
                return;
            }
            if (method == "tools/call")
            {
                Dictionary<string, object> parameters = request.ContainsKey("params") ? request["params"] as Dictionary<string, object> : null;
                if (parameters == null) throw new ConsultantException("invalid_mcp_call", "Faltan parámetros para tools/call.");
                string name = JsonUtil.GetString(parameters, "name", true);
                Dictionary<string, object> arguments = parameters.ContainsKey("arguments") ? parameters["arguments"] as Dictionary<string, object> : new Dictionary<string, object>();
                try
                {
                    object result = CallTool(name, arguments ?? new Dictionary<string, object>());
                    Write(JsonRpcResult(id, ToolResponse(result, false)));
                }
                catch (Exception error)
                {
                    ConsultantException consultant = error as ConsultantException;
                    object safe = JsonUtil.Dictionary(
                        "ok", false,
                        "error_code", consultant == null ? "internal_error" : consultant.Code,
                        "message", Redactor.Clean(error.Message));
                    Write(JsonRpcResult(id, ToolResponse(safe, true)));
                }
                return;
            }
            Write(JsonRpcError(id, -32601, "Método MCP no soportado."));
        }

        private object CallTool(string name, IDictionary<string, object> arguments)
        {
            if (name == "conexion_estado") return studio.ConnectionStatus(JsonUtil.GetString(arguments, "entorno", false));
            if (name == "conexion_probar") return studio.TestConnection(JsonUtil.GetString(arguments, "entorno", false));
            if (name == "odoo_inspeccionar_modelo") return studio.InspectModel(JsonUtil.GetString(arguments, "entorno", true), JsonUtil.GetString(arguments, "modelo", true));
            if (name == "studio_inventariar") return studio.Inventory(JsonUtil.GetString(arguments, "entorno", true), JsonUtil.GetString(arguments, "modelo", false));
            if (name == "odoo_descubrir_capacidades") return generalist.Discover(
                JsonUtil.GetString(arguments, "entorno", true),
                JsonUtil.GetString(arguments, "modelo", false),
                JsonUtil.GetString(arguments, "busqueda", false),
                arguments.ContainsKey("limite") ? (int)JsonUtil.GetLong(arguments, "limite", true) : 50);
            if (name == "odoo_descubrir_base_datos") return OdooClient.DiscoverDatabase(
                JsonUtil.GetString(arguments, "url", true),
                30000);
            if (name == "odoo_consultar_registros")
            {
                Dictionary<string, object> query = arguments.ContainsKey("consulta") ? arguments["consulta"] as Dictionary<string, object> : null;
                if (query == null) throw new ConsultantException("invalid_input", "consulta debe ser un objeto declarativo.");
                return generalist.Search(JsonUtil.GetString(arguments, "entorno", true), query);
            }
            if (name == "odoo_comparar_configuracion") return generalist.CompareConfiguration(
                JsonUtil.GetString(arguments, "entorno_origen", true),
                JsonUtil.GetString(arguments, "entorno_destino", true),
                JsonUtil.GetString(arguments, "modelo", true));
            if (name == "cambio_validar_contrato")
            {
                Dictionary<string, object> contract = arguments.ContainsKey("contrato") ? arguments["contrato"] as Dictionary<string, object> : null;
                if (contract == null) throw new ConsultantException("invalid_input", "contrato debe ser un objeto declarativo.");
                return generalist.ValidateChange(JsonUtil.GetString(arguments, "entorno", true), contract);
            }
            if (name == "cambio_preparar_paquete")
            {
                Dictionary<string, object> contract = arguments.ContainsKey("contrato") ? arguments["contrato"] as Dictionary<string, object> : null;
                if (contract == null) throw new ConsultantException("invalid_input", "contrato debe ser un objeto declarativo.");
                return generalist.PrepareChange(JsonUtil.GetString(arguments, "entorno", true), contract);
            }
            if (name == "studio_preparar_cambio")
            {
                Dictionary<string, object> specification = arguments.ContainsKey("especificacion") ? arguments["especificacion"] as Dictionary<string, object> : null;
                if (specification == null) throw new ConsultantException("invalid_input", "especificacion debe ser un objeto.");
                return studio.PrepareChange(JsonUtil.GetString(arguments, "entorno", true), JsonUtil.GetString(arguments, "tipo", true), JsonUtil.GetString(arguments, "modelo", true), specification);
            }
            if (name == "replica_analizar_destino") return replicas.Analyze(JsonUtil.GetString(arguments, "entorno", true), JsonUtil.GetString(arguments, "replica_slug", true));
            if (name == "replica_preparar_paquete") return replicas.Prepare(JsonUtil.GetString(arguments, "entorno", true), JsonUtil.GetString(arguments, "replica_slug", true));
            if (name == "replica_ejecutar_uat") return replicas.RetryUat(JsonUtil.GetString(arguments, "paquete_id", true), JsonUtil.GetLong(arguments, "registro_prueba_id", true), JsonUtil.GetString(arguments, "confirmacion", true));
            if (name == "replica_probar_efecto_externo") return new ReplicaPhase3Service(project).TestExternal(JsonUtil.GetString(arguments, "paquete_id", true), JsonUtil.GetString(arguments, "accion_key", true), JsonUtil.GetLong(arguments, "registro_prueba_id", true), JsonUtil.GetString(arguments, "confirmacion", true));
            if (name == "replica_activar_efectos_externos") return new ReplicaPhase3Service(project).ActivateExternal(JsonUtil.GetString(arguments, "paquete_id", true), JsonUtil.GetString(arguments, "confirmacion", true), JsonUtil.GetString(arguments, "confirmacion_prueba_manual", false));
            if (name == "replica_verificar_acceso_negativo") return new ReplicaPhase4Service(project).VerifyNegativeAccess(JsonUtil.GetString(arguments, "paquete_id", true), JsonUtil.GetString(arguments, "prueba_key", true), JsonUtil.GetString(arguments, "perfil_prueba", true));
            if (name == "promocion_analizar_impacto") return promotions.AnalyzeImpact(JsonUtil.GetString(arguments, "replica_slug", true));
            if (name == "promocion_preparar_runbook") return promotions.PrepareRunbook(JsonUtil.GetString(arguments, "replica_slug", true));
            if (name == "promocion_verificar_produccion") return promotions.VerifyProduction(JsonUtil.GetString(arguments, "replica_slug", true));
            if (name == "promocion_preparar_paquete")
            {
                IDictionary<string, object> references = arguments.ContainsKey("referencias") ? arguments["referencias"] as IDictionary<string, object> : null;
                return promotions.PreparePackage(
                    JsonUtil.GetString(arguments, "replica_slug", true),
                    JsonUtil.GetString(arguments, "referencia_respaldo", true),
                    references);
            }
            if (name == "promocion_aplicar_paquete")
            {
                IDictionary<string, object> references = arguments.ContainsKey("referencias") ? arguments["referencias"] as IDictionary<string, object> : null;
                return promotions.ApplyPackage(
                    JsonUtil.GetString(arguments, "paquete_id", true),
                    JsonUtil.GetString(arguments, "confirmacion", true),
                    references);
            }
            if (name == "promocion_activar_efectos_externos")
            {
                return promotions.ActivateExternalEffects(
                    JsonUtil.GetString(arguments, "promocion_id", true),
                    JsonUtil.GetString(arguments, "confirmacion", true));
            }
            if (name == "tablero_inspeccionar")
            {
                long? dashboardId = arguments.ContainsKey("tablero_id") && arguments["tablero_id"] != null ? (long?)JsonUtil.GetLong(arguments, "tablero_id", false) : null;
                return spreadsheet.Inspect(
                    JsonUtil.GetString(arguments, "entorno", true),
                    JsonUtil.GetString(arguments, "modelo", false),
                    dashboardId,
                    JsonUtil.GetString(arguments, "nombre", false));
            }
            if (name == "tablero_validar_definicion")
            {
                object def = arguments.ContainsKey("definicion") ? arguments["definicion"] : null;
                bool autoFix = JsonUtil.GetBool(arguments, "auto_fix", false);
                return spreadsheet.ValidateDefinition(def, autoFix);
            }
            if (name == "tablero_preparar_paquete")
            {
                long? targetId = arguments.ContainsKey("tablero_id") && arguments["tablero_id"] != null ? (long?)JsonUtil.GetLong(arguments, "tablero_id", false) : null;
                long? groupId = arguments.ContainsKey("grupo_id") && arguments["grupo_id"] != null ? (long?)JsonUtil.GetLong(arguments, "grupo_id", false) : null;
                object def = arguments.ContainsKey("definicion") ? arguments["definicion"] : null;
                bool autoFix = JsonUtil.GetBool(arguments, "auto_fix", false);
                return spreadsheet.PreparePackage(
                    JsonUtil.GetString(arguments, "entorno", true),
                    JsonUtil.GetString(arguments, "modelo", false),
                    JsonUtil.GetString(arguments, "modo", true),
                    targetId,
                    JsonUtil.GetString(arguments, "nombre", true),
                    groupId,
                    def,
                    autoFix);
            }
            if (name == "tablero_aplicar_paquete")
            {
                return spreadsheet.ApplyPackage(
                    JsonUtil.GetString(arguments, "paquete_id", true),
                    JsonUtil.GetString(arguments, "confirmacion", true));
            }
            if (name == "studio_aplicar_paquete")
            {
                Dictionary<string, object> references = arguments.ContainsKey("referencias") ? arguments["referencias"] as Dictionary<string, object> : null;
                return studio.ApplyPackage(JsonUtil.GetString(arguments, "paquete_id", true), JsonUtil.GetString(arguments, "confirmacion", true), JsonUtil.GetLong(arguments, "registro_prueba_id", false), references);
            }
            if (name == "studio_verificar_paquete") return studio.VerifyPackage(JsonUtil.GetString(arguments, "paquete_id", true));
            if (name == "studio_revertir_paquete_seguro") return studio.RollbackPackage(JsonUtil.GetString(arguments, "paquete_id", true), JsonUtil.GetString(arguments, "confirmacion", true));
            if (name == "studio_generar_runbook")
            {
                IList<object> steps = ToObjectList(arguments.ContainsKey("pasos") ? arguments["pasos"] : null);
                return studio.GenerateRunbook(
                    JsonUtil.GetString(arguments, "entorno", true),
                    JsonUtil.GetString(arguments, "categoria", true),
                    JsonUtil.GetString(arguments, "titulo", true),
                    JsonUtil.GetString(arguments, "objetivo", true),
                    steps);
            }
            if (name == "fuentes_estado") return sources.Status(JsonUtil.GetString(arguments, "version", false));
            if (name == "fuentes_sincronizar") return sources.Synchronize(JsonUtil.GetString(arguments, "version", false), JsonUtil.GetBool(arguments, "incluir_enterprise", false));
            if (name == "fuentes_crear_bloqueo") return sources.CreateLock(JsonUtil.GetString(arguments, "demo_slug", true), JsonUtil.GetString(arguments, "version", false), JsonUtil.GetBool(arguments, "incluir_enterprise", false), JsonUtil.GetBool(arguments, "incluir_codigo_cliente", false));
            if (name == "fuentes_buscar") return sources.Search(
                JsonUtil.GetString(arguments, "repositorio", true), JsonUtil.GetString(arguments, "version", false),
                JsonUtil.GetString(arguments, "termino", true), JsonUtil.GetString(arguments, "modulo", false),
                JsonUtil.GetString(arguments, "simbolo", false), arguments.ContainsKey("limite") ? (int)JsonUtil.GetLong(arguments, "limite", true) : 10);
            if (name == "odoo_identificar_instancia") return sources.Identify(JsonUtil.GetString(arguments, "entorno", true));
            if (name == "modulos_buscar") return modules.Search(
                JsonUtil.GetString(arguments, "entorno", true),
                ToStringList(arguments.ContainsKey("terminos") ? arguments["terminos"] : null),
                JsonUtil.GetBool(arguments, "incluir_instalados", false));
            if (name == "modulos_preparar_instalacion") return modules.Prepare(
                JsonUtil.GetString(arguments, "entorno", true), JsonUtil.GetString(arguments, "objetivo", true),
                ToStringList(arguments.ContainsKey("modulos") ? arguments["modulos"] : null),
                JsonUtil.GetString(arguments, "demo_slug", false));
            if (name == "modulos_aplicar_instalacion") return modules.Apply(JsonUtil.GetString(arguments, "plan_id", true), JsonUtil.GetString(arguments, "confirmacion", true));
            if (name == "modulos_verificar_instalacion") return modules.Verify(JsonUtil.GetString(arguments, "plan_id", true));
            if (name == "funcional_inventariar_aplicacion") return functional.Inventory(JsonUtil.GetString(arguments, "entorno", true), JsonUtil.GetString(arguments, "aplicacion", true));
            if (name == "funcional_diagnosticar_objetivo")
            {
                Dictionary<string, object> objective = arguments.ContainsKey("objetivo") ? arguments["objetivo"] as Dictionary<string, object> : null;
                if (objective == null) throw new ConsultantException("invalid_input", "objetivo debe ser un contrato funcional schema 1.");
                return functional.Diagnose(JsonUtil.GetString(arguments, "entorno", true), JsonUtil.GetString(arguments, "diagnostico_slug", true), objective);
            }
            if (name == "funcional_preparar_parametrizacion") return functional.Prepare(
                JsonUtil.GetString(arguments, "diagnostico_id", true),
                ToStringList(arguments.ContainsKey("brechas") ? arguments["brechas"] : null));
            if (name == "demo_inventariar_aplicacion") return demos.Inventory(JsonUtil.GetString(arguments, "entorno", true), JsonUtil.GetString(arguments, "aplicacion", true));
            if (name == "demo_inventariar_cobertura_total") return instanceContracts.Inventory(
                JsonUtil.GetString(arguments, "entorno", true), JsonUtil.GetString(arguments, "demo_slug", true));
            if (name == "demo_preparar_escenario") return demos.Prepare(
                JsonUtil.GetString(arguments, "entorno", true), JsonUtil.GetString(arguments, "demo_slug", true),
                JsonUtil.GetString(arguments, "aplicacion", true), JsonUtil.GetString(arguments, "objetivo", true),
                ToStringList(arguments.ContainsKey("requerimientos") ? arguments["requerimientos"] : null),
                arguments.ContainsKey("configuracion") ? arguments["configuracion"] as Dictionary<string, object> : null);
            if (name == "demo_preparar_registros_operativos") return demos.PrepareOperational(
                JsonUtil.GetString(arguments, "entorno", true), JsonUtil.GetString(arguments, "demo_slug", true),
                JsonUtil.GetString(arguments, "objetivo", true),
                ToStringList(arguments.ContainsKey("requerimientos") ? arguments["requerimientos"] : null),
                ToObjectList(arguments.ContainsKey("registros") ? arguments["registros"] : null));
            if (name == "demo_preparar_acciones") return demoActions.Prepare(
                JsonUtil.GetString(arguments, "escenario_id", true), ToObjectList(arguments.ContainsKey("acciones") ? arguments["acciones"] : null));
            if (name == "demo_preparar_flujo")
            {
                string flowBlock = arguments.ContainsKey("bloque") && arguments["bloque"] != null ? Convert.ToString(arguments["bloque"], CultureInfo.InvariantCulture) : "general";
                return demoActions.PrepareFlow(
                    JsonUtil.GetString(arguments, "escenario_id", true), flowBlock, ToObjectList(arguments.ContainsKey("acciones") ? arguments["acciones"] : null));
            }
            if (name == "demo_ejecutar_acciones") return demoActions.Apply(
                JsonUtil.GetString(arguments, "plan_id", true), JsonUtil.GetString(arguments, "confirmacion", true));
            if (name == "demo_ejecutar_flujo") return demoActions.Apply(
                JsonUtil.GetString(arguments, "plan_id", true), JsonUtil.GetString(arguments, "confirmacion", true));
            if (name == "demo_verificar_acciones") return demoActions.Verify(JsonUtil.GetString(arguments, "plan_id", true));
            if (name == "demo_verificar_flujo") return demoActions.Verify(JsonUtil.GetString(arguments, "plan_id", true));
            if (name == "demo_preparar_compensacion") return demoCompensation.Prepare(JsonUtil.GetString(arguments, "escenario_id", true));
            if (name == "demo_ejecutar_compensacion") return demoCompensation.Apply(JsonUtil.GetString(arguments, "plan_id", true), JsonUtil.GetString(arguments, "confirmacion", true));
            if (name == "demo_aplicar_escenario") return demos.Apply(
                JsonUtil.GetString(arguments, "escenario_id", true), JsonUtil.GetString(arguments, "confirmacion", true),
                arguments.ContainsKey("referencias") ? arguments["referencias"] as Dictionary<string, object> : null);
            if (name == "demo_verificar_paso") return demos.VerifyStep(JsonUtil.GetString(arguments, "escenario_id", true), JsonUtil.GetString(arguments, "paso", true));
            if (name == "demo_verificar_escenario") return demos.Verify(JsonUtil.GetString(arguments, "escenario_id", true));
            if (name == "demo_limpiar_escenario") return demos.Clean(JsonUtil.GetString(arguments, "escenario_id", true), JsonUtil.GetString(arguments, "confirmacion", true));
            if (name == "demo_preparar_reemplazo_mailing") return demos.PrepareMailingReplacement(
                JsonUtil.GetString(arguments, "escenario_anterior_id", true),
                arguments.ContainsKey("imagen") ? arguments["imagen"] as Dictionary<string, object> : null,
                JsonUtil.GetString(arguments, "personalizacion", true));
            if (name == "demo_archivar_reemplazados") return demos.ArchiveReplacedMailings(JsonUtil.GetString(arguments, "escenario_id", true), JsonUtil.GetString(arguments, "confirmacion", true));
            if (name == "demo_restaurar_reemplazados") return demos.RestoreReplacedMailings(JsonUtil.GetString(arguments, "escenario_id", true), JsonUtil.GetString(arguments, "confirmacion", true));
            throw new ConsultantException("tool_not_found", "La herramienta no existe en el catálogo cerrado.");
        }

        private static IList<object> ToObjectList(object value)
        {
            List<object> result = new List<object>();
            IEnumerable sequence = value as IEnumerable;
            if (sequence != null && !(value is string)) foreach (object item in sequence) result.Add(item);
            return result;
        }

        private static IList<string> ToStringList(object value)
        {
            List<string> result = new List<string>();
            IEnumerable sequence = value as IEnumerable;
            if (sequence != null && !(value is string)) foreach (object item in sequence) result.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
            return result;
        }

        private static object ToolResponse(object result, bool isError)
        {
            return JsonUtil.Dictionary(
                "content", new object[] { JsonUtil.Dictionary("type", "text", "text", JsonUtil.SerializePretty(result)) },
                "isError", isError);
        }

        private static Dictionary<string, object> JsonRpcResult(object id, object result)
        {
            return JsonUtil.Dictionary("jsonrpc", "2.0", "id", id, "result", result);
        }

        private static Dictionary<string, object> JsonRpcError(object id, int code, string message)
        {
            return JsonUtil.Dictionary("jsonrpc", "2.0", "id", id, "error", JsonUtil.Dictionary("code", code, "message", message));
        }

        private static void Write(object response)
        {
            Console.WriteLine(JsonUtil.Serialize(response));
            Console.Out.Flush();
        }

        private static List<object> ToolDefinitions()
        {
            List<object> tools = new List<object>();
            tools.Add(Tool("conexion_estado", "Comprueba perfiles locales y disponibilidad de credenciales sin revelar secretos.",
                ObjectSchema(new Dictionary<string, object> { { "entorno", StringEnum(false, "staging", "production") } }, new string[0]), true, false, true));
            tools.Add(Tool("conexion_probar", "Prueba activamente en vivo la conexión contra Odoo (autenticación de usuario, permisos de administrador, latencia y versión) sin modificar nada.",
                ObjectSchema(new Dictionary<string, object> { { "entorno", StringEnum(false, "staging", "production") } }, new string[0]), true, false, true));
            tools.Add(Tool("odoo_inspeccionar_modelo", "Lee campos y vistas heredadas de un modelo mediante contratos JSON-2 cerrados.",
                ObjectSchema(new Dictionary<string, object> { { "entorno", StringEnum(false, "staging", "production") }, { "modelo", StringSchema("Nombre técnico, por ejemplo sale.order.") } }, new[] { "entorno", "modelo" }), true, false, true));
            tools.Add(Tool("studio_inventariar", "Inventaría metadatos Studio; no lee registros de negocio completos.",
                ObjectSchema(new Dictionary<string, object> { { "entorno", StringEnum(false, "staging", "production") }, { "modelo", StringSchema("Filtro opcional por modelo técnico.") } }, new[] { "entorno" }), true, false, true));
            tools.Add(Tool("odoo_descubrir_capacidades", "Descubre modelos, campos, módulos, /doc y permisos efectivos del usuario sin exponer una llamada arbitraria.",
                ObjectSchema(new Dictionary<string, object> {
                    { "entorno", StringEnum(false, "staging", "production") },
                    { "modelo", StringSchema("Modelo opcional para calcular campos y permisos detallados.") },
                    { "busqueda", StringSchema("Texto opcional para localizar modelos por nombre.") },
                    { "limite", JsonUtil.Dictionary("type", "integer", "minimum", 1, "maximum", 100) }
                }, new[] { "entorno" }), true, false, true));
            tools.Add(Tool("odoo_descubrir_base_datos", "Descubre o infiere el nombre técnico de la base de datos de Odoo a partir de su URL, identificando el tipo de alojamiento (Odoo.sh, SaaS o On-Premise) y la versión del servidor sin necesidad de credenciales previas.",
                ObjectSchema(new Dictionary<string, object> {
                    { "url", StringSchema("URL de la instancia Odoo (por ejemplo https://miempresa.odoo.com).") }
                }, new[] { "url" }), true, false, true));
            tools.Add(Tool("odoo_consultar_registros", "Ejecuta una búsqueda de solo lectura compilada: modelo, campos, filtros conjuntivos y límite validados; no acepta métodos.",
                ObjectSchema(new Dictionary<string, object> {
                    { "entorno", StringEnum(false, "staging", "production") },
                    { "consulta", JsonUtil.Dictionary("type", "object", "description", "Contrato de consulta schema cerrado; máximo 50 registros.", "additionalProperties", true) }
                }, new[] { "entorno", "consulta" }), true, false, true));
            tools.Add(Tool("odoo_comparar_configuracion", "Compara campos y vistas de un modelo entre staging y producción usando hashes y metadatos.",
                ObjectSchema(new Dictionary<string, object> {
                    { "entorno_origen", StringEnum(false, "staging", "production") },
                    { "entorno_destino", StringEnum(false, "staging", "production") },
                    { "modelo", StringSchema("Modelo técnico existente en ambos entornos.") }
                }, new[] { "entorno_origen", "entorno_destino", "modelo" }), true, false, true));
            tools.Add(Tool("cambio_validar_contrato", "Clasifica y valida un contrato declarativo contra modelo, permisos, entorno y política; nunca escribe.",
                ObjectSchema(new Dictionary<string, object> {
                    { "entorno", StringEnum(false, "staging", "production") },
                    { "contrato", JsonUtil.Dictionary("type", "object", "description", "Contrato declarativo versionado; no contiene endpoints ni métodos.", "additionalProperties", true) }
                }, new[] { "entorno", "contrato" }), true, false, true));
            tools.Add(Tool("cambio_preparar_paquete", "Compila un contrato seguro en paquete confirmado; las operaciones de alto riesgo se convierten en runbook.",
                ObjectSchema(new Dictionary<string, object> {
                    { "entorno", StringEnum(false, "staging", "production") },
                    { "contrato", JsonUtil.Dictionary("type", "object", "description", "Contrato declarativo schema_version 1.", "additionalProperties", true) }
                }, new[] { "entorno", "contrato" }), false, false, false));
            tools.Add(Tool("studio_preparar_cambio", "Prepara un paquete aditivo para un campo declarativo o una vista heredada; no escribe en Odoo.",
                ObjectSchema(new Dictionary<string, object> {
                    { "entorno", StringEnum(false, "staging") },
                    { "tipo", StringEnum(false, "campo", "vista") },
                    { "modelo", StringSchema("Modelo existente.") },
                    { "especificacion", JsonUtil.Dictionary("type", "object", "additionalProperties", true) }
                }, new[] { "entorno", "tipo", "modelo", "especificacion" }), false, false, false));
            tools.Add(Tool("replica_analizar_destino", "Analiza una réplica Fase 1, 2, 3 o 4 ubicada estrictamente en replicas/<slug>/ y clasifica ausencias, equivalencias, modificaciones y bloqueos sin escribir.",
                ObjectSchema(new Dictionary<string, object> { { "entorno", StringEnum(false, "staging", "production") }, { "replica_slug", StringSchema("Slug local de la réplica; no admite rutas.") } }, new[] { "entorno", "replica_slug" }), true, false, true));
            tools.Add(Tool("replica_preparar_paquete", "Compila una réplica validada Fase 1, 2, 3 o 4 en un paquete schema 3; producción genera únicamente un runbook.",
                ObjectSchema(new Dictionary<string, object> { { "entorno", StringEnum(false, "staging", "production") }, { "replica_slug", StringSchema("Slug local de la réplica; no admite rutas.") } }, new[] { "entorno", "replica_slug" }), false, false, true));
            tools.Add(Tool("replica_ejecutar_uat", "Reintenta solo la UAT declarada de un paquete instalado; exige PROBAR <id> y no reinstala componentes.",
                ObjectSchema(new Dictionary<string, object> { { "paquete_id", StringSchema("ID chg-...") }, { "registro_prueba_id", JsonUtil.Dictionary("type", "integer", "minimum", 1) }, { "confirmacion", StringSchema("Texto exacto PROBAR <id>.") } }, new[] { "paquete_id", "registro_prueba_id", "confirmacion" }), false, false, true));
            tools.Add(Tool("replica_probar_efecto_externo", "Ejecuta en staging una acción externa exacta del paquete mientras sus automatizaciones siguen inactivas; exige PROBAR-EFECTO <id>.",
                ObjectSchema(new Dictionary<string, object> { { "paquete_id", StringSchema("ID chg-...") }, { "accion_key", StringSchema("Clave lógica declarada de la acción externa.") }, { "registro_prueba_id", JsonUtil.Dictionary("type", "integer", "minimum", 1) }, { "confirmacion", StringSchema("Texto exacto PROBAR-EFECTO <id>.") } }, new[] { "paquete_id", "accion_key", "registro_prueba_id", "confirmacion" }), false, false, true));
            tools.Add(Tool("replica_activar_efectos_externos", "Activa automatizaciones externas en staging solo después de sus pruebas cerradas y exige ACTIVAR-EFECTOS <id>; webhooks entrantes requieren PRUEBA-MANUAL <id> adicional.",
                ObjectSchema(new Dictionary<string, object> { { "paquete_id", StringSchema("ID chg-...") }, { "confirmacion", StringSchema("Texto exacto ACTIVAR-EFECTOS <id>.") }, { "confirmacion_prueba_manual", StringSchema("Para webhooks entrantes: PRUEBA-MANUAL <id>.") } }, new[] { "paquete_id", "confirmacion" }), false, false, true));
            tools.Add(Tool("replica_verificar_acceso_negativo", "Comprueba una prueba de ACL o regla declarada por una réplica Fase 4 usando un perfil funcional separado guardado en Credenciales de Windows; no devuelve registros.",
                ObjectSchema(new Dictionary<string, object> { { "paquete_id", StringSchema("ID chg-...") }, { "prueba_key", StringSchema("Clave lógica declarada en negative_access_tests.") }, { "perfil_prueba", StringSchema("Alias local del perfil funcional de verificación.") } }, new[] { "paquete_id", "prueba_key", "perfil_prueba" }), true, false, true));
            tools.Add(Tool("promocion_analizar_impacto", "Compara los componentes de una réplica validada en staging contra el entorno de producción (estrictamente solo lectura) para detectar viabilidad, preexistencias y advertencias de promoción.",
                ObjectSchema(new Dictionary<string, object> { { "replica_slug", StringSchema("Slug local de la réplica validada en replicas/<slug>/.") } }, new[] { "replica_slug" }), true, false, true));
            tools.Add(Tool("promocion_preparar_runbook", "Genera la promoción formal y la guía de ejecución paso a paso (runbook dual: Studio UI y Ajustes Técnicos con XML/Python sin mojibake) para aplicar la personalización en producción.",
                ObjectSchema(new Dictionary<string, object> { { "replica_slug", StringSchema("Slug local de la réplica validada en replicas/<slug>/.") } }, new[] { "replica_slug" }), false, false, true));
            tools.Add(Tool("promocion_verificar_produccion", "Audita en solo lectura el entorno de producción para verificar componente por componente que la personalización fue aplicada conforme al manifiesto, y emite el certificado de trazabilidad.",
                ObjectSchema(new Dictionary<string, object> { { "replica_slug", StringSchema("Slug local de la réplica validada en replicas/<slug>/.") } }, new[] { "replica_slug" }), true, false, true));
            tools.Add(Tool("promocion_preparar_paquete", "Prepara un paquete declarativo y seguro para promover personalizaciones de Staging a Producción (ventana de 2 horas, sin datos demo, respaldo obligatorio).",
                ObjectSchema(new Dictionary<string, object>
                {
                    { "replica_slug", StringSchema("Slug local de la réplica probada en staging bajo replicas/<slug>/.") },
                    { "referencia_respaldo", StringSchema("Identificador o referencia del snapshot/respaldo previo de producción (obligatorio, mínimo 5 caracteres).") },
                    { "referencias", JsonUtil.Dictionary("type", "object", "description", "Mapeo opcional de referencias y XML IDs para producción.") }
                }, new[] { "replica_slug", "referencia_respaldo" }), false, false, true));
            tools.Add(Tool("promocion_aplicar_paquete", "Aplica de forma atómica y segura un paquete de promoción en Producción; exige confirmación reforzada 'PROMOVER-PRODUCCION <id> RESPALDO-CONFIRMADO', audita al 100% y certifica criptográficamente.",
                ObjectSchema(new Dictionary<string, object>
                {
                    { "paquete_id", StringSchema("ID del paquete de promoción preparado (prom-...).") },
                    { "confirmacion", StringSchema("Confirmación exacta reforzada: PROMOVER-PRODUCCION <id> RESPALDO-CONFIRMADO.") },
                    { "referencias", JsonUtil.Dictionary("type", "object", "description", "Mapeo opcional de referencias y XML IDs para producción.") }
                }, new[] { "paquete_id", "confirmacion" }), false, false, true));
            tools.Add(Tool("promocion_activar_efectos_externos", "Activa compuertas de efectos externos (automatizaciones de correos/webhooks) en Producción tras verificación previa; exige 'ACTIVAR-EFECTOS-PRODUCCION <id>'.",
                ObjectSchema(new Dictionary<string, object>
                {
                    { "promocion_id", StringSchema("ID del paquete o promoción aplicado (prom-... o slug).") },
                    { "confirmacion", StringSchema("Confirmación exacta: ACTIVAR-EFECTOS-PRODUCCION <id>.") }
                }, new[] { "promocion_id", "confirmacion" }), false, false, true));
            tools.Add(Tool("tablero_inspeccionar", "Inspecciona en solo lectura la estructura técnica y el JSON de un tablero u hoja de cálculo en Odoo (spreadsheet.dashboard o documents.document).",
                ObjectSchema(new Dictionary<string, object>
                {
                    { "entorno", StringEnum(false, "staging", "production") },
                    { "modelo", StringEnum(true, "spreadsheet.dashboard", "documents.document") },
                    { "tablero_id", JsonUtil.Dictionary("type", "integer", "minimum", 1) },
                    { "nombre", StringSchema("Nombre o término de búsqueda del tablero si no se indica ID.") }
                }, new[] { "entorno" }), true, false, true));
            tools.Add(Tool("tablero_validar_definicion", "Audita localmente la definición JSON de una hoja de cálculo contra las 5 reglas críticas de Odoo (enlaces con action, dominios 2D AST, mojibake, etiquetas y celdas como cadenas). Permite auto_fix.",
                ObjectSchema(new Dictionary<string, object>
                {
                    { "definicion", JsonUtil.Dictionary("type", "object", "description", "Objeto JSON o diccionario con la definición completa del spreadsheet.") },
                    { "auto_fix", JsonUtil.Dictionary("type", "boolean", "description", "Si es true, repara automáticamente dominios aplanados, enlaces sin action y celdas objeto.") }
                }, new[] { "definicion" }), true, false, true));
            tools.Add(Tool("tablero_preparar_paquete", "Prepara un paquete de creación o actualización de hoja de cálculo / tablero en staging con validación semántica y compuerta de seguridad; producción queda bloqueada.",
                ObjectSchema(new Dictionary<string, object>
                {
                    { "entorno", StringEnum(false, "staging") },
                    { "modelo", StringEnum(true, "spreadsheet.dashboard", "documents.document") },
                    { "modo", StringEnum(false, "create", "update") },
                    { "tablero_id", JsonUtil.Dictionary("type", "integer", "minimum", 1, "description", "ID requerido si modo es update.") },
                    { "nombre", StringSchema("Nombre visible del tablero o documento.") },
                    { "grupo_id", JsonUtil.Dictionary("type", "integer", "minimum", 1, "description", "ID del grupo de tableros (opcional).") },
                    { "definicion", JsonUtil.Dictionary("type", "object", "description", "Definición completa del spreadsheet.") },
                    { "auto_fix", JsonUtil.Dictionary("type", "boolean", "description", "Aplica reparaciones automáticas antes de empaquetar.") }
                }, new[] { "entorno", "modo", "nombre", "definicion" }), false, false, true));
            tools.Add(Tool("tablero_aplicar_paquete", "Aplica en staging un paquete vigente de tablero tras confirmación exacta APLICAR-TABLERO <id> y verifica la persistencia por lectura.",
                ObjectSchema(new Dictionary<string, object>
                {
                    { "paquete_id", StringSchema("ID chg-...") },
                    { "confirmacion", StringSchema("Texto exacto APLICAR-TABLERO <id>.") }
                }, new[] { "paquete_id", "confirmacion" }), false, false, true));
            tools.Add(Tool("studio_aplicar_paquete", "Aplica en staging un paquete vigente tras confirmación exacta. Una réplica puede ejecutar su UAT cerrada sobre el registro autorizado.",
                ObjectSchema(new Dictionary<string, object> { { "paquete_id", StringSchema("ID chg-...") }, { "confirmacion", StringSchema("Texto exacto APLICAR <id>.") }, { "registro_prueba_id", JsonUtil.Dictionary("type", "integer", "minimum", 1) }, { "referencias", JsonUtil.Dictionary("type", "object", "description", "Referencias portables explícitas del destino.", "additionalProperties", true) } }, new[] { "paquete_id", "confirmacion" }), false, false, true));
            tools.Add(Tool("studio_verificar_paquete", "Verifica mediante JSON-2 que los metadatos de un paquete aplicado existen.",
                ObjectSchema(new Dictionary<string, object> { { "paquete_id", StringSchema("ID chg-...") } }, new[] { "paquete_id" }), true, false, true));
            tools.Add(Tool("studio_revertir_paquete_seguro", "Restaura propiedades permitidas y desactiva vistas o menús creados; preserva campos, modelos y datos. Requiere REVERTIR <id>.",
                ObjectSchema(new Dictionary<string, object> { { "paquete_id", StringSchema("ID chg-...") }, { "confirmacion", StringSchema("Texto exacto REVERTIR <id>.") } }, new[] { "paquete_id", "confirmacion" }), false, true, true));
            tools.Add(Tool("studio_generar_runbook", "Genera un runbook manual para operaciones Studio de alto riesgo o promoción a producción; nunca ejecuta esas operaciones.",
                ObjectSchema(new Dictionary<string, object> {
                    { "entorno", StringEnum(false, "staging", "production") },
                    { "categoria", StringEnum(false, "modelo_nuevo", "reporte", "automatizacion", "webhook", "seguridad", "accion_servidor", "codigo_calculado", "llamada_metodo", "modificacion", "eliminacion", "promocion_produccion") },
                    { "titulo", StringSchema("Título del runbook.") },
                    { "objetivo", StringSchema("Objetivo funcional.") },
                    { "pasos", JsonUtil.Dictionary("type", "array", "items", JsonUtil.Dictionary("type", "string")) }
                }, new[] { "entorno", "categoria", "titulo", "objetivo" }), false, false, false));
            tools.Add(Tool("fuentes_estado", "Muestra disponibilidad, commit y autorización de las fuentes registradas sin leer contenido privado bloqueado.",
                ObjectSchema(new Dictionary<string, object> { { "version", StringEnum(false, "17.0", "18.0", "19.0") } }, new string[0]), true, false, true));
            tools.Add(Tool("fuentes_sincronizar", "Sincroniza explícitamente documentación y código oficial desde repositorios registrados. Git usa autenticación interactiva; nunca acepta tokens.",
                ObjectSchema(new Dictionary<string, object> { { "version", StringEnum(false, "17.0", "18.0", "19.0") }, { "incluir_enterprise", JsonUtil.Dictionary("type", "boolean") } }, new string[0]), false, false, true, true));
            tools.Add(Tool("fuentes_crear_bloqueo", "Fija commits de fuentes para una demostración y crea source-lock.json.",
                ObjectSchema(new Dictionary<string, object> { { "demo_slug", StringSchema("Slug bajo demostraciones/.") }, { "version", StringEnum(false, "17.0", "18.0", "19.0") }, { "incluir_enterprise", JsonUtil.Dictionary("type", "boolean") }, { "incluir_codigo_cliente", JsonUtil.Dictionary("type", "boolean") } }, new[] { "demo_slug" }), false, false, true));
            tools.Add(Tool("fuentes_buscar", "Busca términos acotados solo dentro de un repositorio registrado y devuelve evidencia con versión, commit, ruta y línea.",
                ObjectSchema(new Dictionary<string, object> { { "repositorio", StringEnum(false, "documentation", "community", "enterprise", "client") }, { "version", StringEnum(false, "17.0", "18.0", "19.0") }, { "termino", StringSchema("Término literal acotado.") }, { "modulo", StringSchema("Nombre técnico opcional de módulo.") }, { "simbolo", StringSchema("Símbolo opcional.") }, { "limite", JsonUtil.Dictionary("type", "integer", "minimum", 1, "maximum", 25) } }, new[] { "repositorio", "termino" }), true, false, true));
            tools.Add(Tool("odoo_identificar_instancia", "Identifica versión y transporte de la base y bloquea discrepancias con proyecto.yaml.",
                ObjectSchema(new Dictionary<string, object> { { "entorno", StringEnum(false, "staging", "production") } }, new[] { "entorno" }), true, false, true));
            object moduleNames = JsonUtil.Dictionary("type", "array", "minItems", 1, "maxItems", 30, "uniqueItems", true, "items", JsonUtil.Dictionary("type", "string", "pattern", "^[a-z][a-z0-9_]{1,79}$"));
            tools.Add(Tool("modulos_buscar", "Busca aplicaciones y módulos técnicos en el catálogo real de Odoo y clasifica si son oficiales e instalables; no modifica la base.",
                ObjectSchema(new Dictionary<string, object> {
                    { "entorno", StringEnum(false, "staging", "production") },
                    { "terminos", JsonUtil.Dictionary("type", "array", "minItems", 1, "maxItems", 10, "items", JsonUtil.Dictionary("type", "string", "minLength", 2, "maxLength", 80)) },
                    { "incluir_instalados", JsonUtil.Dictionary("type", "boolean") }
                }, new[] { "entorno", "terminos" }), true, false, true));
            tools.Add(Tool("modulos_preparar_instalacion", "Prepara en staging un plan cerrado de módulos oficiales, dependencias y auto-instalaciones potenciales. Verifica administración, colisiones y precondiciones; todavía no instala.",
                ObjectSchema(new Dictionary<string, object> {
                    { "entorno", StringEnum(false, "staging") }, { "objetivo", JsonUtil.Dictionary("type", "string", "minLength", 1, "maxLength", 1000) },
                    { "modulos", moduleNames }, { "demo_slug", JsonUtil.Dictionary("type", "string", "pattern", "^[a-z0-9][a-z0-9_-]{1,62}$") }
                }, new[] { "entorno", "objetivo", "modulos" }), false, false, true));
            tools.Add(Tool("modulos_aplicar_instalacion", "Instala únicamente los módulos oficiales de un plan vigente en staging mediante el contrato fijo de Odoo. Exige APLICAR <id>; no configura cuentas externas ni permite desinstalar.",
                ObjectSchema(new Dictionary<string, object> { { "plan_id", StringSchema("ID chg-... del plan de módulos.") }, { "confirmacion", StringSchema("Texto exacto APLICAR <id>.") } }, new[] { "plan_id", "confirmacion" }), false, true, true));
            tools.Add(Tool("modulos_verificar_instalacion", "Relee los estados de un plan de módulos sin repetir la instalación; usar si Odoo reinició o la respuesta fue ambigua.",
                ObjectSchema(new Dictionary<string, object> { { "plan_id", StringSchema("ID chg-... del plan de módulos.") } }, new[] { "plan_id" }), true, false, true));
            tools.Add(Tool("funcional_inventariar_aplicacion", "Inventaría módulos, modelos, campos, permisos y adaptadores funcionales de una aplicación usando evidencia de la base real; no escribe.",
                ObjectSchema(new Dictionary<string, object> { { "entorno", StringEnum(false, "staging", "production") }, { "aplicacion", StringEnum(false, "crm", "ventas", "compras", "inventario", "contabilidad", "marketing", "sitio_web", "ecommerce") } }, new[] { "entorno", "aplicacion" }), true, false, true));
            tools.Add(Tool("funcional_diagnosticar_objetivo", "Compara un objetivo funcional versionado contra la base y guarda una matriz íntegra de cumplimiento, brechas, decisiones y runbooks; no modifica Odoo.",
                ObjectSchema(new Dictionary<string, object> {
                    { "entorno", StringEnum(false, "staging", "production") },
                    { "diagnostico_slug", StringSchema("Slug estable bajo diagnosticos/.") },
                    { "objetivo", FunctionalObjectiveSchema() }
                }, new[] { "entorno", "diagnostico_slug", "objetivo" }), false, false, false));
            tools.Add(Tool("funcional_preparar_parametrizacion", "Compila brechas ejecutables de un diagnóstico íntegro en un paquete confirmado. Producción genera runbook; no acepta modelos ni métodos.",
                ObjectSchema(new Dictionary<string, object> {
                    { "diagnostico_id", StringSchema("ID diag-... devuelto por el diagnóstico.") },
                    { "brechas", JsonUtil.Dictionary("type", "array", "maxItems", 20, "items", JsonUtil.Dictionary("type", "string", "pattern", "^[a-z0-9][a-z0-9_-]{1,62}$")) }
                }, new[] { "diagnostico_id" }), false, false, true));
            tools.Add(Tool("demo_inventariar_aplicacion", "Inventaría en la base real módulos, modelos, permisos, adaptadores cerrados y capacidad del registro técnico invisible para una demostración.",
                ObjectSchema(new Dictionary<string, object> { { "entorno", StringEnum(false, "staging", "production") }, { "aplicacion", StringEnum(false, "marketing", "operaciones", "crm", "ventas", "compras", "inventario", "contabilidad", "proyectos", "mantenimiento") } }, new[] { "entorno", "aplicacion" }), true, false, true));
            tools.Add(Tool("demo_inventariar_cobertura_total", "Captura y bloquea el contrato real Odoo 19 de CRM/Ventas, Compras/Inventario y Contabilidad/Finanzas mediante módulos, fields_get, permisos y /doc. No requiere ni procesa el repositorio Enterprise.",
                ObjectSchema(new Dictionary<string, object> { { "entorno", StringEnum(false, "staging", "production") }, { "demo_slug", StringSchema("Slug de la demostración que recibirá instance-contract-lock.json.") } }, new[] { "entorno", "demo_slug" }), true, false, true));
            tools.Add(Tool("demo_preparar_escenario", "Prepara la vista previa schema 4 de una demo basada en registros reales de staging: enumera modelos, cantidades, relaciones, nombres limpios, registro técnico y vacíos sin adaptador; todavía no crea registros.",
                ObjectSchema(new Dictionary<string, object> { { "entorno", StringEnum(false, "staging") }, { "demo_slug", StringSchema("Slug estable de la demostración.") }, { "aplicacion", StringEnum(false, "marketing") }, { "objetivo", StringSchema("Objetivo funcional de la demostración.") }, { "requerimientos", JsonUtil.Dictionary("type", "array", "minItems", 1, "maxItems", 20, "items", JsonUtil.Dictionary("type", "string")) }, { "configuracion", DemoConfigurationSchema() } }, new[] { "entorno", "demo_slug", "aplicacion", "objetivo", "requerimientos" }), false, false, false));
            tools.Add(Tool("demo_preparar_registros_operativos", "Prepara hasta 100 registros sintéticos de una demo general mediante tipos cerrados: terceros, analítica, productos, proyectos, CRM, ventas, compras, facturas, asientos, inventario y mantenimiento. Solo prepara; no confirma, publica, reserva, valida ni paga.",
                ObjectSchema(new Dictionary<string, object> {
                    { "entorno", StringEnum(false, "staging") }, { "demo_slug", StringSchema("Slug estable de la demostración.") },
                    { "objetivo", StringSchema("Objetivo funcional de los registros operativos.") },
                    { "requerimientos", JsonUtil.Dictionary("type", "array", "minItems", 1, "maxItems", 100, "items", JsonUtil.Dictionary("type", "string", "minLength", 1, "maxLength", 400)) },
                    { "registros", JsonUtil.Dictionary("type", "array", "minItems", 1, "maxItems", 100, "items", OperationalDemoRecordSchema()) }
                }, new[] { "entorno", "demo_slug", "objetivo", "requerimientos", "registros" }), false, false, false));
            object demoActions = JsonUtil.Dictionary("type", "array", "minItems", 1, "maxItems", 30, "items", ObjectSchema(new Dictionary<string, object>
            {
                { "clave", JsonUtil.Dictionary("type", "string", "pattern", "^[a-z][a-z0-9_]{1,62}$") },
                { "accion", StringEnum(false, "confirmar_venta", "confirmar_compra", "confirmar_acuerdo_compra", "ganar_oportunidad", "perder_oportunidad", "reservar_transferencia", "validar_transferencia", "contabilizar_movimiento", "contabilizar_pago", "aplicar_inventario", "calcular_costo_destino", "validar_costo_destino", "completar_actividad", "convertir_iniciativa", "perder_oportunidad_con_motivo", "aprobar_comisiones", "crear_anticipo_venta", "crear_factura_final_venta", "cancelar_compra", "crear_factura_proveedor_compra", "crear_nota_credito", "crear_nota_debito", "registrar_pago_factura", "reconciliar_apuntes", "confirmar_presupuesto", "calcular_activo", "validar_activo", "calcular_prestamo", "activar_prestamo") },
                { "componente", JsonUtil.Dictionary("type", "string", "pattern", "^[a-z][a-z0-9_]{1,62}$") },
                { "componentes_relacionados", JsonUtil.Dictionary("type", "array", "maxItems", 19, "uniqueItems", true, "items", JsonUtil.Dictionary("type", "string", "pattern", "^[a-z][a-z0-9_]{1,62}$"), "description", "Solo para reconciliar_apuntes: otros apuntes protegidos del mismo escenario.") },
                { "cancelar_backorder", JsonUtil.Dictionary("type", "boolean") },
                { "esperar_backorder", JsonUtil.Dictionary("type", "boolean") },
                { "depende_de", JsonUtil.Dictionary("type", "array", "maxItems", 10, "items", JsonUtil.Dictionary("type", "string", "pattern", "^[a-z][a-z0-9_]{1,62}$")) },
                { "parametros", ObjectSchema(new Dictionary<string, object> {
                    { "percentage", JsonUtil.Dictionary("type", "number", "exclusiveMinimum", 0, "maximum", 100) }, { "amount", JsonUtil.Dictionary("type", "number", "exclusiveMinimum", 0, "maximum", 1000000000000) },
                    { "date", JsonUtil.Dictionary("type", "string", "pattern", "^[0-9]{4}-[0-9]{2}-[0-9]{2}$") }, { "payment_date", JsonUtil.Dictionary("type", "string", "pattern", "^[0-9]{4}-[0-9]{2}-[0-9]{2}$") },
                    { "reason", JsonUtil.Dictionary("type", "string", "maxLength", 200) }, { "journal_id", JsonUtil.Dictionary("type", "integer", "minimum", 1) }, { "payment_method_line_id", JsonUtil.Dictionary("type", "integer", "minimum", 1) },
                    { "partner_id", JsonUtil.Dictionary("type", "integer", "minimum", 1) }, { "user_id", JsonUtil.Dictionary("type", "integer", "minimum", 1) }, { "team_id", JsonUtil.Dictionary("type", "integer", "minimum", 1) }, { "lost_reason_id", JsonUtil.Dictionary("type", "integer", "minimum", 1) }
                }, new string[0]) }
            }, new[] { "clave", "accion", "componente" }));
            tools.Add(Tool("demo_preparar_acciones", "Prepara un plan cerrado de transiciones funcionales sobre registros sintéticos protegidos: confirmar venta/compra, ganar/perder oportunidad, reservar/validar transferencia o contabilizar. No ejecuta todavía.",
                ObjectSchema(new Dictionary<string, object> { { "escenario_id", StringSchema("ID demo-... ya aplicado.") }, { "acciones", demoActions } }, new[] { "escenario_id", "acciones" }), false, false, true));
            tools.Add(Tool("demo_ejecutar_acciones", "Ejecuta únicamente las acciones de un plan vigente sobre registros protegidos. Exige EJECUTAR-DEMO <id>; una respuesta ambigua nunca se reintenta automáticamente.",
                ObjectSchema(new Dictionary<string, object> { { "plan_id", StringSchema("ID act-... del plan semántico.") }, { "confirmacion", StringSchema("Texto exacto EJECUTAR-DEMO <id>.") } }, new[] { "plan_id", "confirmacion" }), false, true, true));
            tools.Add(Tool("demo_verificar_acciones", "Relee los estados esperados de un plan semántico sin repetir ninguna acción de negocio.",
                ObjectSchema(new Dictionary<string, object> { { "plan_id", StringSchema("ID act-... del plan semántico.") } }, new[] { "plan_id" }), true, false, true));
            tools.Add(Tool("demo_preparar_flujo", "Prepara un flujo funcional schema 2 contra instance-contract-lock: general/transversal, CRM/Ventas, Compras/Inventario o Contabilidad/Finanzas en Odoo 17, 18 o 19. Admite dependencias cerradas, pero todavía no ejecuta.",
                ObjectSchema(new Dictionary<string, object> { { "escenario_id", StringSchema("ID demo-... schema 5 ya aplicado.") }, { "bloque", StringEnum(false, "general", "transversal", "crm_sales", "purchase_inventory", "accounting_finance") }, { "acciones", demoActions } }, new[] { "escenario_id", "acciones" }), false, false, true));
            tools.Add(Tool("demo_ejecutar_flujo", "Ejecuta un único bloque funcional vigente sobre registros protegidos y exige EJECUTAR-DEMO <id>. Nunca activa integraciones externas.",
                ObjectSchema(new Dictionary<string, object> { { "plan_id", StringSchema("ID act-... schema 2.") }, { "confirmacion", StringSchema("Texto exacto EJECUTAR-DEMO <id>.") } }, new[] { "plan_id", "confirmacion" }), false, true, true));
            tools.Add(Tool("demo_verificar_flujo", "Verifica por lectura el bloque funcional y sus resultados sin repetir escrituras.",
                ObjectSchema(new Dictionary<string, object> { { "plan_id", StringSchema("ID act-... schema 2.") } }, new[] { "plan_id" }), true, false, true));
            tools.Add(Tool("demo_preparar_compensacion", "Prepara una compensación segura para un escenario Odoo 19: desactivar, despublicar, cancelar cuando Odoo lo permita o indicar restauración de staging. Nunca elimina.",
                ObjectSchema(new Dictionary<string, object> { { "escenario_id", StringSchema("ID demo-... aplicado.") } }, new[] { "escenario_id" }), false, false, true));
            tools.Add(Tool("demo_ejecutar_compensacion", "Aplica solamente operaciones compensatorias cerradas y exige COMPENSAR-DEMO <id>. No ejecuta unlink ni integraciones externas.",
                ObjectSchema(new Dictionary<string, object> { { "plan_id", StringSchema("ID cmp-... de compensación.") }, { "confirmacion", StringSchema("Texto exacto COMPENSAR-DEMO <id>.") } }, new[] { "plan_id", "confirmacion" }), false, true, true));
            tools.Add(Tool("demo_aplicar_escenario", "Crea los registros sintéticos planificados en staging. Schema 4 recupera solo por propiedad DPAPI/XML ID y bloquea coincidencias de nombre; los escenarios v1-v3 conservan compatibilidad. Exige CREAR-DEMO <id> y nunca activa efectos externos.",
                ObjectSchema(new Dictionary<string, object> { { "escenario_id", StringSchema("ID demo-...") }, { "confirmacion", StringSchema("Texto exacto CREAR-DEMO <id>.") }, { "referencias", JsonUtil.Dictionary("type", "object", "description", "Referencias tipadas solicitadas en la vista previa, por ejemplo WEBSITE_ID.", "additionalProperties", JsonUtil.Dictionary("type", "integer", "minimum", 1)) } }, new[] { "escenario_id", "confirmacion" }), false, false, true));
            tools.Add(Tool("demo_verificar_paso", "Verifica un paso cerrado del escenario o lo identifica como revisión manual de solo lectura.",
                ObjectSchema(new Dictionary<string, object> { { "escenario_id", StringSchema("ID demo-...") }, { "paso", StringEnum(false, "landing", "captacion", "encuesta", "mailing", "automatizacion", "redes", "metricas") } }, new[] { "escenario_id", "paso" }), true, false, true));
            tools.Add(Tool("demo_verificar_escenario", "Comprueba que los registros sintéticos, sus relaciones y los criterios de editor continúan disponibles; solo entonces puede declarar lista la demo y actualizar resultados.md.",
                ObjectSchema(new Dictionary<string, object> { { "escenario_id", StringSchema("ID demo-...") } }, new[] { "escenario_id" }), true, false, true));
            tools.Add(Tool("demo_limpiar_escenario", "Archiva o desactiva exclusivamente registros protegidos por el escenario. Exige LIMPIAR-DEMO <id>; nunca elimina.",
                ObjectSchema(new Dictionary<string, object> { { "escenario_id", StringSchema("ID demo-...") }, { "confirmacion", StringSchema("Texto exacto LIMPIAR-DEMO <id>.") } }, new[] { "escenario_id", "confirmacion" }), false, false, true));
            object demoImage = ObjectSchema(new Dictionary<string, object> { { "file", JsonUtil.Dictionary("type", "string", "pattern", "^[A-Za-z0-9][A-Za-z0-9._-]{0,119}\\.(png|jpg|jpeg|webp)$") }, { "sha256", JsonUtil.Dictionary("type", "string", "pattern", "^[a-f0-9]{64}$") }, { "alt", JsonUtil.Dictionary("type", "string", "minLength", 1, "maxLength", 180) } }, new[] { "file", "sha256", "alt" });
            tools.Add(Tool("demo_preparar_reemplazo_mailing", "Prepara, sin escribir, mailings de reemplazo compatibles con el diseñador Odoo 18 y un recurso visual interno; conserva intactos los originales.",
                ObjectSchema(new Dictionary<string, object> { { "escenario_anterior_id", StringSchema("Escenario aplicado que contiene los mailings defectuosos.") }, { "imagen", demoImage }, { "personalizacion", StringEnum(false, "none", "recipient_name") } }, new[] { "escenario_anterior_id", "imagen", "personalizacion" }), false, false, true));
            tools.Add(Tool("demo_archivar_reemplazados", "Tras verificar database_verified y editor_ready, archiva solo los mailings defectuosos declarados. Exige ARCHIVAR-REEMPLAZO <id>.",
                ObjectSchema(new Dictionary<string, object> { { "escenario_id", StringSchema("ID del escenario de reemplazo.") }, { "confirmacion", StringSchema("Texto exacto ARCHIVAR-REEMPLAZO <id>.") } }, new[] { "escenario_id", "confirmacion" }), false, true, true));
            tools.Add(Tool("demo_restaurar_reemplazados", "Restaura el estado previo protegido de los mailings originales archivados por el reemplazo. Exige RESTAURAR-REEMPLAZO <id>.",
                ObjectSchema(new Dictionary<string, object> { { "escenario_id", StringSchema("ID del escenario de reemplazo.") }, { "confirmacion", StringSchema("Texto exacto RESTAURAR-REEMPLAZO <id>.") } }, new[] { "escenario_id", "confirmacion" }), false, false, true));
            return tools;
        }

        private static object DemoConfigurationSchema()
        {
            object enabled = JsonUtil.Dictionary("type", "boolean");
            object shortText = JsonUtil.Dictionary("type", "string", "minLength", 1, "maxLength", 500);
            object textList = JsonUtil.Dictionary("type", "array", "maxItems", 10, "items", JsonUtil.Dictionary("type", "string", "minLength", 1, "maxLength", 300));
            object question = ObjectSchema(new Dictionary<string, object>
            {
                { "type", StringEnum(false, "simple_choice", "multiple_choice", "text_box", "char_box", "numerical_box", "scale", "date", "datetime", "matrix") },
                { "label", shortText }, { "required", enabled }, { "options", textList }, { "rows", textList }
            }, new[] { "type", "label" });
            return ObjectSchema(new Dictionary<string, object>
            {
                { "utm", ObjectSchema(new Dictionary<string, object> { { "source_name", shortText }, { "medium_name", shortText }, { "campaign_name", shortText }, { "lead_name", shortText }, { "contact_name", shortText } }, new string[0]) },
                { "website", ObjectSchema(new Dictionary<string, object> { { "enabled", enabled }, { "headline", shortText }, { "subtitle", shortText }, { "benefits", textList }, { "cta_label", shortText }, { "legal_text", shortText }, { "layout", StringEnum(false, "hero_form", "split_form") } }, new string[0]) },
                { "survey", ObjectSchema(new Dictionary<string, object> { { "enabled", enabled }, { "title", shortText }, { "description", shortText }, { "questions", JsonUtil.Dictionary("type", "array", "minItems", 1, "maxItems", 10, "items", question) } }, new string[0]) },
                { "mailing", ObjectSchema(new Dictionary<string, object> { { "enabled", enabled }, { "subject_a", shortText }, { "subject_b", shortText }, { "preheader", shortText }, { "headline", shortText }, { "body", shortText }, { "cta_label", shortText }, { "ab_percentage", JsonUtil.Dictionary("type", "integer", "minimum", 1, "maximum", 100) }, { "image_required", enabled }, { "image_asset", ObjectSchema(new Dictionary<string, object> { { "file", JsonUtil.Dictionary("type", "string", "pattern", "^[A-Za-z0-9][A-Za-z0-9._-]{0,119}\\.(png|jpg|jpeg|webp)$") }, { "sha256", JsonUtil.Dictionary("type", "string", "pattern", "^[a-f0-9]{64}$") }, { "alt", JsonUtil.Dictionary("type", "string", "minLength", 1, "maxLength", 180) } }, new[] { "file", "sha256", "alt" }) }, { "personalization_variable", StringEnum(false, "none", "recipient_name") } }, new string[0]) },
                { "automation", ObjectSchema(new Dictionary<string, object> { { "enabled", enabled }, { "title", shortText }, { "delay_days", JsonUtil.Dictionary("type", "integer", "minimum", 0, "maximum", 365) }, { "trigger", StringEnum(false, "begin", "mail_open", "mail_not_open", "mail_click", "mail_not_click") }, { "target_model", StringEnum(false, "crm.lead", "mailing.contact") }, { "audience_filter", StringEnum(false, "all_demo_records", "has_email") }, { "branch_trigger", StringEnum(false, "mail_open", "mail_not_open", "mail_click", "mail_not_click") } }, new string[0]) }
            }, new string[0]);
        }

        private static object OperationalDemoRecordSchema()
        {
            object text = JsonUtil.Dictionary("type", "string", "minLength", 1, "maxLength", 2000);
            object relation = JsonUtil.Dictionary("type", "string", "pattern", "^([a-z][a-z0-9_]{1,62}|runtime:[A-Z][A-Z0-9_]{1,62})$");
            object relations = ObjectSchema(new Dictionary<string, object>
            {
                { "empresa", relation }, { "padre", relation }, { "plan", relation }, { "cliente", relation }, { "proveedor", relation },
                { "cuenta_analitica", relation }, { "proyecto", relation }, { "equipo_ventas", relation }, { "fuente", relation },
                { "medio", relation }, { "campana", relation }, { "oportunidad", relation }, { "documento", relation }, { "producto", relation },
                { "diario", relation }, { "cuenta", relation }, { "tipo_operacion", relation }, { "ubicacion_origen", relation },
                { "ubicacion_destino", relation }, { "equipo", relation }, { "moneda", relation }, { "proyecto_plantilla", relation },
                { "lista_precios", relation }, { "producto_entrega", relation }, { "ubicacion_padre", relation }, { "modelo_vehiculo", relation },
                { "conductor", relation }, { "tipo_actividad", relation }, { "usuario", relation }, { "metodo_pago", relation }, { "lote", relation }, { "factura_proveedor", relation },
                { "categoria_producto", relation }, { "cuenta_valoracion", relation }, { "cuenta_entrada", relation }, { "cuenta_salida", relation }, { "movimiento", relation },
                { "posicion_fiscal", relation }, { "cuenta_origen", relation }, { "cuenta_destino", relation }, { "marca_vehiculo", relation },
                { "plan_comision", relation }, { "presupuesto", relation }, { "modelo_activo", relation }, { "grupo_activo", relation },
                { "cuenta_largo_plazo", relation }, { "cuenta_corto_plazo", relation }, { "cuenta_gasto", relation }, { "categoria_logro", relation }
            }, new string[0]);
            return ObjectSchema(new Dictionary<string, object>
            {
                { "clave", JsonUtil.Dictionary("type", "string", "pattern", "^[a-z][a-z0-9_]{1,62}$") },
                { "tipo", StringEnum(false, "empresa_cliente", "empresa_proveedor", "contacto", "plan_analitico", "cuenta_analitica", "modelo_distribucion_analitica", "categoria_valoracion_inventario", "producto_servicio", "producto_almacenable", "producto_servicio_proyecto", "producto_costo_destino", "tarifa_proveedor", "lista_precios", "regla_lista_precios", "proyecto", "tarea", "oportunidad", "actividad_oportunidad", "motivo_perdida", "cotizacion_venta", "linea_venta", "solicitud_cotizacion_compra", "linea_compra", "acuerdo_compra", "linea_acuerdo_compra", "factura_cliente_borrador", "factura_proveedor_borrador", "nota_credito_cliente_borrador", "nota_credito_proveedor_borrador", "linea_factura", "pago_cliente_borrador", "pago_proveedor_borrador", "movimiento_extracto_bancario", "asiento_contable_borrador", "linea_asiento_debito", "linea_asiento_credito", "diario_contable", "posicion_fiscal", "mapeo_cuenta_posicion_fiscal", "presupuesto_analitico", "linea_presupuesto_analitico", "activo_contable", "prestamo", "linea_prestamo", "plan_comision", "objetivo_comision", "nivel_comision", "logro_comision", "participante_comision", "marca_vehiculo", "modelo_vehiculo", "adjunto_sintetico_privado", "ubicacion_inventario", "lote_con_vencimiento", "existencia_inicial", "metodo_entrega", "vehiculo_flota", "costo_destino_borrador", "linea_costo_destino", "transferencia_inventario_borrador", "movimiento_inventario", "cantidad_ejecutada_movimiento", "equipo_mantenimiento", "solicitud_mantenimiento") },
                { "nombre", text }, { "correo", JsonUtil.Dictionary("type", "string", "pattern", "^[^@\\s]+@example\\.invalid$") },
                { "telefono", JsonUtil.Dictionary("type", "string", "minLength", 1, "maxLength", 80) }, { "ciudad", JsonUtil.Dictionary("type", "string", "minLength", 1, "maxLength", 120) },
                { "direccion", JsonUtil.Dictionary("type", "string", "minLength", 1, "maxLength", 300) }, { "referencia", JsonUtil.Dictionary("type", "string", "minLength", 1, "maxLength", 200) },
                { "codigo", JsonUtil.Dictionary("type", "string", "minLength", 1, "maxLength", 120) },
                { "cantidad", JsonUtil.Dictionary("type", "number", "minimum", 0, "maximum", 1000000000) }, { "precio_unitario", JsonUtil.Dictionary("type", "number", "minimum", 0, "maximum", 1000000000000) },
                { "descuento", JsonUtil.Dictionary("type", "number", "minimum", 0, "maximum", 100) }, { "porcentaje_analitico", JsonUtil.Dictionary("type", "number", "exclusiveMinimum", 0, "maximum", 100) },
                { "porcentaje", JsonUtil.Dictionary("type", "number", "minimum", 0, "maximum", 100000) }, { "valor_residual", JsonUtil.Dictionary("type", "number", "minimum", 0, "maximum", 1000000000000) },
                { "fecha", JsonUtil.Dictionary("type", "string", "minLength", 10, "maxLength", 40) }, { "fecha_limite", JsonUtil.Dictionary("type", "string", "minLength", 10, "maxLength", 40) }, { "fecha_alerta", JsonUtil.Dictionary("type", "string", "minLength", 10, "maxLength", 40) },
                { "descripcion", text }, { "tipo_producto", StringEnum(false, "service", "stockable") }, { "seguimiento", StringEnum(false, "none", "lot", "serial") },
                { "secuencia", JsonUtil.Dictionary("type", "integer", "minimum", 0, "maximum", 9999) }, { "dias", JsonUtil.Dictionary("type", "integer", "minimum", 0, "maximum", 3650) },
                { "tipo_ubicacion", StringEnum(false, "internal", "transit", "inventory") }, { "subtipo", StringEnum(false, "general", "bank", "cash", "credit", "sale", "purchase", "entrada", "salida", "fifo", "average", "equal", "by_quantity", "by_current_cost_price", "by_weight", "by_volume", "target", "achievement", "amount_sold", "amount_invoiced", "quantity_sold", "quantity_invoiced", "margin", "expense", "revenue", "both", "linear", "degressive") },
                { "placa", JsonUtil.Dictionary("type", "string", "minLength", 1, "maxLength", 80) }, { "vin", JsonUtil.Dictionary("type", "string", "minLength", 1, "maxLength", 120) },
                { "servicio_seguimiento", StringEnum(false, "project_only", "task_in_project") },
                { "periodicidad", StringEnum(false, "monthly", "quarterly", "yearly", "month", "quarter", "year") },
                { "archivo", JsonUtil.Dictionary("type", "string", "pattern", "^[A-Za-z0-9][A-Za-z0-9._-]{0,119}\\.(png|jpg|jpeg|webp|pdf)$") },
                { "sha256", JsonUtil.Dictionary("type", "string", "pattern", "^[a-f0-9]{64}$") },
                { "mimetype", StringEnum(false, "image/png", "image/jpeg", "image/webp", "application/pdf") },
                { "relaciones", relations }
            }, new[] { "clave", "tipo" });
        }

        private static object FunctionalObjectiveSchema()
        {
            object requirement = ObjectSchema(new Dictionary<string, object>
            {
                { "key", JsonUtil.Dictionary("type", "string", "pattern", "^[a-z0-9][a-z0-9_-]{1,62}$") },
                { "kind", StringEnum(false, "module", "configuration", "decision") },
                { "module", StringEnum(false, "crm", "sales_team", "sale", "sale_management", "purchase", "purchase_stock", "stock", "stock_account", "account", "account_accountant", "utm", "mass_mailing", "marketing_automation", "social", "website", "website_crm", "website_blog", "website_mass_mailing", "website_sale", "website_sale_comparison", "website_sale_wishlist", "website_sale_stock", "website_sale_loyalty") },
                { "expected_state", StringEnum(false, "installed", "uninstalled") },
                { "adapter", StringEnum(false, "crm_stage", "sales_team", "mailing_list", "website_setting", "website_menu", "website_page", "ecommerce_public_category", "ecommerce_ribbon", "ecommerce_product_tag", "ecommerce_product_publish", "ecommerce_delivery_carrier", "ecommerce_payment_provider") },
                { "match_name", StringSchema("Nombre exacto de la configuración; máximo 200 caracteres.") },
                { "desired_state", StringEnum(false, "present", "inactive") },
                { "desired", JsonUtil.Dictionary("type", "object", "additionalProperties", true) },
                { "question", StringSchema("Decisión funcional que debe tomar el consultor.") },
                { "resolved", JsonUtil.Dictionary("type", "boolean") },
                { "decision", StringSchema("Resultado documentado de la decisión.") }
            }, new[] { "key", "kind" });
            return ObjectSchema(new Dictionary<string, object>
            {
                { "schema_version", JsonUtil.Dictionary("type", "integer", "const", 1) },
                { "application", StringEnum(false, "crm", "ventas", "compras", "inventario", "contabilidad", "marketing", "sitio_web", "ecommerce") },
                { "objective", JsonUtil.Dictionary("type", "string", "minLength", 1, "maxLength", 1000) },
                { "requirements", JsonUtil.Dictionary("type", "array", "minItems", 1, "maxItems", 30, "items", requirement) }
            }, new[] { "schema_version", "application", "objective", "requirements" });
        }

        private static object Tool(string name, string description, object schema, bool readOnly, bool destructive, bool idempotent, bool openWorld = false)
        {
            return JsonUtil.Dictionary(
                "name", name,
                "description", description,
                "inputSchema", schema,
                "annotations", JsonUtil.Dictionary("readOnlyHint", readOnly, "destructiveHint", destructive, "idempotentHint", idempotent, "openWorldHint", openWorld));
        }

        private static object ObjectSchema(IDictionary<string, object> properties, IEnumerable<string> required)
        {
            List<object> requiredValues = new List<object>();
            foreach (string value in required) requiredValues.Add(value);
            return JsonUtil.Dictionary("type", "object", "properties", properties, "required", requiredValues.ToArray(), "additionalProperties", false);
        }

        private static object StringSchema(string description)
        {
            return JsonUtil.Dictionary("type", "string", "description", description);
        }

        private static object StringEnum(bool optional, params string[] values)
        {
            List<object> enumValues = new List<object>();
            foreach (string value in values) enumValues.Add(value);
            return JsonUtil.Dictionary("type", "string", "enum", enumValues.ToArray());
        }
    }
}
