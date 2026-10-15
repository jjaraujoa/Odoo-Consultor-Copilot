using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace OdooConsultor
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0 || Is(args[0], "help") || Is(args[0], "--help") || Is(args[0], "-h"))
                {
                    Help();
                    return 0;
                }
                if (Is(args[0], "version"))
                {
                    Console.WriteLine(ProductInfo.Banner);
                    return 0;
                }
                if (Is(args[0], "selftest")) return SelfTest.Run();
                if (Is(args[0], "proyecto")) return ProjectCommand(args.Skip(1).ToArray());
                if (Is(args[0], "conexion")) return ConnectionCommand(args.Skip(1).ToArray());
                if (Is(args[0], "mcp"))
                {
                    Dictionary<string, string> options = ParseOptions(args.Skip(1).ToArray());
                    ProjectStore project = new ProjectStore(RequiredOption(options, "proyecto"));
                    return new McpServer(project).Run();
                }
                throw new ConsultantException("unknown_command", "Comando desconocido. Ejecuta OdooConsultor.ps1 help.");
            }
            catch (ConsultantException error)
            {
                Console.Error.WriteLine(error.Code + ": " + Redactor.Clean(error.Message));
                return 2;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("internal_error: " + Redactor.Clean(error.Message));
                return 1;
            }
        }

        private static int ProjectCommand(string[] args)
        {
            if (args.Length == 0) throw new ConsultantException("invalid_command", "Usa proyecto crear o proyecto actualizar.");
            string action = args[0].ToLowerInvariant();
            Dictionary<string, string> options = ParseOptions(args.Skip(1).ToArray());
            if (action == "actualizar")
            {
                string rootToUpgrade = RequiredOption(options, "proyecto");
                Dictionary<string, string> settings = new Dictionary<string, string>(options, StringComparer.OrdinalIgnoreCase);
                settings.Remove("proyecto");
                ProjectStore upgraded = ProjectStore.Upgrade(rootToUpgrade, Assembly.GetExecutingAssembly().Location, settings);
                Console.WriteLine(JsonUtil.SerializePretty(JsonUtil.Dictionary(
                    "updated", true, "project_root", upgraded.Root, "schema_version", upgraded.Config.schema_version,
                    "odoo_version", upgraded.Config.odoo_version, "source_profile", upgraded.Config.source_profile,
                    "enterprise_source_processing", upgraded.Config.enterprise_source_processing,
                    "client_source_processing", upgraded.Config.client_source_processing,
                    "next_step", "Reinicia Codex para que el proyecto use el MCP actualizado.")));
                return 0;
            }
            if (action != "crear") throw new ConsultantException("invalid_command", "Usa proyecto crear o proyecto actualizar.");
            string root = RequiredOption(options, "ruta");
            ProjectStore project = ProjectStore.Create(root, RequiredOption(options, "slug"), RequiredOption(options, "nombre"), Assembly.GetExecutingAssembly().Location,
                options.ContainsKey("version") ? options["version"] : "19.0",
                options.ContainsKey("edicion") ? options["edicion"] : "enterprise",
                options.ContainsKey("alojamiento") ? options["alojamiento"] : "odoo_sh");
            Console.WriteLine(JsonUtil.SerializePretty(JsonUtil.Dictionary(
                "created", true,
                "project_root", project.Root,
                "client_slug", project.Config.client_slug,
                "odoo_version", project.Config.odoo_version,
                "next_step", project.Config.odoo_version != "19.0" ? "Ejecuta conexion agregar e incluye --base <nombre_tecnico_bd>." : "Ejecuta conexion agregar --proyecto <ruta> --entorno staging --url <https://...> --login <usuario>.")));
            return 0;
        }

        private static int ConnectionCommand(string[] args)
        {
            if (args.Length == 0) throw new ConsultantException("invalid_command", "Usa conexion agregar, listar o quitar.");
            string action = args[0].ToLowerInvariant();
            Dictionary<string, string> options = ParseOptions(args.Skip(1).ToArray());
            ProjectStore project = new ProjectStore(RequiredOption(options, "proyecto"));
            if (action == "listar")
            {
                List<object> profiles = new List<object>();
                foreach (ConnectionProfile profile in project.ListProfiles())
                {
                    profiles.Add(JsonUtil.Dictionary(
                        "profile_type", "primary", "environment", profile.environment, "base_url", profile.base_url, "database", profile.database,
                        "login", profile.login, "uid", profile.uid, "verified_at", profile.verified_at,
                        "server_version", profile.server_version, "server_series", profile.server_series,
                        "api_transport", profile.api_transport, "read_only", profile.read_only,
                        "credential_available", CredentialStore.Exists(profile.credential_target), "credential_fingerprint", profile.credential_fingerprint));
                }
                foreach (ConnectionProfile profile in project.ListVerificationProfiles())
                {
                    profiles.Add(JsonUtil.Dictionary(
                        "profile_type", "verification", "alias", profile.profile_alias, "environment", profile.environment,
                        "base_url", profile.base_url, "database", profile.database, "login", profile.login, "uid", profile.uid,
                        "verified_at", profile.verified_at, "server_version", profile.server_version, "server_series", profile.server_series,
                        "api_transport", profile.api_transport, "read_only", profile.read_only,
                        "credential_available", CredentialStore.Exists(profile.credential_target), "credential_fingerprint", profile.credential_fingerprint));
                }
                Console.WriteLine(JsonUtil.SerializePretty(JsonUtil.Dictionary("client_slug", project.Config.client_slug, "profiles", profiles.ToArray())));
                return 0;
            }
            if (action == "quitar")
            {
                string environment = ProjectStore.NormalizeEnvironment(RequiredOption(options, "entorno"));
                ConnectionProfile profile = project.GetProfile(environment, false);
                if (profile != null)
                {
                    CredentialStore.Delete(profile.credential_target);
                    project.DeleteProfile(environment);
                }
                AuditLog.Append(project, environment, "conexion_quitar", null, "success", "Credencial revocada y perfil local retirado.", 0);
                Console.WriteLine(JsonUtil.SerializePretty(JsonUtil.Dictionary("revoked", profile != null, "environment", environment)));
                return 0;
            }
            if (action == "quitar-prueba")
            {
                string alias = ProjectStore.NormalizeProfileAlias(RequiredOption(options, "alias"));
                ConnectionProfile profile = project.GetVerificationProfile(alias, false);
                if (profile != null) { CredentialStore.Delete(profile.credential_target); project.DeleteVerificationProfile(alias); }
                AuditLog.Append(project, "staging", "conexion_quitar_prueba", null, "success", "Credencial de verificación revocada; alias_hash=" + JsonUtil.Sha256(alias) + ".", 0);
                Console.WriteLine(JsonUtil.SerializePretty(JsonUtil.Dictionary("revoked", profile != null, "profile_type", "verification", "alias", alias)));
                return 0;
            }
            if (action == "agregar") return AddConnection(project, options, false);
            if (action == "agregar-prueba") return AddConnection(project, options, true);
            throw new ConsultantException("invalid_command", "Usa conexion agregar, agregar-prueba, listar, quitar o quitar-prueba.");
        }

        private static int AddConnection(ProjectStore project, IDictionary<string, string> options, bool verification)
        {
            string environment = verification ? "staging" : ProjectStore.NormalizeEnvironment(RequiredOption(options, "entorno"));
            string profileAlias = verification ? ProjectStore.NormalizeProfileAlias(RequiredOption(options, "alias")) : null;
            string baseUrl = OdooClient.NormalizeBaseUrl(RequiredOption(options, "url"));
            string login = RequiredOption(options, "login");
            string database = options.ContainsKey("base") ? options["base"] : null;
            Dictionary<string, object> detected = OdooClient.DetectInstance(baseUrl, 15000);
            string serverSeries = Convert.ToString(detected["server_series"], CultureInfo.InvariantCulture);
            string transport = Convert.ToString(detected["transport"], CultureInfo.InvariantCulture);
            if (transport == "unsupported" || string.IsNullOrWhiteSpace(serverSeries)) throw new ConsultantException("unsupported_odoo_version", "Solo se admiten versiones de Odoo de la 14 a la 19.");
            if (!string.Equals(serverSeries, project.Config.odoo_version, StringComparison.Ordinal)) throw new ConsultantException("odoo_version_mismatch", "La base es Odoo " + serverSeries + " pero proyecto.yaml declara " + project.Config.odoo_version + ". Ejecuta proyecto actualizar con la versión correcta.");
            if (transport == "legacy_rpc" && string.IsNullOrWhiteSpace(database)) throw new ConsultantException("database_required", "Para versiones con protocolo RPC heredado (14 a 18) debes indicar --base con el nombre técnico de la base.");
            string secret = SecretPrompt.Show("OdooConsultor — " + project.Config.client_name, "API key individual para " + login + " en " + environment + ". No se mostrará ni se guardará en archivos.");
            if (string.IsNullOrWhiteSpace(secret)) throw new ConsultantException("credential_cancelled", "No se guardó ninguna credencial.");
            string target = "OdooAI/" + project.Config.client_slug + "/" + (verification ? "verification/" + profileAlias : environment) + "/" + JsonUtil.Sha256(login.ToLowerInvariant()).Substring(0, 16);
            ConnectionProfile previous = verification ? project.GetVerificationProfile(profileAlias, false) : project.GetProfile(environment, false);
            ConnectionProfile candidate = new ConnectionProfile
            {
                schema_version = 2,
                client_slug = project.Config.client_slug,
                environment = environment,
                credential_target = target,
                base_url = baseUrl,
                database = string.IsNullOrWhiteSpace(database) ? null : database.Trim(),
                login = login.Trim(),
                server_version = Convert.ToString(detected["server_version"], CultureInfo.InvariantCulture),
                server_series = serverSeries,
                api_transport = transport,
                read_only = environment == "production",
                profile_alias = profileAlias,
                credential_fingerprint = JsonUtil.Sha256(secret)
            };
            try
            {
                using (OdooClient client = new OdooClient(candidate, secret, 15000))
                {
                    Dictionary<string, object> context = client.ContextGet();
                    if (!context.ContainsKey("uid")) throw new ConsultantException("invalid_odoo_identity", "Odoo no devolvió uid en context_get.");
                    candidate.uid = Convert.ToInt64(context["uid"], CultureInfo.InvariantCulture);
                    IList<Dictionary<string, object>> users = client.Read("res.users", new[] { candidate.uid }, new[] { "login", "name" });
                    if (users.Count != 1) throw new ConsultantException("invalid_odoo_identity", "Odoo no devolvió el usuario autenticado.");
                    string actualLogin = Convert.ToString(users[0]["login"], CultureInfo.InvariantCulture);
                    if (!string.Equals(actualLogin, candidate.login, StringComparison.OrdinalIgnoreCase)) throw new ConsultantException("odoo_login_mismatch", "La API key pertenece a otro login de Odoo.");
                    candidate.user_name = Convert.ToString(users[0]["name"], CultureInfo.InvariantCulture);
                    candidate.verified_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                }
                CredentialStore.Write(target, candidate.login, secret);
                try { if (verification) project.SaveVerificationProfile(candidate); else project.SaveProfile(candidate); }
                catch { CredentialStore.Delete(target); throw; }
                if (previous != null && !string.Equals(previous.credential_target, target, StringComparison.Ordinal)) CredentialStore.Delete(previous.credential_target);
                AuditLog.Append(project, environment, "conexion_agregar", null, "success", "Perfil " + candidate.api_transport + " validado para UID " + candidate.uid + ".", 0);
                Console.WriteLine(JsonUtil.SerializePretty(JsonUtil.Dictionary(
                    "verified", true,
                    "profile_type", verification ? "verification" : "primary",
                    "alias", profileAlias,
                    "environment", environment,
                    "login", candidate.login,
                    "uid", candidate.uid,
                    "user_name", candidate.user_name,
                    "server_version", candidate.server_version,
                    "api_transport", candidate.api_transport,
                    "read_only", candidate.read_only,
                    "credential_fingerprint", candidate.credential_fingerprint)));
                return 0;
            }
            finally
            {
                secret = null;
            }
        }

        private static Dictionary<string, string> ParseOptions(string[] args)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                string current = args[i];
                if (!current.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length) throw new ConsultantException("invalid_option", "Opción inválida: " + current);
                string key = current.Substring(2);
                string value = args[++i];
                if (value.StartsWith("--", StringComparison.Ordinal)) throw new ConsultantException("invalid_option", "Falta valor para --" + key + ".");
                result[key] = value;
            }
            return result;
        }

        private static string RequiredOption(IDictionary<string, string> options, string key)
        {
            string value;
            if (!options.TryGetValue(key, out value) || string.IsNullOrWhiteSpace(value)) throw new ConsultantException("missing_option", "Falta --" + key + ".");
            return value.Trim();
        }

        private static bool Is(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static void Help()
        {
            Console.WriteLine(ProductInfo.Banner);
            Console.WriteLine();
            Console.WriteLine("Crear proyecto:");
            Console.WriteLine("  OdooConsultor.ps1 proyecto crear --ruta <carpeta> --slug <cliente> --nombre <nombre> [--version <14.0|15.0|16.0|17.0|18.0|19.0>] [--edicion <community|enterprise>] [--alojamiento <odoo_sh|online|on_premise>]");
            Console.WriteLine("  OdooConsultor.ps1 proyecto actualizar --proyecto <carpeta> [--version <14.0|15.0|16.0|17.0|18.0|19.0>] [--procesamiento-privado <blocked|authorized>]");
            Console.WriteLine();
            Console.WriteLine("Registrar API key de forma segura:");
            Console.WriteLine("  OdooConsultor.ps1 conexion agregar --proyecto <carpeta> --entorno staging --url <https://...> --login <usuario> [--base <bd>]");
            Console.WriteLine("  OdooConsultor.ps1 conexion agregar-prueba --proyecto <carpeta> --alias <rol> --url <https://...> --login <usuario> [--base <bd>]");
            Console.WriteLine("  OdooConsultor.ps1 conexion listar --proyecto <carpeta>");
            Console.WriteLine("  OdooConsultor.ps1 conexion quitar --proyecto <carpeta> --entorno <staging|production>");
            Console.WriteLine("  OdooConsultor.ps1 conexion quitar-prueba --proyecto <carpeta> --alias <rol>");
            Console.WriteLine();
            Console.WriteLine("Servidor para Codex:");
            Console.WriteLine("  OdooConsultor.ps1 mcp --proyecto <carpeta>");
            Console.WriteLine();
            Console.WriteLine("La API key nunca se acepta como argumento ni se escribe en archivos.");
        }
    }

    internal static class SelfTest
    {
        public static int Run()
        {
            int passed = 0;
            Assert(Redactor.Clean("Authorization: Bearer supersecreto") == "Authorization: Bearer [REDACTADO]", "redacción bearer");
            passed++;
            string first = JsonUtil.Canonicalize(JsonUtil.Dictionary("b", 2, "a", 1));
            string second = JsonUtil.Canonicalize(JsonUtil.Dictionary("a", 1, "b", 2));
            Assert(first == second, "JSON canónico");
            passed++;
            string healedSingle = JsonUtil.SanitizeMojibake("C\u00C3\u00A1mara de Comercio");
            string healedDouble = JsonUtil.SanitizeMojibake("Cotizaci\u00C3\u0083\u00C2\u00B3n");
            Assert(healedSingle == "C\u00E1mara de Comercio" && healedDouble == "Cotizaci\u00F3n", "saneamiento automático de mojibake");
            passed++;
            string escapedJson = JsonUtil.Serialize(JsonUtil.Dictionary("texto", "Cotizaci\u00F3n"));
            Assert(escapedJson.Contains("\\u00f3") && !escapedJson.Contains("\u00F3"), "escape de caracteres no-ASCII en JSON");
            passed++;
            string xmlWithEntities = JsonUtil.XmlNumericEntities("<field string=\"Cotizaci\u00F3n\" />");
            Assert(xmlWithEntities.Contains("&#243;") && !xmlWithEntities.Contains("\u00F3"), "entidades numéricas XML para vistas");
            passed++;
            bool blocked = false;
            try { StudioService.EnsureWriteAllowed("production"); }
            catch (ConsultantException error) { blocked = error.Code == "production_write_blocked"; }
            Assert(blocked, "bloqueo de producción");
            passed++;
            SelfTestMock.VerifyJson2Client();
            passed++;
            SelfTestMock.VerifyLegacyRpcClient();
            passed++;
            passed += SelfTestMock.VerifyDiscoverDatabase();
            passed += DemoAdapterCatalog.SelfTest();
            passed += MailEditorContract.SelfTest();
            Assert(DemoService.SupportsSchemaForSelfTest(1) && DemoService.SupportsSchemaForSelfTest(2) && DemoService.SupportsSchemaForSelfTest(3) && DemoService.SupportsSchemaForSelfTest(4) && DemoService.SupportsSchemaForSelfTest(5) && !DemoService.SupportsSchemaForSelfTest(6), "compatibilidad de escenarios demo v1/v2/v3/v4/v5");
            passed++;
            DemoScenario legacyDemo = new DemoScenario { schema_version = 2, id = "demo-legacy", components = new List<DemoComponent> { new DemoComponent { key = "mailing_a", adapter = "mailing_draft", values = JsonUtil.Dictionary("state", "draft") } }, warnings = new List<string>() };
            DemoService.SignLegacyForSelfTest(legacyDemo);
            Assert(DemoService.IntegrityValidForSelfTest(legacyDemo), "integridad heredada permite preparar reemplazo desde escenarios v2");
            passed++;
            DemoScenario legacyV3Demo = new DemoScenario { schema_version = 3, id = "demo-legacy-v3", components = new List<DemoComponent> { new DemoComponent { key = "mailing_a", adapter = "mailing_draft", values = JsonUtil.Dictionary("state", "draft"), editor_contract = MailEditorContract.ContractVersion } }, warnings = new List<string>(), replacement_state = "prepared" };
            DemoService.SignLegacyV3ForSelfTest(legacyV3Demo);
            Assert(DemoService.IntegrityValidForSelfTest(legacyV3Demo), "integridad heredada conserva escenarios v3 sin migrarlos");
            passed++;
            Assert(DemoService.UsesSurveyParentWriteForSelfTest("17.0", "survey_question") && DemoService.UsesSurveyParentWriteForSelfTest("18.0", "survey_question") && !DemoService.UsesSurveyParentWriteForSelfTest("19.0", "survey_question") && !DemoService.UsesSurveyParentWriteForSelfTest("18.0", "survey_answer"), "creación nativa de preguntas Survey por encuesta padre en Odoo 17/18");
            passed++;
            Assert(DemoService.EquivalentFieldValueForSelfTest(0.0m, 0, "float") && DemoService.EquivalentFieldValueForSelfTest(2L, 2, "integer") && !DemoService.EquivalentFieldValueForSelfTest(1.0m, 0, "float"), "comparación semántica de campos numéricos en verificación demo");
            passed++;
            Dictionary<string, object> activityCompatibility = DemoService.MarketingActivityCompatibilityForSelfTest();
            Assert(Convert.ToInt64(activityCompatibility["source_id"], CultureInfo.InvariantCulture) == 77L && Convert.ToString(activityCompatibility["validity_duration_type"], CultureInfo.InvariantCulture) == "days", "campos técnicos obligatorios de Marketing Automation Odoo 18");
            passed++;
            Assert(DemoService.EscapeForSelfTest("<script>alert('x')</script>").IndexOf("<script>", StringComparison.OrdinalIgnoreCase) < 0, "escape de contenido comercial");
            passed++;
            DemoComponent analyticAccount = DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary(
                "clave", "pozo_horizonte", "tipo", "cuenta_analitica", "nombre", "Pozo Horizonte-01", "codigo", "H01",
                "relaciones", JsonUtil.Dictionary("plan", "municipio_yopal")));
            Assert(analyticAccount.adapter == "analytic_account" && analyticAccount.references["plan_id"] == "municipio_yopal", "compilación cerrada de cuenta analítica");
            passed++;
            DemoComponent saleLine = DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary(
                "clave", "linea_servicio", "tipo", "linea_venta", "nombre", "Servicio de levantamiento", "cantidad", 2, "precio_unitario", 1500000,
                "porcentaje_analitico", 100, "relaciones", JsonUtil.Dictionary("documento", "cotizacion_horizonte", "producto", "servicio_levantamiento", "cuenta_analitica", "pozo_horizonte")));
            Assert(saleLine.adapter == "sale_order_line" && saleLine.references["analytic_distribution"] == "analytic:pozo_horizonte:100", "línea de venta con distribución analítica cerrada");
            passed++;
            DemoComponent invoice = DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary(
                "clave", "factura_cliente", "tipo", "factura_cliente_borrador", "fecha", "2026-08-30", "referencia", "SERV-HORIZONTE-01",
                "relaciones", JsonUtil.Dictionary("cliente", "operadora_horizonte")));
            Assert(invoice.adapter == "account_invoice_draft" && Convert.ToString(invoice.values["move_type"], CultureInfo.InvariantCulture) == "out_invoice", "factura de cliente fijada en borrador por adaptador");
            passed++;
            bool arbitraryOperationalTypeBlocked = false;
            try { DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary("clave", "libre", "tipo", "modelo_arbitrario", "nombre", "No permitido", "relaciones", JsonUtil.Dictionary())); }
            catch (ConsultantException error) { arbitraryOperationalTypeBlocked = error.Code == "demo_operational_type_not_allowed"; }
            Assert(arbitraryOperationalTypeBlocked, "tipo operativo arbitrario bloqueado");
            passed++;
            bool unsafeOperationalEmailBlocked = false;
            try { DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary("clave", "cliente_real", "tipo", "empresa_cliente", "nombre", "Cliente", "correo", "persona@example.com", "relaciones", JsonUtil.Dictionary())); }
            catch (ConsultantException error) { unsafeOperationalEmailBlocked = error.Code == "demo_email_not_safe"; }
            Assert(unsafeOperationalEmailBlocked, "correo real bloqueado en demo operativa");
            passed++;
            Assert(DemoActionService.ActionAllowedForSelfTest("confirmar_venta") && DemoActionService.ActionAllowedForSelfTest("contabilizar_movimiento") && !DemoActionService.ActionAllowedForSelfTest("metodo_arbitrario"), "catálogo cerrado de acciones semánticas");
            passed++;
            Assert(DemoActionService.AdapterAllowedForSelfTest("confirmar_venta", "sale_quotation") && !DemoActionService.AdapterAllowedForSelfTest("confirmar_venta", "purchase_rfq"), "acción semántica ligada al adaptador protegido");
            passed++;
            Assert(DemoActionService.ExpectedForSelfTest("confirmar_compra", JsonUtil.Dictionary("state", "purchase")) && DemoActionService.ExpectedForSelfTest("confirmar_compra", JsonUtil.Dictionary("state", "done")) && !DemoActionService.ExpectedForSelfTest("confirmar_compra", JsonUtil.Dictionary("state", "draft")), "verificación cerrada de compra confirmada");
            passed++;
            Assert(DemoActionService.ExpectedForSelfTest("ganar_oportunidad", JsonUtil.Dictionary("active", true, "probability", 100)) && DemoActionService.ExpectedForSelfTest("perder_oportunidad", JsonUtil.Dictionary("active", false, "probability", 0)), "verificación cerrada de oportunidad ganada o perdida");
            passed++;
            Assert(DemoActionService.ConfirmationForSelfTest("act-123") == "EJECUTAR-DEMO act-123", "confirmación exacta separada para acciones de demo");
            passed++;
            DemoComponent distributionRule = DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary("clave", "dist_horizonte", "tipo", "modelo_distribucion_analitica", "porcentaje_analitico", 100, "relaciones", JsonUtil.Dictionary("cuenta_analitica", "pozo_horizonte", "cliente", "operadora_horizonte")));
            Assert(distributionRule.adapter == "analytic_distribution_rule" && distributionRule.references["analytic_distribution"] == "analytic:pozo_horizonte:100", "modelo cerrado de distribución analítica");
            passed++;
            DemoComponent supplierPrice = DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary("clave", "tarifa_conector", "tipo", "tarifa_proveedor", "nombre", "Conector proveedor", "codigo", "CON-287", "precio_unitario", 450000, "dias", 15, "relaciones", JsonUtil.Dictionary("proveedor", "mecanizados_andinos", "producto", "conector_287")));
            Assert(supplierPrice.adapter == "supplier_price" && supplierPrice.references["product_id"] == "conector_287", "tarifa cerrada de proveedor");
            passed++;
            DemoComponent location = DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary("clave", "cuarentena", "tipo", "ubicacion_inventario", "nombre", "Cuarentena", "tipo_ubicacion", "internal", "relaciones", JsonUtil.Dictionary("ubicacion_padre", "bodega_funza")));
            Assert(location.adapter == "stock_location" && location.references["location_id"] == "bodega_funza", "ubicación logística cerrada");
            passed++;
            DemoComponent lot = DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary("clave", "lote_sellador", "tipo", "lote_con_vencimiento", "codigo", "SELL-2601", "fecha_limite", "2026-12-31T00:00:00Z", "fecha_alerta", "2026-12-15T00:00:00Z", "relaciones", JsonUtil.Dictionary("producto", "sellador_quimico")));
            Assert(lot.adapter == "stock_lot_expiring" && lot.references["product_id"] == "sellador_quimico", "lote cerrado con caducidad");
            passed++;
            DemoComponent carrier = DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary("clave", "transporte_propio", "tipo", "metodo_entrega", "nombre", "Transporte propio Demo", "precio_unitario", 120000, "relaciones", JsonUtil.Dictionary("producto_entrega", "flete_nacional")));
            Assert(carrier.adapter == "delivery_method" && Convert.ToString(carrier.values["delivery_type"], CultureInfo.InvariantCulture) == "fixed", "método de entrega cerrado");
            passed++;
            DemoComponent activity = DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary("clave", "llamada_horizonte", "tipo", "actividad_oportunidad", "nombre", "Llamada de seguimiento", "fecha_limite", "2026-09-15", "relaciones", JsonUtil.Dictionary("oportunidad", "opp_horizonte", "tipo_actividad", "runtime:ACTIVITY_CALL", "usuario", "runtime:DEMO_USER")));
            Assert(activity.adapter == "crm_activity" && activity.references["res_model_id"] == "lookup:model:crm.lead", "actividad CRM ligada a oportunidad");
            passed++;
            DemoComponent agreement = DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary("clave", "acuerdo_componentes", "tipo", "acuerdo_compra", "referencia", "Suministro de componentes — Horizonte-01", "relaciones", JsonUtil.Dictionary("proveedor", "mecanizados_andinos")));
            Assert(agreement.adapter == "purchase_agreement" && Convert.ToString(agreement.values["requisition_type"], CultureInfo.InvariantCulture) == "blanket_order", "acuerdo de compra nativo cerrado");
            passed++;
            DemoComponent serviceProject = DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary("clave", "servicio_optimizacion", "tipo", "producto_servicio_proyecto", "nombre", "Servicio de optimización", "codigo", "SRV-OPT", "servicio_seguimiento", "project_only", "relaciones", JsonUtil.Dictionary()));
            Assert(serviceProject.adapter == "demo_service_project_product" && Convert.ToString(serviceProject.values["service_tracking"], CultureInfo.InvariantCulture) == "project_only", "servicio que genera proyecto");
            passed++;
            Assert(DemoActionService.ActionAllowedForSelfTest("confirmar_acuerdo_compra") && DemoActionService.AdapterAllowedForSelfTest("confirmar_acuerdo_compra", "purchase_agreement"), "confirmación cerrada de acuerdo de compra");
            passed++;
            DemoComponent refund = DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary("clave", "nota_credito", "tipo", "nota_credito_cliente_borrador", "fecha", "2026-09-01", "referencia", "NC-H01", "relaciones", JsonUtil.Dictionary("cliente", "operadora_horizonte")));
            Assert(refund.adapter == "account_invoice_draft" && Convert.ToString(refund.values["move_type"], CultureInfo.InvariantCulture) == "out_refund", "nota crédito de cliente en borrador");
            passed++;
            DemoComponent payment = DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary("clave", "pago_horizonte", "tipo", "pago_cliente_borrador", "nombre", "Pago factura Horizonte", "fecha", "2026-09-02", "referencia", "PAGO-H01", "precio_unitario", 1250000, "relaciones", JsonUtil.Dictionary("cliente", "operadora_horizonte", "diario", "runtime:BANK_JOURNAL")));
            Assert(payment.adapter == "account_payment_draft" && Convert.ToString(payment.values["payment_type"], CultureInfo.InvariantCulture) == "inbound", "pago de cliente en borrador");
            passed++;
            DemoComponent bankLine = DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary("clave", "extracto_pago", "tipo", "movimiento_extracto_bancario", "fecha", "2026-09-02", "referencia", "Transferencia recibida", "precio_unitario", 1250000, "subtipo", "entrada", "relaciones", JsonUtil.Dictionary("diario", "runtime:BANK_JOURNAL", "cliente", "operadora_horizonte")));
            Assert(bankLine.adapter == "bank_statement_line" && Convert.ToDecimal(bankLine.values["amount"], CultureInfo.InvariantCulture) > 0m, "movimiento sintético de extracto");
            passed++;
            DemoComponent quant = DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary("clave", "stock_sellador", "tipo", "existencia_inicial", "cantidad", 25, "relaciones", JsonUtil.Dictionary("producto", "sellador_quimico", "ubicacion_origen", "bodega_funza", "lote", "lote_sellador")));
            Assert(quant.adapter == "stock_inventory_quant" && quant.references["lot_id"] == "lote_sellador", "existencia inicial trazable");
            passed++;
            DemoComponent landed = DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary("clave", "costo_importacion", "tipo", "costo_destino_borrador", "fecha", "2026-09-03", "descripcion", "Flete e importación", "relaciones", JsonUtil.Dictionary("documento", "recepcion_importacion", "diario", "runtime:STOCK_JOURNAL")));
            Assert(landed.adapter == "stock_landed_cost" && landed.references["picking_ids"] == "recepcion_importacion", "costo en destino en borrador");
            passed++;
            DemoComponent landedLine = DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary("clave", "linea_flete", "tipo", "linea_costo_destino", "nombre", "Flete internacional", "precio_unitario", 800000, "subtipo", "by_quantity", "relaciones", JsonUtil.Dictionary("documento", "costo_importacion", "producto", "flete_importacion")));
            Assert(landedLine.adapter == "stock_landed_cost_line" && Convert.ToString(landedLine.values["split_method"], CultureInfo.InvariantCulture) == "by_quantity", "línea de costo en destino");
            passed++;
            DemoComponent valuationCategory = DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary("clave", "categoria_fifo", "tipo", "categoria_valoracion_inventario", "nombre", "Componentes importados", "subtipo", "fifo", "relaciones", JsonUtil.Dictionary()));
            Assert(valuationCategory.adapter == "valuation_product_category" && Convert.ToString(valuationCategory.values["property_valuation"], CultureInfo.InvariantCulture) == "real_time", "categoría FIFO con valoración automática");
            passed++;
            DemoComponent landedProduct = DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary("clave", "flete_importacion", "tipo", "producto_costo_destino", "nombre", "Flete de importación", "codigo", "FLETE-IMP", "subtipo", "by_quantity", "relaciones", JsonUtil.Dictionary()));
            Assert(landedProduct.adapter == "landed_cost_product" && Convert.ToBoolean(landedProduct.values["landed_cost_ok"], CultureInfo.InvariantCulture), "producto de costo en destino");
            passed++;
            Assert(DemoActionService.ExpectedForSelfTest("contabilizar_pago", JsonUtil.Dictionary("state", "in_process")) && DemoActionService.ExpectedForSelfTest("contabilizar_pago", JsonUtil.Dictionary("state", "paid")), "verificación de pago contabilizado");
            passed++;
            Assert(DemoActionService.ExpectedForSelfTest("aplicar_inventario", JsonUtil.Dictionary("quantity", 25, "inventory_quantity_set", false)) && DemoActionService.ExpectedForSelfTest("calcular_costo_destino", JsonUtil.Dictionary("state", "draft", "valuation_adjustment_lines", new object[] { 10L })), "verificación de inventario y costo calculado");
            passed++;
            DemoComponent doneMove = DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary("clave", "cantidad_parcial", "tipo", "cantidad_ejecutada_movimiento", "cantidad", 4, "relaciones", JsonUtil.Dictionary("documento", "entrega_horizonte", "movimiento", "movimiento_sellador", "producto", "sellador_quimico", "ubicacion_origen", "bodega_funza", "ubicacion_destino", "cliente_horizonte", "lote", "lote_sellador")));
            Assert(doneMove.adapter == "stock_move_line_done" && doneMove.references["move_id"] == "movimiento_sellador" && Convert.ToDecimal(doneMove.values["quantity"], CultureInfo.InvariantCulture) == 4m, "cantidad ejecutada para entrega parcial");
            passed++;
            Assert(DemoActionService.ExpectedBackorderForSelfTest(JsonUtil.Dictionary("state", "done", "backorder_ids", new object[] { 91L })) && !DemoActionService.ExpectedBackorderForSelfTest(JsonUtil.Dictionary("state", "done", "backorder_ids", new object[0])), "verificación explícita de backorder creado");
            passed++;
            Assert(InstanceContractService.ContainsModelForSelfTest("sale.commission.plan") && InstanceContractService.ContainsModelForSelfTest("budget.analytic") && InstanceContractService.ContainsModelForSelfTest("account.asset") && InstanceContractService.ContainsModelForSelfTest("account.loan"), "contrato de instancia cubre comisiones, presupuestos, activos y préstamos");
            passed++;
            Assert(InstanceContractService.ModuleForSelfTest("account.loan") == "account_loans" && InstanceContractService.ContainsMethodForSelfTest("loan.validate") && InstanceContractService.MethodPathForSelfTest("account.reconcile") == "account.move.line/reconcile", "contratos Odoo 19 corregidos sin depender de código Enterprise");
            passed++;
            Assert(DemoActionService.ActionAllowedForSelfTest("convertir_iniciativa") && DemoActionService.ActionAllowedForSelfTest("perder_oportunidad_con_motivo") && DemoActionService.RelatedComponentsAllowedForSelfTest("reconciliar_apuntes", 1) && !DemoActionService.RelatedComponentsAllowedForSelfTest("reconciliar_apuntes", 0) && !DemoActionService.RelatedComponentsAllowedForSelfTest("confirmar_venta", 1), "flujos avanzados cerrados y conciliación multiparte protegida");
            passed++;
            Assert(DemoActionService.BlockAllowedForSelfTest("general") && DemoActionService.BlockAllowedForSelfTest("transversal") && DemoActionService.BlockAllowedForSelfTest("crm_sales") && !DemoActionService.BlockAllowedForSelfTest("invalido"), "bloques de flujo schema 2 admiten general y transversal");
            passed++;
            Assert(DemoActionService.ActionVersionAllowedForSelfTest("17.0") && DemoActionService.ActionVersionAllowedForSelfTest("18.0") && DemoActionService.ActionVersionAllowedForSelfTest("19.0") && !DemoActionService.ActionVersionAllowedForSelfTest("16.0"), "soporte multi-versión para acciones y flujos cubre Odoo 17, 18 y 19");
            passed++;
            DemoAdapterContract valCatTest = DemoAdapterCatalog.Get("valuation_product_category");
            Dictionary<string, object> testFields = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "name", new Dictionary<string, object>() },
                { "property_cost_method", new Dictionary<string, object>() },
                { "property_valuation", new Dictionary<string, object>() }
            };
            Dictionary<string, object> filteredVals = DemoAdapterCatalog.FilterAndValidateValues(valCatTest, JsonUtil.Dictionary("name", "Cat", "property_cost_method", "fifo", "property_valuation", "real_time", "property_stock_account_input_categ_id", 99), testFields, true);
            Assert(filteredVals.ContainsKey("name") && !filteredVals.ContainsKey("property_stock_account_input_categ_id"), "adaptación dinámica de campos opcionales omitidos en la base");
            passed++;
            bool missingContractualThrows = false;
            try { DemoAdapterCatalog.FilterAndValidateValues(valCatTest, JsonUtil.Dictionary("name", "Cat"), new Dictionary<string, object>(), true); }
            catch (ConsultantException ex) { missingContractualThrows = ex.Code == "demo_capability_missing"; }
            Assert(missingContractualThrows, "campos contractuales obligatorios siguen siendo estrictamente requeridos");
            passed++;
            Assert(DemoOwnershipRegistry.DerivedModelAllowedForSelfTest("project.task") && DemoOwnershipRegistry.DerivedModelAllowedForSelfTest("account.move") && !DemoOwnershipRegistry.DerivedModelAllowedForSelfTest("res.users"), "captura derivada limitada a modelos funcionales cerrados");
            passed++;
            DemoComponent warehouse = DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary("clave", "almacen_demo", "tipo", "almacen", "nombre", "Centro logístico", "codigo", "CL01", "subtipo", "one_step", "relaciones", JsonUtil.Dictionary("empresa", "runtime:COMPANY")));
            Assert(warehouse.adapter == "stock_warehouse" && Convert.ToString(warehouse.values["reception_steps"], CultureInfo.InvariantCulture) == "one_step", "almacén tipado para logística de demostración");
            passed++;
            DemoComponent fiscalTax = DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary("clave", "iva_mapeado", "tipo", "mapeo_impuesto_posicion_fiscal", "nombre", "IVA alterno", "porcentaje", 19, "subtipo", "sale", "relaciones", JsonUtil.Dictionary("posicion_fiscal", "posicion_demo", "impuesto_origen", "runtime:TAX_SALE", "empresa", "runtime:COMPANY", "pais", "runtime:COUNTRY")));
            Assert(fiscalTax.adapter == "fiscal_position_tax_map" && fiscalTax.references["fiscal_position_ids"] == "posicion_demo", "mapeo fiscal Odoo 19 mediante impuesto sintético relacionado");
            passed++;
            DemoComponent commission = DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary("clave", "plan_comercial", "tipo", "plan_comision", "nombre", "Plan comercial", "subtipo", "objetivo", "periodicidad", "mensual", "fecha", "2026-09-01", "fecha_limite", "2026-12-31", "relaciones", JsonUtil.Dictionary("empresa", "runtime:COMPANY")));
            Assert(commission.adapter == "commission_plan" && Convert.ToString(commission.values["type"], CultureInfo.InvariantCulture) == "target" && Convert.ToString(commission.values["periodicity"], CultureInfo.InvariantCulture) == "month", "normalización cerrada de plan de comisiones");
            passed++;
            DemoComponent asset = DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary("clave", "activo_demo", "tipo", "activo_contable", "nombre", "Equipo de prueba", "fecha", "2026-09-01", "precio_unitario", 12000000, "cantidad", 12, "periodicidad", "month", "relaciones", JsonUtil.Dictionary("empresa", "runtime:COMPANY", "diario", "runtime:MISC_JOURNAL", "modelo_activo", "runtime:ASSET_MODEL", "cuenta_analitica", "runtime:ANALYTIC")));
            Assert(asset.adapter == "account_asset_demo" && Convert.ToString(asset.values["method_period"], CultureInfo.InvariantCulture) == "1", "periodicidad de activo adaptada al contrato real");
            passed++;
            DemoComponent loan = DemoService.CompileOperationalForSelfTest(JsonUtil.Dictionary("clave", "prestamo_demo", "tipo", "prestamo", "nombre", "Financiación sintética", "fecha", "2026-09-01", "precio_unitario", 100000000, "porcentaje", 12, "cantidad", 24, "relaciones", JsonUtil.Dictionary("empresa", "runtime:COMPANY", "diario", "runtime:MISC_JOURNAL", "cuenta_largo_plazo", "runtime:ACCOUNT_LONG", "cuenta_corto_plazo", "runtime:ACCOUNT_SHORT", "cuenta_gasto", "runtime:ACCOUNT_EXPENSE", "grupo_activo", "runtime:ASSET_GROUP")));
            Assert(loan.adapter == "account_loan_demo" && DemoAdapterCatalog.Get("account_loan_demo").modules.Contains("account_loans"), "préstamo sintético usa módulo técnico account_loans");
            passed++;
            Assert(ModuleService.ModuleNameAllowedForSelfTest("sale_commission") && ModuleService.ModuleNameAllowedForSelfTest("account_loans") && !ModuleService.ModuleNameAllowedForSelfTest("l10n_co_edi") && !ModuleService.ModuleNameAllowedForSelfTest("whatsapp_sale") && !ModuleService.ModuleNameAllowedForSelfTest("delivery_fedex"), "instalación conserva bloqueadas integraciones externas");
            passed++;
            Dictionary<string, object> safeContract = JsonUtil.Dictionary(
                "schema_version", 1,
                "objective", "Añadir un dato funcional",
                "target_model", "sale.order",
                "operations", new object[] { JsonUtil.Dictionary("type", "create_custom_field", "parameters", JsonUtil.Dictionary("name", "x_reference_test", "label", "Referencia de prueba", "field_type", "char")) });
            PolicyDecision safeDecision = PolicyCompiler.ClassifyChange("staging", safeContract);
            Assert(safeDecision.executable && safeDecision.route == "confirmed_package" && !string.IsNullOrWhiteSpace(safeDecision.contract_hash), "compilación declarativa segura");
            passed++;
            Dictionary<string, object> highRiskContract = JsonUtil.Dictionary(
                "schema_version", 1,
                "objective", "Automatizar una notificación",
                "target_model", "sale.order",
                "operations", new object[] { JsonUtil.Dictionary("type", "create_automation", "parameters", JsonUtil.Dictionary("summary", "Revisión manual")) });
            PolicyDecision highRiskDecision = PolicyCompiler.ClassifyChange("staging", highRiskContract);
            Assert(!highRiskDecision.executable && highRiskDecision.route == "runbook" && highRiskDecision.risk == "high", "enrutamiento de alto riesgo a runbook");
            passed++;
            Dictionary<string, object> arbitraryMethodContract = JsonUtil.Dictionary(
                "schema_version", 1,
                "objective", "Intento de llamada libre",
                "target_model", "sale.order",
                "operations", new object[] { JsonUtil.Dictionary("type", "call_method", "parameters", JsonUtil.Dictionary("summary", "No ejecutar")) });
            PolicyDecision arbitraryMethodDecision = PolicyCompiler.ClassifyChange("staging", arbitraryMethodContract);
            Assert(!arbitraryMethodDecision.executable && arbitraryMethodDecision.route == "runbook", "llamada de método nunca ejecutable");
            passed++;
            bool mixedRiskRejected = false;
            try
            {
                PolicyCompiler.ClassifyChange("staging", JsonUtil.Dictionary(
                    "schema_version", 1, "objective", "Mezcla insegura", "target_model", "sale.order",
                    "operations", new object[]
                    {
                        JsonUtil.Dictionary("type", "create_custom_field", "parameters", JsonUtil.Dictionary("name", "x_safe", "label", "Seguro", "field_type", "char")),
                        JsonUtil.Dictionary("type", "delete", "parameters", JsonUtil.Dictionary("summary", "Eliminar"))
                    }));
            }
            catch (ConsultantException error) { mixedRiskRejected = error.Code == "mixed_risk_contract"; }
            Assert(mixedRiskRejected, "rechazo de riesgos mezclados");
            passed++;
            bool unknownRejected = false;
            try
            {
                Dictionary<string, object> unknown = new Dictionary<string, object>(safeContract, StringComparer.Ordinal);
                unknown["method"] = "unlink";
                PolicyCompiler.ClassifyChange("staging", unknown);
            }
            catch (ConsultantException error) { unknownRejected = error.Code == "unknown_parameter"; }
            Assert(unknownRejected, "rechazo de parámetros desconocidos");
            passed++;
            Dictionary<string, object> compiledQuery = PolicyCompiler.CompileQuery(JsonUtil.Dictionary(
                "model", "sale.order", "fields", new object[] { "name", "state" },
                "filters", new object[] { JsonUtil.Dictionary("field", "state", "operator", "=", "value", "sale") }, "limit", 10));
            Assert(Convert.ToInt64(compiledQuery["limit"], CultureInfo.InvariantCulture) == 10 && ((object[])compiledQuery["domain"]).Length == 1, "consulta declarativa cerrada");
            passed++;
            bool arbitraryOperatorBlocked = false;
            try
            {
                PolicyCompiler.CompileQuery(JsonUtil.Dictionary(
                    "model", "sale.order", "fields", new object[] { "name" },
                    "filters", new object[] { JsonUtil.Dictionary("field", "name", "operator", "child_of", "value", 1) }));
            }
            catch (ConsultantException error) { arbitraryOperatorBlocked = error.Code == "operator_not_allowed"; }
            Assert(arbitraryOperatorBlocked, "rechazo de operador no permitido");
            passed++;
            bool sensitiveReadBlocked = false;
            try { PolicyCompiler.CompileQuery(JsonUtil.Dictionary("model", "ir.config_parameter", "fields", new object[] { "key" })); }
            catch (ConsultantException error) { sensitiveReadBlocked = error.Code == "model_read_blocked"; }
            Assert(sensitiveReadBlocked, "bloqueo de modelos con secretos");
            passed++;
            Assert(
                OdooClient.NormalizeBaseUrl("https://cliente.example.com/odoo/") == "https://cliente.example.com" &&
                OdooClient.NormalizeBaseUrl("https://cliente.example.com/ODOO") == "https://cliente.example.com" &&
                OdooClient.NormalizeBaseUrl("https://cliente.example.com/") == "https://cliente.example.com",
                "normalización de URL web /odoo");
            passed++;
            Assert(
                FunctionalService.SupportsApplicationForSelfTest("crm") && FunctionalService.SupportsApplicationForSelfTest("ventas") &&
                FunctionalService.SupportsApplicationForSelfTest("compras") && FunctionalService.SupportsApplicationForSelfTest("inventario") &&
                FunctionalService.SupportsApplicationForSelfTest("contabilidad") && FunctionalService.SupportsApplicationForSelfTest("marketing") &&
                FunctionalService.SupportsApplicationForSelfTest("sitio_web") && FunctionalService.SupportsApplicationForSelfTest("ecommerce") &&
                !FunctionalService.SupportsApplicationForSelfTest("arbitrary"),
                "catálogo funcional cerrado por aplicación");
            passed++;
            Assert(string.Join(",", FunctionalService.AdapterKeysForSelfTest()) == "crm_stage,ecommerce_delivery_carrier,ecommerce_payment_provider,ecommerce_product_publish,ecommerce_product_tag,ecommerce_public_category,ecommerce_ribbon,mailing_list,sales_team,website_menu,website_page,website_setting", "adaptadores funcionales cerrados");
            passed++;
            Dictionary<string, object> functionalObjective = FunctionalService.NormalizeObjectiveForSelfTest(JsonUtil.Dictionary(
                "schema_version", 1, "application", "crm", "objective", "Configurar embudo comercial",
                "requirements", new object[]
                {
                    JsonUtil.Dictionary("key", "crm-installed", "kind", "module", "module", "crm", "expected_state", "installed"),
                    JsonUtil.Dictionary("key", "qualified-stage", "kind", "configuration", "adapter", "crm_stage", "match_name", "Calificado", "desired_state", "present", "desired", JsonUtil.Dictionary("sequence", 20, "fold", false)),
                    JsonUtil.Dictionary("key", "lost-policy", "kind", "decision", "question", "¿Cuándo se marca una oportunidad como perdida?", "resolved", false)
                }));
            Assert(JsonUtil.GetString(functionalObjective, "application", true) == "crm" && ((IEnumerable)functionalObjective["requirements"]).Cast<object>().Count() == 3, "objetivo funcional schema 1 normalizado");
            passed++;
            bool arbitraryFunctionalParameterBlocked = false;
            try
            {
                FunctionalService.NormalizeObjectiveForSelfTest(JsonUtil.Dictionary("schema_version", 1, "application", "crm", "objective", "Intento libre", "method", "unlink", "requirements", new object[] { JsonUtil.Dictionary("key", "crm-installed", "kind", "module", "module", "crm", "expected_state", "installed") }));
            }
            catch (ConsultantException error) { arbitraryFunctionalParameterBlocked = error.Code == "unknown_parameter"; }
            Assert(arbitraryFunctionalParameterBlocked, "objetivo funcional bloquea método arbitrario");
            passed++;
            bool crossApplicationAdapterBlocked = false;
            try
            {
                FunctionalService.NormalizeObjectiveForSelfTest(JsonUtil.Dictionary("schema_version", 1, "application", "marketing", "objective", "Cruce inválido", "requirements", new object[] { JsonUtil.Dictionary("key", "stage", "kind", "configuration", "adapter", "crm_stage", "match_name", "Calificado", "desired_state", "present") }));
            }
            catch (ConsultantException error) { crossApplicationAdapterBlocked = error.Code == "adapter_not_in_application"; }
            Assert(crossApplicationAdapterBlocked, "adaptador funcional confinado a su aplicación");
            passed++;
            ChangeSet functionalPackage = new ChangeSet
            {
                schema_version = 4, id = "chg-" + Guid.NewGuid().ToString("N"), client_slug = "selftest", environment = "staging", category = "functional_parameterization", risk = "medium", status = "prepared", model = "crm", target_models = new List<string> { "crm.stage" },
                created_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture), expires_at = DateTimeOffset.UtcNow.AddHours(12).ToString("o", CultureInfo.InvariantCulture), precondition_hash = JsonUtil.Sha256("precondition"), profile_fingerprint = JsonUtil.Sha256("profile"), policy_version = FunctionalService.PolicyVersion, objective = "Crear etapa", contract_hash = JsonUtil.Sha256("contract"), capability_hash = JsonUtil.Sha256("capability"), impact = "Configuración", rollback_strategy = "Archivar",
                preconditions = new List<Dictionary<string, object>>(), validations = new List<Dictionary<string, object>>(), warnings = new List<string>(),
                operations = new List<ChangeOperation> { new ChangeOperation { operation_id = "op-" + Guid.NewGuid().ToString("N"), type = "functional_create", model = "crm.stage", status = "pending", parameters = JsonUtil.Dictionary("adapter", "crm_stage", "gap_key", "qualified-stage", "match_name", "Calificado", "record_id", 0, "expected_current_hash", "missing", "desired_values", JsonUtil.Dictionary("name", "Calificado", "sequence", 20, "fold", false, "active", true), "previous_snapshot", null) } }
            };
            FunctionalService.SignPackageForSelfTest(functionalPackage);
            FunctionalService.ValidatePackage(functionalPackage);
            Assert(true, "paquete funcional schema 4 válido");
            passed++;
            functionalPackage.operations[0].parameters["match_name"] = "Manipulado";
            bool functionalIntegrityBlocked = false;
            try { FunctionalService.ValidatePackage(functionalPackage); }
            catch (ConsultantException error) { functionalIntegrityBlocked = error.Code == "package_integrity_failed"; }
            Assert(functionalIntegrityBlocked, "integridad de paquete funcional");
            passed++;
            functionalPackage.operations[0].parameters["match_name"] = "Calificado";
            functionalPackage.environment = "production";
            FunctionalService.SignPackageForSelfTest(functionalPackage);
            bool functionalProductionBlocked = false;
            try { FunctionalService.ValidatePackage(functionalPackage); }
            catch (ConsultantException error) { functionalProductionBlocked = error.Code == "production_write_blocked"; }
            Assert(functionalProductionBlocked, "parametrización funcional bloqueada en producción antes de red");
            passed++;
            string credentialTarget = "OdooAI/SelfTest/" + Guid.NewGuid().ToString("N");
            try
            {
                CredentialStore.Write(credentialTarget, "selftest", "credencial-ficticia");
                Assert(CredentialStore.Exists(credentialTarget), "persistencia en Credenciales de Windows");
                Assert(CredentialStore.Read(credentialTarget) == "credencial-ficticia", "lectura de Credenciales de Windows");
            }
            finally
            {
                CredentialStore.Delete(credentialTarget);
            }
            Assert(!CredentialStore.Exists(credentialTarget), "revocación en Credenciales de Windows");
            passed++;
            Dictionary<string, object> snapshotSource = JsonUtil.Dictionary("code", "resultado = 1", "webhook_url", "https://example.invalid/secret");
            string protectedSnapshot = ProtectedSnapshot.Protect(snapshotSource);
            Dictionary<string, object> snapshotRoundtrip = ProtectedSnapshot.Unprotect(protectedSnapshot);
            Assert(!protectedSnapshot.Contains("resultado") && Convert.ToString(snapshotRoundtrip["code"], CultureInfo.InvariantCulture) == "resultado = 1", "snapshot de reversión protegido por Windows");
            passed++;
            string tempRoot = Path.Combine(Path.GetTempPath(), "odoo-consultor-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                ProjectStore project = ProjectStore.Create(tempRoot, "selftest", "Cliente de prueba", Assembly.GetExecutingAssembly().Location);
                Assert(File.Exists(Path.Combine(tempRoot, "proyecto.yaml")), "creación de proyecto");
                passed++;
                Assert(project.Config.schema_version == 3 && project.Config.odoo_version == "19.0" && project.Config.enterprise_source_processing == "unavailable" && project.Config.client_source_processing == "blocked", "proyecto schema 3 separa Enterprise no disponible y código cliente bloqueado");
                passed++;
                Assert(project.Config.package_confirmation_minutes == 720 && project.Config.demo_confirmation_minutes == 720, "paquetes y demos vigentes 12 horas");
                passed++;
                string legacyProjectYaml = Path.Combine(tempRoot, "proyecto.yaml");
                string legacyProjectText = Regex.Replace(File.ReadAllText(legacyProjectYaml, Encoding.UTF8), "(?m)^(package|demo)_confirmation_minutes:.*\\r?\\n?", string.Empty);
                File.WriteAllText(legacyProjectYaml, legacyProjectText, new UTF8Encoding(false));
                ProjectStore legacyProject = new ProjectStore(tempRoot);
                Assert(legacyProject.Config.package_confirmation_minutes == 720 && legacyProject.Config.demo_confirmation_minutes == 720, "proyecto existente adopta 12 horas con compatibilidad");
                passed++;
                Assert(Directory.Exists(Path.Combine(tempRoot, "demostraciones")), "carpeta estándar de demostraciones");
                passed++;
                int[] demoNumbers = new int[8];
                System.Threading.Tasks.Parallel.For(0, demoNumbers.Length, index => demoNumbers[index] = project.NextDemoNumber("staging"));
                Assert(demoNumbers.Distinct().Count() == demoNumbers.Length && demoNumbers.Min() == 1 && demoNumbers.Max() == demoNumbers.Length, "secuencia de demos atómica y estable");
                passed++;
                Assert(Directory.Exists(Path.Combine(tempRoot, "replicas")), "carpeta estándar de réplicas");
                passed++;
                object sourceStatus = new SourceService(project).Status("19.0");
                Assert(JsonUtil.Serialize(sourceStatus).Contains("oficial_documentacion") && JsonUtil.Serialize(sourceStatus).Contains("\"content_access\":\"blocked\""), "estado de fuentes sin leer contenido privado");
                passed++;
                object sourceStatus17 = new SourceService(project).Status("17.0");
                Assert(JsonUtil.Serialize(sourceStatus17).Contains("oficial_documentacion"), "estado de fuentes Odoo 17 disponible");
                passed++;
                bool unsupportedSourceWarns = false;
                try { new SourceService(project).Status("15.0"); }
                catch (ConsultantException error) { unsupportedSourceWarns = error.Code == "unsupported_source_version"; }
                Assert(unsupportedSourceWarns, "aviso de falta de fuentes para versiones 14 a 16");
                passed++;
                Assert(ProjectStore.NormalizeOdooVersion("14") == "14.0" && ProjectStore.NormalizeOdooVersion("17") == "17.0" && ProjectStore.NormalizeOdooVersion("18.0") == "18.0", "normalización de versiones Odoo 14 a 18");
                passed++;
                bool privateBlocked = false;
                try { new SourceService(project).Search("enterprise", "19.0", "marketing", null, null, 2); }
                catch (ConsultantException error) { privateBlocked = error.Code == "enterprise_source_unavailable"; }
                Assert(privateBlocked, "contenido Enterprise declarado no disponible sin bloquear fuentes públicas");
                passed++;
                bool demoProductionBlocked = false;
                try { new DemoService(project).Prepare("production", "demo_segura", "marketing", "Probar", new[] { "Captación" }); }
                catch (ConsultantException error) { demoProductionBlocked = error.Code == "production_write_blocked"; }
                Assert(demoProductionBlocked, "demo bloqueada en producción antes de credenciales");
                passed++;
                passed += SelfTestMock.VerifyFunctionalLifecycle(project);
                passed += SelfTestMock.VerifyDemoLifecycle(project);
                passed += SelfTestMock.VerifyModuleLifecycle(project);
                passed += SelfTestMock.VerifyPromotionLifecycle(project);
                passed += SelfTestMock.VerifySpreadsheetLifecycle(project);
                passed += SelfTestMock.VerifyConnectionTestLifecycle(project);
                string preservedKnowledge = Path.Combine(tempRoot, "conocimiento", "negocio.md");
                File.WriteAllText(preservedKnowledge, "Conocimiento existente", new UTF8Encoding(false));
                string projectAgents = Path.Combine(tempRoot, "AGENTS.md");
                File.WriteAllText(projectAgents, "# Instrucciones del proyecto\n\n- Motor esperado: `OdooConsultor 0.5.0`.\n" + File.ReadAllText(projectAgents, Encoding.UTF8), new UTF8Encoding(false));
                ProjectStore upgradedProject = ProjectStore.Upgrade(tempRoot, Assembly.GetExecutingAssembly().Location, new Dictionary<string, string> { { "version", "19.0" } });
                Assert(File.ReadAllText(preservedKnowledge, Encoding.UTF8) == "Conocimiento existente" && upgradedProject.Config.schema_version == 3 && !File.ReadAllText(projectAgents, Encoding.UTF8).Contains("OdooConsultor 0.5.0"), "actualización idempotente preserva conocimiento y retira versión obsoleta");
                passed++;
                ReplicaService.ValidateReplicaView("<data><xpath expr=\"//field[@name='name']\" position=\"after\"><button type=\"action\" name=\"{{SERVER_ACTION_ID}}\"/></xpath></data>", true);
                bool objectButtonBlocked = false;
                try { ReplicaService.ValidateReplicaView("<data><button type=\"object\" name=\"arbitrary_method\"/></data>", true); }
                catch (ConsultantException error) { objectButtonBlocked = error.Code == "unsafe_view_xml" || error.Code == "unsafe_view_button"; }
                Assert(objectButtonBlocked, "bloqueo de botones type object en réplicas");
                passed++;
                string replicaRoot = Path.Combine(tempRoot, "replicas", "sample_replica");
                Directory.CreateDirectory(replicaRoot);
                JsonUtil.WriteJsonFile(Path.Combine(replicaRoot, "manifest.json"), JsonUtil.Dictionary(
                    "schema_version", 1,
                    "name", "sample",
                    "source", JsonUtil.Dictionary("environment", "staging", "contains_business_records", false),
                    "fields", new object[] { JsonUtil.Dictionary("model", "sale.order", "name", "x_sample_replica", "label", "Muestra", "type", "char", "required", false, "readonly", false, "stored", true, "copied", true) },
                    "views", new object[0]));
                object productionRunbook = new ReplicaService(project).Prepare("production", "sample_replica");
                Assert(JsonUtil.Serialize(productionRunbook).Contains("manual_only"), "réplica de producción genera runbook sin conexión");
                passed++;
                object promotionRunbookResult = new PromotionService(project).PrepareRunbook("sample_replica");
                Assert(File.Exists(project.PromotionPath("sample_replica")), "generación de promocion.json");
                passed++;
                Assert(File.Exists(project.PromotionRunbookPath("sample_replica")), "generación de runbook.md de promoción");
                passed++;
                string runbookText = File.ReadAllText(project.PromotionRunbookPath("sample_replica"), Encoding.UTF8);
                Assert(runbookText.Contains("Runbook de Promoción a Producción: sample_replica") && runbookText.Contains("Ajustes > Técnico"), "contenido estructurado de runbook dual de promoción");
                passed++;
                bool externalButtonBlocked = false;
                try { ReplicaService.ValidateReplicaView("<data><button type=\"action\" name=\"999\"/></data>", true); }
                catch (ConsultantException error) { externalButtonBlocked = error.Code == "unsafe_view_button"; }
                Assert(externalButtonBlocked, "botón de acción ajena bloqueado");
                passed++;
                bool externalReplicaPathBlocked = false;
                try { project.ReplicaPath("../escape"); }
                catch (ConsultantException error) { externalReplicaPathBlocked = error.Code == "invalid_replica_slug"; }
                Assert(externalReplicaPathBlocked, "rutas externas de réplica bloqueadas");
                passed++;
                string invalidBinaryRoot = Path.Combine(tempRoot, "replicas", "invalid_binary"); Directory.CreateDirectory(invalidBinaryRoot);
                JsonUtil.WriteJsonFile(Path.Combine(invalidBinaryRoot, "manifest.json"), JsonUtil.Dictionary("schema_version", 1, "name", "invalid", "source", JsonUtil.Dictionary("contains_business_records", false), "fields", new object[] { JsonUtil.Dictionary("model", "sale.order", "name", "x_missing_filename", "label", "Binario", "type", "binary", "filename_field", "x_filename") }, "views", new object[0]));
                bool filenameBlocked = false;
                try { new ReplicaService(project).Prepare("production", "invalid_binary"); }
                catch (ConsultantException error) { filenameBlocked = error.Code == "binary_filename_required"; }
                Assert(filenameBlocked, "binary exige auxiliar filename declarado");
                passed++;
                string invalidSelectionRoot = Path.Combine(tempRoot, "replicas", "invalid_selection"); Directory.CreateDirectory(invalidSelectionRoot);
                JsonUtil.WriteJsonFile(Path.Combine(invalidSelectionRoot, "manifest.json"), JsonUtil.Dictionary("schema_version", 1, "name", "invalid", "source", JsonUtil.Dictionary("contains_business_records", false), "fields", new object[] { JsonUtil.Dictionary("model", "sale.order", "name", "x_duplicate_selection", "label", "Selección", "type", "selection", "selection", new object[] { JsonUtil.Dictionary("sequence", 0, "label", "Uno", "value", "same"), JsonUtil.Dictionary("sequence", 1, "label", "Dos", "value", "same") }) }, "views", new object[0]));
                bool duplicateSelectionBlocked = false;
                try { new ReplicaService(project).Prepare("production", "invalid_selection"); }
                catch (ConsultantException error) { duplicateSelectionBlocked = error.Code == "duplicate_selection_value"; }
                Assert(duplicateSelectionBlocked, "valores técnicos de selección duplicados bloqueados");
                passed++;
                ChangeSet invalidReplicaPackage = new ChangeSet { schema_version = 3, category = "validated_replica", replica_slug = "sample_replica", target_models = new List<string> { "sale.order" }, integrity_hash = "altered", operations = new List<ChangeOperation> { new ChangeOperation { operation_id = "op-" + Guid.NewGuid().ToString("N"), type = "create_custom_field", model = "sale.order", parameters = JsonUtil.Dictionary("name", "x_test") } } };
                bool replicaIntegrityBlocked = false;
                try { ReplicaService.ValidatePackage(invalidReplicaPackage); }
                catch (ConsultantException error) { replicaIntegrityBlocked = error.Code == "package_integrity_failed"; }
                Assert(replicaIntegrityBlocked, "hash alterado de paquete de réplica bloqueado");
                passed++;
                string phase2Root = Path.Combine(tempRoot, "replicas", "phase2_app"); Directory.CreateDirectory(phase2Root);
                string computeCode = "for record in self:\n    record['x_total_label'] = str(record.x_amount or 0)\n";
                File.WriteAllText(Path.Combine(phase2Root, "compute_total.py"), computeCode, new UTF8Encoding(false));
                JsonUtil.WriteJsonFile(Path.Combine(phase2Root, "manifest.json"), JsonUtil.Dictionary(
                    "schema_version", 2, "phase", 2, "name", "phase2_app", "description", "App Studio de prueba",
                    "source", JsonUtil.Dictionary("environment", "staging", "odoo_version", "19", "captured_on", "2026-08-10", "contains_business_records", false),
                    "dependencies", JsonUtil.Dictionary("models", new object[] { "res.currency" }, "features", new object[] { "studio" }),
                    "models", new object[] { JsonUtil.Dictionary("key", "demo_model", "model", "x_phase2_demo", "name", "Demo Fase 2", "mode", "create", "order", "id") },
                    "fields", new object[] {
                        JsonUtil.Dictionary("key", "currency", "model", "x_phase2_demo", "name", "x_currency_id", "label", "Moneda", "type", "many2one", "relation", "res.currency", "on_delete", "set null"),
                        JsonUtil.Dictionary("key", "amount", "model", "x_phase2_demo", "name", "x_amount", "label", "Importe", "type", "monetary", "currency_field", "x_currency_id"),
                        JsonUtil.Dictionary("key", "notes", "model", "x_phase2_demo", "name", "x_notes", "label", "Notas", "type", "html", "sanitize", true),
                        JsonUtil.Dictionary("key", "total_label", "model", "x_phase2_demo", "name", "x_total_label", "label", "Total texto", "type", "char", "compute_file", "compute_total.py", "compute_sha256", JsonUtil.Sha256(computeCode), "depends", "x_amount")
                    },
                    "views", new object[] { JsonUtil.Dictionary("key", "demo_form", "model", "x_phase2_demo", "type", "form", "operation", "create", "arch_template", "<form><sheet><group><field name=\"x_name\"/><field name=\"x_amount\"/><field name=\"x_currency_id\"/></group></sheet></form>") },
                    "window_actions", new object[] { JsonUtil.Dictionary("key", "demo_action", "name", "Demo Fase 2", "res_model", "x_phase2_demo", "view_mode", "list,form", "mode", "create") },
                    "menus", new object[] { JsonUtil.Dictionary("key", "demo_root", "name", "Demo Fase 2", "mode", "create", "sequence", 10), JsonUtil.Dictionary("key", "demo_records", "name", "Registros", "mode", "create", "parent_key", "demo_root", "action_key", "demo_action", "sequence", 20) },
                    "deployment_order", new object[] { "models", "fields", "views", "window_actions", "menus" }, "excluded_artifacts", new object[] { "all_business_records" }));
                object phase2Runbook = new ReplicaService(project).Prepare("production", "phase2_app");
                Assert(JsonUtil.Serialize(phase2Runbook).Contains("\"phase\":2") && JsonUtil.Serialize(phase2Runbook).Contains("manual_only"), "manifiesto Fase 2 aceptado y producción en runbook");
                passed++;
                string invalidUpdateRoot = Path.Combine(tempRoot, "replicas", "phase2_invalid_update"); Directory.CreateDirectory(invalidUpdateRoot);
                JsonUtil.WriteJsonFile(Path.Combine(invalidUpdateRoot, "manifest.json"), JsonUtil.Dictionary("schema_version", 2, "phase", 2, "source", JsonUtil.Dictionary("contains_business_records", false), "models", new object[0], "fields", new object[] { JsonUtil.Dictionary("key", "bad", "model", "sale.order", "name", "x_bad", "label", "Bad", "type", "char", "mode", "update") }, "views", new object[0], "window_actions", new object[0], "menus", new object[0]));
                bool previousHashRequired = false; try { new ReplicaService(project).Prepare("production", "phase2_invalid_update"); } catch (ConsultantException error) { previousHashRequired = error.Code == "invalid_input" || error.Code == "invalid_previous_hash"; }
                Assert(previousHashRequired, "actualización Fase 2 exige previous_hash"); passed++;
                string invalidStandardViewRoot = Path.Combine(tempRoot, "replicas", "phase2_standard_view"); Directory.CreateDirectory(invalidStandardViewRoot);
                JsonUtil.WriteJsonFile(Path.Combine(invalidStandardViewRoot, "manifest.json"), JsonUtil.Dictionary("schema_version", 2, "phase", 2, "source", JsonUtil.Dictionary("contains_business_records", false), "models", new object[0], "fields", new object[0], "views", new object[] { JsonUtil.Dictionary("key", "bad_view", "model", "sale.order", "type", "form", "arch_template", "<form/>") }, "window_actions", new object[0], "menus", new object[0]));
                bool standardOverwriteBlocked = false; try { new ReplicaService(project).Prepare("production", "phase2_standard_view"); } catch (ConsultantException error) { standardOverwriteBlocked = error.Code == "standard_view_extension_required"; }
                Assert(standardOverwriteBlocked, "vista estándar solo se extiende"); passed++;
                string invalidComputeRoot = Path.Combine(tempRoot, "replicas", "phase2_bad_compute"); Directory.CreateDirectory(invalidComputeRoot); File.WriteAllText(Path.Combine(invalidComputeRoot, "compute.py"), computeCode, new UTF8Encoding(false));
                JsonUtil.WriteJsonFile(Path.Combine(invalidComputeRoot, "manifest.json"), JsonUtil.Dictionary("schema_version", 2, "phase", 2, "source", JsonUtil.Dictionary("contains_business_records", false), "models", new object[0], "fields", new object[] { JsonUtil.Dictionary("key", "bad_compute", "model", "sale.order", "name", "x_bad_compute", "label", "Bad", "type", "char", "compute_file", "compute.py", "compute_sha256", new string('0', 64), "depends", "name") }, "views", new object[0], "window_actions", new object[0], "menus", new object[0]));
                bool computeHashBlocked = false; try { new ReplicaService(project).Prepare("production", "phase2_bad_compute"); } catch (ConsultantException error) { computeHashBlocked = error.Code == "replica_file_hash_mismatch"; }
                Assert(computeHashBlocked, "hash de campo calculado alterado bloqueado"); passed++;
                string invalidMenuRoot = Path.Combine(tempRoot, "replicas", "phase2_bad_menu"); Directory.CreateDirectory(invalidMenuRoot);
                JsonUtil.WriteJsonFile(Path.Combine(invalidMenuRoot, "manifest.json"), JsonUtil.Dictionary("schema_version", 2, "phase", 2, "source", JsonUtil.Dictionary("contains_business_records", false), "models", new object[] { JsonUtil.Dictionary("key", "menu_model", "model", "x_menu_demo", "name", "Menu Demo") }, "fields", new object[0], "views", new object[0], "window_actions", new object[0], "menus", new object[] { JsonUtil.Dictionary("key", "child", "name", "Hijo", "parent_key", "parent"), JsonUtil.Dictionary("key", "parent", "name", "Padre") }));
                bool menuOrderBlocked = false; try { new ReplicaService(project).Prepare("production", "phase2_bad_menu"); } catch (ConsultantException error) { menuOrderBlocked = error.Code == "dependency_order_invalid"; }
                Assert(menuOrderBlocked, "orden lógico de menús validado"); passed++;
                string invalidM2oRoot = Path.Combine(tempRoot, "replicas", "phase2_bad_m2o"); Directory.CreateDirectory(invalidM2oRoot);
                JsonUtil.WriteJsonFile(Path.Combine(invalidM2oRoot, "manifest.json"), JsonUtil.Dictionary("schema_version", 2, "phase", 2, "source", JsonUtil.Dictionary("contains_business_records", false), "models", new object[0], "fields", new object[] { JsonUtil.Dictionary("key", "bad_m2o", "model", "sale.order", "name", "x_bad_m2o", "label", "Bad", "type", "many2one", "relation", "res.partner", "required", true, "on_delete", "set null") }, "views", new object[0], "window_actions", new object[0], "menus", new object[0]));
                bool requiredM2oBlocked = false; try { new ReplicaService(project).Prepare("production", "phase2_bad_m2o"); } catch (ConsultantException error) { requiredM2oBlocked = error.Code == "invalid_on_delete"; }
                Assert(requiredM2oBlocked, "many2one requerido bloquea set null"); passed++;
                string unknownParameterRoot = Path.Combine(tempRoot, "replicas", "phase2_unknown_parameter"); Directory.CreateDirectory(unknownParameterRoot);
                JsonUtil.WriteJsonFile(Path.Combine(unknownParameterRoot, "manifest.json"), JsonUtil.Dictionary("schema_version", 2, "phase", 2, "source", JsonUtil.Dictionary("contains_business_records", false), "models", new object[] { JsonUtil.Dictionary("key", "unknown_model", "model", "x_unknown_demo", "name", "Unknown", "typo_parameter", true) }, "fields", new object[0], "views", new object[0], "window_actions", new object[0], "menus", new object[0]));
                bool unknownParameterBlocked = false; try { new ReplicaService(project).Prepare("production", "phase2_unknown_parameter"); } catch (ConsultantException error) { unknownParameterBlocked = error.Code == "unknown_component_parameter"; }
                Assert(unknownParameterBlocked, "parámetro de componente Fase 2 desconocido bloqueado"); passed++;
                string executableViewRoot = Path.Combine(tempRoot, "replicas", "phase2_executable_view"); Directory.CreateDirectory(executableViewRoot);
                JsonUtil.WriteJsonFile(Path.Combine(executableViewRoot, "manifest.json"), JsonUtil.Dictionary("schema_version", 2, "phase", 2, "source", JsonUtil.Dictionary("contains_business_records", false), "models", new object[] { JsonUtil.Dictionary("key", "button_model", "model", "x_button_demo", "name", "Button") }, "fields", new object[0], "views", new object[] { JsonUtil.Dictionary("key", "button_form", "model", "x_button_demo", "type", "form", "arch_template", "<form><button type=\"action\" name=\"1\"/></form>") }, "window_actions", new object[0], "menus", new object[0]));
                bool executableViewBlocked = false; try { new ReplicaService(project).Prepare("production", "phase2_executable_view"); } catch (ConsultantException error) { executableViewBlocked = error.Code == "phase_not_available"; }
                Assert(executableViewBlocked, "botón ejecutable Fase 2 bloqueado para Fase 3"); passed++;
                string targetlessRoot = Path.Combine(tempRoot, "replicas", "phase2_targetless"); Directory.CreateDirectory(targetlessRoot);
                JsonUtil.WriteJsonFile(Path.Combine(targetlessRoot, "manifest.json"), JsonUtil.Dictionary("schema_version", 2, "phase", 2, "source", JsonUtil.Dictionary("contains_business_records", false), "models", new object[0], "fields", new object[0], "views", new object[0], "window_actions", new object[0], "menus", new object[] { JsonUtil.Dictionary("key", "lonely_menu", "name", "Sin modelo") }));
                bool targetModelBlocked = false; try { new ReplicaService(project).Prepare("production", "phase2_targetless"); } catch (ConsultantException error) { targetModelBlocked = error.Code == "missing_target_model"; }
                Assert(targetModelBlocked, "réplica Fase 2 sin modelo objetivo bloqueada"); passed++;
                string phase3Code = "log('fase 3 controlada')\n";
                string phase3CodeHash = JsonUtil.Sha256(phase3Code);
                string phase3Root = Path.Combine(tempRoot, "replicas", "phase3_effects"); Directory.CreateDirectory(phase3Root); File.WriteAllText(Path.Combine(phase3Root, "action.py"), phase3Code, new UTF8Encoding(false));
                JsonUtil.WriteJsonFile(Path.Combine(phase3Root, "manifest.json"), JsonUtil.Dictionary(
                    "schema_version", 2, "phase", 3, "name", "phase3_effects",
                    "source", JsonUtil.Dictionary("environment", "staging", "odoo_version", "19", "contains_business_records", false),
                    "dependencies", JsonUtil.Dictionary("models", new object[] { "res.partner" }, "features", new object[] { "base_automation", "mail" }),
                    "references", JsonUtil.Dictionary(), "computed_fields", new object[0], "mail_templates", new object[0],
                    "server_actions", new object[] { JsonUtil.Dictionary("key", "internal_code", "name", "Fase 3 interna", "model", "res.partner", "state", "code", "mode", "create", "code_file", "action.py", "code_sha256", phase3CodeHash) },
                    "automations", new object[] { JsonUtil.Dictionary("key", "on_create", "name", "Fase 3 al crear", "model", "res.partner", "mode", "create", "trigger", "on_create", "active", true, "action_keys", new object[] { "internal_code" }) },
                    "excluded_artifacts", new object[] { "all_business_records" }));
                object phase3Runbook = new ReplicaService(project).Prepare("production", "phase3_effects");
                Assert(JsonUtil.Serialize(phase3Runbook).Contains("\"phase\":3") && JsonUtil.Serialize(phase3Runbook).Contains("manual_only"), "manifiesto Fase 3 aceptado y producción en runbook"); passed++;
                string phase3BadHashRoot = Path.Combine(tempRoot, "replicas", "phase3_bad_hash"); Directory.CreateDirectory(phase3BadHashRoot); File.WriteAllText(Path.Combine(phase3BadHashRoot, "action.py"), phase3Code, new UTF8Encoding(false));
                JsonUtil.WriteJsonFile(Path.Combine(phase3BadHashRoot, "manifest.json"), JsonUtil.Dictionary("schema_version", 2, "phase", 3, "source", JsonUtil.Dictionary("odoo_version", "19", "contains_business_records", false), "references", JsonUtil.Dictionary(), "computed_fields", new object[0], "mail_templates", new object[0], "server_actions", new object[] { JsonUtil.Dictionary("key", "bad_hash", "name", "Bad hash", "model", "res.partner", "state", "code", "code_file", "action.py", "code_sha256", new string('0', 64)) }, "automations", new object[0], "excluded_artifacts", new object[] { "all_business_records" }));
                bool phase3HashBlocked = false; try { new ReplicaService(project).Prepare("production", "phase3_bad_hash"); } catch (ConsultantException error) { phase3HashBlocked = error.Code == "replica_file_hash_mismatch"; }
                Assert(phase3HashBlocked, "Fase 3 bloquea código con hash alterado"); passed++;
                string phase3CycleRoot = Path.Combine(tempRoot, "replicas", "phase3_cycle"); Directory.CreateDirectory(phase3CycleRoot);
                JsonUtil.WriteJsonFile(Path.Combine(phase3CycleRoot, "manifest.json"), JsonUtil.Dictionary("schema_version", 2, "phase", 3, "source", JsonUtil.Dictionary("odoo_version", "19", "contains_business_records", false), "references", JsonUtil.Dictionary(), "computed_fields", new object[0], "mail_templates", new object[0], "server_actions", new object[] { JsonUtil.Dictionary("key", "cycle_a", "name", "Cycle A", "model", "res.partner", "state", "multi", "child_action_keys", new object[] { "cycle_b" }), JsonUtil.Dictionary("key", "cycle_b", "name", "Cycle B", "model", "res.partner", "state", "multi", "child_action_keys", new object[] { "cycle_a" }) }, "automations", new object[0], "excluded_artifacts", new object[] { "all_business_records" }));
                bool cycleBlocked = false; try { new ReplicaService(project).Prepare("production", "phase3_cycle"); } catch (ConsultantException error) { cycleBlocked = error.Code == "action_cycle" || error.Code == "shared_child_action"; }
                Assert(cycleBlocked, "Fase 3 bloquea ciclos de acciones encadenadas"); passed++;
                string phase3OnchangeRoot = Path.Combine(tempRoot, "replicas", "phase3_onchange"); Directory.CreateDirectory(phase3OnchangeRoot); File.WriteAllText(Path.Combine(phase3OnchangeRoot, "action.py"), phase3Code, new UTF8Encoding(false));
                JsonUtil.WriteJsonFile(Path.Combine(phase3OnchangeRoot, "manifest.json"), JsonUtil.Dictionary("schema_version", 2, "phase", 3, "source", JsonUtil.Dictionary("odoo_version", "19", "contains_business_records", false), "references", JsonUtil.Dictionary(), "computed_fields", new object[0], "mail_templates", new object[0], "server_actions", new object[] { JsonUtil.Dictionary("key", "child_code", "name", "Child", "model", "res.partner", "state", "code", "code_file", "action.py", "code_sha256", phase3CodeHash), JsonUtil.Dictionary("key", "multi_action", "name", "Multi", "model", "res.partner", "state", "multi", "child_action_keys", new object[] { "child_code" }) }, "automations", new object[] { JsonUtil.Dictionary("key", "bad_onchange", "name", "Bad onchange", "model", "res.partner", "trigger", "on_change", "action_keys", new object[] { "multi_action" }) }, "excluded_artifacts", new object[] { "all_business_records" }));
                bool onChangeBlocked = false; try { new ReplicaService(project).Prepare("production", "phase3_onchange"); } catch (ConsultantException error) { onChangeBlocked = error.Code == "on_change_code_only"; }
                Assert(onChangeBlocked, "Fase 3 aplica la restricción real de Odoo para on_change"); passed++;
                string phase3LiteralEmailRoot = Path.Combine(tempRoot, "replicas", "phase3_literal_email"); Directory.CreateDirectory(phase3LiteralEmailRoot); File.WriteAllText(Path.Combine(phase3LiteralEmailRoot, "subject.txt"), "Asunto\n", new UTF8Encoding(false)); File.WriteAllText(Path.Combine(phase3LiteralEmailRoot, "body.html"), "<p>Prueba</p>\n", new UTF8Encoding(false));
                JsonUtil.WriteJsonFile(Path.Combine(phase3LiteralEmailRoot, "manifest.json"), JsonUtil.Dictionary("schema_version", 2, "phase", 3, "source", JsonUtil.Dictionary("odoo_version", "19", "contains_business_records", false), "references", JsonUtil.Dictionary(), "computed_fields", new object[0], "mail_templates", new object[] { JsonUtil.Dictionary("key", "literal", "name", "Literal", "model", "res.partner", "subject_file", "subject.txt", "subject_sha256", JsonUtil.Sha256("Asunto\n"), "body_html_file", "body.html", "body_html_sha256", JsonUtil.Sha256("<p>Prueba</p>\n"), "email_to", "persona@example.com") }, "server_actions", new object[0], "automations", new object[0], "excluded_artifacts", new object[] { "all_business_records" }));
                bool literalEmailBlocked = false; try { new ReplicaService(project).Prepare("production", "phase3_literal_email"); } catch (ConsultantException error) { literalEmailBlocked = error.Code == "typed_reference_required"; }
                Assert(literalEmailBlocked, "Fase 3 no persiste destinatarios de correo en la réplica"); passed++;
                string phase3UnknownRoot = Path.Combine(tempRoot, "replicas", "phase3_unknown"); Directory.CreateDirectory(phase3UnknownRoot); File.WriteAllText(Path.Combine(phase3UnknownRoot, "action.py"), phase3Code, new UTF8Encoding(false));
                JsonUtil.WriteJsonFile(Path.Combine(phase3UnknownRoot, "manifest.json"), JsonUtil.Dictionary("schema_version", 2, "phase", 3, "source", JsonUtil.Dictionary("odoo_version", "19", "contains_business_records", false), "references", JsonUtil.Dictionary(), "computed_fields", new object[0], "mail_templates", new object[0], "server_actions", new object[] { JsonUtil.Dictionary("key", "unknown", "name", "Unknown", "model", "res.partner", "state", "code", "code_file", "action.py", "code_sha256", phase3CodeHash, "arbitrary_method", "unlink") }, "automations", new object[0], "excluded_artifacts", new object[] { "all_business_records" }));
                bool phase3UnknownBlocked = false; try { new ReplicaService(project).Prepare("production", "phase3_unknown"); } catch (ConsultantException error) { phase3UnknownBlocked = error.Code == "unknown_component_parameter"; }
                Assert(phase3UnknownBlocked, "Fase 3 mantiene catálogo cerrado sin métodos arbitrarios"); passed++;
                ReplicaPhase3Service.ValidateActionViewArchitecture("<data><xpath expr=\"//header\" position=\"inside\"><button type=\"action\" name=\"{{ACTION_ID}}\" string=\"Ejecutar\"/></xpath></data>");
                Assert(ReplicaPhase3Service.ResolveActionViewArchitecture("<data><button type=\"action\" name=\"{{ACTION_ID}}\"/></data>", 1135).Contains("name=\"1135\""), "Fase 3 resuelve el botón solo contra la acción declarada"); passed++;
                bool phase3NumericButtonBlocked = false; try { ReplicaPhase3Service.ValidateActionViewArchitecture("<data><button type=\"action\" name=\"1135\"/></data>"); } catch (ConsultantException error) { phase3NumericButtonBlocked = error.Code == "unsafe_view_button"; }
                Assert(phase3NumericButtonBlocked, "Fase 3 bloquea IDs de acción fijos en vistas"); passed++;
                bool missingActivationGateBlocked = false; try { ReplicaPhase3Service.RequireExternalActivationGates(new ChangeSet { operations = new List<ChangeOperation>() }); } catch (ConsultantException error) { missingActivationGateBlocked = error.Code == "external_activation_not_declared"; }
                Assert(missingActivationGateBlocked, "activación externa falla si el paquete no contiene una compuerta real"); passed++;
                bool unresolvedActivationGateBlocked = false; try { ReplicaPhase3Service.RequireExternalActivationGates(new ChangeSet { operations = new List<ChangeOperation> { new ChangeOperation { type = "arm_external_automation", result_id = 0 } } }); } catch (ConsultantException error) { unresolvedActivationGateBlocked = error.Code == "dependency_not_resolved"; }
                Assert(unresolvedActivationGateBlocked, "activación externa falla si la automatización no quedó aplicada"); passed++;
                ChangeSet phase3Tampered = new ChangeSet { schema_version = 3, category = "validated_replica_phase3", policy_version = ReplicaPhase3Service.PolicyVersion, client_slug = "selftest", environment = "staging", model = "res.partner", target_models = new List<string> { "res.partner" }, integrity_hash = "altered", operations = new List<ChangeOperation> { new ChangeOperation { operation_id = "op-" + Guid.NewGuid().ToString("N"), type = "create_server_action", model = "res.partner", parameters = JsonUtil.Dictionary("key", "tampered") } } };
                bool phase3IntegrityBlocked = false; try { ReplicaPhase3Service.ValidatePackage(phase3Tampered); } catch (ConsultantException error) { phase3IntegrityBlocked = error.Code == "package_integrity_failed"; }
                Assert(phase3IntegrityBlocked, "Fase 3 bloquea paquetes manipulados"); passed++;
                phase3Tampered.id = "chg-" + Guid.NewGuid().ToString("N");
                JsonUtil.WriteJsonFile(project.ChangeSetPath(phase3Tampered.id), phase3Tampered);
                bool phase3DispatchCorrect = false;
                try { new StudioService(project).ApplyPackage(phase3Tampered.id, "APLICAR " + phase3Tampered.id); }
                catch (ConsultantException error) { phase3DispatchCorrect = error.Code == "package_integrity_failed"; }
                Assert(phase3DispatchCorrect, "aplicador global enruta paquetes Fase 3 a su validador"); passed++;
                string phase4Root = Path.Combine(tempRoot, "replicas", "phase4_governance"); Directory.CreateDirectory(phase4Root);
                string qweb = "<t t-name=\"odooai.phase4_demo_report\"><t t-call=\"web.external_layout\"><div class=\"page\"><span t-field=\"o.display_name\"/></div></t></t>\n";
                string ruleDomain = "[[\"id\",\"!=\",false]]\n";
                string probeDomain = "[[\"id\",\"!=\",false]]\n";
                File.WriteAllText(Path.Combine(phase4Root, "report.xml"), qweb, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(phase4Root, "rule.domain.json"), ruleDomain, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(phase4Root, "probe.domain.json"), probeDomain, new UTF8Encoding(false));
                JsonUtil.WriteJsonFile(Path.Combine(phase4Root, "manifest.json"), JsonUtil.Dictionary(
                    "schema_version", 2, "phase", 4, "name", "phase4_governance",
                    "source", JsonUtil.Dictionary("environment", "staging", "odoo_version", "19", "contains_business_records", false),
                    "dependencies", JsonUtil.Dictionary("models", new object[] { "res.partner" }, "features", new object[] { "studio", "web" }),
                    "references", JsonUtil.Dictionary("PARTNER_USER_GROUP", JsonUtil.Dictionary("type", "xml_id", "model", "res.groups", "description", "Grupo funcional de prueba")),
                    "qweb_views", new object[] { JsonUtil.Dictionary("key", "partner_report_template", "name", "OdooAI Phase 4 report", "template_name", "odooai.phase4_demo_report", "mode", "create", "arch_file", "report.xml", "arch_sha256", JsonUtil.Sha256(qweb), "active", true) },
                    "reports", new object[] { JsonUtil.Dictionary("key", "partner_report", "name", "OdooAI Phase 4 report", "model", "res.partner", "mode", "create", "template_key", "partner_report_template", "report_type", "qweb-pdf", "group_refs", new object[] { "PARTNER_USER_GROUP" }) },
                    "approvals", new object[0],
                    "access_controls", new object[] { JsonUtil.Dictionary("key", "partner_read_acl", "name", "odooai.phase4.partner.read", "model", "res.partner", "mode", "create", "group_ref", "PARTNER_USER_GROUP", "perm_read", true, "perm_write", false, "perm_create", false, "perm_unlink", false) },
                    "record_rules", new object[] { JsonUtil.Dictionary("key", "partner_record_rule", "name", "OdooAI Phase 4 partner rule", "model", "res.partner", "mode", "create", "group_refs", new object[] { "PARTNER_USER_GROUP" }, "domain_file", "rule.domain.json", "domain_sha256", JsonUtil.Sha256(ruleDomain), "perm_read", true) },
                    "negative_access_tests", new object[] { JsonUtil.Dictionary("key", "partner_create_denied", "model", "res.partner", "operation", "create", "expected_allowed", false), JsonUtil.Dictionary("key", "partner_probe_limited", "model", "res.partner", "operation", "read", "expected_allowed", true, "probe_domain_file", "probe.domain.json", "probe_domain_sha256", JsonUtil.Sha256(probeDomain), "expected_visible_count", 0) },
                    "deployment_order", new object[] { "qweb_views", "reports", "approvals", "access_controls", "record_rules", "negative_access_tests" },
                    "excluded_artifacts", new object[] { "all_business_records" }));
                object phase4Runbook = new ReplicaService(project).Prepare("production", "phase4_governance");
                Assert(JsonUtil.Serialize(phase4Runbook).Contains("\"phase\":4") && JsonUtil.Serialize(phase4Runbook).Contains("manual_only"), "manifiesto Fase 4 aceptado y producción en runbook"); passed++;
                ReplicaPhase4Service.ValidateQwebArchitecture(qweb, "odooai.phase4_demo_report");
                bool unsafeQwebBlocked = false; try { ReplicaPhase4Service.ValidateQwebArchitecture("<t t-name=\"odooai.unsafe\"><script>alert(1)</script></t>", "odooai.unsafe"); } catch (ConsultantException error) { unsafeQwebBlocked = error.Code == "unsafe_qweb"; }
                Assert(unsafeQwebBlocked, "Fase 4 bloquea elementos ejecutables en QWeb"); passed++;
                string phase4BadScopeRoot = Path.Combine(tempRoot, "replicas", "phase4_bad_scope"); Directory.CreateDirectory(phase4BadScopeRoot);
                JsonUtil.WriteJsonFile(Path.Combine(phase4BadScopeRoot, "manifest.json"), JsonUtil.Dictionary("schema_version", 2, "phase", 4, "source", JsonUtil.Dictionary("odoo_version", "19", "contains_business_records", false), "references", JsonUtil.Dictionary("GROUP", JsonUtil.Dictionary("type", "xml_id", "model", "res.groups")), "qweb_views", new object[0], "reports", new object[0], "approvals", new object[0], "access_controls", new object[] { JsonUtil.Dictionary("key", "bad_scope", "name", "Bad", "model", "res.partner", "group_ref", "GROUP", "global", true, "perm_read", true) }, "record_rules", new object[0], "negative_access_tests", new object[0], "excluded_artifacts", new object[] { "all_business_records" }));
                bool badSecurityScopeBlocked = false; try { new ReplicaService(project).Prepare("production", "phase4_bad_scope"); } catch (ConsultantException error) { badSecurityScopeBlocked = error.Code == "invalid_security_scope"; }
                Assert(badSecurityScopeBlocked, "Fase 4 bloquea ámbitos globales y de grupo mezclados"); passed++;
                string phase4MissingTestRoot = Path.Combine(tempRoot, "replicas", "phase4_missing_test"); Directory.CreateDirectory(phase4MissingTestRoot);
                JsonUtil.WriteJsonFile(Path.Combine(phase4MissingTestRoot, "manifest.json"), JsonUtil.Dictionary("schema_version", 2, "phase", 4, "source", JsonUtil.Dictionary("odoo_version", "19", "contains_business_records", false), "references", JsonUtil.Dictionary(), "qweb_views", new object[0], "reports", new object[0], "approvals", new object[0], "access_controls", new object[] { JsonUtil.Dictionary("key", "global_read", "name", "Global read", "model", "res.partner", "global", true, "perm_read", true) }, "record_rules", new object[0], "negative_access_tests", new object[0], "excluded_artifacts", new object[] { "all_business_records" }));
                bool missingNegativeTestBlocked = false; try { new ReplicaService(project).Prepare("production", "phase4_missing_test"); } catch (ConsultantException error) { missingNegativeTestBlocked = error.Code == "negative_access_test_required"; }
                Assert(missingNegativeTestBlocked, "Fase 4 exige prueba negativa cuando cambia seguridad"); passed++;
                ChangeSet phase4Tampered = new ChangeSet { schema_version = 3, id = "chg-" + Guid.NewGuid().ToString("N"), category = "validated_replica_phase4", policy_version = ReplicaPhase4Service.PolicyVersion, client_slug = "selftest", environment = "staging", model = "res.partner", target_models = new List<string> { "res.partner" }, integrity_hash = "altered", operations = new List<ChangeOperation> { new ChangeOperation { operation_id = "op-" + Guid.NewGuid().ToString("N"), type = "create_record_rule", model = "res.partner", parameters = JsonUtil.Dictionary("key", "tampered") } } };
                JsonUtil.WriteJsonFile(project.ChangeSetPath(phase4Tampered.id), phase4Tampered);
                bool phase4DispatchCorrect = false; try { new StudioService(project).ApplyPackage(phase4Tampered.id, "APLICAR " + phase4Tampered.id); } catch (ConsultantException error) { phase4DispatchCorrect = error.Code == "package_integrity_failed"; }
                Assert(phase4DispatchCorrect, "aplicador global enruta paquetes Fase 4 a su validador"); passed++;
                bool invalidAliasBlocked = false; try { ProjectStore.NormalizeProfileAlias("../admin"); } catch (ConsultantException error) { invalidAliasBlocked = error.Code == "invalid_profile_alias"; }
                Assert(invalidAliasBlocked, "alias de perfil de verificación confinado"); passed++;
                ChangeSet phase2Tampered = new ChangeSet { schema_version = 3, id = "chg-" + Guid.NewGuid().ToString("N"), category = "validated_replica_phase2", policy_version = ReplicaPhase2Service.PolicyVersion, client_slug = "selftest", environment = "staging", model = "res.partner", target_models = new List<string> { "res.partner" }, integrity_hash = "altered", operations = new List<ChangeOperation> { new ChangeOperation { operation_id = "op-" + Guid.NewGuid().ToString("N"), type = "create_custom_field", model = "res.partner", parameters = JsonUtil.Dictionary("key", "tampered") } } };
                JsonUtil.WriteJsonFile(project.ChangeSetPath(phase2Tampered.id), phase2Tampered);
                bool phase2DispatchCorrect = false;
                try { new StudioService(project).ApplyPackage(phase2Tampered.id, "APLICAR " + phase2Tampered.id); }
                catch (ConsultantException error) { phase2DispatchCorrect = error.Code == "package_integrity_failed"; }
                Assert(phase2DispatchCorrect, "aplicador global enruta paquetes Fase 2 a su validador"); passed++;
                StudioService studio = new StudioService(project);
                object runbook = studio.GenerateRunbook("staging", "webhook", "Webhook de prueba", "Demostrar que solo se documenta", new List<object> { "Revisar destino", "No ejecutar automáticamente" });
                Assert(Directory.GetFiles(Path.Combine(tempRoot, "cambios", "runbooks"), "*.md").Length == 5, "runbook sin ejecución");
                passed++;
                string tamperedId = "chg-" + Guid.NewGuid().ToString("N");
                ChangeSet tampered = new ChangeSet
                {
                    schema_version = 1,
                    id = tamperedId,
                    client_slug = "selftest",
                    environment = "staging",
                    category = "studio_custom_field",
                    status = "prepared",
                    model = "sale.order",
                    expires_at = DateTimeOffset.UtcNow.AddMinutes(15).ToString("o", CultureInfo.InvariantCulture),
                    operations = new List<ChangeOperation>
                    {
                        new ChangeOperation
                        {
                            operation_id = "op-" + Guid.NewGuid().ToString("N"),
                            type = "create_custom_field",
                            model = "sale.order",
                            status = "pending",
                            parameters = JsonUtil.Dictionary("name", "x_test", "field_description", "Test", "ttype", "char", "required", false, "readonly", false, "model_id", 1)
                        }
                    }
                };
                JsonUtil.WriteJsonFile(project.ChangeSetPath(tamperedId), tampered);
                bool tamperingBlocked = false;
                try { studio.ApplyPackage(tamperedId, "APLICAR " + tamperedId); }
                catch (ConsultantException error) { tamperingBlocked = error.Code == "tool_contract_violation"; }
                Assert(tamperingBlocked, "paquete manipulado fuera de allowlist");
                passed++;
                string foreignId = "chg-" + Guid.NewGuid().ToString("N");
                ChangeSet foreign = new ChangeSet
                {
                    schema_version = 1,
                    id = foreignId,
                    client_slug = "otro-cliente",
                    environment = "staging",
                    model = "sale.order",
                    operations = new List<ChangeOperation>
                    {
                        new ChangeOperation
                        {
                            operation_id = "op-" + Guid.NewGuid().ToString("N"),
                            type = "create_custom_field",
                            model = "sale.order",
                            parameters = JsonUtil.Dictionary("name", "x_foreign", "field_description", "Ajeno", "ttype", "char", "required", false, "readonly", false, "help", null)
                        }
                    }
                };
                JsonUtil.WriteJsonFile(project.ChangeSetPath(foreignId), foreign);
                bool tenantBlocked = false;
                try { studio.ApplyPackage(foreignId, "APLICAR " + foreignId); }
                catch (ConsultantException error) { tenantBlocked = error.Code == "package_tenant_mismatch"; }
                Assert(tenantBlocked, "aislamiento de paquetes por cliente");
                passed++;
                string expiredId = "chg-" + Guid.NewGuid().ToString("N");
                ChangeSet expired = new ChangeSet
                {
                    schema_version = 1,
                    id = expiredId,
                    client_slug = "selftest",
                    environment = "staging",
                    category = "studio_custom_field",
                    risk = "medium",
                    status = "prepared",
                    model = "sale.order",
                    expires_at = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("o", CultureInfo.InvariantCulture),
                    operations = new List<ChangeOperation>
                    {
                        new ChangeOperation
                        {
                            operation_id = "op-" + Guid.NewGuid().ToString("N"),
                            type = "create_custom_field",
                            model = "sale.order",
                            status = "pending",
                            parameters = JsonUtil.Dictionary("name", "x_expired", "field_description", "Vencido", "ttype", "char", "required", false, "readonly", false, "help", null)
                        }
                    }
                };
                JsonUtil.WriteJsonFile(project.ChangeSetPath(expiredId), expired);
                bool expirationBlocked = false;
                try { studio.ApplyPackage(expiredId, "APLICAR " + expiredId); }
                catch (ConsultantException error) { expirationBlocked = error.Code == "confirmation_expired"; }
                Assert(expirationBlocked, "vencimiento de confirmación");
                passed++;
                string integrityId = "chg-" + Guid.NewGuid().ToString("N");
                ChangeSet integrityTampered = new ChangeSet
                {
                    schema_version = 2,
                    id = integrityId,
                    client_slug = "selftest",
                    environment = "staging",
                    category = "declarative_change",
                    risk = "medium",
                    status = "prepared",
                    model = "sale.order",
                    created_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    expires_at = DateTimeOffset.UtcNow.AddMinutes(15).ToString("o", CultureInfo.InvariantCulture),
                    precondition_hash = "snapshot",
                    profile_fingerprint = "profile",
                    policy_version = PolicyCompiler.Version,
                    objective = "Prueba de integridad",
                    contract_hash = "contract",
                    capability_hash = "capability",
                    integrity_hash = "huella-manipulada",
                    operations = new List<ChangeOperation>
                    {
                        new ChangeOperation
                        {
                            operation_id = "op-" + Guid.NewGuid().ToString("N"),
                            type = "create_custom_field",
                            model = "sale.order",
                            status = "pending",
                            parameters = JsonUtil.Dictionary("name", "x_integrity", "field_description", "Integridad", "ttype", "char", "required", false, "readonly", false, "help", null)
                        }
                    }
                };
                JsonUtil.WriteJsonFile(project.ChangeSetPath(integrityId), integrityTampered);
                bool integrityBlocked = false;
                try { studio.ApplyPackage(integrityId, "APLICAR " + integrityId); }
                catch (ConsultantException error) { integrityBlocked = error.Code == "package_integrity_failed"; }
                Assert(integrityBlocked, "huella de integridad del paquete");
                passed++;
                AuditEvent one = AuditLog.Append(project, "staging", "selftest", null, "success", "primer evento", 1);
                AuditEvent two = AuditLog.Append(project, "staging", "selftest", null, "success", "segundo evento", 1);
                Assert(two.previous_hash == one.event_hash && !string.IsNullOrWhiteSpace(two.event_hash), "cadena de auditoría");
                passed++;
                bool contained = false;
                try { project.ContainedPath(Path.Combine("..", "escape.txt")); }
                catch (ConsultantException error) { contained = error.Code == "path_outside_project"; }
                Assert(contained, "contención de rutas");
                passed++;
            }
            finally
            {
                string tempPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string resolved = Path.GetFullPath(tempRoot);
                if (resolved.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("odoo-consultor-selftest-", StringComparison.Ordinal) && Directory.Exists(resolved))
                {
                    for (int retry = 0; retry < 5; retry++)
                    {
                        try { Directory.Delete(resolved, true); break; }
                        catch (IOException) { Thread.Sleep(50); }
                        catch (UnauthorizedAccessException) { Thread.Sleep(50); }
                    }
                }
            }
            Console.WriteLine("Self-test OdooConsultor: " + passed + " comprobaciones correctas.");
            return 0;
        }

        private static void Assert(bool condition, string label)
        {
            if (!condition) throw new Exception("Falló self-test: " + label);
        }
    }
}
