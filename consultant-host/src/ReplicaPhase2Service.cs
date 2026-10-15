using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace OdooConsultor
{
    public sealed class ReplicaPhase2Service
    {
        public const string PolicyVersion = "replica-policy-2";
        private static readonly Regex ModelName = new Regex("^x_[a-z0-9_.]{2,60}$", RegexOptions.Compiled);
        private static readonly Regex FieldName = new Regex("^x_[a-z0-9_]{2,60}$", RegexOptions.Compiled);
        private static readonly Regex ReferenceKey = new Regex("^[A-Z][A-Z0-9_]{1,62}$", RegexOptions.Compiled);
        private static readonly HashSet<string> FieldTypes = new HashSet<string>(new[] { "char", "text", "html", "integer", "float", "monetary", "date", "datetime", "boolean", "binary", "selection", "many2one", "one2many", "many2many" }, StringComparer.Ordinal);
        private static readonly HashSet<string> ComponentModes = new HashSet<string>(new[] { "create", "update", "deactivate" }, StringComparer.Ordinal);
        private static readonly HashSet<string> ViewTypes = new HashSet<string>(new[] { "list", "form", "kanban", "search", "calendar", "graph", "pivot", "activity", "map", "cohort", "gantt" }, StringComparer.Ordinal);
        private readonly ProjectStore project;

        public ReplicaPhase2Service(ProjectStore value) { project = value; }

        public static bool IsPhase2(ProjectStore project, string slug)
        {
            string root;
            try { root = project.ReplicaPath(slug); } catch { return false; }
            string path = Path.Combine(root, "manifest.json");
            if (!File.Exists(path)) return false;
            Dictionary<string, object> raw;
            try { raw = JsonUtil.ReadJsonFile<Dictionary<string, object>>(path); } catch { return false; }
            return JsonUtil.GetLong(raw, "schema_version", false) == 2 && JsonUtil.GetLong(raw, "phase", false) == 2;
        }

        public object Analyze(string environment, string slug)
        {
            environment = ProjectStore.NormalizeEnvironment(environment);
            Manifest manifest = Load(slug);
            using (OdooClient client = Open(environment))
            {
                Analysis analysis = AnalyzeInternal(client, manifest);
                AuditLog.Append(project, environment, "replica_analizar_destino", null, analysis.blocked.Count == 0 ? "success" : "blocked", "Réplica Fase 2 analizada; componentes=" + analysis.components.Count.ToString(CultureInfo.InvariantCulture) + "; bloqueos=" + analysis.blocked.Count.ToString(CultureInfo.InvariantCulture) + ".", 0);
                return JsonUtil.Dictionary("environment", environment, "replica_slug", slug, "phase", 2, "manifest_hash", manifest.hash, "classification", analysis.blocked.Count == 0 ? "ready" : "blocked", "components", analysis.components, "blocked", analysis.blocked, "required_references", manifest.requiredReferences.OrderBy(delegate(string x) { return x; }, StringComparer.Ordinal).ToArray(), "precondition_hash", analysis.preconditionHash, "capability_hash", analysis.capabilityHash);
            }
        }

        internal ChangeSet PreparePromotion(string slug, string backupReference, IDictionary<string, object> references)
        {
            Manifest manifest = Load(slug);
            ConnectionProfile profile = project.GetProfile("production", true);
            using (OdooClient client = Open("production"))
            {
                Analysis analysis = AnalyzeInternal(client, manifest);
                if (analysis.blocked.Count > 0) throw new ConsultantException("replica_blocked", "La réplica Fase 2 tiene componentes bloqueados en producción: " + string.Join("; ", analysis.blocked.ToArray()));
                string id = "prom-" + Guid.NewGuid().ToString("N");
                DateTimeOffset now = DateTimeOffset.UtcNow;
                ChangeSet change = new ChangeSet
                {
                    schema_version = 3,
                    id = id,
                    client_slug = project.Config.client_slug,
                    environment = "production",
                    category = "production_promotion",
                    risk = "production_controlled_promotion",
                    status = "prepared",
                    backup_reference = backupReference,
                    model = manifest.targetModels.First(),
                    target_models = manifest.targetModels.OrderBy(delegate(string x) { return x; }, StringComparer.Ordinal).ToList(),
                    replica_slug = manifest.slug,
                    replica_manifest_hash = manifest.hash,
                    replica_descriptor = JsonUtil.Dictionary("manifest_schema", 2, "phase", 2, "required_references", manifest.requiredReferences.OrderBy(delegate(string x) { return x; }, StringComparer.Ordinal).ToArray()),
                    created_at = now.ToString("o", CultureInfo.InvariantCulture),
                    expires_at = now.AddHours(2).ToString("o", CultureInfo.InvariantCulture),
                    precondition_hash = analysis.preconditionHash,
                    capability_hash = analysis.capabilityHash,
                    profile_fingerprint = profile.credential_fingerprint,
                    requested_by = Environment.UserName,
                    policy_version = PolicyVersion,
                    objective = "Promover a producción de forma segura la réplica Fase 2 " + manifest.slug + ".",
                    contract_hash = manifest.hash,
                    impact = "Crea modelos, campos, vistas, acciones de ventana y menús, o actualiza artefactos Studio.",
                    rollback_strategy = "Restaura snapshots de actualizaciones y desactiva vistas/menús creados. Modelos y campos se preservan para no perder datos.",
                    preconditions = new List<Dictionary<string, object>> { JsonUtil.Dictionary("type", "destination_hash", "expected", analysis.preconditionHash), JsonUtil.Dictionary("type", "capabilities_hash", "expected", analysis.capabilityHash) },
                    validations = new List<Dictionary<string, object>> { JsonUtil.Dictionary("type", "schema_2_phase_2", "required", true), JsonUtil.Dictionary("type", "previous_hash_for_updates", "required", true), JsonUtil.Dictionary("type", "files_sha256", "required", true) },
                    operations = Compile(manifest, analysis),
                    warnings = new List<string> { "Producción: Se requiere confirmación reforzada con respaldo verificado.", "Respaldo registrado: " + backupReference, "Ventana de vigencia reducida: 2 horas.", "Confirmación exacta requerida: PROMOVER-PRODUCCION " + id + " RESPALDO-CONFIRMADO" }
                };
                change.integrity_hash = Integrity(change);
                ValidatePackage(change);
                JsonUtil.WriteJsonFile(project.ChangeSetPath(id), change);
                AuditLog.Append(project, "production", "promocion_preparar_paquete", id, "success", "Paquete de promoción Fase 2 preparado; operaciones=" + change.operations.Count.ToString(CultureInfo.InvariantCulture) + "; respaldo=" + backupReference + ".", 0);
                return change;
            }
        }

        public object Prepare(string environment, string slug)
        {
            environment = ProjectStore.NormalizeEnvironment(environment);
            Manifest manifest = Load(slug);
            if (environment == "production") return ProductionRunbook(manifest);
            StudioService.EnsureWriteAllowed(environment);
            ConnectionProfile profile = project.GetProfile(environment, true);
            using (OdooClient client = Open(environment))
            {
                Analysis analysis = AnalyzeInternal(client, manifest);
                if (analysis.blocked.Count > 0) throw new ConsultantException("replica_blocked", "La réplica tiene componentes bloqueados: " + string.Join("; ", analysis.blocked.ToArray()));
                string id = "chg-" + Guid.NewGuid().ToString("N");
                DateTimeOffset now = DateTimeOffset.UtcNow;
                ChangeSet change = new ChangeSet
                {
                    schema_version = 3,
                    id = id,
                    client_slug = project.Config.client_slug,
                    environment = environment,
                    category = "validated_replica_phase2",
                    risk = "high_controlled_staging",
                    status = "prepared",
                    model = manifest.targetModels.First(),
                    target_models = manifest.targetModels.OrderBy(delegate(string x) { return x; }, StringComparer.Ordinal).ToList(),
                    replica_slug = manifest.slug,
                    replica_manifest_hash = manifest.hash,
                    replica_descriptor = JsonUtil.Dictionary("manifest_schema", 2, "phase", 2, "required_references", manifest.requiredReferences.ToArray()),
                    created_at = now.ToString("o", CultureInfo.InvariantCulture),
                    expires_at = now.AddMinutes(project.Config.package_confirmation_minutes).ToString("o", CultureInfo.InvariantCulture),
                    precondition_hash = analysis.preconditionHash,
                    capability_hash = analysis.capabilityHash,
                    profile_fingerprint = profile.credential_fingerprint,
                    requested_by = Environment.UserName,
                    policy_version = PolicyVersion,
                    objective = "Crear o actualizar la app Studio declarada por la réplica " + manifest.slug + ".",
                    contract_hash = manifest.hash,
                    impact = "Crea modelos, campos, vistas, acciones de ventana y menús, o actualiza artefactos personalizados cuya huella previa coincide.",
                    rollback_strategy = "Restaura snapshots de actualizaciones y desactiva vistas/menús creados. Modelos y campos se preservan para no perder datos.",
                    preconditions = new List<Dictionary<string, object>> { JsonUtil.Dictionary("type", "destination_hash", "expected", analysis.preconditionHash), JsonUtil.Dictionary("type", "capabilities_hash", "expected", analysis.capabilityHash) },
                    validations = new List<Dictionary<string, object>> { JsonUtil.Dictionary("type", "schema_2_phase_2", "required", true), JsonUtil.Dictionary("type", "previous_hash_for_updates", "required", true), JsonUtil.Dictionary("type", "files_sha256", "required", true) },
                    operations = Compile(manifest, analysis),
                    warnings = new List<string> { "Los modelos y campos nunca se eliminan automáticamente.", "Los cambios estructurales de tipo, relación o tabla en campos existentes están bloqueados.", "Confirmación exacta: APLICAR " + id + "." }
                };
                change.integrity_hash = Integrity(change);
                ValidatePackage(change);
                JsonUtil.WriteJsonFile(project.ChangeSetPath(id), change);
                AuditLog.Append(project, environment, "replica_preparar_paquete", id, "success", "Paquete Fase 2 preparado; operaciones=" + change.operations.Count.ToString(CultureInfo.InvariantCulture) + ".", 0);
                return Summary(change, true);
            }
        }

        public object Apply(ChangeSet change, string confirmation, IDictionary<string, object> references)
        {
            ValidatePackage(change);
            StudioService.EnsureWriteAllowed(change.environment, change.category);
            if (change.status == "applied" || change.status == "verified") return Summary(change, false);
            if (change.status != "prepared" && change.status != "failed_partial") throw new ConsultantException("package_not_applicable", "El paquete Fase 2 no se puede aplicar en su estado actual.");
            ValidateConfirmation(change, confirmation, "APLICAR");
            ConnectionProfile profile = project.GetProfile(change.environment, true);
            if (!string.Equals(profile.credential_fingerprint, change.profile_fingerprint, StringComparison.Ordinal)) throw new ConsultantException("profile_changed", "La credencial cambió desde la preparación.");
            Manifest manifest = Load(change.replica_slug);
            if (manifest.hash != change.replica_manifest_hash) throw new ConsultantException("replica_integrity_failed", "El manifiesto o uno de sus archivos cambió desde la preparación.");
            Dictionary<string, string> supplied = ValidateReferences(manifest, references);
            using (OdooClient client = Open(change.environment))
            {
                Dictionary<string, long> resolvedReferences = ResolveReferences(client, supplied);
                ValidateReferenceMenuState(client, change, resolvedReferences);
                foreach (ChangeOperation menu in change.operations.Where(delegate(ChangeOperation x) { return x.type.Contains("menu"); }))
                {
                    string xmlid = JsonUtil.GetString(menu.parameters, "parent_xml_id", false);
                    if (!string.IsNullOrWhiteSpace(xmlid)) resolvedReferences["xmlid:" + xmlid] = ResolveXmlId(client, xmlid, "ir.ui.menu");
                }
                Analysis current = AnalyzeInternal(client, manifest);
                if (change.status == "prepared" && (current.preconditionHash != change.precondition_hash || current.capabilityHash != change.capability_hash)) throw new ConsultantException("precondition_changed", "La configuración o los permisos cambiaron desde la vista previa.");
                if (change.status == "failed_partial") ValidateResume(client, change, current);
                change.status = "applying"; Save(change);
                try
                {
                    foreach (ChangeOperation operation in change.operations)
                    {
                        if (operation.status == "applied" || operation.status == "reused" || operation.status == "preserved_noop") continue;
                        ApplyOperation(client, manifest, change, operation, resolvedReferences);
                        Save(change);
                    }
                    change.status = "applied";
                    change.applied_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    change.last_error = null; Save(change);
                }
                catch (Exception error)
                {
                    change.status = "failed_partial"; change.last_error = SafeError(error); Save(change); throw;
                }
            }
            AuditLog.Append(project, change.environment, "studio_aplicar_paquete", change.id, "success", "Paquete Fase 2 aplicado; operaciones=" + change.operations.Count.ToString(CultureInfo.InvariantCulture) + ".", 0);
            return Summary(change, false);
        }

        public object Verify(ChangeSet change)
        {
            ValidatePackage(change);
            if (change.status != "applied" && change.status != "verified" && change.status != "rolled_back" && change.status != "rollback_partial") throw new ConsultantException("package_not_verifiable", "El paquete todavía no se ha aplicado.");
            bool all = true;
            using (OdooClient client = Open(change.environment))
            {
                foreach (ChangeOperation operation in change.operations)
                {
                    bool ok = VerifyOperation(client, change, operation, change.status);
                    operation.verification = ok ? "verified" : "mismatch";
                    if (!ok && operation.status != "preserved_noop") all = false;
                }
            }
            if (all && change.status == "applied") change.status = "verified";
            change.verified_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture); Save(change);
            return Summary(change, false);
        }

        public object Rollback(ChangeSet change, string confirmation)
        {
            ValidatePackage(change); StudioService.EnsureWriteAllowed(change.environment);
            if (!string.Equals(confirmation, "REVERTIR " + change.id, StringComparison.Ordinal)) throw new ConsultantException("confirmation_mismatch", "La confirmación debe ser exactamente REVERTIR " + change.id + ".");
            if (change.status == "rolled_back" || change.status == "rollback_partial") return Summary(change, false);
            if (change.status != "applied" && change.status != "verified") throw new ConsultantException("package_not_reversible", "Solo se pueden revertir paquetes aplicados.");
            bool partial = false;
            using (OdooClient client = Open(change.environment))
            {
                foreach (ChangeOperation operation in change.operations.AsEnumerable().Reverse())
                {
                    if (operation.status == "reused" || operation.status == "preserved_noop") continue;
                    Dictionary<string, object> previous = Dict(operation.parameters, "previous_values", false);
                    string technicalModel = TechnicalModel(operation.type);
                    if (operation.type == "update_custom_field" && JsonUtil.GetBool(operation.parameters, "structural_change", false))
                    {
                        operation.status = "preserved_manual_cleanup"; partial = true;
                    }
                    else if (operation.type.StartsWith("update_", StringComparison.Ordinal) && previous != null && operation.result_id > 0)
                        operation.status = client.Write(technicalModel, new[] { operation.result_id }, RestorableValues(technicalModel, previous)) ? "rolled_back" : "rollback_failed";
                    else if ((operation.type == "deactivate_view" || operation.type == "deactivate_menu") && previous != null && operation.result_id > 0)
                        operation.status = client.Write(technicalModel, new[] { operation.result_id }, RestorableValues(technicalModel, previous)) ? "rolled_back" : "rollback_failed";
                    else if (operation.type == "upsert_selection_option" && previous != null && operation.result_id > 0)
                        operation.status = client.Write("ir.model.fields.selection", new[] { operation.result_id }, RestorableValues("ir.model.fields.selection", previous)) ? "rolled_back" : "rollback_failed";
                    else if ((operation.type == "create_view" || operation.type == "create_menu") && operation.result_id > 0)
                        operation.status = client.Write(technicalModel, new[] { operation.result_id }, JsonUtil.Dictionary("active", false)) ? "rolled_back" : "rollback_failed";
                    else { operation.status = "preserved_manual_cleanup"; partial = true; }
                    if (operation.status == "rollback_failed") partial = true;
                }
            }
            change.status = partial ? "rollback_partial" : "rolled_back"; change.rolled_back_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture); Save(change);
            return Summary(change, false);
        }

        public static void ValidatePackage(ChangeSet change)
        {
            if (change == null || change.schema_version != 3 || (change.category != "validated_replica_phase2" && change.category != "production_promotion") || change.policy_version != PolicyVersion) throw new ConsultantException("unsupported_package_schema", "El paquete no corresponde a una réplica Fase 2.");
            if (change.operations == null || change.operations.Count == 0 || change.operations.Count > 200 || change.target_models == null || change.target_models.Count == 0) throw new ConsultantException("invalid_package", "El paquete Fase 2 está incompleto.");
            if (change.integrity_hash != Integrity(change)) throw new ConsultantException("package_integrity_failed", "La parte inmutable del paquete Fase 2 fue modificada.");
            HashSet<string> allowed = new HashSet<string>(new[] { "create_custom_model", "update_custom_model", "preserve_custom_model", "create_custom_field", "update_custom_field", "preserve_custom_field", "upsert_selection_option", "create_view", "update_view", "deactivate_view", "create_window_action", "update_window_action", "preserve_window_action", "create_menu", "update_menu", "deactivate_menu" }, StringComparer.Ordinal);
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (ChangeOperation operation in change.operations)
            {
                if (!allowed.Contains(operation.type) || string.IsNullOrWhiteSpace(operation.operation_id) || !ids.Add(operation.operation_id)) throw new ConsultantException("tool_contract_violation", "El paquete Fase 2 contiene una operación no permitida o repetida.");
                StudioService.ValidateModel(operation.model);
            }
        }

        private Manifest Load(string slug)
        {
            string root = project.ReplicaPath(slug);
            Dictionary<string, object> raw = JsonUtil.ReadJsonFile<Dictionary<string, object>>(Path.Combine(root, "manifest.json"));
            if (JsonUtil.GetLong(raw, "schema_version", true) != 2 || JsonUtil.GetLong(raw, "phase", true) != 2) throw new ConsultantException("unsupported_replica_schema", "La Fase 2 exige schema_version 2 y phase 2.");
            EnsureAllowedKeys(raw, new[] { "schema_version", "phase", "name", "description", "source", "dependencies", "models", "fields", "views", "window_actions", "menus", "deployment_order", "excluded_artifacts" }, "manifest.json Fase 2");
            Dictionary<string, object> source = Dict(raw, "source", true);
            EnsureAllowedKeys(source, new[] { "environment", "odoo_version", "captured_on", "contains_business_records", "metadata_hashes" }, "source");
            if (JsonUtil.GetBool(source, "contains_business_records", true)) throw new ConsultantException("business_records_forbidden", "La réplica no puede contener registros de negocio.");
            string sourceEnvironment = JsonUtil.GetString(source, "environment", false); if (!string.IsNullOrWhiteSpace(sourceEnvironment) && sourceEnvironment != "staging" && sourceEnvironment != "production") throw new ConsultantException("invalid_source_environment", "source.environment debe ser staging o production.");
            string sourceVersion = JsonUtil.GetString(source, "odoo_version", false); if (!string.IsNullOrWhiteSpace(sourceVersion) && sourceVersion != "19") throw new ConsultantException("unsupported_odoo_version", "La réplica Fase 2 solo admite metadatos de Odoo 19.");
            Dictionary<string, object> metadataHashes = Dict(source, "metadata_hashes", false);
            if (metadataHashes != null) foreach (KeyValuePair<string, object> item in metadataHashes) { StudioService.ValidateModel(item.Key); if (!Regex.IsMatch(Convert.ToString(item.Value, CultureInfo.InvariantCulture) ?? string.Empty, "^[a-f0-9]{64}$")) throw new ConsultantException("invalid_metadata_hash", "source.metadata_hashes contiene un hash inválido."); }
            Dictionary<string, object> dependencies = Dict(raw, "dependencies", false);
            if (dependencies != null)
            {
                EnsureAllowedKeys(dependencies, new[] { "models", "features" }, "dependencies");
                foreach (string model in StringList(dependencies, "models")) StudioService.ValidateModel(model);
                StringList(dependencies, "features");
            }
            List<string> deploymentOrder = StringList(raw, "deployment_order");
            string[] expectedOrder = new[] { "models", "fields", "views", "window_actions", "menus" };
            if (deploymentOrder.Count > 0 && !deploymentOrder.SequenceEqual(expectedOrder)) throw new ConsultantException("invalid_deployment_order", "deployment_order debe respetar models, fields, views, window_actions y menus.");
            Manifest result = new Manifest { slug = slug, root = root, raw = raw, models = DictList(raw, "models"), fields = DictList(raw, "fields"), views = DictList(raw, "views"), actions = DictList(raw, "window_actions"), menus = DictList(raw, "menus"), targetModels = new List<string>(), requiredReferences = new HashSet<string>(StringComparer.Ordinal) };
            if (result.models.Count + result.fields.Count + result.views.Count + result.actions.Count + result.menus.Count == 0) throw new ConsultantException("empty_replica", "La réplica Fase 2 no contiene componentes.");
            foreach (Dictionary<string, object> model in result.models)
            {
                EnsureAllowedKeys(model, new[] { "key", "model", "name", "mode", "previous_hash", "order", "info", "fold_name" }, "models");
                EnsureKeys(model, new[] { "key", "model", "name" }); string technical = JsonUtil.GetString(model, "model", true); if (!ModelName.IsMatch(technical)) throw new ConsultantException("invalid_custom_model", "Los modelos personalizados deben comenzar por x_."); string mode = Mode(model, false); if (mode != "create") RequirePreviousHash(model); ValidateOrder(JsonUtil.GetString(model, "order", false) ?? "id"); Add(result.targetModels, technical);
            }
            foreach (Dictionary<string, object> field in result.fields) ValidateField(result, field);
            foreach (Dictionary<string, object> view in result.views) ValidateView(result, view);
            foreach (Dictionary<string, object> action in result.actions) ValidateAction(result, action);
            foreach (Dictionary<string, object> menu in result.menus) ValidateMenu(result, menu);
            ValidateLogicalReferences(result);
            if (result.targetModels.Count == 0) throw new ConsultantException("missing_target_model", "La réplica Fase 2 debe declarar al menos un modelo o una acción de ventana asociada a un modelo.");
            List<string> excluded = StringList(raw, "excluded_artifacts");
            if (excluded.Count == 0 || !excluded.Any(delegate(string value) { return Regex.IsMatch(value, "business|negocio|record", RegexOptions.IgnoreCase); })) throw new ConsultantException("business_exclusion_required", "excluded_artifacts debe excluir explícitamente los registros de negocio.");
            if (raw.ContainsKey("actions") || raw.ContainsKey("automations") || raw.ContainsKey("server_action")) throw new ConsultantException("phase_not_available", "Las acciones de servidor y automatizaciones deben permanecer en una réplica Fase 1 o esperar la Fase 3.");
            result.hash = JsonUtil.CanonicalHash(JsonUtil.Dictionary("manifest", raw, "file_hashes", result.fileHashes));
            return result;
        }

        private void ValidateField(Manifest manifest, Dictionary<string, object> field)
        {
            EnsureAllowedKeys(field, new[] { "key", "model", "name", "label", "type", "mode", "previous_hash", "help", "required", "readonly", "stored", "copied", "indexed", "relation", "relation_field", "relation_table", "column1", "column2", "domain", "related", "depends", "currency_field", "on_delete", "translate", "group_expand", "selectable", "sanitize", "sanitize_tags", "sanitize_attributes", "sanitize_style", "sanitize_form", "strip_style", "strip_classes", "filename_field", "selection", "compute_file", "compute_sha256" }, "fields");
            EnsureKeys(field, new[] { "key", "model", "name", "label", "type" });
            string model = JsonUtil.GetString(field, "model", true); StudioService.ValidateModel(model); Add(manifest.targetModels, model);
            string name = JsonUtil.GetString(field, "name", true); if (!FieldName.IsMatch(name)) throw new ConsultantException("invalid_custom_field", "El campo personalizado debe comenzar por x_.");
            if (name == "x_name" && manifest.models.Any(delegate(Dictionary<string, object> x) { return JsonUtil.GetString(x, "model", true) == model && Mode(x, false) == "create"; })) throw new ConsultantException("default_name_field", "Odoo crea x_name automáticamente en modelos nuevos; no debe repetirse en fields.");
            string type = JsonUtil.GetString(field, "type", true); if (!FieldTypes.Contains(type)) throw new ConsultantException("unsupported_field_type", "Tipo de campo no soportado en Fase 2: " + type + ".");
            string mode = Mode(field, false); if (mode != "create") RequirePreviousHash(field);
            string relation = JsonUtil.GetString(field, "relation", false);
            if (type == "many2one" || type == "one2many" || type == "many2many") { StudioService.ValidateModel(relation); Add(manifest.targetModels, relation); }
            if (type == "one2many" && string.IsNullOrWhiteSpace(JsonUtil.GetString(field, "relation_field", false))) throw new ConsultantException("relation_field_required", "one2many requiere relation_field.");
            if (type == "many2many") { ValidatePgName(JsonUtil.GetString(field, "relation_table", true), "relation_table"); ValidatePgName(JsonUtil.GetString(field, "column1", true), "column1"); ValidatePgName(JsonUtil.GetString(field, "column2", true), "column2"); }
            if (type == "binary" && !string.IsNullOrWhiteSpace(JsonUtil.GetString(field, "filename_field", false)) && !FieldName.IsMatch(JsonUtil.GetString(field, "filename_field", true))) throw new ConsultantException("invalid_filename_field", "filename_field no es válido.");
            if (type == "monetary" && string.IsNullOrWhiteSpace(JsonUtil.GetString(field, "currency_field", false))) throw new ConsultantException("currency_field_required", "monetary requiere currency_field.");
            string related = JsonUtil.GetString(field, "related", false); if (!string.IsNullOrWhiteSpace(related) && !Regex.IsMatch(related, "^[a-z0-9_]+(?:\\.[a-z0-9_]+)*$")) throw new ConsultantException("invalid_related_path", "La ruta related no es válida.");
            string domain = JsonUtil.GetString(field, "domain", false); if (!string.IsNullOrWhiteSpace(domain)) ValidateExpression(domain, '[', ']', "domain");
            if (type == "many2one" && JsonUtil.GetBool(field, "required", false) && (JsonUtil.GetString(field, "on_delete", false) ?? "set null") == "set null") throw new ConsultantException("invalid_on_delete", "Un many2one requerido debe usar on_delete restrict o cascade.");
            string computeFile = JsonUtil.GetString(field, "compute_file", false);
            if (!string.IsNullOrWhiteSpace(computeFile)) { if (mode != "create") throw new ConsultantException("computed_update_requires_phase3", "La Fase 2 crea campos calculados firmados, pero no reemplaza código calculado existente."); ValidateFile(manifest, computeFile, JsonUtil.GetString(field, "compute_sha256", true)); if (string.IsNullOrWhiteSpace(JsonUtil.GetString(field, "depends", false))) throw new ConsultantException("compute_depends_required", "Un campo calculado requiere depends."); }
            if (type == "selection")
            {
                List<Dictionary<string, object>> options = DictList(field, "selection");
                if (mode != "deactivate" && options.Count == 0) throw new ConsultantException("selection_values_required", "Un campo selection requiere valores técnicos declarados.");
                HashSet<string> values = new HashSet<string>(StringComparer.Ordinal); HashSet<long> sequences = new HashSet<long>();
                foreach (Dictionary<string, object> option in options)
                {
                    EnsureAllowedKeys(option, new[] { "value", "label", "sequence" }, "selection");
                    EnsureKeys(option, new[] { "value", "label" });
                    long sequence = JsonUtil.GetLong(option, "sequence", false); if (sequence < 0 || !sequences.Add(sequence)) throw new ConsultantException("invalid_selection_sequence", "Las secuencias de una selección deben ser únicas y no negativas.");
                    if (!values.Add(JsonUtil.GetString(option, "value", true))) throw new ConsultantException("duplicate_selection_value", "La selección repite un valor técnico.");
                }
            }
        }

        private void ValidateView(Manifest manifest, Dictionary<string, object> view)
        {
            EnsureAllowedKeys(view, new[] { "key", "model", "type", "operation", "previous_hash", "inherit_view_name", "inherit_view_xml_id", "arch_template", "arch_file", "arch_sha256", "priority", "active" }, "views");
            EnsureKeys(view, new[] { "key", "model", "type" }); string model = JsonUtil.GetString(view, "model", true); StudioService.ValidateModel(model); Add(manifest.targetModels, model);
            if (!ViewTypes.Contains(JsonUtil.GetString(view, "type", true))) throw new ConsultantException("invalid_view_type", "El tipo de vista no está permitido.");
            string operation = Operation(view); if (operation != "create") RequirePreviousHash(view);
            string arch = LoadText(manifest, view, "arch_template", "arch_file", "arch_sha256", operation != "deactivate");
            if (!string.IsNullOrWhiteSpace(arch)) view["arch_template"] = arch;
            if (!string.IsNullOrWhiteSpace(arch)) ValidatePhase2View(arch);
            string inherit = JsonUtil.GetString(view, "inherit_view_name", false), inheritXmlId = JsonUtil.GetString(view, "inherit_view_xml_id", false);
            if (!string.IsNullOrWhiteSpace(inherit) && !string.IsNullOrWhiteSpace(inheritXmlId)) throw new ConsultantException("ambiguous_parent_view", "La vista debe usar inherit_view_name o inherit_view_xml_id, no ambos.");
            if (!string.IsNullOrWhiteSpace(inheritXmlId)) ValidateXmlId(inheritXmlId);
            if (string.IsNullOrWhiteSpace(inherit) && string.IsNullOrWhiteSpace(inheritXmlId) && !ModelName.IsMatch(model)) throw new ConsultantException("standard_view_extension_required", "Los modelos estándar solo pueden recibir vistas heredadas nuevas.");
        }

        private static void ValidateAction(Manifest manifest, Dictionary<string, object> action)
        {
            EnsureAllowedKeys(action, new[] { "key", "name", "res_model", "view_mode", "mode", "previous_hash", "target", "domain", "context", "limit", "help" }, "window_actions");
            EnsureKeys(action, new[] { "key", "name", "res_model", "view_mode" }); string model = JsonUtil.GetString(action, "res_model", true); StudioService.ValidateModel(model); Add(manifest.targetModels, model);
            string mode = Mode(action, false); if (mode != "create") RequirePreviousHash(action);
            string viewMode = JsonUtil.GetString(action, "view_mode", true); string[] modes = viewMode.Split(','); if (!Regex.IsMatch(viewMode, "^[a-z]+(?:,[a-z]+)*$") || modes.Distinct().Count() != modes.Length || modes.Any(delegate(string value) { return !ViewTypes.Contains(value); })) throw new ConsultantException("invalid_view_mode", "view_mode contiene espacios, duplicados o valores inválidos.");
            string target = JsonUtil.GetString(action, "target", false) ?? "current"; if (target != "current" && target != "new" && target != "fullscreen" && target != "main") throw new ConsultantException("invalid_action_target", "target de acción no permitido.");
            long limit = JsonUtil.GetLong(action, "limit", false); if (limit < 0 || limit > 1000) throw new ConsultantException("invalid_action_limit", "El límite de la acción debe estar entre 1 y 1000, o quedar omitido para usar 80.");
            ValidateExpression(JsonUtil.GetString(action, "domain", false) ?? "[]", '[', ']', "domain de acción"); ValidateExpression(JsonUtil.GetString(action, "context", false) ?? "{}", '{', '}', "context de acción");
        }

        private static void ValidateMenu(Manifest manifest, Dictionary<string, object> menu)
        {
            EnsureAllowedKeys(menu, new[] { "key", "name", "mode", "previous_hash", "sequence", "active", "parent_key", "parent_xml_id", "parent_reference", "action_key", "web_icon" }, "menus");
            EnsureKeys(menu, new[] { "key", "name" }); string mode = Mode(menu, false); if (mode != "create") RequirePreviousHash(menu);
            int parentKinds = new[] { "parent_key", "parent_xml_id", "parent_reference" }.Count(delegate(string key) { return !string.IsNullOrWhiteSpace(JsonUtil.GetString(menu, key, false)); }); if (parentKinds > 1) throw new ConsultantException("ambiguous_menu_parent", "El menú debe declarar un solo tipo de referencia al padre.");
            string parentReference = JsonUtil.GetString(menu, "parent_reference", false); if (!string.IsNullOrWhiteSpace(parentReference)) { if (!ReferenceKey.IsMatch(parentReference)) throw new ConsultantException("invalid_reference_key", "parent_reference no es válida."); manifest.requiredReferences.Add(parentReference); }
            if (!string.IsNullOrWhiteSpace(parentReference) && mode != "create") throw new ConsultantException("reference_requires_create", "parent_reference solo se admite al crear un menú; las actualizaciones deben usar parent_xml_id o parent_key.");
            if (!string.IsNullOrWhiteSpace(JsonUtil.GetString(menu, "parent_xml_id", false))) ValidateXmlId(JsonUtil.GetString(menu, "parent_xml_id", true));
        }

        private Analysis AnalyzeInternal(OdooClient client, Manifest manifest)
        {
            Analysis result = new Analysis { components = new List<Dictionary<string, object>>(), blocked = new List<string>(), modelIds = new Dictionary<string, long>(StringComparer.Ordinal), parentViewIds = new Dictionary<string, long>(StringComparer.Ordinal) };
            Dictionary<string, bool> access = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (Dictionary<string, object> model in manifest.models)
            {
                string technical = JsonUtil.GetString(model, "model", true); IList<Dictionary<string, object>> rows = client.SearchRead("ir.model", new object[] { new object[] { "model", "=", technical } }, new[] { "id", "model", "name", "order", "info", "state", "fold_name" }, 2, null);
                AddAnalysis(result, "model:" + JsonUtil.GetString(model, "key", true), "model", Mode(model, false), model, rows, "ir.model", technical, delegate(Dictionary<string, object> actual) { return Convert.ToString(actual["state"], CultureInfo.InvariantCulture) == "manual"; });
                if (rows.Count == 1) result.modelIds[technical] = Id(rows[0]);
            }
            foreach (string modelName in manifest.targetModels.Where(delegate(string x) { return !result.modelIds.ContainsKey(x); }))
            {
                IList<Dictionary<string, object>> rows = client.SearchRead("ir.model", new object[] { new object[] { "model", "=", modelName } }, new[] { "id", "model", "name", "order", "info", "state", "fold_name" }, 2, null);
                if (rows.Count == 1) result.modelIds[modelName] = Id(rows[0]); else if (!manifest.models.Any(delegate(Dictionary<string, object> x) { return JsonUtil.GetString(x, "model", true) == modelName && Mode(x, false) == "create"; })) result.blocked.Add("modelo ausente: " + modelName);
            }
            foreach (Dictionary<string, object> field in manifest.fields)
            {
                string model = JsonUtil.GetString(field, "model", true), name = JsonUtil.GetString(field, "name", true);
                IList<Dictionary<string, object>> rows = client.SearchRead("ir.model.fields", new object[] { new object[] { "model", "=", model }, new object[] { "name", "=", name } }, FieldReadFields, 2, null);
                if (rows.Count == 1 && !string.IsNullOrWhiteSpace(JsonUtil.GetString(field, "compute_file", false)))
                {
                    IList<Dictionary<string, object>> computeRows = client.Read("ir.model.fields", new[] { Id(rows[0]) }, new[] { "id", "compute" });
                    string compute = computeRows.Count == 1 ? Text(computeRows[0], "compute").Replace("\r\n", "\n").Replace("\r", "\n") : string.Empty;
                    rows[0]["compute_hash"] = JsonUtil.Sha256(compute);
                }
                if (rows.Count == 1 && JsonUtil.GetString(field, "type", true) == "selection") rows[0]["selection_options"] = client.SearchRead("ir.model.fields.selection", new object[] { new object[] { "field_id", "=", Id(rows[0]) } }, new[] { "id", "name", "value", "sequence" }, 100, "sequence,id");
                bool structuralChange = rows.Count == 1 && !FieldStructureUnchanged(field, rows[0]);
                bool structureCanChange = !structuralChange || FieldHasNoStoredValues(client, model, name);
                AddAnalysis(result, "field:" + JsonUtil.GetString(field, "key", true), "field", Mode(field, false), field, rows, "ir.model.fields", model + "." + name, delegate(Dictionary<string, object> actual) { return Convert.ToString(actual["state"], CultureInfo.InvariantCulture) == "manual" && structureCanChange; });
                result.components[result.components.Count - 1]["structural_change"] = structuralChange;
                if (JsonUtil.GetString(field, "type", true) == "many2many")
                {
                    string table = JsonUtil.GetString(field, "relation_table", true); IList<Dictionary<string, object>> owners = client.SearchRead("ir.model.fields", new object[] { new object[] { "relation_table", "=", table }, new object[] { "name", "!=", name } }, new[] { "id", "model", "name" }, 5, null);
                    if (owners.Count > 0) result.blocked.Add("tabla Many2many en uso: " + table);
                }
                ValidateFieldDependencies(client, manifest, field, result.blocked);
            }
            foreach (Dictionary<string, object> view in manifest.views)
            {
                string model = JsonUtil.GetString(view, "model", true), key = JsonUtil.GetString(view, "key", true), inherit = JsonUtil.GetString(view, "inherit_view_name", false), inheritXmlId = JsonUtil.GetString(view, "inherit_view_xml_id", false);
                long parentId = 0;
                if (!string.IsNullOrWhiteSpace(inherit)) { IList<Dictionary<string, object>> parents = client.SearchRead("ir.ui.view", new object[] { new object[] { "name", "=", inherit }, new object[] { "model", "=", model } }, new[] { "id", "name", "model" }, 3, null); if (parents.Count != 1) result.blocked.Add("vista padre ausente o ambigua: " + inherit); else { parentId = Id(parents[0]); result.parentViewIds[key] = parentId; } }
                else if (!string.IsNullOrWhiteSpace(inheritXmlId))
                {
                    try
                    {
                        parentId = ResolveXmlId(client, inheritXmlId, "ir.ui.view");
                        IList<Dictionary<string, object>> parents = client.Read("ir.ui.view", new[] { parentId }, new[] { "id", "model" });
                        if (parents.Count != 1 || Convert.ToString(parents[0]["model"], CultureInfo.InvariantCulture) != model) { result.blocked.Add("vista padre XML ID incompatible: " + inheritXmlId); parentId = 0; }
                        else result.parentViewIds[key] = parentId;
                    }
                    catch (ConsultantException) { result.blocked.Add("vista padre XML ID ausente: " + inheritXmlId); }
                }
                string target = "OdooAI Replica - " + manifest.slug + " - " + key;
                IList<Dictionary<string, object>> rows = client.SearchRead("ir.ui.view", new object[] { new object[] { "name", "=", target }, new object[] { "model", "=", model } }, new[] { "id", "name", "model", "type", "inherit_id", "arch", "mode", "priority", "active" }, 2, null);
                Dictionary<string, object> desiredView = new Dictionary<string, object>(view, StringComparer.Ordinal); desiredView["resolved_parent_id"] = parentId;
                AddAnalysis(result, "view:" + key, "view", Operation(view), desiredView, rows, "ir.ui.view", target, delegate(Dictionary<string, object> actual) { return RelationId(actual["inherit_id"]) == parentId && IsCustomOwned(client, "ir.ui.view", Id(actual)); });
            }
            Dictionary<string, long> actionIds = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (Dictionary<string, object> action in manifest.actions)
            {
                string name = JsonUtil.GetString(action, "name", true), model = JsonUtil.GetString(action, "res_model", true);
                IList<Dictionary<string, object>> rows = client.SearchRead("ir.actions.act_window", new object[] { new object[] { "name", "=", name }, new object[] { "res_model", "=", model } }, new[] { "id", "name", "res_model", "view_mode", "target", "domain", "context", "limit", "help" }, 2, null);
                AddAnalysis(result, "window_action:" + JsonUtil.GetString(action, "key", true), "window_action", Mode(action, false), action, rows, "ir.actions.act_window", name, delegate(Dictionary<string, object> actual) { return IsCustomOwned(client, "ir.actions.act_window", Id(actual)); });
                if (rows.Count == 1) actionIds[JsonUtil.GetString(action, "key", true)] = Id(rows[0]);
            }
            Dictionary<string, long> menuIds = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (Dictionary<string, object> menu in manifest.menus)
            {
                string name = JsonUtil.GetString(menu, "name", true), key = JsonUtil.GetString(menu, "key", true), parentKey = JsonUtil.GetString(menu, "parent_key", false), parentXmlId = JsonUtil.GetString(menu, "parent_xml_id", false), parentReference = JsonUtil.GetString(menu, "parent_reference", false), actionKey = JsonUtil.GetString(menu, "action_key", false);
                long parentId = 0, actionId = 0; bool deferred = false;
                if (!string.IsNullOrWhiteSpace(parentKey)) { if (!menuIds.TryGetValue(parentKey, out parentId)) deferred = true; }
                else if (!string.IsNullOrWhiteSpace(parentXmlId)) { try { parentId = ResolveXmlId(client, parentXmlId, "ir.ui.menu"); } catch (ConsultantException) { result.blocked.Add("menú padre XML ID ausente: " + parentXmlId); deferred = true; } }
                else if (!string.IsNullOrWhiteSpace(parentReference)) deferred = true;
                if (!string.IsNullOrWhiteSpace(actionKey) && !actionIds.TryGetValue(actionKey, out actionId)) actionId = -1;
                IList<Dictionary<string, object>> rows = deferred ? new List<Dictionary<string, object>>() : client.SearchRead("ir.ui.menu", MenuDomain(name, parentId), new[] { "id", "name", "active", "sequence", "parent_id", "action", "web_icon" }, 2, null);
                Dictionary<string, object> desiredMenu = new Dictionary<string, object>(menu, StringComparer.Ordinal); desiredMenu["resolved_parent_id"] = parentId; desiredMenu["resolved_action_id"] = actionId;
                AddAnalysis(result, "menu:" + key, "menu", Mode(menu, false), desiredMenu, rows, "ir.ui.menu", name, delegate(Dictionary<string, object> actual) { return IsCustomOwned(client, "ir.ui.menu", Id(actual)); });
                if (rows.Count == 1) menuIds[key] = Id(rows[0]);
            }
            HashSet<string> requiredCapabilities = new HashSet<string>(StringComparer.Ordinal);
            foreach (Dictionary<string, object> component in result.components)
            {
                string classification = Convert.ToString(component["classification"], CultureInfo.InvariantCulture);
                if (classification != "absent" && classification != "modifiable") continue;
                string operation = classification == "absent" ? "create" : "write";
                requiredCapabilities.Add(Convert.ToString(component["technical_model"], CultureInfo.InvariantCulture) + ":" + operation);
            }
            if (manifest.fields.Any(delegate(Dictionary<string, object> field) { return JsonUtil.GetString(field, "type", true) == "selection" && Mode(field, false) != "deactivate"; }))
            {
                requiredCapabilities.Add("ir.model.fields.selection:create");
                requiredCapabilities.Add("ir.model.fields.selection:write");
            }
            foreach (string capability in requiredCapabilities.OrderBy(delegate(string value) { return value; }, StringComparer.Ordinal))
            {
                int separator = capability.LastIndexOf(':');
                bool permitted = SafeAccess(client, capability.Substring(0, separator), capability.Substring(separator + 1));
                access[capability] = permitted;
                if (!permitted) result.blocked.Add("permiso requerido: " + capability);
            }
            result.preconditionHash = JsonUtil.CanonicalHash(result.components); result.capabilityHash = JsonUtil.CanonicalHash(access);
            return result;
        }

        private static void AddAnalysis(Analysis analysis, string key, string kind, string mode, Dictionary<string, object> desired, IList<Dictionary<string, object>> rows, string technicalModel, string label, Func<Dictionary<string, object>, bool> mutable)
        {
            string classification; long id = 0; string actualHash = null; Dictionary<string, object> snapshot = null;
            if (rows.Count > 1) { classification = "blocked"; analysis.blocked.Add("componente ambiguo: " + label); }
            else if (rows.Count == 0) { classification = mode == "create" ? "absent" : "blocked"; if (classification == "blocked") analysis.blocked.Add("componente no encontrado para " + mode + ": " + label); }
            else
            {
                id = Id(rows[0]); snapshot = NormalizeRecord(rows[0]); actualHash = JsonUtil.CanonicalHash(snapshot);
                if (mode == "create") { classification = DesiredMatches(kind, desired, snapshot) && mutable(rows[0]) ? "compatible" : "blocked"; if (classification == "blocked") analysis.blocked.Add("colisión incompatible: " + label); }
                else if (!mutable(rows[0])) { classification = "blocked"; analysis.blocked.Add("artefacto estándar o cambio estructural bloqueado: " + label); }
                else if (JsonUtil.GetString(desired, "previous_hash", true) != actualHash) { classification = "blocked"; analysis.blocked.Add("previous_hash no coincide: " + label); }
                else classification = "modifiable";
            }
            analysis.components.Add(JsonUtil.Dictionary("key", key, "kind", kind, "mode", mode, "classification", classification, "technical_model", technicalModel, "technical_id", id, "actual_hash", actualHash, "snapshot", snapshot));
        }

        private List<ChangeOperation> Compile(Manifest manifest, Analysis analysis)
        {
            Dictionary<string, Dictionary<string, object>> components = analysis.components.ToDictionary(delegate(Dictionary<string, object> x) { return Convert.ToString(x["key"], CultureInfo.InvariantCulture); }, StringComparer.Ordinal);
            List<ChangeOperation> ops = new List<ChangeOperation>();
            foreach (Dictionary<string, object> model in manifest.models) ops.Add(BuildOperation("custom_model", JsonUtil.GetString(model, "model", true), Mode(model, false), ModelValues(model), components["model:" + JsonUtil.GetString(model, "key", true)]));
            foreach (Dictionary<string, object> field in manifest.fields)
            {
                string model = JsonUtil.GetString(field, "model", true), mode = Mode(field, false); Dictionary<string, object> values = FieldValues(manifest, field); if (analysis.modelIds.ContainsKey(model)) values["model_id"] = analysis.modelIds[model];
                ChangeOperation fieldOp = BuildOperation("custom_field", model, mode, values, components["field:" + JsonUtil.GetString(field, "key", true)]); ops.Add(fieldOp);
                if (JsonUtil.GetString(field, "type", true) == "selection" && mode != "deactivate")
                {
                    Dictionary<string, object> fieldSnapshot = components["field:" + JsonUtil.GetString(field, "key", true)]["snapshot"] as Dictionary<string, object>;
                    IEnumerable previousOptions = fieldSnapshot != null && fieldSnapshot.ContainsKey("selection_options") ? fieldSnapshot["selection_options"] as IEnumerable : null;
                    foreach (Dictionary<string, object> option in DictList(field, "selection"))
                    {
                        Dictionary<string, object> previousOption = null;
                        if (previousOptions != null) foreach (object raw in previousOptions) { Dictionary<string, object> candidate = raw as Dictionary<string, object>; if (candidate != null && Convert.ToString(candidate["value"], CultureInfo.InvariantCulture) == JsonUtil.GetString(option, "value", true)) previousOption = candidate; }
                        ops.Add(new ChangeOperation { operation_id = NewOp(), type = "upsert_selection_option", model = model, parameters = JsonUtil.Dictionary("component_key", "field:" + JsonUtil.GetString(field, "key", true), "field_key", JsonUtil.GetString(field, "key", true), "field_name", JsonUtil.GetString(field, "name", true), "value", JsonUtil.GetString(option, "value", true), "name", JsonUtil.GetString(option, "label", true), "sequence", JsonUtil.GetLong(option, "sequence", false), "previous_values", previousOption), status = "pending", result_id = previousOption == null ? 0 : Convert.ToInt64(previousOption["id"], CultureInfo.InvariantCulture) });
                    }
                }
            }
            foreach (Dictionary<string, object> view in manifest.views)
            {
                string key = JsonUtil.GetString(view, "key", true), model = JsonUtil.GetString(view, "model", true), mode = Operation(view); Dictionary<string, object> values = ViewValues(manifest, view); if (analysis.parentViewIds.ContainsKey(key)) values["inherit_id"] = analysis.parentViewIds[key];
                ops.Add(BuildOperation("view", model, mode, values, components["view:" + key]));
            }
            foreach (Dictionary<string, object> action in manifest.actions) ops.Add(BuildOperation("window_action", JsonUtil.GetString(action, "res_model", true), Mode(action, false), ActionValues(action), components["window_action:" + JsonUtil.GetString(action, "key", true)]));
            foreach (Dictionary<string, object> menu in manifest.menus) ops.Add(BuildOperation("menu", manifest.targetModels.First(), Mode(menu, false), MenuValues(menu), components["menu:" + JsonUtil.GetString(menu, "key", true)]));
            return ops;
        }

        private static ChangeOperation BuildOperation(string kind, string model, string mode, Dictionary<string, object> values, Dictionary<string, object> component)
        {
            string classification = Convert.ToString(component["classification"], CultureInfo.InvariantCulture); long id = Convert.ToInt64(component["technical_id"], CultureInfo.InvariantCulture);
            values["existing_id"] = id; values["previous_values"] = component["snapshot"]; values["component_key"] = component["key"];
            if (component.ContainsKey("structural_change")) values["structural_change"] = component["structural_change"];
            string type = mode == "create" ? "create_" + kind : mode == "update" ? "update_" + kind : (kind == "view" || kind == "menu" ? "deactivate_" + kind : "preserve_" + kind);
            return new ChangeOperation { operation_id = NewOp(), type = type, model = model, parameters = values, status = classification == "compatible" ? "reused" : type.StartsWith("preserve_", StringComparison.Ordinal) ? "preserved_noop" : "pending", result_id = id };
        }

        private void ApplyOperation(OdooClient client, Manifest manifest, ChangeSet change, ChangeOperation op, Dictionary<string, long> references)
        {
            if (op.type == "create_custom_model") { if (!RecoverCreate(client, op, "ir.model", new object[] { new object[] { "model", "=", op.model } })) op.result_id = client.Create("ir.model", Values(op, new[] { "existing_id", "previous_values" })); }
            else if (op.type == "update_custom_model") Write(client, "ir.model", op, Values(op, new[] { "existing_id", "previous_values", "model", "state" }));
            else if (op.type == "create_custom_field")
            {
                Dictionary<string, object> values = Values(op, new[] { "existing_id", "previous_values", "compute_file", "compute_sha256" }); values["model_id"] = ResolveModelId(client, change, op.model); AddCompute(manifest, op.parameters, values); if (!RecoverCreate(client, op, "ir.model.fields", new object[] { new object[] { "model", "=", op.model }, new object[] { "name", "=", op.parameters["name"] } })) op.result_id = client.Create("ir.model.fields", values);
            }
            else if (op.type == "update_custom_field")
            {
                bool structural = JsonUtil.GetBool(op.parameters, "structural_change", false);
                string[] excluded = structural
                    ? new[] { "existing_id", "previous_values", "structural_change", "name", "model_id", "state", "compute_file", "compute_sha256" }
                    : new[] { "existing_id", "previous_values", "structural_change", "name", "model_id", "state", "ttype", "relation", "relation_field", "relation_table", "column1", "column2", "related", "compute_file", "compute_sha256", "currency_field", "store" };
                Dictionary<string, object> values = Values(op, excluded); AddCompute(manifest, op.parameters, values); Write(client, "ir.model.fields", op, values);
            }
            else if (op.type == "upsert_selection_option") UpsertSelection(client, change, op);
            else if (op.type == "create_view") { Dictionary<string, object> values = Values(op, new[] { "existing_id", "previous_values", "arch_file", "arch_sha256" }); if (values.ContainsKey("arch")) values["arch"] = JsonUtil.XmlNumericEntities(Convert.ToString(values["arch"], CultureInfo.InvariantCulture)); if (!RecoverCreate(client, op, "ir.ui.view", new object[] { new object[] { "name", "=", op.parameters["name"] }, new object[] { "model", "=", op.model } })) op.result_id = client.Create("ir.ui.view", values); }
            else if (op.type == "update_view") { Dictionary<string, object> values = Values(op, new[] { "existing_id", "previous_values", "arch_file", "arch_sha256", "model", "inherit_id", "type", "mode" }); if (values.ContainsKey("arch")) values["arch"] = JsonUtil.XmlNumericEntities(Convert.ToString(values["arch"], CultureInfo.InvariantCulture)); Write(client, "ir.ui.view", op, values); }
            else if (op.type == "deactivate_view") Write(client, "ir.ui.view", op, JsonUtil.Dictionary("active", false));
            else if (op.type == "create_window_action") { if (!RecoverCreate(client, op, "ir.actions.act_window", new object[] { new object[] { "name", "=", op.parameters["name"] }, new object[] { "res_model", "=", op.model } })) op.result_id = client.Create("ir.actions.act_window", Values(op, new[] { "existing_id", "previous_values", "key" })); }
            else if (op.type == "update_window_action") Write(client, "ir.actions.act_window", op, Values(op, new[] { "existing_id", "previous_values", "key", "res_model" }));
            else if (op.type == "create_menu" || op.type == "update_menu")
            {
                Dictionary<string, object> values = ResolveMenuValues(change, op, references);
                if (op.type == "create_menu") { if (!RecoverMenu(client, op, values)) op.result_id = client.Create("ir.ui.menu", values); } else Write(client, "ir.ui.menu", op, values);
            }
            else if (op.type == "deactivate_menu") Write(client, "ir.ui.menu", op, JsonUtil.Dictionary("active", false));
            else if (op.type.StartsWith("preserve_", StringComparison.Ordinal)) { op.status = "preserved_noop"; return; }
            else throw new ConsultantException("tool_contract_violation", "Operación Fase 2 no permitida.");
            op.status = "applied";
        }

        private static bool VerifyOperation(OdooClient client, ChangeSet change, ChangeOperation op, string status)
        {
            if (op.status == "preserved_noop" || op.status == "preserved_manual_cleanup") return true; if (op.status == "rollback_failed" || op.result_id <= 0) return false;
            string model = TechnicalModel(op.type); IList<Dictionary<string, object>> rows = client.Read(model, new[] { op.result_id }, VerificationFields(model)); if (rows.Count != 1) return false;
            Dictionary<string, object> actual = rows[0];
            if (op.status == "rolled_back")
            {
                if (op.type == "create_view" || op.type == "create_menu") return !Convert.ToBoolean(actual["active"], CultureInfo.InvariantCulture);
                Dictionary<string, object> previous = Dict(op.parameters, "previous_values", false);
                return previous != null && RestoredValuesMatch(RestorableValues(model, previous), actual);
            }
            if (op.type == "deactivate_view" || op.type == "deactivate_menu") return !Convert.ToBoolean(actual["active"], CultureInfo.InvariantCulture);
            if (op.type.Contains("custom_model")) return Text(actual, "model") == op.model && Text(actual, "name") == JsonUtil.GetString(op.parameters, "name", true) && Text(actual, "state") == "manual" && Text(actual, "order") == JsonUtil.GetString(op.parameters, "order", true);
            if (op.type.Contains("custom_field")) return FieldOperationMatches(op.parameters, actual);
            if (op.type == "upsert_selection_option") return Text(actual, "value") == JsonUtil.GetString(op.parameters, "value", true) && Text(actual, "name") == JsonUtil.GetString(op.parameters, "name", true) && Convert.ToInt64(actual["sequence"], CultureInfo.InvariantCulture) == JsonUtil.GetLong(op.parameters, "sequence", false);
            if (op.type.Contains("view")) return Text(actual, "name") == JsonUtil.GetString(op.parameters, "name", true) && Text(actual, "model") == op.model && Text(actual, "type") == JsonUtil.GetString(op.parameters, "type", true) && NormalizeXml(Text(actual, "arch")) == NormalizeXml(JsonUtil.GetString(op.parameters, "arch", true)) && RelationId(actual["inherit_id"]) == JsonUtil.GetLong(op.parameters, "inherit_id", false) && Convert.ToInt64(actual["priority"], CultureInfo.InvariantCulture) == JsonUtil.GetLong(op.parameters, "priority", true) && Convert.ToBoolean(actual["active"], CultureInfo.InvariantCulture) == JsonUtil.GetBool(op.parameters, "active", true);
            if (op.type.Contains("window_action")) return Text(actual, "name") == JsonUtil.GetString(op.parameters, "name", true) && Text(actual, "res_model") == op.model && Text(actual, "view_mode") == JsonUtil.GetString(op.parameters, "view_mode", true) && Text(actual, "target") == JsonUtil.GetString(op.parameters, "target", true) && Text(actual, "domain") == JsonUtil.GetString(op.parameters, "domain", true) && Text(actual, "context") == JsonUtil.GetString(op.parameters, "context", true);
            if (op.type.Contains("menu"))
            {
                if (Text(actual, "name") != JsonUtil.GetString(op.parameters, "name", true) || Convert.ToInt64(actual["sequence"], CultureInfo.InvariantCulture) != JsonUtil.GetLong(op.parameters, "sequence", true) || Convert.ToBoolean(actual["active"], CultureInfo.InvariantCulture) != JsonUtil.GetBool(op.parameters, "active", true)) return false;
                string parentKey = JsonUtil.GetString(op.parameters, "parent_key", false); if (!string.IsNullOrWhiteSpace(parentKey) && RelationId(actual["parent_id"]) != OperationByKey(change, "menu", parentKey).result_id) return false;
                string actionKey = JsonUtil.GetString(op.parameters, "action_key", false); if (!string.IsNullOrWhiteSpace(actionKey) && Text(actual, "action") != "ir.actions.act_window," + OperationByKey(change, "window_action", actionKey).result_id.ToString(CultureInfo.InvariantCulture)) return false;
                return true;
            }
            return false;
        }

        private static void ValidateResume(OdooClient client, ChangeSet change, Analysis current)
        {
            Dictionary<string, Dictionary<string, object>> components = current.components.ToDictionary(delegate(Dictionary<string, object> item) { return Convert.ToString(item["key"], CultureInfo.InvariantCulture); }, StringComparer.Ordinal);
            foreach (ChangeOperation operation in change.operations)
            {
                if (operation.status == "applied" || operation.status == "reused")
                {
                    if (!VerifyOperation(client, change, operation, "failed_partial")) throw new ConsultantException("precondition_changed", "Un componente ya aplicado cambió antes del reintento.");
                    continue;
                }
                if (operation.status == "preserved_noop") continue;
                if (operation.type == "upsert_selection_option") { ValidatePendingSelection(client, change, operation); continue; }
                string componentKey = JsonUtil.GetString(operation.parameters, "component_key", true);
                Dictionary<string, object> component;
                if (!components.TryGetValue(componentKey, out component)) throw new ConsultantException("precondition_changed", "No se encontró un componente pendiente durante el reintento.");
                string classification = Convert.ToString(component["classification"], CultureInfo.InvariantCulture);
                if (classification == "absent" || classification == "modifiable") continue;
                Dictionary<string, object> snapshot = Dict(component, "snapshot", false);
                if (classification == "compatible" || (snapshot != null && OperationDesiredMatches(operation, snapshot)))
                {
                    operation.result_id = Convert.ToInt64(component["technical_id"], CultureInfo.InvariantCulture);
                    operation.status = "applied";
                    continue;
                }
                throw new ConsultantException("precondition_changed", "Un componente pendiente cambió desde el intento anterior.");
            }
            if (current.blocked.Any(delegate(string item) { return !item.StartsWith("previous_hash no coincide:", StringComparison.Ordinal); })) throw new ConsultantException("precondition_changed", "Las dependencias, colisiones o permisos cambiaron antes del reintento.");
        }

        private static void ValidatePendingSelection(OdooClient client, ChangeSet change, ChangeOperation operation)
        {
            ChangeOperation field = change.operations.First(delegate(ChangeOperation item) { return item.type.Contains("custom_field") && item.model == operation.model && JsonUtil.GetString(item.parameters, "name", false) == JsonUtil.GetString(operation.parameters, "field_name", true); });
            if (field.result_id <= 0) return;
            IList<Dictionary<string, object>> rows = client.SearchRead("ir.model.fields.selection", new object[] { new object[] { "field_id", "=", field.result_id }, new object[] { "value", "=", operation.parameters["value"] } }, new[] { "id", "name", "value", "sequence" }, 2, null);
            if (rows.Count == 0) { if (Dict(operation.parameters, "previous_values", false) != null) throw new ConsultantException("precondition_changed", "Una opción de selección existente desapareció antes del reintento."); return; }
            if (rows.Count != 1) throw new ConsultantException("precondition_changed", "Una opción de selección quedó ambigua antes del reintento.");
            Dictionary<string, object> current = rows[0], previous = Dict(operation.parameters, "previous_values", false);
            bool desired = Text(current, "name") == JsonUtil.GetString(operation.parameters, "name", true) && Convert.ToInt64(current["sequence"], CultureInfo.InvariantCulture) == JsonUtil.GetLong(operation.parameters, "sequence", false);
            bool unchanged = previous != null && Text(current, "name") == Text(previous, "name") && Convert.ToInt64(current["sequence"], CultureInfo.InvariantCulture) == JsonUtil.GetLong(previous, "sequence", false);
            if (desired) { operation.result_id = Id(current); operation.status = "applied"; }
            else if (!unchanged) throw new ConsultantException("precondition_changed", "Una opción de selección cambió antes del reintento.");
        }

        private static bool OperationDesiredMatches(ChangeOperation operation, Dictionary<string, object> actual)
        {
            if (operation.type.Contains("custom_model")) return Text(actual, "model") == operation.model && Text(actual, "name") == JsonUtil.GetString(operation.parameters, "name", true) && Text(actual, "order") == JsonUtil.GetString(operation.parameters, "order", true);
            if (operation.type.Contains("custom_field")) return FieldOperationMatches(operation.parameters, actual);
            if (operation.type.Contains("view")) return operation.type == "deactivate_view" ? !Convert.ToBoolean(actual["active"], CultureInfo.InvariantCulture) : Text(actual, "name") == JsonUtil.GetString(operation.parameters, "name", true) && NormalizeXml(Text(actual, "arch")) == NormalizeXml(JsonUtil.GetString(operation.parameters, "arch", true));
            if (operation.type.Contains("window_action")) return Text(actual, "name") == JsonUtil.GetString(operation.parameters, "name", true) && Text(actual, "view_mode") == JsonUtil.GetString(operation.parameters, "view_mode", true);
            if (operation.type.Contains("menu")) return operation.type == "deactivate_menu" ? !Convert.ToBoolean(actual["active"], CultureInfo.InvariantCulture) : Text(actual, "name") == JsonUtil.GetString(operation.parameters, "name", true) && Convert.ToInt64(actual["sequence"], CultureInfo.InvariantCulture) == JsonUtil.GetLong(operation.parameters, "sequence", true);
            return false;
        }

        private static Dictionary<string, object> ModelValues(Dictionary<string, object> model) { return JsonUtil.Dictionary("model", JsonUtil.GetString(model, "model", true), "name", JsonUtil.GetString(model, "name", true), "order", JsonUtil.GetString(model, "order", false) ?? "id", "info", JsonUtil.GetString(model, "info", false), "state", "manual", "fold_name", JsonUtil.GetString(model, "fold_name", false)); }
        private static Dictionary<string, object> FieldValues(Manifest manifest, Dictionary<string, object> field)
        {
            Dictionary<string, object> values = JsonUtil.Dictionary("name", JsonUtil.GetString(field, "name", true), "field_description", JsonUtil.GetString(field, "label", true), "ttype", JsonUtil.GetString(field, "type", true), "state", "manual", "required", JsonUtil.GetBool(field, "required", false), "readonly", JsonUtil.GetBool(field, "readonly", false) || !string.IsNullOrWhiteSpace(JsonUtil.GetString(field, "related", false)) || !string.IsNullOrWhiteSpace(JsonUtil.GetString(field, "compute_file", false)), "store", JsonUtil.GetBool(field, "stored", true), "copied", JsonUtil.GetBool(field, "copied", true), "index", JsonUtil.GetBool(field, "indexed", false), "help", JsonUtil.GetString(field, "help", false));
            foreach (string key in new[] { "relation", "relation_field", "relation_table", "column1", "column2", "domain", "related", "depends", "currency_field", "on_delete" }) { string value = JsonUtil.GetString(field, key, false); if (!string.IsNullOrWhiteSpace(value)) values[key] = value; }
            foreach (string key in new[] { "translate", "group_expand", "selectable", "sanitize", "sanitize_tags", "sanitize_attributes", "sanitize_style", "sanitize_form", "strip_style", "strip_classes" }) if (field.ContainsKey(key)) values[key] = JsonUtil.GetBool(field, key, false);
            string computeFile = JsonUtil.GetString(field, "compute_file", false); if (!string.IsNullOrWhiteSpace(computeFile)) { values["compute_file"] = computeFile; values["compute_sha256"] = JsonUtil.GetString(field, "compute_sha256", true); }
            return values;
        }
        private static Dictionary<string, object> ViewValues(Manifest manifest, Dictionary<string, object> view) { string key = JsonUtil.GetString(view, "key", true); string arch = LoadText(manifest, view, "arch_template", "arch_file", "arch_sha256", Operation(view) != "deactivate"); long priority = JsonUtil.GetLong(view, "priority", false); if (priority == 0) priority = 16; bool inherited = !string.IsNullOrWhiteSpace(JsonUtil.GetString(view, "inherit_view_name", false)) || !string.IsNullOrWhiteSpace(JsonUtil.GetString(view, "inherit_view_xml_id", false)); Dictionary<string, object> values = JsonUtil.Dictionary("name", "OdooAI Replica - " + manifest.slug + " - " + key, "model", JsonUtil.GetString(view, "model", true), "type", JsonUtil.GetString(view, "type", true), "arch", arch, "mode", inherited ? "extension" : "primary", "priority", priority, "active", JsonUtil.GetBool(view, "active", true)); string file = JsonUtil.GetString(view, "arch_file", false); if (!string.IsNullOrWhiteSpace(file)) { values["arch_file"] = file; values["arch_sha256"] = JsonUtil.GetString(view, "arch_sha256", true); } return values; }
        private static Dictionary<string, object> ActionValues(Dictionary<string, object> action) { return JsonUtil.Dictionary("key", JsonUtil.GetString(action, "key", true), "name", JsonUtil.GetString(action, "name", true), "res_model", JsonUtil.GetString(action, "res_model", true), "view_mode", JsonUtil.GetString(action, "view_mode", true), "target", JsonUtil.GetString(action, "target", false) ?? "current", "domain", JsonUtil.GetString(action, "domain", false) ?? "[]", "context", JsonUtil.GetString(action, "context", false) ?? "{}", "limit", JsonUtil.GetLong(action, "limit", false) == 0 ? 80 : JsonUtil.GetLong(action, "limit", false), "help", JsonUtil.GetString(action, "help", false)); }
        private static Dictionary<string, object> MenuValues(Dictionary<string, object> menu) { long sequence = JsonUtil.GetLong(menu, "sequence", false); if (sequence == 0) sequence = 10; Dictionary<string, object> values = JsonUtil.Dictionary("key", JsonUtil.GetString(menu, "key", true), "name", JsonUtil.GetString(menu, "name", true), "sequence", sequence, "active", JsonUtil.GetBool(menu, "active", true)); foreach (string key in new[] { "parent_key", "parent_xml_id", "parent_reference", "action_key", "web_icon" }) { string value = JsonUtil.GetString(menu, key, false); if (!string.IsNullOrWhiteSpace(value)) values[key] = value; } return values; }

        private static bool DesiredMatches(string kind, Dictionary<string, object> desired, Dictionary<string, object> actual)
        {
            if (kind == "model") return Text(actual, "model") == JsonUtil.GetString(desired, "model", true) && Text(actual, "name") == JsonUtil.GetString(desired, "name", true) && Text(actual, "state") == "manual" && Text(actual, "order") == (JsonUtil.GetString(desired, "order", false) ?? "id") && Text(actual, "info") == (JsonUtil.GetString(desired, "info", false) ?? string.Empty) && Text(actual, "fold_name") == (JsonUtil.GetString(desired, "fold_name", false) ?? string.Empty);
            if (kind == "field") return FieldDesiredMatches(desired, actual);
            if (kind == "window_action") return Text(actual, "name") == JsonUtil.GetString(desired, "name", true) && Text(actual, "res_model") == JsonUtil.GetString(desired, "res_model", true) && Text(actual, "view_mode") == JsonUtil.GetString(desired, "view_mode", true) && Text(actual, "target") == (JsonUtil.GetString(desired, "target", false) ?? "current") && Text(actual, "domain") == (JsonUtil.GetString(desired, "domain", false) ?? "[]") && Text(actual, "context") == (JsonUtil.GetString(desired, "context", false) ?? "{}") && Convert.ToInt64(actual["limit"], CultureInfo.InvariantCulture) == (JsonUtil.GetLong(desired, "limit", false) == 0 ? 80 : JsonUtil.GetLong(desired, "limit", false));
            if (kind == "menu") { long actionId = JsonUtil.GetLong(desired, "resolved_action_id", false); string expectedAction = actionId > 0 ? "ir.actions.act_window," + actionId.ToString(CultureInfo.InvariantCulture) : actionId < 0 ? "__pending_action__" : string.Empty; return Text(actual, "name") == JsonUtil.GetString(desired, "name", true) && Convert.ToInt64(actual["sequence"], CultureInfo.InvariantCulture) == (JsonUtil.GetLong(desired, "sequence", false) == 0 ? 10 : JsonUtil.GetLong(desired, "sequence", false)) && Convert.ToBoolean(actual["active"], CultureInfo.InvariantCulture) == JsonUtil.GetBool(desired, "active", true) && Text(actual, "web_icon") == (JsonUtil.GetString(desired, "web_icon", false) ?? string.Empty) && RelationId(actual["parent_id"]) == JsonUtil.GetLong(desired, "resolved_parent_id", false) && Text(actual, "action") == expectedAction; }
            if (kind == "view") { long priority = JsonUtil.GetLong(desired, "priority", false); if (priority == 0) priority = 16; return Text(actual, "type") == JsonUtil.GetString(desired, "type", true) && RelationId(actual["inherit_id"]) == JsonUtil.GetLong(desired, "resolved_parent_id", false) && NormalizeXml(Text(actual, "arch")) == NormalizeXml(JsonUtil.GetString(desired, "arch_template", true)) && Convert.ToInt64(actual["priority"], CultureInfo.InvariantCulture) == priority && Convert.ToBoolean(actual["active"], CultureInfo.InvariantCulture) == JsonUtil.GetBool(desired, "active", true); }
            return false;
        }
        private static bool FieldStructureUnchanged(Dictionary<string, object> desired, Dictionary<string, object> actual) { foreach (string key in new[] { "type", "relation", "relation_field", "relation_table", "column1", "column2", "related", "currency_field" }) { string actualKey = key == "type" ? "ttype" : key; string wanted = JsonUtil.GetString(desired, key, false) ?? string.Empty; string got = actual.ContainsKey(actualKey) && actual[actualKey] != null && !(actual[actualKey] is bool) ? Convert.ToString(actual[actualKey], CultureInfo.InvariantCulture) : string.Empty; if (wanted != got) return false; } return true; }
        private static bool FieldDesiredMatches(Dictionary<string, object> desired, Dictionary<string, object> actual)
        {
            if (!FieldStructureUnchanged(desired, actual) || Text(actual, "field_description") != JsonUtil.GetString(desired, "label", true) || Convert.ToBoolean(actual["required"], CultureInfo.InvariantCulture) != JsonUtil.GetBool(desired, "required", false) || Convert.ToBoolean(actual["store"], CultureInfo.InvariantCulture) != JsonUtil.GetBool(desired, "stored", true)) return false;
            string computeHash = JsonUtil.GetString(desired, "compute_sha256", false); if (!string.IsNullOrWhiteSpace(computeHash) && Text(actual, "compute_hash") != computeHash) return false;
            if (JsonUtil.GetString(desired, "type", true) != "selection") return true;
            IEnumerable options = actual.ContainsKey("selection_options") ? actual["selection_options"] as IEnumerable : null;
            foreach (Dictionary<string, object> wanted in DictList(desired, "selection"))
            {
                bool found = false;
                if (options != null) foreach (object raw in options) { Dictionary<string, object> existing = raw as Dictionary<string, object>; if (existing != null && Convert.ToString(existing["value"], CultureInfo.InvariantCulture) == JsonUtil.GetString(wanted, "value", true) && Convert.ToString(existing["name"], CultureInfo.InvariantCulture) == JsonUtil.GetString(wanted, "label", true) && Convert.ToInt64(existing["sequence"], CultureInfo.InvariantCulture) == JsonUtil.GetLong(wanted, "sequence", false)) found = true; }
                if (!found) return false;
            }
            return true;
        }
        private static string Text(IDictionary<string, object> source, string key) { object value; if (source == null || !source.TryGetValue(key, out value) || value == null || value is bool) return string.Empty; return Convert.ToString(value, CultureInfo.InvariantCulture); }
        private static string NormalizeXml(string value) { return Regex.Replace(System.Net.WebUtility.HtmlDecode(JsonUtil.SanitizeMojibake(value ?? string.Empty)).Replace("\r", string.Empty).Replace("\n", string.Empty), ">\\s+<", "><").Trim(); }
        private static Dictionary<string, object> NormalizeRecord(Dictionary<string, object> row) { Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal); foreach (KeyValuePair<string, object> item in row.OrderBy(delegate(KeyValuePair<string, object> x) { return x.Key; }, StringComparer.Ordinal)) if (item.Key != "id") result[item.Key] = NormalizeValue(item.Value); return result; }
        private static object NormalizeValue(object value) { if (value == null || value is string || value is bool || value is int || value is long || value is decimal || value is double) return value; IDictionary dictionary = value as IDictionary; if (dictionary != null) { Dictionary<string, object> normalized = new Dictionary<string, object>(StringComparer.Ordinal); foreach (object key in dictionary.Keys) normalized[Convert.ToString(key, CultureInfo.InvariantCulture)] = NormalizeValue(dictionary[key]); return normalized; } IEnumerable seq = value as IEnumerable; if (seq != null) { List<object> items = new List<object>(); foreach (object item in seq) items.Add(NormalizeValue(item)); return items.ToArray(); } return Convert.ToString(value, CultureInfo.InvariantCulture); }

        private static Dictionary<string, string> ValidateReferences(Manifest manifest, IDictionary<string, object> references) { Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal); if (references != null) foreach (KeyValuePair<string, object> item in references) { if (!manifest.requiredReferences.Contains(item.Key)) throw new ConsultantException("unexpected_reference", "Se proporcionó una referencia no declarada: " + item.Key + "."); string xmlid = Convert.ToString(item.Value, CultureInfo.InvariantCulture); ValidateXmlId(xmlid); result[item.Key] = xmlid; } foreach (string required in manifest.requiredReferences) if (!result.ContainsKey(required)) throw new ConsultantException("unresolved_reference", "Falta la referencia requerida " + required + "."); return result; }
        private static Dictionary<string, long> ResolveReferences(OdooClient client, Dictionary<string, string> references) { Dictionary<string, long> result = new Dictionary<string, long>(StringComparer.Ordinal); foreach (KeyValuePair<string, string> item in references) result[item.Key] = ResolveXmlId(client, item.Value, "ir.ui.menu"); return result; }
        private static void ValidateReferenceMenuState(OdooClient client, ChangeSet change, Dictionary<string, long> references)
        {
            foreach (ChangeOperation menu in change.operations.Where(delegate(ChangeOperation item) { return item.type == "create_menu" && !string.IsNullOrWhiteSpace(JsonUtil.GetString(item.parameters, "parent_reference", false)); }))
            {
                string reference = JsonUtil.GetString(menu.parameters, "parent_reference", true); long parentId = references[reference];
                IList<Dictionary<string, object>> rows = client.SearchRead("ir.ui.menu", MenuDomain(JsonUtil.GetString(menu.parameters, "name", true), parentId), new[] { "id", "name", "active", "sequence", "parent_id", "action", "web_icon" }, 2, null);
                if (rows.Count == 0) continue;
                string actionKey = JsonUtil.GetString(menu.parameters, "action_key", false); long actionId = 0; if (!string.IsNullOrWhiteSpace(actionKey)) { ChangeOperation action = change.operations.FirstOrDefault(delegate(ChangeOperation item) { return item.type.Contains("window_action") && JsonUtil.GetString(item.parameters, "key", false) == actionKey; }); if (action != null) actionId = action.result_id; }
                Dictionary<string, object> desired = new Dictionary<string, object>(menu.parameters, StringComparer.Ordinal); desired["resolved_parent_id"] = parentId; desired["resolved_action_id"] = actionId > 0 ? actionId : -1;
                if (change.status == "failed_partial" && rows.Count == 1 && actionId > 0 && DesiredMatches("menu", desired, rows[0]) && IsCustomOwned(client, "ir.ui.menu", Id(rows[0]))) { menu.result_id = Id(rows[0]); menu.status = "applied"; continue; }
                throw new ConsultantException("precondition_changed", "El menú referenciado cambió o colisionó antes de la primera escritura.");
            }
        }
        private static long ResolveXmlId(OdooClient client, string xmlid, string expectedModel) { int separator = xmlid.IndexOf('.'); string module = xmlid.Substring(0, separator), name = xmlid.Substring(separator + 1); IList<Dictionary<string, object>> rows = client.SearchRead("ir.model.data", new object[] { new object[] { "module", "=", module }, new object[] { "name", "=", name }, new object[] { "model", "=", expectedModel } }, new[] { "id", "module", "name", "model", "res_id" }, 2, null); if (rows.Count != 1) throw new ConsultantException("reference_not_found", "No se resolvió una referencia XML ID del destino."); return Convert.ToInt64(rows[0]["res_id"], CultureInfo.InvariantCulture); }
        private static Dictionary<string, object> ResolveMenuValues(ChangeSet change, ChangeOperation op, Dictionary<string, long> references) { Dictionary<string, object> values = Values(op, new[] { "existing_id", "previous_values", "key", "parent_key", "parent_xml_id", "parent_reference", "action_key" }); string parentKey = JsonUtil.GetString(op.parameters, "parent_key", false); if (!string.IsNullOrWhiteSpace(parentKey)) values["parent_id"] = OperationByKey(change, "menu", parentKey).result_id; string parentRef = JsonUtil.GetString(op.parameters, "parent_reference", false); if (!string.IsNullOrWhiteSpace(parentRef)) values["parent_id"] = references[parentRef]; string parentXml = JsonUtil.GetString(op.parameters, "parent_xml_id", false); if (!string.IsNullOrWhiteSpace(parentXml)) values["parent_id"] = references["xmlid:" + parentXml]; string actionKey = JsonUtil.GetString(op.parameters, "action_key", false); if (!string.IsNullOrWhiteSpace(actionKey)) values["action"] = "ir.actions.act_window," + OperationByKey(change, "window_action", actionKey).result_id.ToString(CultureInfo.InvariantCulture); return values; }
        private static ChangeOperation OperationByKey(ChangeSet change, string kind, string key) { string expected = kind == "menu" ? "menu" : "window_action"; ChangeOperation op = change.operations.FirstOrDefault(delegate(ChangeOperation x) { return x.type.Contains(expected) && JsonUtil.GetString(x.parameters, "key", false) == key; }); if (op == null || op.result_id <= 0) throw new ConsultantException("dependency_not_resolved", "No se resolvió la dependencia lógica " + key + "."); return op; }

        private static long ResolveModelId(OdooClient client, ChangeSet change, string model) { ChangeOperation created = change.operations.FirstOrDefault(delegate(ChangeOperation x) { return x.type == "create_custom_model" && x.model == model && x.result_id > 0; }); if (created != null) return created.result_id; IList<Dictionary<string, object>> rows = client.SearchRead("ir.model", new object[] { new object[] { "model", "=", model } }, new[] { "id" }, 2, null); if (rows.Count != 1) throw new ConsultantException("model_not_found", "No se resolvió el modelo del campo."); return Id(rows[0]); }
        private static void UpsertSelection(OdooClient client, ChangeSet change, ChangeOperation op) { ChangeOperation field = change.operations.First(delegate(ChangeOperation x) { return x.type.Contains("custom_field") && JsonUtil.GetString(x.parameters, "name", false) == JsonUtil.GetString(op.parameters, "field_name", true) && x.model == op.model; }); IList<Dictionary<string, object>> rows = client.SearchRead("ir.model.fields.selection", new object[] { new object[] { "field_id", "=", field.result_id }, new object[] { "value", "=", op.parameters["value"] } }, new[] { "id", "name", "value", "sequence" }, 2, null); Dictionary<string, object> values = JsonUtil.Dictionary("name", op.parameters["name"], "sequence", op.parameters["sequence"]); if (rows.Count == 0) { values["field_id"] = field.result_id; values["value"] = op.parameters["value"]; op.result_id = client.Create("ir.model.fields.selection", values); } else if (rows.Count == 1) { op.result_id = Id(rows[0]); if (!client.Write("ir.model.fields.selection", new[] { op.result_id }, values)) throw new ConsultantException("selection_update_failed", "No se actualizó una opción de selección."); } else throw new ConsultantException("selection_collision", "La opción de selección es ambigua."); op.status = "applied"; }
        private static void AddCompute(Manifest manifest, Dictionary<string, object> parameters, Dictionary<string, object> values) { string file = JsonUtil.GetString(parameters, "compute_file", false); if (string.IsNullOrWhiteSpace(file)) return; string path = ContainedFile(manifest.root, file); if (ReplicaService.FileHash(path) != JsonUtil.GetString(parameters, "compute_sha256", true)) throw new ConsultantException("replica_file_hash_mismatch", "El código calculado cambió antes de aplicar."); values["compute"] = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n").Replace("\r", "\n"); }
        private static void Write(OdooClient client, string model, ChangeOperation op, Dictionary<string, object> values) { if (op.result_id <= 0 || !client.Write(model, new[] { op.result_id }, values)) throw new ConsultantException("metadata_write_failed", "Odoo no confirmó la actualización de " + model + "."); op.status = "applied"; }
        private static bool RecoverCreate(OdooClient client, ChangeOperation op, string model, object[] domain) { IList<Dictionary<string, object>> rows = client.SearchRead(model, domain, new[] { "id" }, 2, null); if (rows.Count == 0) return false; if (rows.Count > 1) throw new ConsultantException("recovery_ambiguous", "La recuperación idempotente encontró más de un componente."); op.result_id = Id(rows[0]); op.status = "applied"; return true; }
        private static object[] MenuDomain(string name, long parentId)
        {
            List<object> domain = new List<object> { new object[] { "name", "=", name }, new object[] { "parent_id", "=", parentId > 0 ? (object)parentId : false } };
            return domain.ToArray();
        }
        private static bool RecoverMenu(OdooClient client, ChangeOperation op, Dictionary<string, object> values)
        {
            long parentId = JsonUtil.GetLong(values, "parent_id", false); string action = JsonUtil.GetString(values, "action", false); long actionId = 0; if (!string.IsNullOrWhiteSpace(action)) long.TryParse(action.Substring(action.LastIndexOf(',') + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out actionId);
            IList<Dictionary<string, object>> rows = client.SearchRead("ir.ui.menu", MenuDomain(JsonUtil.GetString(values, "name", true), parentId), new[] { "id", "name", "active", "sequence", "parent_id", "action", "web_icon" }, 2, null);
            if (rows.Count == 0) return false; if (rows.Count > 1) throw new ConsultantException("recovery_ambiguous", "La recuperación idempotente encontró más de un menú.");
            Dictionary<string, object> desired = new Dictionary<string, object>(op.parameters, StringComparer.Ordinal); desired["resolved_parent_id"] = parentId; desired["resolved_action_id"] = actionId;
            if (!DesiredMatches("menu", desired, rows[0]) || !IsCustomOwned(client, "ir.ui.menu", Id(rows[0]))) throw new ConsultantException("recovery_collision", "Apareció un menú incompatible después de comprobar las precondiciones.");
            op.result_id = Id(rows[0]); op.status = "applied"; return true;
        }
        private static bool IsCustomOwned(OdooClient client, string model, long id) { IList<Dictionary<string, object>> rows = client.SearchRead("ir.model.data", new object[] { new object[] { "model", "=", model }, new object[] { "res_id", "=", id } }, new[] { "id", "module", "name" }, 10, null); if (rows.Count == 0) return true; return rows.All(delegate(Dictionary<string, object> row) { string module = Convert.ToString(row["module"], CultureInfo.InvariantCulture); return module == "studio_customization" || module == "__export__"; }); }
        private static bool FieldHasNoStoredValues(OdooClient client, string model, string field)
        {
            try { return client.SearchRead(model, new object[] { new object[] { field, "!=", false } }, new[] { "id" }, 1, null).Count == 0; }
            catch { return false; }
        }
        private static void ValidateFieldDependencies(OdooClient client, Manifest manifest, Dictionary<string, object> field, List<string> blocked)
        {
            string model = JsonUtil.GetString(field, "model", true), type = JsonUtil.GetString(field, "type", true);
            string related = JsonUtil.GetString(field, "related", false);
            if (!string.IsNullOrWhiteSpace(related))
            {
                Dictionary<string, object> terminal = ResolveFieldPath(client, manifest, model, related, blocked);
                if (terminal != null && (JsonUtil.GetString(terminal, "type", true) != type || (JsonUtil.GetString(field, "relation", false) ?? string.Empty) != (JsonUtil.GetString(terminal, "relation", false) ?? string.Empty))) blocked.Add("related incompatible: " + model + "." + JsonUtil.GetString(field, "name", true));
            }
            string depends = JsonUtil.GetString(field, "depends", false);
            if (!string.IsNullOrWhiteSpace(depends)) foreach (string path in depends.Split(',')) { string trimmed = path.Trim(); if (trimmed.Length == 0 || trimmed == "id") blocked.Add("depends inválido en " + model); else ResolveFieldPath(client, manifest, model, trimmed, blocked); }
            if (type == "monetary")
            {
                Dictionary<string, object> currency = ResolveField(client, manifest, model, JsonUtil.GetString(field, "currency_field", true));
                if (currency == null || JsonUtil.GetString(currency, "type", true) != "many2one" || JsonUtil.GetString(currency, "relation", true) != "res.currency") blocked.Add("currency_field inválido en " + model + "." + JsonUtil.GetString(field, "name", true));
            }
            if (type == "one2many")
            {
                string relation = JsonUtil.GetString(field, "relation", true), inverse = JsonUtil.GetString(field, "relation_field", true);
                Dictionary<string, object> target = ResolveField(client, manifest, relation, inverse);
                if (target == null || JsonUtil.GetString(target, "type", true) != "many2one" || JsonUtil.GetString(target, "relation", true) != model) blocked.Add("campo inverso one2many inválido: " + relation + "." + inverse);
            }
            if (type == "binary" && !string.IsNullOrWhiteSpace(JsonUtil.GetString(field, "filename_field", false)))
            {
                string filename = JsonUtil.GetString(field, "filename_field", true);
                Dictionary<string, object> auxiliary = ResolveField(client, manifest, model, filename);
                if (auxiliary == null || JsonUtil.GetString(auxiliary, "type", true) != "char") blocked.Add("filename_field inválido en " + model + "." + JsonUtil.GetString(field, "name", true));
            }
        }
        private static Dictionary<string, object> ResolveFieldPath(OdooClient client, Manifest manifest, string model, string path, List<string> blocked)
        {
            Dictionary<string, object> current = null; string currentModel = model; string[] names = path.Split('.');
            for (int i = 0; i < names.Length; i++)
            {
                current = ResolveField(client, manifest, currentModel, names[i]);
                if (current == null) { blocked.Add("campo de dependencia ausente: " + currentModel + "." + names[i]); return null; }
                if (i < names.Length - 1) { string relation = JsonUtil.GetString(current, "relation", false); if (string.IsNullOrWhiteSpace(relation)) { blocked.Add("ruta no relacional: " + path); return null; } currentModel = relation; }
            }
            return current;
        }
        private static Dictionary<string, object> ResolveField(OdooClient client, Manifest manifest, string model, string name)
        {
            Dictionary<string, object> declared = manifest.fields.FirstOrDefault(delegate(Dictionary<string, object> x) { return JsonUtil.GetString(x, "model", true) == model && JsonUtil.GetString(x, "name", true) == name && Mode(x, false) != "deactivate"; });
            if (declared != null) return JsonUtil.Dictionary("type", JsonUtil.GetString(declared, "type", true), "relation", JsonUtil.GetString(declared, "relation", false));
            if (name == "x_name" && manifest.models.Any(delegate(Dictionary<string, object> x) { return JsonUtil.GetString(x, "model", true) == model && Mode(x, false) == "create"; })) return JsonUtil.Dictionary("type", "char", "relation", null);
            IList<Dictionary<string, object>> rows = client.SearchRead("ir.model.fields", new object[] { new object[] { "model", "=", model }, new object[] { "name", "=", name } }, new[] { "id", "ttype", "relation" }, 2, null);
            return rows.Count == 1 ? JsonUtil.Dictionary("type", Convert.ToString(rows[0]["ttype"], CultureInfo.InvariantCulture), "relation", rows[0]["relation"] is bool ? null : Convert.ToString(rows[0]["relation"], CultureInfo.InvariantCulture)) : null;
        }
        private static Dictionary<string, object> RestorableValues(string model, Dictionary<string, object> previous) { string[] allowed = model == "ir.model" ? new[] { "name", "order", "info", "fold_name" } : model == "ir.model.fields" ? new[] { "field_description", "help", "required", "readonly", "copied", "index", "domain", "translate", "group_expand", "selectable", "sanitize", "sanitize_tags", "sanitize_attributes", "sanitize_style", "sanitize_form", "strip_style", "strip_classes" } : model == "ir.ui.view" ? new[] { "name", "arch", "priority", "active" } : model == "ir.actions.act_window" ? new[] { "name", "view_mode", "target", "domain", "context", "limit", "help" } : model == "ir.ui.menu" ? new[] { "name", "active", "sequence", "parent_id", "action", "web_icon" } : new[] { "name", "sequence" }; HashSet<string> include = new HashSet<string>(allowed, StringComparer.Ordinal); return previous.Where(delegate(KeyValuePair<string, object> x) { return include.Contains(x.Key); }).ToDictionary(delegate(KeyValuePair<string, object> x) { return x.Key; }, delegate(KeyValuePair<string, object> x) { return x.Value; }, StringComparer.Ordinal); }
        private static bool RestoredValuesMatch(Dictionary<string, object> expected, Dictionary<string, object> actual)
        {
            foreach (KeyValuePair<string, object> item in expected)
            {
                if (!actual.ContainsKey(item.Key)) return false;
                if (item.Key == "arch") { if (NormalizeXml(Convert.ToString(item.Value, CultureInfo.InvariantCulture)) != NormalizeXml(Text(actual, item.Key))) return false; }
                else if (JsonUtil.CanonicalHash(NormalizeValue(item.Value)) != JsonUtil.CanonicalHash(NormalizeValue(actual[item.Key]))) return false;
            }
            return true;
        }
        private static Dictionary<string, object> Values(ChangeOperation op, IEnumerable<string> excluded) { HashSet<string> skip = new HashSet<string>(excluded, StringComparer.Ordinal); skip.Add("component_key"); skip.Add("structural_change"); return op.parameters.Where(delegate(KeyValuePair<string, object> x) { return !skip.Contains(x.Key); }).ToDictionary(delegate(KeyValuePair<string, object> x) { return x.Key; }, delegate(KeyValuePair<string, object> x) { return x.Value; }, StringComparer.Ordinal); }
        private static string TechnicalModel(string operation) { if (operation.Contains("custom_model")) return "ir.model"; if (operation.Contains("custom_field")) return "ir.model.fields"; if (operation == "upsert_selection_option") return "ir.model.fields.selection"; if (operation.Contains("window_action")) return "ir.actions.act_window"; if (operation.Contains("menu")) return "ir.ui.menu"; return "ir.ui.view"; }
        private static string[] VerificationFields(string model)
        {
            if (model == "ir.model") return new[] { "id", "model", "name", "order", "info", "state", "fold_name" };
            if (model == "ir.model.fields") return FieldReadFields.Concat(new[] { "compute" }).Distinct().ToArray();
            if (model == "ir.model.fields.selection") return new[] { "id", "name", "value", "sequence" };
            if (model == "ir.ui.view") return new[] { "id", "name", "model", "type", "inherit_id", "arch", "mode", "priority", "active" };
            if (model == "ir.actions.act_window") return new[] { "id", "name", "res_model", "view_mode", "target", "domain", "context", "limit", "help" };
            return new[] { "id", "name", "active", "sequence", "parent_id", "action", "web_icon" };
        }
        private static bool FieldOperationMatches(Dictionary<string, object> expected, Dictionary<string, object> actual)
        {
            if (Text(actual, "state") != "manual") return false;
            foreach (string key in new[] { "name", "field_description", "ttype", "relation", "relation_field", "relation_table", "column1", "column2", "domain", "related", "depends", "currency_field", "on_delete" })
                if (expected.ContainsKey(key) && Text(actual, key) != Text(expected, key)) return false;
            foreach (string key in new[] { "required", "readonly", "store", "copied", "index", "translate", "group_expand", "selectable", "sanitize", "sanitize_tags", "sanitize_attributes", "sanitize_style", "sanitize_form", "strip_style", "strip_classes" })
                if (expected.ContainsKey(key) && Convert.ToBoolean(actual[key], CultureInfo.InvariantCulture) != JsonUtil.GetBool(expected, key, false)) return false;
            string computeHash = JsonUtil.GetString(expected, "compute_sha256", false);
            if (!string.IsNullOrWhiteSpace(computeHash))
            {
                string actualHash = actual.ContainsKey("compute") ? JsonUtil.Sha256(Text(actual, "compute").Replace("\r\n", "\n").Replace("\r", "\n")) : Text(actual, "compute_hash");
                if (actualHash != computeHash) return false;
            }
            return true;
        }

        private object ProductionRunbook(Manifest manifest) { string id = "run-" + Guid.NewGuid().ToString("N"); StringBuilder text = new StringBuilder(); text.AppendLine("# Promoción manual de réplica Fase 2 " + manifest.slug).AppendLine().AppendLine("- Entorno: `production`").AppendLine("- Hash: `" + manifest.hash + "`").AppendLine("- Escritura automática: bloqueada").AppendLine().AppendLine("## Componentes").AppendLine().AppendLine("- Modelos: " + manifest.models.Count).AppendLine("- Campos: " + manifest.fields.Count).AppendLine("- Vistas: " + manifest.views.Count).AppendLine("- Acciones de ventana: " + manifest.actions.Count).AppendLine("- Menús: " + manifest.menus.Count); File.WriteAllText(project.RunbookPath(id), text.ToString(), new UTF8Encoding(false)); AuditLog.Append(project, "production", "replica_preparar_paquete", id, "success", "Runbook Fase 2 generado sin red.", 0); return JsonUtil.Dictionary("environment", "production", "replica_slug", manifest.slug, "phase", 2, "execution", "manual_only", "runbook_id", id); }
        private OdooClient Open(string environment) { return project.OpenClient(environment, 30000); }
        private void Save(ChangeSet change) { JsonUtil.WriteJsonFile(project.ChangeSetPath(change.id), change); }

        private static string Mode(Dictionary<string, object> component, bool required) { string value = JsonUtil.GetString(component, "mode", required) ?? "create"; if (!ComponentModes.Contains(value)) throw new ConsultantException("invalid_component_mode", "mode debe ser create, update o deactivate."); return value; }
        private static string Operation(Dictionary<string, object> view) { string value = JsonUtil.GetString(view, "operation", false) ?? "create"; if (!ComponentModes.Contains(value)) throw new ConsultantException("invalid_component_mode", "operation debe ser create, update o deactivate."); return value; }
        private static void RequirePreviousHash(Dictionary<string, object> component) { string hash = JsonUtil.GetString(component, "previous_hash", true); if (!Regex.IsMatch(hash, "^[a-f0-9]{64}$")) throw new ConsultantException("invalid_previous_hash", "previous_hash debe ser SHA-256 hexadecimal."); }
        private static void ValidatePgName(string value, string label) { if (!Regex.IsMatch(value ?? string.Empty, "^[a-z][a-z0-9_]{1,62}$")) throw new ConsultantException("invalid_pg_name", label + " no es válido."); }
        private static void ValidateXmlId(string value) { if (!Regex.IsMatch(value ?? string.Empty, "^[a-z0-9_]+\\.[a-z0-9_.-]+$")) throw new ConsultantException("invalid_xml_id", "La referencia XML ID no es válida."); }
        private static void EnsureKeys(Dictionary<string, object> source, IEnumerable<string> keys) { foreach (string key in keys) JsonUtil.GetString(source, key, true); }
        private static void EnsureAllowedKeys(Dictionary<string, object> source, IEnumerable<string> allowed, string group)
        {
            HashSet<string> accepted = new HashSet<string>(allowed, StringComparer.Ordinal);
            string unknown = source.Keys.FirstOrDefault(delegate(string key) { return !accepted.Contains(key); });
            if (unknown != null) throw new ConsultantException("unknown_component_parameter", "El parámetro " + unknown + " no está permitido en " + group + ".");
        }
        private static void ValidatePhase2View(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml) || xml.Length > 100000) throw new ConsultantException("invalid_view_xml", "La vista está vacía o supera 100.000 caracteres.");
            XmlDocument document = new XmlDocument(); document.XmlResolver = null;
            try
            {
                XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 100000 };
                using (StringReader input = new StringReader(xml)) using (XmlReader reader = XmlReader.Create(input, settings)) document.Load(reader);
            }
            catch { throw new ConsultantException("invalid_view_xml", "La arquitectura de la vista no es XML válido."); }
            string lower = xml.ToLowerInvariant();
            foreach (string token in new[] { "<!doctype", "<!entity", "<script", "t-raw=", "t-call=", "type=\"object\"", "type='object'" })
                if (lower.Contains(token)) throw new ConsultantException("unsafe_view_xml", "La vista contiene un elemento ejecutable no permitido: " + token + ".");
            if (document.SelectNodes("//button").Count > 0) throw new ConsultantException("phase_not_available", "Los botones ejecutables de vistas Fase 2 deben declararse mediante los adaptadores de acciones de la Fase 3.");
        }
        private static void ValidateLogicalReferences(Manifest manifest)
        {
            ValidateUniqueKeys(manifest.models, "models"); ValidateUniqueKeys(manifest.fields, "fields"); ValidateUniqueKeys(manifest.views, "views"); ValidateUniqueKeys(manifest.actions, "window_actions"); ValidateUniqueKeys(manifest.menus, "menus");
            HashSet<string> actionKeys = new HashSet<string>(manifest.actions.Select(delegate(Dictionary<string, object> x) { return JsonUtil.GetString(x, "key", true); }), StringComparer.Ordinal);
            HashSet<string> priorMenus = new HashSet<string>(StringComparer.Ordinal);
            foreach (Dictionary<string, object> menu in manifest.menus)
            {
                string parent = JsonUtil.GetString(menu, "parent_key", false); if (!string.IsNullOrWhiteSpace(parent) && !priorMenus.Contains(parent)) throw new ConsultantException("dependency_order_invalid", "El menú padre " + parent + " debe declararse antes que su hijo.");
                string action = JsonUtil.GetString(menu, "action_key", false); if (!string.IsNullOrWhiteSpace(action) && !actionKeys.Contains(action)) throw new ConsultantException("unknown_action_key", "El menú referencia una acción de ventana no declarada.");
                priorMenus.Add(JsonUtil.GetString(menu, "key", true));
            }
        }
        private static void ValidateUniqueKeys(IEnumerable<Dictionary<string, object>> components, string group) { HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal); foreach (Dictionary<string, object> component in components) { string key = JsonUtil.GetString(component, "key", true); if (!Regex.IsMatch(key, "^[a-z][a-z0-9_]{1,62}$") || !keys.Add(key)) throw new ConsultantException("invalid_component_key", "Las claves de " + group + " deben ser técnicas, estables y únicas."); } }
        private static void ValidateOrder(string order) { foreach (string raw in order.Split(',')) { string token = raw.Trim(); if (!Regex.IsMatch(token, "^(?:id|x_[a-z0-9_]{2,60})(?: (?:asc|desc))?$", RegexOptions.IgnoreCase)) throw new ConsultantException("invalid_model_order", "order solo admite id o campos x_ con asc/desc."); } }
        private static void ValidateExpression(string value, char opening, char closing, string label) { string text = (value ?? string.Empty).Trim(); if (text.Length > 5000 || text.Length < 2 || text[0] != opening || text[text.Length - 1] != closing || Regex.IsMatch(text, "__|\\b(?:import|lambda|exec|eval|open)\\b", RegexOptions.IgnoreCase)) throw new ConsultantException("invalid_expression", label + " no cumple el contrato declarativo permitido."); }
        private static void Add(List<string> values, string value) { if (!values.Contains(value)) values.Add(value); }
        private static string NewOp() { return "op-" + Guid.NewGuid().ToString("N"); }
        private static bool SafeAccess(OdooClient client, string model, string operation) { try { return client.CheckAccessRights(model, operation); } catch { return false; } }
        private static long Id(Dictionary<string, object> row) { return Convert.ToInt64(row["id"], CultureInfo.InvariantCulture); }
        private static long RelationId(object value) { IEnumerable sequence = value as IEnumerable; if (sequence != null && !(value is string)) foreach (object item in sequence) { long parsed; if (long.TryParse(Convert.ToString(item, CultureInfo.InvariantCulture), out parsed)) return parsed; } long direct; return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out direct) ? direct : 0; }
        private static Dictionary<string, object> Dict(IDictionary<string, object> source, string key, bool required) { object value; Dictionary<string, object> result = source != null && source.TryGetValue(key, out value) ? value as Dictionary<string, object> : null; if (required && result == null) throw new ConsultantException("invalid_replica_manifest", "Falta el objeto " + key + "."); return result; }
        private static List<Dictionary<string, object>> DictList(IDictionary<string, object> source, string key) { object value = null; List<Dictionary<string, object>> result = new List<Dictionary<string, object>>(); IEnumerable seq = source != null && source.TryGetValue(key, out value) ? value as IEnumerable : null; if (seq != null && !(value is string)) foreach (object item in seq) { Dictionary<string, object> row = item as Dictionary<string, object>; if (row == null) throw new ConsultantException("invalid_replica_manifest", key + " debe contener objetos."); result.Add(row); } return result; }
        private static List<string> StringList(IDictionary<string, object> source, string key) { object value = null; List<string> result = new List<string>(); if (source == null || !source.TryGetValue(key, out value) || value == null) return result; IEnumerable sequence = value as IEnumerable; if (sequence == null || value is string) throw new ConsultantException("invalid_replica_manifest", key + " debe ser una lista."); foreach (object item in sequence) { string text = item as string; if (string.IsNullOrWhiteSpace(text)) throw new ConsultantException("invalid_replica_manifest", key + " debe contener textos no vacíos."); result.Add(text.Trim()); } return result; }
        private static string ContainedFile(string root, string relative) { if (Path.IsPathRooted(relative ?? string.Empty)) throw new ConsultantException("external_replica_path", "La réplica no admite rutas absolutas."); string path = Path.GetFullPath(Path.Combine(root, relative)); string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) throw new ConsultantException("external_replica_path", "El archivo queda fuera de la réplica o no existe."); return path; }
        private static void ValidateFile(Manifest manifest, string relative, string expected) { string path = ContainedFile(manifest.root, relative); if (!Regex.IsMatch(expected ?? string.Empty, "^[a-f0-9]{64}$") || ReplicaService.FileHash(path) != expected) throw new ConsultantException("replica_file_hash_mismatch", "Un archivo de la réplica no coincide con su SHA-256."); manifest.fileHashes[relative] = expected; }
        private static string LoadText(Manifest manifest, Dictionary<string, object> source, string inlineKey, string fileKey, string hashKey, bool required) { string file = JsonUtil.GetString(source, fileKey, false); if (!string.IsNullOrWhiteSpace(file)) { ValidateFile(manifest, file, JsonUtil.GetString(source, hashKey, true)); return File.ReadAllText(ContainedFile(manifest.root, file), Encoding.UTF8).Replace("\r\n", "\n").Replace("\r", "\n"); } string value = JsonUtil.GetString(source, inlineKey, required); if (!string.IsNullOrWhiteSpace(value) && source.ContainsKey(hashKey) && JsonUtil.Sha256(value.Replace("\r\n", "\n").Replace("\r", "\n")) != JsonUtil.GetString(source, hashKey, true)) throw new ConsultantException("replica_file_hash_mismatch", "El XML inline no coincide con su SHA-256."); return value; }

        private static string Integrity(ChangeSet change)
        {
            Dictionary<string, object> d = JsonUtil.Dictionary("schema_version", change.schema_version, "id", change.id, "client_slug", change.client_slug, "environment", change.environment, "category", change.category, "model", change.model, "target_models", change.target_models, "replica_slug", change.replica_slug, "replica_manifest_hash", change.replica_manifest_hash, "replica_descriptor", change.replica_descriptor, "created_at", change.created_at, "expires_at", change.expires_at, "precondition_hash", change.precondition_hash, "capability_hash", change.capability_hash, "profile_fingerprint", change.profile_fingerprint, "policy_version", change.policy_version, "operations", change.operations.Select(delegate(ChangeOperation x) { return JsonUtil.Dictionary("operation_id", x.operation_id, "type", x.type, "model", x.model, "parameters", x.parameters); }).ToArray());
            if (!string.IsNullOrEmpty(change.backup_reference)) d["backup_reference"] = change.backup_reference;
            return JsonUtil.CanonicalHash(d);
        }

        private static void ValidateConfirmation(ChangeSet change, string confirmation, string verb)
        {
            DateTimeOffset expires; if (!DateTimeOffset.TryParse(change.expires_at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expires) || DateTimeOffset.UtcNow > expires) throw new ConsultantException("confirmation_expired", "La confirmación venció. Prepara un paquete nuevo.");
            string expected = string.Equals(change.category, "production_promotion", StringComparison.Ordinal) ? "PROMOVER-PRODUCCION " + change.id + " RESPALDO-CONFIRMADO" : verb + " " + change.id;
            if (!string.Equals(confirmation, expected, StringComparison.Ordinal)) throw new ConsultantException("confirmation_mismatch", "La confirmación debe ser exactamente " + expected + ".");
        }

        private static object Summary(ChangeSet change, bool confirmation)
        {
            Dictionary<string, object> result = JsonUtil.Dictionary("package_id", change.id, "environment", change.environment, "replica_slug", change.replica_slug, "phase", 2, "status", change.status, "created_at", change.created_at, "expires_at", change.expires_at, "manifest_hash", change.replica_manifest_hash, "required_references", change.replica_descriptor == null ? null : change.replica_descriptor["required_references"], "operations", change.operations, "warnings", change.warnings, "last_error", change.last_error);
            if (!string.IsNullOrEmpty(change.backup_reference)) result["backup_reference"] = change.backup_reference;
            if (confirmation) result["required_confirmation"] = string.Equals(change.category, "production_promotion", StringComparison.Ordinal) ? "PROMOVER-PRODUCCION " + change.id + " RESPALDO-CONFIRMADO" : "APLICAR " + change.id;
            return result;
        }
        private static string SafeError(Exception error) { return Redactor.SafeError(error); }

        private static readonly string[] FieldReadFields = new[] { "id", "model", "name", "field_description", "help", "ttype", "required", "readonly", "store", "copied", "index", "state", "relation", "relation_field", "relation_table", "column1", "column2", "domain", "related", "depends", "currency_field", "on_delete", "translate", "group_expand", "selectable", "sanitize", "sanitize_tags", "sanitize_attributes", "sanitize_style", "sanitize_form", "strip_style", "strip_classes" };
        private sealed class Manifest { public string slug; public string root; public string hash; public Dictionary<string, object> raw; public List<Dictionary<string, object>> models; public List<Dictionary<string, object>> fields; public List<Dictionary<string, object>> views; public List<Dictionary<string, object>> actions; public List<Dictionary<string, object>> menus; public List<string> targetModels; public HashSet<string> requiredReferences; public Dictionary<string, string> fileHashes = new Dictionary<string, string>(StringComparer.Ordinal); }
        private sealed class Analysis { public List<Dictionary<string, object>> components; public List<string> blocked; public Dictionary<string, long> modelIds; public Dictionary<string, long> parentViewIds; public string preconditionHash; public string capabilityHash; }
    }
}
