using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace OdooConsultor
{
    internal static class SelfTestMock
    {
        public static void VerifyJson2Client()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Exception serverError = null;
            string[] routes = new[]
            {
                "/json/2/res.users/context_get",
                "/json/2/res.users/read",
                "/json/2/ir.model.fields/create",
                "/json/2/ir.ui.view/write",
                "/json/2/sale.order/check_access_rights",
                "/doc",
                "/json/2/ir.actions.server/run",
                "/json/2/res.users/has_group",
                "/json/2/ir.module.module/button_immediate_install",
                "/json/2/ir.model.fields/search_read"
            };
            string[] responses = new[]
            {
                "{\"uid\":42}",
                "[{\"id\":42,\"login\":\"consultor@test.invalid\",\"name\":\"Consultor Test\"}]",
                "[101]",
                "true",
                "true",
                "<html><title>Odoo API</title></html>",
                "false",
                "true",
                "{\"type\":\"ir.actions.act_url\"}",
                "{\"message\":\"denied\"}"
            };
            Thread server = new Thread(delegate()
            {
                try
                {
                    for (int i = 0; i < routes.Length; i++)
                    {
                        using (TcpClient connection = listener.AcceptTcpClient())
                        using (NetworkStream stream = connection.GetStream())
                        {
                            string request = ReadRequest(stream);
                            string firstLine = request.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                            if (!firstLine.Contains(" " + routes[i] + " ")) throw new Exception("Ruta JSON-2 inesperada: " + firstLine);
                            if (request.IndexOf("Authorization: bearer clave-ficticia", StringComparison.OrdinalIgnoreCase) < 0) throw new Exception("Falta bearer en la llamada simulada.");
                            if (routes[i].EndsWith("/ir.actions.server/run", StringComparison.Ordinal) && (!request.Contains("\"active_model\":\"stock.picking\"") || !request.Contains("\"active_id\":77"))) throw new Exception("La UAT no envió el contexto cerrado esperado.");
                            if (routes[i].EndsWith("/res.users/has_group", StringComparison.Ordinal) && (!request.Contains("base.group_system") || !request.Contains("\"ids\":[42]"))) throw new Exception("La comprobación administrativa no usó el grupo fijo esperado.");
                            if (routes[i].EndsWith("/ir.module.module/button_immediate_install", StringComparison.Ordinal) && !request.Contains("\"ids\":[201,202]")) throw new Exception("La instalación no usó los IDs cerrados del plan.");
                            int status = i == routes.Length - 1 ? 403 : 200;
                            WriteResponse(stream, status, responses[i]);
                        }
                    }
                }
                catch (Exception error) { serverError = error; }
                finally { listener.Stop(); }
            });
            server.IsBackground = true;
            server.Start();

            ConnectionProfile profile = new ConnectionProfile
            {
                base_url = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture),
                database = "mock",
                login = "consultor@test.invalid"
            };
            using (OdooClient client = new OdooClient(profile, "  clave-ficticia  ", 5000))
            {
                Dictionary<string, object> context = client.ContextGet();
                if (Convert.ToInt64(context["uid"], CultureInfo.InvariantCulture) != 42) throw new Exception("context_get simulado no devolvió UID.");
                IList<Dictionary<string, object>> users = client.Read("res.users", new[] { 42L }, new[] { "login", "name" });
                if (users.Count != 1) throw new Exception("read simulado no devolvió usuario.");
                long created = client.Create("ir.model.fields", JsonUtil.Dictionary("name", "x_test"));
                if (created != 101) throw new Exception("create simulado no devolvió ID.");
                if (!client.Write("ir.ui.view", new[] { 101L }, JsonUtil.Dictionary("active", false))) throw new Exception("write simulado falló.");
                if (!client.CheckAccessRights("sale.order", "read")) throw new Exception("check_access_rights simulado falló.");
                Dictionary<string, object> documentation = client.ProbeDocumentation();
                if (!Convert.ToBoolean(documentation["available"], CultureInfo.InvariantCulture)) throw new Exception("El sondeo /doc simulado falló.");
                client.RunServerAction(55, "stock.picking", 77);
                if (!client.IsSystemAdministrator(42)) throw new Exception("has_group administrativo simulado falló.");
                client.InstallModules(new[] { 201L, 202L });
                bool denied = false;
                try { client.SearchRead("ir.model.fields", new object[0], new[] { "id" }, 1, null); }
                catch (ConsultantException error) { denied = error.Code == "odoo_request_failed"; }
                if (!denied) throw new Exception("El rechazo simulado no se cerró con error seguro.");
            }
            if (!server.Join(5000)) throw new Exception("El servidor JSON-2 simulado no terminó.");
            if (serverError != null) throw serverError;
            bool transportDiagnosed = false;
            try
            {
                using (OdooClient unavailable = new OdooClient(profile, "clave-ficticia", 1000)) unavailable.ContextGet();
            }
            catch (ConsultantException error)
            {
                transportDiagnosed = error.Code == "odoo_request_failed" && error.Message.Contains("Fallo de transporte");
            }
            if (!transportDiagnosed) throw new Exception("El fallo de transporte no devolvió un diagnóstico seguro.");
        }

        public static void VerifyLegacyRpcClient()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Exception serverError = null;
            string[] responses = new[]
            {
                "{\"jsonrpc\":\"2.0\",\"id\":0,\"result\":{\"server_version\":\"18.0+e\",\"server_version_info\":[18,0,0,\"final\",0,\"e\"]}}",
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":18}",
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"uid\":18,\"lang\":\"es_CO\"}}",
                "{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":[{\"id\":7,\"name\":\"Demo\"}]}",
                "{\"jsonrpc\":\"2.0\",\"id\":4,\"result\":91}",
                "{\"jsonrpc\":\"2.0\",\"id\":5,\"result\":[92]}",
                "{\"jsonrpc\":\"2.0\",\"id\":6,\"result\":true}",
                "{\"jsonrpc\":\"2.0\",\"id\":7,\"result\":true}",
                "{\"jsonrpc\":\"2.0\",\"id\":8,\"result\":{\"name\":{\"type\":\"char\",\"required\":true}}}",
                "{\"jsonrpc\":\"2.0\",\"id\":9,\"result\":true}",
                "{\"jsonrpc\":\"2.0\",\"id\":10,\"result\":true}",
                "{\"jsonrpc\":\"2.0\",\"id\":11,\"result\":{\"type\":\"ir.actions.act_url\"}}",
                "{\"jsonrpc\":\"2.0\",\"id\":12,\"error\":{\"code\":200,\"message\":\"Access denied\",\"data\":{\"name\":\"odoo.exceptions.ValidationError\",\"message\":\"Sensitive record detail\"}}}",
                "{\"jsonrpc\":\"2.0\",\"id\":13,\"error\":{\"code\":200,\"message\":\"Server error\",\"data\":{\"name\":\"builtins.TypeError\",\"message\":\"Sensitive type detail\",\"debug\":\"Traceback\\nTypeError: create() missing 1 required positional argument: 'secret-value'\"}}}"
            };
            Thread server = new Thread(delegate()
            {
                try
                {
                    for (int i = 0; i < responses.Length; i++)
                    {
                        using (TcpClient connection = listener.AcceptTcpClient())
                        using (NetworkStream stream = connection.GetStream())
                        {
                            string request = ReadRequest(stream);
                            string firstLine = request.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                            if (i == 0)
                            {
                                if (!firstLine.StartsWith("POST /web/webclient/version_info ", StringComparison.Ordinal) || !request.Contains("\"jsonrpc\":\"2.0\"")) throw new Exception("No se consultó la versión estándar de Odoo mediante JSON POST.");
                            }
                            else
                            {
                                if (!firstLine.Contains(" /jsonrpc ")) throw new Exception("Ruta RPC heredada inesperada.");
                                if (i == 1 && (!request.Contains("\"service\":\"common\"") || !request.Contains("\"authenticate\"") || !request.Contains("base_demo"))) throw new Exception("La autenticación RPC no usó el contrato esperado.");
                                if (i > 1 && !request.Contains("\"execute_kw\"")) throw new Exception("La operación RPC no usó execute_kw cerrado.");
                                if (i == 5 && (!request.Contains("\"survey.question.answer\",\"create\",[[{") || request.Contains("_closed_model_create_multi"))) throw new Exception("survey.question.answer no conservó vals_list como argumento model_create_multi cerrado.");
                                if (i == 6 && (!request.Contains("\"survey.survey\",\"write\"") || !request.Contains("question_and_page_ids") || !request.Contains("[0,0,{"))) throw new Exception("La creación nativa de preguntas no usó el comando cerrado sobre la encuesta padre.");
                                if (i == 10 && (!request.Contains("\"res.users\",\"has_group\"") || !request.Contains("base.group_system"))) throw new Exception("La comprobación administrativa RPC no usó el contrato fijo.");
                                if (i == 11 && (!request.Contains("\"ir.module.module\",\"button_immediate_install\"") || !request.Contains("[201,202]"))) throw new Exception("La instalación RPC no usó el contrato fijo.");
                            }
                            WriteResponse(stream, 200, responses[i]);
                        }
                    }
                }
                catch (Exception error) { serverError = error; }
                finally { listener.Stop(); }
            });
            server.IsBackground = true;
            server.Start();

            string baseUrl = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
            Dictionary<string, object> detected = OdooClient.DetectInstance(baseUrl, 5000);
            if (Convert.ToString(detected["server_series"], CultureInfo.InvariantCulture) != "18.0" || Convert.ToString(detected["transport"], CultureInfo.InvariantCulture) != "legacy_rpc") throw new Exception("La selección automática de transporte Odoo 18 falló.");
            ConnectionProfile profile = new ConnectionProfile { base_url = baseUrl, database = "base_demo", login = "consultor", api_transport = "legacy_rpc" };
            using (OdooClient client = new OdooClient(profile, "clave-ficticia", 5000))
            {
                Dictionary<string, object> context = client.ContextGet();
                if (Convert.ToInt64(context["uid"], CultureInfo.InvariantCulture) != 18) throw new Exception("context_get RPC no devolvió UID.");
                if (client.SearchRead("utm.source", new object[0], new[] { "id", "name" }, 1, null).Count != 1) throw new Exception("search_read RPC falló.");
                if (client.Create("utm.source", JsonUtil.Dictionary("name", "[DEMO]")) != 91) throw new Exception("create RPC falló.");
                if (client.CreateSurveyComponent("survey.question.answer", JsonUtil.Dictionary("value", "[DEMO] Respuesta", "question_id", 10)) != 92) throw new Exception("model_create_multi de respuestas de Encuestas RPC falló.");
                if (!client.Write("survey.survey", new[] { 10L }, JsonUtil.Dictionary("question_and_page_ids", new object[] { new object[] { 0, 0, JsonUtil.Dictionary("title", "[DEMO] Pregunta") } }))) throw new Exception("write relacional de preguntas RPC falló.");
                if (!client.Write("utm.source", new[] { 91L }, JsonUtil.Dictionary("active", false))) throw new Exception("write RPC falló.");
                if (!client.FieldsGet("utm.source", new[] { "type", "required" }).ContainsKey("name")) throw new Exception("fields_get RPC falló.");
                if (!client.CheckAccessRights("utm.source", "read")) throw new Exception("check_access_rights RPC falló.");
                if (!client.IsSystemAdministrator(18)) throw new Exception("has_group RPC falló.");
                client.InstallModules(new[] { 201L, 202L });
                bool safeError = false;
                try { client.SearchRead("utm.source", new object[0], new[] { "id" }, 1, null); }
                catch (ConsultantException error) { safeError = error.Code == "odoo_rpc_error" && error.Message.Contains("ValidationError") && !error.Message.Contains("Access denied") && !error.Message.Contains("Sensitive record detail"); }
                if (!safeError) throw new Exception("El error RPC no fue redactado o cerrado.");
                bool structuralTypeError = false;
                try { client.SearchRead("utm.source", new object[0], new[] { "id" }, 1, null); }
                catch (ConsultantException error) { structuralTypeError = error.Code == "odoo_rpc_error" && error.Message.Contains("TypeError") && error.Message.Contains("detalle estructural") && error.Message.Contains("<redacted>") && !error.Message.Contains("secret-value") && !error.Message.Contains("Sensitive type detail"); }
                if (!structuralTypeError) throw new Exception("El detalle estructural TypeError no fue redactado correctamente.");
                Dictionary<string, object> documentation = client.ProbeDocumentation();
                if (Convert.ToBoolean(documentation["available"], CultureInfo.InvariantCulture)) throw new Exception("Odoo 18 no debe anunciar /doc.");
            }
            if (!server.Join(5000)) throw new Exception("El servidor RPC simulado no terminó.");
            if (serverError != null) throw serverError;
        }

        public static int VerifyFunctionalLifecycle(ProjectStore project)
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Exception serverError = null;
            int requestCount = 0;
            int createCount = 0;
            ManualResetEvent stopSignal = new ManualResetEvent(false);
            Dictionary<string, object> stage = null;
            Thread server = new Thread(delegate()
            {
                try
                {
                    while (!stopSignal.WaitOne(5))
                    {
                        if (!listener.Pending()) continue;
                        using (TcpClient connection = listener.AcceptTcpClient())
                        using (NetworkStream stream = connection.GetStream())
                        {
                            string request = ReadRequest(stream);
                            Interlocked.Increment(ref requestCount);
                            string firstLine = request.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                            if (firstLine.Contains("/unlink ")) throw new Exception("La parametrización funcional intentó eliminar registros.");
                            string[] lineParts = firstLine.Split(' ');
                            string[] route = lineParts[1].Trim('/').Split('/');
                            if (route.Length != 4 || route[0] != "json" || route[1] != "2") throw new Exception("Ruta funcional inesperada: " + firstLine);
                            string model = Uri.UnescapeDataString(route[2]);
                            string method = Uri.UnescapeDataString(route[3]);
                            Dictionary<string, object> body = RequestBody(request);
                            string response;
                            if (method == "check_access_rights") response = "true";
                            else if (method == "fields_get")
                            {
                                Dictionary<string, object> fields = new Dictionary<string, object>(StringComparer.Ordinal);
                                fields["name"] = DemoField("char", true, null, null);
                                fields["active"] = DemoField("boolean", false, null, null);
                                if (model == "crm.stage")
                                {
                                    fields["sequence"] = DemoField("integer", false, null, null);
                                    fields["fold"] = DemoField("boolean", false, null, null);
                                }
                                response = JsonUtil.Serialize(fields);
                            }
                            else if (method == "search_read")
                            {
                                if (model == "ir.module.module") response = "[{\"id\":1,\"name\":\"crm\",\"state\":\"installed\",\"installed_version\":\"19.0\"},{\"id\":2,\"name\":\"sales_team\",\"state\":\"installed\",\"installed_version\":\"19.0\"}]";
                                else if (model == "ir.model") response = "[{\"id\":1,\"model\":\"crm.lead\",\"name\":\"Lead\",\"state\":\"base\"},{\"id\":2,\"model\":\"crm.stage\",\"name\":\"Stage\",\"state\":\"base\"},{\"id\":3,\"model\":\"crm.team\",\"name\":\"Team\",\"state\":\"base\"}]";
                                else if (model == "crm.stage") response = stage == null ? "[]" : "[" + JsonUtil.Serialize(stage) + "]";
                                else response = "[]";
                            }
                            else if (method == "create")
                            {
                                Dictionary<string, object> values = FirstDictionary(body.ContainsKey("vals_list") ? body["vals_list"] : null);
                                stage = new Dictionary<string, object>(values, StringComparer.Ordinal);
                                stage["id"] = 501;
                                Interlocked.Increment(ref createCount);
                                response = "[501]";
                            }
                            else if (method == "read") response = stage == null ? "[]" : "[" + JsonUtil.Serialize(stage) + "]";
                            else if (method == "write")
                            {
                                Dictionary<string, object> values = body.ContainsKey("vals") ? body["vals"] as Dictionary<string, object> : null;
                                if (stage != null && values != null) foreach (KeyValuePair<string, object> pair in values) stage[pair.Key] = pair.Value;
                                response = "true";
                            }
                            else throw new Exception("Método funcional inesperado: " + method);
                            WriteResponse(stream, 200, response);
                        }
                    }
                }
                catch (Exception error) { serverError = error; }
                finally { listener.Stop(); }
            });
            server.IsBackground = true;
            server.Start();

            string target = "OdooAI/SelfTestFunctional/" + Guid.NewGuid().ToString("N");
            string currentStep = "credential";
            try
            {
                CredentialStore.Write(target, "functional", "clave-ficticia");
                project.SaveProfile(new ConnectionProfile
                {
                    schema_version = 2, client_slug = project.Config.client_slug, environment = "staging", credential_target = target,
                    base_url = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture), login = "functional", uid = 2,
                    read_only = false, credential_fingerprint = JsonUtil.Sha256("clave-ficticia"), server_version = "19.0", server_series = "19.0", api_transport = "json2"
                });
                FunctionalService service = new FunctionalService(project);
                currentStep = "inventory";
                Dictionary<string, object> inventory = service.Inventory("staging", "crm") as Dictionary<string, object>;
                if (Convert.ToString(inventory["evidence"], CultureInfo.InvariantCulture) != "verificado_base") throw new Exception("El inventario no clasificó la evidencia real.");
                currentStep = "diagnose";
                Dictionary<string, object> diagnostic = service.Diagnose("staging", "crm_pipeline", JsonUtil.Dictionary(
                    "schema_version", 1, "application", "crm", "objective", "Preparar el embudo comercial",
                    "requirements", new object[]
                    {
                        JsonUtil.Dictionary("key", "crm-installed", "kind", "module", "module", "crm", "expected_state", "installed"),
                        JsonUtil.Dictionary("key", "qualified-stage", "kind", "configuration", "adapter", "crm_stage", "match_name", "[CFG] Calificado", "desired_state", "present", "desired", JsonUtil.Dictionary("sequence", 20, "fold", false))
                    })) as Dictionary<string, object>;
                string diagnosticId = Convert.ToString(diagnostic["diagnostic_id"], CultureInfo.InvariantCulture);
                if (Convert.ToString(diagnostic["status"], CultureInfo.InvariantCulture) != "gaps_found") throw new Exception("El diagnóstico no detectó la etapa faltante.");
                currentStep = "prepare";
                Dictionary<string, object> package = service.Prepare(diagnosticId, new[] { "qualified-stage" }) as Dictionary<string, object>;
                string packageId = Convert.ToString(package["package_id"], CultureInfo.InvariantCulture);
                ChangeSet change = JsonUtil.ReadJsonFile<ChangeSet>(project.ChangeSetPath(packageId));
                currentStep = "confirmation";
                bool confirmationBlocked = false;
                try { service.Apply(change, "APLICAR incorrecto"); }
                catch (ConsultantException error) { confirmationBlocked = error.Code == "confirmation_mismatch"; }
                if (!confirmationBlocked) throw new Exception("La parametrización aceptó una confirmación incorrecta.");
                currentStep = "apply";
                Dictionary<string, object> applied = service.Apply(change, "APLICAR " + packageId) as Dictionary<string, object>;
                if (Convert.ToString(applied["status"], CultureInfo.InvariantCulture) != "applied" || createCount != 1) throw new Exception("La parametrización no creó exactamente una etapa.");
                int afterApply = requestCount;
                currentStep = "idempotency";
                change = JsonUtil.ReadJsonFile<ChangeSet>(project.ChangeSetPath(packageId));
                service.Apply(change, "APLICAR " + packageId);
                if (requestCount != afterApply || createCount != 1) throw new Exception("El reintento funcional hizo llamadas o duplicó la configuración.");
                currentStep = "verify";
                change = JsonUtil.ReadJsonFile<ChangeSet>(project.ChangeSetPath(packageId));
                Dictionary<string, object> verified = service.Verify(change) as Dictionary<string, object>;
                if (Convert.ToString(verified["status"], CultureInfo.InvariantCulture) != "verified") throw new Exception("La parametrización no verificó por lectura.");
                currentStep = "rollback";
                change = JsonUtil.ReadJsonFile<ChangeSet>(project.ChangeSetPath(packageId));
                Dictionary<string, object> rolledBack = service.Rollback(change, "REVERTIR " + packageId) as Dictionary<string, object>;
                if (Convert.ToString(rolledBack["status"], CultureInfo.InvariantCulture) != "rolled_back" || stage == null || Convert.ToBoolean(stage["active"], CultureInfo.InvariantCulture)) throw new Exception("La reversión no archivó la configuración creada.");
                currentStep = "rollback-verify";
                change = JsonUtil.ReadJsonFile<ChangeSet>(project.ChangeSetPath(packageId));
                service.Verify(change);
            }
            catch (Exception error) { throw new Exception("Ciclo funcional en etapa " + currentStep + ": " + error.Message, error); }
            finally
            {
                CredentialStore.Delete(target);
                project.DeleteProfile("staging");
                stopSignal.Set();
                listener.Stop();
            }
            if (!server.Join(5000)) { listener.Stop(); throw new Exception("El servidor funcional simulado no terminó."); }
            if (serverError != null) throw serverError;
            return 7;
        }

        public static int VerifyDemoLifecycle(ProjectStore project)
        {
            if (!DemoService.ShouldSkipSurveySectionForSelfTest("17.0", "survey_section") || !DemoService.ShouldSkipSurveySectionForSelfTest("18.0", "survey_section") || DemoService.ShouldSkipSurveySectionForSelfTest("19.0", "survey_section") || DemoService.ShouldSkipSurveySectionForSelfTest("18.0", "survey_question")) throw new Exception("La compatibilidad cerrada de secciones Survey no distingue Odoo 17/18 de Odoo 19.");
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Exception serverError = null;
            int requestCount = 0;
            int nextId = 100;
            int denyCreate = 0;
            int denyRegistryCreate = 0;
            int failCreateAt = 0;
            int createAttempt = 0;
            ManualResetEvent stopSignal = new ManualResetEvent(false);
            Dictionary<string, Dictionary<string, object>> records = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            Thread server = new Thread(delegate()
            {
                try
                {
                    while (!stopSignal.WaitOne(5))
                    {
                        if (!listener.Pending()) continue;
                        using (TcpClient connection = listener.AcceptTcpClient())
                        using (NetworkStream stream = connection.GetStream())
                        {
                            string request = ReadRequest(stream);
                            requestCount++;
                            string firstLine = request.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                            string response;
                            int responseStatus = 200;
                            if (firstLine.Contains("/unlink ")) throw new Exception("La demo intentó eliminar registros.");
                            if (firstLine.Contains("/web/webclient/version_info ")) response = "{\"jsonrpc\":\"2.0\",\"result\":{\"server_version\":\"19.0\",\"server_version_info\":[19,0,0,\"final\",0]}}";
                            else
                            {
                                string[] lineParts = firstLine.Split(' ');
                                string[] route = lineParts[1].Trim('/').Split('/');
                                if (route.Length != 4 || route[0] != "json" || route[1] != "2") throw new Exception("Ruta de demo inesperada: " + firstLine);
                                string model = Uri.UnescapeDataString(route[2]);
                                string method = Uri.UnescapeDataString(route[3]);
                                Dictionary<string, object> body = RequestBody(request);
                                if (method == "fields_get") response = DemoFieldsResponse();
                                else if (method == "check_access_rights") response = (Interlocked.CompareExchange(ref denyCreate, 0, 0) == 1 || model == "ir.model.data" && Interlocked.CompareExchange(ref denyRegistryCreate, 0, 0) == 1) && JsonUtil.GetString(body, "operation", false) == "create" ? "false" : "true";
                                else if (method == "search_read")
                                {
                                    if (model == "ir.module.module") response = "[{\"id\":1,\"name\":\"mock\",\"state\":\"installed\"}]";
                                    else if (model == "website") response = "[{\"id\":7,\"name\":\"Demo Website\"}]";
                                    else if (model == "ir.model") response = "[{\"id\":901,\"model\":\"mailing.contact\"}]";
                                    else if (model == "ir.model.data")
                                    {
                                        string registryModule = DomainString(body, "module");
                                        string registryName = DomainString(body, "name");
                                        List<Dictionary<string, object>> matches = records.Where(x => x.Key.StartsWith("ir.model.data:", StringComparison.Ordinal) && Convert.ToString(x.Value.ContainsKey("module") ? x.Value["module"] : null, CultureInfo.InvariantCulture) == registryModule && Convert.ToString(x.Value.ContainsKey("name") ? x.Value["name"] : null, CultureInfo.InvariantCulture) == registryName).Select(x =>
                                        {
                                            Dictionary<string, object> row = new Dictionary<string, object>(x.Value, StringComparer.Ordinal);
                                            row["id"] = Convert.ToInt64(x.Key.Substring("ir.model.data:".Length), CultureInfo.InvariantCulture);
                                            return row;
                                        }).ToList();
                                        response = JsonUtil.Serialize(matches);
                                    }
                                    else
                                    {
                                        List<Dictionary<string, object>> matches = records.Where(x => x.Key.StartsWith(model + ":", StringComparison.Ordinal) && MatchesDomain(x.Value, body)).Take(2).Select(x => JsonUtil.Dictionary("id", Convert.ToInt64(x.Key.Substring(model.Length + 1), CultureInfo.InvariantCulture))).ToList();
                                        response = JsonUtil.Serialize(matches);
                                    }
                                }
                                else if (method == "create")
                                {
                                    int currentAttempt = Interlocked.Increment(ref createAttempt);
                                    if (Interlocked.CompareExchange(ref failCreateAt, 0, 0) == currentAttempt)
                                    {
                                        responseStatus = 500;
                                        response = "{\"message\":\"synthetic partial failure\"}";
                                        WriteResponse(stream, responseStatus, response);
                                        continue;
                                    }
                                    Dictionary<string, object> values = FirstDictionary(body.ContainsKey("vals_list") ? body["vals_list"] : null);
                                    if (model == "survey.question" && values.ContainsKey("is_page") && Convert.ToBoolean(values["is_page"], CultureInfo.InvariantCulture) && (!values.ContainsKey("question_type") || !(values["question_type"] is bool) || Convert.ToBoolean(values["question_type"], CultureInfo.InvariantCulture)))
                                    {
                                        response = "{\"error\":{\"message\":\"Question type should be empty for pages\"}}";
                                        WriteResponse(stream, responseStatus, response);
                                        continue;
                                    }
                                    int id = nextId++;
                                    records[model + ":" + id.ToString(CultureInfo.InvariantCulture)] = new Dictionary<string, object>(values, StringComparer.Ordinal);
                                    response = "[" + id.ToString(CultureInfo.InvariantCulture) + "]";
                                }
                                else if (method == "read")
                                {
                                    long id = FirstLong(body.ContainsKey("ids") ? body["ids"] : null);
                                    Dictionary<string, object> stored;
                                    if (!records.TryGetValue(model + ":" + id.ToString(CultureInfo.InvariantCulture), out stored)) response = "[]";
                                    else
                                    {
                                        Dictionary<string, object> row = new Dictionary<string, object>(StringComparer.Ordinal) { { "id", id }, { "display_name", "[DEMO]" } };
                                        foreach (string field in StringValues(body.ContainsKey("fields") ? body["fields"] : null)) if (stored.ContainsKey(field)) row[field] = stored[field];
                                        response = "[" + JsonUtil.Serialize(row) + "]";
                                    }
                                }
                                else if (method == "write")
                                {
                                    long id = FirstLong(body.ContainsKey("ids") ? body["ids"] : null);
                                    Dictionary<string, object> stored;
                                    if (records.TryGetValue(model + ":" + id.ToString(CultureInfo.InvariantCulture), out stored))
                                    {
                                        Dictionary<string, object> values = body.ContainsKey("vals") ? body["vals"] as Dictionary<string, object> : null;
                                        if (values != null) foreach (KeyValuePair<string, object> pair in values) stored[pair.Key] = pair.Value;
                                    }
                                    response = "true";
                                }
                                else throw new Exception("Método de demo inesperado: " + method);
                            }
                            WriteResponse(stream, responseStatus, response);
                        }
                    }
                }
                catch (Exception error) { serverError = error; }
                finally { listener.Stop(); }
            });
            server.IsBackground = true;
            server.Start();

            string target = "OdooAI/SelfTestDemo/" + Guid.NewGuid().ToString("N");
            string stage = "credential";
            try
            {
                CredentialStore.Write(target, "demo", "clave-ficticia");
                stage = "profile";
                project.SaveProfile(new ConnectionProfile
                {
                    schema_version = 2, client_slug = project.Config.client_slug, environment = "staging", credential_target = target,
                    base_url = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture), login = "demo", uid = 2,
                    read_only = false, credential_fingerprint = JsonUtil.Sha256("clave-ficticia"), server_version = "19.0", server_series = "19.0", api_transport = "json2"
                });
                stage = "source-lock";
                string demoRoot = project.DemoDirectory("marketing_seguro", true);
                Dictionary<string, object> sourceLock = JsonUtil.Dictionary("schema_version", 1, "odoo_version", "19.0", "repositories", new object[0]);
                sourceLock["lock_hash"] = JsonUtil.CanonicalHash(sourceLock);
                JsonUtil.WriteJsonFile(Path.Combine(demoRoot, "source-lock.json"), sourceLock);
                Directory.CreateDirectory(Path.Combine(demoRoot, "assets"));
                byte[] hero = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
                string heroPath = Path.Combine(demoRoot, "assets", "hero.png");
                File.WriteAllBytes(heroPath, hero);
                string heroHash;
                using (SHA256 sha = SHA256.Create()) heroHash = string.Concat(sha.ComputeHash(hero).Select(x => x.ToString("x2", CultureInfo.InvariantCulture)).ToArray());
                Array.Clear(hero, 0, hero.Length);

                DemoService demos = new DemoService(project);
                stage = "prepare";
                Dictionary<string, object> configuration = JsonUtil.Dictionary(
                    "utm", JsonUtil.Dictionary("source_name", "Meta Ads", "medium_name", "Social pagado", "campaign_name", "Diagnóstico B2B"),
                    "mailing", JsonUtil.Dictionary("image_required", true, "image_asset", JsonUtil.Dictionary("file", "hero.png", "sha256", heroHash, "alt", "Imagen sintética del producto"), "personalization_variable", "recipient_name"));
                Dictionary<string, object> prepared = demos.Prepare("staging", "marketing_seguro", "marketing", "Demostrar marketing con datos ficticios", new[] { "Captación", "Automatización", "Métricas" }, configuration) as Dictionary<string, object>;
                string scenarioId = Convert.ToString(prepared["scenario_id"], CultureInfo.InvariantCulture);
                if (!Convert.ToString(prepared["next_step"], CultureInfo.InvariantCulture).Contains("CREAR-DEMO " + scenarioId)) throw new Exception("La demo no devolvió confirmación exacta.");
                DemoScenario configured = JsonUtil.ReadJsonFile<DemoScenario>(project.DemoScenarioPath(scenarioId));
                if (configured.schema_version != 4 || configured.delivery_mode != "database_records" || configured.demo_number <= 0 || configured.demo_label != "DEMO " + configured.demo_number.ToString(CultureInfo.InvariantCulture)) throw new Exception("La demo no usa el contrato de registros schema 4.");
                if (Convert.ToString(configured.components.First(x => x.key == "source").values["name"], CultureInfo.InvariantCulture) != "Meta Ads" || Convert.ToString(configured.components.First(x => x.key == "medium").values["name"], CultureInfo.InvariantCulture) != "Social pagado" || Convert.ToString(configured.components.First(x => x.key == "campaign").values["name"], CultureInfo.InvariantCulture) != "Diagnóstico B2B") throw new Exception("La demo no conservó nombres UTM naturales.");
                string visibleValues = JsonUtil.Serialize(configured.components.Select(x => x.values).ToArray());
                if (visibleValues.IndexOf("[DEMO:", StringComparison.OrdinalIgnoreCase) >= 0 || visibleValues.IndexOf(configured.demo_label, StringComparison.OrdinalIgnoreCase) >= 0 || Regex.IsMatch(visibleValues, "demo-[a-f0-9]{8,}", RegexOptions.IgnoreCase)) throw new Exception("La demo expone su identidad técnica en campos visibles.");
                stage = "wrong-confirmation";
                bool wrongConfirmationBlocked = false;
                try { demos.Apply(scenarioId, "CREAR-DEMO incorrecto"); }
                catch (ConsultantException error) { wrongConfirmationBlocked = error.Code == "confirmation_required"; }
                if (!wrongConfirmationBlocked) throw new Exception("La demo aceptó una confirmación incorrecta.");
                stage = "source-lock-change";
                string lockPath = Path.Combine(demoRoot, "source-lock.json");
                string originalLock = File.ReadAllText(lockPath, Encoding.UTF8);
                File.WriteAllText(lockPath, originalLock + " ", new UTF8Encoding(false));
                bool changedLockBlocked = false;
                try { demos.Apply(scenarioId, "CREAR-DEMO " + scenarioId); }
                catch (ConsultantException error) { changedLockBlocked = error.Code == "source_lock_changed"; }
                File.WriteAllText(lockPath, originalLock, new UTF8Encoding(false));
                if (!changedLockBlocked) throw new Exception("La demo aceptó un source lock modificado.");
                stage = "capability-change";
                Interlocked.Exchange(ref denyCreate, 1);
                bool capabilityBlocked = false;
                try { demos.Apply(scenarioId, "CREAR-DEMO " + scenarioId); }
                catch (ConsultantException error) { capabilityBlocked = error.Code == "demo_capabilities_changed"; }
                Interlocked.Exchange(ref denyCreate, 0);
                if (!capabilityBlocked) throw new Exception("La demo aceptó permisos cambiados.");
                stage = "partial-failure";
                Interlocked.Exchange(ref createAttempt, 0);
                Interlocked.Exchange(ref failCreateAt, 3);
                bool partialFailed = false;
                try { demos.Apply(scenarioId, "CREAR-DEMO " + scenarioId); }
                catch (ConsultantException) { partialFailed = true; }
                Interlocked.Exchange(ref failCreateAt, 0);
                if (!partialFailed) throw new Exception("No se simuló el fallo parcial esperado.");
                stage = "verify-partial";
                Dictionary<string, object> partialVerification = demos.Verify(scenarioId) as Dictionary<string, object>;
                if (Convert.ToString(partialVerification["status"], CultureInfo.InvariantCulture) != "failed_partial") throw new Exception("La verificación parcial dejó el escenario sin posibilidad de recuperación.");
                stage = "fresh-reconfirmation";
                DemoScenario expiredPartial = JsonUtil.ReadJsonFile<DemoScenario>(project.DemoScenarioPath(scenarioId));
                expiredPartial.expires_at = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("o", CultureInfo.InvariantCulture);
                DemoService.SignForSelfTest(expiredPartial);
                JsonUtil.WriteJsonFile(project.DemoScenarioPath(scenarioId), expiredPartial);
                Dictionary<string, object> applied = demos.Apply(scenarioId, "CREAR-DEMO " + scenarioId) as Dictionary<string, object>;
                if (Convert.ToString(applied["status"], CultureInfo.InvariantCulture) != "applied") throw new Exception("La demo no quedó aplicada.");
                DateTimeOffset renewedExpiry;
                if (!DateTimeOffset.TryParse(Convert.ToString(applied["expires_at"], CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out renewedExpiry) || renewedExpiry <= DateTimeOffset.UtcNow) throw new Exception("La confirmación nueva no renovó la ventana del escenario parcial.");
                stage = "registry-recovery";
                DemoScenario recovery = JsonUtil.ReadJsonFile<DemoScenario>(project.DemoScenarioPath(scenarioId));
                Dictionary<string, object> recoveryRecords = ProtectedSnapshot.Unprotect(recovery.protected_records);
                int sourcesBeforeRecovery = records.Keys.Count(x => x.StartsWith("utm.source:", StringComparison.Ordinal));
                recoveryRecords.Remove("source");
                recovery.protected_records = ProtectedSnapshot.Protect(recoveryRecords);
                recovery.components.First(x => x.key == "source").status = "planned";
                recovery.status = "failed_partial";
                DemoService.SignForSelfTest(recovery);
                JsonUtil.WriteJsonFile(project.DemoScenarioPath(scenarioId), recovery);
                demos.Apply(scenarioId, "CREAR-DEMO " + scenarioId);
                DemoScenario recovered = JsonUtil.ReadJsonFile<DemoScenario>(project.DemoScenarioPath(scenarioId));
                Dictionary<string, object> recoveredRecords = ProtectedSnapshot.Unprotect(recovered.protected_records);
                Dictionary<string, object> recoveredSource = recoveredRecords["source"] as Dictionary<string, object>;
                if (records.Keys.Count(x => x.StartsWith("utm.source:", StringComparison.Ordinal)) != sourcesBeforeRecovery || recoveredSource == null || !recoveredSource.ContainsKey("registry_id")) throw new Exception("La recuperación por XML ID duplicó o no protegió el registro.");
                stage = "expired-confirmation";
                DemoScenario expiring = JsonUtil.ReadJsonFile<DemoScenario>(project.DemoScenarioPath(scenarioId));
                string originalExpiry = expiring.expires_at;
                expiring.expires_at = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("o", CultureInfo.InvariantCulture);
                DemoService.SignForSelfTest(expiring);
                JsonUtil.WriteJsonFile(project.DemoScenarioPath(scenarioId), expiring);
                bool expiryBlocked = false;
                try { demos.Apply(scenarioId, "CREAR-DEMO " + scenarioId); }
                catch (ConsultantException error) { expiryBlocked = error.Code == "confirmation_expired"; }
                expiring.expires_at = originalExpiry;
                DemoService.SignForSelfTest(expiring);
                JsonUtil.WriteJsonFile(project.DemoScenarioPath(scenarioId), expiring);
                if (!expiryBlocked) throw new Exception("La demo aceptó una confirmación vencida.");
                int afterFirstApply = requestCount;
                stage = "idempotency";
                demos.Apply(scenarioId, "CREAR-DEMO " + scenarioId);
                if (requestCount != afterFirstApply) throw new Exception("El reintento de demo hizo llamadas y pudo duplicar registros.");
                if (records.Keys.Count(x => x.StartsWith("ir.attachment:", StringComparison.Ordinal)) != 1 || records.Keys.Count(x => x.StartsWith("mailing.mailing:", StringComparison.Ordinal)) < 2) throw new Exception("La aplicación idempotente duplicó o perdió el recurso visual o los mailings.");
                if (records.Keys.Count(x => x.StartsWith("ir.model.data:", StringComparison.Ordinal)) < configured.components.Count) throw new Exception("La identidad híbrida no registró todos los componentes creados.");
                stage = "business-collision";
                Dictionary<string, object> duplicatePreview = demos.Prepare("staging", "marketing_seguro", "marketing", "Segunda demostración con nombres existentes", new[] { "Captación" }, configuration) as Dictionary<string, object>;
                string duplicateId = Convert.ToString(duplicatePreview["scenario_id"], CultureInfo.InvariantCulture);
                bool collisionBlocked = false;
                try { demos.Apply(duplicateId, "CREAR-DEMO " + duplicateId); }
                catch (ConsultantException error) { collisionBlocked = error.Code == "demo_business_collision"; }
                if (!collisionBlocked || records.Keys.Count(x => x.StartsWith("utm.source:", StringComparison.Ordinal)) != sourcesBeforeRecovery) throw new Exception("La colisión comercial reutilizó o duplicó un registro real.");
                stage = "local-only-fallback";
                Interlocked.Exchange(ref denyRegistryCreate, 1);
                Dictionary<string, object> localConfiguration = JsonUtil.Dictionary(
                    "utm", JsonUtil.Dictionary("source_name", "Evento sectorial", "medium_name", "Evento", "campaign_name", "Encuentro empresarial", "lead_name", "Solicitud de diagnóstico", "contact_name", "María Torres"),
                    "website", JsonUtil.Dictionary("enabled", false), "survey", JsonUtil.Dictionary("enabled", false), "mailing", JsonUtil.Dictionary("enabled", false), "automation", JsonUtil.Dictionary("enabled", false));
                Dictionary<string, object> localPreview = demos.Prepare("staging", "marketing_seguro", "marketing", "Validar protección local", new[] { "Captación" }, localConfiguration) as Dictionary<string, object>;
                string localId = Convert.ToString(localPreview["scenario_id"], CultureInfo.InvariantCulture);
                if (Convert.ToString(localPreview["registry_status"], CultureInfo.InvariantCulture) != "local_only") throw new Exception("La vista previa no informó el fallback local.");
                int registryBeforeLocal = records.Keys.Count(x => x.StartsWith("ir.model.data:", StringComparison.Ordinal));
                Dictionary<string, object> localApplied = demos.Apply(localId, "CREAR-DEMO " + localId) as Dictionary<string, object>;
                DemoScenario localStored = JsonUtil.ReadJsonFile<DemoScenario>(project.DemoScenarioPath(localId));
                Dictionary<string, object> localRecords = ProtectedSnapshot.Unprotect(localStored.protected_records);
                if (Convert.ToString(localApplied["status"], CultureInfo.InvariantCulture) != "applied" || records.Keys.Count(x => x.StartsWith("ir.model.data:", StringComparison.Ordinal)) != registryBeforeLocal || localRecords.Values.Cast<Dictionary<string, object>>().Any(x => x.ContainsKey("registry_id"))) throw new Exception("El fallback local creó XML IDs o dejó registros sin protección DPAPI.");
                Interlocked.Exchange(ref denyRegistryCreate, 0);
                stage = "verify";
                Dictionary<string, object> verified = demos.Verify(scenarioId) as Dictionary<string, object>;
                if (Convert.ToString(verified["status"], CultureInfo.InvariantCulture) != "verified")
                {
                    DemoScenario failedVerification = JsonUtil.ReadJsonFile<DemoScenario>(project.DemoScenarioPath(scenarioId));
                    throw new Exception("La demo no verificó: " + string.Join(",", failedVerification.components.Where(x => x.status != "verified" && x.status != "skipped_optional").Select(x => x.key + "=" + x.editor_validation).ToArray()));
                }
                stage = "cleanup";
                Dictionary<string, object> cleaned = demos.Clean(scenarioId, "LIMPIAR-DEMO " + scenarioId) as Dictionary<string, object>;
                if (Convert.ToString(cleaned["status"], CultureInfo.InvariantCulture) != "cleanup_partial") throw new Exception("La demo no identificó la contención manual de componentes sin archivo seguro.");

                stage = "evidence";
                DemoScenario stored = JsonUtil.ReadJsonFile<DemoScenario>(project.DemoScenarioPath(scenarioId));
                if (string.IsNullOrWhiteSpace(stored.protected_records) || stored.protected_records.TrimStart().StartsWith("{", StringComparison.Ordinal)) throw new Exception("Los IDs protegidos quedaron legibles.");
                string audit = File.ReadAllText(project.ContainedPath(Path.Combine("evidencias", "auditoria.jsonl")), Encoding.UTF8);
                if (audit.Contains("example.invalid") || audit.Contains("Persona de demostración") || audit.Contains("\"id\":100")) throw new Exception("La auditoría de demo contiene datos sintéticos o IDs.");
            }
            catch (Exception error) { throw new Exception("Ciclo demo en etapa " + stage + ": " + error.Message, error); }
            finally
            {
                CredentialStore.Delete(target);
                project.DeleteProfile("staging");
                stopSignal.Set();
                listener.Stop();
            }
            if (!server.Join(5000)) { listener.Stop(); throw new Exception("El servidor de demo simulado no terminó."); }
            if (serverError != null) throw serverError;
            if (requestCount < 100) throw new Exception("El ciclo comercial v2 no ejercitó todos los contratos cerrados esperados.");
            return 6;
        }

        public static int VerifyModuleLifecycle(ProjectStore project)
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Exception serverError = null;
            int requestCount = 0;
            int installCount = 0;
            bool commissionInstalled = false;
            bool exposeCustomAutoInstall = true;
            ManualResetEvent stopSignal = new ManualResetEvent(false);
            Thread server = new Thread(delegate()
            {
                try
                {
                    while (!stopSignal.WaitOne(5))
                    {
                        if (!listener.Pending()) continue;
                        using (TcpClient connection = listener.AcceptTcpClient())
                        using (NetworkStream stream = connection.GetStream())
                        {
                            string request = ReadRequest(stream);
                            Interlocked.Increment(ref requestCount);
                            string firstLine = request.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                            if (firstLine.Contains("module_uninstall") || firstLine.Contains("/unlink ")) throw new Exception("El instalador intentó desinstalar o eliminar.");
                            string[] route = firstLine.Split(' ')[1].Trim('/').Split('/');
                            if (route.Length != 4 || route[0] != "json" || route[1] != "2") throw new Exception("Ruta de módulos inesperada: " + firstLine);
                            string model = Uri.UnescapeDataString(route[2]);
                            string method = Uri.UnescapeDataString(route[3]);
                            string response;
                            if (model == "res.users" && method == "has_group") response = "true";
                            else if (method == "check_access_rights") response = "true";
                            else if (model == "ir.module.module" && method == "fields_get") response = ModuleFieldsResponse();
                            else if (model == "ir.module.module" && method == "search_read")
                            {
                                if (request.Contains("to install") || request.Contains("to upgrade") || request.Contains("to remove")) response = "[]";
                                else if (request.Contains("\"auto_install\",\"=\",true")) response = exposeCustomAutoInstall ? "[{\"id\":103,\"name\":\"custom_sale_bridge\",\"shortdesc\":\"Custom Bridge\",\"summary\":\"Custom\",\"state\":\"uninstalled\",\"license\":\"Other proprietary\",\"author\":\"Third Party\",\"maintainer\":\"Third Party\",\"website\":\"https://vendor.invalid\",\"application\":false,\"auto_install\":true,\"to_buy\":false,\"dependencies_id\":[502]}]" : "[]";
                                else if (request.Contains("\"state\",\"=\",\"installed\""))
                                    response = commissionInstalled ? "[{\"id\":102,\"name\":\"sale\"},{\"id\":101,\"name\":\"sale_commission\"}]" : "[{\"id\":102,\"name\":\"sale\"}]";
                                else response = "[" + ModuleRow(commissionInstalled ? "installed" : "uninstalled") + "," + SaleModuleRow() + "]";
                            }
                            else if (model == "ir.module.module.dependency" && method == "read") response = "[{\"id\":501,\"module_id\":[101,\"Commissions\"],\"name\":\"sale\",\"auto_install_required\":true},{\"id\":502,\"module_id\":[103,\"Custom Bridge\"],\"name\":\"sale_commission\",\"auto_install_required\":true}]";
                            else if (model == "ir.module.module" && method == "button_immediate_install")
                            {
                                if (!request.Contains("\"ids\":[101]")) throw new Exception("La instalación no quedó limitada al módulo raíz pendiente.");
                                commissionInstalled = true;
                                Interlocked.Increment(ref installCount);
                                response = "{\"type\":\"ir.actions.act_url\",\"url\":\"/odoo\"}";
                            }
                            else throw new Exception("Contrato de módulos inesperado: " + model + "." + method);
                            WriteResponse(stream, 200, response);
                        }
                    }
                }
                catch (Exception error) { serverError = error; }
                finally { listener.Stop(); }
            });
            server.IsBackground = true;
            server.Start();

            string target = "OdooAI/SelfTestModules/" + Guid.NewGuid().ToString("N");
            try
            {
                CredentialStore.Write(target, "modules", "clave-ficticia");
                project.SaveProfile(new ConnectionProfile
                {
                    schema_version = 2, client_slug = project.Config.client_slug, environment = "staging", credential_target = target,
                    base_url = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture), login = "modules", uid = 2,
                    read_only = false, credential_fingerprint = JsonUtil.Sha256("clave-ficticia"), server_version = "19.0", server_series = "19.0", api_transport = "json2"
                });
                ModuleService service = new ModuleService(project);
                Dictionary<string, object> search = service.Search("staging", new[] { "comisiones" }, true) as Dictionary<string, object>;
                if (search == null || !JsonUtil.Serialize(search).Contains("sale_commission")) throw new Exception("La búsqueda cerrada no encontró el módulo oficial simulado.");
                bool customAutoBlocked = false;
                try { service.Prepare("staging", "Detectar puente personalizado", new[] { "sale_commission" }, null); }
                catch (ConsultantException error) { customAutoBlocked = error.Code == "custom_auto_install_blocked"; }
                if (!customAutoBlocked || installCount != 0) throw new Exception("Un módulo personalizado de auto-instalación no fue bloqueado.");
                exposeCustomAutoInstall = false;
                Dictionary<string, object> preview = service.Prepare("staging", "Habilitar comisiones nativas para una demostración", new[] { "sale_commission" }, null) as Dictionary<string, object>;
                string planId = Convert.ToString(preview["plan_id"], CultureInfo.InvariantCulture);
                if (!Convert.ToString(preview["required_confirmation"], CultureInfo.InvariantCulture).Equals("APLICAR " + planId, StringComparison.Ordinal)) throw new Exception("El plan no devolvió la confirmación exacta.");
                bool wrongConfirmation = false;
                try { service.Apply(planId, "APLICAR otro"); }
                catch (ConsultantException error) { wrongConfirmation = error.Code == "confirmation_mismatch"; }
                if (!wrongConfirmation || installCount != 0) throw new Exception("Una confirmación incorrecta alcanzó la instalación.");
                Dictionary<string, object> applied = service.Apply(planId, "APLICAR " + planId) as Dictionary<string, object>;
                if (Convert.ToString(applied["status"], CultureInfo.InvariantCulture) != "verified" || installCount != 1) throw new Exception("La instalación simulada no quedó verificada.");
                int beforeRetry = requestCount;
                service.Apply(planId, "APLICAR " + planId);
                if (requestCount != beforeRetry || installCount != 1) throw new Exception("El reintento duplicó la instalación o hizo llamadas de red.");
                Dictionary<string, object> verified = service.Verify(planId) as Dictionary<string, object>;
                if (Convert.ToString(verified["status"], CultureInfo.InvariantCulture) != "verified") throw new Exception("La verificación separada perdió el estado instalado.");
                int beforeProduction = requestCount;
                bool productionBlocked = false;
                try { service.Prepare("production", "No ejecutar", new[] { "sale_commission" }, null); }
                catch (ConsultantException error) { productionBlocked = error.Code == "production_write_blocked"; }
                if (!productionBlocked || requestCount != beforeProduction) throw new Exception("Producción no se bloqueó antes de la red.");
                ModuleInstallationPlan stored = JsonUtil.ReadJsonFile<ModuleInstallationPlan>(project.ModulePlanPath(planId));
                stored.requested_modules[0] = "sale_margin";
                bool integrityBlocked = false;
                try { ModuleService.ValidateForSelfTest(project, stored); }
                catch (ConsultantException error) { integrityBlocked = error.Code == "package_integrity_failed"; }
                if (!integrityBlocked) throw new Exception("La integridad no bloqueó un plan manipulado.");
            }
            finally
            {
                CredentialStore.Delete(target);
                project.DeleteProfile("staging");
                stopSignal.Set();
                listener.Stop();
            }
            if (!server.Join(5000)) { listener.Stop(); throw new Exception("El servidor de módulos simulado no terminó."); }
            if (serverError != null) throw serverError;
            return 9;
        }

        private static string ModuleFieldsResponse()
        {
            Dictionary<string, object> fields = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (string name in new[] { "id", "name", "shortdesc", "summary", "state", "license", "author", "maintainer", "website" }) fields[name] = DemoField("char", false, null, null);
            foreach (string name in new[] { "application", "auto_install", "to_buy" }) fields[name] = DemoField("boolean", false, null, null);
            fields["dependencies_id"] = DemoField("one2many", false, "ir.module.module.dependency", null);
            return JsonUtil.Serialize(fields);
        }

        private static string ModuleRow(string state)
        {
            return "{\"id\":101,\"name\":\"sale_commission\",\"shortdesc\":\"Sales Commissions\",\"summary\":\"Commission plans\",\"state\":\"" + state + "\",\"license\":\"OEEL-1\",\"author\":\"Odoo S.A.\",\"maintainer\":\"Odoo S.A.\",\"website\":\"https://www.odoo.com\",\"application\":false,\"auto_install\":false,\"to_buy\":false,\"dependencies_id\":[501]}";
        }

        private static string SaleModuleRow()
        {
            return "{\"id\":102,\"name\":\"sale\",\"shortdesc\":\"Sales\",\"summary\":\"Sales\",\"state\":\"installed\",\"license\":\"LGPL-3\",\"author\":\"Odoo S.A.\",\"maintainer\":\"Odoo S.A.\",\"website\":\"https://www.odoo.com\",\"application\":true,\"auto_install\":false,\"to_buy\":false,\"dependencies_id\":[]}";
        }

        private static string DemoFieldsResponse()
        {
            Dictionary<string, object> fields = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (string name in new[] { "name", "title", "email", "subject", "contact_name", "partner_name", "email_from", "phone", "preview", "url", "key", "value", "display_name", "mimetype", "type", "res_model", "module", "model" }) fields[name] = DemoField("char", false, null, null);
            foreach (string name in new[] { "description", "body_arch" }) fields[name] = DemoField("html", false, null, null);
            fields["datas"] = DemoField("binary", false, null, null);
            fields["res_id"] = DemoField("integer", false, null, null);
            fields["noupdate"] = DemoField("boolean", false, null, null);
            fields["mailing_domain"] = DemoField("char", false, null, null);
            fields["arch"] = DemoField("text", false, null, null);
            foreach (string name in new[] { "active", "track", "website_indexed", "is_published", "users_login_required", "certification", "constr_mandatory", "is_correct", "ab_testing_enabled", "is_page", "public" }) fields[name] = DemoField("boolean", false, null, null);
            foreach (string name in new[] { "sequence", "ab_testing_pc", "interval_number", "res_id" }) fields[name] = DemoField("integer", false, null, null);
            fields["answer_score"] = DemoField("float", false, null, null);
            fields["type"] = DemoField("selection", false, null, new object[] { new object[] { "lead", "Lead" }, new object[] { "qweb", "QWeb" }, new object[] { "binary", "Binary" } });
            fields["mailing_type"] = DemoField("selection", true, null, new object[] { new object[] { "mail", "Email" }, new object[] { "sms", "SMS" } });
            fields["state"] = DemoField("selection", true, null, new object[] { new object[] { "draft", "Draft" }, new object[] { "running", "Running" }, new object[] { "done", "Done" } });
            fields["access_mode"] = DemoField("selection", true, null, new object[] { new object[] { "public", "Public" }, new object[] { "token", "Token" } });
            fields["scoring_type"] = DemoField("selection", true, null, new object[] { new object[] { "no_scoring", "No scoring" } });
            fields["question_type"] = DemoField("selection", false, null, new object[] { new object[] { "simple_choice", "Choice" }, new object[] { "multiple_choice", "Multiple" }, new object[] { "text_box", "Text" }, new object[] { "char_box", "Char" }, new object[] { "numerical_box", "Number" }, new object[] { "scale", "Scale" }, new object[] { "date", "Date" }, new object[] { "datetime", "Datetime" }, new object[] { "matrix", "Matrix" } });
            fields["activity_type"] = DemoField("selection", true, null, new object[] { new object[] { "email", "Email" } });
            fields["interval_type"] = DemoField("selection", true, null, new object[] { new object[] { "days", "Days" } });
            fields["trigger_type"] = DemoField("selection", true, null, new object[] { new object[] { "begin", "Begin" }, new object[] { "mail_open", "Open" }, new object[] { "mail_not_open", "Not open" }, new object[] { "mail_click", "Click" }, new object[] { "mail_not_click", "Not click" } });
            foreach (KeyValuePair<string, string> relation in new Dictionary<string, string> { { "source_id", "utm.source" }, { "medium_id", "utm.medium" }, { "campaign_id", "utm.campaign" }, { "website_id", "website" }, { "survey_id", "survey.survey" }, { "question_id", "survey.question" }, { "matrix_question_id", "survey.question" }, { "model_id", "ir.model" }, { "mailing_model_id", "ir.model" }, { "parent_id", "marketing.activity" }, { "mass_mailing_id", "mailing.mailing" } }) fields[relation.Key] = DemoField("many2one", false, relation.Value, null);
            foreach (KeyValuePair<string, string> relation in new Dictionary<string, string> { { "list_ids", "mailing.list" }, { "contact_list_ids", "mailing.list" } }) fields[relation.Key] = DemoField("many2many", false, relation.Value, null);
            fields["domain"] = DemoField("char", false, null, null);
            return JsonUtil.Serialize(fields);
        }

        private static Dictionary<string, object> DemoField(string type, bool required, string relation, object selection)
        {
            Dictionary<string, object> value = JsonUtil.Dictionary("type", type, "required", required, "readonly", false, "relation", relation, "store", true, "string", type);
            if (selection != null) value["selection"] = selection;
            return value;
        }

        private static Dictionary<string, object> RequestBody(string request)
        {
            int separator = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            return separator < 0 ? new Dictionary<string, object>() : JsonUtil.DeserializeObject(request.Substring(separator + 4));
        }

        private static Dictionary<string, object> FirstDictionary(object value)
        {
            IEnumerable sequence = value as IEnumerable;
            if (sequence != null && !(value is string)) foreach (object item in sequence) return item as Dictionary<string, object> ?? new Dictionary<string, object>();
            return new Dictionary<string, object>();
        }

        private static long FirstLong(object value)
        {
            IEnumerable sequence = value as IEnumerable;
            if (sequence != null && !(value is string)) foreach (object item in sequence) return Convert.ToInt64(item, CultureInfo.InvariantCulture);
            return 0;
        }

        private static IEnumerable<string> StringValues(object value)
        {
            List<string> result = new List<string>();
            IEnumerable sequence = value as IEnumerable;
            if (sequence != null && !(value is string)) foreach (object item in sequence) result.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
            return result;
        }

        private static string DomainString(IDictionary<string, object> body, string field)
        {
            object raw;
            IEnumerable domain = body != null && body.TryGetValue("domain", out raw) ? raw as IEnumerable : null;
            if (domain == null) return string.Empty;
            foreach (object item in domain)
            {
                IEnumerable tuple = item as IEnumerable;
                if (tuple == null || item is string) continue;
                List<object> values = new List<object>();
                foreach (object value in tuple) values.Add(value);
                if (values.Count >= 3 && Convert.ToString(values[0], CultureInfo.InvariantCulture) == field && Convert.ToString(values[1], CultureInfo.InvariantCulture) == "=") return Convert.ToString(values[2], CultureInfo.InvariantCulture);
            }
            return string.Empty;
        }

        private static bool MatchesDomain(IDictionary<string, object> record, IDictionary<string, object> body)
        {
            object raw;
            IEnumerable domain = body != null && body.TryGetValue("domain", out raw) ? raw as IEnumerable : null;
            if (domain == null) return true;
            foreach (object item in domain)
            {
                IEnumerable tuple = item as IEnumerable;
                if (tuple == null || item is string) continue;
                List<object> values = new List<object>();
                foreach (object value in tuple) values.Add(value);
                if (values.Count < 3 || Convert.ToString(values[1], CultureInfo.InvariantCulture) != "=") continue;
                string field = Convert.ToString(values[0], CultureInfo.InvariantCulture);
                object actual;
                if (!record.TryGetValue(field, out actual) || JsonUtil.CanonicalHash(actual) != JsonUtil.CanonicalHash(values[2])) return false;
            }
            return true;
        }

        private static string ReadRequest(NetworkStream stream)
        {
            MemoryStream buffer = new MemoryStream();
            byte[] one = new byte[1];
            int headerEnd = -1;
            while (buffer.Length < 1024 * 1024 && stream.Read(one, 0, 1) == 1)
            {
                buffer.WriteByte(one[0]);
                byte[] data = buffer.GetBuffer();
                int length = (int)buffer.Length;
                if (length >= 4 && data[length - 4] == 13 && data[length - 3] == 10 && data[length - 2] == 13 && data[length - 1] == 10)
                {
                    headerEnd = length;
                    break;
                }
            }
            if (headerEnd < 0) throw new Exception("Solicitud HTTP simulada incompleta.");
            string headers = Encoding.ASCII.GetString(buffer.ToArray());
            int contentLength = 0;
            foreach (string line in headers.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line.Substring(line.IndexOf(':') + 1).Trim(), out contentLength);
            }
            byte[] body = new byte[contentLength];
            int offset = 0;
            while (offset < body.Length)
            {
                int read = stream.Read(body, offset, body.Length - offset);
                if (read <= 0) break;
                offset += read;
            }
            string result = headers + Encoding.UTF8.GetString(body, 0, offset);
            Array.Clear(body, 0, body.Length);
            return result;
        }

        public static int VerifyPromotionLifecycle(ProjectStore project)
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Exception serverError = null;
            int requestCount = 0;
            int writeCount = 0;
            bool productionHasField = false;
            ManualResetEvent stopSignal = new ManualResetEvent(false);

            Thread server = new Thread(delegate()
            {
                try
                {
                    while (!stopSignal.WaitOne(5))
                    {
                        if (!listener.Pending()) continue;
                        using (TcpClient connection = listener.AcceptTcpClient())
                        using (NetworkStream stream = connection.GetStream())
                        {
                            string request = ReadRequest(stream);
                            Interlocked.Increment(ref requestCount);
                            string firstLine = request.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                            string[] lineParts = firstLine.Split(' ');
                            string[] route = lineParts[1].Trim('/').Split('/');
                            if (route.Length != 4 || route[0] != "json" || route[1] != "2") throw new Exception("Ruta de promoción inesperada: " + firstLine);
                            string model = Uri.UnescapeDataString(route[2]);
                            string method = Uri.UnescapeDataString(route[3]);

                            if (method == "create" || method == "write" || method == "unlink")
                            {
                                Interlocked.Increment(ref writeCount);
                            }

                            string response = "[]";
                            if (method == "check_access_rights")
                            {
                                response = "true";
                            }
                            else if (method == "context_get")
                            {
                                response = "{\"uid\":2}";
                            }
                            else if (method == "search_read")
                            {
                                if (model == "ir.model")
                                {
                                    response = "[{\"id\":10,\"model\":\"sale.order\"}]";
                                }
                                else if (model == "ir.model.fields")
                                {
                                    bool isStaging = request.Contains("clave-ficticia-stg");
                                    if (isStaging || productionHasField)
                                    {
                                        response = "[{\"id\":101,\"model\":\"sale.order\",\"name\":\"x_sample_replica\",\"field_description\":\"Muestra\",\"ttype\":\"char\",\"required\":false,\"readonly\":false,\"store\":true,\"copied\":true,\"state\":\"manual\"}]";
                                    }
                                    else
                                    {
                                        response = "[]";
                                    }
                                }
                                else if (model == "ir.ui.view" || model == "ir.actions.server")
                                {
                                    response = "[]";
                                }
                            }
                            WriteResponse(stream, 200, response);
                        }
                    }
                }
                catch (Exception error) { serverError = error; }
                finally { listener.Stop(); }
            });
            server.IsBackground = true;
            server.Start();

            string stagingTarget = "OdooAI/SelfTestPromoStaging/" + Guid.NewGuid().ToString("N");
            string prodTarget = "OdooAI/SelfTestPromoProd/" + Guid.NewGuid().ToString("N");
            int passed = 0;
            try
            {
                CredentialStore.Write(stagingTarget, "staging", "clave-ficticia-stg");
                CredentialStore.Write(prodTarget, "production", "clave-ficticia-prd");

                project.SaveProfile(new ConnectionProfile
                {
                    schema_version = 2, client_slug = project.Config.client_slug, environment = "staging", credential_target = stagingTarget,
                    base_url = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture), login = "staging", uid = 2,
                    read_only = false, credential_fingerprint = JsonUtil.Sha256("clave-ficticia-stg"), server_version = "19.0", server_series = "19.0", api_transport = "json2"
                });

                project.SaveProfile(new ConnectionProfile
                {
                    schema_version = 2, client_slug = project.Config.client_slug, environment = "production", credential_target = prodTarget,
                    base_url = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture), login = "production", uid = 2,
                    read_only = true, credential_fingerprint = JsonUtil.Sha256("clave-ficticia-prd"), server_version = "19.0", server_series = "19.0", api_transport = "json2"
                });

                string replicaRoot = Path.Combine(project.Root, "replicas", "sample_replica");
                Directory.CreateDirectory(replicaRoot);
                JsonUtil.WriteJsonFile(Path.Combine(replicaRoot, "manifest.json"), JsonUtil.Dictionary(
                    "schema_version", 1,
                    "name", "sample",
                    "source", JsonUtil.Dictionary("environment", "staging", "contains_business_records", false),
                    "fields", new object[] { JsonUtil.Dictionary("model", "sale.order", "name", "x_sample_replica", "label", "Muestra", "type", "char", "required", false, "readonly", false, "stored", true, "copied", true) },
                    "views", new object[0]));

                PromotionService promoService = new PromotionService(project);

                // 1. Analizar impacto
                Dictionary<string, object> impact = (Dictionary<string, object>)promoService.AnalyzeImpact("sample_replica");
                if (Convert.ToString(impact["status"], CultureInfo.InvariantCulture) != "ready_for_promotion")
                    throw new Exception("El análisis de impacto no reportó ready_for_promotion.");
                if (!impact.ContainsKey("ui_views_detected_in_staging") || !impact.ContainsKey("ui_visibility_guarantee"))
                    throw new Exception("AnalyzeImpact no contiene los campos de garantía de visibilidad UI.");
                passed++;

                // 2. Preparar runbook
                Dictionary<string, object> runbookResult = (Dictionary<string, object>)promoService.PrepareRunbook("sample_replica");
                if (Convert.ToString(runbookResult["status"], CultureInfo.InvariantCulture) != "prepared")
                    throw new Exception("El runbook de promoción no se preparó.");
                passed++;

                string runbookPath = project.PromotionRunbookPath("sample_replica");
                if (!File.Exists(runbookPath)) throw new Exception("Falta el archivo runbook.md.");
                string runbookContent = File.ReadAllText(runbookPath, Encoding.UTF8);
                if (!runbookContent.Contains("Runbook de Promoción a Producción: sample_replica") || !runbookContent.Contains("x_sample_replica"))
                    throw new Exception("El runbook no contiene el contenido esperado.");
                passed++;

                // 3. Verificar producción cuando falta el campo
                Dictionary<string, object> verifyPending = (Dictionary<string, object>)promoService.VerifyProduction("sample_replica");
                if (Convert.ToBoolean(verifyPending["certified"], CultureInfo.InvariantCulture))
                    throw new Exception("Producción no debió certificarse sin los campos aplicados.");
                passed++;

                // 4. Simular aplicación del cambio en producción
                productionHasField = true;

                // 5. Verificar producción completada
                Dictionary<string, object> verifyCertified = (Dictionary<string, object>)promoService.VerifyProduction("sample_replica");
                if (!Convert.ToBoolean(verifyCertified["certified"], CultureInfo.InvariantCulture))
                    throw new Exception("Producción no se certificó al 100%.");
                passed++;

                string certPath = project.PromotionCertificatePath("sample_replica");
                if (!File.Exists(certPath)) throw new Exception("Falta certificado_trazabilidad.json.");
                Dictionary<string, object> cert = JsonUtil.ReadJsonFile<Dictionary<string, object>>(certPath);
                if (Convert.ToString(cert["status"], CultureInfo.InvariantCulture) != "certified_100_percent")
                    throw new Exception("El certificado no indica certified_100_percent.");
                if (string.IsNullOrWhiteSpace(Convert.ToString(cert["signature_hash"], CultureInfo.InvariantCulture)))
                    throw new Exception("Falta firma criptográfica en el certificado.");
                passed++;

                // 6. Garantizar que producción fue estrictamente de solo lectura
                if (writeCount != 0) throw new Exception("Se detectaron escrituras hacia producción.");
                passed++;

                // 7. Preparación exige referencia de respaldo válida
                bool backupReqBlocked = false;
                try { promoService.PreparePackage("sample_replica", "bkp", null); }
                catch (ConsultantException ex) { backupReqBlocked = ex.Code == "backup_reference_required"; }
                if (!backupReqBlocked) throw new Exception("No se bloqueó la preparación sin referencia de respaldo válida.");
                passed++;

                // 8. Preparación bloquea tajantemente datos demo / registros de ejemplo
                string demoReplicaRoot = Path.Combine(project.Root, "replicas", "demo_tainted_replica");
                Directory.CreateDirectory(demoReplicaRoot);
                JsonUtil.WriteJsonFile(Path.Combine(demoReplicaRoot, "manifest.json"), JsonUtil.Dictionary(
                    "schema_version", 2, "phase", 2, "name", "demo_tainted",
                    "source", JsonUtil.Dictionary("environment", "staging", "contains_business_records", false),
                    "models", new object[] { JsonUtil.Dictionary("key", "demo_mod", "model", "x_demo_mcp", "name", "Demo", "mode", "create") },
                    "fields", new object[] { JsonUtil.Dictionary("key", "demo_fld", "model", "odoo_ai_demo", "name", "x_fld", "label", "Demo Field", "type", "char", "mode", "create") },
                    "views", new object[0], "window_actions", new object[0], "menus", new object[0],
                    "excluded_artifacts", new object[] { "all_business_records" }));
                bool demoDataBlocked = false;
                try { promoService.PreparePackage("demo_tainted_replica", "BKP-PROD-20260922", null); }
                catch (ConsultantException ex) { demoDataBlocked = ex.Code == "demo_data_prohibited"; }
                if (!demoDataBlocked) throw new Exception("No se bloqueó la preparación con datos demo.");
                passed++;

                string promoFakeId = "prom-" + Guid.NewGuid().ToString("N");
                ChangeSet fakePromotion = new ChangeSet
                {
                    schema_version = 3, id = promoFakeId, client_slug = project.Config.client_slug,
                    environment = "production", category = "production_promotion", status = "prepared",
                    backup_reference = "BKP-TEST-12345", model = "sale.order", target_models = new List<string> { "sale.order" },
                    replica_slug = "sample_replica", created_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    expires_at = DateTimeOffset.UtcNow.AddHours(2).ToString("o", CultureInfo.InvariantCulture),
                    policy_version = ReplicaPhase2Service.PolicyVersion,
                    operations = new List<ChangeOperation>()
                };
                JsonUtil.WriteJsonFile(project.ChangeSetPath(fakePromotion.id), fakePromotion);
                bool promoMismatchBlocked = false;
                try { promoService.ApplyPackage(fakePromotion.id, "APLICAR " + fakePromotion.id, null); }
                catch (ConsultantException ex) { promoMismatchBlocked = ex.Code == "confirmation_mismatch"; }
                if (!promoMismatchBlocked) throw new Exception("No se exigió la confirmación reforzada PROMOVER-PRODUCCION <id> RESPALDO-CONFIRMADO.");
                passed++;

                // 10. Activación de efectos externos exige ACTIVAR-EFECTOS-PRODUCCION <id>
                bool extMismatchBlocked = false;
                try { promoService.ActivateExternalEffects(fakePromotion.id, "ACTIVAR-EFECTOS " + fakePromotion.id); }
                catch (ConsultantException ex) { extMismatchBlocked = ex.Code == "confirmation_mismatch"; }
                if (!extMismatchBlocked) throw new Exception("No se exigió la confirmación ACTIVAR-EFECTOS-PRODUCCION <id>.");
                passed++;
            }
            finally
            {
                stopSignal.Set();
                server.Join(1000);
                CredentialStore.Delete(stagingTarget);
                CredentialStore.Delete(prodTarget);
            }
            if (serverError != null) throw new Exception("Error en el servidor simulado de promoción: " + serverError.Message, serverError);
            return passed;
        }

        public static int VerifySpreadsheetLifecycle(ProjectStore project)
        {
            int passed = 0;

            // 1. Validar deteccion de errores estructurales (Regla 1: missing action, Regla 2: 1D domain, Regla 5: object cell)
            string invalidDefinitionJson = "{\"version\":1,\"sheets\":[{\"name\":\"Hoja 1\",\"rowNumber\":10,\"colNumber\":5,\"cells\":{\"A1\":\"[Ver Ventas](odoo://view/{\\\"name\\\":\\\"Ventas\\\",\\\"viewType\\\":\\\"list\\\",\\\"modelName\\\":\\\"sale.order\\\",\\\"domain\\\":[[\\\"state\\\",\\\"=\\\",\\\"sale\\\"]]})\",\"A2\":{\"content\":\"100\"}},\"figures\":[{\"id\":\"fig1\",\"tag\":\"chart\",\"data\":{\"searchParams\":{\"model\":\"sale.order\",\"domain\":[\"state\",\"=\",\"sale\"]}}}]}]}";

            Dictionary<string, object> defObj = JsonUtil.Deserialize<Dictionary<string, object>>(invalidDefinitionJson);
            Dictionary<string, object> fixedDef;
            Dictionary<string, object> report = SpreadsheetService.ValidateInternal(defObj, false, out fixedDef);

            if (Convert.ToBoolean(report["valid"], CultureInfo.InvariantCulture))
                throw new Exception("La validacion de tablero debio fallar ante link sin action y celda objeto.");
            IList fatalErrors = (IList)report["fatal_errors"];
            if (fatalErrors == null || fatalErrors.Count < 3)
                throw new Exception("Se esperaban al menos 3 errores estructurales (action ausente, celda objeto, dominio 1D).");
            passed++;

            // 2. Probar auto_fix = true: reparacion automatica de action, dominio y celdas
            Dictionary<string, object> repairReport = SpreadsheetService.ValidateInternal(defObj, true, out fixedDef);
            if (!Convert.ToBoolean(repairReport["valid"], CultureInfo.InvariantCulture) || !Convert.ToBoolean(repairReport["repaired"], CultureInfo.InvariantCulture))
                throw new Exception("auto_fix debio corregir y marcar repaired = true.");
            if (fixedDef == null)
                throw new Exception("auto_fix no genero fixedSpreadsheet.");
            passed++;

            // 3. Verificar que PreparePackage en 'production' esta estrictamente bloqueado
            SpreadsheetService service = new SpreadsheetService(project);
            bool prodBlocked = false;
            try
            {
                service.PreparePackage("production", "spreadsheet.dashboard", "create", null, "Tablero Produccion", null, fixedDef, false);
            }
            catch (ConsultantException ex)
            {
                prodBlocked = ex.Code == "production_write_blocked";
            }
            if (!prodBlocked)
                throw new Exception("PreparePackage debio bloquear escrituras en produccion.");
            passed++;

            // 4. Iniciar servidor simulado para SearchRead y Create en staging
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Exception serverError = null;
            int requestCount = 0;
            int createCount = 0;
            ManualResetEvent stopSignal = new ManualResetEvent(false);

            Thread server = new Thread(delegate()
            {
                try
                {
                    while (!stopSignal.WaitOne(5))
                    {
                        try
                        {
                            if (!listener.Pending()) continue;
                        }
                        catch (InvalidOperationException) { break; }
                        catch (SocketException) { break; }
                        using (TcpClient connection = listener.AcceptTcpClient())
                        using (NetworkStream stream = connection.GetStream())
                        {
                            string request = ReadRequest(stream);
                            Interlocked.Increment(ref requestCount);
                            string firstLine = request.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                            string[] lineParts = firstLine.Split(' ');
                            string[] route = lineParts[1].Trim('/').Split('/');
                            if (route.Length != 4 || route[0] != "json" || route[1] != "2") throw new Exception("Ruta de tablero inesperada: " + firstLine);
                            string model = Uri.UnescapeDataString(route[2]);
                            string method = Uri.UnescapeDataString(route[3]);

                            string response = "[]";
                            if (method == "check_access_rights")
                            {
                                response = "true";
                            }
                            else if (method == "context_get")
                            {
                                response = "{\"uid\":2}";
                            }
                            else if (model == "spreadsheet.dashboard" && method == "create")
                            {
                                Interlocked.Increment(ref createCount);
                                response = "[88]";
                            }
                            else if (model == "spreadsheet.dashboard" && method == "search_read")
                            {
                                string serializedData = JsonUtil.Serialize(fixedDef);
                                Dictionary<string, object> mockRow = JsonUtil.Dictionary(
                                    "id", 88,
                                    "name", "Tablero Staging",
                                    "spreadsheet_data", serializedData,
                                    "write_date", "2026-09-10 12:00:00"
                                );
                                response = JsonUtil.Serialize(new object[] { mockRow });
                            }
                            WriteResponse(stream, 200, response);
                        }
                    }
                }
                catch (Exception error)
                {
                    if (!stopSignal.WaitOne(0)) serverError = error;
                }
                finally { listener.Stop(); }
            });
            server.IsBackground = true;
            server.Start();

            string stagingTarget = "OdooAI/SelfTestSpreadsheetStaging/" + Guid.NewGuid().ToString("N");
            try
            {
                CredentialStore.Write(stagingTarget, "staging", "clave-ficticia-tableros");
                project.SaveProfile(new ConnectionProfile
                {
                    schema_version = 2,
                    client_slug = project.Config.client_slug,
                    environment = "staging",
                    credential_target = stagingTarget,
                    base_url = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture),
                    login = "staging",
                    uid = 2,
                    read_only = false,
                    credential_fingerprint = JsonUtil.Sha256("clave-ficticia-tableros"),
                    server_version = "19.0",
                    server_series = "19.0",
                    api_transport = "json2"
                });

                // 5. Preparar paquete en staging
                Dictionary<string, object> prepResult = (Dictionary<string, object>)service.PreparePackage("staging", "spreadsheet.dashboard", "create", null, "Tablero Staging", null, fixedDef, false);
                string pkgId = Convert.ToString(prepResult["paquete_id"], CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(pkgId) || !File.Exists(project.ChangeSetPath(pkgId)))
                    throw new Exception("El paquete de tablero no fue guardado en disco.");
                passed++;

                // 6. Rechazar confirmacion incorrecta
                bool mismatchBlocked = false;
                try
                {
                    service.ApplyPackage(pkgId, "CONFIRMAR " + pkgId);
                }
                catch (ConsultantException ex)
                {
                    mismatchBlocked = ex.Code == "confirmation_mismatch";
                }
                if (!mismatchBlocked)
                    throw new Exception("ApplyPackage no rechazo frase de confirmacion incorrecta.");
                passed++;

                // 7. Aplicar paquete con frase exacta APLICAR-TABLERO <id>
                Dictionary<string, object> applyResult = (Dictionary<string, object>)service.ApplyPackage(pkgId, "APLICAR-TABLERO " + pkgId);
                if (Convert.ToString(applyResult["status"], CultureInfo.InvariantCulture) != "applied_and_verified")
                    throw new Exception("El tablero no quedo en status applied_and_verified.");
                if (createCount != 1)
                    throw new Exception("La creacion del tablero en Odoo simulado no se ejecuto una unica vez.");
                passed++;

                // 8. Inspeccionar el tablero recien creado
                Dictionary<string, object> inspectResult = (Dictionary<string, object>)service.Inspect("staging", "spreadsheet.dashboard", 88, null);
                if (!Convert.ToBoolean(inspectResult["ok"], CultureInfo.InvariantCulture) || Convert.ToString(inspectResult["nombre"], CultureInfo.InvariantCulture) != "Tablero Staging")
                    throw new Exception("La inspeccion del tablero en staging no devolvio los metadatos esperados.");
                IList inspectSummaries = (IList)inspectResult["resumen_hojas"];
                if (inspectSummaries.Count != 1)
                    throw new Exception("La inspeccion del tablero no reporto la hoja creada.");
                passed++;
            }
            finally
            {
                stopSignal.Set();
                server.Join(1000);
                CredentialStore.Delete(stagingTarget);
            }

            if (serverError != null) throw new Exception("Error en el servidor simulado de tableros: " + serverError.Message, serverError);
            return passed;
        }

        public static int VerifyDiscoverDatabase()
        {
            int passed = 0;

            // 1. Validaciones previas de formato de URL
            bool emptyBlocked = false;
            try { OdooClient.DiscoverDatabase("", 5000); }
            catch (ConsultantException ex) { emptyBlocked = ex.Code == "invalid_odoo_url"; }
            if (!emptyBlocked) throw new Exception("DiscoverDatabase debio bloquear URL vacia.");
            passed++;

            bool insecureBlocked = false;
            try { OdooClient.DiscoverDatabase("http://empresa.odoo.com", 5000); }
            catch (ConsultantException ex) { insecureBlocked = ex.Code == "insecure_odoo_url"; }
            if (!insecureBlocked) throw new Exception("DiscoverDatabase debio bloquear HTTP no local.");
            passed++;

            // 2. Simulacion con servidor local (Odoo.sh con lista de BD y version_info)
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Exception serverError = null;
            ManualResetEvent stopSignal = new ManualResetEvent(false);

            Thread server = new Thread(delegate()
            {
                try
                {
                    while (!stopSignal.WaitOne(5))
                    {
                        try
                        {
                            if (!listener.Pending()) continue;
                        }
                        catch (InvalidOperationException) { break; }
                        catch (SocketException) { break; }

                        using (TcpClient connection = listener.AcceptTcpClient())
                        using (NetworkStream stream = connection.GetStream())
                        {
                            string request = ReadRequest(stream);
                            string firstLine = request.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                            if (firstLine.Contains("/web/database/list"))
                            {
                                WriteResponseWithHeader(stream, 200, "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":[\"mock-sh-master-123456\"]}", "Server: Odoo.sh\r\n");
                            }
                            else if (firstLine.Contains("/web/webclient/version_info"))
                            {
                                WriteResponseWithHeader(stream, 200, "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"server_version\":\"19.0+e\",\"server_serie\":\"19.0\",\"protocol_version\":1}}", "Server: Odoo.sh\r\n");
                            }
                            else
                            {
                                WriteResponse(stream, 404, "{}");
                            }
                        }
                    }
                }
                catch (Exception error)
                {
                    if (!stopSignal.WaitOne(0)) serverError = error;
                }
                finally { listener.Stop(); }
            });
            server.IsBackground = true;
            server.Start();

            try
            {
                Dictionary<string, object> res = OdooClient.DiscoverDatabase("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture), 5000);
                if (Convert.ToString(res["base_datos"], CultureInfo.InvariantCulture) != "mock-sh-master-123456")
                    throw new Exception("DiscoverDatabase no devolvio la base de datos simulada.");
                if (Convert.ToString(res["tipo_alojamiento"], CultureInfo.InvariantCulture) != "odoo_sh")
                    throw new Exception("DiscoverDatabase no detecto hosting odoo_sh.");
                if (Convert.ToString(res["nivel_confianza"], CultureInfo.InvariantCulture) != "confirmed")
                    throw new Exception("DiscoverDatabase debio reportar confianza confirmed.");
                if (Convert.ToString(res["serie_odoo"], CultureInfo.InvariantCulture) != "19.0")
                    throw new Exception("DiscoverDatabase no detecto la serie 19.0.");
                if (Convert.ToString(res["transporte_api_recomendado"], CultureInfo.InvariantCulture) != "json2")
                    throw new Exception("DiscoverDatabase debio recomendar json2.");
                passed++;
            }
            finally
            {
                stopSignal.Set();
                try { listener.Stop(); } catch { }
                server.Join(1000);
            }

            if (serverError != null) throw new Exception("Error en el servidor simulado de descubrimiento: " + serverError.Message, serverError);
            return passed;
        }

        public static int VerifyConnectionTestLifecycle(ProjectStore project)
        {
            int passed = 0;
            project.DeleteProfile("production");
            project.DeleteProfile("staging");
            StudioService studio = new StudioService(project);

            // 1. Probar entorno no configurado
            Dictionary<string, object> unconfigured = (Dictionary<string, object>)studio.TestConnection("production");
            if (Convert.ToBoolean(unconfigured["configured"], CultureInfo.InvariantCulture) != false ||
                Convert.ToBoolean(unconfigured["ok"], CultureInfo.InvariantCulture) != false)
                throw new Exception("TestConnection debio reportar configured: false para produccion.");
            passed++;

            // 2. Iniciar servidor simulado para staging
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Exception serverError = null;
            ManualResetEvent stopSignal = new ManualResetEvent(false);

            Thread server = new Thread(delegate()
            {
                try
                {
                    while (!stopSignal.WaitOne(5))
                    {
                        try
                        {
                            if (!listener.Pending()) continue;
                        }
                        catch (InvalidOperationException) { break; }
                        catch (SocketException) { break; }

                        using (TcpClient connection = listener.AcceptTcpClient())
                        using (NetworkStream stream = connection.GetStream())
                        {
                            string request = ReadRequest(stream);
                            string firstLine = request.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                            string[] route = firstLine.Split(' ')[1].Trim('/').Split('/');
                            string method = route.Length == 4 ? route[3] : string.Empty;

                            string response = "{}";
                            if (method == "context_get")
                            {
                                response = "{\"uid\":10}";
                            }
                            else if (method == "read")
                            {
                                response = "[{\"id\":10,\"login\":\"consultor@test.invalid\",\"name\":\"Consultor Activo\"}]";
                            }
                            else if (method == "has_group")
                            {
                                response = "true";
                            }

                            WriteResponse(stream, 200, response);
                        }
                    }
                }
                catch (Exception error)
                {
                    if (!stopSignal.WaitOne(0)) serverError = error;
                }
                finally { listener.Stop(); }
            });
            server.IsBackground = true;
            server.Start();

            string stagingTarget = "OdooAI/SelfTestConnStaging/" + Guid.NewGuid().ToString("N");
            try
            {
                CredentialStore.Write(stagingTarget, "staging", "clave-ficticia-conn");
                project.SaveProfile(new ConnectionProfile
                {
                    schema_version = 2,
                    client_slug = project.Config.client_slug,
                    environment = "staging",
                    credential_target = stagingTarget,
                    base_url = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture),
                    login = "consultor@test.invalid",
                    uid = 10,
                    read_only = false,
                    credential_fingerprint = JsonUtil.Sha256("clave-ficticia-conn"),
                    server_version = "19.0",
                    server_series = "19.0",
                    api_transport = "json2"
                });

                Dictionary<string, object> successResult = (Dictionary<string, object>)studio.TestConnection("staging");
                if (Convert.ToBoolean(successResult["ok"], CultureInfo.InvariantCulture) != true)
                    throw new Exception("TestConnection no reporto ok: true para staging.");
                if (Convert.ToString(successResult["user_name"], CultureInfo.InvariantCulture) != "Consultor Activo")
                    throw new Exception("TestConnection no devolvio el nombre del usuario.");
                if (Convert.ToBoolean(successResult["is_admin"], CultureInfo.InvariantCulture) != true)
                    throw new Exception("TestConnection no reporto is_admin: true.");
                passed++;

                // 3. Probar sin argumento de entorno (evalua ambos)
                Dictionary<string, object> allResults = (Dictionary<string, object>)studio.TestConnection(null);
                IList list = (IList)allResults["results"];
                if (list.Count != 2)
                    throw new Exception("TestConnection(null) debio evaluar exactamente 2 entornos.");
                passed++;
            }
            finally
            {
                stopSignal.Set();
                try { listener.Stop(); } catch { }
                server.Join(1000);
                CredentialStore.Delete(stagingTarget);
                project.DeleteProfile("staging");
                project.DeleteProfile("production");
            }

            if (serverError != null) throw new Exception("Error en servidor simulado de prueba de conexión: " + serverError.Message, serverError);
            return passed;
        }

        private static void WriteResponse(NetworkStream stream, int status, string json)
        {
            WriteResponseWithHeader(stream, status, json, null);
        }

        private static void WriteResponseWithHeader(NetworkStream stream, int status, string json, string extraHeaders)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            string head = "HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + (status == 200 ? " OK" : " Forbidden") + "\r\nContent-Type: application/json\r\n" + (extraHeaders ?? string.Empty) + "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n";
            byte[] headers = Encoding.ASCII.GetBytes(head);
            stream.Write(headers, 0, headers.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
            Array.Clear(body, 0, body.Length);
        }
    }
}
