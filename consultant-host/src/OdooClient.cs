using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace OdooConsultor
{
    public sealed partial class OdooClient : IDisposable
    {
        private static readonly Regex ModelPattern = new Regex("^[a-z0-9_.]+$", RegexOptions.Compiled);
        private static readonly Regex MethodPattern = new Regex("^[A-Za-z0-9_]+$", RegexOptions.Compiled);
        private readonly ConnectionProfile profile;
        private string apiKey;
        private readonly int timeoutMs;
        private long authenticatedUid;

        static OdooClient()
        {
            ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | SecurityProtocolType.Tls12;
        }

        public OdooClient(ConnectionProfile connectionProfile, string secret, int requestTimeoutMs)
        {
            if (connectionProfile == null) throw new ArgumentNullException("connectionProfile");
            if (string.IsNullOrWhiteSpace(secret)) throw new ConsultantException("credential_empty", "La credencial recuperada está vacía.");
            profile = connectionProfile;
            profile.base_url = NormalizeBaseUrl(profile.base_url);
            if (string.IsNullOrWhiteSpace(profile.api_transport)) profile.api_transport = "json2";
            if (profile.api_transport != "json2" && profile.api_transport != "legacy_rpc") throw new ConsultantException("unsupported_transport", "El transporte configurado no está soportado.");
            if (profile.api_transport == "legacy_rpc" && string.IsNullOrWhiteSpace(profile.database)) throw new ConsultantException("database_required", "Las versiones con protocolo RPC heredado (14 a 18) requieren el nombre técnico de la base.");
            apiKey = secret.Trim();
            timeoutMs = requestTimeoutMs;
        }

        public Dictionary<string, object> ContextGet()
        {
            return AsDictionary(Call("res.users", "context_get", new Dictionary<string, object>()), "Odoo no devolvió un contexto válido.");
        }

        public IList<Dictionary<string, object>> Read(string model, IEnumerable<long> ids, IEnumerable<string> fields)
        {
            List<object> idList = new List<object>();
            foreach (long id in ids) idList.Add(id);
            return AsRecordList(Call(model, "read", JsonUtil.Dictionary("ids", idList.ToArray(), "fields", ToObjectArray(fields), "load", null)));
        }

        public IList<Dictionary<string, object>> SearchRead(string model, object[] domain, IEnumerable<string> fields, int limit, string order)
        {
            return SearchReadPage(model, domain, fields, limit, 0, order);
        }

        public IList<Dictionary<string, object>> SearchReadPage(string model, object[] domain, IEnumerable<string> fields, int limit, int offset, string order)
        {
            if (limit < 1 || limit > 5000) throw new ConsultantException("invalid_read_limit", "El límite de lectura técnica no es válido.");
            if (offset < 0 || offset > 1000000) throw new ConsultantException("invalid_read_offset", "El desplazamiento de lectura técnica no es válido.");
            Dictionary<string, object> body = JsonUtil.Dictionary("domain", domain ?? new object[0], "fields", ToObjectArray(fields), "limit", limit, "offset", offset);
            if (!string.IsNullOrWhiteSpace(order)) body["order"] = order;
            return AsRecordList(Call(model, "search_read", body));
        }

        public Dictionary<string, object> FieldsGet(string model, IEnumerable<string> attributes)
        {
            return AsDictionary(Call(model, "fields_get", JsonUtil.Dictionary("attributes", ToObjectArray(attributes))), "Odoo no devolvió los campos del modelo.");
        }

        public bool CheckAccessRights(string model, string operation)
        {
            if (operation != "read" && operation != "create" && operation != "write" && operation != "unlink") throw new ConsultantException("invalid_access_operation", "La operación de permisos no está permitida.");
            object response = Call(model, "check_access_rights", JsonUtil.Dictionary("operation", operation, "raise_exception", false));
            return response is bool ? (bool)response : string.Equals(Convert.ToString(response, CultureInfo.InvariantCulture), "true", StringComparison.OrdinalIgnoreCase);
        }

        public Dictionary<string, object> ProbeDocumentation()
        {
            if (profile.api_transport == "legacy_rpc") return JsonUtil.Dictionary("available", false, "status", 0, "reason", "Las versiones con protocolo RPC heredado no exponen /doc; se usan fields_get y metadatos técnicos.");
            string url = profile.base_url.TrimEnd('/') + "/doc";
            HttpWebRequest request = CreateRequest(url, "GET");
            request.Accept = "text/html,application/json";
            request.AllowAutoRedirect = false;
            request.Headers[HttpRequestHeader.Authorization] = "bearer " + apiKey;
            if (!string.IsNullOrWhiteSpace(profile.database)) request.Headers["X-Odoo-Database"] = profile.database;
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    return JsonUtil.Dictionary("available", (int)response.StatusCode >= 200 && (int)response.StatusCode < 400, "status", (int)response.StatusCode, "content_type", response.ContentType, "content_length", response.ContentLength);
            }
            catch (WebException error)
            {
                using (HttpWebResponse response = error.Response as HttpWebResponse)
                {
                    int status = response == null ? 0 : (int)response.StatusCode;
                    return JsonUtil.Dictionary("available", false, "status", status, "content_type", null, "content_length", -1);
                }
            }
        }

        public Dictionary<string, object> ProbeDocumentedMethods(IDictionary<string, string> contracts)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (string key in contracts.Keys) result[key] = false;
            if (profile.api_transport != "json2") return JsonUtil.Dictionary("available", false, "methods", result, "document_hash", null, "reason", "transport_without_doc");
            string url = profile.base_url.TrimEnd('/') + "/doc";
            HttpWebRequest request = CreateRequest(url, "GET");
            request.Accept = "text/html,application/json";
            request.AllowAutoRedirect = true;
            request.Headers[HttpRequestHeader.Authorization] = "bearer " + apiKey;
            if (!string.IsNullOrWhiteSpace(profile.database)) request.Headers["X-Odoo-Database"] = profile.database;
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    char[] buffer = new char[8192];
                    StringBuilder content = new StringBuilder();
                    int read;
                    const int maximum = 16 * 1024 * 1024;
                    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (content.Length + read > maximum) throw new ConsultantException("odoo_doc_too_large", "La documentación dinámica supera el límite seguro de lectura.");
                        content.Append(buffer, 0, read);
                    }
                    string text = content.ToString();
                    foreach (KeyValuePair<string, string> contract in contracts)
                    {
                        string[] parts = (contract.Value ?? string.Empty).Split('/');
                        if (parts.Length != 2 || !ModelPattern.IsMatch(parts[0]) || !MethodPattern.IsMatch(parts[1])) throw new ConsultantException("invalid_odoo_contract", "El sondeo de métodos contiene un contrato inválido.");
                        int modelAt = text.IndexOf(parts[0], StringComparison.Ordinal);
                        int methodAt = text.IndexOf(parts[1], StringComparison.Ordinal);
                        result[contract.Key] = modelAt >= 0 && methodAt >= 0;
                    }
                    return JsonUtil.Dictionary("available", true, "status", (int)response.StatusCode, "methods", result, "document_hash", JsonUtil.Sha256(text), "content_length", text.Length);
                }
            }
            catch (ConsultantException) { throw; }
            catch (WebException error)
            {
                using (HttpWebResponse response = error.Response as HttpWebResponse)
                {
                    int status = response == null ? 0 : (int)response.StatusCode;
                    return JsonUtil.Dictionary("available", false, "status", status, "methods", result, "document_hash", null);
                }
            }
        }

        public long Create(string model, IDictionary<string, object> values)
        {
            return FirstInteger(Call(model, "create", JsonUtil.Dictionary("vals_list", new object[] { values })), "Odoo no devolvió el ID creado.");
        }

        public long CreateSurveyComponent(string model, IDictionary<string, object> values)
        {
            if (model != "survey.question" && model != "survey.question.answer") throw new ConsultantException("invalid_odoo_contract", "La creación múltiple cerrada solo está disponible para componentes de Encuestas.");
            if (profile.api_transport == "legacy_rpc" && model == "survey.question") throw new ConsultantException("invalid_odoo_contract", "Las versiones con protocolo RPC heredado deben crear preguntas mediante la relación cerrada de la encuesta padre.");
            Dictionary<string, object> body = JsonUtil.Dictionary("vals_list", new object[] { values });
            // survey.question.create de Odoo 18 implementa el contrato
            // model_create_multi. El RPC heredado debe conservar la lista como
            // primer argumento posicional; extraer el diccionario individual
            // provoca un TypeError dentro del método del modelo.
            if (profile.api_transport == "legacy_rpc") body["_closed_model_create_multi"] = true;
            return FirstInteger(Call(model, "create", body), "Odoo no devolvió el ID del componente de Encuestas.");
        }

        public bool Write(string model, IEnumerable<long> ids, IDictionary<string, object> values)
        {
            List<object> idList = new List<object>();
            foreach (long id in ids) idList.Add(id);
            object response = Call(model, "write", JsonUtil.Dictionary("ids", idList.ToArray(), "vals", values));
            return response is bool ? (bool)response : string.Equals(Convert.ToString(response, CultureInfo.InvariantCulture), "true", StringComparison.OrdinalIgnoreCase);
        }

        public object RunServerAction(long actionId, string activeModel, long activeId)
        {
            if (actionId <= 0 || activeId <= 0) throw new ConsultantException("invalid_uat_target", "La acción y el registro de prueba deben tener IDs positivos.");
            if (!ModelPattern.IsMatch(activeModel ?? string.Empty)) throw new ConsultantException("invalid_model", "El modelo UAT no es válido.");
            return Call("ir.actions.server", "run", JsonUtil.Dictionary("ids", new object[] { actionId }, "context", JsonUtil.Dictionary("active_model", activeModel, "active_id", activeId, "active_ids", new object[] { activeId })));
        }

        public bool IsSystemAdministrator(long userId)
        {
            if (userId <= 0) throw new ConsultantException("invalid_odoo_identity", "El UID verificado no es válido.");
            object response = Call("res.users", "has_group", JsonUtil.Dictionary("ids", new object[] { userId }, "group_ext_id", "base.group_system"));
            return response is bool ? (bool)response : string.Equals(Convert.ToString(response, CultureInfo.InvariantCulture), "true", StringComparison.OrdinalIgnoreCase);
        }

        public object InstallModules(IEnumerable<long> moduleIds)
        {
            List<object> ids = new List<object>();
            foreach (long id in moduleIds)
            {
                if (id <= 0) throw new ConsultantException("invalid_module_id", "El plan contiene un ID de módulo no válido.");
                if (!ids.Contains(id)) ids.Add(id);
            }
            if (ids.Count == 0) throw new ConsultantException("module_install_empty", "No hay módulos pendientes de instalación.");
            return Call("ir.module.module", "button_immediate_install", JsonUtil.Dictionary("ids", ids.ToArray()));
        }

        public static Dictionary<string, object> DetectInstance(string baseUrl, int requestTimeoutMs)
        {
            string normalized = NormalizeBaseUrl(baseUrl);
            HttpWebRequest request = CreateHttpRequest(normalized + "/web/webclient/version_info", "POST", requestTimeoutMs);
            request.Accept = "application/json";
            request.ContentType = "application/json; charset=utf-8";
            byte[] payload = Encoding.UTF8.GetBytes(JsonUtil.Serialize(JsonUtil.Dictionary("jsonrpc", "2.0", "method", "call", "params", new Dictionary<string, object>(), "id", Guid.NewGuid().ToString("N"))));
            request.ContentLength = payload.Length;
            try
            {
                using (Stream output = request.GetRequestStream()) output.Write(payload, 0, payload.Length);
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    Dictionary<string, object> envelope = JsonUtil.DeserializeObject(reader.ReadToEnd());
                    Dictionary<string, object> result = Get(envelope, "result") as Dictionary<string, object> ?? envelope;
                    string serverVersion = Convert.ToString(Get(result, "server_version"), CultureInfo.InvariantCulture);
                    string series = MajorSeries(serverVersion);
                    string transport = series == "19.0" ? "json2" : IsLegacyRpcSeries(series) ? "legacy_rpc" : "unsupported";
                    return JsonUtil.Dictionary("base_url", normalized, "server_version", serverVersion, "server_series", series, "transport", transport);
                }
            }
            catch (WebException error)
            {
                HttpWebResponse response = error.Response as HttpWebResponse;
                int status = response == null ? 0 : (int)response.StatusCode;
                if (response != null) response.Dispose();
                throw new ConsultantException("instance_identification_failed", status > 0 ? "No se pudo identificar la versión de Odoo (HTTP " + status + ")." : "No se recibió respuesta al identificar la versión de Odoo.");
            }
            finally { Array.Clear(payload, 0, payload.Length); }
        }

        private object Call(string model, string method, IDictionary<string, object> body)
        {
            if (!ModelPattern.IsMatch(model ?? string.Empty) || !MethodPattern.IsMatch(method ?? string.Empty)) throw new ConsultantException("invalid_odoo_contract", "El contrato interno de Odoo no es válido.");
            return profile.api_transport == "legacy_rpc" ? CallLegacy(model, method, body) : CallJson2(model, method, body);
        }

        private object CallJson2(string model, string method, IDictionary<string, object> body)
        {
            string url = profile.base_url.TrimEnd('/') + "/json/2/" + Uri.EscapeDataString(model) + "/" + Uri.EscapeDataString(method);
            HttpWebRequest request = CreateRequest(url, "POST");
            request.ContentType = "application/json; charset=utf-8";
            request.Accept = "application/json";
            request.Headers[HttpRequestHeader.Authorization] = "bearer " + apiKey;
            if (!string.IsNullOrWhiteSpace(profile.database)) request.Headers["X-Odoo-Database"] = profile.database;
            return SendJson(request, body, false);
        }

        private object CallLegacy(string model, string method, IDictionary<string, object> body)
        {
            long uid = AuthenticateLegacy();
            object[] positional;
            Dictionary<string, object> keywords = new Dictionary<string, object>();
            if (method == "context_get" || method == "fields_get") positional = new object[0];
            else if (method == "read") positional = new object[] { Get(body, "ids") };
            else if (method == "search_read") positional = new object[] { Get(body, "domain") ?? new object[0] };
            else if (method == "check_access_rights") positional = new object[] { Get(body, "operation") };
            else if (method == "create")
            {
                IEnumerable values = Get(body, "vals_list") as IEnumerable;
                object first = null;
                if (values != null) foreach (object item in values) { first = item; break; }
                bool modelCreateMulti = Get(body, "_closed_model_create_multi") is bool && (bool)Get(body, "_closed_model_create_multi");
                positional = new object[] { modelCreateMulti ? (object)Get(body, "vals_list") : first };
            }
            else if (method == "write") positional = new object[] { Get(body, "ids"), Get(body, "vals") };
            else if (method == "run") positional = new object[] { Get(body, "ids") };
            else if (method == "has_group") positional = new object[] { Get(body, "ids"), Get(body, "group_ext_id") };
            else if (method == "button_immediate_install") positional = new object[] { Get(body, "ids") };
            else if (method == "action_confirm" || method == "button_confirm" || method == "action_set_won" || method == "action_set_lost" || method == "action_assign" || method == "button_validate" || method == "action_post" || method == "action_apply_inventory" || method == "compute_landed_cost" || method == "action_done" || method == "action_approve" || method == "action_cancel" || method == "button_cancel" || method == "action_create_invoice" || method == "reconcile" || method == "compute_depreciation_board" || method == "validate" || method == "action_compute" || method == "action_validate" || method == "action_apply" || method == "action_lost_reason_apply" || method == "create_invoices" || method == "reverse_moves" || method == "create_debit" || method == "action_create_payments") positional = new object[] { Get(body, "ids") };
            else throw new ConsultantException("invalid_odoo_contract", "El método no pertenece al catálogo cerrado para el protocolo RPC heredado.");

            foreach (KeyValuePair<string, object> pair in body)
            {
                if (pair.Key == "ids" || pair.Key == "domain" || pair.Key == "vals" || pair.Key == "vals_list" || pair.Key == "operation" || pair.Key == "group_ext_id" || pair.Key == "_closed_model_create_multi") continue;
                if (pair.Value != null) keywords[pair.Key] = pair.Value;
            }
            object[] rpcArgs = new object[] { profile.database, uid, apiKey, model, method, positional, keywords };
            return JsonRpc("object", "execute_kw", rpcArgs);
        }

        private long AuthenticateLegacy()
        {
            if (authenticatedUid > 0) return authenticatedUid;
            object result = JsonRpc("common", "authenticate", new object[] { profile.database, profile.login, apiKey, new Dictionary<string, object>() });
            long uid;
            if (!long.TryParse(Convert.ToString(result, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out uid) || uid <= 0) throw new ConsultantException("odoo_authentication_failed", "Odoo 18 rechazó la credencial, el login o el nombre de base.");
            if (profile.uid > 0 && profile.uid != uid) throw new ConsultantException("odoo_identity_changed", "La credencial ya no corresponde al UID validado.");
            authenticatedUid = uid;
            return uid;
        }

        private object JsonRpc(string service, string method, object[] args)
        {
            Dictionary<string, object> payload = JsonUtil.Dictionary("jsonrpc", "2.0", "method", "call", "params", JsonUtil.Dictionary("service", service, "method", method, "args", args), "id", Guid.NewGuid().ToString("N"));
            HttpWebRequest request = CreateRequest(profile.base_url.TrimEnd('/') + "/jsonrpc", "POST");
            request.ContentType = "application/json; charset=utf-8";
            request.Accept = "application/json";
            return SendJson(request, payload, true);
        }

        private object SendJson(HttpWebRequest request, object body, bool rpcEnvelope)
        {
            byte[] payload = Encoding.UTF8.GetBytes(JsonUtil.Serialize(body));
            request.ContentLength = payload.Length;
            try
            {
                using (Stream output = request.GetRequestStream()) output.Write(payload, 0, payload.Length);
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string text = reader.ReadToEnd();
                    if (!rpcEnvelope) return JsonUtil.DeserializeObject("{\"value\":" + text + "}")["value"];
                    Dictionary<string, object> envelope = JsonUtil.DeserializeObject(text);
                    if (envelope.ContainsKey("error")) throw new ConsultantException("odoo_rpc_error", "Odoo rechazó una operación RPC cerrada" + SafeRpcErrorClass(envelope) + ".");
                    return Get(envelope, "result");
                }
            }
            catch (ConsultantException) { throw; }
            catch (WebException error)
            {
                using (HttpWebResponse response = error.Response as HttpWebResponse)
                {
                    int status = response == null ? 0 : (int)response.StatusCode;
                    string transport = profile.api_transport == "legacy_rpc" ? "RPC" : "JSON-2";
                    string safeMessage = status == 401 || status == 403
                        ? "Odoo rechazó la credencial o los permisos para esta operación (HTTP " + status + ")."
                        : status > 0 ? "La llamada " + transport + " fue rechazada por Odoo (HTTP " + status + ")."
                        : "No se recibió respuesta HTTP de Odoo. Fallo de transporte: " + error.Status + ".";
                    throw new ConsultantException("odoo_request_failed", safeMessage);
                }
            }
            finally { Array.Clear(payload, 0, payload.Length); }
        }

        public static HttpWebRequest CreateHttpRequest(string url, string method, int timeoutMs)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.UserAgent = ProductInfo.UserAgent;
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            ConfigureTransport(request);
            return request;
        }

        private HttpWebRequest CreateRequest(string url, string method)
        {
            return CreateHttpRequest(url, method, timeoutMs);
        }

        private static string SafeRpcErrorClass(IDictionary<string, object> envelope)
        {
            object rawError;
            if (envelope == null || !envelope.TryGetValue("error", out rawError)) return string.Empty;
            Dictionary<string, object> error = rawError as Dictionary<string, object>;
            object rawData;
            Dictionary<string, object> data = error != null && error.TryGetValue("data", out rawData) ? rawData as Dictionary<string, object> : null;
            string name = data == null ? string.Empty : Convert.ToString(Get(data, "name"), CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(name) || !Regex.IsMatch(name, "^[A-Za-z0-9_.]{1,120}$")) return string.Empty;
            string[] parts = name.Split('.');
            return "; clase segura: " + parts[parts.Length - 1] + SafeRpcStructuralDetail(data, parts[parts.Length - 1]);
        }

        private static string SafeRpcStructuralDetail(IDictionary<string, object> data, string exceptionClass)
        {
            if (data == null || (exceptionClass != "TypeError" && exceptionClass != "ValidationError" && exceptionClass != "UserError")) return string.Empty;
            string raw = Convert.ToString(Get(data, "debug"), CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(raw)) raw = Convert.ToString(Get(data, "message"), CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            MatchCollection matches = Regex.Matches(raw, @"(?:TypeError|ValidationError|UserError):\s*([^\r\n]{1,300})", RegexOptions.IgnoreCase);
            string detail = matches.Count > 0 ? matches[matches.Count - 1].Groups[1].Value : string.Empty;
            if (string.IsNullOrWhiteSpace(detail)) return string.Empty;
            detail = Regex.Replace(detail, "(['\"])(?:\\\\.|(?!\\1).)*\\1", "'<redacted>'");
            detail = Regex.Replace(detail, @"\b\d+\b", "<n>");
            detail = Regex.Replace(detail, @"[^A-Za-z0-9_ .,:;()<>+\-/*=\[\]]", "?");
            detail = Regex.Replace(detail, @"\s+", " ").Trim();
            if (detail.Length > 220) detail = detail.Substring(0, 220);
            return string.IsNullOrWhiteSpace(detail) ? string.Empty : "; detalle estructural: " + detail;
        }

        private static object Get(IDictionary<string, object> values, string key)
        {
            object value;
            return values != null && values.TryGetValue(key, out value) ? value : null;
        }

        private static bool IsLegacyRpcSeries(string series)
        {
            return series == "14.0" || series == "15.0" || series == "16.0" || series == "17.0" || series == "18.0";
        }

        private static string MajorSeries(string version)
        {
            Match match = Regex.Match(version ?? string.Empty, "^(1[4-9])(?:\\.|$)");
            return match.Success ? match.Groups[1].Value + ".0" : string.Empty;
        }

        private static void ConfigureTransport(HttpWebRequest request)
        {
            request.KeepAlive = false;
            request.ServicePoint.Expect100Continue = false;
            IWebProxy proxy = request.Proxy;
            if (proxy == null) return;
            try
            {
                Uri proxyUri = proxy.GetProxy(request.RequestUri);
                if (proxyUri != null && proxyUri.IsLoopback && proxyUri.Port == 9) request.Proxy = null;
            }
            catch (Exception) { }
        }

        private static object[] ToObjectArray(IEnumerable<string> values)
        {
            List<object> result = new List<object>();
            if (values != null) foreach (string value in values) result.Add(value);
            return result.ToArray();
        }

        private static Dictionary<string, object> AsDictionary(object value, string message)
        {
            Dictionary<string, object> dictionary = value as Dictionary<string, object>;
            if (dictionary == null) throw new ConsultantException("invalid_odoo_response", message);
            return dictionary;
        }

        private static IList<Dictionary<string, object>> AsRecordList(object value)
        {
            List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();
            IEnumerable sequence = value as IEnumerable;
            if (sequence == null || value is string) throw new ConsultantException("invalid_odoo_response", "Odoo no devolvió una lista de registros.");
            foreach (object item in sequence)
            {
                Dictionary<string, object> record = item as Dictionary<string, object>;
                if (record != null) records.Add(record);
            }
            return records;
        }

        private static long FirstInteger(object value, string message)
        {
            IEnumerable sequence = value as IEnumerable;
            if (sequence != null && !(value is string)) foreach (object item in sequence) { long parsed; if (long.TryParse(Convert.ToString(item, CultureInfo.InvariantCulture), out parsed)) return parsed; }
            long direct;
            if (long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out direct)) return direct;
            throw new ConsultantException("invalid_odoo_response", message);
        }

        public static void ValidateBaseUrl(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri)) throw new ConsultantException("invalid_odoo_url", "La URL de Odoo no es válida.");
            bool local = string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) || uri.Host == "127.0.0.1" || uri.Host == "::1";
            if (uri.Scheme != Uri.UriSchemeHttps && !(local && uri.Scheme == Uri.UriSchemeHttp)) throw new ConsultantException("insecure_odoo_url", "Odoo debe usar HTTPS; HTTP solo se permite en localhost.");
            if (!string.IsNullOrEmpty(uri.UserInfo)) throw new ConsultantException("invalid_odoo_url", "La URL no puede contener usuario ni contraseña.");
        }

        public static string NormalizeBaseUrl(string value)
        {
            string candidate = (value ?? string.Empty).Trim();
            ValidateBaseUrl(candidate);
            Uri uri = new Uri(candidate, UriKind.Absolute);
            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) throw new ConsultantException("invalid_odoo_url", "La URL de Odoo no puede contener parámetros ni fragmentos.");
            string normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            if (normalized.EndsWith("/odoo", StringComparison.OrdinalIgnoreCase)) normalized = normalized.Substring(0, normalized.Length - "/odoo".Length).TrimEnd('/');
            ValidateBaseUrl(normalized);
            return normalized;
        }

        public static Dictionary<string, object> DiscoverDatabase(string rawUrl, int requestTimeoutMs)
        {
            if (string.IsNullOrWhiteSpace(rawUrl))
                throw new ConsultantException("invalid_odoo_url", "La URL de Odoo es obligatoria.");

            string candidate = rawUrl.Trim();
            if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (candidate.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                    candidate.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                    candidate.StartsWith("[::1]", StringComparison.OrdinalIgnoreCase))
                {
                    candidate = "http://" + candidate;
                }
                else
                {
                    candidate = "https://" + candidate;
                }
            }

            Uri uri;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out uri))
                throw new ConsultantException("invalid_odoo_url", "La URL proporcionada no tiene un formato válido.");

            bool isLocal = string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                           uri.Host == "127.0.0.1" || uri.Host == "::1";
            if (uri.Scheme != Uri.UriSchemeHttps && !(isLocal && uri.Scheme == Uri.UriSchemeHttp))
                throw new ConsultantException("insecure_odoo_url", "Odoo debe usar HTTPS; HTTP solo se permite en localhost.");

            if (!string.IsNullOrEmpty(uri.UserInfo))
                throw new ConsultantException("invalid_odoo_url", "La URL no puede contener usuario ni contraseña.");

            string path = uri.AbsolutePath.TrimEnd('/');
            if (path.EndsWith("/web", StringComparison.OrdinalIgnoreCase)) path = path.Substring(0, path.Length - "/web".Length);
            if (path.EndsWith("/odoo", StringComparison.OrdinalIgnoreCase)) path = path.Substring(0, path.Length - "/odoo".Length);
            path = path.TrimEnd('/');

            string baseUrl = uri.Scheme + "://" + uri.Authority + path;
            string host = uri.Host.ToLowerInvariant();
            int timeout = requestTimeoutMs > 0 ? requestTimeoutMs : 15000;

            string hosting = "on_premise_custom";
            if (host.EndsWith(".odoo.com", StringComparison.OrdinalIgnoreCase))
            {
                hosting = "odoo_online_saas";
            }

            List<string> discoveredDatabases = new List<string>();
            string serverHeader = null;

            // 1. Sondeo POST /web/database/list
            byte[] dbListPayload = Encoding.UTF8.GetBytes(JsonUtil.Serialize(JsonUtil.Dictionary(
                "jsonrpc", "2.0",
                "method", "call",
                "params", new Dictionary<string, object>(),
                "id", 1)));
            try
            {
                HttpWebRequest dbListReq = CreateHttpRequest(baseUrl + "/web/database/list", "POST", timeout);
                dbListReq.ContentType = "application/json; charset=utf-8";
                dbListReq.Accept = "application/json";
                dbListReq.ContentLength = dbListPayload.Length;

                using (Stream stream = dbListReq.GetRequestStream())
                {
                    stream.Write(dbListPayload, 0, dbListPayload.Length);
                }

                using (HttpWebResponse dbListResp = (HttpWebResponse)dbListReq.GetResponse())
                {
                    serverHeader = dbListResp.Headers["Server"];
                    using (StreamReader reader = new StreamReader(dbListResp.GetResponseStream(), Encoding.UTF8))
                    {
                        string jsonText = reader.ReadToEnd();
                        Dictionary<string, object> json = JsonUtil.DeserializeObject(jsonText);
                        object resultObj;
                        if (json != null && json.TryGetValue("result", out resultObj))
                        {
                            IEnumerable seq = resultObj as IEnumerable;
                            if (seq != null && !(resultObj is string))
                            {
                                foreach (object item in seq)
                                {
                                    string dbName = Convert.ToString(item, CultureInfo.InvariantCulture);
                                    if (!string.IsNullOrWhiteSpace(dbName) && !discoveredDatabases.Contains(dbName))
                                    {
                                        discoveredDatabases.Add(dbName.Trim());
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (WebException wex)
            {
                HttpWebResponse errResp = wex.Response as HttpWebResponse;
                if (errResp != null)
                {
                    serverHeader = errResp.Headers["Server"];
                    errResp.Close();
                }
            }
            catch (Exception) { }
            finally
            {
                Array.Clear(dbListPayload, 0, dbListPayload.Length);
            }

            if (!string.IsNullOrWhiteSpace(serverHeader) && serverHeader.IndexOf("Odoo.sh", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hosting = "odoo_sh";
            }

            // 2. Extraer subdominio si aplica
            string subdomain = string.Empty;
            if (host.EndsWith(".odoo.com", StringComparison.OrdinalIgnoreCase) && host.Length > ".odoo.com".Length)
            {
                subdomain = host.Substring(0, host.Length - ".odoo.com".Length);
            }
            else
            {
                string[] parts = host.Split('.');
                if (parts.Length > 2) subdomain = parts[0];
            }

            string targetDatabase = null;
            string confidence = "protected_database";
            string detectionMethod = "manual_required";
            string suggestionReason = null;

            if (discoveredDatabases.Count == 1)
            {
                targetDatabase = discoveredDatabases[0];
                confidence = "confirmed";
                detectionMethod = hosting == "odoo_sh" ? "odoo_sh_database_list" : "database_list_public";
                suggestionReason = "Base de datos única descubierta y confirmada por el servidor Odoo.";
            }
            else if (discoveredDatabases.Count > 1)
            {
                confidence = "multiple_found";
                detectionMethod = hosting == "odoo_sh" ? "odoo_sh_database_list" : "database_list_public";
                string match = null;
                if (!string.IsNullOrWhiteSpace(subdomain))
                {
                    foreach (string db in discoveredDatabases)
                    {
                        if (db.IndexOf(subdomain, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            subdomain.IndexOf(db, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            match = db;
                            break;
                        }
                    }
                }
                if (match != null)
                {
                    targetDatabase = match;
                    suggestionReason = "Sugerida por coincidencia con el subdominio '" + subdomain + "' entre las bases descubiertas.";
                }
                else
                {
                    targetDatabase = discoveredDatabases[0];
                    suggestionReason = "Primera base de datos en la lista pública de bases del servidor.";
                }
            }
            else
            {
                if (hosting == "odoo_online_saas" && !string.IsNullOrWhiteSpace(subdomain))
                {
                    targetDatabase = subdomain;
                    discoveredDatabases.Add(subdomain);
                    confidence = "inferred_saas";
                    detectionMethod = "saas_subdomain_inference";
                    suggestionReason = "En Odoo Online (SaaS *.odoo.com), el nombre técnico de la base coincide exactamente con el subdominio.";
                }
                else
                {
                    string loginDb = TryInspectLogin(baseUrl, timeout, ref serverHeader);
                    if (!string.IsNullOrWhiteSpace(loginDb))
                    {
                        targetDatabase = loginDb;
                        discoveredDatabases.Add(loginDb);
                        confidence = "confirmed";
                        detectionMethod = "login_page_inspection";
                        suggestionReason = "Detectada en la pantalla pública de inicio de sesión (/web/login).";
                    }
                    else
                    {
                        confidence = "protected_database";
                        detectionMethod = "manual_required";
                        suggestionReason = "El servidor tiene 'list_db = False' o utiliza un dominio personalizado protegido.";
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(serverHeader) && serverHeader.IndexOf("Odoo.sh", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hosting = "odoo_sh";
            }

            // 3. Sondeo POST /web/webclient/version_info
            string serverVersion = null;
            string serverSeries = null;
            string apiTransport = "json2";
            byte[] verPayload = Encoding.UTF8.GetBytes(JsonUtil.Serialize(JsonUtil.Dictionary(
                "jsonrpc", "2.0",
                "method", "call",
                "params", new Dictionary<string, object>(),
                "id", 1)));
            try
            {
                HttpWebRequest verReq = CreateHttpRequest(baseUrl + "/web/webclient/version_info", "POST", timeout);
                verReq.ContentType = "application/json; charset=utf-8";
                verReq.Accept = "application/json";
                verReq.ContentLength = verPayload.Length;

                using (Stream stream = verReq.GetRequestStream())
                {
                    stream.Write(verPayload, 0, verPayload.Length);
                }

                using (HttpWebResponse verResp = (HttpWebResponse)verReq.GetResponse())
                using (StreamReader reader = new StreamReader(verResp.GetResponseStream(), Encoding.UTF8))
                {
                    string jsonText = reader.ReadToEnd();
                    Dictionary<string, object> json = JsonUtil.DeserializeObject(jsonText);
                    object resultObj;
                    if (json != null && json.TryGetValue("result", out resultObj))
                    {
                        Dictionary<string, object> resDict = resultObj as Dictionary<string, object>;
                        if (resDict != null)
                        {
                            object verObj;
                            if (resDict.TryGetValue("server_version", out verObj))
                                serverVersion = Convert.ToString(verObj, CultureInfo.InvariantCulture);

                            object serieObj;
                            if (resDict.TryGetValue("server_serie", out serieObj))
                                serverSeries = Convert.ToString(serieObj, CultureInfo.InvariantCulture);
                        }
                    }
                }
            }
            catch (Exception) { }
            finally
            {
                Array.Clear(verPayload, 0, verPayload.Length);
            }

            if (string.IsNullOrWhiteSpace(serverSeries) && !string.IsNullOrWhiteSpace(serverVersion))
            {
                serverSeries = MajorSeries(serverVersion);
            }

            if (!string.IsNullOrWhiteSpace(serverSeries))
            {
                apiTransport = IsLegacyRpcSeries(serverSeries) ? "legacy_rpc" : "json2";
            }

            Dictionary<string, object> result = new Dictionary<string, object>();
            result["url_normalizada"] = baseUrl;
            result["base_datos"] = targetDatabase;
            result["bases_disponibles"] = discoveredDatabases.ToArray();
            result["nivel_confianza"] = confidence;
            result["metodo_deteccion"] = detectionMethod;
            result["tipo_alojamiento"] = hosting;
            result["version_odoo"] = serverVersion;
            result["serie_odoo"] = serverSeries;
            result["transporte_api_recomendado"] = apiTransport;
            result["motivo_sugerencia"] = suggestionReason;

            if (confidence == "protected_database" || string.IsNullOrWhiteSpace(targetDatabase))
            {
                result["instrucciones_consultor"] =
                    "Para obtener el nombre técnico de la base de datos en esta instancia protegida:\n" +
                    "1. Inicia sesión en Odoo desde tu navegador habitual.\n" +
                    "2. Abre las Herramientas de Desarrollador (F12) y dirígete a la pestaña 'Consola' (Console).\n" +
                    "3. Escribe `odoo.session_info.db` y presiona Enter. La consola te devolverá el nombre técnico exacto.\n" +
                    "4. Alternativamente, activa el Modo Desarrollador (?debug=1) en la URL y consulta el menú Ajustes -> Técnico.";
            }

            return result;
        }

        private static string TryInspectLogin(string baseUrl, int timeout, ref string serverHeader)
        {
            try
            {
                HttpWebRequest req = CreateHttpRequest(baseUrl + "/web/login", "GET", timeout);

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    if (string.IsNullOrWhiteSpace(serverHeader))
                        serverHeader = resp.Headers["Server"];

                    using (StreamReader reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        string html = reader.ReadToEnd();
                        Match match = Regex.Match(html, @"""db""\s*:\s*""([^""]+)""");
                        if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value) && match.Groups[1].Value != "false")
                        {
                            return match.Groups[1].Value.Trim();
                        }
                        Match inputMatch = Regex.Match(html, @"<input[^>]+name=[""']db[""'][^>]+value=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                        if (inputMatch.Success && !string.IsNullOrWhiteSpace(inputMatch.Groups[1].Value))
                        {
                            return inputMatch.Groups[1].Value.Trim();
                        }
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        public void Dispose()
        {
            apiKey = null;
            authenticatedUid = 0;
        }
    }
}
