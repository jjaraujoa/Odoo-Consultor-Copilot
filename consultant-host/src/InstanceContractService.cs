using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace OdooConsultor
{
    public sealed class InstanceContractService
    {
        public const string PolicyVersion = "instance-contract-lock/1";
        private readonly ProjectStore project;

        private sealed class ModelContract
        {
            public string family;
            public string module;
            public string model;
            public string[] fields;
            public bool required;
        }

        private static readonly ModelContract[] Models = new[]
        {
            Model("crm_sales", "crm", "crm.lead", true, "name", "type", "partner_id", "lost_reason_id", "active", "probability", "stage_id"),
            Model("crm_sales", "crm", "crm.lead2opportunity.partner", true, "name", "action", "lead_id", "partner_id", "user_id", "team_id", "force_assignment"),
            Model("crm_sales", "crm", "crm.lead.lost", true, "lead_ids", "lost_reason_id", "lost_feedback"),
            Model("crm_sales", "mail", "mail.activity", true, "res_model_id", "res_id", "activity_type_id", "user_id", "date_deadline", "state"),
            Model("crm_sales", "sale_management", "sale.order", true, "partner_id", "state", "invoice_status", "invoice_ids", "picking_ids", "project_ids", "opportunity_id", "pricelist_id", "fiscal_position_id", "carrier_id", "project_account_id"),
            Model("crm_sales", "sale", "sale.advance.payment.inv", true, "advance_payment_method", "amount", "fixed_amount", "sale_order_ids"),
            Model("crm_sales", "sale_commission", "sale.commission.plan", true, "name", "active", "state", "company_id", "type", "periodicity", "date_from", "date_to", "commission_amount", "achievement_ids", "target_ids", "user_ids"),
            Model("crm_sales", "sale_commission", "sale.commission.plan.target", true, "plan_id", "date_from", "date_to", "amount", "payment_date"),
            Model("crm_sales", "sale_commission", "sale.commission.plan.target.commission", true, "plan_id", "target_rate", "amount_rate", "amount"),
            Model("crm_sales", "sale_commission", "sale.commission.plan.achievement", true, "plan_id", "type", "product_id", "product_categ_id", "rate"),
            Model("crm_sales", "sale_commission", "sale.commission.plan.user", true, "plan_id", "user_id", "date_from", "date_to"),
            Model("purchase_inventory", "purchase", "purchase.order", true, "partner_id", "state", "requisition_id", "picking_ids", "invoice_ids", "invoice_status"),
            Model("purchase_inventory", "purchase_requisition", "purchase.requisition", true, "name", "reference", "state", "purchase_ids", "requisition_type", "date_start", "date_end"),
            Model("purchase_inventory", "stock", "stock.picking", true, "state", "backorder_ids", "backorder_id", "move_ids", "purchase_id", "sale_id", "picking_type_id", "location_id", "location_dest_id"),
            Model("purchase_inventory", "stock", "stock.warehouse", true, "name", "code", "company_id", "route_ids", "buy_to_resupply", "reception_steps", "delivery_steps"),
            Model("purchase_inventory", "stock", "stock.route", true, "name", "active", "rule_ids", "sale_selectable", "product_selectable", "warehouse_selectable"),
            Model("purchase_inventory", "stock_dropshipping", "stock.rule", true, "name", "action", "route_id", "location_src_id", "location_dest_id", "picking_type_id"),
            Model("purchase_inventory", "stock_landed_costs", "stock.landed.cost", true, "state", "picking_ids", "vendor_bill_id", "valuation_adjustment_lines", "amount_total"),
            Model("accounting_finance", "account", "account.move", true, "name", "move_type", "state", "payment_state", "amount_total", "amount_residual", "reversed_entry_id", "reversal_move_ids", "invoice_origin", "line_ids"),
            Model("accounting_finance", "account", "account.payment", true, "name", "state", "is_reconciled", "amount", "partner_id", "journal_id", "reconciled_invoice_ids", "reconciled_bill_ids"),
            Model("accounting_finance", "account", "account.bank.statement.line", true, "payment_ref", "amount", "is_reconciled", "move_id", "journal_id"),
            Model("accounting_finance", "account", "account.move.reversal", true, "date", "reason", "journal_id", "move_ids"),
            Model("accounting_finance", "account_debit_note", "account.debit.note", true, "date", "reason", "journal_id", "move_ids", "copy_lines"),
            Model("accounting_finance", "account", "account.payment.register", true, "amount", "payment_date", "journal_id", "payment_method_line_id", "group_payment", "line_ids"),
            Model("accounting_finance", "account_budget", "budget.analytic", true, "name", "state", "user_id", "company_id", "date_from", "date_to", "budget_type", "budget_line_ids"),
            Model("accounting_finance", "account_budget", "budget.line", true, "budget_analytic_id", "account_id", "auto_account_id", "budget_amount", "sequence"),
            Model("accounting_finance", "account_asset", "account.asset", true, "name", "active", "state", "company_id", "model_id", "method", "method_number", "method_period", "original_value", "salvage_value", "acquisition_date", "journal_id", "depreciation_move_ids"),
            Model("accounting_finance", "account_loans", "account.loan", true, "name", "active", "state", "company_id", "date", "amount_borrowed", "interest", "duration", "skip_until_date", "long_term_account_id", "short_term_account_id", "expense_account_id", "journal_id", "asset_group_id", "line_ids"),
            Model("accounting_finance", "account_loans", "account.loan.line", true, "loan_id", "sequence", "date", "principal", "interest"),
            Model("configuration", "account", "account.fiscal.position", true, "name", "active", "company_id", "sequence", "tax_ids", "account_ids"),
            Model("configuration", "account", "account.fiscal.position.account", true, "position_id", "account_src_id", "account_dest_id"),
            Model("configuration", "account", "account.tax", true, "name", "amount", "amount_type", "type_tax_use", "company_id", "country_id", "original_tax_ids", "fiscal_position_ids", "active"),
            Model("configuration", "fleet", "fleet.vehicle.model.brand", true, "name", "active"),
            Model("configuration", "fleet", "fleet.vehicle.model", true, "name", "brand_id", "active"),
            Model("configuration", "crm", "crm.lost.reason", true, "name", "active"),
            Model("configuration", "base", "ir.attachment", true, "name", "type", "datas", "mimetype", "public", "res_model", "res_id")
        };

        private static readonly Dictionary<string, string> Methods = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "crm.win", "crm.lead/action_set_won" }, { "crm.lose", "crm.lead/action_set_lost" }, { "crm.convert", "crm.lead2opportunity.partner/action_apply" }, { "crm.lose_reason", "crm.lead.lost/action_lost_reason_apply" }, { "crm.activity_done", "mail.activity/action_done" },
            { "sale.confirm", "sale.order/action_confirm" }, { "sale.down_payment", "sale.advance.payment.inv/create_invoices" },
            { "commission.approve", "sale.commission.plan/action_approve" },
            { "purchase.agreement_confirm", "purchase.requisition/action_confirm" }, { "purchase.confirm", "purchase.order/button_confirm" },
            { "purchase.cancel", "purchase.order/button_cancel" }, { "purchase.vendor_bill", "purchase.order/action_create_invoice" },
            { "stock.reserve", "stock.picking/action_assign" }, { "stock.validate", "stock.picking/button_validate" },
            { "landed_cost.compute", "stock.landed.cost/compute_landed_cost" }, { "landed_cost.validate", "stock.landed.cost/button_validate" },
            { "account.post_move", "account.move/action_post" }, { "account.post_payment", "account.payment/action_post" },
            { "account.reverse", "account.move.reversal/reverse_moves" }, { "account.debit_note", "account.debit.note/create_debit" },
            { "account.register_payment", "account.payment.register/action_create_payments" }, { "account.reconcile", "account.move.line/reconcile" },
            { "budget.confirm", "budget.analytic/action_confirm" }, { "asset.compute", "account.asset/compute_depreciation_board" },
            { "asset.validate", "account.asset/validate" }, { "loan.compute", "account.loan/action_compute" }, { "loan.validate", "account.loan/action_validate" }
        };

        public InstanceContractService(ProjectStore store) { project = store; }

        public object Inventory(string environment, string demoSlug)
        {
            environment = ProjectStore.NormalizeEnvironment(environment);
            ConnectionProfile profile = project.GetProfile(environment, true);
            bool supported = (profile.server_series == "17.0" || profile.server_series == "18.0" || profile.server_series == "19.0")
                && (project.Config.odoo_version == "17.0" || project.Config.odoo_version == "18.0" || project.Config.odoo_version == "19.0");
            if (!supported)
                throw new ConsultantException("instance_contract_version_not_supported", "La cobertura total de flujos está habilitada para Odoo 17, 18 y 19.");
            string directory = project.DemoDirectory(demoSlug, true);
            string sourcePath = Path.Combine(directory, "source-lock.json");
            string sourceHash = File.Exists(sourcePath) ? JsonUtil.Sha256(File.ReadAllText(sourcePath)) : null;
            List<object> modules = new List<object>();
            List<object> models = new List<object>();
            Dictionary<string, bool> moduleInstalled = new Dictionary<string, bool>(StringComparer.Ordinal);
            Dictionary<string, bool> modelAvailable = new Dictionary<string, bool>(StringComparer.Ordinal);
            Dictionary<string, bool> familyReady = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (string family in Models.Select(x => x.family).Distinct(StringComparer.Ordinal)) familyReady[family] = true;

            using (OdooClient client = Open(profile))
            {
                foreach (string module in Models.Select(x => x.module).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
                {
                    IList<Dictionary<string, object>> rows = client.SearchRead("ir.module.module", new object[] { new object[] { "name", "=", module } }, new[] { "name", "state", "installed_version" }, 2, null);
                    string state = rows.Count == 1 ? Text(rows[0], "state") : "not_found";
                    moduleInstalled[module] = state == "installed";
                    modules.Add(JsonUtil.Dictionary("name", module, "state", state, "version", rows.Count == 1 ? Text(rows[0], "installed_version") : null));
                }
                foreach (ModelContract contract in Models)
                {
                    bool installed = moduleInstalled.ContainsKey(contract.module) && moduleInstalled[contract.module];
                    Dictionary<string, object> allFields = null;
                    Dictionary<string, object> selected = new Dictionary<string, object>(StringComparer.Ordinal);
                    bool read = false, create = false, write = false;
                    string error = null;
                    if (installed)
                    {
                        try
                        {
                            allFields = client.FieldsGet(contract.model, new[] { "string", "type", "required", "readonly", "store", "relation", "selection" });
                            foreach (string field in contract.fields)
                            {
                                object descriptor;
                                if (allFields.TryGetValue(field, out descriptor)) selected[field] = descriptor;
                            }
                            read = client.CheckAccessRights(contract.model, "read");
                            create = client.CheckAccessRights(contract.model, "create");
                            write = client.CheckAccessRights(contract.model, "write");
                        }
                        catch (ConsultantException problem) { error = problem.Code; }
                    }
                    bool available = installed && allFields != null && contract.fields.All(selected.ContainsKey) && read;
                    modelAvailable[contract.model] = available;
                    if (contract.required && (!available || !create || !write)) familyReady[contract.family] = false;
                    models.Add(JsonUtil.Dictionary("family", contract.family, "module", contract.module, "model", contract.model, "required", contract.required,
                        "available", available, "read", read, "create", create, "write", write, "selected_fields", selected,
                        "field_metadata_hash", allFields == null ? null : JsonUtil.CanonicalHash(allFields), "error", error));
                }
                Dictionary<string, object> documented = client.ProbeDocumentedMethods(Methods);
                Dictionary<string, object> methodStates = documented["methods"] as Dictionary<string, object> ?? new Dictionary<string, object>(StringComparer.Ordinal);
                Dictionary<string, object> familyStates = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (string family in familyReady.Keys.OrderBy(x => x, StringComparer.Ordinal))
                {
                    string[] familyMethodKeys = Methods.Keys.Where(x => MethodFamily(x) == family).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                    string[] missingMethods = familyMethodKeys.Where(x => !methodStates.ContainsKey(x) || !Convert.ToBoolean(methodStates[x], CultureInfo.InvariantCulture)).ToArray();
                    familyStates[family] = JsonUtil.Dictionary(
                        "ready", familyReady[family],
                        "method_contract_status", missingMethods.Length == 0 ? "documented_by_instance" : "closed_pending_real_validation",
                        "missing_dynamic_documentation", missingMethods,
                        "methods_block_preparation", false);
                }
                Dictionary<string, object> body = JsonUtil.Dictionary(
                    "schema_version", 1, "policy_version", PolicyVersion, "client_slug", project.Config.client_slug, "environment", environment,
                    "server_series", profile.server_series, "edition_declared", project.Config.odoo_edition, "hosting_declared", project.Config.hosting,
                    "captured_at", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture), "profile_fingerprint", profile.credential_fingerprint,
                    "enterprise_source_required", false, "enterprise_source_processed", false, "client_source_processed", false,
                    "evidence_order", new[] { "verificado_base", "oficial_documentacion", "oficial_codigo_community" },
                    "source_lock_present", sourceHash != null, "source_lock_file_hash", sourceHash,
                    "modules", modules.ToArray(), "models", models.ToArray(), "documented_methods", documented,
                    "families", familyStates,
                    "external_integrations", JsonUtil.Dictionary("dian", false, "bank_connection", false, "bank_file_import", false, "email_send", false, "whatsapp", false, "sms", false, "carrier_api", false),
                    "production_write", false);
                body["integrity_hash"] = JsonUtil.CanonicalHash(body);
                JsonUtil.WriteJsonFile(Path.Combine(directory, "instance-contract-lock.json"), body);
                bool ready = environment == "staging" && sourceHash != null && familyReady.Values.All(x => x);
                bool fullyValidated = ready && methodStates.Values.All(x => Convert.ToBoolean(x, CultureInfo.InvariantCulture));
                AuditLog.Append(project, environment, "demo_inventariar_cobertura_total", null, "success", "Contrato de instancia capturado; ready=" + ready.ToString(CultureInfo.InvariantCulture).ToLowerInvariant() + "; models=" + models.Count.ToString(CultureInfo.InvariantCulture) + ".", 0);
                return JsonUtil.Dictionary("schema_version", 1, "policy_version", PolicyVersion, "environment", environment, "demo_slug", demoSlug,
                    "ready", ready, "fully_validated", fullyValidated, "families", body["families"], "source_lock_present", sourceHash != null, "enterprise_source_required", false,
                    "contract_file", "demostraciones/" + demoSlug + "/instance-contract-lock.json", "contract_hash", body["integrity_hash"],
                    "external_integrations_enabled", false,
                    "message", ready ? (fullyValidated ? "La base documenta los contratos cerrados requeridos para preparar flujos Odoo 19." : "Los modelos, campos y permisos permiten preparar flujos. Los métodos que la página /doc no publica quedan señalados para validación real paso a paso; no se procesó código Enterprise.") : "La cobertura continúa bloqueada donde falten modelos, campos, permisos o source-lock; no se infirieron contratos de escritura.");
            }
        }

        public Dictionary<string, object> Load(string demoSlug)
        {
            string path = Path.Combine(project.DemoDirectory(demoSlug, false), "instance-contract-lock.json");
            if (!File.Exists(path)) throw new ConsultantException("instance_contract_missing", "Primero ejecuta demo_inventariar_cobertura_total para esta demostración.");
            Dictionary<string, object> value = JsonUtil.ReadJsonFile<Dictionary<string, object>>(path);
            object stored;
            if (!value.TryGetValue("integrity_hash", out stored)) throw new ConsultantException("instance_contract_integrity_failed", "El contrato de instancia no contiene huella de integridad.");
            string expected = Convert.ToString(stored, CultureInfo.InvariantCulture);
            value.Remove("integrity_hash");
            string actual = JsonUtil.CanonicalHash(value);
            value["integrity_hash"] = expected;
            if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new ConsultantException("instance_contract_integrity_failed", "El contrato de instancia fue modificado fuera del motor.");
            return value;
        }

        internal static bool ContainsModelForSelfTest(string model) { return Models.Any(x => x.model == model); }
        internal static string ModuleForSelfTest(string model) { ModelContract contract = Models.FirstOrDefault(x => x.model == model); return contract == null ? null : contract.module; }
        internal static bool ContainsMethodForSelfTest(string key) { return Methods.ContainsKey(key); }
        internal static string MethodPathForSelfTest(string key) { return Methods[key]; }

        private static ModelContract Model(string family, string module, string model, bool required, params string[] fields)
        {
            return new ModelContract { family = family, module = module, model = model, required = required, fields = fields };
        }
        private static string MethodFamily(string key)
        {
            if (key.StartsWith("crm.", StringComparison.Ordinal) || key.StartsWith("sale.", StringComparison.Ordinal) || key.StartsWith("commission.", StringComparison.Ordinal)) return "crm_sales";
            if (key.StartsWith("purchase.", StringComparison.Ordinal) || key.StartsWith("stock.", StringComparison.Ordinal) || key.StartsWith("landed_cost.", StringComparison.Ordinal)) return "purchase_inventory";
            return "accounting_finance";
        }
        private OdooClient Open(ConnectionProfile profile)
        {
            return ProjectStore.OpenClient(profile, 120000);
        }
        private static string Text(IDictionary<string, object> row, string key)
        {
            object value;
            return row != null && row.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }
    }
}
