using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace OdooConsultor
{
    public sealed class PolicyDecision
    {
        public bool executable { get; set; }
        public string risk { get; set; }
        public string route { get; set; }
        public string reason { get; set; }
        public string target_model { get; set; }
        public string objective { get; set; }
        public List<Dictionary<string, object>> operations { get; set; }
        public string contract_hash { get; set; }
    }

    public static class PolicyCompiler
    {
        public const string Version = "odoo-consultor-policy/2";
        private static readonly Regex ModelPattern = new Regex("^[a-z0-9_.]+$", RegexOptions.Compiled);
        private static readonly Regex FieldPattern = new Regex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.Compiled);
        private static readonly Regex CustomFieldPattern = new Regex("^x_[a-z0-9_]{2,60}$", RegexOptions.Compiled);
        private static readonly HashSet<string> AllowedFieldTypes = new HashSet<string>(new[] { "char", "text", "integer", "float", "boolean", "date", "datetime", "many2one" }, StringComparer.Ordinal);
        private static readonly HashSet<string> ExecutableOperations = new HashSet<string>(new[] { "create_custom_field", "create_inherited_view" }, StringComparer.Ordinal);
        private static readonly HashSet<string> RunbookOperations = new HashSet<string>(new[]
        {
            "create_model", "install_module", "create_report", "create_automation", "create_webhook",
            "change_security", "create_server_action", "computed_code", "call_method", "modify_existing",
            "delete", "production_promotion", "write_business_record"
        }, StringComparer.Ordinal);
        private static readonly HashSet<string> AllowedOperators = new HashSet<string>(new[] { "=", "!=", ">", ">=", "<", "<=", "in", "not in", "like", "ilike", "=like", "=ilike" }, StringComparer.Ordinal);
        private static readonly HashSet<string> DeniedReadModels = new HashSet<string>(new[]
        {
            "ir.config_parameter", "res.users.apikeys", "res.users.apikeys.description", "ir.attachment", "mail.message", "mail.mail"
        }, StringComparer.Ordinal);
        private static readonly Regex SensitiveField = new Regex("(?i)(password|passwd|secret|token|api[_]?key|private[_]?key|oauth|totp|signature)", RegexOptions.Compiled);

        public static PolicyDecision ClassifyChange(string environment, IDictionary<string, object> contract)
        {
            environment = ProjectStore.NormalizeEnvironment(environment);
            EnsureOnlyKeys(contract, new[] { "schema_version", "objective", "target_model", "operations", "impact", "requested_validations" });
            long schema = JsonUtil.GetLong(contract, "schema_version", true);
            if (schema != 1) throw new ConsultantException("unsupported_contract_schema", "El contrato declarativo debe usar schema_version 1.");
            string objective = JsonUtil.GetString(contract, "objective", true);
            if (objective.Length > 1000) throw new ConsultantException("invalid_contract", "El objetivo supera 1.000 caracteres.");
            string model = JsonUtil.GetString(contract, "target_model", true);
            ValidateModel(model);
            IList<object> rawOperations = ToList(contract.ContainsKey("operations") ? contract["operations"] : null);
            if (rawOperations.Count == 0 || rawOperations.Count > 10) throw new ConsultantException("invalid_contract", "El contrato debe contener entre 1 y 10 operaciones.");

            bool hasRunbook = false;
            List<Dictionary<string, object>> normalized = new List<Dictionary<string, object>>();
            foreach (object item in rawOperations)
            {
                Dictionary<string, object> operation = item as Dictionary<string, object>;
                if (operation == null) throw new ConsultantException("invalid_contract", "Cada operación debe ser un objeto.");
                EnsureOnlyKeys(operation, new[] { "type", "parameters" });
                string type = JsonUtil.GetString(operation, "type", true).ToLowerInvariant();
                if (!ExecutableOperations.Contains(type) && !RunbookOperations.Contains(type)) throw new ConsultantException("operation_not_supported", "La operación " + type + " no pertenece al lenguaje declarativo aprobado.");
                if (RunbookOperations.Contains(type)) hasRunbook = true;
                Dictionary<string, object> parameters = operation.ContainsKey("parameters") ? operation["parameters"] as Dictionary<string, object> : null;
                if (parameters == null) throw new ConsultantException("invalid_contract", "parameters debe ser un objeto.");
                if (ExecutableOperations.Contains(type)) ValidateDeclarativeParameters(type, parameters);
                else if (JsonUtil.Serialize(parameters).Length > 10000) throw new ConsultantException("invalid_contract", "Los detalles de una operación de runbook superan 10.000 caracteres.");
                normalized.Add(JsonUtil.Dictionary("type", type, "parameters", parameters));
            }
            if (hasRunbook && normalized.Any(delegate(Dictionary<string, object> op) { return ExecutableOperations.Contains(Convert.ToString(op["type"], CultureInfo.InvariantCulture)); }))
                throw new ConsultantException("mixed_risk_contract", "No se pueden mezclar operaciones ejecutables y de runbook en el mismo contrato.");

            object normalizedContract = JsonUtil.Dictionary("schema_version", 1, "objective", objective, "target_model", model, "operations", normalized);
            return new PolicyDecision
            {
                executable = !hasRunbook && environment == "staging",
                risk = hasRunbook ? "high" : "medium",
                route = hasRunbook || environment == "production" ? "runbook" : "confirmed_package",
                reason = hasRunbook ? "La operación requiere ejecución manual o aprobación reforzada." : (environment == "production" ? "Producción permanece en solo lectura." : "Operaciones aditivas y reversibles dentro del catálogo compilado."),
                target_model = model,
                objective = objective,
                operations = normalized,
                contract_hash = JsonUtil.CanonicalHash(normalizedContract)
            };
        }

        public static Dictionary<string, object> CompileQuery(IDictionary<string, object> query)
        {
            EnsureOnlyKeys(query, new[] { "model", "fields", "filters", "limit", "order_by", "order_direction" });
            string model = JsonUtil.GetString(query, "model", true);
            ValidateModel(model);
            if (DeniedReadModels.Contains(model)) throw new ConsultantException("model_read_blocked", "El modelo solicitado contiene secretos, adjuntos o mensajería y no está disponible en la primitiva general.");

            IList<object> rawFields = ToList(query.ContainsKey("fields") ? query["fields"] : null);
            if (rawFields.Count == 0 || rawFields.Count > 30) throw new ConsultantException("invalid_query", "Debes solicitar entre 1 y 30 campos.");
            List<string> fields = new List<string>();
            foreach (object raw in rawFields)
            {
                string field = Convert.ToString(raw, CultureInfo.InvariantCulture);
                ValidateReadableField(field);
                if (!fields.Contains(field, StringComparer.Ordinal)) fields.Add(field);
            }

            long requestedLimit = query.ContainsKey("limit") ? JsonUtil.GetLong(query, "limit", true) : 20;
            if (requestedLimit < 1 || requestedLimit > 50) throw new ConsultantException("invalid_query_limit", "El límite debe estar entre 1 y 50 registros.");
            List<object> domain = new List<object>();
            IList<object> filters = ToList(query.ContainsKey("filters") ? query["filters"] : null);
            if (filters.Count > 10) throw new ConsultantException("invalid_query", "La consulta admite como máximo 10 filtros conjuntivos.");
            foreach (object raw in filters)
            {
                Dictionary<string, object> filter = raw as Dictionary<string, object>;
                if (filter == null) throw new ConsultantException("invalid_query", "Cada filtro debe ser un objeto.");
                EnsureOnlyKeys(filter, new[] { "field", "operator", "value" });
                string field = JsonUtil.GetString(filter, "field", true);
                string op = JsonUtil.GetString(filter, "operator", true).ToLowerInvariant();
                ValidateReadableField(field);
                if (!AllowedOperators.Contains(op)) throw new ConsultantException("operator_not_allowed", "El operador del filtro no está permitido.");
                object value = filter.ContainsKey("value") ? filter["value"] : null;
                ValidateFilterValue(value, op);
                domain.Add(new object[] { field, op, value });
            }
            string orderBy = JsonUtil.GetString(query, "order_by", false);
            string direction = (JsonUtil.GetString(query, "order_direction", false) ?? "asc").ToLowerInvariant();
            string order = null;
            if (!string.IsNullOrWhiteSpace(orderBy))
            {
                ValidateReadableField(orderBy);
                if (direction != "asc" && direction != "desc") throw new ConsultantException("invalid_query", "order_direction debe ser asc o desc.");
                order = orderBy + " " + direction;
            }
            return JsonUtil.Dictionary("model", model, "fields", fields.ToArray(), "domain", domain.ToArray(), "limit", requestedLimit, "order", order);
        }

        public static void ValidateModel(string model)
        {
            if (!ModelPattern.IsMatch(model ?? string.Empty)) throw new ConsultantException("invalid_model", "El nombre técnico del modelo no es válido.");
        }

        public static void ValidateExecutableOperation(string packageId, ChangeOperation operation)
        {
            if (operation == null || !ExecutableOperations.Contains(operation.type ?? string.Empty)) throw new ConsultantException("tool_contract_violation", "El paquete contiene una operación fuera del catálogo compilado.");
            ValidateModel(operation.model);
            if (operation.parameters == null) throw new ConsultantException("tool_contract_violation", "La operación no contiene parámetros.");
            if (operation.type == "create_custom_field")
            {
                EnsurePackageKeys(operation.parameters, new[] { "name", "field_description", "ttype", "required", "readonly", "help", "relation" });
                string name = JsonUtil.GetString(operation.parameters, "name", true);
                string type = JsonUtil.GetString(operation.parameters, "ttype", true);
                if (!CustomFieldPattern.IsMatch(name) || !AllowedFieldTypes.Contains(type)) throw new ConsultantException("tool_contract_violation", "El campo no cumple el catálogo declarativo.");
                if (JsonUtil.GetBool(operation.parameters, "required", false) || JsonUtil.GetBool(operation.parameters, "readonly", false)) throw new ConsultantException("tool_contract_violation", "El campo intenta imponer propiedades no permitidas.");
                string relation = JsonUtil.GetString(operation.parameters, "relation", false);
                if (type == "many2one") ValidateModel(relation);
                else if (!string.IsNullOrWhiteSpace(relation)) throw new ConsultantException("tool_contract_violation", "La relación no corresponde al tipo del campo.");
            }
            else
            {
                EnsurePackageKeys(operation.parameters, new[] { "name", "inherit_id", "arch", "mode", "active" });
                string name = JsonUtil.GetString(operation.parameters, "name", true);
                if (!name.StartsWith("OdooAI - " + packageId + " - ", StringComparison.Ordinal) || JsonUtil.GetLong(operation.parameters, "inherit_id", true) <= 0 || JsonUtil.GetString(operation.parameters, "mode", true) != "extension" || !JsonUtil.GetBool(operation.parameters, "active", false))
                    throw new ConsultantException("tool_contract_violation", "La vista no cumple el contrato aditivo.");
                StudioService.ValidateSafeViewArchitecture(JsonUtil.GetString(operation.parameters, "arch", true));
            }
        }

        public static void EnsureOnlyKeys(IDictionary<string, object> values, IEnumerable<string> allowed)
        {
            if (values == null) throw new ConsultantException("invalid_input", "Se esperaba un objeto.");
            HashSet<string> allowlist = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (string key in values.Keys) if (!allowlist.Contains(key)) throw new ConsultantException("unknown_parameter", "El contrato contiene el parámetro desconocido " + key + ".");
        }

        private static void ValidateDeclarativeParameters(string type, IDictionary<string, object> parameters)
        {
            if (type == "create_custom_field")
            {
                EnsureOnlyKeys(parameters, new[] { "name", "label", "field_type", "help", "relation", "required" });
                string name = JsonUtil.GetString(parameters, "name", true).ToLowerInvariant();
                string label = JsonUtil.GetString(parameters, "label", true);
                string fieldType = JsonUtil.GetString(parameters, "field_type", true).ToLowerInvariant();
                if (!CustomFieldPattern.IsMatch(name) || !AllowedFieldTypes.Contains(fieldType)) throw new ConsultantException("invalid_contract", "La definición del campo no pertenece al catálogo permitido.");
                if (label.Length > 200) throw new ConsultantException("invalid_contract", "La etiqueta del campo supera 200 caracteres.");
                if (JsonUtil.GetBool(parameters, "required", false)) throw new ConsultantException("runbook_required", "Un campo requerido exige estrategia de datos y runbook.");
                string relation = JsonUtil.GetString(parameters, "relation", false);
                if (fieldType == "many2one") ValidateModel(relation);
                else if (!string.IsNullOrWhiteSpace(relation)) throw new ConsultantException("invalid_contract", "relation solo es válida para many2one.");
                string help = JsonUtil.GetString(parameters, "help", false);
                if (help != null && help.Length > 1000) throw new ConsultantException("invalid_contract", "La ayuda del campo supera 1.000 caracteres.");
            }
            else
            {
                EnsureOnlyKeys(parameters, new[] { "name", "parent_view_id", "architecture_xml" });
                string name = JsonUtil.GetString(parameters, "name", true);
                if (name.Length > 200 || JsonUtil.GetLong(parameters, "parent_view_id", true) <= 0) throw new ConsultantException("invalid_contract", "La vista declarativa no tiene nombre o vista padre válidos.");
                StudioService.ValidateSafeViewArchitecture(JsonUtil.GetString(parameters, "architecture_xml", true));
            }
        }

        private static void EnsurePackageKeys(IDictionary<string, object> values, IEnumerable<string> allowed)
        {
            HashSet<string> allowlist = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (string key in values.Keys) if (!allowlist.Contains(key)) throw new ConsultantException("tool_contract_violation", "El paquete contiene el parámetro no permitido " + key + ".");
        }

        private static IList<object> ToList(object value)
        {
            List<object> result = new List<object>();
            IEnumerable sequence = value as IEnumerable;
            if (sequence != null && !(value is string)) foreach (object item in sequence) result.Add(item);
            return result;
        }

        private static void ValidateReadableField(string field)
        {
            if (!FieldPattern.IsMatch(field ?? string.Empty) || SensitiveField.IsMatch(field)) throw new ConsultantException("field_read_blocked", "El campo solicitado no está permitido en la primitiva general.");
        }

        private static void ValidateFilterValue(object value, string op)
        {
            if (value is IDictionary) throw new ConsultantException("invalid_filter_value", "Los filtros no admiten objetos anidados.");
            IEnumerable sequence = value as IEnumerable;
            if (sequence != null && !(value is string))
            {
                if (op != "in" && op != "not in") throw new ConsultantException("invalid_filter_value", "Una lista solo se admite con in o not in.");
                int count = 0;
                foreach (object item in sequence)
                {
                    if (item is IDictionary || (item is IEnumerable && !(item is string))) throw new ConsultantException("invalid_filter_value", "El filtro contiene una estructura anidada.");
                    count++;
                }
                if (count > 100) throw new ConsultantException("invalid_filter_value", "La lista del filtro supera 100 valores.");
            }
            else if (op == "in" || op == "not in") throw new ConsultantException("invalid_filter_value", "in y not in requieren una lista.");
            string text = value as string;
            if (text != null && text.Length > 500) throw new ConsultantException("invalid_filter_value", "El valor del filtro supera 500 caracteres.");
        }
    }
}
