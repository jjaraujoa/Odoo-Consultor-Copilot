using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace OdooConsultor
{
    public sealed class ReplicaService
    {
        private static readonly Regex FieldPattern = new Regex("^x_[a-z0-9_]{2,62}$", RegexOptions.Compiled);
        private static readonly Regex TablePattern = new Regex("^x_[a-z0-9_]{2,62}$", RegexOptions.Compiled);
        private static readonly HashSet<string> PhaseOneTypes = new HashSet<string>(new[] { "char", "binary", "selection", "many2many" }, StringComparer.Ordinal);
        private readonly ProjectStore project;

        public ReplicaService(ProjectStore value) { project = value; }

        public object Analyze(string environment, string slug)
        {
            if (ReplicaPhase4Service.IsPhase4(project, slug)) return new ReplicaPhase4Service(project).Analyze(environment, slug);
            if (ReplicaPhase3Service.IsPhase3(project, slug)) return new ReplicaPhase3Service(project).Analyze(environment, slug);
            if (ReplicaPhase2Service.IsPhase2(project, slug)) return new ReplicaPhase2Service(project).Analyze(environment, slug);
            environment = ProjectStore.NormalizeEnvironment(environment);
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                ReplicaManifest replica = Load(slug);
                using (OdooClient client = Open(environment))
                {
                    ReplicaAnalysis analysis = AnalyzeInternal(client, replica);
                    AuditLog.Append(project, environment, "replica_analizar_destino", null, analysis.blocked.Count == 0 ? "success" : "blocked",
                        "Réplica analizada: " + slug + "; componentes=" + analysis.components.Count.ToString(CultureInfo.InvariantCulture) + "; bloqueos=" + analysis.blocked.Count.ToString(CultureInfo.InvariantCulture) + ".", timer.ElapsedMilliseconds);
                    return AnalysisSummary(environment, replica, analysis);
                }
            }
            catch (Exception error)
            {
                AuditLog.Append(project, environment, "replica_analizar_destino", null, "error", SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }

        public ChangeSet PreparePromotion(string slug, string backupReference, IDictionary<string, object> references)
        {
            if (ReplicaPhase4Service.IsPhase4(project, slug)) return new ReplicaPhase4Service(project).PreparePromotion(slug, backupReference, references);
            if (ReplicaPhase3Service.IsPhase3(project, slug)) return new ReplicaPhase3Service(project).PreparePromotion(slug, backupReference, references);
            if (ReplicaPhase2Service.IsPhase2(project, slug)) return new ReplicaPhase2Service(project).PreparePromotion(slug, backupReference, references);
            throw new ConsultantException("unsupported_replica_phase", "La promoción a producción requiere una réplica estructurada (Fase 2, 3 o 4).");
        }

        public object Prepare(string environment, string slug)
        {
            if (ReplicaPhase4Service.IsPhase4(project, slug)) return new ReplicaPhase4Service(project).Prepare(environment, slug);
            if (ReplicaPhase3Service.IsPhase3(project, slug)) return new ReplicaPhase3Service(project).Prepare(environment, slug);
            if (ReplicaPhase2Service.IsPhase2(project, slug)) return new ReplicaPhase2Service(project).Prepare(environment, slug);
            environment = ProjectStore.NormalizeEnvironment(environment);
            ReplicaManifest replica = Load(slug);
            if (environment == "production")
            {
                string runbookId = "run-" + Guid.NewGuid().ToString("N");
                string path = project.RunbookPath(runbookId);
                StringBuilder text = new StringBuilder();
                text.AppendLine("# Promoción manual de réplica " + replica.slug).AppendLine();
                text.AppendLine("- Entorno: `production`");
                text.AppendLine("- Hash del manifiesto: `" + replica.hash + "`");
                text.AppendLine("- Ejecución automática: bloqueada").AppendLine();
                text.AppendLine("## Procedimiento").AppendLine();
                text.AppendLine("1. Validar primero el paquete equivalente en staging.");
                text.AppendLine("2. Revisar diferencias y referencias de producción.");
                text.AppendLine("3. Promover manualmente y ejecutar la UAT autorizada.");
                File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
                AuditLog.Append(project, environment, "replica_preparar_paquete", runbookId, "success", "Runbook de producción generado para réplica " + slug + ".", 0);
                return JsonUtil.Dictionary("environment", environment, "replica_slug", slug, "execution", "manual_only", "runbook_id", runbookId, "path", Path.Combine("cambios", "runbooks", runbookId + ".md"));
            }

            StudioService.EnsureWriteAllowed(environment);
            string packageId = "chg-" + Guid.NewGuid().ToString("N");
            ConnectionProfile profile = project.GetProfile(environment, true);
            using (OdooClient client = Open(environment))
            {
                ReplicaAnalysis analysis = AnalyzeInternal(client, replica);
                if (analysis.blocked.Count > 0) throw new ConsultantException("replica_blocked", "La réplica tiene colisiones o dependencias bloqueadas: " + string.Join("; ", analysis.blocked.ToArray()));
                List<ChangeOperation> operations = Compile(replica, analysis);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                ChangeSet change = new ChangeSet
                {
                    schema_version = 3,
                    id = packageId,
                    client_slug = project.Config.client_slug,
                    environment = environment,
                    category = "validated_replica",
                    risk = "high_controlled_staging",
                    status = "prepared",
                    model = replica.models.First(),
                    target_models = replica.models.OrderBy(delegate(string x) { return x; }, StringComparer.Ordinal).ToList(),
                    replica_slug = replica.slug,
                    replica_manifest_hash = replica.hash,
                    replica_descriptor = JsonUtil.Dictionary("manifest_schema", replica.schema, "uat_model", replica.action == null ? null : JsonUtil.GetString(replica.action, "model", true), "uat_action_key", replica.action == null ? null : "server_action"),
                    created_at = now.ToString("o", CultureInfo.InvariantCulture),
                    expires_at = now.AddMinutes(project.Config.package_confirmation_minutes).ToString("o", CultureInfo.InvariantCulture),
                    precondition_hash = analysis.preconditionHash,
                    capability_hash = analysis.capabilityHash,
                    profile_fingerprint = profile.credential_fingerprint,
                    requested_by = Environment.UserName,
                    policy_version = "replica-policy-1",
                    objective = "Instalar de forma idempotente la réplica validada " + replica.slug + ".",
                    contract_hash = replica.hash,
                    impact = "Crea metadatos Studio y una acción de servidor cuyo código coincide con el archivo firmado de la réplica.",
                    rollback_strategy = "Las vistas y la vinculación de la acción pueden desactivarse; los campos y sus datos se preservan.",
                    preconditions = new List<Dictionary<string, object>> { JsonUtil.Dictionary("type", "replica_destination_hash", "expected", analysis.preconditionHash), JsonUtil.Dictionary("type", "capabilities_hash", "expected", analysis.capabilityHash) },
                    validations = new List<Dictionary<string, object>> { JsonUtil.Dictionary("type", "manifest_and_files_sha256", "required", true), JsonUtil.Dictionary("type", "technical_metadata", "required", true) },
                    operations = operations,
                    warnings = new List<string> { "El código ejecutable se releerá desde replicas/ y se comprobará por SHA-256 antes de escribir.", "Si una escritura intermedia falla, el paquete quedará failed_partial sin reversión automática.", "Confirmación exacta: APLICAR " + packageId + "." }
                };
                change.integrity_hash = Integrity(change);
                JsonUtil.WriteJsonFile(project.ChangeSetPath(packageId), change);
                AuditLog.Append(project, environment, "replica_preparar_paquete", packageId, "success", "Paquete de réplica preparado; operaciones=" + operations.Count.ToString(CultureInfo.InvariantCulture) + ".", 0);
                return Summary(change, true);
            }
        }

        public object Apply(ChangeSet change, string confirmation, long testRecordId, IDictionary<string, object> references)
        {
            if (change != null && string.Equals(change.policy_version, ReplicaPhase4Service.PolicyVersion, StringComparison.Ordinal)) return new ReplicaPhase4Service(project).Apply(change, confirmation, references);
            if (change != null && string.Equals(change.policy_version, ReplicaPhase3Service.PolicyVersion, StringComparison.Ordinal)) return new ReplicaPhase3Service(project).Apply(change, confirmation, references);
            if (change != null && string.Equals(change.policy_version, ReplicaPhase2Service.PolicyVersion, StringComparison.Ordinal)) return new ReplicaPhase2Service(project).Apply(change, confirmation, references);
            ValidatePackage(change);
            if (references != null && references.Count > 0) throw new ConsultantException("unsupported_reference", "La Fase 1 solo resuelve el marcador interno SERVER_ACTION_ID; las referencias externas se habilitarán con su adaptador correspondiente.");
            StudioService.EnsureWriteAllowed(change.environment);
            if (change.status == "applied" || change.status == "verified" || change.status == "applied_uat_failed") return Summary(change, false);
            if (change.status != "prepared" && change.status != "failed_partial") throw new ConsultantException("package_not_applicable", "El paquete de réplica no se puede aplicar en su estado actual.");
            DateTimeOffset expires;
            if (!DateTimeOffset.TryParse(change.expires_at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expires) || DateTimeOffset.UtcNow > expires) throw new ConsultantException("confirmation_expired", "La confirmación venció. Prepara un paquete nuevo.");
            if (!string.Equals(confirmation, "APLICAR " + change.id, StringComparison.Ordinal)) throw new ConsultantException("confirmation_mismatch", "La confirmación debe ser exactamente APLICAR " + change.id + ".");
            ConnectionProfile profile = project.GetProfile(change.environment, true);
            if (!string.Equals(profile.credential_fingerprint, change.profile_fingerprint, StringComparison.Ordinal)) throw new ConsultantException("profile_changed", "La credencial cambió desde la preparación.");
            ReplicaManifest replica = Load(change.replica_slug);
            if (!string.Equals(replica.hash, change.replica_manifest_hash, StringComparison.Ordinal)) throw new ConsultantException("replica_integrity_failed", "El manifiesto cambió desde la preparación.");
            using (OdooClient client = Open(change.environment))
            {
                ReplicaAnalysis current = AnalyzeInternal(client, replica);
                if (change.status == "prepared" && (!string.Equals(current.preconditionHash, change.precondition_hash, StringComparison.Ordinal) || !string.Equals(current.capabilityHash, change.capability_hash, StringComparison.Ordinal)))
                    throw new ConsultantException("precondition_changed", "La configuración o los permisos cambiaron desde la vista previa.");
                change.status = "applying";
                Save(change);
                try
                {
                    foreach (ChangeOperation operation in change.operations)
                    {
                        if (operation.status == "applied" || operation.status == "reused") continue;
                        ApplyOperation(client, replica, change, operation);
                        Save(change);
                    }
                    change.status = "applied";
                    change.applied_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    Save(change);
                }
                catch (Exception error)
                {
                    change.status = "failed_partial";
                    change.last_error = SafeError(error);
                    Save(change);
                    throw;
                }
                if (replica.action != null)
                {
                    if (testRecordId <= 0)
                    {
                        change.uat_status = "pending_record";
                        Save(change);
                    }
                    else RunUat(client, change, testRecordId);
                }
            }
            AuditLog.Append(project, change.environment, "studio_aplicar_paquete", change.id, "success", "Réplica aplicada; operaciones=" + change.operations.Count.ToString(CultureInfo.InvariantCulture) + "; UAT=" + (change.uat_status ?? "not_declared") + ".", 0);
            return Summary(change, false);
        }

        public object RetryUat(string packageId, long testRecordId, string confirmation)
        {
            ChangeSet change = LoadPackage(packageId);
            ValidatePackage(change);
            if (!string.Equals(confirmation, "PROBAR " + packageId, StringComparison.Ordinal)) throw new ConsultantException("confirmation_mismatch", "La confirmación debe ser exactamente PROBAR " + packageId + ".");
            if (change.status != "applied" && change.status != "verified" && change.status != "applied_uat_failed") throw new ConsultantException("package_not_testable", "El paquete debe estar aplicado antes de ejecutar la UAT.");
            using (OdooClient client = Open(change.environment)) RunUat(client, change, testRecordId);
            AuditLog.Append(project, change.environment, "replica_ejecutar_uat", packageId, change.uat_status == "passed" ? "success" : "error", "UAT ejecutada; objetivo_hash=" + (change.uat_target_hash ?? string.Empty) + ".", 0);
            return Summary(change, false);
        }

        public object Verify(ChangeSet change)
        {
            if (change != null && string.Equals(change.policy_version, ReplicaPhase4Service.PolicyVersion, StringComparison.Ordinal)) return new ReplicaPhase4Service(project).Verify(change);
            if (change != null && string.Equals(change.policy_version, ReplicaPhase3Service.PolicyVersion, StringComparison.Ordinal)) return new ReplicaPhase3Service(project).Verify(change);
            if (change != null && string.Equals(change.policy_version, ReplicaPhase2Service.PolicyVersion, StringComparison.Ordinal)) return new ReplicaPhase2Service(project).Verify(change);
            ValidatePackage(change);
            if (change.status != "applied" && change.status != "verified" && change.status != "applied_uat_failed" && change.status != "rollback_partial" && change.status != "rolled_back") throw new ConsultantException("package_not_verifiable", "El paquete todavía no se ha aplicado.");
            bool ok = true;
            using (OdooClient client = Open(change.environment))
            {
                foreach (ChangeOperation operation in change.operations)
                {
                    bool verified = VerifyOperation(client, operation, change.status);
                    operation.verification = verified ? "verified" : "mismatch";
                    ok = ok && verified;
                }
            }
            if (ok && change.status == "applied" && (change.uat_status == null || change.uat_status == "passed")) change.status = "verified";
            change.verified_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            Save(change);
            return Summary(change, false);
        }

        public object Rollback(ChangeSet change, string confirmation)
        {
            if (change != null && string.Equals(change.policy_version, ReplicaPhase4Service.PolicyVersion, StringComparison.Ordinal)) return new ReplicaPhase4Service(project).Rollback(change, confirmation);
            if (change != null && string.Equals(change.policy_version, ReplicaPhase3Service.PolicyVersion, StringComparison.Ordinal)) return new ReplicaPhase3Service(project).Rollback(change, confirmation);
            if (change != null && string.Equals(change.policy_version, ReplicaPhase2Service.PolicyVersion, StringComparison.Ordinal)) return new ReplicaPhase2Service(project).Rollback(change, confirmation);
            ValidatePackage(change);
            StudioService.EnsureWriteAllowed(change.environment);
            if (!string.Equals(confirmation, "REVERTIR " + change.id, StringComparison.Ordinal)) throw new ConsultantException("confirmation_mismatch", "La confirmación debe ser exactamente REVERTIR " + change.id + ".");
            if (change.status == "rolled_back" || change.status == "rollback_partial") return Summary(change, false);
            if (change.status != "applied" && change.status != "verified" && change.status != "applied_uat_failed") throw new ConsultantException("package_not_reversible", "Solo se pueden revertir paquetes aplicados.");
            bool partial = false;
            using (OdooClient client = Open(change.environment))
            {
                foreach (ChangeOperation operation in change.operations.AsEnumerable().Reverse())
                {
                    if (operation.status == "reused") continue;
                    if (operation.type == "create_inherited_view" && operation.result_id > 0)
                        operation.status = client.Write("ir.ui.view", new[] { operation.result_id }, JsonUtil.Dictionary("active", false)) ? "rolled_back" : "rollback_failed";
                    else if (operation.type == "bind_server_action" && operation.result_id > 0)
                        operation.status = client.Write("ir.actions.server", new[] { operation.result_id }, JsonUtil.Dictionary("binding_model_id", false)) ? "rolled_back" : "rollback_failed";
                    else { operation.status = "preserved_manual_cleanup"; partial = true; }
                    if (operation.status == "rollback_failed") partial = true;
                }
            }
            change.status = partial ? "rollback_partial" : "rolled_back";
            change.rolled_back_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            Save(change);
            return Summary(change, false);
        }

        public static void ValidateReplicaView(string xml, bool actionExpected)
        {
            if (string.IsNullOrWhiteSpace(xml) || xml.Length > 50000) throw new ConsultantException("invalid_view_xml", "La vista está vacía o supera 50.000 caracteres.");
            XmlDocument document = new XmlDocument(); document.XmlResolver = null;
            try
            {
                XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 50000 };
                using (StringReader input = new StringReader(xml)) using (XmlReader reader = XmlReader.Create(input, settings)) document.Load(reader);
            }
            catch { throw new ConsultantException("invalid_view_xml", "La arquitectura de la vista no es XML válido."); }
            string lower = xml.ToLowerInvariant();
            foreach (string token in new[] { "<!doctype", "<!entity", "<script", "t-raw=", "type=\"object\"", "type='object'", "position=\"replace\"", "position='replace'", "position=\"attributes\"", "position='attributes'" })
                if (lower.Contains(token)) throw new ConsultantException("unsafe_view_xml", "La vista contiene un elemento no permitido: " + token + ".");
            XmlNodeList buttons = document.SelectNodes("//button");
            foreach (XmlNode button in buttons)
            {
                string type = button.Attributes["type"] == null ? string.Empty : button.Attributes["type"].Value;
                string name = button.Attributes["name"] == null ? string.Empty : button.Attributes["name"].Value;
                if (type != "action" || !actionExpected || name != "{{SERVER_ACTION_ID}}") throw new ConsultantException("unsafe_view_button", "Solo se admite un botón type=action que apunte a la acción declarada por la misma réplica.");
            }
        }

        public static string FileHash(string path)
        {
            string text = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n").Replace("\r", "\n");
            return JsonUtil.Sha256(text);
        }

        private ReplicaManifest Load(string slug)
        {
            string root = project.ReplicaPath(slug);
            string manifestPath = Path.Combine(root, "manifest.json");
            Dictionary<string, object> raw = JsonUtil.ReadJsonFile<Dictionary<string, object>>(manifestPath);
            int schema = Convert.ToInt32(raw.ContainsKey("schema_version") ? raw["schema_version"] : 0, CultureInfo.InvariantCulture);
            if (schema != 1 && schema != 2) throw new ConsultantException("unsupported_replica_schema", "Solo se admiten manifiestos de réplica schema 1 o 2.");
            Dictionary<string, object> source = Dict(raw, "source", true);
            if (JsonUtil.GetBool(source, "contains_business_records", true)) throw new ConsultantException("business_records_forbidden", "La réplica no puede contener registros de negocio.");
            ReplicaManifest result = new ReplicaManifest { slug = slug, root = root, raw = raw, schema = schema, fields = new List<Dictionary<string, object>>(), views = new List<Dictionary<string, object>>(), models = new List<string>() };
            foreach (Dictionary<string, object> field in DictList(raw, "fields"))
            {
                string model = JsonUtil.GetString(field, "model", true); StudioService.ValidateModel(model);
                string name = JsonUtil.GetString(field, "name", true); if (!FieldPattern.IsMatch(name)) throw new ConsultantException("invalid_custom_field", "Campo no válido en réplica: " + name + ".");
                string type = JsonUtil.GetString(field, "type", true); if (!PhaseOneTypes.Contains(type)) throw new ConsultantException("phase_not_available", "El tipo " + type + " pertenece a una fase posterior.");
                if (type == "many2many")
                {
                    string relationModel = JsonUtil.GetString(field, "relation", true); StudioService.ValidateModel(relationModel); AddModel(result.models, relationModel);
                    if (!TablePattern.IsMatch(JsonUtil.GetString(field, "relation_table", true))) throw new ConsultantException("invalid_relation_table", "La tabla Many2many no es válida.");
                    ValidateColumn(JsonUtil.GetString(field, "column1", true)); ValidateColumn(JsonUtil.GetString(field, "column2", true));
                }
                if (type == "binary" && !FieldPattern.IsMatch(JsonUtil.GetString(field, "filename_field", true))) throw new ConsultantException("binary_filename_required", "El campo binary requiere un auxiliar filename válido.");
                result.fields.Add(field); AddModel(result.models, model);
            }
            foreach (Dictionary<string, object> field in result.fields.Where(delegate(Dictionary<string, object> x) { return JsonUtil.GetString(x, "type", true) == "binary"; }))
            {
                string model = JsonUtil.GetString(field, "model", true), filename = JsonUtil.GetString(field, "filename_field", true);
                bool declared = result.fields.Any(delegate(Dictionary<string, object> x) { return JsonUtil.GetString(x, "model", true) == model && JsonUtil.GetString(x, "name", true) == filename && JsonUtil.GetString(x, "type", true) == "char"; });
                if (!declared) throw new ConsultantException("binary_filename_required", "El auxiliar filename debe declararse como campo char en la misma réplica.");
            }
            foreach (Dictionary<string, object> field in result.fields.Where(delegate(Dictionary<string, object> x) { return JsonUtil.GetString(x, "type", true) == "selection"; }))
            {
                HashSet<string> values = new HashSet<string>(StringComparer.Ordinal);
                foreach (Dictionary<string, object> option in DictList(field, "selection")) if (!values.Add(JsonUtil.GetString(option, "value", true))) throw new ConsultantException("duplicate_selection_value", "Una selección repite el mismo valor técnico.");
            }
            Dictionary<string, object> action = Dict(raw, "server_action", false);
            if (action != null)
            {
                string model = JsonUtil.GetString(action, "model", true); StudioService.ValidateModel(model); AddModel(result.models, model);
                if (JsonUtil.GetString(action, "state", true) != "code") throw new ConsultantException("unsupported_server_action", "La Fase 1 solo admite una acción de servidor state=code.");
                string relative = JsonUtil.GetString(action, "source_file", true);
                string sourcePath = ContainedFile(root, relative);
                string expected = JsonUtil.GetString(action, "source_sha256", true).ToLowerInvariant();
                if (!Regex.IsMatch(expected, "^[a-f0-9]{64}$") || FileHash(sourcePath) != expected) throw new ConsultantException("replica_file_hash_mismatch", "El archivo de código no coincide con el SHA-256 del manifiesto.");
                result.action = action;
            }
            foreach (Dictionary<string, object> view in DictList(raw, "views"))
            {
                string model = JsonUtil.GetString(view, "model", true); StudioService.ValidateModel(model); AddModel(result.models, model);
                ValidateReplicaView(JsonUtil.GetString(view, "arch_template", true), action != null);
                result.views.Add(view);
            }
            if (result.fields.Count == 0 && result.views.Count == 0 && result.action == null) throw new ConsultantException("empty_replica", "La réplica no contiene componentes soportados.");
            result.hash = JsonUtil.CanonicalHash(raw);
            return result;
        }

        private ReplicaAnalysis AnalyzeInternal(OdooClient client, ReplicaManifest replica)
        {
            ReplicaAnalysis analysis = new ReplicaAnalysis { components = new List<Dictionary<string, object>>(), blocked = new List<string>(), modelIds = new Dictionary<string, long>(StringComparer.Ordinal), parentViewIds = new Dictionary<string, long>(StringComparer.Ordinal) };
            foreach (string model in replica.models)
            {
                IList<Dictionary<string, object>> rows = client.SearchRead("ir.model", new object[] { new object[] { "model", "=", model } }, new[] { "id", "model" }, 2, null);
                if (rows.Count != 1) analysis.blocked.Add("modelo no visible: " + model); else analysis.modelIds[model] = Id(rows[0]);
            }
            Dictionary<string, bool> access = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (string technical in new[] { "ir.model.fields", "ir.model.fields.selection", "ir.actions.server", "ir.ui.view" })
            {
                access[technical + ":create"] = SafeAccess(client, technical, "create");
                access[technical + ":write"] = SafeAccess(client, technical, "write");
            }
            foreach (KeyValuePair<string, bool> item in access) if (!item.Value && (item.Key.EndsWith(":create", StringComparison.Ordinal) || item.Key == "ir.actions.server:write")) analysis.blocked.Add("permiso requerido: " + item.Key);

            foreach (Dictionary<string, object> field in replica.fields)
            {
                string model = JsonUtil.GetString(field, "model", true), name = JsonUtil.GetString(field, "name", true), type = JsonUtil.GetString(field, "type", true);
                IList<Dictionary<string, object>> found = client.SearchRead("ir.model.fields", new object[] { new object[] { "model", "=", model }, new object[] { "name", "=", name } },
                    new[] { "id", "model", "name", "field_description", "ttype", "required", "readonly", "store", "copied", "relation", "relation_table", "column1", "column2", "domain" }, 2, null);
                string disposition = "absent"; long id = 0;
                if (found.Count > 1) { disposition = "blocked"; analysis.blocked.Add("campo ambiguo: " + model + "." + name); }
                else if (found.Count == 1)
                {
                    id = Id(found[0]);
                    if (EquivalentField(field, found[0])) disposition = "compatible"; else { disposition = "blocked"; analysis.blocked.Add("colisión incompatible: " + model + "." + name); }
                    if (type == "selection" && disposition == "compatible")
                    {
                        IList<Dictionary<string, object>> options = client.SearchRead("ir.model.fields.selection", new object[] { new object[] { "field_id", "=", id } }, new[] { "id", "value", "name", "sequence" }, 100, "sequence,id");
                        foreach (Dictionary<string, object> desired in DictList(field, "selection"))
                        {
                            string optionValue = JsonUtil.GetString(desired, "value", true);
                            Dictionary<string, object> existing = options.FirstOrDefault(delegate(Dictionary<string, object> x) { return Convert.ToString(x["value"], CultureInfo.InvariantCulture) == optionValue; });
                            if (existing != null && (Convert.ToString(existing["name"], CultureInfo.InvariantCulture) != JsonUtil.GetString(desired, "label", true) || Convert.ToInt64(existing["sequence"], CultureInfo.InvariantCulture) != JsonUtil.GetLong(desired, "sequence", false)))
                            { disposition = "blocked"; analysis.blocked.Add("selección incompatible: " + model + "." + name + ":" + optionValue); }
                        }
                    }
                }
                if (type == "many2many")
                {
                    string table = JsonUtil.GetString(field, "relation_table", true);
                    IList<Dictionary<string, object>> owners = client.SearchRead("ir.model.fields", new object[] { new object[] { "relation_table", "=", table }, new object[] { "name", "!=", name } }, new[] { "id", "model", "name", "relation_table" }, 5, null);
                    if (owners.Count > 0) { disposition = "blocked"; analysis.blocked.Add("tabla Many2many ya usada: " + table); }
                }
                analysis.components.Add(JsonUtil.Dictionary("key", model + "." + name, "kind", "field", "classification", disposition, "technical_id", id));
            }
            if (replica.action != null)
            {
                string name = JsonUtil.GetString(replica.action, "name", true), model = JsonUtil.GetString(replica.action, "model", true);
                IList<Dictionary<string, object>> found = client.SearchRead("ir.actions.server", new object[] { new object[] { "name", "=", name }, new object[] { "model_name", "=", model } }, new[] { "id", "name", "model_id", "model_name", "state", "code", "binding_model_id", "binding_view_types" }, 2, null);
                string disposition = found.Count == 0 ? "absent" : "blocked"; long id = 0;
                if (found.Count == 1)
                {
                    id = Id(found[0]);
                    string code = File.ReadAllText(ContainedFile(replica.root, JsonUtil.GetString(replica.action, "source_file", true)), Encoding.UTF8).Replace("\r\n", "\n").Replace("\r", "\n");
                    long expectedBinding = analysis.modelIds.ContainsKey(JsonUtil.GetString(replica.action, "binding_model", true)) ? analysis.modelIds[JsonUtil.GetString(replica.action, "binding_model", true)] : 0;
                    string expectedViews = string.Join(",", Objects(replica.action["binding_view_types"]).Select(delegate(object x) { return Convert.ToString(x, CultureInfo.InvariantCulture); }).ToArray());
                    disposition = Convert.ToString(found[0]["state"], CultureInfo.InvariantCulture) == "code" && JsonUtil.Sha256(Convert.ToString(found[0]["code"], CultureInfo.InvariantCulture).Replace("\r\n", "\n").Replace("\r", "\n")) == JsonUtil.Sha256(code) && RelationId(found[0]["binding_model_id"]) == expectedBinding && Convert.ToString(found[0]["binding_view_types"], CultureInfo.InvariantCulture) == expectedViews ? "compatible" : "blocked";
                }
                if (disposition == "blocked") analysis.blocked.Add("colisión incompatible en acción: " + name);
                analysis.components.Add(JsonUtil.Dictionary("key", "server_action", "kind", "server_action", "classification", disposition, "technical_id", id));
            }
            foreach (Dictionary<string, object> view in replica.views)
            {
                string key = JsonUtil.GetString(view, "key", true), model = JsonUtil.GetString(view, "model", true), parentName = JsonUtil.GetString(view, "inherit_view_name", true);
                IList<Dictionary<string, object>> parents = client.SearchRead("ir.ui.view", new object[] { new object[] { "name", "=", parentName }, new object[] { "model", "=", model } }, new[] { "id", "name", "model", "type", "active" }, 3, null);
                if (parents.Count != 1) { analysis.blocked.Add("vista padre ambigua o ausente: " + parentName); continue; }
                long parentId = Id(parents[0]); analysis.parentViewIds[key] = parentId;
                string targetName = "OdooAI Replica - " + replica.slug + " - " + key;
                IList<Dictionary<string, object>> found = client.SearchRead("ir.ui.view", new object[] { new object[] { "name", "=", targetName }, new object[] { "model", "=", model }, new object[] { "inherit_id", "=", parentId } }, new[] { "id", "name", "model", "inherit_id", "arch", "active", "priority" }, 2, null);
                string disposition = found.Count == 0 ? "absent" : "blocked"; long id = 0;
                if (found.Count == 1)
                {
                    id = Id(found[0]);
                    Dictionary<string, object> actionComponent = analysis.components.FirstOrDefault(delegate(Dictionary<string, object> x) { return Convert.ToString(x["key"], CultureInfo.InvariantCulture) == "server_action"; });
                    long actionId = actionComponent == null ? 0 : Convert.ToInt64(actionComponent["technical_id"], CultureInfo.InvariantCulture);
                    string expectedArch = JsonUtil.GetString(view, "arch_template", true);
                    if (expectedArch.Contains("{{SERVER_ACTION_ID}}") && actionId > 0) expectedArch = expectedArch.Replace("{{SERVER_ACTION_ID}}", actionId.ToString(CultureInfo.InvariantCulture));
                    string actualArch = Convert.ToString(found[0]["arch"], CultureInfo.InvariantCulture);
                    bool equivalent = !expectedArch.Contains("{{") && NormalizeXml(actualArch) == NormalizeXml(expectedArch) && Convert.ToInt64(found[0]["priority"], CultureInfo.InvariantCulture) == JsonUtil.GetLong(view, "priority", false) && Convert.ToBoolean(found[0]["active"], CultureInfo.InvariantCulture) == JsonUtil.GetBool(view, "active", true);
                    disposition = equivalent ? "compatible" : "blocked";
                    if (!equivalent) analysis.blocked.Add("colisión incompatible en vista: " + key);
                }
                if (found.Count > 1) analysis.blocked.Add("vista de réplica ambigua: " + key);
                analysis.components.Add(JsonUtil.Dictionary("key", key, "kind", "view", "classification", disposition, "technical_id", id, "parent_id", parentId));
            }
            analysis.preconditionHash = JsonUtil.CanonicalHash(analysis.components);
            analysis.capabilityHash = JsonUtil.CanonicalHash(JsonUtil.Dictionary("models", replica.models.OrderBy(delegate(string x) { return x; }).ToArray(), "access", access));
            return analysis;
        }

        private List<ChangeOperation> Compile(ReplicaManifest replica, ReplicaAnalysis analysis)
        {
            List<ChangeOperation> operations = new List<ChangeOperation>();
            Dictionary<string, Dictionary<string, object>> components = analysis.components.ToDictionary(delegate(Dictionary<string, object> x) { return Convert.ToString(x["key"], CultureInfo.InvariantCulture); }, StringComparer.Ordinal);
            IEnumerable<Dictionary<string, object>> ordered = replica.fields.OrderBy(delegate(Dictionary<string, object> x) { string t = JsonUtil.GetString(x, "type", true); return t == "char" ? 0 : t == "binary" ? 1 : t == "selection" ? 2 : 4; });
            foreach (Dictionary<string, object> field in ordered)
            {
                string model = JsonUtil.GetString(field, "model", true), name = JsonUtil.GetString(field, "name", true);
                Dictionary<string, object> component = components[model + "." + name];
                Dictionary<string, object> parameters = FieldValues(field); parameters["model_id"] = analysis.modelIds[model];
                ChangeOperation op = Op("create_custom_field", model, parameters, component);
                operations.Add(op);
                if (JsonUtil.GetString(field, "type", true) == "selection")
                {
                    foreach (Dictionary<string, object> option in DictList(field, "selection"))
                    {
                        Dictionary<string, object> p = JsonUtil.Dictionary("field_model", model, "field_name", name, "value", JsonUtil.GetString(option, "value", true), "name", JsonUtil.GetString(option, "label", true), "sequence", JsonUtil.GetLong(option, "sequence", false));
                        operations.Add(new ChangeOperation { operation_id = "op-" + Guid.NewGuid().ToString("N"), type = "create_selection_option", model = model, parameters = p, status = "pending" });
                    }
                }
            }
            if (replica.action != null)
            {
                Dictionary<string, object> component = components["server_action"];
                Dictionary<string, object> p = JsonUtil.Dictionary("name", JsonUtil.GetString(replica.action, "name", true), "model_id", analysis.modelIds[JsonUtil.GetString(replica.action, "model", true)], "state", "code", "source_file", JsonUtil.GetString(replica.action, "source_file", true), "source_sha256", JsonUtil.GetString(replica.action, "source_sha256", true));
                operations.Add(Op("create_server_action", JsonUtil.GetString(replica.action, "model", true), p, component));
            }
            foreach (Dictionary<string, object> view in replica.views)
            {
                string key = JsonUtil.GetString(view, "key", true), model = JsonUtil.GetString(view, "model", true);
                Dictionary<string, object> p = JsonUtil.Dictionary("name", "OdooAI Replica - " + replica.slug + " - " + key, "inherit_id", analysis.parentViewIds[key], "arch_template", JsonUtil.GetString(view, "arch_template", true), "mode", "extension", "priority", JsonUtil.GetLong(view, "priority", false), "active", JsonUtil.GetBool(view, "active", true));
                operations.Add(Op("create_inherited_view", model, p, components[key]));
            }
            if (replica.action != null)
            {
                string views = string.Join(",", Objects(replica.action.ContainsKey("binding_view_types") ? replica.action["binding_view_types"] : null).Select(delegate(object x) { return Convert.ToString(x, CultureInfo.InvariantCulture); }).ToArray());
                ChangeOperation actionOperation = operations.First(delegate(ChangeOperation x) { return x.type == "create_server_action"; });
                operations.Add(new ChangeOperation { operation_id = "op-" + Guid.NewGuid().ToString("N"), type = "bind_server_action", model = JsonUtil.GetString(replica.action, "model", true), parameters = JsonUtil.Dictionary("binding_model_id", analysis.modelIds[JsonUtil.GetString(replica.action, "binding_model", true)], "binding_view_types", views), status = actionOperation.status == "reused" ? "reused" : "pending", result_id = actionOperation.result_id });
            }
            return operations;
        }

        private void ApplyOperation(OdooClient client, ReplicaManifest replica, ChangeSet change, ChangeOperation op)
        {
            if (op.status == "reused") return;
            if (op.type == "create_custom_field") op.result_id = client.Create("ir.model.fields", CleanValues(op.parameters, new[] { "disposition", "existing_id" }));
            else if (op.type == "create_selection_option")
            {
                ChangeOperation field = change.operations.First(delegate(ChangeOperation x) { return x.type == "create_custom_field" && x.model == Convert.ToString(op.parameters["field_model"], CultureInfo.InvariantCulture) && Convert.ToString(x.parameters["name"], CultureInfo.InvariantCulture) == Convert.ToString(op.parameters["field_name"], CultureInfo.InvariantCulture); });
                IList<Dictionary<string, object>> existing = client.SearchRead("ir.model.fields.selection", new object[] { new object[] { "field_id", "=", field.result_id }, new object[] { "value", "=", op.parameters["value"] } }, new[] { "id", "value", "name", "sequence" }, 2, null);
                if (existing.Count == 1 && Convert.ToString(existing[0]["name"], CultureInfo.InvariantCulture) == Convert.ToString(op.parameters["name"], CultureInfo.InvariantCulture) && Convert.ToInt64(existing[0]["sequence"], CultureInfo.InvariantCulture) == Convert.ToInt64(op.parameters["sequence"], CultureInfo.InvariantCulture)) { op.result_id = Id(existing[0]); op.status = "reused"; return; }
                if (existing.Count > 0) throw new ConsultantException("selection_collision", "Existe un valor técnico de selección incompatible.");
                op.result_id = client.Create("ir.model.fields.selection", JsonUtil.Dictionary("field_id", field.result_id, "value", op.parameters["value"], "name", op.parameters["name"], "sequence", op.parameters["sequence"]));
            }
            else if (op.type == "create_server_action")
            {
                string source = ContainedFile(replica.root, Convert.ToString(op.parameters["source_file"], CultureInfo.InvariantCulture));
                if (FileHash(source) != Convert.ToString(op.parameters["source_sha256"], CultureInfo.InvariantCulture)) throw new ConsultantException("replica_file_hash_mismatch", "El código cambió antes de aplicar.");
                op.result_id = client.Create("ir.actions.server", JsonUtil.Dictionary("name", op.parameters["name"], "model_id", op.parameters["model_id"], "state", "code", "code", File.ReadAllText(source, Encoding.UTF8).Replace("\r\n", "\n").Replace("\r", "\n")));
            }
            else if (op.type == "create_inherited_view")
            {
                string arch = Convert.ToString(op.parameters["arch_template"], CultureInfo.InvariantCulture);
                if (arch.Contains("{{SERVER_ACTION_ID}}")) arch = arch.Replace("{{SERVER_ACTION_ID}}", ServerAction(change).result_id.ToString(CultureInfo.InvariantCulture));
                if (Regex.IsMatch(arch, "\\{\\{[^}]+\\}\\}")) throw new ConsultantException("unresolved_placeholder", "La vista conserva referencias sin resolver.");
                ValidateResolvedView(arch, ServerAction(change).result_id);
                op.result_id = client.Create("ir.ui.view", JsonUtil.Dictionary("name", op.parameters["name"], "model", op.model, "inherit_id", op.parameters["inherit_id"], "arch", JsonUtil.XmlNumericEntities(arch), "mode", "extension", "priority", op.parameters["priority"], "active", op.parameters["active"]));
            }
            else if (op.type == "bind_server_action")
            {
                ChangeOperation action = ServerAction(change); op.result_id = action.result_id;
                if (!client.Write("ir.actions.server", new[] { action.result_id }, JsonUtil.Dictionary("binding_model_id", op.parameters["binding_model_id"], "binding_view_types", op.parameters["binding_view_types"]))) throw new ConsultantException("server_action_bind_failed", "Odoo no vinculó la acción de servidor.");
            }
            else throw new ConsultantException("tool_contract_violation", "Operación de réplica no permitida.");
            op.status = "applied";
        }

        private void RunUat(OdooClient client, ChangeSet change, long recordId)
        {
            if (recordId <= 0) throw new ConsultantException("invalid_uat_target", "registro_prueba_id debe ser positivo.");
            string model = Convert.ToString(change.replica_descriptor["uat_model"], CultureInfo.InvariantCulture);
            try
            {
                IList<Dictionary<string, object>> rows = client.Read(model, new[] { recordId }, new[] { "id" });
                if (rows.Count != 1) throw new ConsultantException("uat_record_not_found", "El registro de prueba no existe o no es visible.");
                client.RunServerAction(ServerAction(change).result_id, model, recordId);
                change.uat_status = "passed"; change.uat_target_hash = JsonUtil.Sha256(change.id + "|" + model + "|" + recordId.ToString(CultureInfo.InvariantCulture)); change.last_error = null; Save(change);
            }
            catch (Exception error)
            {
                change.status = "applied_uat_failed"; change.uat_status = "failed"; change.uat_target_hash = JsonUtil.Sha256(change.id + "|" + model + "|" + recordId.ToString(CultureInfo.InvariantCulture)); change.last_error = SafeError(error); Save(change);
            }
        }

        private bool VerifyOperation(OdooClient client, ChangeOperation op, string packageStatus)
        {
            if (op.result_id <= 0) return false;
            if (op.type == "create_custom_field") return client.Read("ir.model.fields", new[] { op.result_id }, new[] { "id", "name", "ttype" }).Count == 1;
            if (op.type == "create_selection_option") return client.Read("ir.model.fields.selection", new[] { op.result_id }, new[] { "id", "value", "name", "sequence" }).Count == 1;
            if (op.type == "create_server_action" || op.type == "bind_server_action") return client.Read("ir.actions.server", new[] { op.result_id }, new[] { "id", "state", "binding_model_id", "binding_view_types" }).Count == 1;
            if (op.type == "create_inherited_view")
            {
                IList<Dictionary<string, object>> rows = client.Read("ir.ui.view", new[] { op.result_id }, new[] { "id", "active", "model", "inherit_id" });
                if (rows.Count != 1) return false;
                bool active = Convert.ToBoolean(rows[0]["active"], CultureInfo.InvariantCulture);
                return packageStatus == "rollback_partial" || packageStatus == "rolled_back" ? !active : active;
            }
            return false;
        }

        private static ChangeOperation Op(string type, string model, Dictionary<string, object> parameters, Dictionary<string, object> component)
        {
            string disposition = Convert.ToString(component["classification"], CultureInfo.InvariantCulture);
            long id = Convert.ToInt64(component["technical_id"], CultureInfo.InvariantCulture);
            parameters["disposition"] = disposition; parameters["existing_id"] = id;
            return new ChangeOperation { operation_id = "op-" + Guid.NewGuid().ToString("N"), type = type, model = model, parameters = parameters, status = disposition == "compatible" ? "reused" : "pending", result_id = id };
        }

        private static Dictionary<string, object> FieldValues(Dictionary<string, object> field)
        {
            Dictionary<string, object> p = JsonUtil.Dictionary("name", JsonUtil.GetString(field, "name", true), "field_description", JsonUtil.GetString(field, "label", true), "ttype", JsonUtil.GetString(field, "type", true), "state", "manual", "required", JsonUtil.GetBool(field, "required", false), "readonly", JsonUtil.GetBool(field, "readonly", false), "store", JsonUtil.GetBool(field, "stored", true), "copied", JsonUtil.GetBool(field, "copied", true));
            foreach (string key in new[] { "relation", "relation_table", "column1", "column2", "domain" }) { string value = JsonUtil.GetString(field, key, false); if (!string.IsNullOrWhiteSpace(value)) p[key] = value; }
            return p;
        }

        private static bool EquivalentField(Dictionary<string, object> desired, Dictionary<string, object> actual)
        {
            if (Convert.ToString(actual["ttype"], CultureInfo.InvariantCulture) != JsonUtil.GetString(desired, "type", true)) return false;
            if (Convert.ToString(actual["field_description"], CultureInfo.InvariantCulture) != JsonUtil.GetString(desired, "label", true)) return false;
            foreach (string key in new[] { "required", "readonly" }) if (Convert.ToBoolean(actual[key], CultureInfo.InvariantCulture) != JsonUtil.GetBool(desired, key, false)) return false;
            if (Convert.ToBoolean(actual["store"], CultureInfo.InvariantCulture) != JsonUtil.GetBool(desired, "stored", true)) return false;
            if (Convert.ToBoolean(actual["copied"], CultureInfo.InvariantCulture) != JsonUtil.GetBool(desired, "copied", true)) return false;
            foreach (string key in new[] { "relation", "relation_table", "column1", "column2" })
            {
                string wanted = JsonUtil.GetString(desired, key, false) ?? string.Empty;
                string got = actual.ContainsKey(key) && actual[key] != null && !(actual[key] is bool) ? Convert.ToString(actual[key], CultureInfo.InvariantCulture) : string.Empty;
                if (wanted != got) return false;
            }
            return true;
        }

        private static void ValidateResolvedView(string xml, long actionId)
        {
            XmlDocument doc = new XmlDocument(); doc.XmlResolver = null; doc.LoadXml(xml);
            foreach (XmlNode button in doc.SelectNodes("//button"))
            {
                string type = button.Attributes["type"] == null ? string.Empty : button.Attributes["type"].Value;
                string name = button.Attributes["name"] == null ? string.Empty : button.Attributes["name"].Value;
                if (type != "action" || name != actionId.ToString(CultureInfo.InvariantCulture)) throw new ConsultantException("unsafe_view_button", "El botón no apunta a la acción creada por el paquete.");
            }
        }

        private static long RelationId(object value)
        {
            IEnumerable sequence = value as IEnumerable;
            if (sequence != null && !(value is string)) foreach (object item in sequence) { long parsed; if (long.TryParse(Convert.ToString(item, CultureInfo.InvariantCulture), out parsed)) return parsed; }
            long direct; return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out direct) ? direct : 0;
        }

        private static string NormalizeXml(string value) { return Regex.Replace(System.Net.WebUtility.HtmlDecode(JsonUtil.SanitizeMojibake(value ?? string.Empty)).Replace("\r", string.Empty).Replace("\n", string.Empty), ">\\s+<", "><").Trim(); }

        private static void ValidateColumn(string value) { if (!Regex.IsMatch(value ?? string.Empty, "^[a-z][a-z0-9_]{1,62}$")) throw new ConsultantException("invalid_relation_column", "Una columna Many2many no es válida."); }
        private static string ContainedFile(string root, string relative) { if (Path.IsPathRooted(relative ?? string.Empty)) throw new ConsultantException("external_replica_path", "La réplica no admite rutas absolutas."); string path = Path.GetFullPath(Path.Combine(root, relative)); string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) throw new ConsultantException("external_replica_path", "El archivo queda fuera de la réplica o no existe."); return path; }
        private OdooClient Open(string environment) { return project.OpenClient(environment, 30000); }
        private static bool SafeAccess(OdooClient client, string model, string operation) { try { return client.CheckAccessRights(model, operation); } catch { return false; } }
        private static long Id(Dictionary<string, object> row) { return Convert.ToInt64(row["id"], CultureInfo.InvariantCulture); }
        private static void AddModel(List<string> models, string model) { if (!models.Contains(model)) models.Add(model); }
        private void Save(ChangeSet change) { JsonUtil.WriteJsonFile(project.ChangeSetPath(change.id), change); }
        private ChangeSet LoadPackage(string id) { return JsonUtil.ReadJsonFile<ChangeSet>(project.ChangeSetPath(id)); }
        private static ChangeOperation ServerAction(ChangeSet change) { ChangeOperation op = change.operations.FirstOrDefault(delegate(ChangeOperation x) { return x.type == "create_server_action"; }); if (op == null || op.result_id <= 0) throw new ConsultantException("server_action_missing", "El paquete no tiene una acción de servidor resuelta."); return op; }
        private static Dictionary<string, object> CleanValues(Dictionary<string, object> input, IEnumerable<string> excluded) { HashSet<string> skip = new HashSet<string>(excluded, StringComparer.Ordinal); return input.Where(delegate(KeyValuePair<string, object> x) { return !skip.Contains(x.Key); }).ToDictionary(delegate(KeyValuePair<string, object> x) { return x.Key; }, delegate(KeyValuePair<string, object> x) { return x.Value; }, StringComparer.Ordinal); }
        private static Dictionary<string, object> Dict(IDictionary<string, object> source, string key, bool required) { object value; Dictionary<string, object> result = source != null && source.TryGetValue(key, out value) ? value as Dictionary<string, object> : null; if (required && result == null) throw new ConsultantException("invalid_replica_manifest", "Falta el objeto " + key + "."); return result; }
        private static List<Dictionary<string, object>> DictList(IDictionary<string, object> source, string key) { object value = null; List<Dictionary<string, object>> result = new List<Dictionary<string, object>>(); IEnumerable sequence = source != null && source.TryGetValue(key, out value) ? value as IEnumerable : null; if (sequence != null && !(value is string)) foreach (object item in sequence) { Dictionary<string, object> row = item as Dictionary<string, object>; if (row == null) throw new ConsultantException("invalid_replica_manifest", key + " debe contener objetos."); result.Add(row); } return result; }
        private static List<object> Objects(object value) { List<object> result = new List<object>(); IEnumerable sequence = value as IEnumerable; if (sequence != null && !(value is string)) foreach (object item in sequence) result.Add(item); return result; }

        private static string Integrity(ChangeSet change)
        {
            return JsonUtil.CanonicalHash(JsonUtil.Dictionary("schema_version", change.schema_version, "id", change.id, "client_slug", change.client_slug, "environment", change.environment, "category", change.category, "model", change.model, "target_models", change.target_models, "replica_slug", change.replica_slug, "replica_manifest_hash", change.replica_manifest_hash, "replica_descriptor", change.replica_descriptor, "created_at", change.created_at, "expires_at", change.expires_at, "precondition_hash", change.precondition_hash, "capability_hash", change.capability_hash, "profile_fingerprint", change.profile_fingerprint, "operations", change.operations.Select(delegate(ChangeOperation x) { return JsonUtil.Dictionary("operation_id", x.operation_id, "type", x.type, "model", x.model, "parameters", x.parameters); }).ToArray()));
        }

        public static void ValidatePackage(ChangeSet change)
        {
            if (change != null && string.Equals(change.policy_version, ReplicaPhase4Service.PolicyVersion, StringComparison.Ordinal)) { ReplicaPhase4Service.ValidatePackage(change); return; }
            if (change != null && string.Equals(change.policy_version, ReplicaPhase3Service.PolicyVersion, StringComparison.Ordinal)) { ReplicaPhase3Service.ValidatePackage(change); return; }
            if (change != null && string.Equals(change.policy_version, ReplicaPhase2Service.PolicyVersion, StringComparison.Ordinal)) { ReplicaPhase2Service.ValidatePackage(change); return; }
            if (change == null || change.schema_version != 3 || change.category != "validated_replica") throw new ConsultantException("unsupported_package_schema", "El paquete no es una réplica schema 3.");
            if (string.IsNullOrWhiteSpace(change.replica_slug) || change.target_models == null || change.target_models.Count == 0 || change.operations == null || change.operations.Count == 0 || change.operations.Count > 100) throw new ConsultantException("invalid_package", "El paquete de réplica está incompleto.");
            if (!string.Equals(change.integrity_hash, Integrity(change), StringComparison.Ordinal)) throw new ConsultantException("package_integrity_failed", "La parte inmutable del paquete fue modificada.");
            HashSet<string> allowed = new HashSet<string>(new[] { "create_custom_field", "create_selection_option", "create_server_action", "create_inherited_view", "bind_server_action" }, StringComparer.Ordinal);
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (ChangeOperation op in change.operations) { StudioService.ValidateModel(op.model); if (!allowed.Contains(op.type) || string.IsNullOrWhiteSpace(op.operation_id) || !ids.Add(op.operation_id)) throw new ConsultantException("tool_contract_violation", "El paquete contiene una operación no permitida o repetida."); }
        }

        private static object AnalysisSummary(string environment, ReplicaManifest replica, ReplicaAnalysis analysis) { return JsonUtil.Dictionary("environment", environment, "replica_slug", replica.slug, "manifest_schema", replica.schema, "manifest_hash", replica.hash, "classification", analysis.blocked.Count == 0 ? "ready" : "blocked", "components", analysis.components, "blocked", analysis.blocked, "precondition_hash", analysis.preconditionHash, "capability_hash", analysis.capabilityHash); }
        private static object Summary(ChangeSet change, bool confirmation) { Dictionary<string, object> result = JsonUtil.Dictionary("package_id", change.id, "environment", change.environment, "replica_slug", change.replica_slug, "status", change.status, "uat_status", change.uat_status, "created_at", change.created_at, "expires_at", change.expires_at, "manifest_hash", change.replica_manifest_hash, "precondition_hash", change.precondition_hash, "capability_hash", change.capability_hash, "operations", change.operations, "warnings", change.warnings, "last_error", change.last_error); if (confirmation) result["required_confirmation"] = "APLICAR " + change.id; return result; }
        private static string SafeError(Exception error) { return Redactor.SafeError(error); }

        private sealed class ReplicaManifest { public string slug; public string root; public int schema; public string hash; public Dictionary<string, object> raw; public List<Dictionary<string, object>> fields; public Dictionary<string, object> action; public List<Dictionary<string, object>> views; public List<string> models; }
        private sealed class ReplicaAnalysis { public List<Dictionary<string, object>> components; public List<string> blocked; public Dictionary<string, long> modelIds; public Dictionary<string, long> parentViewIds; public string preconditionHash; public string capabilityHash; }
    }
}
