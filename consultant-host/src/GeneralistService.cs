using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace OdooConsultor
{
    public sealed class GeneralistService
    {
        private readonly ProjectStore project;
        private readonly StudioService studio;

        public GeneralistService(ProjectStore projectStore, StudioService studioService)
        {
            project = projectStore;
            studio = studioService;
        }

        public object Discover(string environment, string model, string searchText, int requestedLimit)
        {
            environment = ProjectStore.NormalizeEnvironment(environment);
            if (!string.IsNullOrWhiteSpace(model)) PolicyCompiler.ValidateModel(model);
            int limit = requestedLimit <= 0 ? 50 : requestedLimit;
            if (limit < 1 || limit > 100) throw new ConsultantException("invalid_discovery_limit", "El descubrimiento admite entre 1 y 100 modelos.");
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                using (OdooClient client = Open(environment))
                {
                    List<object> domain = new List<object>();
                    if (!string.IsNullOrWhiteSpace(model)) domain.Add(new object[] { "model", "=", model });
                    else if (!string.IsNullOrWhiteSpace(searchText))
                    {
                        string safe = searchText.Trim();
                        if (safe.Length > 100) throw new ConsultantException("invalid_discovery_query", "La búsqueda supera 100 caracteres.");
                        domain.Add(new object[] { "name", "ilike", safe });
                    }
                    IList<Dictionary<string, object>> models = client.SearchRead("ir.model", domain.ToArray(),
                        new[] { "id", "name", "model", "state", "transient" }, limit, "model asc");
                    IList<Dictionary<string, object>> modules = client.SearchRead("ir.module.module",
                        new object[] { new object[] { "state", "=", "installed" } },
                        new[] { "id", "name", "shortdesc", "installed_version", "application" }, 200, "name asc");
                    Dictionary<string, object> doc = client.ProbeDocumentation();
                    Dictionary<string, object> target = null;
                    if (!string.IsNullOrWhiteSpace(model))
                    {
                        if (models.Count != 1) throw new ConsultantException("model_not_found", "El modelo no existe o no es visible para el usuario.");
                        Dictionary<string, object> fields = client.FieldsGet(model, new[] { "string", "type", "required", "readonly", "store", "relation" });
                        target = JsonUtil.Dictionary(
                            "model", model,
                            "field_count", fields.Count,
                            "field_metadata_hash", JsonUtil.CanonicalHash(fields),
                            "access", AccessMatrix(client, model),
                            "technical_write_access", JsonUtil.Dictionary(
                                "ir_model_fields_create", SafeAccess(client, "ir.model.fields", "create"),
                                "ir_ui_view_create", SafeAccess(client, "ir.ui.view", "create")));
                    }
                    object capabilityPayload = JsonUtil.Dictionary(
                        "client_slug", project.Config.client_slug,
                        "environment", environment,
                        "profile_uid", project.GetProfile(environment, true).uid,
                        "models", models,
                        "installed_modules", modules,
                        "target", target);
                    string hash = JsonUtil.CanonicalHash(capabilityPayload);
                    AuditLog.Append(project, environment, "odoo_descubrir_capacidades", null, "success",
                        "Descubrimiento: " + models.Count.ToString(CultureInfo.InvariantCulture) + " modelos y " + modules.Count.ToString(CultureInfo.InvariantCulture) + " módulos; hash " + hash.Substring(0, 12) + ".", timer.ElapsedMilliseconds);
                    return JsonUtil.Dictionary(
                        "client_slug", project.Config.client_slug,
                        "environment", environment,
                        "documentation", doc,
                        "models", models,
                        "installed_modules", modules,
                        "target_capabilities", target,
                        "capability_hash", hash,
                        "notes", new[] { "La lista respeta la visibilidad del usuario de Odoo.", "Los permisos detallados se calculan cuando se indica un modelo concreto.", "El endpoint /doc se sondea, pero sus contenidos no se guardan en evidencia." });
                }
            }
            catch (Exception error)
            {
                AuditLog.Append(project, environment, "odoo_descubrir_capacidades", null, "error", SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }

        public object Search(string environment, IDictionary<string, object> query)
        {
            environment = ProjectStore.NormalizeEnvironment(environment);
            Dictionary<string, object> compiled = PolicyCompiler.CompileQuery(query);
            string model = Convert.ToString(compiled["model"], CultureInfo.InvariantCulture);
            Stopwatch timer = Stopwatch.StartNew();
            string queryHash = JsonUtil.CanonicalHash(compiled);
            try
            {
                using (OdooClient client = Open(environment))
                {
                    if (!client.CheckAccessRights(model, "read")) throw new ConsultantException("read_access_denied", "El usuario no tiene permiso de lectura sobre el modelo.");
                    Dictionary<string, object> metadata = client.FieldsGet(model, new[] { "type", "store", "readonly" });
                    string[] fields = ((IEnumerable)compiled["fields"]).Cast<object>().Select(delegate(object value) { return Convert.ToString(value, CultureInfo.InvariantCulture); }).ToArray();
                    foreach (string field in fields) ValidateFieldFromMetadata(metadata, field);
                    object[] domain = ((IEnumerable)compiled["domain"]).Cast<object>().ToArray();
                    foreach (object condition in domain)
                    {
                        object[] tuple = ((IEnumerable)condition).Cast<object>().ToArray();
                        ValidateFieldFromMetadata(metadata, Convert.ToString(tuple[0], CultureInfo.InvariantCulture));
                    }
                    int limit = Convert.ToInt32(compiled["limit"], CultureInfo.InvariantCulture);
                    string order = compiled["order"] == null ? null : Convert.ToString(compiled["order"], CultureInfo.InvariantCulture);
                    IList<Dictionary<string, object>> records = client.SearchRead(model, domain, fields, limit, order);
                    AuditLog.Append(project, environment, "odoo_consultar_registros", null, "success",
                        "Consulta " + queryHash.Substring(0, 12) + " sobre " + model + "; " + records.Count.ToString(CultureInfo.InvariantCulture) + " registros.", timer.ElapsedMilliseconds);
                    return JsonUtil.Dictionary("environment", environment, "model", model, "query_hash", queryHash, "count", records.Count, "limit", limit, "records", records);
                }
            }
            catch (Exception error)
            {
                AuditLog.Append(project, environment, "odoo_consultar_registros", null, "error", "Consulta " + queryHash.Substring(0, 12) + ": " + SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }

        public object CompareConfiguration(string firstEnvironment, string secondEnvironment, string model)
        {
            firstEnvironment = ProjectStore.NormalizeEnvironment(firstEnvironment);
            secondEnvironment = ProjectStore.NormalizeEnvironment(secondEnvironment);
            PolicyCompiler.ValidateModel(model);
            if (firstEnvironment == secondEnvironment) throw new ConsultantException("invalid_comparison", "Los entornos de comparación deben ser diferentes.");
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                Dictionary<string, object> first;
                Dictionary<string, object> second;
                using (OdooClient client = Open(firstEnvironment)) first = ConfigurationDescriptor(client, model);
                using (OdooClient client = Open(secondEnvironment)) second = ConfigurationDescriptor(client, model);
                List<string> firstFields = ((Dictionary<string, object>)first["fields"]).Keys.OrderBy(delegate(string x) { return x; }, StringComparer.Ordinal).ToList();
                List<string> secondFields = ((Dictionary<string, object>)second["fields"]).Keys.OrderBy(delegate(string x) { return x; }, StringComparer.Ordinal).ToList();
                Dictionary<string, object> firstFieldDefinitions = (Dictionary<string, object>)first["fields"];
                Dictionary<string, object> secondFieldDefinitions = (Dictionary<string, object>)second["fields"];
                List<string> changedFields = firstFields.Intersect(secondFields, StringComparer.Ordinal)
                    .Where(delegate(string field) { return JsonUtil.CanonicalHash(firstFieldDefinitions[field]) != JsonUtil.CanonicalHash(secondFieldDefinitions[field]); })
                    .ToList();
                List<string> firstViews = ViewSignatures((IList<Dictionary<string, object>>)first["views"]);
                List<string> secondViews = ViewSignatures((IList<Dictionary<string, object>>)second["views"]);
                object differences = JsonUtil.Dictionary(
                    "fields_only_in_first", firstFields.Except(secondFields, StringComparer.Ordinal).ToArray(),
                    "fields_only_in_second", secondFields.Except(firstFields, StringComparer.Ordinal).ToArray(),
                    "field_definitions_changed", changedFields.ToArray(),
                    "views_only_in_first", firstViews.Except(secondViews, StringComparer.Ordinal).ToArray(),
                    "views_only_in_second", secondViews.Except(firstViews, StringComparer.Ordinal).ToArray());
                AuditLog.Append(project, "comparison", "odoo_comparar_configuracion", null, "success", "Configuración comparada para " + model + ".", timer.ElapsedMilliseconds);
                return JsonUtil.Dictionary(
                    "model", model,
                    "first", JsonUtil.Dictionary("environment", firstEnvironment, "hash", first["hash"], "field_count", firstFields.Count, "view_count", firstViews.Count),
                    "second", JsonUtil.Dictionary("environment", secondEnvironment, "hash", second["hash"], "field_count", secondFields.Count, "view_count", secondViews.Count),
                    "equal", string.Equals(Convert.ToString(first["hash"], CultureInfo.InvariantCulture), Convert.ToString(second["hash"], CultureInfo.InvariantCulture), StringComparison.Ordinal),
                    "differences", differences);
            }
            catch (Exception error)
            {
                AuditLog.Append(project, "comparison", "odoo_comparar_configuracion", null, "error", SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }

        public object ValidateChange(string environment, IDictionary<string, object> contract)
        {
            environment = ProjectStore.NormalizeEnvironment(environment);
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                PolicyDecision decision = PolicyCompiler.ClassifyChange(environment, contract);
                Dictionary<string, object> capabilities = null;
                bool fieldCreate = false;
                bool viewCreate = false;
                using (OdooClient client = Open(environment))
                {
                    IList<Dictionary<string, object>> models = client.SearchRead("ir.model", new object[] { new object[] { "model", "=", decision.target_model } }, new[] { "id", "model", "name" }, 2, null);
                    if (models.Count != 1) throw new ConsultantException("model_not_found", "El modelo objetivo no existe o no es visible.");
                    fieldCreate = SafeAccess(client, "ir.model.fields", "create");
                    viewCreate = SafeAccess(client, "ir.ui.view", "create");
                    capabilities = JsonUtil.Dictionary(
                        "model", decision.target_model,
                        "access", AccessMatrix(client, decision.target_model),
                        "ir_model_fields_create", fieldCreate,
                        "ir_ui_view_create", viewCreate);
                }
                bool needsField = decision.operations.Any(delegate(Dictionary<string, object> operation) { return Convert.ToString(operation["type"], CultureInfo.InvariantCulture) == "create_custom_field"; });
                bool needsView = decision.operations.Any(delegate(Dictionary<string, object> operation) { return Convert.ToString(operation["type"], CultureInfo.InvariantCulture) == "create_inherited_view"; });
                bool permissionReady = (!needsField || fieldCreate) && (!needsView || viewCreate);
                bool executable = decision.executable && permissionReady;
                string route = decision.executable && !permissionReady ? "blocked_permissions" : decision.route;
                string reason = decision.executable && !permissionReady ? "El usuario no tiene permisos técnicos suficientes para compilar el paquete." : decision.reason;
                string capabilityHash = JsonUtil.CanonicalHash(capabilities);
                AuditLog.Append(project, environment, "cambio_validar_contrato", null, "success", "Contrato " + decision.contract_hash.Substring(0, 12) + " validado; ruta " + route + ".", timer.ElapsedMilliseconds);
                return JsonUtil.Dictionary(
                    "valid", true,
                    "policy_version", PolicyCompiler.Version,
                    "contract_hash", decision.contract_hash,
                    "capability_hash", capabilityHash,
                    "environment", environment,
                    "target_model", decision.target_model,
                    "risk", decision.risk,
                    "route", route,
                    "executable", executable,
                    "reason", reason,
                    "capabilities", capabilities);
            }
            catch (Exception error)
            {
                AuditLog.Append(project, environment, "cambio_validar_contrato", null, "error", SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }

        public object PrepareChange(string environment, IDictionary<string, object> contract)
        {
            PolicyDecision decision = PolicyCompiler.ClassifyChange(environment, contract);
            if (!decision.executable)
            {
                List<object> steps = new List<object>();
                foreach (Dictionary<string, object> operation in decision.operations) steps.Add("Revisar manualmente la operación " + Convert.ToString(operation["type"], CultureInfo.InvariantCulture) + " sobre " + decision.target_model + ".");
                return studio.GenerateRunbook(environment, RunbookCategory(decision), "Cambio controlado: " + decision.objective, decision.objective, steps);
            }
            return studio.PrepareDeclarativeChange(environment, contract, decision);
        }

        private Dictionary<string, object> ConfigurationDescriptor(OdooClient client, string model)
        {
            if (!client.CheckAccessRights(model, "read")) throw new ConsultantException("read_access_denied", "El usuario no tiene permiso de lectura sobre " + model + ".");
            Dictionary<string, object> fields = client.FieldsGet(model, new[] { "string", "type", "required", "readonly", "store", "relation" });
            IList<Dictionary<string, object>> views = client.SearchRead("ir.ui.view", new object[] { new object[] { "model", "=", model } },
                new[] { "id", "name", "type", "inherit_id", "mode", "priority", "active", "write_date" }, 500, "name asc");
            List<Dictionary<string, object>> normalizedViews = views.Select(delegate(Dictionary<string, object> row)
            {
                return JsonUtil.Dictionary(
                    "name", row.ContainsKey("name") ? row["name"] : null,
                    "type", row.ContainsKey("type") ? row["type"] : null,
                    "inherit", RelationDisplay(row.ContainsKey("inherit_id") ? row["inherit_id"] : null),
                    "mode", row.ContainsKey("mode") ? row["mode"] : null,
                    "priority", row.ContainsKey("priority") ? row["priority"] : null,
                    "active", row.ContainsKey("active") ? row["active"] : null);
            }).OrderBy(delegate(Dictionary<string, object> row) { return Convert.ToString(row["name"], CultureInfo.InvariantCulture); }, StringComparer.Ordinal).ToList();
            return JsonUtil.Dictionary("fields", fields, "views", normalizedViews, "hash", JsonUtil.CanonicalHash(JsonUtil.Dictionary("fields", fields, "views", normalizedViews)));
        }

        private static Dictionary<string, object> AccessMatrix(OdooClient client, string model)
        {
            return JsonUtil.Dictionary(
                "read", SafeAccess(client, model, "read"),
                "create", SafeAccess(client, model, "create"),
                "write", SafeAccess(client, model, "write"),
                "unlink", SafeAccess(client, model, "unlink"));
        }

        private static bool SafeAccess(OdooClient client, string model, string operation)
        {
            try { return client.CheckAccessRights(model, operation); }
            catch (ConsultantException) { return false; }
        }

        private static void ValidateFieldFromMetadata(IDictionary<string, object> metadata, string field)
        {
            object raw;
            if (!metadata.TryGetValue(field, out raw)) throw new ConsultantException("field_not_found", "El campo " + field + " no existe o no es visible.");
            Dictionary<string, object> definition = raw as Dictionary<string, object>;
            string type = definition == null || !definition.ContainsKey("type") ? string.Empty : Convert.ToString(definition["type"], CultureInfo.InvariantCulture);
            if (type == "binary" || type == "html") throw new ConsultantException("field_type_blocked", "Los campos binary y html no se devuelven mediante la primitiva general.");
        }

        private static List<string> ViewSignatures(IList<Dictionary<string, object>> views)
        {
            return views.Select(delegate(Dictionary<string, object> row)
            {
                return Convert.ToString(row.ContainsKey("name") ? row["name"] : string.Empty, CultureInfo.InvariantCulture) + "|" +
                    Convert.ToString(row.ContainsKey("type") ? row["type"] : string.Empty, CultureInfo.InvariantCulture) + "|" +
                    JsonUtil.CanonicalHash(row).Substring(0, 12);
            }).OrderBy(delegate(string x) { return x; }, StringComparer.Ordinal).ToList();
        }

        private static object RelationDisplay(object value)
        {
            IEnumerable sequence = value as IEnumerable;
            if (sequence != null && !(value is string))
            {
                List<object> parts = new List<object>();
                foreach (object item in sequence) parts.Add(item);
                if (parts.Count > 1) return Convert.ToString(parts[1], CultureInfo.InvariantCulture);
            }
            return value is bool ? value : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private OdooClient Open(string environment)
        {
            return project.OpenClient(environment, 30000);
        }

        private static string RunbookCategory(PolicyDecision decision)
        {
            string type = Convert.ToString(decision.operations[0]["type"], CultureInfo.InvariantCulture);
            if (type == "create_model") return "modelo_nuevo";
            if (type == "create_report") return "reporte";
            if (type == "create_automation") return "automatizacion";
            if (type == "create_webhook") return "webhook";
            if (type == "change_security") return "seguridad";
            if (type == "create_server_action") return "accion_servidor";
            if (type == "computed_code") return "codigo_calculado";
            if (type == "call_method") return "llamada_metodo";
            if (type == "delete") return "eliminacion";
            if (type == "production_promotion") return "promocion_produccion";
            return "modificacion";
        }

        private static string SafeError(Exception error)
        {
            return Redactor.SafeError(error);
        }
    }
}
