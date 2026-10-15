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
    public sealed class ReplicaPhase3Service
    {
        public const string PolicyVersion = "replica-policy-3.1";
        private static readonly Regex KeyPattern = new Regex("^[a-z][a-z0-9_]{1,62}$", RegexOptions.Compiled);
        private static readonly Regex ReferencePattern = new Regex("^[A-Z][A-Z0-9_]{1,62}$", RegexOptions.Compiled);
        private static readonly Regex ShaPattern = new Regex("^[a-f0-9]{64}$", RegexOptions.Compiled);
        private static readonly Regex PlaceholderPattern = new Regex("^\\{\\{([A-Z][A-Z0-9_]{1,62})\\}\\}$", RegexOptions.Compiled);
        private static readonly Regex FieldPattern = new Regex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.Compiled);
        private static readonly Regex SecretFieldPattern = new Regex("(?:password|passwd|token|secret|api[_-]?key|private[_-]?key|credential)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly HashSet<string> Modes = new HashSet<string>(new[] { "create", "update", "deactivate" }, StringComparer.Ordinal);
        private static readonly HashSet<string> ActionStates = new HashSet<string>(new[] { "code", "multi", "next_activity", "mail_post", "webhook" }, StringComparer.Ordinal);
        private static readonly HashSet<string> Triggers = new HashSet<string>(new[]
        {
            "on_stage_set", "on_user_set", "on_tag_set", "on_state_set", "on_priority_set", "on_archive", "on_unarchive",
            "on_create", "on_create_or_write", "on_write", "on_unlink", "on_change", "on_time", "on_time_created",
            "on_time_updated", "on_message_received", "on_message_sent", "on_webhook"
        }, StringComparer.Ordinal);
        private readonly ProjectStore project;

        public ReplicaPhase3Service(ProjectStore value) { project = value; }

        public static bool IsPhase3(ProjectStore project, string slug)
        {
            try
            {
                string path = Path.Combine(project.ReplicaPath(slug), "manifest.json");
                if (!File.Exists(path)) return false;
                Dictionary<string, object> raw = JsonUtil.ReadJsonFile<Dictionary<string, object>>(path);
                return JsonUtil.GetLong(raw, "schema_version", false) == 2 && JsonUtil.GetLong(raw, "phase", false) == 3;
            }
            catch { return false; }
        }

        public object Analyze(string environment, string slug)
        {
            environment = ProjectStore.NormalizeEnvironment(environment);
            Manifest manifest = Load(slug);
            using (OdooClient client = Open(environment))
            {
                Analysis analysis = AnalyzeInternal(client, manifest);
                AuditLog.Append(project, environment, "replica_analizar_destino", null, analysis.blocked.Count == 0 ? "success" : "blocked",
                    "Réplica Fase 3 analizada; componentes=" + analysis.components.Count.ToString(CultureInfo.InvariantCulture) + "; bloqueos=" + analysis.blocked.Count.ToString(CultureInfo.InvariantCulture) + ".", 0);
                return JsonUtil.Dictionary(
                    "environment", environment,
                    "replica_slug", slug,
                    "phase", 3,
                    "manifest_hash", manifest.hash,
                    "classification", analysis.blocked.Count == 0 ? "ready" : "blocked",
                    "components", PublicComponents(analysis.components),
                    "blocked", analysis.blocked,
                    "required_references", PublicReferences(manifest),
                    "external_effects", JsonUtil.Dictionary("actions_to_test", manifest.externalTestActionKeys.ToArray(), "automations_gated", manifest.externalAutomationKeys.ToArray(), "inbound_webhooks", manifest.inboundAutomationKeys.ToArray()),
                    "precondition_hash", analysis.preconditionHash,
                    "capability_hash", analysis.capabilityHash);
            }
        }

        internal ChangeSet PreparePromotion(string slug, string backupReference, IDictionary<string, object> references)
        {
            Manifest manifest = Load(slug);
            ConnectionProfile profile = project.GetProfile("production", true);
            using (OdooClient client = Open("production"))
            {
                Analysis analysis = AnalyzeInternal(client, manifest);
                if (analysis.blocked.Count > 0) throw new ConsultantException("replica_blocked", "La réplica Fase 3 tiene componentes bloqueados en producción: " + string.Join("; ", analysis.blocked.ToArray()));
                string id = "prom-" + Guid.NewGuid().ToString("N");
                DateTimeOffset now = DateTimeOffset.UtcNow;
                ChangeSet change = new ChangeSet
                {
                    schema_version = 3, id = id, client_slug = project.Config.client_slug, environment = "production",
                    category = "production_promotion", risk = "production_controlled_promotion", status = "prepared",
                    backup_reference = backupReference,
                    model = manifest.targetModels.First(), target_models = manifest.targetModels.OrderBy(delegate(string x) { return x; }, StringComparer.Ordinal).ToList(),
                    replica_slug = manifest.slug, replica_manifest_hash = manifest.hash,
                    replica_descriptor = JsonUtil.Dictionary(
                        "manifest_schema", 2,
                        "phase", 3,
                        "required_references", PublicReferences(manifest),
                        "external_test_action_keys", manifest.externalTestActionKeys.ToArray(),
                        "external_automation_keys", manifest.externalAutomationKeys.ToArray(),
                        "inbound_automation_keys", manifest.inboundAutomationKeys.ToArray()),
                    created_at = now.ToString("o", CultureInfo.InvariantCulture),
                    expires_at = now.AddHours(2).ToString("o", CultureInfo.InvariantCulture),
                    precondition_hash = analysis.preconditionHash, capability_hash = analysis.capabilityHash,
                    profile_fingerprint = profile.credential_fingerprint, requested_by = Environment.UserName,
                    policy_version = PolicyVersion, objective = "Promover a producción de forma segura la réplica Fase 3 " + manifest.slug + ".",
                    contract_hash = manifest.hash,
                    impact = "Crea automatizaciones, plantillas de correo, acciones de servidor y actividades en producción.",
                    rollback_strategy = "Desactiva artefactos creados; no elimina registros ni metadatos.",
                    preconditions = new List<Dictionary<string, object>> { JsonUtil.Dictionary("type", "destination_hash", "expected", analysis.preconditionHash), JsonUtil.Dictionary("type", "capabilities_hash", "expected", analysis.capabilityHash) },
                    validations = new List<Dictionary<string, object>> { JsonUtil.Dictionary("type", "schema_2_phase_3", "required", true), JsonUtil.Dictionary("type", "files_sha256_reread", "required", true) },
                    operations = Compile(manifest, analysis),
                    warnings = new List<string> { "Producción: Se requiere confirmación reforzada con respaldo verificado.", "Respaldo registrado: " + backupReference, "Ventana de vigencia reducida: 2 horas.", "Confirmación exacta requerida: PROMOVER-PRODUCCION " + id + " RESPALDO-CONFIRMADO" }
                };
                change.integrity_hash = Integrity(change);
                ValidatePackage(change);
                JsonUtil.WriteJsonFile(project.ChangeSetPath(id), change);
                AuditLog.Append(project, "production", "promocion_preparar_paquete", id, "success", "Paquete de promoción Fase 3 preparado; operaciones=" + change.operations.Count.ToString(CultureInfo.InvariantCulture) + "; respaldo=" + backupReference + ".", 0);
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
                if (analysis.blocked.Count > 0) throw new ConsultantException("replica_blocked", "La réplica Fase 3 tiene componentes bloqueados: " + string.Join("; ", analysis.blocked.ToArray()));
                string id = "chg-" + Guid.NewGuid().ToString("N");
                DateTimeOffset now = DateTimeOffset.UtcNow;
                ChangeSet change = new ChangeSet
                {
                    schema_version = 3,
                    id = id,
                    client_slug = project.Config.client_slug,
                    environment = environment,
                    category = "validated_replica_phase3",
                    risk = "executable_controlled_staging",
                    status = "prepared",
                    model = manifest.targetModels.First(),
                    target_models = manifest.targetModels.OrderBy(delegate(string value) { return value; }, StringComparer.Ordinal).ToList(),
                    replica_slug = manifest.slug,
                    replica_manifest_hash = manifest.hash,
                    replica_descriptor = JsonUtil.Dictionary(
                        "manifest_schema", 2,
                        "phase", 3,
                        "required_references", PublicReferences(manifest),
                        "external_test_action_keys", manifest.externalTestActionKeys.ToArray(),
                        "external_automation_keys", manifest.externalAutomationKeys.ToArray(),
                        "inbound_automation_keys", manifest.inboundAutomationKeys.ToArray(),
                        "external_activation_scope", manifest.automations.Where(delegate(Dictionary<string, object> item) { return JsonUtil.GetBool(item, "external_effect", false) && DesiredAutomationActive(item); }).Select(delegate(Dictionary<string, object> item)
                        {
                            return JsonUtil.Dictionary("automation_key", JsonUtil.GetString(item, "key", true), "model", JsonUtil.GetString(item, "model", true), "trigger", JsonUtil.GetString(item, "trigger", true), "action_keys", StringList(item, "action_keys").ToArray());
                        }).ToArray()),
                    created_at = now.ToString("o", CultureInfo.InvariantCulture),
                    expires_at = now.AddMinutes(project.Config.package_confirmation_minutes).ToString("o", CultureInfo.InvariantCulture),
                    precondition_hash = analysis.preconditionHash,
                    capability_hash = analysis.capabilityHash,
                    profile_fingerprint = profile.credential_fingerprint,
                    requested_by = Environment.UserName,
                    policy_version = PolicyVersion,
                    objective = "Instalar los efectos controlados declarados por la réplica " + manifest.slug + ".",
                    contract_hash = manifest.hash,
                    impact = "Actualiza código calculado y crea o actualiza plantillas, acciones encadenadas y automatizaciones. Los efectos externos permanecen inactivos.",
                    rollback_strategy = "Restaura snapshots protegidos y neutraliza los artefactos creados; no elimina campos ni registros.",
                    preconditions = new List<Dictionary<string, object>>
                    {
                        JsonUtil.Dictionary("type", "destination_hash", "expected", analysis.preconditionHash),
                        JsonUtil.Dictionary("type", "capabilities_hash", "expected", analysis.capabilityHash)
                    },
                    validations = new List<Dictionary<string, object>>
                    {
                        JsonUtil.Dictionary("type", "schema_2_phase_3", "required", true),
                        JsonUtil.Dictionary("type", "files_sha256_reread", "required", true),
                        JsonUtil.Dictionary("type", "previous_hash_for_updates", "required", true),
                        JsonUtil.Dictionary("type", "external_effect_gate", "required", true)
                    },
                    operations = Compile(manifest, analysis),
                    warnings = new List<string>
                    {
                        "El código, los dominios y las plantillas se releerán desde replicas/ y se comprobarán por SHA-256 justo antes de escribir.",
                        "Los correos, webhooks y automatizaciones receptoras de webhooks se instalarán inactivos hasta completar una autorización reforzada.",
                        "Confirmación exacta: APLICAR " + id + "."
                    }
                };
                change.integrity_hash = Integrity(change);
                ValidatePackage(change);
                JsonUtil.WriteJsonFile(project.ChangeSetPath(id), change);
                AuditLog.Append(project, environment, "replica_preparar_paquete", id, "success", "Paquete Fase 3 preparado; operaciones=" + change.operations.Count.ToString(CultureInfo.InvariantCulture) + ".", 0);
                return Summary(change, true);
            }
        }

        public object Apply(ChangeSet change, string confirmation, IDictionary<string, object> references)
        {
            ValidatePackage(change);
            StudioService.EnsureWriteAllowed(change.environment, change.category);
            if (change.status == "applied" || change.status == "verified") return Summary(change, false);
            if (change.status != "prepared" && change.status != "failed_partial") throw new ConsultantException("package_not_applicable", "El paquete Fase 3 no se puede aplicar en su estado actual.");
            ValidateConfirmation(change, confirmation, "APLICAR");
            ConnectionProfile profile = project.GetProfile(change.environment, true);
            if (!string.Equals(profile.credential_fingerprint, change.profile_fingerprint, StringComparison.Ordinal)) throw new ConsultantException("profile_changed", "La credencial cambió desde la preparación.");
            Manifest manifest = Load(change.replica_slug);
            if (manifest.hash != change.replica_manifest_hash) throw new ConsultantException("replica_integrity_failed", "El manifiesto o uno de sus archivos cambió desde la preparación.");
            Dictionary<string, string> supplied = ValidateReferences(manifest, references);
            using (OdooClient client = Open(change.environment))
            {
                Analysis current = AnalyzeInternal(client, manifest);
                if (change.status == "prepared" && (current.preconditionHash != change.precondition_hash || current.capabilityHash != change.capability_hash)) throw new ConsultantException("precondition_changed", "La configuración o los permisos cambiaron desde la vista previa.");
                if (change.status == "failed_partial") ValidateResume(client, change);
                Dictionary<string, long> resolvedXmlIds = ResolveXmlReferences(client, manifest, supplied);
                change.status = "applying";
                Save(change);
                try
                {
                    foreach (ChangeOperation operation in change.operations)
                    {
                        if (operation.status == "applied" || operation.status == "reused" || operation.status == "external_pending") continue;
                        ApplyOperation(client, manifest, change, operation, supplied, resolvedXmlIds);
                        Save(change);
                    }
                    change.status = "applied";
                    change.applied_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    change.last_error = null;
                    if (change.operations.Any(delegate(ChangeOperation operation) { return operation.status == "external_pending"; })) change.uat_status = "external_authorization_pending";
                    Save(change);
                }
                catch (Exception error)
                {
                    change.status = "failed_partial";
                    change.last_error = SafeError(error);
                    Save(change);
                    throw;
                }
            }
            AuditLog.Append(project, change.environment, "studio_aplicar_paquete", change.id, "success",
                "Paquete Fase 3 aplicado; operaciones=" + change.operations.Count.ToString(CultureInfo.InvariantCulture) + "; efectos_externos=" + (change.uat_status ?? "none") + ".", 0);
            return Summary(change, false);
        }

        public object TestExternal(string packageId, string actionKey, long recordId, string confirmation)
        {
            ChangeSet change = LoadPackage(packageId);
            ValidatePackage(change);
            StudioService.EnsureWriteAllowed(change.environment);
            if (change.status != "applied" && change.status != "verified") throw new ConsultantException("package_not_testable", "El paquete Fase 3 debe estar aplicado antes de probar efectos externos.");
            if (!string.Equals(confirmation, "PROBAR-EFECTO " + packageId, StringComparison.Ordinal)) throw new ConsultantException("confirmation_mismatch", "La confirmación debe ser exactamente PROBAR-EFECTO " + packageId + ".");
            if (recordId <= 0) throw new ConsultantException("invalid_uat_target", "El registro de prueba debe tener un ID positivo.");
            List<string> allowed = DescriptorStrings(change, "external_test_action_keys");
            if (!allowed.Contains(actionKey)) throw new ConsultantException("external_action_not_allowed", "La acción no es un efecto externo de prueba declarado por este paquete.");
            ChangeOperation action = ComponentOperation(change, "server_action", actionKey);
            if (action.result_id <= 0) throw new ConsultantException("dependency_not_resolved", "La acción externa no tiene un ID técnico aplicado.");
            try
            {
                using (OdooClient client = Open(change.environment))
                {
                    EnsureExternalAutomationsInactive(client, change);
                    client.RunServerAction(action.result_id, action.model, recordId);
                }
                action.verification = "external_test_passed";
                change.uat_target_hash = JsonUtil.Sha256(action.model + ":" + recordId.ToString(CultureInfo.InvariantCulture));
                bool all = allowed.All(delegate(string key) { return ComponentOperation(change, "server_action", key).verification == "external_test_passed"; });
                change.uat_status = all ? "external_effects_tested" : "external_effect_test_partial";
                Save(change);
                AuditLog.Append(project, change.environment, "replica_probar_efecto_externo", change.id, "success", "Efecto externo probado; acción_hash=" + JsonUtil.Sha256(actionKey) + ".", 0);
                return Summary(change, false);
            }
            catch (Exception error)
            {
                change.uat_status = "external_effect_failed";
                change.last_error = SafeError(error);
                Save(change);
                AuditLog.Append(project, change.environment, "replica_probar_efecto_externo", change.id, "error", SafeError(error), 0);
                throw;
            }
        }

        public object ActivateExternal(string packageId, string confirmation, string manualTestConfirmation)
        {
            ChangeSet change = LoadPackage(packageId);
            ValidatePackage(change);
            StudioService.EnsureWriteAllowed(change.environment, change.category);
            if (change.status != "applied" && change.status != "verified") throw new ConsultantException("package_not_activatable", "El paquete Fase 3 debe estar aplicado antes de activar efectos externos.");
            string expectedConfirmation = string.Equals(change.category, "production_promotion", StringComparison.Ordinal) ? "ACTIVAR-EFECTOS-PRODUCCION " + packageId : "ACTIVAR-EFECTOS " + packageId;
            if (!string.Equals(confirmation, expectedConfirmation, StringComparison.Ordinal)) throw new ConsultantException("confirmation_mismatch", "La confirmación debe ser exactamente " + expectedConfirmation + ".");
            if (!string.Equals(change.category, "production_promotion", StringComparison.Ordinal))
            {
                foreach (string actionKey in DescriptorStrings(change, "external_test_action_keys"))
                    if (ComponentOperation(change, "server_action", actionKey).verification != "external_test_passed") throw new ConsultantException("external_test_required", "Falta probar una acción externa con PROBAR-EFECTO antes de activar su automatización.");
            }
            List<string> inbound = DescriptorStrings(change, "inbound_automation_keys");
            if (inbound.Count > 0 && !string.Equals(manualTestConfirmation, "PRUEBA-MANUAL " + packageId, StringComparison.Ordinal))
                throw new ConsultantException("manual_webhook_test_required", "Las automatizaciones receptoras de webhook requieren la confirmación adicional PRUEBA-MANUAL " + packageId + ".");
            List<ChangeOperation> gates = RequireExternalActivationGates(change);
            List<ChangeOperation> activated = new List<ChangeOperation>();
            try
            {
                using (OdooClient client = Open(change.environment))
                {
                    foreach (ChangeOperation operation in gates)
                    {
                        if (!JsonUtil.GetBool(operation.parameters, "desired_active", true)) throw new ConsultantException("external_activation_not_declared", "La réplica no declaró la activación final de esta automatización.");
                        if (operation.result_id <= 0 || !client.Write("base.automation", new[] { operation.result_id }, JsonUtil.Dictionary("active", true))) throw new ConsultantException("metadata_write_failed", "Odoo no confirmó la activación reforzada de la automatización.");
                        activated.Add(operation);
                        IList<Dictionary<string, object>> rows = client.Read("base.automation", new[] { operation.result_id }, new[] { "id", "active" });
                        if (rows.Count != 1 || !Bool(rows[0], "active")) throw new ConsultantException("external_activation_verification_failed", "Odoo no devolvió la automatización como activa después de escribirla.");
                    }
                }
                foreach (ChangeOperation operation in activated) operation.status = "applied";
            }
            catch (Exception error)
            {
                try
                {
                    using (OdooClient client = Open(change.environment))
                        foreach (ChangeOperation operation in activated)
                        {
                            client.Write("base.automation", new[] { operation.result_id }, JsonUtil.Dictionary("active", false));
                            operation.status = "external_pending";
                        }
                }
                catch { }
                change.uat_status = "external_activation_failed";
                change.last_error = SafeError(error);
                Save(change);
                AuditLog.Append(project, change.environment, "replica_activar_efectos_externos", change.id, "error", SafeError(error), 0);
                throw;
            }
            change.uat_status = "external_effects_armed";
            change.last_error = null;
            Save(change);
            AuditLog.Append(project, change.environment, "replica_activar_efectos_externos", change.id, "success", "Automatizaciones externas activadas tras autorización reforzada.", 0);
            return Summary(change, false);
        }

        public object Verify(ChangeSet change)
        {
            ValidatePackage(change);
            if (change.status != "applied" && change.status != "verified" && change.status != "rolled_back" && change.status != "rollback_partial") throw new ConsultantException("package_not_verifiable", "El paquete Fase 3 todavía no se ha aplicado.");
            bool all = true;
            Manifest manifest = Load(change.replica_slug);
            using (OdooClient client = Open(change.environment))
            {
                foreach (ChangeOperation operation in change.operations)
                {
                    bool ok = VerifyOperation(client, manifest, change, operation);
                    if (operation.verification != "external_test_passed") operation.verification = ok ? "verified" : "mismatch";
                    if (!ok && operation.status != "reused") all = false;
                }
            }
            if (all && change.status == "applied") change.status = "verified";
            change.verified_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            Save(change);
            AuditLog.Append(project, change.environment, "studio_verificar_paquete", change.id, all ? "success" : "error", "Verificación Fase 3 completada; resultado=" + (all ? "ok" : "mismatch") + ".", 0);
            return Summary(change, false);
        }

        public object Rollback(ChangeSet change, string confirmation)
        {
            ValidatePackage(change);
            StudioService.EnsureWriteAllowed(change.environment);
            if (!string.Equals(confirmation, "REVERTIR " + change.id, StringComparison.Ordinal)) throw new ConsultantException("confirmation_mismatch", "La confirmación debe ser exactamente REVERTIR " + change.id + ".");
            if (change.status == "rolled_back" || change.status == "rollback_partial") return Summary(change, false);
            if (change.status != "applied" && change.status != "verified") throw new ConsultantException("package_not_reversible", "Solo se pueden revertir paquetes Fase 3 aplicados.");
            bool partial = false;
            using (OdooClient client = Open(change.environment))
            {
                foreach (ChangeOperation operation in change.operations.AsEnumerable().Reverse())
                {
                    try { RollbackOperation(client, change, operation); }
                    catch { operation.status = "rollback_failed"; partial = true; }
                    if (operation.status == "preserved_manual_cleanup" || operation.status == "rollback_failed") partial = true;
                    Save(change);
                }
            }
            change.status = partial ? "rollback_partial" : "rolled_back";
            change.rolled_back_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            Save(change);
            AuditLog.Append(project, change.environment, "studio_revertir_paquete_seguro", change.id, partial ? "error" : "success", partial ? "Reversión Fase 3 parcial." : "Reversión Fase 3 completada.", 0);
            return Summary(change, false);
        }

        public static void ValidatePackage(ChangeSet change)
        {
            if (change == null || change.schema_version != 3 || (change.category != "validated_replica_phase3" && change.category != "production_promotion") || change.policy_version != PolicyVersion) throw new ConsultantException("unsupported_package_schema", "El paquete no corresponde a una réplica Fase 3.");
            if (change.operations == null || change.operations.Count == 0 || change.operations.Count > 250 || change.target_models == null || change.target_models.Count == 0) throw new ConsultantException("invalid_package", "El paquete Fase 3 está incompleto.");
            if (change.integrity_hash != Integrity(change)) throw new ConsultantException("package_integrity_failed", "La parte inmutable del paquete Fase 3 fue modificada.");
            HashSet<string> allowed = new HashSet<string>(new[]
            {
                "update_computed_field", "create_mail_template", "update_mail_template", "deactivate_mail_template", "preserve_mail_template",
                "create_server_action", "update_server_action", "deactivate_server_action", "preserve_server_action", "link_action_chain",
                "create_view", "update_view", "deactivate_view", "preserve_view",
                "create_automation", "update_automation", "deactivate_automation", "preserve_automation", "bind_automation_action",
                "activate_internal_automation", "arm_external_automation"
            }, StringComparer.Ordinal);
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (ChangeOperation operation in change.operations)
            {
                if (!allowed.Contains(operation.type) || string.IsNullOrWhiteSpace(operation.operation_id) || !ids.Add(operation.operation_id)) throw new ConsultantException("tool_contract_violation", "El paquete Fase 3 contiene una operación no permitida o repetida.");
                StudioService.ValidateModel(operation.model);
                string serialized = JsonUtil.Serialize(operation.parameters);
                if (Regex.IsMatch(serialized, "https?://[^\\\"]+", RegexOptions.IgnoreCase) || Regex.IsMatch(serialized, "[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}", RegexOptions.IgnoreCase)) throw new ConsultantException("secret_in_package", "El paquete Fase 3 no puede persistir destinos externos; use referencias tipadas.");
            }
        }

        private Manifest Load(string slug)
        {
            string root = project.ReplicaPath(slug);
            Dictionary<string, object> raw = JsonUtil.ReadJsonFile<Dictionary<string, object>>(Path.Combine(root, "manifest.json"));
            if (JsonUtil.GetLong(raw, "schema_version", true) != 2 || JsonUtil.GetLong(raw, "phase", true) != 3) throw new ConsultantException("unsupported_replica_schema", "La Fase 3 exige schema_version 2 y phase 3.");
            EnsureAllowedKeys(raw, new[] { "schema_version", "phase", "name", "description", "source", "dependencies", "references", "computed_fields", "mail_templates", "server_actions", "views", "automations", "deployment_order", "excluded_artifacts" }, "manifest.json Fase 3");
            Dictionary<string, object> source = Dict(raw, "source", true);
            EnsureAllowedKeys(source, new[] { "environment", "odoo_version", "captured_on", "contains_business_records", "metadata_hashes" }, "source");
            if (JsonUtil.GetBool(source, "contains_business_records", true)) throw new ConsultantException("business_records_forbidden", "La réplica no puede contener registros de negocio.");
            string version = JsonUtil.GetString(source, "odoo_version", false);
            if (!string.IsNullOrWhiteSpace(version) && version != "19") throw new ConsultantException("unsupported_odoo_version", "La Fase 3 solo admite Odoo 19.");
            Manifest manifest = new Manifest
            {
                slug = slug,
                root = root,
                raw = raw,
                references = Dict(raw, "references", false) ?? new Dictionary<string, object>(StringComparer.Ordinal),
                computedFields = DictList(raw, "computed_fields"),
                templates = DictList(raw, "mail_templates"),
                actions = DictList(raw, "server_actions"),
                views = DictList(raw, "views"),
                automations = DictList(raw, "automations"),
                targetModels = new List<string>(),
                externalTestActionKeys = new List<string>(),
                externalAutomationKeys = new List<string>(),
                inboundAutomationKeys = new List<string>()
            };
            ValidateDependencies(raw, manifest);
            ValidateReferences(manifest);
            ValidateUniqueKeys(manifest.computedFields, "computed_fields");
            ValidateUniqueKeys(manifest.templates, "mail_templates");
            ValidateUniqueKeys(manifest.actions, "server_actions");
            ValidateUniqueKeys(manifest.views, "views");
            ValidateUniqueKeys(manifest.automations, "automations");
            foreach (Dictionary<string, object> field in manifest.computedFields) ValidateComputedField(manifest, field);
            foreach (Dictionary<string, object> template in manifest.templates) ValidateTemplate(manifest, template);
            foreach (Dictionary<string, object> action in manifest.actions) ValidateAction(manifest, action);
            foreach (Dictionary<string, object> view in manifest.views) ValidateView(manifest, view);
            foreach (Dictionary<string, object> automation in manifest.automations) ValidateAutomation(manifest, automation);
            ValidateLogicalDependencies(manifest);
            foreach (Dictionary<string, object> template in manifest.templates) template["ownership_marker"] = "OdooAI replica " + manifest.slug + "/" + JsonUtil.GetString(template, "key", true);
            foreach (Dictionary<string, object> action in manifest.actions) action["ownership_marker"] = "OdooAI replica " + manifest.slug + "/" + JsonUtil.GetString(action, "key", true);
            foreach (Dictionary<string, object> view in manifest.views) view["ownership_marker"] = "OdooAI replica " + manifest.slug + "/" + JsonUtil.GetString(view, "key", true);
            foreach (Dictionary<string, object> automation in manifest.automations) automation["ownership_marker"] = "OdooAI replica " + manifest.slug + "/" + JsonUtil.GetString(automation, "key", true);
            if (manifest.computedFields.Count + manifest.templates.Count + manifest.actions.Count + manifest.views.Count + manifest.automations.Count == 0) throw new ConsultantException("empty_replica", "La réplica Fase 3 no contiene componentes.");
            List<string> order = StringList(raw, "deployment_order");
            string[] expected = new[] { "computed_fields", "mail_templates", "server_actions", "action_chains", "views", "automations", "automation_bindings", "activation_gates" };
            if (order.Count > 0 && !order.SequenceEqual(expected)) throw new ConsultantException("invalid_deployment_order", "deployment_order no respeta el orden cerrado de la Fase 3.");
            List<string> excluded = StringList(raw, "excluded_artifacts");
            if (excluded.Count == 0 || !excluded.Any(delegate(string item) { return Regex.IsMatch(item, "business|negocio|record", RegexOptions.IgnoreCase); })) throw new ConsultantException("business_exclusion_required", "excluded_artifacts debe excluir expresamente los registros de negocio.");
            if (manifest.targetModels.Count == 0) throw new ConsultantException("missing_target_model", "La réplica Fase 3 debe declarar al menos un modelo objetivo.");
            manifest.hash = JsonUtil.CanonicalHash(JsonUtil.Dictionary("manifest", raw, "file_hashes", manifest.fileHashes));
            return manifest;
        }

        private static void ValidateDependencies(Dictionary<string, object> raw, Manifest manifest)
        {
            Dictionary<string, object> dependencies = Dict(raw, "dependencies", false);
            if (dependencies == null) return;
            EnsureAllowedKeys(dependencies, new[] { "models", "features" }, "dependencies");
            foreach (string model in StringList(dependencies, "models")) { StudioService.ValidateModel(model); Add(manifest.targetModels, model); }
            foreach (string feature in StringList(dependencies, "features"))
                if (feature != "base_automation" && feature != "mail" && feature != "studio") throw new ConsultantException("unsupported_feature", "Dependencia Fase 3 no soportada: " + feature + ".");
        }

        private static void ValidateReferences(Manifest manifest)
        {
            foreach (KeyValuePair<string, object> item in manifest.references)
            {
                if (!ReferencePattern.IsMatch(item.Key)) throw new ConsultantException("invalid_reference_key", "La referencia " + item.Key + " no tiene una clave técnica válida.");
                Dictionary<string, object> spec = item.Value as Dictionary<string, object>;
                if (spec == null) throw new ConsultantException("invalid_reference", "Cada referencia debe declarar un objeto tipado.");
                EnsureAllowedKeys(spec, new[] { "type", "model", "purpose" }, "references." + item.Key);
                string type = JsonUtil.GetString(spec, "type", true);
                if (type != "email" && type != "https_url" && type != "xml_id") throw new ConsultantException("invalid_reference_type", "Tipo de referencia no permitido: " + type + ".");
                if (type == "xml_id") StudioService.ValidateModel(JsonUtil.GetString(spec, "model", true));
                else if (!string.IsNullOrWhiteSpace(JsonUtil.GetString(spec, "model", false))) throw new ConsultantException("invalid_reference", "Solo xml_id puede declarar model.");
            }
        }

        private static void ValidateComputedField(Manifest manifest, Dictionary<string, object> field)
        {
            EnsureAllowedKeys(field, new[] { "key", "model", "name", "mode", "previous_hash", "compute_file", "compute_sha256", "depends" }, "computed_fields");
            EnsureKeys(field, new[] { "key", "model", "name", "mode", "previous_hash", "compute_file", "compute_sha256", "depends" });
            if (Mode(field) != "update") throw new ConsultantException("computed_field_update_only", "La Fase 3 solo reemplaza código de campos calculados existentes; la creación corresponde a Fase 2.");
            string model = JsonUtil.GetString(field, "model", true); StudioService.ValidateModel(model); Add(manifest.targetModels, model);
            string name = JsonUtil.GetString(field, "name", true); if (!name.StartsWith("x_", StringComparison.Ordinal) || !FieldPattern.IsMatch(name)) throw new ConsultantException("invalid_custom_field", "El campo calculado debe ser un campo Studio x_.");
            RequirePreviousHash(field);
            ValidateFile(manifest, JsonUtil.GetString(field, "compute_file", true), JsonUtil.GetString(field, "compute_sha256", true), 200000);
            string depends = JsonUtil.GetString(field, "depends", true);
            foreach (string dependency in depends.Split(',')) if (!Regex.IsMatch(dependency.Trim(), "^[a-z][a-z0-9_]*(?:\\.[a-z][a-z0-9_]*)*$")) throw new ConsultantException("invalid_compute_depends", "depends contiene una ruta no válida.");
        }

        private static void ValidateTemplate(Manifest manifest, Dictionary<string, object> template)
        {
            EnsureAllowedKeys(template, new[] { "key", "name", "model", "mode", "previous_hash", "description", "subject_file", "subject_sha256", "body_html_file", "body_html_sha256", "email_from", "email_to", "email_cc", "reply_to", "use_default_to", "auto_delete", "active" }, "mail_templates");
            EnsureKeys(template, new[] { "key", "name", "model" });
            string model = JsonUtil.GetString(template, "model", true); StudioService.ValidateModel(model); Add(manifest.targetModels, model);
            string mode = Mode(template); if (mode != "create") RequirePreviousHash(template);
            if (mode != "deactivate")
            {
                ValidateFile(manifest, JsonUtil.GetString(template, "subject_file", true), JsonUtil.GetString(template, "subject_sha256", true), 20000);
                ValidateFile(manifest, JsonUtil.GetString(template, "body_html_file", true), JsonUtil.GetString(template, "body_html_sha256", true), 250000);
            }
            foreach (string key in new[] { "email_from", "email_to", "email_cc", "reply_to" })
            {
                string value = JsonUtil.GetString(template, key, false);
                if (!string.IsNullOrWhiteSpace(value) && PlaceholderPattern.IsMatch(value)) RequireReference(manifest, value, "email", null);
                else if (!string.IsNullOrWhiteSpace(value) && (key == "email_to" || key == "email_cc")) throw new ConsultantException("typed_reference_required", key + " debe usar una referencia {{CLAVE}}; no se persisten destinatarios en la réplica.");
            }
        }

        private static void ValidateAction(Manifest manifest, Dictionary<string, object> action)
        {
            EnsureAllowedKeys(action, new[] { "key", "name", "model", "state", "mode", "previous_hash", "sequence", "code_file", "code_sha256", "child_action_keys", "template_key", "mail_post_method", "mail_post_autofollow", "activity_type_reference", "activity_summary", "activity_note_file", "activity_note_sha256", "activity_deadline_range", "activity_deadline_unit", "activity_user_reference", "activity_user_field", "webhook_url", "webhook_fields" }, "server_actions");
            EnsureKeys(action, new[] { "key", "name", "model", "state" });
            string model = JsonUtil.GetString(action, "model", true); StudioService.ValidateModel(model); Add(manifest.targetModels, model);
            string state = JsonUtil.GetString(action, "state", true); if (!ActionStates.Contains(state)) throw new ConsultantException("unsupported_server_action_state", "Estado de acción no permitido: " + state + ".");
            string mode = Mode(action); if (mode != "create") RequirePreviousHash(action);
            long sequence = JsonUtil.GetLong(action, "sequence", false); if (sequence < 0 || sequence > 10000) throw new ConsultantException("invalid_sequence", "La secuencia de la acción no es válida.");
            if (state == "code") ValidateFile(manifest, JsonUtil.GetString(action, "code_file", true), JsonUtil.GetString(action, "code_sha256", true), 250000);
            if (state == "multi" && StringList(action, "child_action_keys").Count == 0) throw new ConsultantException("child_actions_required", "Una acción multi requiere child_action_keys.");
            if (state == "mail_post")
            {
                JsonUtil.GetString(action, "template_key", true);
                string method = JsonUtil.GetString(action, "mail_post_method", false) ?? "email";
                if (method != "email" && method != "comment" && method != "note") throw new ConsultantException("invalid_mail_post_method", "mail_post_method no es válido.");
            }
            if (state == "next_activity")
            {
                RequireReference(manifest, JsonUtil.GetString(action, "activity_type_reference", true), "xml_id", "mail.activity.type");
                string userReference = JsonUtil.GetString(action, "activity_user_reference", false), userField = JsonUtil.GetString(action, "activity_user_field", false);
                if (!string.IsNullOrWhiteSpace(userReference) && !string.IsNullOrWhiteSpace(userField)) throw new ConsultantException("ambiguous_activity_user", "La actividad debe usar usuario fijo o campo dinámico, no ambos.");
                if (!string.IsNullOrWhiteSpace(userReference)) RequireReference(manifest, userReference, "xml_id", "res.users");
                if (!string.IsNullOrWhiteSpace(userField)) ValidateFieldName(userField);
                string noteFile = JsonUtil.GetString(action, "activity_note_file", false);
                if (!string.IsNullOrWhiteSpace(noteFile)) ValidateFile(manifest, noteFile, JsonUtil.GetString(action, "activity_note_sha256", true), 100000);
                string unit = JsonUtil.GetString(action, "activity_deadline_unit", false) ?? "days";
                if (unit != "days" && unit != "weeks" && unit != "months") throw new ConsultantException("invalid_activity_deadline", "Unidad de vencimiento de actividad no válida.");
                long range = JsonUtil.GetLong(action, "activity_deadline_range", false); if (range < 0 || range > 3650) throw new ConsultantException("invalid_activity_deadline", "El vencimiento de actividad está fuera de rango.");
            }
            if (state == "webhook")
            {
                RequireReference(manifest, JsonUtil.GetString(action, "webhook_url", true), "https_url", null);
                List<string> fields = StringList(action, "webhook_fields");
                if (fields.Count == 0 || fields.Count > 50) throw new ConsultantException("webhook_fields_required", "El webhook debe declarar entre 1 y 50 campos.");
                foreach (string field in fields) { ValidateFieldName(field); if (SecretFieldPattern.IsMatch(field)) throw new ConsultantException("secret_field_blocked", "El webhook no puede enviar campos de secretos."); }
            }
        }

        private static void ValidateView(Manifest manifest, Dictionary<string, object> view)
        {
            EnsureAllowedKeys(view, new[] { "key", "name", "model", "mode", "previous_hash", "inherit_view_name", "inherit_view_xml_id", "arch_file", "arch_sha256", "priority", "active", "action_key" }, "views");
            EnsureKeys(view, new[] { "key", "name", "model", "arch_file", "arch_sha256", "action_key" });
            string model = JsonUtil.GetString(view, "model", true); StudioService.ValidateModel(model); Add(manifest.targetModels, model);
            string mode = Mode(view); if (mode != "create") RequirePreviousHash(view);
            string inheritName = JsonUtil.GetString(view, "inherit_view_name", false), inheritXmlId = JsonUtil.GetString(view, "inherit_view_xml_id", false);
            if (string.IsNullOrWhiteSpace(inheritName) == string.IsNullOrWhiteSpace(inheritXmlId)) throw new ConsultantException("view_parent_required", "Una vista ejecutable debe declarar exactamente un padre por nombre o XML ID.");
            if (!string.IsNullOrWhiteSpace(inheritXmlId)) ValidateXmlId(inheritXmlId);
            long priority = JsonUtil.GetLong(view, "priority", false); if (priority < 0 || priority > 100000) throw new ConsultantException("invalid_view_priority", "La prioridad de la vista no es válida.");
            string arch = ReadSigned(manifest, view, "arch_file", "arch_sha256", 100000);
            ValidateActionViewArchitecture(arch);
        }

        internal static void ValidateActionViewArchitecture(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml) || xml.Length > 100000) throw new ConsultantException("invalid_view_xml", "La arquitectura de la vista está vacía o supera el límite.");
            XmlDocument document = new XmlDocument(); document.XmlResolver = null;
            try
            {
                XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using (StringReader text = new StringReader(xml))
                using (XmlReader reader = XmlReader.Create(text, settings)) document.Load(reader);
            }
            catch { throw new ConsultantException("invalid_view_xml", "La arquitectura de la vista ejecutable no es XML válido."); }
            string lower = xml.ToLowerInvariant();
            foreach (string token in new[] { "<!doctype", "<!entity", "<script", "type=\"object\"", "type='object'", "t-raw=", "<form action=" })
                if (lower.Contains(token)) throw new ConsultantException("unsafe_view_xml", "La vista ejecutable contiene un elemento no permitido: " + token + ".");
            XmlNodeList buttons = document.SelectNodes("//button");
            if (buttons == null || buttons.Count == 0) throw new ConsultantException("action_button_required", "La vista ejecutable debe contener al menos un botón.");
            foreach (XmlNode button in buttons)
            {
                string type = button.Attributes["type"] == null ? string.Empty : button.Attributes["type"].Value;
                string name = button.Attributes["name"] == null ? string.Empty : button.Attributes["name"].Value;
                if (type != "action" || name != "{{ACTION_ID}}") throw new ConsultantException("unsafe_view_button", "Cada botón debe usar type=action y name={{ACTION_ID}}.");
            }
        }

        internal static string ResolveActionViewArchitecture(string xml, long actionId)
        {
            if (actionId <= 0) throw new ConsultantException("dependency_not_resolved", "La acción del botón no tiene un ID técnico resuelto.");
            ValidateActionViewArchitecture(xml);
            string resolved = xml.Replace("{{ACTION_ID}}", actionId.ToString(CultureInfo.InvariantCulture));
            if (resolved.Contains("{{ACTION_ID}}")) throw new ConsultantException("unresolved_view_action", "No se resolvió la acción del botón.");
            return resolved;
        }

        private static void ValidateAutomation(Manifest manifest, Dictionary<string, object> automation)
        {
            EnsureAllowedKeys(automation, new[] { "key", "name", "model", "mode", "previous_hash", "description", "trigger", "active", "install_active", "activation_mode", "desired_active_after_authorization", "action_keys", "filter_domain_file", "filter_domain_sha256", "filter_pre_domain_file", "filter_pre_domain_sha256", "trigger_fields", "on_change_fields", "trigger_selection_field", "trigger_selection_value", "trigger_reference", "trigger_date_field", "trigger_date_range", "trigger_date_mode", "trigger_date_unit", "calendar_reference", "record_getter_file", "record_getter_sha256", "log_webhook_calls" }, "automations");
            EnsureKeys(automation, new[] { "key", "name", "model", "trigger", "action_keys" });
            string model = JsonUtil.GetString(automation, "model", true); StudioService.ValidateModel(model); Add(manifest.targetModels, model);
            string mode = Mode(automation); if (mode != "create") RequirePreviousHash(automation);
            if (JsonUtil.GetBool(automation, "install_active", false)) throw new ConsultantException("unsafe_install_activation", "Las automatizaciones Fase 3 siempre se instalan inactivas.");
            string activationMode = JsonUtil.GetString(automation, "activation_mode", false);
            if (!string.IsNullOrWhiteSpace(activationMode) && activationMode != "immediate" && activationMode != "gated") throw new ConsultantException("invalid_activation_mode", "activation_mode debe ser immediate o gated.");
            string trigger = JsonUtil.GetString(automation, "trigger", true); if (!Triggers.Contains(trigger)) throw new ConsultantException("unsupported_automation_trigger", "Disparador no permitido: " + trigger + ".");
            foreach (string pair in new[] { "filter_domain", "filter_pre_domain" })
            {
                string file = JsonUtil.GetString(automation, pair + "_file", false);
                if (!string.IsNullOrWhiteSpace(file)) ValidateFile(manifest, file, JsonUtil.GetString(automation, pair + "_sha256", true), 100000);
            }
            foreach (string field in StringList(automation, "trigger_fields").Concat(StringList(automation, "on_change_fields"))) ValidateFieldName(field);
            string selectionField = JsonUtil.GetString(automation, "trigger_selection_field", false);
            if (!string.IsNullOrWhiteSpace(selectionField)) { ValidateFieldName(selectionField); JsonUtil.GetString(automation, "trigger_selection_value", true); }
            string triggerReference = JsonUtil.GetString(automation, "trigger_reference", false);
            if (!string.IsNullOrWhiteSpace(triggerReference)) RequireReference(manifest, triggerReference, "xml_id", null);
            string dateField = JsonUtil.GetString(automation, "trigger_date_field", false); if (!string.IsNullOrWhiteSpace(dateField)) ValidateFieldName(dateField);
            long range = JsonUtil.GetLong(automation, "trigger_date_range", false); if (range < 0 || range > 100000) throw new ConsultantException("invalid_trigger_delay", "El retraso de automatización está fuera de rango.");
            string dateMode = JsonUtil.GetString(automation, "trigger_date_mode", false) ?? "after"; if (dateMode != "after" && dateMode != "before") throw new ConsultantException("invalid_trigger_delay", "trigger_date_mode no es válido.");
            string dateUnit = JsonUtil.GetString(automation, "trigger_date_unit", false) ?? "hour"; if (dateUnit != "minutes" && dateUnit != "hour" && dateUnit != "day" && dateUnit != "month") throw new ConsultantException("invalid_trigger_delay", "trigger_date_unit no es válido.");
            string calendar = JsonUtil.GetString(automation, "calendar_reference", false); if (!string.IsNullOrWhiteSpace(calendar)) RequireReference(manifest, calendar, "xml_id", "resource.calendar");
            if (trigger == "on_webhook") ValidateFile(manifest, JsonUtil.GetString(automation, "record_getter_file", true), JsonUtil.GetString(automation, "record_getter_sha256", true), 100000);
        }

        private static void ValidateLogicalDependencies(Manifest manifest)
        {
            Dictionary<string, Dictionary<string, object>> templates = manifest.templates.ToDictionary(delegate(Dictionary<string, object> item) { return JsonUtil.GetString(item, "key", true); }, StringComparer.Ordinal);
            Dictionary<string, Dictionary<string, object>> actions = manifest.actions.ToDictionary(delegate(Dictionary<string, object> item) { return JsonUtil.GetString(item, "key", true); }, StringComparer.Ordinal);
            HashSet<string> children = new HashSet<string>(StringComparer.Ordinal);
            foreach (Dictionary<string, object> action in manifest.actions)
            {
                string key = JsonUtil.GetString(action, "key", true), state = JsonUtil.GetString(action, "state", true);
                if (state == "mail_post")
                {
                    string templateKey = JsonUtil.GetString(action, "template_key", true);
                    if (!templates.ContainsKey(templateKey)) throw new ConsultantException("unknown_template_key", "La acción " + key + " referencia una plantilla no declarada.");
                    if ((JsonUtil.GetString(action, "mail_post_method", false) ?? "email") == "email")
                    {
                        Dictionary<string, object> template = templates[templateKey];
                        if (JsonUtil.GetBool(template, "use_default_to", false) || !PlaceholderPattern.IsMatch(JsonUtil.GetString(template, "email_to", true))) throw new ConsultantException("test_recipient_required", "Una acción de correo externo requiere use_default_to=false y email_to={{REFERENCIA_EMAIL}}.");
                    }
                }
                if (state == "multi")
                {
                    List<string> childKeys = StringList(action, "child_action_keys");
                    if (childKeys.Distinct().Count() != childKeys.Count) throw new ConsultantException("duplicate_child_action", "Una acción multi repite un hijo.");
                    foreach (string child in childKeys)
                    {
                        if (!actions.ContainsKey(child) || child == key) throw new ConsultantException("unknown_child_action", "La acción multi referencia un hijo no válido.");
                        if (!children.Add(child)) throw new ConsultantException("shared_child_action", "Una acción hija no puede pertenecer a dos cadenas.");
                        if (JsonUtil.GetString(actions[child], "model", true) != JsonUtil.GetString(action, "model", true)) throw new ConsultantException("action_model_mismatch", "Las acciones encadenadas deben usar el mismo modelo.");
                    }
                }
            }
            EnsureNoActionCycles(actions);
            foreach (Dictionary<string, object> view in manifest.views)
            {
                string actionKey = JsonUtil.GetString(view, "action_key", true);
                Dictionary<string, object> action;
                if (!actions.TryGetValue(actionKey, out action)) throw new ConsultantException("unknown_action_key", "La vista referencia una acción no declarada en la misma réplica.");
                if (JsonUtil.GetString(action, "model", true) != JsonUtil.GetString(view, "model", true)) throw new ConsultantException("action_model_mismatch", "El botón y su acción deben usar el mismo modelo.");
            }
            HashSet<string> topActions = new HashSet<string>(StringComparer.Ordinal);
            foreach (Dictionary<string, object> automation in manifest.automations)
            {
                string automationKey = JsonUtil.GetString(automation, "key", true), model = JsonUtil.GetString(automation, "model", true), trigger = JsonUtil.GetString(automation, "trigger", true);
                List<string> actionKeys = StringList(automation, "action_keys");
                if (actionKeys.Count == 0 || actionKeys.Distinct().Count() != actionKeys.Count) throw new ConsultantException("automation_actions_required", "Cada automatización requiere acciones únicas.");
                bool external = trigger == "on_webhook";
                if (trigger == "on_webhook") manifest.inboundAutomationKeys.Add(automationKey);
                foreach (string actionKey in actionKeys)
                {
                    Dictionary<string, object> action;
                    if (!actions.TryGetValue(actionKey, out action)) throw new ConsultantException("unknown_action_key", "La automatización referencia una acción no declarada.");
                    if (children.Contains(actionKey)) throw new ConsultantException("child_action_bound", "Una acción hija de una cadena no puede vincularse directamente a una automatización.");
                    if (!topActions.Add(actionKey)) throw new ConsultantException("action_bound_twice", "Una acción no puede pertenecer a dos automatizaciones.");
                    if (JsonUtil.GetString(action, "model", true) != model) throw new ConsultantException("action_model_mismatch", "La automatización y sus acciones deben usar el mismo modelo.");
                    if (trigger == "on_change" && JsonUtil.GetString(action, "state", true) != "code") throw new ConsultantException("on_change_code_only", "Odoo solo admite acciones de código con el disparador on_change.");
                    if (trigger == "on_unlink" && ContainsState(actions, actionKey, new HashSet<string>(new[] { "mail_post", "next_activity" }, StringComparer.Ordinal))) throw new ConsultantException("unlink_effect_forbidden", "Odoo no permite correo o actividad después de eliminar el registro.");
                    if (ContainsExternalAction(actions, actionKey))
                    {
                        external = true;
                        Add(manifest.externalTestActionKeys, actionKey);
                    }
                }
                if (external)
                {
                    Add(manifest.externalAutomationKeys, automationKey);
                    automation["external_effect"] = true;
                }
                string activationMode = JsonUtil.GetString(automation, "activation_mode", false) ?? (external ? "gated" : "immediate");
                bool desiredActive = DesiredAutomationActive(automation);
                if (external && desiredActive && activationMode != "gated") throw new ConsultantException("external_activation_gate_required", "Una automatización con efectos externos solo puede activarse mediante activation_mode=gated.");
                if (!external && activationMode == "gated") throw new ConsultantException("unnecessary_activation_gate", "activation_mode=gated se reserva para automatizaciones con efectos externos.");
                automation["activation_mode"] = activationMode;
                automation["desired_active_after_authorization"] = desiredActive;
            }
        }

        private Analysis AnalyzeInternal(OdooClient client, Manifest manifest)
        {
            Analysis analysis = new Analysis
            {
                components = new List<Dictionary<string, object>>(),
                blocked = new List<string>(),
                previousValues = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal),
                modelIds = new Dictionary<string, long>(StringComparer.Ordinal),
                fieldIds = new Dictionary<string, long>(StringComparer.Ordinal),
                componentIds = new Dictionary<string, long>(StringComparer.Ordinal)
            };
            Dictionary<string, object> contracts = ProbeContracts(client, analysis.blocked);
            foreach (string model in manifest.targetModels)
            {
                IList<Dictionary<string, object>> rows = client.SearchRead("ir.model", new object[] { new object[] { "model", "=", model } }, new[] { "id", "model", "name", "state", "is_mail_thread", "is_mail_activity" }, 2, null);
                if (rows.Count != 1) { analysis.blocked.Add("modelo ausente o ambiguo: " + model); continue; }
                analysis.modelIds[model] = Id(rows[0]);
            }
            ValidateBusinessFields(client, manifest, analysis);
            foreach (Dictionary<string, object> field in manifest.computedFields)
            {
                string model = JsonUtil.GetString(field, "model", true), name = JsonUtil.GetString(field, "name", true), key = "computed_field:" + JsonUtil.GetString(field, "key", true);
                IList<Dictionary<string, object>> rows = client.SearchRead("ir.model.fields", new object[] { new object[] { "model", "=", model }, new object[] { "name", "=", name } }, ComputedFieldReadFields, 2, null);
                AddComponent(client, analysis, key, "computed_field", field, rows, "ir.model.fields", delegate(Dictionary<string, object> row) { return Text(row, "state") == "manual"; });
                if (rows.Count == 1) { analysis.fieldIds[model + ":" + name] = Id(rows[0]); analysis.componentIds[key] = Id(rows[0]); }
            }
            foreach (Dictionary<string, object> template in manifest.templates)
            {
                string key = "mail_template:" + JsonUtil.GetString(template, "key", true), model = JsonUtil.GetString(template, "model", true);
                long modelId; if (!analysis.modelIds.TryGetValue(model, out modelId)) continue;
                IList<Dictionary<string, object>> rows = client.SearchRead("mail.template", new object[] { new object[] { "name", "=", JsonUtil.GetString(template, "name", true) }, new object[] { "model_id", "=", modelId } }, TemplateReadFields, 2, null);
                AddComponent(client, analysis, key, "mail_template", template, rows, "mail.template", delegate(Dictionary<string, object> row) { return IsCustomOwned(client, "mail.template", Id(row)); });
                if (rows.Count == 1) analysis.componentIds[key] = Id(rows[0]);
            }
            foreach (Dictionary<string, object> action in manifest.actions)
            {
                string key = "server_action:" + JsonUtil.GetString(action, "key", true), model = JsonUtil.GetString(action, "model", true);
                long modelId; if (!analysis.modelIds.TryGetValue(model, out modelId)) continue;
                IList<Dictionary<string, object>> rows = client.SearchRead("ir.actions.server", new object[] { new object[] { "name", "=", JsonUtil.GetString(action, "name", true) }, new object[] { "model_id", "=", modelId } }, ActionReadFields, 2, null);
                AddComponent(client, analysis, key, "server_action", action, rows, "ir.actions.server", delegate(Dictionary<string, object> row) { return IsCustomOwned(client, "ir.actions.server", Id(row)); });
                if (rows.Count == 1) analysis.componentIds[key] = Id(rows[0]);
            }
            foreach (Dictionary<string, object> view in manifest.views)
            {
                string logical = JsonUtil.GetString(view, "key", true), key = "view:" + logical, model = JsonUtil.GetString(view, "model", true);
                long parentId = 0;
                string parentXmlId = JsonUtil.GetString(view, "inherit_view_xml_id", false);
                if (!string.IsNullOrWhiteSpace(parentXmlId))
                {
                    try
                    {
                        parentId = ResolveXmlId(client, parentXmlId, "ir.ui.view");
                        IList<Dictionary<string, object>> parents = client.Read("ir.ui.view", new[] { parentId }, new[] { "id", "model" });
                        if (parents.Count != 1 || Text(parents[0], "model") != model) { analysis.blocked.Add("vista padre no pertenece al modelo: " + parentXmlId); continue; }
                    }
                    catch { analysis.blocked.Add("vista padre ausente: " + parentXmlId); continue; }
                }
                else
                {
                    string parentName = JsonUtil.GetString(view, "inherit_view_name", true);
                    IList<Dictionary<string, object>> parents = client.SearchRead("ir.ui.view", new object[] { new object[] { "name", "=", parentName }, new object[] { "model", "=", model } }, new[] { "id", "name", "model" }, 3, null);
                    if (parents.Count != 1) { analysis.blocked.Add("vista padre ausente o ambigua: " + parentName); continue; }
                    parentId = Id(parents[0]);
                }
                view["resolved_parent_id"] = parentId;
                long actionId; analysis.componentIds.TryGetValue("server_action:" + JsonUtil.GetString(view, "action_key", true), out actionId);
                view["resolved_action_id"] = actionId;
                string sourceArch = ReadSigned(manifest, view, "arch_file", "arch_sha256", 100000);
                view["resolved_arch_hash"] = actionId > 0 ? JsonUtil.Sha256(NormalizeXml(ResolveActionViewArchitecture(sourceArch, actionId))) : null;
                IList<Dictionary<string, object>> rows = client.SearchRead("ir.ui.view", new object[] { new object[] { "name", "=", JsonUtil.GetString(view, "name", true) }, new object[] { "model", "=", model } }, ViewReadFields, 2, null);
                AddComponent(client, analysis, key, "view", view, rows, "ir.ui.view", delegate(Dictionary<string, object> row) { return RelationId(row["inherit_id"]) == parentId && IsCustomOwned(client, "ir.ui.view", Id(row)); });
                if (rows.Count == 1) analysis.componentIds[key] = Id(rows[0]);
            }
            foreach (Dictionary<string, object> automation in manifest.automations)
            {
                string key = "automation:" + JsonUtil.GetString(automation, "key", true), model = JsonUtil.GetString(automation, "model", true);
                long modelId; if (!analysis.modelIds.TryGetValue(model, out modelId)) continue;
                IList<Dictionary<string, object>> rows = client.SearchRead("base.automation", new object[] { new object[] { "name", "=", JsonUtil.GetString(automation, "name", true) }, new object[] { "model_id", "=", modelId } }, AutomationReadFields, 2, null);
                AddComponent(client, analysis, key, "automation", automation, rows, "base.automation", delegate(Dictionary<string, object> row) { return IsCustomOwned(client, "base.automation", Id(row)); });
                if (rows.Count == 1) analysis.componentIds[key] = Id(rows[0]);
            }
            ValidateModelCapabilities(client, manifest, analysis);
            Dictionary<string, object> access = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (string technical in RequiredTechnicalModels(manifest))
            {
                bool read = SafeAccess(client, technical, "read"), create = SafeAccess(client, technical, "create"), write = SafeAccess(client, technical, "write");
                access[technical] = JsonUtil.Dictionary("read", read, "create", create, "write", write);
                if (!read || (NeedsWrite(manifest, technical) && !write) || (NeedsCreate(manifest, technical) && !create)) analysis.blocked.Add("permisos insuficientes para " + technical);
            }
            analysis.preconditionHash = JsonUtil.CanonicalHash(analysis.components.Select(delegate(Dictionary<string, object> item) { return PublicComponent(item); }).OrderBy(delegate(Dictionary<string, object> item) { return Convert.ToString(item["key"], CultureInfo.InvariantCulture); }, StringComparer.Ordinal).ToArray());
            analysis.capabilityHash = JsonUtil.CanonicalHash(JsonUtil.Dictionary("contracts", contracts, "access", access, "models", analysis.modelIds.OrderBy(delegate(KeyValuePair<string, long> item) { return item.Key; }, StringComparer.Ordinal).ToDictionary(delegate(KeyValuePair<string, long> item) { return item.Key; }, delegate(KeyValuePair<string, long> item) { return (object)item.Value; }, StringComparer.Ordinal)));
            analysis.blocked = analysis.blocked.Distinct().OrderBy(delegate(string value) { return value; }, StringComparer.Ordinal).ToList();
            return analysis;
        }

        private static Dictionary<string, object> ProbeContracts(OdooClient client, List<string> blocked)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            Dictionary<string, string[]> required = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                { "ir.actions.server", new[] { "name", "model_id", "state", "code", "parent_id", "base_automation_id", "template_id", "activity_type_id", "webhook_url", "webhook_field_ids" } },
                { "base.automation", new[] { "name", "model_id", "trigger", "action_server_ids", "active", "filter_domain", "record_getter" } },
                { "mail.template", new[] { "name", "model_id", "subject", "body_html", "email_to", "active" } },
                { "ir.ui.view", new[] { "name", "model", "type", "inherit_id", "arch", "mode", "priority", "active" } },
                { "ir.model.fields", new[] { "model", "name", "compute", "depends", "state" } }
            };
            foreach (KeyValuePair<string, string[]> item in required)
            {
                try
                {
                    Dictionary<string, object> fields = client.FieldsGet(item.Key, new[] { "type", "relation" });
                    List<string> missing = item.Value.Where(delegate(string field) { return !fields.ContainsKey(field); }).ToList();
                    result[item.Key] = JsonUtil.CanonicalHash(fields.OrderBy(delegate(KeyValuePair<string, object> value) { return value.Key; }, StringComparer.Ordinal).ToDictionary(delegate(KeyValuePair<string, object> value) { return value.Key; }, delegate(KeyValuePair<string, object> value) { return value.Value; }, StringComparer.Ordinal));
                    if (missing.Count > 0) blocked.Add("contrato Odoo 19 incompleto en " + item.Key + ": " + string.Join(",", missing.ToArray()));
                }
                catch { blocked.Add("no se pudo descubrir el contrato de " + item.Key); }
            }
            return result;
        }

        private static void ValidateBusinessFields(OdooClient client, Manifest manifest, Analysis analysis)
        {
            foreach (string model in manifest.targetModels.Where(delegate(string item) { return analysis.modelIds.ContainsKey(item); }))
            {
                IList<Dictionary<string, object>> rows = client.SearchRead("ir.model.fields", new object[] { new object[] { "model", "=", model } }, new[] { "id", "name", "ttype", "relation" }, 5000, "name asc");
                foreach (Dictionary<string, object> row in rows) analysis.fieldIds[model + ":" + Text(row, "name")] = Id(row);
            }
            foreach (Dictionary<string, object> action in manifest.actions)
            {
                string model = JsonUtil.GetString(action, "model", true), state = JsonUtil.GetString(action, "state", true);
                foreach (string field in StringList(action, "webhook_fields")) if (!analysis.fieldIds.ContainsKey(model + ":" + field)) analysis.blocked.Add("campo de webhook ausente: " + model + "." + field);
                string userField = JsonUtil.GetString(action, "activity_user_field", false); if (!string.IsNullOrWhiteSpace(userField) && !analysis.fieldIds.ContainsKey(model + ":" + userField)) analysis.blocked.Add("campo de usuario de actividad ausente: " + model + "." + userField);
                if (state == "next_activity" && SecretFieldPattern.IsMatch(userField ?? string.Empty)) analysis.blocked.Add("campo de actividad sensible bloqueado");
            }
            foreach (Dictionary<string, object> automation in manifest.automations)
            {
                string model = JsonUtil.GetString(automation, "model", true);
                foreach (string field in StringList(automation, "trigger_fields").Concat(StringList(automation, "on_change_fields"))) if (!analysis.fieldIds.ContainsKey(model + ":" + field)) analysis.blocked.Add("campo disparador ausente: " + model + "." + field);
                foreach (string fieldKey in new[] { "trigger_selection_field", "trigger_date_field" })
                {
                    string field = JsonUtil.GetString(automation, fieldKey, false); if (!string.IsNullOrWhiteSpace(field) && !analysis.fieldIds.ContainsKey(model + ":" + field)) analysis.blocked.Add("campo de automatización ausente: " + model + "." + field);
                }
            }
        }

        private static void ValidateModelCapabilities(OdooClient client, Manifest manifest, Analysis analysis)
        {
            foreach (Dictionary<string, object> action in manifest.actions.Where(delegate(Dictionary<string, object> item) { return JsonUtil.GetString(item, "state", true) == "next_activity"; }))
            {
                string model = JsonUtil.GetString(action, "model", true);
                IList<Dictionary<string, object>> rows = client.SearchRead("ir.model", new object[] { new object[] { "model", "=", model } }, new[] { "id", "is_mail_activity" }, 1, null);
                if (rows.Count != 1 || !Bool(rows[0], "is_mail_activity")) analysis.blocked.Add("el modelo no admite actividades: " + model);
            }
            foreach (Dictionary<string, object> automation in manifest.automations.Where(delegate(Dictionary<string, object> item) { string trigger = JsonUtil.GetString(item, "trigger", true); return trigger == "on_message_received" || trigger == "on_message_sent"; }))
            {
                string model = JsonUtil.GetString(automation, "model", true);
                IList<Dictionary<string, object>> rows = client.SearchRead("ir.model", new object[] { new object[] { "model", "=", model } }, new[] { "id", "is_mail_thread" }, 1, null);
                if (rows.Count != 1 || !Bool(rows[0], "is_mail_thread")) analysis.blocked.Add("el modelo no admite disparadores de correo: " + model);
            }
        }

        private static void AddComponent(OdooClient client, Analysis analysis, string key, string kind, Dictionary<string, object> desired, IList<Dictionary<string, object>> rows, string technicalModel, Func<Dictionary<string, object>, bool> mutable)
        {
            string mode = Mode(desired), classification, reason = null, actualHash = null;
            long technicalId = 0;
            if (rows.Count == 0)
            {
                classification = mode == "create" ? "absent" : "blocked";
                if (classification == "blocked") { reason = "componente ausente para " + mode; analysis.blocked.Add(reason + ": " + key); }
            }
            else if (rows.Count > 1)
            {
                classification = "blocked"; reason = "colisión ambigua"; analysis.blocked.Add(reason + ": " + key);
            }
            else
            {
                Dictionary<string, object> row = rows[0]; technicalId = Id(row);
                Dictionary<string, object> snapshot = SafeSnapshot(kind, row); actualHash = JsonUtil.CanonicalHash(snapshot);
                analysis.previousValues[key] = RawSnapshot(kind, row);
                bool equivalent = DesiredMatches(kind, desired, row);
                if (mode == "create")
                {
                    if (equivalent && (kind == "mail_template" || mutable(row))) classification = "compatible";
                    else { classification = "blocked"; reason = "colisión incompatible"; analysis.blocked.Add(reason + ": " + key); }
                }
                else
                {
                    string expected = JsonUtil.GetString(desired, "previous_hash", true);
                    if (expected != actualHash) { classification = "blocked"; reason = "previous_hash no coincide"; analysis.blocked.Add(reason + ": " + key); }
                    else if (!mutable(row)) { classification = "blocked"; reason = "artefacto estándar no modificable"; analysis.blocked.Add(reason + ": " + key); }
                    else classification = equivalent && mode == "update" ? "compatible" : "modifiable";
                }
            }
            analysis.components.Add(JsonUtil.Dictionary("key", key, "kind", kind, "mode", mode, "classification", classification, "technical_model", technicalModel, "technical_id", technicalId, "actual_hash", actualHash, "desired_hash", DesiredHash(desired), "reason", reason));
        }

        private List<ChangeOperation> Compile(Manifest manifest, Analysis analysis)
        {
            Dictionary<string, Dictionary<string, object>> components = analysis.components.ToDictionary(delegate(Dictionary<string, object> item) { return Convert.ToString(item["key"], CultureInfo.InvariantCulture); }, StringComparer.Ordinal);
            List<ChangeOperation> result = new List<ChangeOperation>();
            foreach (Dictionary<string, object> field in manifest.computedFields) result.Add(BuildComponentOperation("computed_field", field, components, analysis));
            foreach (Dictionary<string, object> template in manifest.templates) result.Add(BuildComponentOperation("mail_template", template, components, analysis));
            foreach (Dictionary<string, object> action in manifest.actions) result.Add(BuildComponentOperation("server_action", action, components, analysis));
            foreach (Dictionary<string, object> action in manifest.actions.Where(delegate(Dictionary<string, object> item) { return JsonUtil.GetString(item, "state", true) == "multi" && Mode(item) != "deactivate"; }))
            {
                Dictionary<string, object> prior = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (string child in StringList(action, "child_action_keys"))
                {
                    Dictionary<string, object> previous;
                    if (analysis.previousValues.TryGetValue("server_action:" + child, out previous)) prior[child] = previous.ContainsKey("parent_id") ? previous["parent_id"] : false;
                }
                result.Add(new ChangeOperation { operation_id = NewOp(), type = "link_action_chain", model = JsonUtil.GetString(action, "model", true), status = "pending", parameters = JsonUtil.Dictionary("component_key", "action_chain:" + JsonUtil.GetString(action, "key", true), "parent_action_key", JsonUtil.GetString(action, "key", true), "child_action_keys", StringList(action, "child_action_keys").ToArray(), "previous_snapshot", prior.Count == 0 ? null : ProtectedSnapshot.Protect(prior)) });
            }
            foreach (Dictionary<string, object> view in manifest.views) result.Add(BuildComponentOperation("view", view, components, analysis));
            foreach (Dictionary<string, object> automation in manifest.automations) result.Add(BuildComponentOperation("automation", automation, components, analysis));
            foreach (Dictionary<string, object> automation in manifest.automations.Where(delegate(Dictionary<string, object> item) { return Mode(item) != "deactivate"; }))
            {
                foreach (string actionKey in StringList(automation, "action_keys"))
                {
                    result.Add(new ChangeOperation { operation_id = NewOp(), type = "bind_automation_action", model = JsonUtil.GetString(automation, "model", true), status = "pending", parameters = JsonUtil.Dictionary("component_key", "automation_binding:" + JsonUtil.GetString(automation, "key", true) + ":" + actionKey, "automation_key", JsonUtil.GetString(automation, "key", true), "action_key", actionKey) });
                }
                if (DesiredAutomationActive(automation))
                {
                    bool external = JsonUtil.GetBool(automation, "external_effect", false);
                    string activationMode = JsonUtil.GetString(automation, "activation_mode", false) ?? (external ? "gated" : "immediate");
                    result.Add(new ChangeOperation
                    {
                        operation_id = NewOp(),
                        type = external && activationMode == "gated" ? "arm_external_automation" : "activate_internal_automation",
                        model = JsonUtil.GetString(automation, "model", true),
                        status = "pending",
                        parameters = JsonUtil.Dictionary("component_key", "automation_activation:" + JsonUtil.GetString(automation, "key", true), "automation_key", JsonUtil.GetString(automation, "key", true), "desired_active", true, "external_action_keys", StringList(automation, "action_keys").Where(delegate(string key) { return manifest.externalTestActionKeys.Contains(key); }).ToArray(), "inbound_webhook", JsonUtil.GetString(automation, "trigger", true) == "on_webhook")
                    });
                }
            }
            return result;
        }

        private static ChangeOperation BuildComponentOperation(string kind, Dictionary<string, object> desired, Dictionary<string, Dictionary<string, object>> components, Analysis analysis)
        {
            string logical = JsonUtil.GetString(desired, "key", true), componentKey = kind + ":" + logical;
            Dictionary<string, object> component = components[componentKey];
            string classification = Convert.ToString(component["classification"], CultureInfo.InvariantCulture), mode = Mode(desired), type;
            if (classification == "compatible") type = "preserve_" + kind;
            else if (kind == "computed_field") type = "update_computed_field";
            else type = (mode == "deactivate" ? "deactivate_" : mode + "_") + kind;
            Dictionary<string, object> parameters = new Dictionary<string, object>(desired, StringComparer.Ordinal);
            parameters["component_key"] = componentKey;
            parameters["actual_hash"] = component["actual_hash"];
            Dictionary<string, object> previous;
            if (analysis.previousValues.TryGetValue(componentKey, out previous)) parameters["previous_snapshot"] = ProtectedSnapshot.Protect(previous);
            return new ChangeOperation
            {
                operation_id = NewOp(),
                type = type,
                model = JsonUtil.GetString(desired, "model", true),
                status = classification == "compatible" ? "reused" : "pending",
                result_id = Convert.ToInt64(component["technical_id"], CultureInfo.InvariantCulture),
                parameters = parameters
            };
        }

        private void ApplyOperation(OdooClient client, Manifest manifest, ChangeSet change, ChangeOperation operation, Dictionary<string, string> supplied, Dictionary<string, long> xmlIds)
        {
            if (operation.type == "update_computed_field")
            {
                EnsureTarget(operation); Dictionary<string, object> values = JsonUtil.Dictionary("compute", ReadSigned(manifest, operation.parameters, "compute_file", "compute_sha256", 200000), "depends", JsonUtil.GetString(operation.parameters, "depends", true));
                Write(client, "ir.model.fields", operation, values); return;
            }
            if (operation.type == "preserve_mail_template" || operation.type == "preserve_server_action" || operation.type == "preserve_view" || operation.type == "preserve_automation") { operation.status = "reused"; return; }
            if (operation.type.Contains("mail_template"))
            {
                if (operation.type == "deactivate_mail_template") { EnsureTarget(operation); Write(client, "mail.template", operation, JsonUtil.Dictionary("active", false)); return; }
                Dictionary<string, object> values = TemplateValues(client, manifest, change, operation, supplied);
                Upsert(client, operation, "mail.template", values, new object[] { new object[] { "name", "=", values["name"] }, new object[] { "model_id", "=", values["model_id"] } }); return;
            }
            if (operation.type.Contains("server_action"))
            {
                if (operation.type == "deactivate_server_action") { EnsureTarget(operation); Write(client, "ir.actions.server", operation, JsonUtil.Dictionary("binding_model_id", false, "base_automation_id", false, "parent_id", false, "usage", "ir_actions_server")); return; }
                Dictionary<string, object> values = ActionValues(client, manifest, change, operation, supplied, xmlIds);
                Upsert(client, operation, "ir.actions.server", values, new object[] { new object[] { "name", "=", values["name"] }, new object[] { "model_id", "=", values["model_id"] } }); return;
            }
            if (operation.type == "link_action_chain")
            {
                ChangeOperation parent = ComponentOperation(change, "server_action", JsonUtil.GetString(operation.parameters, "parent_action_key", true));
                EnsureTarget(parent); operation.result_id = parent.result_id;
                foreach (string childKey in ObjectStrings(operation.parameters["child_action_keys"]))
                {
                    ChangeOperation child = ComponentOperation(change, "server_action", childKey); EnsureTarget(child);
                    if (!client.Write("ir.actions.server", new[] { child.result_id }, JsonUtil.Dictionary("parent_id", parent.result_id, "base_automation_id", false, "usage", "ir_actions_server"))) throw new ConsultantException("metadata_write_failed", "Odoo no confirmó el encadenamiento de acciones.");
                }
                operation.status = "applied"; return;
            }
            if (operation.type.Contains("view"))
            {
                if (operation.type == "deactivate_view") { EnsureTarget(operation); Write(client, "ir.ui.view", operation, JsonUtil.Dictionary("active", false)); return; }
                ChangeOperation action = ComponentOperation(change, "server_action", JsonUtil.GetString(operation.parameters, "action_key", true)); EnsureTarget(action);
                string arch = ResolveActionViewArchitecture(ReadSigned(manifest, operation.parameters, "arch_file", "arch_sha256", 100000), action.result_id);
                long priority = JsonUtil.GetLong(operation.parameters, "priority", false); if (priority == 0) priority = 16;
                Dictionary<string, object> values = JsonUtil.Dictionary("name", JsonUtil.GetString(operation.parameters, "name", true), "model", operation.model, "type", "form", "inherit_id", JsonUtil.GetLong(operation.parameters, "resolved_parent_id", true), "arch", JsonUtil.XmlNumericEntities(arch), "mode", "extension", "priority", priority, "active", JsonUtil.GetBool(operation.parameters, "active", true));
                Upsert(client, operation, "ir.ui.view", values, new object[] { new object[] { "name", "=", values["name"] }, new object[] { "model", "=", operation.model } }); return;
            }
            if (operation.type.Contains("automation") && operation.type != "bind_automation_action" && operation.type != "activate_internal_automation" && operation.type != "arm_external_automation")
            {
                if (operation.type == "deactivate_automation") { EnsureTarget(operation); Write(client, "base.automation", operation, JsonUtil.Dictionary("active", false)); return; }
                Dictionary<string, object> values = AutomationValues(client, manifest, change, operation, supplied, xmlIds);
                Upsert(client, operation, "base.automation", values, new object[] { new object[] { "name", "=", values["name"] }, new object[] { "model_id", "=", values["model_id"] } }); return;
            }
            if (operation.type == "bind_automation_action")
            {
                ChangeOperation automation = ComponentOperation(change, "automation", JsonUtil.GetString(operation.parameters, "automation_key", true));
                ChangeOperation action = ComponentOperation(change, "server_action", JsonUtil.GetString(operation.parameters, "action_key", true));
                EnsureTarget(automation); EnsureTarget(action);
                if (!client.Write("ir.actions.server", new[] { action.result_id }, JsonUtil.Dictionary("base_automation_id", automation.result_id, "parent_id", false, "usage", "base_automation"))) throw new ConsultantException("metadata_write_failed", "Odoo no confirmó la vinculación de la acción a la automatización.");
                operation.result_id = action.result_id; operation.status = "applied"; return;
            }
            if (operation.type == "activate_internal_automation" || operation.type == "arm_external_automation")
            {
                ChangeOperation automation = ComponentOperation(change, "automation", JsonUtil.GetString(operation.parameters, "automation_key", true)); EnsureTarget(automation); operation.result_id = automation.result_id;
                if (operation.type == "arm_external_automation")
                {
                    if (!client.Write("base.automation", new[] { automation.result_id }, JsonUtil.Dictionary("active", false))) throw new ConsultantException("metadata_write_failed", "No se pudo mantener inactiva la automatización externa.");
                    operation.status = "external_pending";
                }
                else
                {
                    if (!client.Write("base.automation", new[] { automation.result_id }, JsonUtil.Dictionary("active", JsonUtil.GetBool(operation.parameters, "desired_active", true)))) throw new ConsultantException("metadata_write_failed", "No se pudo activar la automatización interna.");
                    operation.status = "applied";
                }
                return;
            }
            throw new ConsultantException("tool_contract_violation", "Operación Fase 3 no reconocida.");
        }

        private static Dictionary<string, object> TemplateValues(OdooClient client, Manifest manifest, ChangeSet change, ChangeOperation operation, Dictionary<string, string> supplied)
        {
            Dictionary<string, object> p = operation.parameters;
            Dictionary<string, object> values = JsonUtil.Dictionary(
                "name", JsonUtil.GetString(p, "name", true),
                "model_id", ResolveModelId(client, operation.model),
                "description", Marker(manifest, p),
                "subject", ReadSigned(manifest, p, "subject_file", "subject_sha256", 20000),
                "body_html", ReadSigned(manifest, p, "body_html_file", "body_html_sha256", 250000),
                "use_default_to", JsonUtil.GetBool(p, "use_default_to", false),
                "auto_delete", JsonUtil.GetBool(p, "auto_delete", true),
                "active", JsonUtil.GetBool(p, "active", true));
                foreach (string key in new[] { "email_from", "email_to", "email_cc", "reply_to" })
            {
                string value = JsonUtil.GetString(p, key, false); if (!string.IsNullOrWhiteSpace(value)) values[key] = ResolvePlaceholder(value, supplied);
            }
            return values;
        }

        private static Dictionary<string, object> ActionValues(OdooClient client, Manifest manifest, ChangeSet change, ChangeOperation operation, Dictionary<string, string> supplied, Dictionary<string, long> xmlIds)
        {
            Dictionary<string, object> p = operation.parameters;
            string state = JsonUtil.GetString(p, "state", true);
            long sequence = JsonUtil.GetLong(p, "sequence", false); if (sequence == 0) sequence = 5;
            Dictionary<string, object> values = JsonUtil.Dictionary("name", JsonUtil.GetString(p, "name", true), "model_id", ResolveModelId(client, operation.model), "state", state, "sequence", sequence, "usage", "ir_actions_server", "help", Marker(manifest, p));
            if (state == "code") values["code"] = ReadSigned(manifest, p, "code_file", "code_sha256", 250000);
            else if (state == "mail_post")
            {
                ChangeOperation template = ComponentOperation(change, "mail_template", JsonUtil.GetString(p, "template_key", true)); EnsureTarget(template);
                values["template_id"] = template.result_id; values["mail_post_method"] = JsonUtil.GetString(p, "mail_post_method", false) ?? "email"; values["mail_post_autofollow"] = JsonUtil.GetBool(p, "mail_post_autofollow", false);
            }
            else if (state == "next_activity")
            {
                values["activity_type_id"] = ResolveReferenceId(JsonUtil.GetString(p, "activity_type_reference", true), xmlIds);
                values["activity_summary"] = JsonUtil.GetString(p, "activity_summary", false);
                string note = JsonUtil.GetString(p, "activity_note_file", false); if (!string.IsNullOrWhiteSpace(note)) values["activity_note"] = ReadSigned(manifest, p, "activity_note_file", "activity_note_sha256", 100000);
                values["activity_date_deadline_range"] = JsonUtil.GetLong(p, "activity_deadline_range", false);
                values["activity_date_deadline_range_type"] = JsonUtil.GetString(p, "activity_deadline_unit", false) ?? "days";
                string userReference = JsonUtil.GetString(p, "activity_user_reference", false), userField = JsonUtil.GetString(p, "activity_user_field", false);
                if (!string.IsNullOrWhiteSpace(userReference)) { values["activity_user_type"] = "specific"; values["activity_user_id"] = ResolveReferenceId(userReference, xmlIds); values["activity_user_field_name"] = false; }
                else { values["activity_user_type"] = "generic"; values["activity_user_id"] = false; values["activity_user_field_name"] = userField; }
            }
            else if (state == "webhook")
            {
                values["webhook_url"] = ResolvePlaceholder(JsonUtil.GetString(p, "webhook_url", true), supplied);
                values["webhook_field_ids"] = Many2ManyCommand(StringList(p, "webhook_fields").Select(delegate(string field) { return ResolveFieldId(client, operation.model, field); }));
            }
            return values;
        }

        private static Dictionary<string, object> AutomationValues(OdooClient client, Manifest manifest, ChangeSet change, ChangeOperation operation, Dictionary<string, string> supplied, Dictionary<string, long> xmlIds)
        {
            Dictionary<string, object> p = operation.parameters;
            string trigger = JsonUtil.GetString(p, "trigger", true);
            Dictionary<string, object> values = JsonUtil.Dictionary("name", JsonUtil.GetString(p, "name", true), "description", Marker(manifest, p), "model_id", ResolveModelId(client, operation.model), "trigger", trigger, "active", false);
            foreach (string key in new[] { "filter_domain", "filter_pre_domain" })
            {
                string file = JsonUtil.GetString(p, key + "_file", false); if (!string.IsNullOrWhiteSpace(file)) values[key] = ReadSigned(manifest, p, key + "_file", key + "_sha256", 100000);
            }
            List<string> triggerFields = StringList(p, "trigger_fields"), onchange = StringList(p, "on_change_fields");
            if (triggerFields.Count > 0) values["trigger_field_ids"] = Many2ManyCommand(triggerFields.Select(delegate(string field) { return ResolveFieldId(client, operation.model, field); }));
            if (onchange.Count > 0) values["on_change_field_ids"] = Many2ManyCommand(onchange.Select(delegate(string field) { return ResolveFieldId(client, operation.model, field); }));
            string selectionField = JsonUtil.GetString(p, "trigger_selection_field", false);
            if (!string.IsNullOrWhiteSpace(selectionField)) values["trg_selection_field_id"] = ResolveSelectionId(client, operation.model, selectionField, JsonUtil.GetString(p, "trigger_selection_value", true));
            string triggerReference = JsonUtil.GetString(p, "trigger_reference", false); if (!string.IsNullOrWhiteSpace(triggerReference)) values["trg_field_ref"] = ResolveReferenceId(triggerReference, xmlIds);
            string dateField = JsonUtil.GetString(p, "trigger_date_field", false); if (!string.IsNullOrWhiteSpace(dateField)) values["trg_date_id"] = ResolveFieldId(client, operation.model, dateField);
            if (trigger == "on_time" || trigger == "on_time_created" || trigger == "on_time_updated")
            {
                values["trg_date_range"] = JsonUtil.GetLong(p, "trigger_date_range", false); values["trg_date_range_mode"] = JsonUtil.GetString(p, "trigger_date_mode", false) ?? "after"; values["trg_date_range_type"] = JsonUtil.GetString(p, "trigger_date_unit", false) ?? "hour";
                string calendar = JsonUtil.GetString(p, "calendar_reference", false); if (!string.IsNullOrWhiteSpace(calendar)) values["trg_date_calendar_id"] = ResolveReferenceId(calendar, xmlIds);
            }
            if (trigger == "on_webhook")
            {
                values["record_getter"] = ReadSigned(manifest, p, "record_getter_file", "record_getter_sha256", 100000); values["log_webhook_calls"] = JsonUtil.GetBool(p, "log_webhook_calls", false);
            }
            return values;
        }

        private static void Upsert(OdooClient client, ChangeOperation operation, string model, Dictionary<string, object> values, object[] recoveryDomain)
        {
            if (operation.type.StartsWith("create_", StringComparison.Ordinal))
            {
                if (operation.result_id <= 0)
                {
                    IList<Dictionary<string, object>> recovered = client.SearchRead(model, recoveryDomain, new[] { "id" }, 2, null);
                    if (recovered.Count > 1) throw new ConsultantException("recovery_ambiguous", "La recuperación idempotente encontró una colisión.");
                    operation.result_id = recovered.Count == 1 ? Id(recovered[0]) : client.Create(model, values);
                    if (recovered.Count == 1 && !client.Write(model, new[] { operation.result_id }, values)) throw new ConsultantException("metadata_write_failed", "No se completó un componente recuperado.");
                }
                else if (!client.Write(model, new[] { operation.result_id }, values)) throw new ConsultantException("metadata_write_failed", "No se completó un componente creado previamente.");
                operation.status = "applied";
            }
            else { EnsureTarget(operation); Write(client, model, operation, values); }
        }

        private static void Write(OdooClient client, string model, ChangeOperation operation, Dictionary<string, object> values)
        {
            if (!client.Write(model, new[] { operation.result_id }, values)) throw new ConsultantException("metadata_write_failed", "Odoo no confirmó la actualización de " + model + ".");
            operation.status = "applied";
        }

        private static bool VerifyOperation(OdooClient client, Manifest manifest, ChangeSet change, ChangeOperation operation)
        {
            if (operation.status == "rolled_back" || operation.status == "preserved_manual_cleanup") return true;
            if (operation.type == "link_action_chain")
            {
                ChangeOperation parent = ComponentOperation(change, "server_action", JsonUtil.GetString(operation.parameters, "parent_action_key", true));
                foreach (string childKey in ObjectStrings(operation.parameters["child_action_keys"]))
                {
                    ChangeOperation child = ComponentOperation(change, "server_action", childKey);
                    IList<Dictionary<string, object>> rows = client.Read("ir.actions.server", new[] { child.result_id }, new[] { "id", "parent_id" });
                    if (rows.Count != 1 || RelationId(rows[0]["parent_id"]) != parent.result_id) return false;
                }
                return true;
            }
            if (operation.type == "bind_automation_action")
            {
                ChangeOperation automation = ComponentOperation(change, "automation", JsonUtil.GetString(operation.parameters, "automation_key", true));
                ChangeOperation action = ComponentOperation(change, "server_action", JsonUtil.GetString(operation.parameters, "action_key", true));
                IList<Dictionary<string, object>> rows = client.Read("ir.actions.server", new[] { action.result_id }, new[] { "id", "base_automation_id" });
                return rows.Count == 1 && RelationId(rows[0]["base_automation_id"]) == automation.result_id;
            }
            if (operation.type.Contains("view"))
            {
                if (operation.result_id <= 0) return false;
                IList<Dictionary<string, object>> rows = client.Read("ir.ui.view", new[] { operation.result_id }, ViewReadFields);
                if (rows.Count != 1) return false;
                if (operation.type == "deactivate_view") return !Bool(rows[0], "active");
                ChangeOperation action = ComponentOperation(change, "server_action", JsonUtil.GetString(operation.parameters, "action_key", true));
                Dictionary<string, object> desired = new Dictionary<string, object>(operation.parameters, StringComparer.Ordinal);
                desired["resolved_action_id"] = action.result_id;
                return DesiredMatches("view", desired, rows[0]);
            }
            if (operation.type == "activate_internal_automation" || operation.type == "arm_external_automation")
            {
                IList<Dictionary<string, object>> rows = client.Read("base.automation", new[] { operation.result_id }, new[] { "id", "active" });
                bool expected = operation.type == "activate_internal_automation" || operation.status == "applied" ? JsonUtil.GetBool(operation.parameters, "desired_active", true) : false;
                return rows.Count == 1 && Bool(rows[0], "active") == expected;
            }
            string technical = TechnicalModel(operation.type);
            if (operation.result_id <= 0) return false;
            IList<Dictionary<string, object>> actual = client.Read(technical, new[] { operation.result_id }, VerificationFields(technical));
            if (actual.Count != 1) return false;
            if (operation.type.StartsWith("deactivate_", StringComparison.Ordinal)) return technical == "ir.actions.server" ? RelationId(actual[0]["base_automation_id"]) == 0 && RelationId(actual[0]["parent_id"]) == 0 : !Bool(actual[0], "active");
            return DesiredMatches(KindFromOperation(operation.type), operation.parameters, actual[0]);
        }

        private static void RollbackOperation(OdooClient client, ChangeSet change, ChangeOperation operation)
        {
            if (operation.status == "reused") return;
            if (operation.type == "activate_internal_automation" || operation.type == "arm_external_automation")
            {
                if (operation.result_id > 0) client.Write("base.automation", new[] { operation.result_id }, JsonUtil.Dictionary("active", false)); operation.status = "rolled_back"; return;
            }
            if (operation.type == "bind_automation_action")
            {
                ChangeOperation action = ComponentOperation(change, "server_action", JsonUtil.GetString(operation.parameters, "action_key", true));
                if (action.result_id > 0) client.Write("ir.actions.server", new[] { action.result_id }, JsonUtil.Dictionary("base_automation_id", false, "usage", "ir_actions_server")); operation.status = "rolled_back"; return;
            }
            if (operation.type == "link_action_chain")
            {
                Dictionary<string, object> prior = ProtectedSnapshot.Unprotect(JsonUtil.GetString(operation.parameters, "previous_snapshot", false));
                foreach (string childKey in ObjectStrings(operation.parameters["child_action_keys"]))
                {
                    ChangeOperation child = ComponentOperation(change, "server_action", childKey); object previous = prior != null && prior.ContainsKey(childKey) ? prior[childKey] : false;
                    if (child.result_id > 0) client.Write("ir.actions.server", new[] { child.result_id }, JsonUtil.Dictionary("parent_id", previous));
                }
                operation.status = "rolled_back"; return;
            }
            string technical = TechnicalModel(operation.type);
            if (operation.type.StartsWith("create_", StringComparison.Ordinal))
            {
                if (operation.result_id <= 0) { operation.status = "rolled_back"; return; }
                Dictionary<string, object> neutral = technical == "mail.template" ? JsonUtil.Dictionary("active", false) : technical == "base.automation" ? JsonUtil.Dictionary("active", false) : technical == "ir.ui.view" ? JsonUtil.Dictionary("active", false) : technical == "ir.actions.server" ? JsonUtil.Dictionary("base_automation_id", false, "parent_id", false, "binding_model_id", false, "usage", "ir_actions_server") : null;
                if (neutral == null || !client.Write(technical, new[] { operation.result_id }, neutral)) operation.status = "preserved_manual_cleanup"; else operation.status = "rolled_back";
                return;
            }
            Dictionary<string, object> previousValues = ProtectedSnapshot.Unprotect(JsonUtil.GetString(operation.parameters, "previous_snapshot", true));
            Dictionary<string, object> restored = RestorableValues(technical, previousValues);
            if (operation.result_id <= 0 || !client.Write(technical, new[] { operation.result_id }, restored)) operation.status = "rollback_failed"; else operation.status = "rolled_back";
        }

        private static Dictionary<string, object> SafeSnapshot(string kind, Dictionary<string, object> row)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            if (kind == "computed_field")
            {
                Copy(result, row, new[] { "model", "name", "state", "depends", "store", "readonly" }); result["compute_sha256"] = JsonUtil.Sha256(Text(row, "compute").Replace("\r\n", "\n").Replace("\r", "\n"));
            }
            else if (kind == "mail_template")
            {
                Copy(result, row, new[] { "name", "model_id", "use_default_to", "auto_delete", "active" });
                foreach (string key in new[] { "description", "subject", "body_html", "email_from", "email_to", "email_cc", "reply_to" }) result[key + "_sha256"] = JsonUtil.Sha256(Text(row, key));
            }
            else if (kind == "server_action")
            {
                Copy(result, row, new[] { "name", "model_id", "state", "sequence", "usage", "base_automation_id", "parent_id", "child_ids", "template_id", "mail_post_method", "mail_post_autofollow", "activity_type_id", "activity_summary", "activity_date_deadline_range", "activity_date_deadline_range_type", "activity_user_type", "activity_user_id", "activity_user_field_name", "webhook_field_ids" });
                result["code_sha256"] = JsonUtil.Sha256(Text(row, "code").Replace("\r\n", "\n").Replace("\r", "\n")); result["activity_note_sha256"] = JsonUtil.Sha256(Text(row, "activity_note")); result["webhook_url_sha256"] = JsonUtil.Sha256(Text(row, "webhook_url"));
            }
            else if (kind == "view")
            {
                Copy(result, row, new[] { "name", "model", "type", "inherit_id", "mode", "priority", "active" });
                result["arch_sha256"] = JsonUtil.Sha256(NormalizeXml(Text(row, "arch")));
            }
            else
            {
                Copy(result, row, new[] { "name", "model_id", "active", "trigger", "trg_selection_field_id", "trg_field_ref", "trg_date_id", "trg_date_range", "trg_date_range_mode", "trg_date_range_type", "trg_date_calendar_id", "on_change_field_ids", "trigger_field_ids", "action_server_ids", "log_webhook_calls" });
                foreach (string key in new[] { "description", "filter_pre_domain", "filter_domain", "record_getter" }) result[key + "_sha256"] = JsonUtil.Sha256(Text(row, key).Replace("\r\n", "\n").Replace("\r", "\n"));
            }
            return NormalizeRecord(result);
        }

        private static Dictionary<string, object> RawSnapshot(string kind, Dictionary<string, object> row)
        {
            string[] allowed = kind == "computed_field" ? new[] { "compute", "depends" }
                : kind == "mail_template" ? new[] { "name", "description", "subject", "body_html", "email_from", "email_to", "email_cc", "reply_to", "use_default_to", "auto_delete", "active" }
                : kind == "server_action" ? new[] { "name", "state", "sequence", "usage", "base_automation_id", "parent_id", "code", "template_id", "mail_post_method", "mail_post_autofollow", "activity_type_id", "activity_summary", "activity_note", "activity_date_deadline_range", "activity_date_deadline_range_type", "activity_user_type", "activity_user_id", "activity_user_field_name", "webhook_url", "webhook_field_ids", "binding_model_id", "binding_type", "binding_view_types" }
                : kind == "view" ? new[] { "name", "arch", "priority", "active" }
                : new[] { "name", "description", "active", "trigger", "filter_pre_domain", "filter_domain", "on_change_field_ids", "trigger_field_ids", "trg_selection_field_id", "trg_field_ref", "trg_date_id", "trg_date_range", "trg_date_range_mode", "trg_date_range_type", "trg_date_calendar_id", "record_getter", "log_webhook_calls" };
            return row.Where(delegate(KeyValuePair<string, object> item) { return allowed.Contains(item.Key); }).ToDictionary(delegate(KeyValuePair<string, object> item) { return item.Key; }, delegate(KeyValuePair<string, object> item) { return item.Value; }, StringComparer.Ordinal);
        }

        private static bool DesiredMatches(string kind, Dictionary<string, object> desired, Dictionary<string, object> actual)
        {
            if (kind == "computed_field") return Text(actual, "state") == "manual" && Text(actual, "depends") == JsonUtil.GetString(desired, "depends", true) && JsonUtil.Sha256(Text(actual, "compute").Replace("\r\n", "\n").Replace("\r", "\n")) == JsonUtil.GetString(desired, "compute_sha256", true);
            if (Text(actual, "name") != JsonUtil.GetString(desired, "name", true)) return false;
            if (kind == "mail_template")
            {
                if (JsonUtil.GetString(desired, "subject_sha256", false) != null && JsonUtil.Sha256(Text(actual, "subject").Replace("\r\n", "\n").Replace("\r", "\n")) != JsonUtil.GetString(desired, "subject_sha256", true)) return false;
                if (JsonUtil.GetString(desired, "body_html_sha256", false) != null && JsonUtil.Sha256(Text(actual, "body_html").Replace("\r\n", "\n").Replace("\r", "\n")) != JsonUtil.GetString(desired, "body_html_sha256", true)) return false;
                if (desired.ContainsKey("active") && Bool(actual, "active") != JsonUtil.GetBool(desired, "active", true)) return false;
                foreach (string key in new[] { "email_to", "email_cc", "partner_to" }) { string wanted = JsonUtil.GetString(desired, key, false); if (!string.IsNullOrWhiteSpace(wanted) && !ValueMatches(wanted, Text(actual, key))) return false; }
                return true;
            }
            if (kind == "server_action")
            {
                string marker = JsonUtil.GetString(desired, "ownership_marker", true);
                string actualHelp = Text(actual, "help");
                if (Mode(desired) == "create" && !actualHelp.StartsWith(marker, StringComparison.Ordinal) && !Regex.Replace(actualHelp, "<[^>]+>", "").Trim().StartsWith(marker, StringComparison.Ordinal)) return false;
                string state = JsonUtil.GetString(desired, "state", true); if (Text(actual, "state") != state) return false;
                if (state == "code" && JsonUtil.Sha256(Text(actual, "code").Replace("\r\n", "\n").Replace("\r", "\n")) != JsonUtil.GetString(desired, "code_sha256", true)) return false;
                if (state == "mail_post" && Text(actual, "mail_post_method") != (JsonUtil.GetString(desired, "mail_post_method", false) ?? "email")) return false;
                if (state == "webhook" && !ValueMatches(JsonUtil.GetString(desired, "webhook_url", true), Text(actual, "webhook_url"))) return false;
                return true;
            }
            if (kind == "view")
            {
                long priority = JsonUtil.GetLong(desired, "priority", false); if (priority == 0) priority = 16;
                return Text(actual, "model") == JsonUtil.GetString(desired, "model", true)
                    && Text(actual, "type") == "form"
                    && Text(actual, "mode") == "extension"
                    && RelationId(actual["inherit_id"]) == JsonUtil.GetLong(desired, "resolved_parent_id", true)
                    && !string.IsNullOrWhiteSpace(JsonUtil.GetString(desired, "resolved_arch_hash", false))
                    && JsonUtil.Sha256(NormalizeXml(Text(actual, "arch"))) == JsonUtil.GetString(desired, "resolved_arch_hash", true)
                    && Convert.ToInt64(actual["priority"], CultureInfo.InvariantCulture) == priority
                    && Bool(actual, "active") == JsonUtil.GetBool(desired, "active", true);
            }
            string autoMarker = JsonUtil.GetString(desired, "ownership_marker", true);
            string actualDesc = Text(actual, "description");
            if (Mode(desired) == "create" && !actualDesc.StartsWith(autoMarker, StringComparison.Ordinal) && !Regex.Replace(actualDesc, "<[^>]+>", "").Trim().StartsWith(autoMarker, StringComparison.Ordinal)) return false;
            if (Text(actual, "trigger") != JsonUtil.GetString(desired, "trigger", true)) return false;
            if (desired.ContainsKey("active") && Bool(actual, "active") != JsonUtil.GetBool(desired, "active", true) && !JsonUtil.GetBool(desired, "external_effect", false)) return false;
            foreach (string key in new[] { "filter_domain", "filter_pre_domain", "record_getter" })
            {
                string hash = JsonUtil.GetString(desired, key + "_sha256", false); if (!string.IsNullOrWhiteSpace(hash) && JsonUtil.Sha256(Text(actual, key).Replace("\r\n", "\n").Replace("\r", "\n")) != hash) return false;
            }
            return true;
        }

        private static Dictionary<string, object> RestorableValues(string technicalModel, Dictionary<string, object> previous)
        {
            string[] allowed = technicalModel == "ir.model.fields" ? new[] { "compute", "depends" }
                : technicalModel == "mail.template" ? new[] { "name", "description", "subject", "body_html", "email_from", "email_to", "email_cc", "reply_to", "use_default_to", "auto_delete", "active" }
                : technicalModel == "ir.actions.server" ? new[] { "name", "state", "sequence", "usage", "base_automation_id", "parent_id", "code", "template_id", "mail_post_method", "mail_post_autofollow", "activity_type_id", "activity_summary", "activity_note", "activity_date_deadline_range", "activity_date_deadline_range_type", "activity_user_type", "activity_user_id", "activity_user_field_name", "webhook_url", "webhook_field_ids", "binding_model_id", "binding_type", "binding_view_types" }
                : technicalModel == "ir.ui.view" ? new[] { "name", "arch", "priority", "active" }
                : new[] { "name", "description", "active", "trigger", "filter_pre_domain", "filter_domain", "on_change_field_ids", "trigger_field_ids", "trg_selection_field_id", "trg_field_ref", "trg_date_id", "trg_date_range", "trg_date_range_mode", "trg_date_range_type", "trg_date_calendar_id", "record_getter", "log_webhook_calls" };
            Dictionary<string, object> result = previous.Where(delegate(KeyValuePair<string, object> item) { return allowed.Contains(item.Key); }).ToDictionary(delegate(KeyValuePair<string, object> item) { return item.Key; }, delegate(KeyValuePair<string, object> item) { return RestoreValue(technicalModel, item.Key, item.Value); }, StringComparer.Ordinal);
            return result;
        }

        private static Dictionary<string, string> ValidateReferences(Manifest manifest, IDictionary<string, object> references)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (references != null)
            {
                foreach (KeyValuePair<string, object> item in references)
                {
                    Dictionary<string, object> spec;
                    if (!TryReference(manifest, item.Key, out spec)) throw new ConsultantException("unexpected_reference", "Se proporcionó una referencia no declarada: " + item.Key + ".");
                    string value = Convert.ToString(item.Value, CultureInfo.InvariantCulture);
                    string type = JsonUtil.GetString(spec, "type", true);
                    if (type == "email" && !Regex.IsMatch(value ?? string.Empty, "^[^@\\s,;]+@[^@\\s,;]+\\.[^@\\s,;]+$")) throw new ConsultantException("invalid_email_reference", "La referencia de correo no es válida.");
                    if (type == "https_url") ValidateHttpsUrl(value);
                    if (type == "xml_id") ValidateXmlId(value);
                    result[item.Key] = value;
                }
            }
            foreach (string key in manifest.usedReferences) if (!result.ContainsKey(key)) throw new ConsultantException("unresolved_reference", "Falta la referencia requerida " + key + ".");
            return result;
        }

        private static Dictionary<string, long> ResolveXmlReferences(OdooClient client, Manifest manifest, Dictionary<string, string> supplied)
        {
            Dictionary<string, long> result = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> item in supplied)
            {
                Dictionary<string, object> spec; if (!TryReference(manifest, item.Key, out spec) || JsonUtil.GetString(spec, "type", true) != "xml_id") continue;
                result[item.Key] = ResolveXmlId(client, item.Value, JsonUtil.GetString(spec, "model", true));
            }
            return result;
        }

        private static void ValidateResume(OdooClient client, ChangeSet change)
        {
            foreach (ChangeOperation operation in change.operations.Where(delegate(ChangeOperation item) { return item.status == "applied" || item.status == "reused" || item.status == "external_pending"; }))
                if (!VerifyOperation(client, null, change, operation)) throw new ConsultantException("precondition_changed", "Un componente aplicado cambió antes del reintento.");
        }

        private static void EnsureExternalAutomationsInactive(OdooClient client, ChangeSet change)
        {
            List<ChangeOperation> gates = change.operations.Where(delegate(ChangeOperation item) { return item.type == "arm_external_automation"; }).ToList();
            if (DescriptorStrings(change, "external_automation_keys").Count > 0 && gates.Count == 0) throw new ConsultantException("external_activation_not_declared", "El paquete declara una automatización externa, pero no contiene su compuerta de activación.");
            foreach (ChangeOperation operation in gates)
            {
                IList<Dictionary<string, object>> rows = client.Read("base.automation", new[] { operation.result_id }, new[] { "id", "active" });
                if (rows.Count != 1 || Bool(rows[0], "active")) throw new ConsultantException("external_automation_active", "La prueba cerrada exige que las automatizaciones externas permanezcan inactivas.");
            }
        }

        internal static List<ChangeOperation> RequireExternalActivationGates(ChangeSet change)
        {
            List<ChangeOperation> gates = change == null || change.operations == null ? new List<ChangeOperation>() : change.operations.Where(delegate(ChangeOperation item) { return item.type == "arm_external_automation"; }).ToList();
            if (gates.Count == 0) throw new ConsultantException("external_activation_not_declared", "El paquete no contiene ninguna automatización externa declarada como activable. Regenera el paquete desde una réplica con desired_active_after_authorization=true.");
            if (gates.Any(delegate(ChangeOperation item) { return item.result_id <= 0; })) throw new ConsultantException("dependency_not_resolved", "Una automatización externa no tiene ID técnico aplicado.");
            return gates;
        }

        private object ProductionRunbook(Manifest manifest)
        {
            string id = "run-" + Guid.NewGuid().ToString("N");
            StringBuilder text = new StringBuilder();
            text.AppendLine("# Promoción manual de réplica Fase 3 " + manifest.slug).AppendLine()
                .AppendLine("- Entorno: `production`").AppendLine("- Hash: `" + manifest.hash + "`").AppendLine("- Escritura automática: bloqueada").AppendLine()
                .AppendLine("## Componentes").AppendLine().AppendLine("- Campos calculados: " + manifest.computedFields.Count).AppendLine("- Plantillas: " + manifest.templates.Count)
                .AppendLine("- Acciones de servidor: " + manifest.actions.Count).AppendLine("- Vistas ejecutables: " + manifest.views.Count).AppendLine("- Automatizaciones: " + manifest.automations.Count).AppendLine("- Efectos externos: " + manifest.externalAutomationKeys.Count);
            File.WriteAllText(project.RunbookPath(id), text.ToString(), new UTF8Encoding(false));
            AuditLog.Append(project, "production", "replica_preparar_paquete", id, "success", "Runbook Fase 3 generado sin red.", 0);
            return JsonUtil.Dictionary("environment", "production", "replica_slug", manifest.slug, "phase", 3, "execution", "manual_only", "runbook_id", id);
        }

        private OdooClient Open(string environment) { return project.OpenClient(environment, 30000); }
        private void Save(ChangeSet change) { JsonUtil.WriteJsonFile(project.ChangeSetPath(change.id), change); }
        private ChangeSet LoadPackage(string id) { if (!Regex.IsMatch(id ?? string.Empty, "^(chg|prom)-[a-f0-9]{32}$")) throw new ConsultantException("invalid_package_id", "El ID de paquete no es válido."); ChangeSet change = JsonUtil.ReadJsonFile<ChangeSet>(project.ChangeSetPath(id)); if (change.client_slug != project.Config.client_slug) throw new ConsultantException("package_tenant_mismatch", "El paquete pertenece a otro cliente."); return change; }

        private static object PublicReferences(Manifest manifest)
        {
            return manifest.usedReferences.OrderBy(delegate(string value) { return value; }, StringComparer.Ordinal).Select(delegate(string key)
            {
                Dictionary<string, object> spec = (Dictionary<string, object>)manifest.references[key];
                return JsonUtil.Dictionary("key", key, "type", JsonUtil.GetString(spec, "type", true), "model", JsonUtil.GetString(spec, "model", false), "purpose", JsonUtil.GetString(spec, "purpose", false));
            }).ToArray();
        }

        private static object PublicComponents(IEnumerable<Dictionary<string, object>> components) { return components.Select(PublicComponent).ToArray(); }
        private static Dictionary<string, object> PublicComponent(Dictionary<string, object> item) { return JsonUtil.Dictionary("key", item["key"], "kind", item["kind"], "mode", item["mode"], "classification", item["classification"], "technical_model", item["technical_model"], "technical_id", item["technical_id"], "actual_hash", item["actual_hash"], "desired_hash", item["desired_hash"], "reason", item["reason"]); }

        private static object Summary(ChangeSet change, bool confirmation)
        {
            Dictionary<string, object> result = JsonUtil.Dictionary(
                "package_id", change.id, "environment", change.environment, "replica_slug", change.replica_slug, "phase", 3, "status", change.status,
                "created_at", change.created_at, "expires_at", change.expires_at, "manifest_hash", change.replica_manifest_hash,
                "required_references", change.replica_descriptor == null ? null : change.replica_descriptor["required_references"],
                "external_test_actions", change.replica_descriptor == null ? null : change.replica_descriptor["external_test_action_keys"],
                "operations", change.operations.Select(PublicOperation).ToArray(), "uat_status", change.uat_status, "warnings", change.warnings, "last_error", change.last_error);
            if (confirmation) result["required_confirmation"] = string.Equals(change.category, "production_promotion", StringComparison.Ordinal) ? "PROMOVER-PRODUCCION " + change.id + " RESPALDO-CONFIRMADO" : "APLICAR " + change.id;
            if (!string.IsNullOrEmpty(change.backup_reference)) result["backup_reference"] = change.backup_reference;
            return result;
        }

        private static Dictionary<string, object> PublicOperation(ChangeOperation operation)
        {
            Dictionary<string, object> parameters = operation.parameters.Where(delegate(KeyValuePair<string, object> item)
            {
                return item.Key != "previous_snapshot" && item.Key != "email_from" && item.Key != "email_to" && item.Key != "email_cc" && item.Key != "reply_to" && item.Key != "webhook_url";
            }).ToDictionary(delegate(KeyValuePair<string, object> item) { return item.Key; }, delegate(KeyValuePair<string, object> item) { return item.Value; }, StringComparer.Ordinal);
            return JsonUtil.Dictionary("operation_id", operation.operation_id, "type", operation.type, "model", operation.model, "parameters", parameters, "status", operation.status, "result_id", operation.result_id, "verification", operation.verification);
        }

        private static string Integrity(ChangeSet change)
        {
            Dictionary<string, object> d = JsonUtil.Dictionary("schema_version", change.schema_version, "id", change.id, "client_slug", change.client_slug, "environment", change.environment, "category", change.category, "model", change.model, "target_models", change.target_models, "replica_slug", change.replica_slug, "replica_manifest_hash", change.replica_manifest_hash, "replica_descriptor", change.replica_descriptor, "created_at", change.created_at, "expires_at", change.expires_at, "precondition_hash", change.precondition_hash, "capability_hash", change.capability_hash, "profile_fingerprint", change.profile_fingerprint, "policy_version", change.policy_version, "operations", change.operations.Select(delegate(ChangeOperation operation) { return JsonUtil.Dictionary("operation_id", operation.operation_id, "type", operation.type, "model", operation.model, "parameters", operation.parameters); }).ToArray());
            if (!string.IsNullOrEmpty(change.backup_reference)) d["backup_reference"] = change.backup_reference;
            return JsonUtil.CanonicalHash(d);
        }

        private static void ValidateConfirmation(ChangeSet change, string confirmation, string verb)
        {
            DateTimeOffset expires; if (!DateTimeOffset.TryParse(change.expires_at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expires) || DateTimeOffset.UtcNow > expires) throw new ConsultantException("confirmation_expired", "La confirmación venció. Prepara un paquete nuevo.");
            string expected = string.Equals(change.category, "production_promotion", StringComparison.Ordinal) ? "PROMOVER-PRODUCCION " + change.id + " RESPALDO-CONFIRMADO" : verb + " " + change.id;
            if (!string.Equals(confirmation, expected, StringComparison.Ordinal)) throw new ConsultantException("confirmation_mismatch", "La confirmación debe ser exactamente " + expected + ".");
        }

        private static string Mode(IDictionary<string, object> component) { string value = JsonUtil.GetString(component, "mode", false) ?? "create"; if (!Modes.Contains(value)) throw new ConsultantException("invalid_component_mode", "mode debe ser create, update o deactivate."); return value; }
        private static void RequirePreviousHash(IDictionary<string, object> component) { if (!ShaPattern.IsMatch(JsonUtil.GetString(component, "previous_hash", true))) throw new ConsultantException("invalid_previous_hash", "previous_hash debe ser SHA-256 hexadecimal."); }
        private static void ValidateUniqueKeys(IEnumerable<Dictionary<string, object>> items, string group) { HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal); foreach (Dictionary<string, object> item in items) { string key = JsonUtil.GetString(item, "key", true); if (!KeyPattern.IsMatch(key) || !keys.Add(key)) throw new ConsultantException("invalid_component_key", "Las claves de " + group + " deben ser técnicas, estables y únicas."); } }
        private static void EnsureKeys(IDictionary<string, object> source, IEnumerable<string> keys) { foreach (string key in keys) JsonUtil.GetString(source, key, true); }
        private static void EnsureAllowedKeys(IDictionary<string, object> source, IEnumerable<string> keys, string group) { HashSet<string> allowed = new HashSet<string>(keys, StringComparer.Ordinal); string unknown = source.Keys.FirstOrDefault(delegate(string key) { return !allowed.Contains(key); }); if (unknown != null) throw new ConsultantException("unknown_component_parameter", "El parámetro " + unknown + " no está permitido en " + group + "."); }
        private static Dictionary<string, object> Dict(IDictionary<string, object> source, string key, bool required) { object value; Dictionary<string, object> result = source != null && source.TryGetValue(key, out value) ? value as Dictionary<string, object> : null; if (required && result == null) throw new ConsultantException("invalid_replica_manifest", "Falta el objeto " + key + "."); return result; }
        private static List<Dictionary<string, object>> DictList(IDictionary<string, object> source, string key) { object value = null; IEnumerable sequence = source != null && source.TryGetValue(key, out value) ? value as IEnumerable : null; List<Dictionary<string, object>> result = new List<Dictionary<string, object>>(); if (sequence != null && !(value is string)) foreach (object item in sequence) { Dictionary<string, object> row = item as Dictionary<string, object>; if (row == null) throw new ConsultantException("invalid_replica_manifest", key + " debe contener objetos."); result.Add(row); } return result; }
        private static List<string> StringList(IDictionary<string, object> source, string key) { object value; if (source == null || !source.TryGetValue(key, out value) || value == null) return new List<string>(); return ObjectStrings(value); }
        private static List<string> ObjectStrings(object value) { IEnumerable sequence = value as IEnumerable; if (sequence == null || value is string) throw new ConsultantException("invalid_replica_manifest", "Se esperaba una lista de textos."); List<string> result = new List<string>(); foreach (object item in sequence) { string text = item as string; if (string.IsNullOrWhiteSpace(text)) throw new ConsultantException("invalid_replica_manifest", "La lista contiene un texto vacío."); result.Add(text.Trim()); } return result; }
        private static void ValidateFieldName(string value) { if (!FieldPattern.IsMatch(value ?? string.Empty) || SecretFieldPattern.IsMatch(value)) throw new ConsultantException("invalid_field", "Nombre de campo no permitido."); }
        private static void ValidateXmlId(string value) { if (!Regex.IsMatch(value ?? string.Empty, "^[a-z0-9_]+\\.[a-z0-9_.-]+$")) throw new ConsultantException("invalid_xml_id", "La referencia XML ID no es válida."); }
        private static void ValidateHttpsUrl(string value) { Uri uri; if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrWhiteSpace(uri.Host)) throw new ConsultantException("invalid_https_reference", "La referencia de webhook debe ser una URL HTTPS sin credenciales embebidas."); }

        private static void RequireReference(Manifest manifest, string placeholder, string expectedType, string expectedModel)
        {
            Match match = PlaceholderPattern.Match(placeholder ?? string.Empty); if (!match.Success) throw new ConsultantException("typed_reference_required", "Se esperaba un marcador {{REFERENCIA}} tipado.");
            string key = match.Groups[1].Value; Dictionary<string, object> spec; if (!TryReference(manifest, key, out spec)) throw new ConsultantException("unknown_reference", "La referencia " + key + " no está declarada.");
            if (JsonUtil.GetString(spec, "type", true) != expectedType) throw new ConsultantException("reference_type_mismatch", "La referencia " + key + " no tiene el tipo requerido.");
            string model = JsonUtil.GetString(spec, "model", false); if (!string.IsNullOrWhiteSpace(expectedModel) && model != expectedModel) throw new ConsultantException("reference_model_mismatch", "La referencia " + key + " no apunta al modelo requerido.");
            Add(manifest.usedReferences, key);
        }

        private static bool TryReference(Manifest manifest, string key, out Dictionary<string, object> spec) { object value; spec = manifest.references.TryGetValue(key, out value) ? value as Dictionary<string, object> : null; return spec != null; }
        private static string ResolvePlaceholder(string value, IDictionary<string, string> supplied) { Match match = PlaceholderPattern.Match(value ?? string.Empty); return match.Success ? supplied[match.Groups[1].Value] : value; }
        private static long ResolveReferenceId(string placeholder, IDictionary<string, long> values) { Match match = PlaceholderPattern.Match(placeholder ?? string.Empty); if (!match.Success || !values.ContainsKey(match.Groups[1].Value)) throw new ConsultantException("unresolved_reference", "No se resolvió una referencia XML ID."); return values[match.Groups[1].Value]; }

        private static void ValidateFile(Manifest manifest, string relative, string expected, int maxChars) { if (!ShaPattern.IsMatch(expected ?? string.Empty)) throw new ConsultantException("replica_file_hash_mismatch", "Un hash de archivo no es válido."); string path = ContainedFile(manifest.root, relative); string text = File.ReadAllText(path, Encoding.UTF8); if (text.Length > maxChars || ReplicaService.FileHash(path) != expected) throw new ConsultantException("replica_file_hash_mismatch", "Un archivo de la réplica no coincide con su SHA-256 o supera el límite."); manifest.fileHashes[relative] = expected; }
        private static string ReadSigned(Manifest manifest, IDictionary<string, object> source, string fileKey, string hashKey, int maxChars) { string relative = JsonUtil.GetString(source, fileKey, true), expected = JsonUtil.GetString(source, hashKey, true); ValidateFile(manifest, relative, expected, maxChars); return File.ReadAllText(ContainedFile(manifest.root, relative), Encoding.UTF8).Replace("\r\n", "\n").Replace("\r", "\n"); }
        private static string ContainedFile(string root, string relative) { if (Path.IsPathRooted(relative ?? string.Empty)) throw new ConsultantException("external_replica_path", "La réplica no admite rutas absolutas."); string path = Path.GetFullPath(Path.Combine(root, relative)); string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) throw new ConsultantException("external_replica_path", "El archivo queda fuera de la réplica o no existe."); return path; }
        private static string Marker(Manifest manifest, IDictionary<string, object> component) { string description = JsonUtil.GetString(component, "description", false); return "OdooAI replica " + manifest.slug + "/" + JsonUtil.GetString(component, "key", true) + (string.IsNullOrWhiteSpace(description) ? string.Empty : "\n" + description); }

        private static long ResolveModelId(OdooClient client, string model) { IList<Dictionary<string, object>> rows = client.SearchRead("ir.model", new object[] { new object[] { "model", "=", model } }, new[] { "id" }, 2, null); if (rows.Count != 1) throw new ConsultantException("model_not_found", "No se resolvió el modelo del destino."); return Id(rows[0]); }
        private static long ResolveFieldId(OdooClient client, string model, string field) { IList<Dictionary<string, object>> rows = client.SearchRead("ir.model.fields", new object[] { new object[] { "model", "=", model }, new object[] { "name", "=", field } }, new[] { "id" }, 2, null); if (rows.Count != 1) throw new ConsultantException("field_not_found", "No se resolvió un campo técnico del destino."); return Id(rows[0]); }
        private static long ResolveSelectionId(OdooClient client, string model, string field, string value) { long fieldId = ResolveFieldId(client, model, field); IList<Dictionary<string, object>> rows = client.SearchRead("ir.model.fields.selection", new object[] { new object[] { "field_id", "=", fieldId }, new object[] { "value", "=", value } }, new[] { "id" }, 2, null); if (rows.Count != 1) throw new ConsultantException("selection_not_found", "No se resolvió el valor técnico de selección del disparador."); return Id(rows[0]); }
        private static long ResolveXmlId(OdooClient client, string xmlid, string expectedModel) { int separator = xmlid.IndexOf('.'); string module = xmlid.Substring(0, separator), name = xmlid.Substring(separator + 1); IList<Dictionary<string, object>> rows = client.SearchRead("ir.model.data", new object[] { new object[] { "module", "=", module }, new object[] { "name", "=", name }, new object[] { "model", "=", expectedModel } }, new[] { "id", "res_id" }, 2, null); if (rows.Count != 1) throw new ConsultantException("reference_not_found", "No se resolvió una referencia XML ID del destino."); return Convert.ToInt64(rows[0]["res_id"], CultureInfo.InvariantCulture); }
        private static object Many2ManyCommand(IEnumerable<long> ids) { return new object[] { new object[] { 6, 0, ids.Cast<object>().ToArray() } }; }

        private static ChangeOperation ComponentOperation(ChangeSet change, string kind, string key) { ChangeOperation result = change.operations.FirstOrDefault(delegate(ChangeOperation operation) { return (operation.type.Contains(kind) || (kind == "automation" && operation.type.EndsWith("_automation", StringComparison.Ordinal))) && JsonUtil.GetString(operation.parameters, "key", false) == key; }); if (result == null) throw new ConsultantException("dependency_not_resolved", "No se resolvió la dependencia lógica " + key + "."); return result; }
        private static void EnsureTarget(ChangeOperation operation) { if (operation == null || operation.result_id <= 0) throw new ConsultantException("dependency_not_resolved", "Una operación no tiene ID técnico resuelto."); }
        private static List<string> DescriptorStrings(ChangeSet change, string key) { if (change.replica_descriptor == null || !change.replica_descriptor.ContainsKey(key)) return new List<string>(); return ObjectStrings(change.replica_descriptor[key]); }
        private static string TechnicalModel(string operation) { if (operation.Contains("computed_field")) return "ir.model.fields"; if (operation.Contains("mail_template")) return "mail.template"; if (operation.Contains("server_action")) return "ir.actions.server"; if (operation.Contains("view")) return "ir.ui.view"; return "base.automation"; }
        private static string KindFromOperation(string operation) { if (operation.Contains("computed_field")) return "computed_field"; if (operation.Contains("mail_template")) return "mail_template"; if (operation.Contains("server_action")) return "server_action"; if (operation.Contains("view")) return "view"; return "automation"; }
        private static string[] VerificationFields(string model) { if (model == "ir.model.fields") return ComputedFieldReadFields; if (model == "mail.template") return TemplateReadFields; if (model == "ir.actions.server") return ActionReadFields; if (model == "ir.ui.view") return ViewReadFields; return AutomationReadFields; }
        private static bool IsCustomOwned(OdooClient client, string model, long id) { IList<Dictionary<string, object>> rows = client.SearchRead("ir.model.data", new object[] { new object[] { "model", "=", model }, new object[] { "res_id", "=", id } }, new[] { "id", "module" }, 20, null); return rows.Count == 0 || rows.All(delegate(Dictionary<string, object> row) { string module = Text(row, "module"); return module == "studio_customization" || module == "__export__"; }); }

        private static IEnumerable<string> RequiredTechnicalModels(Manifest manifest) { HashSet<string> result = new HashSet<string>(new[] { "ir.model", "ir.model.fields", "ir.model.data" }, StringComparer.Ordinal); if (manifest.actions.Count > 0) result.Add("ir.actions.server"); if (manifest.views.Count > 0) result.Add("ir.ui.view"); if (manifest.automations.Count > 0) result.Add("base.automation"); if (manifest.templates.Count > 0 || manifest.actions.Any(delegate(Dictionary<string, object> item) { return JsonUtil.GetString(item, "state", true) == "mail_post"; })) result.Add("mail.template"); if (manifest.actions.Any(delegate(Dictionary<string, object> item) { return JsonUtil.GetString(item, "state", true) == "next_activity"; })) result.Add("mail.activity.type"); if (manifest.automations.Any(delegate(Dictionary<string, object> item) { return !string.IsNullOrWhiteSpace(JsonUtil.GetString(item, "trigger_selection_field", false)); })) result.Add("ir.model.fields.selection"); return result; }
        private static bool NeedsWrite(Manifest manifest, string technical) { if (technical == "ir.model.fields") return manifest.computedFields.Count > 0; if (technical == "mail.template") return manifest.templates.Count > 0; if (technical == "ir.actions.server") return manifest.actions.Count > 0; if (technical == "ir.ui.view") return manifest.views.Count > 0; if (technical == "base.automation") return manifest.automations.Count > 0; return false; }
        private static bool NeedsCreate(Manifest manifest, string technical) { if (technical == "mail.template") return manifest.templates.Any(delegate(Dictionary<string, object> item) { return Mode(item) == "create"; }); if (technical == "ir.actions.server") return manifest.actions.Any(delegate(Dictionary<string, object> item) { return Mode(item) == "create"; }); if (technical == "ir.ui.view") return manifest.views.Any(delegate(Dictionary<string, object> item) { return Mode(item) == "create"; }); if (technical == "base.automation") return manifest.automations.Any(delegate(Dictionary<string, object> item) { return Mode(item) == "create"; }); return false; }
        private static bool SafeAccess(OdooClient client, string model, string operation) { try { return client.CheckAccessRights(model, operation); } catch { return false; } }

        private static bool ContainsExternalAction(Dictionary<string, Dictionary<string, object>> actions, string key) { Dictionary<string, object> action = actions[key]; string state = JsonUtil.GetString(action, "state", true); if (state == "webhook" || (state == "mail_post" && (JsonUtil.GetString(action, "mail_post_method", false) ?? "email") == "email")) return true; return state == "multi" && StringList(action, "child_action_keys").Any(delegate(string child) { return ContainsExternalAction(actions, child); }); }
        private static bool DesiredAutomationActive(IDictionary<string, object> automation) { return automation.ContainsKey("desired_active_after_authorization") ? JsonUtil.GetBool(automation, "desired_active_after_authorization", false) : JsonUtil.GetBool(automation, "active", true); }
        private static string NormalizeXml(string xml) { return Regex.Replace(System.Net.WebUtility.HtmlDecode(JsonUtil.SanitizeMojibake(xml ?? string.Empty)).Replace("\r\n", "\n").Replace("\r", "\n").Trim(), ">\\s+<", "><"); }
        private static bool ContainsState(Dictionary<string, Dictionary<string, object>> actions, string key, HashSet<string> states) { Dictionary<string, object> action = actions[key]; string state = JsonUtil.GetString(action, "state", true); return states.Contains(state) || (state == "multi" && StringList(action, "child_action_keys").Any(delegate(string child) { return ContainsState(actions, child, states); })); }
        private static void EnsureNoActionCycles(Dictionary<string, Dictionary<string, object>> actions) { foreach (string key in actions.Keys) VisitAction(actions, key, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal)); }
        private static void VisitAction(Dictionary<string, Dictionary<string, object>> actions, string key, HashSet<string> visiting, HashSet<string> complete) { if (complete.Contains(key)) return; if (!visiting.Add(key)) throw new ConsultantException("action_cycle", "Las acciones encadenadas contienen un ciclo."); foreach (string child in StringList(actions[key], "child_action_keys")) VisitAction(actions, child, visiting, complete); visiting.Remove(key); complete.Add(key); }

        private static bool ValueMatches(string desired, string actual) { return PlaceholderPattern.IsMatch(desired ?? string.Empty) ? !string.IsNullOrWhiteSpace(actual) && !PlaceholderPattern.IsMatch(actual) : string.Equals(desired ?? string.Empty, actual ?? string.Empty, StringComparison.Ordinal); }
        private static string DesiredHash(Dictionary<string, object> desired) { return JsonUtil.CanonicalHash(desired.Where(delegate(KeyValuePair<string, object> item) { return item.Key != "previous_hash"; }).ToDictionary(delegate(KeyValuePair<string, object> item) { return item.Key; }, delegate(KeyValuePair<string, object> item) { return item.Value; }, StringComparer.Ordinal)); }
        private static Dictionary<string, object> NormalizeRecord(Dictionary<string, object> source) { return source.OrderBy(delegate(KeyValuePair<string, object> item) { return item.Key; }, StringComparer.Ordinal).ToDictionary(delegate(KeyValuePair<string, object> item) { return item.Key; }, delegate(KeyValuePair<string, object> item) { return NormalizeValue(item.Value); }, StringComparer.Ordinal); }
        private static object NormalizeValue(object value) { if (value == null || value is string || value is bool || value is int || value is long || value is decimal || value is double) return value; IEnumerable sequence = value as IEnumerable; if (sequence != null) { List<object> items = new List<object>(); foreach (object item in sequence) items.Add(NormalizeValue(item)); return items.OrderBy(delegate(object item) { return Convert.ToString(item, CultureInfo.InvariantCulture); }, StringComparer.Ordinal).ToArray(); } return Convert.ToString(value, CultureInfo.InvariantCulture); }
        private static void Copy(Dictionary<string, object> target, IDictionary<string, object> source, IEnumerable<string> keys) { foreach (string key in keys) if (source.ContainsKey(key)) target[key] = source[key]; }
        private static void Add(List<string> values, string value) { if (!values.Contains(value)) values.Add(value); }
        private static string NewOp() { return "op-" + Guid.NewGuid().ToString("N"); }
        private static long Id(Dictionary<string, object> row) { return Convert.ToInt64(row["id"], CultureInfo.InvariantCulture); }
        private static string Text(IDictionary<string, object> source, string key) { object value; if (source == null || !source.TryGetValue(key, out value) || value == null || value is bool) return string.Empty; return Convert.ToString(value, CultureInfo.InvariantCulture); }
        private static bool Bool(IDictionary<string, object> source, string key) { object value; return source != null && source.TryGetValue(key, out value) && value != null && !(value is string && string.IsNullOrWhiteSpace((string)value)) && Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
        private static long RelationId(object value) { IEnumerable sequence = value as IEnumerable; if (sequence != null && !(value is string)) foreach (object item in sequence) { long parsed; if (long.TryParse(Convert.ToString(item, CultureInfo.InvariantCulture), out parsed)) return parsed; } long direct; return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out direct) ? direct : 0; }
        private static object RestoreValue(string model, string field, object value)
        {
            HashSet<string> many2one = model == "ir.actions.server"
                ? new HashSet<string>(new[] { "base_automation_id", "parent_id", "template_id", "activity_type_id", "activity_user_id", "binding_model_id" }, StringComparer.Ordinal)
                : model == "ir.ui.view" ? new HashSet<string>(new[] { "inherit_id" }, StringComparer.Ordinal)
                : new HashSet<string>(new[] { "trg_selection_field_id", "trg_field_ref", "trg_date_id", "trg_date_calendar_id" }, StringComparer.Ordinal);
            if (many2one.Contains(field)) { long id = RelationId(value); return id > 0 ? (object)id : false; }
            bool many2many = (model == "ir.actions.server" && field == "webhook_field_ids") || (model == "base.automation" && (field == "on_change_field_ids" || field == "trigger_field_ids"));
            if (many2many)
            {
                List<long> ids = new List<long>(); IEnumerable sequence = value as IEnumerable;
                if (sequence != null && !(value is string)) foreach (object item in sequence) { long id; if (long.TryParse(Convert.ToString(item, CultureInfo.InvariantCulture), out id)) ids.Add(id); }
                return Many2ManyCommand(ids);
            }
            return value;
        }
        private static string SafeError(Exception error) { return Redactor.SafeError(error); }

        private static readonly string[] ComputedFieldReadFields = new[] { "id", "model", "name", "field_description", "state", "ttype", "compute", "depends", "store", "readonly" };
        private static readonly string[] TemplateReadFields = new[] { "id", "name", "description", "model_id", "subject", "body_html", "email_from", "email_to", "email_cc", "reply_to", "partner_to", "use_default_to", "auto_delete", "active" };
        private static readonly string[] ActionReadFields = new[] { "id", "name", "model_id", "state", "sequence", "usage", "base_automation_id", "parent_id", "child_ids", "code", "template_id", "mail_post_method", "mail_post_autofollow", "activity_type_id", "activity_summary", "activity_note", "activity_date_deadline_range", "activity_date_deadline_range_type", "activity_user_type", "activity_user_id", "activity_user_field_name", "webhook_url", "webhook_field_ids", "binding_model_id", "binding_type", "binding_view_types", "help" };
        private static readonly string[] ViewReadFields = new[] { "id", "name", "model", "type", "inherit_id", "arch", "mode", "priority", "active" };
        private static readonly string[] AutomationReadFields = new[] { "id", "name", "description", "model_id", "active", "trigger", "filter_pre_domain", "filter_domain", "on_change_field_ids", "trigger_field_ids", "trg_selection_field_id", "trg_field_ref", "trg_date_id", "trg_date_range", "trg_date_range_mode", "trg_date_range_type", "trg_date_calendar_id", "action_server_ids", "record_getter", "log_webhook_calls" };

        private sealed class Manifest
        {
            public string slug; public string root; public string hash; public Dictionary<string, object> raw; public Dictionary<string, object> references;
            public List<Dictionary<string, object>> computedFields; public List<Dictionary<string, object>> templates; public List<Dictionary<string, object>> actions; public List<Dictionary<string, object>> views; public List<Dictionary<string, object>> automations;
            public List<string> targetModels; public List<string> usedReferences = new List<string>(); public List<string> externalTestActionKeys; public List<string> externalAutomationKeys; public List<string> inboundAutomationKeys;
            public Dictionary<string, string> fileHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        private sealed class Analysis
        {
            public List<Dictionary<string, object>> components; public List<string> blocked; public Dictionary<string, Dictionary<string, object>> previousValues; public Dictionary<string, long> modelIds; public Dictionary<string, long> fieldIds; public Dictionary<string, long> componentIds; public string preconditionHash; public string capabilityHash;
        }
    }
}
