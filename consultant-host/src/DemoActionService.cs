using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OdooConsultor
{
    public sealed class DemoActionService
    {
        public const string PolicyVersion = "demo-semantic-actions/1";
        public const string FlowPolicyVersion = "demo-functional-flow/2";
        private readonly ProjectStore project;

        private sealed class Contract
        {
            public string action;
            public string model;
            public string[] adapters;
            public string[] fields;
            public string expected;
            public string block;
            public string[] parameterKeys;
            public bool odoo19Only;
        }

        private static readonly Dictionary<string, Contract> Contracts = new Dictionary<string, Contract>(StringComparer.Ordinal)
        {
            { "confirmar_venta", ContractOf("confirmar_venta", "sale.order", "sale", new[] { "state", "picking_ids", "project_ids", "invoice_ids" }, "sale_quotation") },
            { "confirmar_compra", ContractOf("confirmar_compra", "purchase.order", "purchase", new[] { "state", "picking_ids", "invoice_ids" }, "purchase_rfq") },
            { "confirmar_acuerdo_compra", ContractOf("confirmar_acuerdo_compra", "purchase.requisition", "confirmed", new[] { "state", "purchase_ids" }, "purchase_agreement") },
            { "ganar_oportunidad", ContractOf("ganar_oportunidad", "crm.lead", "won", new[] { "active", "probability" }, "crm_opportunity") },
            { "perder_oportunidad", ContractOf("perder_oportunidad", "crm.lead", "lost", new[] { "active", "probability" }, "crm_opportunity") },
            { "reservar_transferencia", ContractOf("reservar_transferencia", "stock.picking", "assigned", new[] { "state" }, "stock_picking_draft") },
            { "validar_transferencia", ContractOf("validar_transferencia", "stock.picking", "done", new[] { "state", "backorder_ids" }, "stock_picking_draft") },
            { "contabilizar_movimiento", ContractOf("contabilizar_movimiento", "account.move", "posted", new[] { "state" }, "account_invoice_draft", "account_journal_entry_draft") }
            ,{ "contabilizar_pago", ContractOf("contabilizar_pago", "account.payment", "in_process_or_paid", new[] { "state" }, "account_payment_draft") }
            ,{ "aplicar_inventario", ContractOf("aplicar_inventario", "stock.quant", "inventory_applied", new[] { "quantity", "inventory_quantity_set" }, "stock_inventory_quant") }
            ,{ "calcular_costo_destino", ContractOf("calcular_costo_destino", "stock.landed.cost", "computed", new[] { "state", "valuation_adjustment_lines" }, "stock_landed_cost") }
            ,{ "validar_costo_destino", ContractOf("validar_costo_destino", "stock.landed.cost", "done", new[] { "state" }, "stock_landed_cost") }
            ,{ "completar_actividad", Advanced("completar_actividad", "mail.activity", "activity_completed", new[] { "id" }, "crm_sales", new string[0], "crm_activity") }
            ,{ "convertir_iniciativa", Advanced("convertir_iniciativa", "crm.lead", "converted", new[] { "type", "partner_id", "active" }, "crm_sales", new[] { "partner_id", "user_id", "team_id" }, "crm_opportunity") }
            ,{ "perder_oportunidad_con_motivo", Advanced("perder_oportunidad_con_motivo", "crm.lead", "lost_with_reason", new[] { "active", "probability", "lost_reason_id" }, "crm_sales", new[] { "lost_reason_id" }, "crm_opportunity") }
            ,{ "aprobar_comisiones", Advanced("aprobar_comisiones", "sale.commission.plan", "approved", new[] { "state" }, "crm_sales", new string[0], "commission_plan") }
            ,{ "crear_anticipo_venta", Advanced("crear_anticipo_venta", "sale.order", "invoice_created", new[] { "state", "invoice_ids" }, "crm_sales", new[] { "percentage" }, "sale_quotation") }
            ,{ "crear_factura_final_venta", Advanced("crear_factura_final_venta", "sale.order", "invoice_created", new[] { "state", "invoice_ids" }, "crm_sales", new string[0], "sale_quotation") }
            ,{ "cancelar_compra", Advanced("cancelar_compra", "purchase.order", "cancel", new[] { "state" }, "purchase_inventory", new string[0], "purchase_rfq") }
            ,{ "crear_factura_proveedor_compra", Advanced("crear_factura_proveedor_compra", "purchase.order", "vendor_bill_created", new[] { "state", "invoice_ids" }, "purchase_inventory", new string[0], "purchase_rfq") }
            ,{ "crear_nota_credito", Advanced("crear_nota_credito", "account.move", "reversal_created", new[] { "state", "reversal_move_ids" }, "accounting_finance", new[] { "reason", "date", "journal_id" }, "account_invoice_draft") }
            ,{ "crear_nota_debito", Advanced("crear_nota_debito", "account.move", "debit_created", new[] { "state", "debit_note_ids" }, "accounting_finance", new[] { "reason", "date", "journal_id" }, "account_invoice_draft") }
            ,{ "registrar_pago_factura", Advanced("registrar_pago_factura", "account.move", "payment_registered", new[] { "state", "payment_state", "amount_residual", "payment_ids" }, "accounting_finance", new[] { "amount", "payment_date", "journal_id", "payment_method_line_id" }, "account_invoice_draft") }
            ,{ "reconciliar_apuntes", Advanced("reconciliar_apuntes", "account.move.line", "reconciled", new[] { "reconciled" }, "accounting_finance", new string[0], "account_journal_line", "account_invoice_line") }
            ,{ "confirmar_presupuesto", Advanced("confirmar_presupuesto", "budget.analytic", "confirmed", new[] { "state" }, "accounting_finance", new string[0], "analytic_budget") }
            ,{ "calcular_activo", Advanced("calcular_activo", "account.asset", "depreciation_computed", new[] { "state", "depreciation_move_ids" }, "accounting_finance", new string[0], "account_asset_demo") }
            ,{ "validar_activo", Advanced("validar_activo", "account.asset", "running", new[] { "state" }, "accounting_finance", new string[0], "account_asset_demo") }
            ,{ "calcular_prestamo", Advanced("calcular_prestamo", "account.loan", "schedule_computed", new[] { "state", "line_ids" }, "accounting_finance", new string[0], "account_loan_demo") }
            ,{ "activar_prestamo", Advanced("activar_prestamo", "account.loan", "running", new[] { "state" }, "accounting_finance", new string[0], "account_loan_demo") }
        };

        public DemoActionService(ProjectStore store) { project = store; }

        public object Prepare(string scenarioId, IList<object> requested) { return PrepareCore(scenarioId, requested, false); }

        private object PrepareCore(string scenarioId, IList<object> requested, bool advancedAllowed)
        {
            DemoScenario scenario = LoadScenario(scenarioId);
            ValidateScenario(scenario);
            if (requested == null || requested.Count == 0 || requested.Count > 30) throw new ConsultantException("invalid_demo_actions", "El plan debe contener entre 1 y 30 acciones.");
            ConnectionProfile profile = project.GetProfile("staging", true);
            if (!string.Equals(profile.credential_fingerprint, scenario.profile_fingerprint, StringComparison.Ordinal)) throw new ConsultantException("demo_profile_changed", "La credencial cambió desde la preparación del escenario.");
            Dictionary<string, object> protectedRecords = ProtectedSnapshot.Unprotect(scenario.protected_records) ?? new Dictionary<string, object>(StringComparer.Ordinal);
            List<DemoActionItem> actions = new List<DemoActionItem>();
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            using (OdooClient client = Open(profile, 30000))
            {
                foreach (object raw in requested)
                {
                    IDictionary<string, object> input = raw as IDictionary<string, object>;
                    if (input == null) throw new ConsultantException("invalid_demo_action", "Cada acción debe ser un objeto declarativo.");
                    string key = JsonUtil.GetString(input, "clave", true);
                    if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-z][a-z0-9_]{1,62}$") || !keys.Add(key)) throw new ConsultantException("invalid_demo_action_key", "Las claves de acciones deben ser únicas y usar formato técnico seguro.");
                    string action = JsonUtil.GetString(input, "accion", true);
                    Contract contract;
                    if (!Contracts.TryGetValue(action, out contract)) throw new ConsultantException("demo_action_not_allowed", "La acción semántica no pertenece al catálogo cerrado.");
                    if (contract.odoo19Only && !advancedAllowed) throw new ConsultantException("demo_flow_required", "La acción avanzada debe prepararse mediante demo_preparar_flujo y un contrato de instancia vigente.");
                    if (contract.odoo19Only && profile.server_series != "17.0" && profile.server_series != "18.0" && profile.server_series != "19.0") throw new ConsultantException("demo_action_version_not_supported", "Las acciones avanzadas están habilitadas para Odoo 17, 18 y 19.");
                    string componentKey = JsonUtil.GetString(input, "componente", true);
                    DemoComponent component = (scenario.components ?? new List<DemoComponent>()).FirstOrDefault(x => x.key == componentKey);
                    if (component == null || !contract.adapters.Contains(component.adapter, StringComparer.Ordinal)) throw new ConsultantException("demo_action_adapter_mismatch", "La acción no corresponde al tipo del componente protegido.");
                    List<string> relatedComponentKeys = ToStringList(input.ContainsKey("componentes_relacionados") ? input["componentes_relacionados"] : null).Distinct(StringComparer.Ordinal).ToList();
                    if (action == "reconciliar_apuntes")
                    {
                        if (relatedComponentKeys.Count == 0 || relatedComponentKeys.Count > 19 || relatedComponentKeys.Contains(componentKey, StringComparer.Ordinal)) throw new ConsultantException("demo_reconciliation_components_invalid", "La conciliación requiere entre uno y diecinueve apuntes relacionados distintos del componente principal.");
                        foreach (string relatedKey in relatedComponentKeys)
                        {
                            DemoComponent related = (scenario.components ?? new List<DemoComponent>()).FirstOrDefault(x => x.key == relatedKey);
                            if (related == null || !contract.adapters.Contains(related.adapter, StringComparer.Ordinal)) throw new ConsultantException("demo_action_adapter_mismatch", "Todos los componentes de la conciliación deben ser apuntes protegidos del catálogo cerrado.");
                            GetProtectedEntry(protectedRecords, relatedKey, contract.model);
                        }
                    }
                    else if (relatedComponentKeys.Count > 0) throw new ConsultantException("demo_related_components_not_applicable", "Los componentes relacionados solo aplican a reconciliar_apuntes.");
                    List<long> targetIds = ResolveTargetIds(protectedRecords, componentKey, relatedComponentKeys, contract.model);
                    List<Dictionary<string, object>> currentRows = ReadTargets(client, contract, targetIds);
                    bool cancelBackorder = JsonUtil.GetBool(input, "cancelar_backorder", false);
                    bool expectBackorder = JsonUtil.GetBool(input, "esperar_backorder", false);
                    if (action != "validar_transferencia" && (cancelBackorder || expectBackorder)) throw new ConsultantException("demo_action_backorder_not_applicable", "Las opciones de backorder solo aplican a validar_transferencia.");
                    if (cancelBackorder && expectBackorder) throw new ConsultantException("demo_action_backorder_conflict", "No se puede cancelar y esperar un backorder en la misma validación.");
                    bool alreadyExpected = currentRows.All(row => IsExpected(contract, row, expectBackorder));
                    Dictionary<string, object> parameters = input.ContainsKey("parametros") ? input["parametros"] as Dictionary<string, object> : null;
                    ValidateParameters(contract, parameters);
                    ValidateParameterValues(client, parameters);
                    List<string> dependencies = ToStringList(input.ContainsKey("depende_de") ? input["depende_de"] : null);
                    actions.Add(new DemoActionItem
                    {
                        key = key, action = action, component_key = componentKey, related_component_keys = relatedComponentKeys, model = contract.model, status = alreadyExpected ? "verified" : "prepared",
                        expected_state = contract.expected, precondition_hash = StateHash(contract, currentRows),
                        cancel_backorder = cancelBackorder, expect_backorder = expectBackorder, verification = alreadyExpected ? "already_in_expected_state" : "pending",
                        block = contract.block, depends_on = dependencies, protected_parameters = parameters == null || parameters.Count == 0 ? null : ProtectedSnapshot.Protect(parameters)
                    });
                }
            }
            ValidateDependencies(actions);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DemoActionPlan plan = new DemoActionPlan
            {
                schema_version = 1, id = "act-" + Guid.NewGuid().ToString("N"), client_slug = project.Config.client_slug, environment = "staging",
                scenario_id = scenario.id, demo_slug = scenario.demo_slug, status = actions.All(x => x.status == "verified") ? "verified" : "prepared",
                created_at = now.ToString("o", CultureInfo.InvariantCulture), expires_at = now.AddMinutes(project.Config.demo_confirmation_minutes).ToString("o", CultureInfo.InvariantCulture),
                profile_fingerprint = profile.credential_fingerprint, scenario_integrity_hash = scenario.integrity_hash, policy_version = PolicyVersion,
                actions = actions, warnings = new List<string>
                {
                    "Las acciones se ejecutan solo sobre registros sintéticos protegidos por este escenario.",
                    "Una respuesta ambigua no se reintenta: primero se verifica el estado por lectura.",
                    "Validar una transferencia puede requerir un asistente manual si faltan cantidades o decisiones de backorder.",
                    "No hay reversión automática para documentos confirmados o contabilizados; el staging se recupera mediante respaldo o reconstrucción."
                }
            };
            Save(plan);
            AuditLog.Append(project, "staging", "demo_preparar_acciones", plan.id, "success", "Plan semántico preparado; actions=" + actions.Count.ToString(CultureInfo.InvariantCulture) + ".", 0);
            return Public(plan, plan.status == "verified" ? "Los registros ya estaban en los estados esperados; no se ejecutará ninguna acción." : "Confirma exactamente con EJECUTAR-DEMO " + plan.id + ".");
        }

        public object PrepareFlow(string scenarioId, string block, IList<object> requested)
        {
            block = (block ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(block)) block = "general";
            if (block != "general" && block != "transversal" && block != "crm_sales" && block != "purchase_inventory" && block != "accounting_finance") throw new ConsultantException("invalid_demo_flow_block", "El bloque debe ser general, transversal, crm_sales, purchase_inventory o accounting_finance.");
            DemoScenario scenario = LoadScenario(scenarioId); ValidateScenario(scenario);
            if (scenario.server_series != "17.0" && scenario.server_series != "18.0" && scenario.server_series != "19.0") throw new ConsultantException("demo_flow_version_not_supported", "Los flujos schema 2 están habilitados para Odoo 17, 18 y 19.");
            Dictionary<string, object> instance = new InstanceContractService(project).Load(scenario.demo_slug);
            Dictionary<string, object> families = instance.ContainsKey("families") ? instance["families"] as Dictionary<string, object> : null;
            bool isGeneral = block == "general" || block == "transversal";
            if (!isGeneral)
            {
                Dictionary<string, object> family = families != null && families.ContainsKey(block) ? families[block] as Dictionary<string, object> : null;
                if (family == null || !JsonUtil.GetBool(family, "ready", false)) throw new ConsultantException("demo_flow_contract_not_ready", "El contrato real de la instancia no habilita todavía este bloque; revisa módulos, permisos y /doc.");
            }
            Dictionary<string, object> prepared = PrepareCore(scenarioId, requested, true) as Dictionary<string, object>;
            DemoActionPlan plan = Load(JsonUtil.GetString(prepared, "id", true));
            if (!isGeneral && plan.actions.Any(x => x.block != block)) throw new ConsultantException("demo_flow_block_mismatch", "Todas las acciones del flujo deben pertenecer al mismo bloque funcional.");
            if (isGeneral && families != null)
            {
                foreach (DemoActionItem act in plan.actions)
                {
                    if (!string.IsNullOrWhiteSpace(act.block) && families.ContainsKey(act.block))
                    {
                        Dictionary<string, object> fam = families[act.block] as Dictionary<string, object>;
                        if (fam == null || !JsonUtil.GetBool(fam, "ready", false))
                            throw new ConsultantException("demo_flow_contract_not_ready", "El contrato real de la instancia no habilita el bloque " + act.block + " requerido por la acción " + act.action + "; revisa módulos y permisos.");
                    }
                }
            }
            plan.schema_version = 2; plan.flow_block = block; plan.policy_version = FlowPolicyVersion; plan.instance_contract_hash = JsonUtil.GetString(instance, "integrity_hash", true);
            Save(plan);
            AuditLog.Append(project, "staging", "demo_preparar_flujo", plan.id, "success", "Flujo schema 2 preparado; block=" + block + "; actions=" + plan.actions.Count.ToString(CultureInfo.InvariantCulture) + ".", 0);
            return Public(plan, plan.status == "verified" ? "El flujo ya estaba verificado." : "Confirma este flujo exactamente con EJECUTAR-DEMO " + plan.id + ".");
        }

        public object Apply(string planId, string confirmation)
        {
            DemoActionPlan plan = Load(planId); ValidatePlan(plan);
            if (!string.Equals(confirmation, "EJECUTAR-DEMO " + plan.id, StringComparison.Ordinal)) throw new ConsultantException("confirmation_mismatch", "La confirmación debe ser exactamente EJECUTAR-DEMO " + plan.id + ".");
            DateTimeOffset expires;
            if (!DateTimeOffset.TryParse(plan.expires_at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expires) || expires < DateTimeOffset.UtcNow) throw new ConsultantException("confirmation_expired", "La confirmación del plan de acciones venció. Prepara un plan nuevo.");
            if (plan.status == "verified") return Public(plan, "Todas las acciones ya estaban verificadas; no se repitieron.");
            if (plan.status == "verification_required" || plan.status == "manual_wizard_required" || plan.status == "applying") return Verify(plan.id);
            if (plan.status != "prepared" && plan.status != "failed_partial") throw new ConsultantException("demo_action_plan_state_invalid", "El plan no está disponible para ejecutar.");
            DemoScenario scenario = LoadScenario(plan.scenario_id); ValidateScenario(scenario);
            if (!string.Equals(scenario.integrity_hash, plan.scenario_integrity_hash, StringComparison.Ordinal)) throw new ConsultantException("demo_scenario_changed", "El escenario cambió desde la vista previa de acciones.");
            ConnectionProfile profile = project.GetProfile("staging", true);
            if (!string.Equals(profile.credential_fingerprint, plan.profile_fingerprint, StringComparison.Ordinal)) throw new ConsultantException("demo_profile_changed", "La credencial cambió desde la vista previa.");
            Dictionary<string, object> protectedRecords = ProtectedSnapshot.Unprotect(scenario.protected_records) ?? new Dictionary<string, object>(StringComparer.Ordinal);
            plan.status = "applying"; plan.last_error = null; Save(plan);
            using (OdooClient client = Open(profile, 30000))
            {
                foreach (DemoActionItem item in plan.actions)
                {
                    if (item.status == "verified") continue;
                    Contract contract = Contracts[item.action];
                    List<long> ids = ResolveTargetIds(protectedRecords, item.component_key, item.related_component_keys, contract.model);
                    List<Dictionary<string, object>> currentRows = ReadTargets(client, contract, ids);
                    Dictionary<string, object> current = currentRows[0];
                    if (currentRows.All(row => IsExpected(contract, row, item.expect_backorder))) { CaptureDerived(client, scenario, protectedRecords, item, current); plan.scenario_integrity_hash = scenario.integrity_hash; item.status = "verified"; item.verification = "expected_state_confirmed"; Save(plan); continue; }
                    if (!string.Equals(StateHash(contract, currentRows), item.precondition_hash, StringComparison.Ordinal))
                    {
                        bool controlledDependency = plan.schema_version == 2 && (item.depends_on ?? new List<string>()).Count > 0
                            && item.depends_on.All(key => plan.actions.Any(previous => previous.key == key && previous.status == "verified"));
                        if (!controlledDependency)
                        {
                            plan.status = "failed_partial"; item.status = "precondition_changed"; item.verification = "state_changed"; Save(plan);
                            throw new ConsultantException("demo_action_precondition_changed", "El estado del registro cambió desde la vista previa; no se ejecutó la acción pendiente.");
                        }
                        item.precondition_hash = StateHash(contract, currentRows); item.verification = "precondition_rebased_after_verified_dependency"; Save(plan);
                    }
                    item.status = "executing"; Save(plan);
                    try { client.ExecuteDemoSemanticAction(item.action, ids, item.cancel_backorder, string.IsNullOrWhiteSpace(item.protected_parameters) ? null : ProtectedSnapshot.Unprotect(item.protected_parameters)); }
                    catch (Exception error)
                    {
                        item.status = "verification_required"; item.verification = "ambiguous_response"; plan.status = "verification_required";
                        ConsultantException known = error as ConsultantException; plan.last_error = known == null ? "internal_error" : known.Code; Save(plan);
                        throw new ConsultantException("demo_action_verification_required", "Odoo no confirmó de forma concluyente la acción. No se repetirá: usa demo_verificar_acciones con el mismo plan.");
                    }
                    currentRows = ReadTargets(client, contract, ids); current = currentRows[0];
                    if (!currentRows.All(row => IsExpected(contract, row, item.expect_backorder)))
                    {
                        item.status = item.action == "validar_transferencia" ? "manual_wizard_required" : "verification_required";
                        item.verification = "expected_state_not_reached"; plan.status = item.status; Save(plan);
                        return Public(plan, item.status == "manual_wizard_required" ? "Odoo requiere completar manualmente un asistente cerrado de la transferencia; la acción no se repetirá." : "La llamada terminó, pero el estado esperado aún no fue confirmado; verifica antes de reintentar.");
                    }
                    CaptureDerived(client, scenario, protectedRecords, item, current); plan.scenario_integrity_hash = scenario.integrity_hash;
                    item.status = "verified"; item.verification = "expected_state_confirmed"; Save(plan);
                }
            }
            plan.applied_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture); plan.status = "verified"; plan.verified_at = plan.applied_at; Save(plan);
            AuditLog.Append(project, "staging", "demo_ejecutar_acciones", plan.id, "verified", "Acciones semánticas verificadas; actions=" + plan.actions.Count.ToString(CultureInfo.InvariantCulture) + ".", 0);
            return Public(plan, "Acciones ejecutadas y estados verificados por lectura.");
        }

        public object Verify(string planId)
        {
            DemoActionPlan plan = Load(planId); ValidatePlan(plan);
            DemoScenario scenario = LoadScenario(plan.scenario_id); ValidateScenario(scenario);
            Dictionary<string, object> protectedRecords = ProtectedSnapshot.Unprotect(scenario.protected_records) ?? new Dictionary<string, object>(StringComparer.Ordinal);
            bool all = true;
            using (OdooClient client = Open(project.GetProfile("staging", true), 30000))
            {
                foreach (DemoActionItem item in plan.actions)
                {
                    Contract contract = Contracts[item.action];
                    List<long> ids = ResolveTargetIds(protectedRecords, item.component_key, item.related_component_keys, contract.model);
                    List<Dictionary<string, object>> currentRows = ReadTargets(client, contract, ids); Dictionary<string, object> current = currentRows[0];
                    bool expected = currentRows.All(row => IsExpected(contract, row, item.expect_backorder));
                    if (expected) { CaptureDerived(client, scenario, protectedRecords, item, current); plan.scenario_integrity_hash = scenario.integrity_hash; }
                    item.status = expected ? "verified" : (item.status == "manual_wizard_required" ? item.status : "verification_required");
                    item.verification = expected ? "expected_state_confirmed" : "expected_state_not_reached";
                    if (!expected) all = false;
                }
            }
            if (all) { plan.status = "verified"; plan.verified_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture); plan.last_error = null; }
            else plan.status = plan.actions.Any(x => x.status == "manual_wizard_required") ? "manual_wizard_required" : "verification_required";
            Save(plan);
            AuditLog.Append(project, "staging", "demo_verificar_acciones", plan.id, plan.status, "Verificación semántica; verified=" + plan.actions.Count(x => x.status == "verified").ToString(CultureInfo.InvariantCulture) + ".", 0);
            return Public(plan, all ? "Todos los estados se confirmaron por lectura." : "Hay acciones pendientes o un asistente manual; no se repitió ninguna escritura.");
        }

        private static Contract ContractOf(string action, string model, string expected, string[] fields, params string[] adapters) { return new Contract { action = action, model = model, expected = expected, fields = fields, adapters = adapters, block = LegacyBlock(action), parameterKeys = new string[0], odoo19Only = false }; }
        private static Contract Advanced(string action, string model, string expected, string[] fields, string block, string[] parameterKeys, params string[] adapters) { return new Contract { action = action, model = model, expected = expected, fields = fields, adapters = adapters, block = block, parameterKeys = parameterKeys, odoo19Only = true }; }
        private static string LegacyBlock(string action) { if (action.Contains("venta") || action.Contains("oportunidad")) return "crm_sales"; if (action.Contains("compra") || action.Contains("transferencia") || action.Contains("inventario") || action.Contains("costo_destino")) return "purchase_inventory"; return "accounting_finance"; }
        private DemoScenario LoadScenario(string id) { DemoScenario value = JsonUtil.ReadJsonFile<DemoScenario>(project.DemoScenarioPath(id)); DemoService.EnsureIntegrityForService(value); return value; }
        private static void ValidateScenario(DemoScenario scenario)
        {
            if (scenario.environment != "staging") throw new ConsultantException("production_write_blocked", "Producción se bloquea antes de cualquier llamada de red.");
            if (scenario.status != "applied" && scenario.status != "verified" && scenario.status != "verification_failed") throw new ConsultantException("demo_not_applied", "Primero crea los registros del escenario con CREAR-DEMO.");
            if (string.IsNullOrWhiteSpace(scenario.protected_records)) throw new ConsultantException("demo_records_not_protected", "El escenario no contiene un registro protegido de propiedad.");
        }
        private static Dictionary<string, object> GetProtectedEntry(IDictionary<string, object> records, string key, string model)
        {
            object raw; Dictionary<string, object> entry;
            if (!records.TryGetValue(key, out raw) || (entry = raw as Dictionary<string, object>) == null || JsonUtil.GetString(entry, "model", true) != model || JsonUtil.GetLong(entry, "id", true) <= 0)
                throw new ConsultantException("demo_action_target_not_owned", "La acción solo puede usar un registro protegido por el escenario y del modelo esperado.");
            return entry;
        }
        private static Dictionary<string, object> ReadOne(OdooClient client, Contract contract, long id)
        {
            IList<Dictionary<string, object>> rows = client.Read(contract.model, new[] { id }, contract.fields);
            if (rows.Count == 0 && contract.action == "completar_actividad") return JsonUtil.Dictionary("id", id, "completed", true);
            if (rows.Count != 1) throw new ConsultantException("demo_action_target_missing", "El registro protegido ya no está disponible.");
            return rows[0];
        }
        private static List<long> ResolveTargetIds(IDictionary<string, object> records, string primaryKey, IEnumerable<string> relatedKeys, string model)
        {
            List<long> ids = new List<long> { JsonUtil.GetLong(GetProtectedEntry(records, primaryKey, model), "id", true) };
            foreach (string key in relatedKeys ?? new string[0]) ids.Add(JsonUtil.GetLong(GetProtectedEntry(records, key, model), "id", true));
            if (ids.Count != ids.Distinct().Count()) throw new ConsultantException("demo_reconciliation_components_invalid", "La acción contiene el mismo registro protegido más de una vez.");
            return ids;
        }
        private static List<Dictionary<string, object>> ReadTargets(OdooClient client, Contract contract, IEnumerable<long> ids)
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            foreach (long id in ids) rows.Add(ReadOne(client, contract, id));
            return rows;
        }
        private static string StateHash(Contract contract, IDictionary<string, object> row)
        {
            Dictionary<string, object> state = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (string field in contract.fields) { object value; state[field] = row.TryGetValue(field, out value) ? value : null; }
            return JsonUtil.CanonicalHash(JsonUtil.Dictionary("model", contract.model, "action", contract.action, "state", state));
        }
        private static string StateHash(Contract contract, IEnumerable<Dictionary<string, object>> rows)
        {
            return JsonUtil.CanonicalHash(JsonUtil.Dictionary("model", contract.model, "action", contract.action, "states", (rows ?? new Dictionary<string, object>[0]).Select(row => StateHash(contract, row)).ToArray()));
        }
        private static bool IsExpected(Contract contract, IDictionary<string, object> row) { return IsExpected(contract, row, false); }
        private static bool IsExpected(Contract contract, IDictionary<string, object> row, bool expectBackorder)
        {
            if (contract.action == "ganar_oportunidad") return Bool(row, "active", true) && Number(row, "probability") >= 99.999m;
            if (contract.action == "perder_oportunidad") return !Bool(row, "active", true) && Number(row, "probability") <= 0.001m;
            if (contract.action == "contabilizar_pago") { string paymentState = Convert.ToString(Value(row, "state"), CultureInfo.InvariantCulture); return paymentState == "in_process" || paymentState == "paid"; }
            if (contract.action == "aplicar_inventario") return !Bool(row, "inventory_quantity_set", true) && Number(row, "quantity") > 0m;
            if (contract.action == "calcular_costo_destino") return Count(Value(row, "valuation_adjustment_lines")) > 0;
            if (contract.action == "validar_transferencia" && expectBackorder) return Convert.ToString(Value(row, "state"), CultureInfo.InvariantCulture) == "done" && Count(Value(row, "backorder_ids")) > 0;
            if (contract.action == "completar_actividad") return Bool(row, "completed", false);
            if (contract.action == "convertir_iniciativa") return Convert.ToString(Value(row, "type"), CultureInfo.InvariantCulture) == "opportunity" && ReferenceId(Value(row, "partner_id")) > 0;
            if (contract.action == "perder_oportunidad_con_motivo") return !Bool(row, "active", true) && ReferenceId(Value(row, "lost_reason_id")) > 0;
            if (contract.action == "crear_anticipo_venta" || contract.action == "crear_factura_final_venta" || contract.action == "crear_factura_proveedor_compra") return Count(Value(row, "invoice_ids")) > 0;
            if (contract.action == "crear_nota_credito") return Count(Value(row, "reversal_move_ids")) > 0;
            if (contract.action == "crear_nota_debito") return Count(Value(row, "debit_note_ids")) > 0;
            if (contract.action == "registrar_pago_factura") { string paymentState = Convert.ToString(Value(row, "payment_state"), CultureInfo.InvariantCulture); return paymentState == "in_payment" || paymentState == "paid" || paymentState == "partial"; }
            if (contract.action == "reconciliar_apuntes") return Bool(row, "reconciled", false);
            if (contract.action == "calcular_activo" || contract.action == "calcular_prestamo") return Count(Value(row, contract.action == "calcular_activo" ? "depreciation_move_ids" : "line_ids")) > 0;
            string state = Convert.ToString(Value(row, "state"), CultureInfo.InvariantCulture);
            if (contract.action == "validar_activo" || contract.action == "activar_prestamo") return state != "draft" && state != "cancel";
            if (contract.action == "confirmar_compra") return state == "purchase" || state == "done";
            if (contract.action == "reservar_transferencia") return state == "assigned";
            return state == contract.expected;
        }
        private static object Value(IDictionary<string, object> row, string key) { object value; return row != null && row.TryGetValue(key, out value) ? value : null; }
        private static bool Bool(IDictionary<string, object> row, string key, bool fallback) { object value = Value(row, key); if (value == null) return fallback; bool parsed; return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) ? parsed : fallback; }
        private static decimal Number(IDictionary<string, object> row, string key) { decimal parsed; return decimal.TryParse(Convert.ToString(Value(row, key), CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed) ? parsed : 0m; }
        private static long ReferenceId(object value)
        {
            if (value == null || value is bool) return 0;
            IEnumerable sequence = value as IEnumerable;
            if (sequence != null && !(value is string))
            {
                foreach (object item in sequence)
                {
                    long nested = ReferenceId(item);
                    if (nested > 0) return nested;
                }
                return 0;
            }
            long parsed;
            return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }
        private static int Count(object value) { IEnumerable sequence = value as IEnumerable; if (sequence == null || value is string) return 0; int count = 0; foreach (object ignored in sequence) count++; return count; }
        private void CaptureDerived(OdooClient client, DemoScenario scenario, IDictionary<string, object> protectedRecords, DemoActionItem item, IDictionary<string, object> current)
        {
            Dictionary<string, string> relations = new Dictionary<string, string>(StringComparer.Ordinal);
            if (item.action == "confirmar_venta") { relations["project_ids"] = "project.project"; relations["picking_ids"] = "stock.picking"; }
            else if (item.action == "confirmar_compra") relations["picking_ids"] = "stock.picking";
            else if (item.action == "confirmar_acuerdo_compra") relations["purchase_ids"] = "purchase.order";
            else if (item.action == "crear_anticipo_venta" || item.action == "crear_factura_final_venta" || item.action == "crear_factura_proveedor_compra") relations["invoice_ids"] = "account.move";
            else if (item.action == "validar_transferencia") relations["backorder_ids"] = "stock.picking";
            else if (item.action == "crear_nota_credito") relations["reversal_move_ids"] = "account.move";
            else if (item.action == "crear_nota_debito") relations["debit_note_ids"] = "account.move";
            else if (item.action == "registrar_pago_factura") relations["payment_ids"] = "account.payment";
            else if (item.action == "calcular_activo") relations["depreciation_move_ids"] = "account.move";
            else if (item.action == "calcular_prestamo") relations["line_ids"] = "account.loan.line";
            bool changed = false; DemoRegistryCapability capability = null;
            foreach (KeyValuePair<string, string> relation in relations)
            {
                object raw; if (!current.TryGetValue(relation.Key, out raw)) continue;
                changed |= AddDerivedIds(client, scenario, protectedRecords, item.component_key, relation.Key, relation.Value, ExtractIds(raw), ref capability);
            }
            if (item.action == "confirmar_venta")
            {
                try
                {
                    Dictionary<string, object> taskFields = client.FieldsGet("project.task", new[] { "type", "relation" });
                    if (taskFields.ContainsKey("sale_order_id"))
                    {
                        IList<Dictionary<string, object>> tasks = client.SearchRead("project.task", new object[] { new object[] { "sale_order_id", "=", JsonUtil.GetLong(GetProtectedEntry(protectedRecords, item.component_key, "sale.order"), "id", true) } }, new[] { "id" }, 100, "id asc");
                        changed |= AddDerivedIds(client, scenario, protectedRecords, item.component_key, "task_ids", "project.task", tasks.Select(x => JsonUtil.GetLong(x, "id", true)), ref capability);
                    }
                }
                catch (ConsultantException) { }
            }
            if (changed)
            {
                scenario.protected_records = ProtectedSnapshot.Protect(protectedRecords);
                DemoService.SaveScenarioForService(project, scenario);
            }
        }
        private static bool AddDerivedIds(OdooClient client, DemoScenario scenario, IDictionary<string, object> protectedRecords, string parentKey, string relation, string model, IEnumerable<long> ids, ref DemoRegistryCapability capability)
        {
            bool changed = false; int sequence = 0;
            foreach (long id in (ids ?? new long[0]).Where(x => x > 0).Distinct().OrderBy(x => x))
            {
                sequence++;
                bool exists = protectedRecords.Values.OfType<Dictionary<string, object>>().Any(x => JsonUtil.GetString(x, "model", false) == model && JsonUtil.GetLong(x, "id", false) == id);
                if (exists) continue;
                string key = parentKey + "__" + relation.Replace("_ids", string.Empty) + "_" + sequence.ToString(CultureInfo.InvariantCulture);
                while (protectedRecords.ContainsKey(key)) { sequence++; key = parentKey + "__" + relation.Replace("_ids", string.Empty) + "_" + sequence.ToString(CultureInfo.InvariantCulture); }
                if (capability == null) capability = DemoOwnershipRegistry.Probe(client);
                DemoRegistryRecord registry = DemoOwnershipRegistry.RegisterDerived(client, scenario, key, model, id, capability);
                protectedRecords[key] = JsonUtil.Dictionary("model", model, "id", id, "created_by_scenario", true, "derived", true, "parent_component", parentKey, "relation", relation, "registry_id", registry == null ? 0 : registry.registry_id, "registry_name", registry == null ? null : registry.name);
                changed = true;
            }
            return changed;
        }
        private static IEnumerable<long> ExtractIds(object value)
        {
            List<long> result = new List<long>(); IEnumerable sequence = value as IEnumerable;
            if (sequence == null || value is string) { long scalar; if (long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out scalar) && scalar > 0) result.Add(scalar); return result; }
            foreach (object item in sequence)
            {
                if (item is IEnumerable && !(item is string)) result.AddRange(ExtractIds(item));
                else { long parsed; if (long.TryParse(Convert.ToString(item, CultureInfo.InvariantCulture), out parsed) && parsed > 0) result.Add(parsed); }
            }
            return result;
        }
        private OdooClient Open(ConnectionProfile profile, int timeout) { return ProjectStore.OpenClient(profile, timeout); }
        private void Save(DemoActionPlan plan) { plan.integrity_hash = Integrity(plan); JsonUtil.WriteJsonFile(project.DemoActionPlanPath(plan.id), plan); }
        private DemoActionPlan Load(string id) { return JsonUtil.ReadJsonFile<DemoActionPlan>(project.DemoActionPlanPath(id)); }
        private static string Integrity(DemoActionPlan plan) { Dictionary<string, object> value = JsonUtil.DeserializeObject(JsonUtil.Serialize(plan)); value.Remove("integrity_hash"); return JsonUtil.CanonicalHash(value); }
        private void ValidatePlan(DemoActionPlan plan)
        {
            bool schemaValid = plan != null && ((plan.schema_version == 1 && plan.policy_version == PolicyVersion) || (plan.schema_version == 2 && plan.policy_version == FlowPolicyVersion));
            if (!schemaValid || plan.client_slug != project.Config.client_slug || plan.environment != "staging" || (plan.integrity_hash != Integrity(plan) && plan.integrity_hash != LegacyIntegrity(plan))) throw new ConsultantException("demo_action_plan_integrity_failed", "El plan de acciones no es válido o fue modificado fuera del motor.");
            if (plan.schema_version == 2)
            {
                Dictionary<string, object> current = new InstanceContractService(project).Load(plan.demo_slug);
                if (JsonUtil.GetString(current, "integrity_hash", true) != plan.instance_contract_hash) throw new ConsultantException("instance_contract_changed", "El contrato de la instancia cambió desde la vista previa del flujo.");
            }
            if (plan.actions == null || plan.actions.Count == 0 || plan.actions.Count > 30 || plan.actions.Any(x => !Contracts.ContainsKey(x.action))) throw new ConsultantException("demo_action_plan_integrity_failed", "El plan no contiene acciones válidas.");
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, string> lastByComponent = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (DemoActionItem item in plan.actions)
            {
                Contract contract = Contracts[item.action];
                if (string.IsNullOrWhiteSpace(item.key) || !keys.Add(item.key) || string.IsNullOrWhiteSpace(item.component_key) || item.model != contract.model || item.expected_state != contract.expected || string.IsNullOrWhiteSpace(item.precondition_hash))
                    throw new ConsultantException("demo_action_plan_integrity_failed", "El plan contiene una acción inconsistente con el catálogo cerrado.");
                if (item.action == "reconciliar_apuntes" && ((item.related_component_keys ?? new List<string>()).Count == 0 || item.related_component_keys.Count > 19)) throw new ConsultantException("demo_action_plan_integrity_failed", "La conciliación debe conservar todos sus componentes protegidos.");
                if (item.action != "reconciliar_apuntes" && (item.related_component_keys ?? new List<string>()).Count > 0) throw new ConsultantException("demo_action_plan_integrity_failed", "La acción contiene componentes relacionados no permitidos.");
                string previous; if (lastByComponent.TryGetValue(item.component_key, out previous) && !(item.depends_on ?? new List<string>()).Contains(previous)) throw new ConsultantException("demo_action_plan_integrity_failed", "Las acciones consecutivas sobre el mismo registro deben declarar su dependencia."); lastByComponent[item.component_key] = item.key;
                if (item.action != "validar_transferencia" && (item.cancel_backorder || item.expect_backorder)) throw new ConsultantException("demo_action_plan_integrity_failed", "El plan contiene opciones de backorder fuera de una transferencia.");
                if (item.cancel_backorder && item.expect_backorder) throw new ConsultantException("demo_action_plan_integrity_failed", "El plan contiene opciones de backorder incompatibles.");
                if (plan.schema_version == 2 && item.block != contract.block) throw new ConsultantException("demo_action_plan_integrity_failed", "El bloque funcional de una acción no coincide con el catálogo cerrado.");
                if (!string.IsNullOrWhiteSpace(item.protected_parameters)) ValidateParameters(contract, ProtectedSnapshot.Unprotect(item.protected_parameters));
            }
            ValidateDependencies(plan.actions);
        }
        private static Dictionary<string, object> Public(DemoActionPlan plan, string message)
        {
            return JsonUtil.Dictionary("schema_version", plan.schema_version, "id", plan.id, "scenario_id", plan.scenario_id, "demo_slug", plan.demo_slug, "environment", plan.environment,
                "status", plan.status, "created_at", plan.created_at, "expires_at", plan.expires_at, "confirmation", "EJECUTAR-DEMO " + plan.id,
                "actions", (plan.actions ?? new List<DemoActionItem>()).Select(x => JsonUtil.Dictionary("key", x.key, "action", x.action, "component_key", x.component_key, "related_component_keys", x.related_component_keys, "model", x.model, "block", x.block, "depends_on", x.depends_on, "parameter_names", string.IsNullOrWhiteSpace(x.protected_parameters) ? new object[0] : ProtectedSnapshot.Unprotect(x.protected_parameters).Keys.OrderBy(y => y, StringComparer.Ordinal).ToArray(), "status", x.status, "expected_state", x.expected_state, "verification", x.verification, "cancel_backorder", x.cancel_backorder, "expect_backorder", x.expect_backorder)).ToArray(),
                "warnings", plan.warnings, "last_error", plan.last_error, "message", message);
        }

        internal static bool ActionAllowedForSelfTest(string action) { return Contracts.ContainsKey(action ?? string.Empty); }
        internal static bool AdapterAllowedForSelfTest(string action, string adapter) { Contract contract; return Contracts.TryGetValue(action ?? string.Empty, out contract) && contract.adapters.Contains(adapter ?? string.Empty, StringComparer.Ordinal); }
        internal static bool ExpectedForSelfTest(string action, IDictionary<string, object> state) { Contract contract; return Contracts.TryGetValue(action ?? string.Empty, out contract) && IsExpected(contract, state ?? new Dictionary<string, object>()); }
        internal static bool ExpectedBackorderForSelfTest(IDictionary<string, object> state) { return IsExpected(Contracts["validar_transferencia"], state ?? new Dictionary<string, object>(), true); }
        internal static bool RelatedComponentsAllowedForSelfTest(string action, int count) { return action == "reconciliar_apuntes" ? count >= 1 && count <= 19 : count == 0; }
        internal static string ConfirmationForSelfTest(string id) { return "EJECUTAR-DEMO " + id; }
        internal static bool BlockAllowedForSelfTest(string block) { block = (block ?? string.Empty).Trim().ToLowerInvariant(); return block == "general" || block == "transversal" || block == "crm_sales" || block == "purchase_inventory" || block == "accounting_finance"; }
        internal static bool ActionVersionAllowedForSelfTest(string series) { return series == "17.0" || series == "18.0" || series == "19.0"; }

        private static void ValidateParameters(Contract contract, IDictionary<string, object> parameters)
        {
            HashSet<string> allowed = new HashSet<string>(contract.parameterKeys ?? new string[0], StringComparer.Ordinal);
            foreach (string key in (parameters ?? new Dictionary<string, object>()).Keys) if (!allowed.Contains(key)) throw new ConsultantException("invalid_demo_action_parameter", "La acción no permite el parámetro " + key + ".");
            if (contract.action == "crear_anticipo_venta" && (parameters == null || !parameters.ContainsKey("percentage"))) throw new ConsultantException("invalid_demo_action_parameter", "El anticipo requiere percentage.");
            if (contract.action == "registrar_pago_factura" && (parameters == null || !parameters.ContainsKey("amount") || !parameters.ContainsKey("journal_id") || !parameters.ContainsKey("payment_method_line_id"))) throw new ConsultantException("invalid_demo_action_parameter", "El pago requiere importe, diario y método de pago tipados.");
            if (contract.action == "convertir_iniciativa" && (parameters == null || !parameters.ContainsKey("partner_id"))) throw new ConsultantException("invalid_demo_action_parameter", "La conversión requiere partner_id tipado.");
            if (contract.action == "perder_oportunidad_con_motivo" && (parameters == null || !parameters.ContainsKey("lost_reason_id"))) throw new ConsultantException("invalid_demo_action_parameter", "El cierre perdido requiere lost_reason_id tipado.");
        }
        private static void ValidateParameterValues(OdooClient client, IDictionary<string, object> parameters)
        {
            if (parameters == null) return;
            foreach (string key in new[] { "percentage", "amount" })
            {
                object raw; decimal number;
                if (parameters.TryGetValue(key, out raw) && (!decimal.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number) || number <= 0m || (key == "percentage" && number > 100m) || (key == "amount" && number > 1000000000000m)))
                    throw new ConsultantException("invalid_demo_action_parameter", "El parámetro " + key + " está fuera del rango permitido.");
            }
            foreach (string key in new[] { "date", "payment_date" })
            {
                object raw; DateTime parsed;
                if (parameters.TryGetValue(key, out raw) && !DateTime.TryParseExact(Convert.ToString(raw, CultureInfo.InvariantCulture), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                    throw new ConsultantException("invalid_demo_action_parameter", "El parámetro " + key + " debe usar yyyy-MM-dd.");
            }
            object reasonRaw;
            if (parameters.TryGetValue("reason", out reasonRaw))
            {
                string reason = Convert.ToString(reasonRaw, CultureInfo.InvariantCulture) ?? string.Empty;
                if (reason.Length > 200 || reason.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new ConsultantException("invalid_demo_action_parameter", "El motivo no cumple el contrato cerrado.");
            }
            Dictionary<string, string> references = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "journal_id", "account.journal" }, { "payment_method_line_id", "account.payment.method.line" }, { "partner_id", "res.partner" },
                { "user_id", "res.users" }, { "team_id", "crm.team" }, { "lost_reason_id", "crm.lost.reason" }
            };
            foreach (KeyValuePair<string, string> reference in references)
            {
                object raw; long id;
                if (!parameters.TryGetValue(reference.Key, out raw)) continue;
                if (!long.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), out id) || id <= 0 || client.Read(reference.Value, new[] { id }, new[] { "display_name" }).Count != 1)
                    throw new ConsultantException("invalid_demo_action_parameter", "La referencia tipada " + reference.Key + " no existe o no es visible en el modelo esperado.");
            }
        }
        private static List<string> ToStringList(object value)
        {
            List<string> result = new List<string>(); IEnumerable sequence = value as IEnumerable;
            if (sequence != null && !(value is string)) foreach (object item in sequence) { string text = Convert.ToString(item, CultureInfo.InvariantCulture); if (!string.IsNullOrWhiteSpace(text)) result.Add(text); }
            return result;
        }
        private static void ValidateDependencies(IList<DemoActionItem> actions)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (DemoActionItem item in actions)
            {
                foreach (string dependency in item.depends_on ?? new List<string>()) if (!seen.Contains(dependency)) throw new ConsultantException("demo_action_dependency_invalid", "Cada dependencia debe apuntar a una acción anterior del mismo plan.");
                seen.Add(item.key);
            }
        }
        private static string LegacyIntegrity(DemoActionPlan plan)
        {
            Dictionary<string, object> value = JsonUtil.DeserializeObject(JsonUtil.Serialize(plan)); value.Remove("integrity_hash"); value.Remove("flow_block"); value.Remove("instance_contract_hash");
            object raw; IEnumerable items = value.TryGetValue("actions", out raw) ? raw as IEnumerable : null;
            if (items != null) foreach (object item in items) { Dictionary<string, object> action = item as Dictionary<string, object>; if (action != null) { action.Remove("block"); action.Remove("depends_on"); action.Remove("protected_parameters"); action.Remove("related_component_keys"); } }
            return JsonUtil.CanonicalHash(value);
        }
    }
}
