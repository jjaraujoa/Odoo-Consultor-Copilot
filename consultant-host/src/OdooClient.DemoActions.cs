using System;
using System.Collections.Generic;
using System.Globalization;

namespace OdooConsultor
{
    public sealed partial class OdooClient
    {
        public object ExecuteDemoSemanticAction(string action, IEnumerable<long> recordIds, bool cancelBackorder)
        {
            return ExecuteDemoSemanticAction(action, recordIds, cancelBackorder, null);
        }

        public object ExecuteDemoSemanticAction(string action, IEnumerable<long> recordIds, bool cancelBackorder, IDictionary<string, object> parameters)
        {
            List<object> ids = new List<object>();
            foreach (long id in recordIds ?? new long[0])
            {
                if (id <= 0) throw new ConsultantException("invalid_demo_action_target", "La acción contiene un ID no válido.");
                if (!ids.Contains(id)) ids.Add(id);
            }
            if (ids.Count == 0) throw new ConsultantException("invalid_demo_action_target", "La acción no contiene registros.");
            Dictionary<string, object> body = JsonUtil.Dictionary("ids", ids.ToArray());
            if (action == "confirmar_venta") return Call("sale.order", "action_confirm", body);
            if (action == "confirmar_compra") return Call("purchase.order", "button_confirm", body);
            if (action == "confirmar_acuerdo_compra") return Call("purchase.requisition", "action_confirm", body);
            if (action == "ganar_oportunidad") return Call("crm.lead", "action_set_won", body);
            if (action == "perder_oportunidad") return Call("crm.lead", "action_set_lost", body);
            if (action == "reservar_transferencia") return Call("stock.picking", "action_assign", body);
            if (action == "validar_transferencia")
            {
                body["context"] = JsonUtil.Dictionary("button_validate_picking_ids", ids.ToArray(), "skip_backorder", true, "picking_ids_not_to_backorder", cancelBackorder ? ids.ToArray() : new object[0]);
                return Call("stock.picking", "button_validate", body);
            }
            if (action == "contabilizar_movimiento") return Call("account.move", "action_post", body);
            if (action == "contabilizar_pago") return Call("account.payment", "action_post", body);
            if (action == "aplicar_inventario") return Call("stock.quant", "action_apply_inventory", body);
            if (action == "calcular_costo_destino") return Call("stock.landed.cost", "compute_landed_cost", body);
            if (action == "validar_costo_destino") return Call("stock.landed.cost", "button_validate", body);
            if (profile.server_series != "17.0" && profile.server_series != "18.0" && profile.server_series != "19.0") throw new ConsultantException("demo_action_version_not_supported", "Las acciones avanzadas están habilitadas para Odoo 17, 18 y 19.");
            if (action == "completar_actividad") return Call("mail.activity", "action_done", body);
            if (action == "aprobar_comisiones") return Call("sale.commission.plan", "action_approve", body);
            if (action == "confirmar_presupuesto") return Call("budget.analytic", "action_confirm", body);
            if (action == "calcular_activo") return Call("account.asset", "compute_depreciation_board", body);
            if (action == "validar_activo") return Call("account.asset", "validate", body);
            if (action == "calcular_prestamo") return Call("account.loan", "action_compute", body);
            if (action == "activar_prestamo") return Call("account.loan", "action_validate", body);
            if (action == "cancelar_compra") return Call("purchase.order", "button_cancel", body);
            if (action == "crear_factura_proveedor_compra") return Call("purchase.order", "action_create_invoice", body);
            if (action == "reconciliar_apuntes") return Call("account.move.line", "reconcile", body);
            if (action == "convertir_iniciativa")
            {
                long partnerId = ParameterLong(parameters, "partner_id", true);
                Dictionary<string, object> values = JsonUtil.Dictionary("name", "convert", "action", "exist", "lead_id", ids[0], "partner_id", partnerId, "force_assignment", true);
                long userId = ParameterLong(parameters, "user_id", false); if (userId > 0) values["user_id"] = userId;
                long teamId = ParameterLong(parameters, "team_id", false); if (teamId > 0) values["team_id"] = teamId;
                Dictionary<string, object> context = JsonUtil.Dictionary("active_model", "crm.lead", "active_id", ids[0], "active_ids", ids.ToArray());
                long wizard = CreateWithContext("crm.lead2opportunity.partner", values, context);
                return Call("crm.lead2opportunity.partner", "action_apply", JsonUtil.Dictionary("ids", new object[] { wizard }, "context", context));
            }
            if (action == "perder_oportunidad_con_motivo")
            {
                long reasonId = ParameterLong(parameters, "lost_reason_id", true);
                Dictionary<string, object> context = JsonUtil.Dictionary("active_model", "crm.lead", "active_id", ids[0], "active_ids", ids.ToArray());
                long wizard = CreateWithContext("crm.lead.lost", JsonUtil.Dictionary("lead_ids", new object[] { new object[] { 6, 0, ids.ToArray() } }, "lost_reason_id", reasonId), context);
                return Call("crm.lead.lost", "action_lost_reason_apply", JsonUtil.Dictionary("ids", new object[] { wizard }, "context", context));
            }
            if (action == "crear_anticipo_venta" || action == "crear_factura_final_venta")
            {
                Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.Ordinal);
                if (action == "crear_anticipo_venta")
                {
                    decimal percentage = ParameterDecimal(parameters, "percentage", 0.01m, 100m);
                    values["advance_payment_method"] = "percentage";
                    values["amount"] = percentage;
                }
                Dictionary<string, object> context = JsonUtil.Dictionary("active_model", "sale.order", "active_id", ids[0], "active_ids", ids.ToArray());
                long wizard = CreateWithContext("sale.advance.payment.inv", values, context);
                return Call("sale.advance.payment.inv", "create_invoices", JsonUtil.Dictionary("ids", new object[] { wizard }, "context", context));
            }
            if (action == "crear_nota_credito" || action == "crear_nota_debito")
            {
                string wizardModel = action == "crear_nota_credito" ? "account.move.reversal" : "account.debit.note";
                string method = action == "crear_nota_credito" ? "reverse_moves" : "create_debit";
                Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.Ordinal);
                string reason = ParameterText(parameters, "reason", 200);
                if (!string.IsNullOrWhiteSpace(reason)) values["reason"] = reason;
                string date = ParameterDate(parameters, "date");
                if (!string.IsNullOrWhiteSpace(date)) values["date"] = date;
                long journal = ParameterLong(parameters, "journal_id", false);
                if (journal > 0) values["journal_id"] = journal;
                if (action == "crear_nota_debito") values["copy_lines"] = true;
                Dictionary<string, object> context = JsonUtil.Dictionary("active_model", "account.move", "active_id", ids[0], "active_ids", ids.ToArray());
                long wizard = CreateWithContext(wizardModel, values, context);
                return Call(wizardModel, method, JsonUtil.Dictionary("ids", new object[] { wizard }, "context", context));
            }
            if (action == "registrar_pago_factura")
            {
                Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.Ordinal);
                values["amount"] = ParameterDecimal(parameters, "amount", 0.01m, 1000000000000m);
                string date = ParameterDate(parameters, "payment_date"); if (!string.IsNullOrWhiteSpace(date)) values["payment_date"] = date;
                long journal = ParameterLong(parameters, "journal_id", true); values["journal_id"] = journal;
                long methodLine = ParameterLong(parameters, "payment_method_line_id", true); values["payment_method_line_id"] = methodLine;
                values["group_payment"] = true;
                Dictionary<string, object> context = JsonUtil.Dictionary("active_model", "account.move", "active_id", ids[0], "active_ids", ids.ToArray());
                long wizard = CreateWithContext("account.payment.register", values, context);
                return Call("account.payment.register", "action_create_payments", JsonUtil.Dictionary("ids", new object[] { wizard }, "context", context));
            }
            throw new ConsultantException("demo_action_not_allowed", "La acción semántica no pertenece al catálogo cerrado.");
        }

        public object ExecuteDemoCompensationAction(string operation, long recordId)
        {
            if (profile.server_series != "17.0" && profile.server_series != "18.0" && profile.server_series != "19.0") throw new ConsultantException("demo_compensation_version_not_supported", "La compensación cerrada está habilitada para Odoo 17, 18 y 19.");
            if (recordId <= 0) throw new ConsultantException("invalid_demo_action_target", "La compensación contiene un ID no válido.");
            Dictionary<string, object> body = JsonUtil.Dictionary("ids", new object[] { recordId });
            if (operation == "cancel_sale") return Call("sale.order", "action_cancel", body);
            if (operation == "cancel_purchase") return Call("purchase.order", "button_cancel", body);
            if (operation == "cancel_picking") return Call("stock.picking", "action_cancel", body);
            if (operation == "cancel_move") return Call("account.move", "button_cancel", body);
            if (operation == "cancel_payment") return Call("account.payment", "action_cancel", body);
            throw new ConsultantException("demo_compensation_not_allowed", "La compensación no pertenece al catálogo cerrado.");
        }

        private long CreateWithContext(string model, IDictionary<string, object> values, IDictionary<string, object> context)
        {
            return FirstInteger(Call(model, "create", JsonUtil.Dictionary("vals_list", new object[] { values }, "context", context)), "Odoo no devolvió el ID del asistente cerrado.");
        }

        private static decimal ParameterDecimal(IDictionary<string, object> values, string key, decimal minimum, decimal maximum)
        {
            object raw; decimal result;
            if (values == null || !values.TryGetValue(key, out raw) || !decimal.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out result) || result < minimum || result > maximum) throw new ConsultantException("invalid_demo_action_parameter", "El parámetro " + key + " está ausente o fuera de rango.");
            return result;
        }

        private static long ParameterLong(IDictionary<string, object> values, string key, bool required)
        {
            object raw; long result;
            if (values == null || !values.TryGetValue(key, out raw) || !long.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), out result) || result <= 0)
            {
                if (!required) return 0;
                throw new ConsultantException("invalid_demo_action_parameter", "El parámetro " + key + " debe ser un ID positivo.");
            }
            return result;
        }

        private static string ParameterText(IDictionary<string, object> values, string key, int maximum)
        {
            object raw; if (values == null || !values.TryGetValue(key, out raw) || raw == null) return null;
            string result = Convert.ToString(raw, CultureInfo.InvariantCulture).Trim();
            if (result.Length > maximum || result.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new ConsultantException("invalid_demo_action_parameter", "El parámetro " + key + " no es válido.");
            return result;
        }

        private static string ParameterDate(IDictionary<string, object> values, string key)
        {
            string result = ParameterText(values, key, 10); if (string.IsNullOrWhiteSpace(result)) return null;
            DateTime parsed; if (!DateTime.TryParseExact(result, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)) throw new ConsultantException("invalid_demo_action_parameter", "El parámetro " + key + " debe usar yyyy-MM-dd.");
            return result;
        }
    }
}
