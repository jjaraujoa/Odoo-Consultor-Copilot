using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace OdooConsultor
{
    public sealed class DemoRegistryCapability
    {
        public bool read { get; set; }
        public bool create { get; set; }
        public bool available { get; set; }
        public string status { get; set; }
        public string capability_hash { get; set; }
    }

    public sealed class DemoRegistryRecord
    {
        public long registry_id { get; set; }
        public long record_id { get; set; }
        public string model { get; set; }
        public string module { get; set; }
        public string name { get; set; }
    }

    public static class DemoOwnershipRegistry
    {
        public const string RegistryModel = "ir.model.data";
        public const string RegistryModule = "odoo_ai_demo";
        private static readonly string[] RequiredFields = new[] { "module", "name", "model", "res_id", "noupdate" };
        private static readonly HashSet<string> DerivedModels = new HashSet<string>(new[] { "project.project", "project.task", "stock.picking", "purchase.order", "account.move", "account.payment", "account.loan.line", "account.move.line" }, StringComparer.Ordinal);

        internal static bool DerivedModelAllowedForSelfTest(string model) { return DerivedModels.Contains(model ?? string.Empty); }

        public static DemoRegistryCapability Probe(OdooClient client)
        {
            try
            {
                Dictionary<string, object> fields = client.FieldsGet(RegistryModel, new[] { "type", "required", "readonly", "string" });
                bool read = client.CheckAccessRights(RegistryModel, "read");
                bool create = client.CheckAccessRights(RegistryModel, "create");
                bool complete = RequiredFields.All(fields.ContainsKey);
                Dictionary<string, object> descriptor = JsonUtil.Dictionary("model", RegistryModel, "required_fields", RequiredFields, "read", read, "create", create, "complete", complete);
                return new DemoRegistryCapability
                {
                    read = read,
                    create = create,
                    available = read && create && complete,
                    status = read && create && complete ? "hybrid" : "local_only",
                    capability_hash = JsonUtil.CanonicalHash(descriptor)
                };
            }
            catch (ConsultantException)
            {
                return new DemoRegistryCapability { read = false, create = false, available = false, status = "local_only", capability_hash = JsonUtil.CanonicalHash(JsonUtil.Dictionary("model", RegistryModel, "available", false)) };
            }
        }

        public static DemoRegistryRecord Find(OdooClient client, DemoScenario scenario, DemoComponent component)
        {
            string name = TechnicalName(scenario, component.key);
            IList<Dictionary<string, object>> rows = client.SearchRead(RegistryModel, new object[] { new object[] { "module", "=", RegistryModule }, new object[] { "name", "=", name } }, new[] { "id", "module", "name", "model", "res_id" }, 2, null);
            if (rows.Count > 1) throw new ConsultantException("demo_registry_collision", "La identidad técnica de la demostración no es única.");
            if (rows.Count == 0) return null;
            Dictionary<string, object> row = rows[0];
            DemoAdapterContract contract = DemoAdapterCatalog.Get(component.adapter);
            string model = Convert.ToString(row["model"], CultureInfo.InvariantCulture);
            long resId = Convert.ToInt64(row["res_id"], CultureInfo.InvariantCulture);
            if (!string.Equals(model, contract.model, StringComparison.Ordinal) || resId <= 0) throw new ConsultantException("demo_registry_collision", "La identidad técnica apunta a otro artefacto.");
            if (client.Read(model, new[] { resId }, new[] { "display_name" }).Count != 1) throw new ConsultantException("demo_registry_stale", "La identidad técnica apunta a un registro que ya no existe o no es visible.");
            return new DemoRegistryRecord { registry_id = Convert.ToInt64(row["id"], CultureInfo.InvariantCulture), record_id = resId, model = model, module = RegistryModule, name = name };
        }

        public static DemoRegistryRecord Register(OdooClient client, DemoScenario scenario, DemoComponent component, long recordId, DemoRegistryCapability capability)
        {
            if (capability == null || !capability.available) return null;
            DemoRegistryRecord existing = Find(client, scenario, component);
            if (existing != null)
            {
                if (existing.record_id != recordId) throw new ConsultantException("demo_registry_collision", "La identidad técnica ya pertenece a otro registro.");
                return existing;
            }
            string name = TechnicalName(scenario, component.key);
            long registryId = client.Create(RegistryModel, JsonUtil.Dictionary("module", RegistryModule, "name", name, "model", DemoAdapterCatalog.Get(component.adapter).model, "res_id", recordId, "noupdate", true));
            return new DemoRegistryRecord { registry_id = registryId, record_id = recordId, model = DemoAdapterCatalog.Get(component.adapter).model, module = RegistryModule, name = name };
        }

        public static DemoRegistryRecord RegisterDerived(OdooClient client, DemoScenario scenario, string componentKey, string model, long recordId, DemoRegistryCapability capability)
        {
            if (capability == null || !capability.available) return null;
            if (!DerivedModels.Contains(model ?? string.Empty) || recordId <= 0) throw new ConsultantException("demo_derived_model_blocked", "El registro derivado no pertenece al catálogo cerrado.");
            string name = TechnicalName(scenario, componentKey);
            IList<Dictionary<string, object>> rows = client.SearchRead(RegistryModel, new object[] { new object[] { "module", "=", RegistryModule }, new object[] { "name", "=", name } }, new[] { "id", "module", "name", "model", "res_id" }, 2, null);
            if (rows.Count > 1) throw new ConsultantException("demo_registry_collision", "La identidad técnica derivada no es única.");
            if (rows.Count == 1)
            {
                string currentModel = Convert.ToString(rows[0]["model"], CultureInfo.InvariantCulture); long currentId = Convert.ToInt64(rows[0]["res_id"], CultureInfo.InvariantCulture);
                if (currentModel != model || currentId != recordId) throw new ConsultantException("demo_registry_collision", "La identidad técnica derivada apunta a otro registro.");
                return new DemoRegistryRecord { registry_id = Convert.ToInt64(rows[0]["id"], CultureInfo.InvariantCulture), record_id = currentId, model = currentModel, module = RegistryModule, name = name };
            }
            if (client.Read(model, new[] { recordId }, new[] { "display_name" }).Count != 1) throw new ConsultantException("demo_derived_record_missing", "El registro derivado no existe o no es visible.");
            long registryId = client.Create(RegistryModel, JsonUtil.Dictionary("module", RegistryModule, "name", name, "model", model, "res_id", recordId, "noupdate", true));
            return new DemoRegistryRecord { registry_id = registryId, record_id = recordId, model = model, module = RegistryModule, name = name };
        }

        public static string TechnicalName(DemoScenario scenario, string componentKey)
        {
            string client = Sanitize(scenario == null ? string.Empty : scenario.client_slug);
            string component = Sanitize(componentKey);
            string value = client + "_d" + (scenario == null ? 0 : scenario.demo_number).ToString(CultureInfo.InvariantCulture) + "_" + component;
            if (value.Length <= 110) return value;
            return value.Substring(0, 93) + "_" + JsonUtil.Sha256(value).Substring(0, 16);
        }

        private static string Sanitize(string value)
        {
            string normalized = Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), "[^a-z0-9_]+", "_").Trim('_');
            return string.IsNullOrWhiteSpace(normalized) ? "item" : normalized;
        }
    }
}
