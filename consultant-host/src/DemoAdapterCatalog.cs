using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OdooConsultor
{
    public sealed class DemoAdapterContract
    {
        public string key { get; set; }
        public string model { get; set; }
        public string[] modules { get; set; }
        public string[] allowed_fields { get; set; }
        public string[] required_contract_fields { get; set; }
        public string[] identity_fields { get; set; }
        public string[] verification_fields { get; set; }
        public string cleanup_strategy { get; set; }
        public string contained_by { get; set; }
    }

    public static class DemoAdapterCatalog
    {
        public const string ContractVersion = "commercial-demo-v5";
        public const string ScenarioContractVersion = "commercial-demo-v5.0";

        private static readonly Dictionary<string, DemoAdapterContract> Contracts = Build();

        public static IEnumerable<DemoAdapterContract> All { get { return Contracts.Values.OrderBy(x => x.key, StringComparer.Ordinal); } }

        public static DemoAdapterContract Get(string key)
        {
            DemoAdapterContract value;
            if (!Contracts.TryGetValue(key ?? string.Empty, out value)) throw new ConsultantException("demo_adapter_not_allowed", "El adaptador no pertenece al catálogo cerrado de demostraciones.");
            return value;
        }

        public static Dictionary<string, object> CapabilityDescriptor(DemoAdapterContract contract, IDictionary<string, object> fields, bool read, bool create, bool write)
        {
            Dictionary<string, object> projection = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (string field in contract.allowed_fields)
            {
                object definition;
                if (fields.TryGetValue(field, out definition)) projection[field] = definition;
            }
            return JsonUtil.Dictionary(
                "contract_version", ContractVersion,
                "adapter", contract.key,
                "model", contract.model,
                "modules", contract.modules,
                "allowed_fields", contract.allowed_fields,
                "field_contract", projection,
                "read", read,
                "create", create,
                "write", write,
                "cleanup_strategy", contract.cleanup_strategy);
        }

        public static Dictionary<string, object> FilterAndValidateValues(DemoAdapterContract contract, IDictionary<string, object> desired, IDictionary<string, object> fields, bool strict)
        {
            HashSet<string> allowed = new HashSet<string>(contract.allowed_fields, StringComparer.Ordinal);
            HashSet<string> requiredContract = new HashSet<string>(contract.required_contract_fields ?? new string[0], StringComparer.Ordinal);
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> pair in desired ?? new Dictionary<string, object>())
            {
                if (!allowed.Contains(pair.Key)) throw new ConsultantException("demo_field_not_allowed", "El adaptador " + contract.key + " no permite el campo " + pair.Key + ".");
                object raw;
                if (!fields.TryGetValue(pair.Key, out raw))
                {
                    if (strict && requiredContract.Contains(pair.Key)) throw new ConsultantException("demo_capability_missing", "La base no expone un campo requerido por el adaptador " + contract.key + ".");
                    continue;
                }
                Dictionary<string, object> definition = raw as Dictionary<string, object>;
                if (definition != null && JsonUtil.GetBool(definition, "readonly", false))
                {
                    if (strict && requiredContract.Contains(pair.Key)) throw new ConsultantException("demo_field_readonly", "La base marcó como solo lectura un campo requerido del adaptador " + contract.key + ".");
                    continue;
                }
                result[pair.Key] = pair.Value;
            }
            foreach (string required in requiredContract)
            {
                if (!fields.ContainsKey(required)) throw new ConsultantException("demo_capability_missing", "Falta el campo contractual " + required + " en " + contract.model + ".");
            }
            return result;
        }

        public static string IdentityField(DemoAdapterContract contract, IDictionary<string, object> values)
        {
            foreach (string field in contract.identity_fields) if (values != null && values.ContainsKey(field)) return field;
            return null;
        }

        public static int SelfTest()
        {
            int passed = 0;
            if (Get("website_landing_unpublished").model != "website.page") throw new Exception("El adaptador de landing no usa website.page.");
            passed++;
            if (Get("survey_answer").model != "survey.question.answer") throw new Exception("El adaptador de respuestas de encuesta no es correcto.");
            passed++;
            if (!Get("mailing_draft").allowed_fields.Contains("ab_testing_enabled")) throw new Exception("El adaptador de mailing no cubre A/B.");
            passed++;
            if (Get("mailing_visual_asset").model != "ir.attachment" || Get("mailing_visual_asset").cleanup_strategy != "detach_and_unpublish") throw new Exception("El recurso visual no está protegido por un adaptador cerrado.");
            passed++;
            if (Get("marketing_activity_inactive").cleanup_strategy != "contained_by_parent") throw new Exception("La actividad de marketing no está gobernada por su campaña inactiva.");
            passed++;
            if (Get("stock_move_draft").required_contract_fields.Contains("name") || !Get("stock_move_draft").allowed_fields.Contains("description_picking")) throw new Exception("El movimiento de inventario no es compatible con la descripción de Odoo 19.");
            passed++;
            if (Get("stock_move_line_done").model != "stock.move.line" || !Get("stock_move_line_done").required_contract_fields.Contains("quantity")) throw new Exception("La cantidad ejecutada no usa un adaptador cerrado de stock.move.line.");
            passed++;
            bool arbitraryBlocked = false;
            try
            {
                FilterAndValidateValues(Get("crm_lead"), JsonUtil.Dictionary("password", "x"), JsonUtil.Dictionary("password", JsonUtil.Dictionary("readonly", false)), true);
            }
            catch (ConsultantException error) { arbitraryBlocked = error.Code == "demo_field_not_allowed"; }
            if (!arbitraryBlocked) throw new Exception("El catálogo aceptó un campo arbitrario.");
            passed++;
            return passed;
        }

        private static Dictionary<string, DemoAdapterContract> Build()
        {
            Dictionary<string, DemoAdapterContract> values = new Dictionary<string, DemoAdapterContract>(StringComparer.Ordinal);
            Add(values, Contract("utm_source", "utm.source", new[] { "utm" }, new[] { "name", "active" }, new[] { "name" }, new[] { "name" }, new[] { "name", "active" }, "deactivate", null));
            Add(values, Contract("utm_medium", "utm.medium", new[] { "utm" }, new[] { "name", "active" }, new[] { "name" }, new[] { "name" }, new[] { "name", "active" }, "deactivate", null));
            Add(values, Contract("utm_campaign", "utm.campaign", new[] { "utm" }, new[] { "name", "active" }, new[] { "name" }, new[] { "name" }, new[] { "name", "active" }, "deactivate", null));
            Add(values, Contract("crm_lead", "crm.lead", new[] { "crm" }, new[] { "name", "type", "contact_name", "partner_name", "email_from", "phone", "description", "source_id", "medium_id", "campaign_id", "active" }, new[] { "name", "email_from" }, new[] { "name" }, new[] { "name", "email_from", "source_id", "medium_id", "campaign_id", "active" }, "deactivate", null));
            Add(values, Contract("mailing_list", "mailing.list", new[] { "mass_mailing" }, new[] { "name", "active" }, new[] { "name" }, new[] { "name" }, new[] { "name", "active" }, "deactivate", null));
            Add(values, Contract("mailing_contact", "mailing.contact", new[] { "mass_mailing" }, new[] { "name", "email", "list_ids", "active" }, new[] { "name", "email" }, new[] { "name", "email" }, new[] { "name", "email", "list_ids", "active" }, "deactivate", null));
            Add(values, Contract("mailing_visual_asset", "ir.attachment", new[] { "mass_mailing" }, new[] { "name", "type", "datas", "mimetype", "public", "res_model", "res_id" }, new[] { "name", "type", "mimetype", "public" }, new[] { "name" }, new[] { "name", "mimetype", "public", "res_model", "res_id" }, "detach_and_unpublish", null));
            Add(values, Contract("mailing_draft", "mailing.mailing", new[] { "mass_mailing" }, new[] { "subject", "preview", "email_from", "mailing_type", "body_arch", "contact_list_ids", "mailing_model_id", "mailing_domain", "state", "campaign_id", "ab_testing_enabled", "ab_testing_pc", "active" }, new[] { "subject", "email_from", "mailing_type", "body_arch", "state" }, new[] { "subject" }, new[] { "subject", "preview", "mailing_type", "body_arch", "state", "campaign_id", "mailing_model_id", "mailing_domain", "ab_testing_enabled", "ab_testing_pc", "active" }, "deactivate", null));
            Add(values, Contract("marketing_campaign_inactive", "marketing.campaign", new[] { "marketing_automation" }, new[] { "name", "title", "active", "state", "model_id", "domain" }, new[] { "name", "title", "active", "state", "model_id" }, new[] { "title", "name" }, new[] { "title", "active", "state", "model_id" }, "deactivate", null));
            Add(values, Contract("marketing_activity_inactive", "marketing.activity", new[] { "marketing_automation" }, new[] { "campaign_id", "activity_type", "interval_number", "interval_type", "trigger_type", "parent_id", "mass_mailing_id" }, new[] { "campaign_id", "activity_type", "interval_type", "trigger_type", "mass_mailing_id" }, new string[0], new[] { "campaign_id", "activity_type", "interval_number", "interval_type", "trigger_type", "mass_mailing_id" }, "contained_by_parent", "marketing_campaign"));
            Add(values, Contract("website_landing_unpublished", "website.page", new[] { "website", "website_crm" }, new[] { "name", "url", "website_id", "arch", "key", "type", "track", "website_indexed", "is_published", "active" }, new[] { "name", "url", "arch", "type", "is_published" }, new[] { "name", "url" }, new[] { "name", "url", "website_id", "is_published", "active", "website_indexed" }, "unpublish_and_deactivate", null));
            Add(values, Contract("survey_draft", "survey.survey", new[] { "survey" }, new[] { "title", "description", "access_mode", "users_login_required", "scoring_type", "certification", "active" }, new[] { "title", "access_mode", "scoring_type", "active" }, new[] { "title" }, new[] { "title", "access_mode", "users_login_required", "scoring_type", "certification", "active" }, "deactivate", null));
            Add(values, Contract("survey_section", "survey.question", new[] { "survey" }, new[] { "survey_id", "title", "sequence", "is_page" }, new[] { "survey_id", "title", "sequence", "is_page" }, new[] { "title" }, new[] { "survey_id", "title", "sequence", "is_page" }, "contained_by_parent", "survey"));
            Add(values, Contract("survey_question", "survey.question", new[] { "survey" }, new[] { "survey_id", "title", "description", "question_type", "sequence", "constr_mandatory", "is_page" }, new[] { "survey_id", "title", "question_type", "sequence" }, new[] { "title" }, new[] { "survey_id", "title", "question_type", "sequence", "constr_mandatory", "is_page" }, "contained_by_parent", "survey"));
            Add(values, Contract("survey_answer", "survey.question.answer", new[] { "survey" }, new[] { "question_id", "value", "sequence", "is_correct", "answer_score" }, new[] { "question_id", "value", "sequence" }, new[] { "value" }, new[] { "question_id", "value", "sequence", "is_correct", "answer_score" }, "contained_by_parent", "survey"));
            Add(values, Contract("survey_matrix_row", "survey.question.answer", new[] { "survey" }, new[] { "matrix_question_id", "value", "sequence" }, new[] { "matrix_question_id", "value", "sequence" }, new[] { "value" }, new[] { "matrix_question_id", "value", "sequence" }, "contained_by_parent", "survey"));

            // Catálogo operativo para demostraciones funcionales generales. El
            // modelo no se recibe desde MCP: cada tipo lógico se compila a uno
            // de estos contratos y a este conjunto fijo de campos.
            Add(values, Contract("business_company", "res.partner", new[] { "base" }, new[] { "name", "company_type", "email", "phone", "mobile", "city", "street", "vat", "customer_rank", "supplier_rank", "active" }, new[] { "name", "company_type" }, new[] { "name" }, new[] { "name", "company_type", "email", "customer_rank", "supplier_rank", "active" }, "deactivate", null));
            Add(values, Contract("business_contact", "res.partner", new[] { "base" }, new[] { "name", "company_type", "type", "parent_id", "email", "phone", "mobile", "function", "active" }, new[] { "name", "company_type", "parent_id" }, new[] { "name" }, new[] { "name", "parent_id", "email", "active" }, "deactivate", null));
            Add(values, Contract("analytic_plan", "account.analytic.plan", new[] { "analytic" }, new[] { "name", "parent_id", "description", "default_applicability" }, new[] { "name" }, new[] { "name" }, new[] { "name", "parent_id" }, "preserve", null));
            Add(values, Contract("analytic_account", "account.analytic.account", new[] { "analytic" }, new[] { "name", "plan_id", "partner_id", "code", "active" }, new[] { "name", "plan_id" }, new[] { "name" }, new[] { "name", "plan_id", "partner_id", "active" }, "deactivate", null));
            Add(values, Contract("analytic_distribution_rule", "account.analytic.distribution.model", new[] { "account" }, new[] { "sequence", "partner_id", "analytic_distribution" }, new[] { "sequence", "analytic_distribution" }, new string[0], new[] { "sequence", "partner_id", "analytic_distribution" }, "preserve", null));
            Add(values, Contract("demo_product", "product.product", new[] { "product" }, new[] { "name", "default_code", "barcode", "type", "is_storable", "tracking", "sale_ok", "purchase_ok", "list_price", "standard_price", "categ_id", "route_ids", "active" }, new[] { "name", "sale_ok", "purchase_ok" }, new[] { "default_code", "name" }, new[] { "name", "default_code", "type", "is_storable", "tracking", "sale_ok", "purchase_ok", "categ_id", "route_ids", "active" }, "deactivate", null));
            Add(values, Contract("demo_service_project_product", "product.product", new[] { "product", "sale_project" }, new[] { "name", "default_code", "type", "sale_ok", "purchase_ok", "list_price", "service_tracking", "project_template_id", "active" }, new[] { "name", "sale_ok", "service_tracking" }, new[] { "default_code", "name" }, new[] { "name", "default_code", "service_tracking", "project_template_id", "active" }, "deactivate", null));
            Add(values, Contract("landed_cost_product", "product.product", new[] { "stock_landed_costs" }, new[] { "name", "default_code", "type", "sale_ok", "purchase_ok", "standard_price", "landed_cost_ok", "split_method_landed_cost", "active" }, new[] { "name", "type", "landed_cost_ok", "split_method_landed_cost" }, new[] { "default_code", "name" }, new[] { "name", "default_code", "type", "landed_cost_ok", "split_method_landed_cost", "active" }, "deactivate", null));
            Add(values, Contract("valuation_product_category", "product.category", new[] { "stock_account" }, new[] { "name", "parent_id", "property_cost_method", "property_valuation", "property_stock_valuation_account_id", "property_stock_account_input_categ_id", "property_stock_account_output_categ_id" }, new[] { "name", "property_cost_method", "property_valuation" }, new[] { "name" }, new[] { "name", "parent_id", "property_cost_method", "property_valuation", "property_stock_valuation_account_id" }, "preserve", null));
            Add(values, Contract("supplier_price", "product.supplierinfo", new[] { "purchase" }, new[] { "partner_id", "product_id", "product_tmpl_id", "product_name", "product_code", "sequence", "min_qty", "price", "delay", "discount", "date_start", "date_end", "currency_id" }, new[] { "partner_id", "product_id", "price" }, new[] { "product_code" }, new[] { "partner_id", "product_id", "product_tmpl_id", "product_code", "min_qty", "price", "delay", "discount", "currency_id" }, "preserve", null));
            Add(values, Contract("product_pricelist", "product.pricelist", new[] { "product" }, new[] { "name", "currency_id", "sequence", "active" }, new[] { "name", "currency_id" }, new[] { "name" }, new[] { "name", "currency_id", "sequence", "active" }, "deactivate", null));
            Add(values, Contract("product_pricelist_item", "product.pricelist.item", new[] { "product" }, new[] { "pricelist_id", "applied_on", "product_id", "min_quantity", "compute_price", "fixed_price", "date_start", "date_end" }, new[] { "pricelist_id", "applied_on", "compute_price", "fixed_price" }, new string[0], new[] { "pricelist_id", "applied_on", "product_id", "min_quantity", "compute_price", "fixed_price" }, "contained_by_parent", "product_pricelist"));
            Add(values, Contract("project_project", "project.project", new[] { "project" }, new[] { "name", "partner_id", "account_id", "description", "privacy_visibility", "active" }, new[] { "name" }, new[] { "name" }, new[] { "name", "partner_id", "account_id", "active" }, "deactivate", null));
            Add(values, Contract("project_task", "project.task", new[] { "project" }, new[] { "name", "project_id", "partner_id", "description", "date_deadline", "planned_hours", "active" }, new[] { "name", "project_id" }, new[] { "name" }, new[] { "name", "project_id", "partner_id", "active" }, "deactivate", null));
            Add(values, Contract("crm_opportunity", "crm.lead", new[] { "crm" }, new[] { "name", "type", "partner_id", "contact_name", "partner_name", "email_from", "phone", "description", "team_id", "expected_revenue", "probability", "source_id", "medium_id", "campaign_id", "active" }, new[] { "name", "type" }, new[] { "name" }, new[] { "name", "type", "partner_id", "team_id", "active" }, "deactivate", null));
            Add(values, Contract("sale_quotation", "sale.order", new[] { "sale_management" }, new[] { "partner_id", "client_order_ref", "origin", "note", "date_order", "validity_date", "team_id", "opportunity_id", "campaign_id", "medium_id", "source_id", "pricelist_id", "fiscal_position_id", "carrier_id", "project_id", "project_account_id" }, new[] { "partner_id" }, new[] { "client_order_ref" }, new[] { "name", "state", "partner_id", "client_order_ref", "pricelist_id", "fiscal_position_id", "carrier_id", "project_id", "project_account_id", "amount_total" }, "preserve_business_document", null));
            Add(values, Contract("sale_order_line", "sale.order.line", new[] { "sale_management" }, new[] { "order_id", "product_id", "name", "product_uom_qty", "price_unit", "discount", "analytic_distribution" }, new[] { "order_id", "product_id", "name", "product_uom_qty", "price_unit" }, new string[0], new[] { "order_id", "product_id", "name", "product_uom_qty", "price_unit", "analytic_distribution" }, "contained_by_parent", "sale_order"));
            Add(values, Contract("purchase_rfq", "purchase.order", new[] { "purchase" }, new[] { "partner_id", "partner_ref", "origin", "notes", "date_order", "date_planned", "requisition_id" }, new[] { "partner_id" }, new[] { "partner_ref" }, new[] { "name", "state", "partner_id", "partner_ref", "requisition_id", "amount_total" }, "preserve_business_document", null));
            Add(values, Contract("purchase_order_line", "purchase.order.line", new[] { "purchase" }, new[] { "order_id", "product_id", "name", "product_qty", "price_unit", "date_planned", "discount", "analytic_distribution" }, new[] { "order_id", "product_id", "name", "product_qty", "price_unit" }, new string[0], new[] { "order_id", "product_id", "name", "product_qty", "price_unit", "analytic_distribution" }, "contained_by_parent", "purchase_order"));
            Add(values, Contract("purchase_agreement", "purchase.requisition", new[] { "purchase_requisition" }, new[] { "reference", "vendor_id", "requisition_type", "date_start", "date_end", "user_id", "description", "active" }, new[] { "reference", "requisition_type" }, new[] { "reference" }, new[] { "name", "reference", "vendor_id", "requisition_type", "date_start", "date_end", "state", "active" }, "deactivate", null));
            Add(values, Contract("purchase_agreement_line", "purchase.requisition.line", new[] { "purchase_requisition" }, new[] { "requisition_id", "product_id", "product_qty", "price_unit", "product_description_variants", "analytic_distribution" }, new[] { "requisition_id", "product_id", "product_qty", "price_unit" }, new string[0], new[] { "requisition_id", "product_id", "product_qty", "price_unit", "analytic_distribution" }, "contained_by_parent", "purchase_agreement"));
            Add(values, Contract("account_invoice_draft", "account.move", new[] { "account" }, new[] { "move_type", "partner_id", "invoice_date", "invoice_date_due", "ref", "narration", "journal_id", "invoice_origin" }, new[] { "move_type", "partner_id", "invoice_date", "ref" }, new[] { "ref" }, new[] { "name", "move_type", "state", "partner_id", "invoice_date", "ref", "amount_total" }, "preserve_business_document", null));
            Add(values, Contract("account_invoice_line", "account.move.line", new[] { "account" }, new[] { "move_id", "product_id", "name", "quantity", "price_unit", "discount", "account_id", "analytic_distribution" }, new[] { "move_id", "name", "quantity", "price_unit" }, new string[0], new[] { "move_id", "product_id", "name", "quantity", "price_unit", "account_id", "analytic_distribution" }, "contained_by_parent", "account_move"));
            Add(values, Contract("account_journal_entry_draft", "account.move", new[] { "account" }, new[] { "move_type", "date", "ref", "journal_id", "narration" }, new[] { "move_type", "date", "ref", "journal_id" }, new[] { "ref" }, new[] { "name", "move_type", "state", "date", "ref", "journal_id", "amount_total" }, "preserve_business_document", null));
            Add(values, Contract("account_journal_line", "account.move.line", new[] { "account" }, new[] { "move_id", "name", "account_id", "partner_id", "debit", "credit", "analytic_distribution" }, new[] { "move_id", "name", "account_id" }, new string[0], new[] { "move_id", "name", "account_id", "debit", "credit", "analytic_distribution" }, "contained_by_parent", "account_move"));
            Add(values, Contract("stock_picking_draft", "stock.picking", new[] { "stock" }, new[] { "partner_id", "picking_type_id", "location_id", "location_dest_id", "origin", "scheduled_date", "note", "move_type" }, new[] { "picking_type_id", "location_id", "location_dest_id", "origin" }, new[] { "origin" }, new[] { "name", "state", "partner_id", "picking_type_id", "location_id", "location_dest_id", "origin" }, "preserve_business_document", null));
            Add(values, Contract("stock_move_draft", "stock.move", new[] { "stock" }, new[] { "name", "description_picking", "picking_id", "product_id", "product_uom_qty", "product_uom", "location_id", "location_dest_id", "date" }, new[] { "picking_id", "product_id", "product_uom_qty", "location_id", "location_dest_id" }, new string[0], new[] { "name", "description_picking", "picking_id", "product_id", "product_uom_qty", "location_id", "location_dest_id", "state" }, "contained_by_parent", "stock_picking"));
            Add(values, Contract("stock_move_line_done", "stock.move.line", new[] { "stock" }, new[] { "picking_id", "move_id", "product_id", "product_uom_id", "quantity", "lot_id", "location_id", "location_dest_id", "picked" }, new[] { "picking_id", "move_id", "product_id", "quantity", "location_id", "location_dest_id" }, new string[0], new[] { "picking_id", "move_id", "product_id", "quantity", "lot_id", "location_id", "location_dest_id", "picked" }, "contained_by_parent", "stock_picking"));
            Add(values, Contract("stock_location", "stock.location", new[] { "stock" }, new[] { "name", "location_id", "usage", "barcode", "active" }, new[] { "name", "location_id", "usage" }, new[] { "name" }, new[] { "name", "location_id", "usage", "barcode", "active" }, "deactivate", null));
            Add(values, Contract("stock_warehouse", "stock.warehouse", new[] { "stock" }, new[] { "name", "code", "company_id", "reception_steps", "delivery_steps", "buy_to_resupply", "active" }, new[] { "name", "code", "company_id" }, new[] { "code", "name" }, new[] { "name", "code", "company_id", "reception_steps", "delivery_steps", "buy_to_resupply", "active" }, "deactivate", null));
            Add(values, Contract("stock_route", "stock.route", new[] { "stock" }, new[] { "name", "sequence", "company_id", "sale_selectable", "product_selectable", "warehouse_selectable", "active" }, new[] { "name" }, new[] { "name" }, new[] { "name", "sequence", "company_id", "sale_selectable", "product_selectable", "warehouse_selectable", "active" }, "deactivate", null));
            Add(values, Contract("stock_rule", "stock.rule", new[] { "stock" }, new[] { "name", "action", "route_id", "location_src_id", "location_dest_id", "picking_type_id", "procure_method", "delay", "propagate_cancel", "auto", "company_id", "active" }, new[] { "name", "action", "route_id", "location_dest_id" }, new[] { "name" }, new[] { "name", "action", "route_id", "location_src_id", "location_dest_id", "picking_type_id", "procure_method", "active" }, "deactivate", null));
            Add(values, Contract("stock_lot_expiring", "stock.lot", new[] { "stock", "product_expiry" }, new[] { "name", "product_id", "expiration_date", "use_date", "removal_date", "alert_date" }, new[] { "name", "product_id", "expiration_date" }, new[] { "name" }, new[] { "name", "product_id", "expiration_date", "use_date", "removal_date", "alert_date" }, "preserve", null));
            Add(values, Contract("delivery_method", "delivery.carrier", new[] { "delivery" }, new[] { "name", "delivery_type", "product_id", "fixed_price", "active" }, new[] { "name", "delivery_type", "product_id" }, new[] { "name" }, new[] { "name", "delivery_type", "product_id", "fixed_price", "active" }, "deactivate", null));
            Add(values, Contract("fleet_vehicle", "fleet.vehicle", new[] { "fleet" }, new[] { "model_id", "license_plate", "vin_sn", "driver_id", "odometer", "active" }, new[] { "model_id", "license_plate" }, new[] { "license_plate" }, new[] { "name", "model_id", "license_plate", "vin_sn", "driver_id", "active" }, "deactivate", null));
            Add(values, Contract("crm_activity", "mail.activity", new[] { "crm" }, new[] { "activity_type_id", "summary", "note", "date_deadline", "user_id", "res_model_id", "res_id" }, new[] { "activity_type_id", "summary", "date_deadline", "user_id", "res_model_id", "res_id" }, new[] { "summary" }, new[] { "activity_type_id", "summary", "date_deadline", "user_id", "res_model_id", "res_id" }, "preserve", null));
            Add(values, Contract("account_journal", "account.journal", new[] { "account" }, new[] { "name", "code", "type", "currency_id", "active" }, new[] { "name", "code", "type" }, new[] { "code" }, new[] { "name", "code", "type", "currency_id", "active" }, "deactivate", null));
            Add(values, Contract("account_payment_draft", "account.payment", new[] { "account" }, new[] { "payment_type", "partner_type", "partner_id", "amount", "date", "journal_id", "payment_method_line_id", "destination_account_id", "memo", "payment_reference", "currency_id", "invoice_ids" }, new[] { "payment_type", "partner_type", "partner_id", "amount", "date", "journal_id" }, new[] { "payment_reference" }, new[] { "name", "state", "payment_type", "partner_type", "partner_id", "amount", "date", "journal_id", "payment_reference", "invoice_ids", "is_reconciled" }, "preserve_business_document", null));
            Add(values, Contract("bank_statement_line", "account.bank.statement.line", new[] { "account" }, new[] { "journal_id", "partner_id", "payment_ref", "amount", "date" }, new[] { "journal_id", "payment_ref", "amount", "date" }, new[] { "payment_ref" }, new[] { "payment_ref", "amount", "date", "journal_id", "partner_id", "is_reconciled" }, "preserve_business_document", null));
            Add(values, Contract("stock_inventory_quant", "stock.quant", new[] { "stock" }, new[] { "product_id", "location_id", "lot_id", "inventory_quantity", "inventory_date", "user_id" }, new[] { "product_id", "location_id", "inventory_quantity" }, new string[0], new[] { "product_id", "location_id", "lot_id", "quantity", "inventory_quantity", "inventory_quantity_set" }, "preserve_business_document", null));
            Add(values, Contract("stock_landed_cost", "stock.landed.cost", new[] { "stock_landed_costs" }, new[] { "date", "target_model", "picking_ids", "account_journal_id", "vendor_bill_id", "description" }, new[] { "date", "target_model", "picking_ids", "account_journal_id" }, new string[0], new[] { "name", "date", "state", "picking_ids", "account_journal_id", "vendor_bill_id", "amount_total", "valuation_adjustment_lines" }, "preserve_business_document", null));
            Add(values, Contract("stock_landed_cost_line", "stock.landed.cost.lines", new[] { "stock_landed_costs" }, new[] { "cost_id", "name", "product_id", "price_unit", "split_method", "account_id" }, new[] { "cost_id", "name", "product_id", "price_unit", "split_method" }, new string[0], new[] { "cost_id", "name", "product_id", "price_unit", "split_method", "account_id" }, "contained_by_parent", "stock_landed_cost"));
            Add(values, Contract("maintenance_equipment", "maintenance.equipment", new[] { "maintenance" }, new[] { "name", "serial_no", "model", "note", "partner_id", "active" }, new[] { "name" }, new[] { "serial_no", "name" }, new[] { "name", "serial_no", "partner_id", "active" }, "deactivate", null));
            Add(values, Contract("maintenance_request", "maintenance.request", new[] { "maintenance" }, new[] { "name", "equipment_id", "description", "request_date", "schedule_date", "maintenance_type", "priority", "owner_user_id", "active" }, new[] { "name", "equipment_id", "maintenance_type" }, new[] { "name" }, new[] { "name", "equipment_id", "maintenance_type", "stage_id", "active" }, "deactivate", null));
            Add(values, Contract("fiscal_position", "account.fiscal.position", new[] { "account" }, new[] { "name", "active", "sequence", "note", "auto_apply", "vat_required", "country_id", "country_group_id", "state_ids", "zip_from", "zip_to", "foreign_vat" }, new[] { "name" }, new[] { "name" }, new[] { "name", "active", "sequence", "auto_apply" }, "deactivate", null));
            Add(values, Contract("fiscal_position_account_map", "account.fiscal.position.account", new[] { "account" }, new[] { "position_id", "account_src_id", "account_dest_id" }, new[] { "position_id", "account_src_id", "account_dest_id" }, new string[0], new[] { "position_id", "account_src_id", "account_dest_id" }, "contained_by_parent", "fiscal_position"));
            Add(values, Contract("fiscal_position_tax_map", "account.tax", new[] { "account" }, new[] { "name", "amount", "amount_type", "type_tax_use", "company_id", "country_id", "original_tax_ids", "fiscal_position_ids", "active" }, new[] { "name", "amount", "amount_type", "type_tax_use", "original_tax_ids", "fiscal_position_ids" }, new[] { "name" }, new[] { "name", "amount", "amount_type", "type_tax_use", "original_tax_ids", "fiscal_position_ids", "active" }, "deactivate", null));
            Add(values, Contract("fleet_vehicle_brand", "fleet.vehicle.model.brand", new[] { "fleet" }, new[] { "name", "active" }, new[] { "name" }, new[] { "name" }, new[] { "name", "active" }, "deactivate", null));
            Add(values, Contract("fleet_vehicle_model", "fleet.vehicle.model", new[] { "fleet" }, new[] { "name", "brand_id", "active" }, new[] { "name", "brand_id" }, new[] { "name" }, new[] { "name", "brand_id", "active" }, "deactivate", null));
            Add(values, Contract("crm_lost_reason", "crm.lost.reason", new[] { "crm" }, new[] { "name", "active" }, new[] { "name" }, new[] { "name" }, new[] { "name", "active" }, "deactivate", null));
            Add(values, Contract("demo_private_attachment", "ir.attachment", new[] { "base" }, new[] { "name", "type", "datas", "mimetype", "public", "res_model", "res_id", "description" }, new[] { "name", "type", "datas", "mimetype", "public" }, new[] { "name" }, new[] { "name", "mimetype", "public", "res_model", "res_id" }, "detach_and_unpublish", null));
            Add(values, Contract("commission_plan", "sale.commission.plan", new[] { "sale_commission" }, new[] { "name", "active", "state", "company_id", "type", "periodicity", "date_from", "date_to", "commission_amount" }, new[] { "name", "state", "company_id", "type", "periodicity", "date_from", "date_to" }, new[] { "name" }, new[] { "name", "active", "state", "type", "periodicity", "date_from", "date_to", "commission_amount" }, "deactivate", null));
            Add(values, Contract("commission_target", "sale.commission.plan.target", new[] { "sale_commission" }, new[] { "plan_id", "amount", "payment_date" }, new[] { "plan_id", "amount" }, new string[0], new[] { "plan_id", "date_from", "date_to", "amount", "payment_date" }, "contained_by_parent", "commission_plan"));
            Add(values, Contract("commission_level", "sale.commission.plan.target.commission", new[] { "sale_commission" }, new[] { "plan_id", "target_rate", "amount_rate", "amount" }, new[] { "plan_id", "target_rate", "amount" }, new string[0], new[] { "plan_id", "target_rate", "amount_rate", "amount" }, "contained_by_parent", "commission_plan"));
            Add(values, Contract("commission_achievement", "sale.commission.plan.achievement", new[] { "sale_commission" }, new[] { "plan_id", "type", "product_id", "product_categ_id", "rate" }, new[] { "plan_id", "type", "rate" }, new string[0], new[] { "plan_id", "type", "product_id", "product_categ_id", "rate" }, "contained_by_parent", "commission_plan"));
            Add(values, Contract("commission_participant", "sale.commission.plan.user", new[] { "sale_commission" }, new[] { "plan_id", "user_id", "date_from", "date_to" }, new[] { "plan_id", "user_id" }, new string[0], new[] { "plan_id", "user_id", "date_from", "date_to" }, "contained_by_parent", "commission_plan"));
            Add(values, Contract("analytic_budget", "budget.analytic", new[] { "account_budget" }, new[] { "name", "user_id", "company_id", "date_from", "date_to", "budget_type" }, new[] { "name", "date_from", "date_to", "budget_type" }, new[] { "name" }, new[] { "name", "state", "date_from", "date_to", "budget_type" }, "preserve_business_document", null));
            Add(values, Contract("analytic_budget_line", "budget.line", new[] { "account_budget" }, new[] { "budget_analytic_id", "account_id", "auto_account_id", "sequence", "budget_amount" }, new[] { "budget_analytic_id", "budget_amount" }, new string[0], new[] { "budget_analytic_id", "account_id", "auto_account_id", "sequence", "budget_amount" }, "contained_by_parent", "analytic_budget"));
            Add(values, Contract("account_asset_demo", "account.asset", new[] { "account_asset" }, new[] { "name", "active", "company_id", "model_id", "method", "method_number", "method_period", "original_value", "salvage_value", "acquisition_date", "journal_id", "analytic_distribution" }, new[] { "name", "company_id", "original_value", "acquisition_date" }, new[] { "name" }, new[] { "name", "active", "state", "method", "method_number", "method_period", "original_value", "salvage_value", "acquisition_date" }, "preserve_business_document", null));
            Add(values, Contract("account_loan_demo", "account.loan", new[] { "account_loans" }, new[] { "name", "active", "state", "company_id", "date", "amount_borrowed", "interest", "duration", "skip_until_date", "long_term_account_id", "short_term_account_id", "expense_account_id", "journal_id", "asset_group_id" }, new[] { "name", "company_id", "state", "amount_borrowed", "duration" }, new[] { "name" }, new[] { "name", "active", "state", "amount_borrowed", "interest", "duration", "start_date", "end_date", "line_ids" }, "preserve_business_document", null));
            Add(values, Contract("account_loan_line_demo", "account.loan.line", new[] { "account_loans" }, new[] { "loan_id", "date", "principal", "interest" }, new[] { "loan_id", "date" }, new string[0], new[] { "loan_id", "date", "principal", "interest" }, "contained_by_parent", "account_loan"));
            return values;
        }

        private static DemoAdapterContract Contract(string key, string model, string[] modules, string[] allowed, string[] required, string[] identity, string[] verify, string cleanup, string containedBy)
        {
            return new DemoAdapterContract { key = key, model = model, modules = modules, allowed_fields = allowed, required_contract_fields = required, identity_fields = identity, verification_fields = verify, cleanup_strategy = cleanup, contained_by = containedBy };
        }

        private static void Add(IDictionary<string, DemoAdapterContract> target, DemoAdapterContract value) { target.Add(value.key, value); }
    }
}
