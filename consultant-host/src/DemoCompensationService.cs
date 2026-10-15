using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OdooConsultor
{
    public sealed class DemoCompensationService
    {
        public const string PolicyVersion = "demo-compensation/1";
        private readonly ProjectStore project;
        public DemoCompensationService(ProjectStore store) { project = store; }

        public object Prepare(string scenarioId)
        {
            DemoScenario scenario = JsonUtil.ReadJsonFile<DemoScenario>(project.DemoScenarioPath(scenarioId)); DemoService.EnsureIntegrityForService(scenario);
            if (scenario.environment != "staging") throw new ConsultantException("production_write_blocked", "Producción se bloquea antes de cualquier llamada de red.");
            if (scenario.server_series != "17.0" && scenario.server_series != "18.0" && scenario.server_series != "19.0") throw new ConsultantException("demo_compensation_version_not_supported", "La compensación cerrada está habilitada para Odoo 17, 18 y 19.");
            if (string.IsNullOrWhiteSpace(scenario.protected_records)) throw new ConsultantException("demo_records_not_protected", "El escenario no contiene registros protegidos.");
            Dictionary<string, object> protectedRecords = ProtectedSnapshot.Unprotect(scenario.protected_records);
            ConnectionProfile profile = project.GetProfile("staging", true);
            List<DemoCompensationItem> items = new List<DemoCompensationItem>(); List<string> warnings = new List<string>();
            using (OdooClient client = Open(profile))
            {
                foreach (DemoComponent component in (scenario.components ?? new List<DemoComponent>()).Reverse<DemoComponent>())
                {
                    object raw; Dictionary<string, object> entry;
                    if (!protectedRecords.TryGetValue(component.key, out raw) || (entry = raw as Dictionary<string, object>) == null || !JsonUtil.GetBool(entry, "created_by_scenario", false)) continue;
                    long id = JsonUtil.GetLong(entry, "id", true); DemoAdapterContract contract = DemoAdapterCatalog.Get(component.adapter);
                    Dictionary<string, object> fields = client.FieldsGet(contract.model, new[] { "type", "readonly" });
                    string[] snapshotFields = new[] { "active", "state", "public", "res_model", "res_id", "is_published" }.Where(fields.ContainsKey).ToArray();
                    IList<Dictionary<string, object>> rows = client.Read(contract.model, new[] { id }, snapshotFields.Length == 0 ? new[] { "display_name" } : snapshotFields);
                    if (rows.Count != 1) { warnings.Add(component.key + ": registro no disponible; no se compensará."); continue; }
                    string operation = Operation(contract, rows[0]);
                    if (operation == "manual_restore_staging") warnings.Add(component.key + ": Odoo no ofrece una compensación cerrada segura; se requiere restaurar o reconstruir staging.");
                    items.Add(new DemoCompensationItem { component_key = component.key, adapter = component.adapter, model = contract.model, operation = operation, status = operation == "contained_by_parent" || operation == "preserve" ? "not_required" : "prepared", precondition_hash = JsonUtil.CanonicalHash(rows[0]), protected_snapshot = ProtectedSnapshot.Protect(rows[0]), verification = "pending" });
                }
                HashSet<string> componentKeys = new HashSet<string>((scenario.components ?? new List<DemoComponent>()).Select(x => x.key), StringComparer.Ordinal);
                foreach (KeyValuePair<string, object> pair in protectedRecords.Where(x => !componentKeys.Contains(x.Key)).OrderByDescending(x => x.Key, StringComparer.Ordinal))
                {
                    Dictionary<string, object> entry = pair.Value as Dictionary<string, object>;
                    if (entry == null || !JsonUtil.GetBool(entry, "created_by_scenario", false) || !JsonUtil.GetBool(entry, "derived", false)) continue;
                    string model = JsonUtil.GetString(entry, "model", true); long id = JsonUtil.GetLong(entry, "id", true);
                    Dictionary<string, object> fields = client.FieldsGet(model, new[] { "type", "readonly" });
                    string[] snapshotFields = new[] { "active", "state", "public", "res_model", "res_id", "is_published" }.Where(fields.ContainsKey).ToArray();
                    IList<Dictionary<string, object>> rows = client.Read(model, new[] { id }, snapshotFields.Length == 0 ? new[] { "display_name" } : snapshotFields);
                    if (rows.Count != 1) { warnings.Add(pair.Key + ": derivado no disponible; no se compensará."); continue; }
                    string operation = DerivedOperation(model, rows[0], fields);
                    if (operation == "manual_restore_staging") warnings.Add(pair.Key + ": el derivado requiere restaurar o reconstruir staging.");
                    items.Add(new DemoCompensationItem { component_key = pair.Key, adapter = "derived:" + model, model = model, operation = operation, status = operation == "contained_by_parent" || operation == "preserve" ? "not_required" : operation == "manual_restore_staging" ? "manual_restore_required" : "prepared", precondition_hash = JsonUtil.CanonicalHash(rows[0]), protected_snapshot = ProtectedSnapshot.Protect(rows[0]), verification = "pending" });
                }
            }
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DemoCompensationPlan plan = new DemoCompensationPlan { schema_version = 1, id = "cmp-" + Guid.NewGuid().ToString("N"), client_slug = project.Config.client_slug, environment = "staging", scenario_id = scenario.id, demo_slug = scenario.demo_slug, status = items.Any(x => x.status == "prepared") ? "prepared" : "manual_restore_required", created_at = now.ToString("o", CultureInfo.InvariantCulture), expires_at = now.AddMinutes(project.Config.demo_confirmation_minutes).ToString("o", CultureInfo.InvariantCulture), profile_fingerprint = profile.credential_fingerprint, policy_version = PolicyVersion, items = items, warnings = warnings };
            Save(plan); AuditLog.Append(project, "staging", "demo_preparar_compensacion", plan.id, "success", "Compensación preparada; items=" + items.Count.ToString(CultureInfo.InvariantCulture) + ".", 0);
            return Public(plan, "Revisa la compensación. Para ejecutarla confirma exactamente COMPENSAR-DEMO " + plan.id + ".");
        }

        public object Apply(string planId, string confirmation)
        {
            DemoCompensationPlan plan = Load(planId); Validate(plan);
            if (confirmation != "COMPENSAR-DEMO " + plan.id) throw new ConsultantException("confirmation_mismatch", "La confirmación debe ser exactamente COMPENSAR-DEMO " + plan.id + ".");
            DateTimeOffset expires; if (!DateTimeOffset.TryParse(plan.expires_at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expires) || expires < DateTimeOffset.UtcNow) throw new ConsultantException("confirmation_expired", "La compensación venció; prepara una nueva.");
            if (plan.status == "compensated" || plan.status == "compensated_partial") return Public(plan, "La compensación ya fue procesada; no se repitieron acciones.");
            ConnectionProfile profile = project.GetProfile("staging", true); if (profile.credential_fingerprint != plan.profile_fingerprint) throw new ConsultantException("demo_profile_changed", "La credencial cambió desde la vista previa.");
            DemoScenario scenario = JsonUtil.ReadJsonFile<DemoScenario>(project.DemoScenarioPath(plan.scenario_id)); DemoService.EnsureIntegrityForService(scenario);
            Dictionary<string, object> protectedRecords = ProtectedSnapshot.Unprotect(scenario.protected_records);
            using (OdooClient client = Open(profile))
            {
                foreach (DemoCompensationItem item in plan.items.Where(x => x.status == "prepared"))
                {
                    Dictionary<string, object> entry = protectedRecords[item.component_key] as Dictionary<string, object>; long id = JsonUtil.GetLong(entry, "id", true);
                    try
                    {
                        if (item.operation == "deactivate") client.Write(item.model, new[] { id }, JsonUtil.Dictionary("active", false));
                        else if (item.operation == "unpublish_deactivate") client.Write(item.model, new[] { id }, JsonUtil.Dictionary("is_published", false, "active", false));
                        else if (item.operation == "detach_unpublish") client.Write(item.model, new[] { id }, JsonUtil.Dictionary("public", false, "res_model", false, "res_id", 0));
                        else if (item.operation.StartsWith("cancel_", StringComparison.Ordinal)) client.ExecuteDemoCompensationAction(item.operation, id);
                        else { item.status = "manual_restore_required"; item.verification = "no_safe_closed_compensation"; Save(plan); continue; }
                        item.status = "compensated"; item.verification = "operation_completed"; Save(plan);
                    }
                    catch (ConsultantException) { item.status = "manual_restore_required"; item.verification = "odoo_rejected_safe_compensation"; Save(plan); }
                }
            }
            plan.applied_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture); plan.status = plan.items.Any(x => x.status == "manual_restore_required") ? "compensated_partial" : "compensated"; Save(plan);
            AuditLog.Append(project, "staging", "demo_ejecutar_compensacion", plan.id, plan.status, "Compensación cerrada procesada; no unlink; no external effects.", 0);
            return Public(plan, plan.status == "compensated" ? "Compensación aplicada sin eliminar registros." : "Se aplicó la parte segura; los elementos señalados requieren restaurar o reconstruir staging.");
        }

        private static string Operation(DemoAdapterContract contract, IDictionary<string, object> row)
        {
            if (contract.cleanup_strategy == "deactivate") return "deactivate";
            if (contract.cleanup_strategy == "unpublish_and_deactivate") return "unpublish_deactivate";
            if (contract.cleanup_strategy == "detach_and_unpublish") return "detach_unpublish";
            if (contract.cleanup_strategy == "contained_by_parent") return "contained_by_parent";
            string state = Text(row, "state");
            if (contract.model == "sale.order" && state != "cancel") return "cancel_sale";
            if (contract.model == "purchase.order" && state != "cancel") return "cancel_purchase";
            if (contract.model == "stock.picking" && state != "done" && state != "cancel") return "cancel_picking";
            if (contract.model == "account.move" && state == "draft") return "cancel_move";
            if (contract.model == "account.payment" && state != "cancel") return "cancel_payment";
            if (contract.cleanup_strategy == "preserve") return "preserve";
            return "manual_restore_staging";
        }
        private static string DerivedOperation(string model, IDictionary<string, object> row, IDictionary<string, object> fields)
        {
            string state = Text(row, "state");
            if ((model == "project.project" || model == "project.task") && fields.ContainsKey("active")) return "deactivate";
            if (model == "stock.picking") return state != "done" && state != "cancel" ? "cancel_picking" : state == "cancel" ? "preserve" : "manual_restore_staging";
            if (model == "purchase.order") return state != "done" && state != "cancel" ? "cancel_purchase" : state == "cancel" ? "preserve" : "manual_restore_staging";
            if (model == "account.move") return state == "draft" ? "cancel_move" : state == "cancel" ? "preserve" : "manual_restore_staging";
            if (model == "account.payment") return state == "draft" ? "cancel_payment" : state == "cancel" ? "preserve" : "manual_restore_staging";
            if (model == "account.loan.line" || model == "account.move.line") return "contained_by_parent";
            return "manual_restore_staging";
        }
        private static string Text(IDictionary<string, object> row, string key) { object value; return row.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null; }
        private OdooClient Open(ConnectionProfile profile) { return ProjectStore.OpenClient(profile, 120000); }
        private void Save(DemoCompensationPlan plan) { plan.integrity_hash = Integrity(plan); JsonUtil.WriteJsonFile(project.DemoCompensationPlanPath(plan.id), plan); }
        private DemoCompensationPlan Load(string id) { return JsonUtil.ReadJsonFile<DemoCompensationPlan>(project.DemoCompensationPlanPath(id)); }
        private static string Integrity(DemoCompensationPlan plan) { Dictionary<string, object> value = JsonUtil.DeserializeObject(JsonUtil.Serialize(plan)); value.Remove("integrity_hash"); return JsonUtil.CanonicalHash(value); }
        private void Validate(DemoCompensationPlan plan) { if (plan == null || plan.schema_version != 1 || plan.client_slug != project.Config.client_slug || plan.environment != "staging" || plan.policy_version != PolicyVersion || plan.integrity_hash != Integrity(plan)) throw new ConsultantException("demo_compensation_integrity_failed", "El plan de compensación no es válido o fue modificado."); }
        private static object Public(DemoCompensationPlan plan, string message) { return JsonUtil.Dictionary("schema_version", plan.schema_version, "id", plan.id, "scenario_id", plan.scenario_id, "demo_slug", plan.demo_slug, "status", plan.status, "expires_at", plan.expires_at, "confirmation", "COMPENSAR-DEMO " + plan.id, "items", plan.items.Select(x => JsonUtil.Dictionary("component_key", x.component_key, "adapter", x.adapter, "model", x.model, "operation", x.operation, "status", x.status, "verification", x.verification)).ToArray(), "warnings", plan.warnings, "unlink", false, "external_effects", false, "message", message); }
    }
}
