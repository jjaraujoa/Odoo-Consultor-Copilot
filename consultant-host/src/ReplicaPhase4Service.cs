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
    public sealed class ReplicaPhase4Service
    {
        public const string PolicyVersion = "replica-policy-4.0";
        private static readonly Regex KeyPattern = new Regex("^[a-z][a-z0-9_]{1,62}$", RegexOptions.Compiled);
        private static readonly Regex RefPattern = new Regex("^[A-Z][A-Z0-9_]{1,62}$", RegexOptions.Compiled);
        private static readonly Regex ShaPattern = new Regex("^[a-f0-9]{64}$", RegexOptions.Compiled);
        private static readonly Regex TemplatePattern = new Regex("^odooai\\.[a-z0-9_]{2,100}$", RegexOptions.Compiled);
        private static readonly Regex XmlIdPattern = new Regex("^[a-z0-9_]+\\.[a-z0-9_.-]+$", RegexOptions.Compiled);
        private static readonly HashSet<string> Modes = new HashSet<string>(new[] { "create", "update", "deactivate" }, StringComparer.Ordinal);
        private static readonly HashSet<string> ReportTypes = new HashSet<string>(new[] { "qweb-pdf", "qweb-html" }, StringComparer.Ordinal);
        private static readonly HashSet<string> Operations = new HashSet<string>(new[] { "read", "create", "write", "unlink" }, StringComparer.Ordinal);
        private static readonly HashSet<string> ReferenceTypes = new HashSet<string>(new[] { "xml_id", "user_login" }, StringComparer.Ordinal);
        private static readonly HashSet<string> ReferenceModels = new HashSet<string>(new[] { "res.groups", "res.users", "report.paperformat", "ir.actions.actions", "ir.actions.server" }, StringComparer.Ordinal);
        private readonly ProjectStore project;

        public ReplicaPhase4Service(ProjectStore value) { project = value; }

        public static bool IsPhase4(ProjectStore project, string slug)
        {
            try
            {
                string path = Path.Combine(project.ReplicaPath(slug), "manifest.json");
                Dictionary<string, object> raw = JsonUtil.ReadJsonFile<Dictionary<string, object>>(path);
                return JsonUtil.GetLong(raw, "schema_version", false) == 2 && JsonUtil.GetLong(raw, "phase", false) == 4;
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
                    "Réplica Fase 4 analizada; componentes=" + analysis.components.Count.ToString(CultureInfo.InvariantCulture) + "; bloqueos=" + analysis.blocked.Count.ToString(CultureInfo.InvariantCulture) + ".", 0);
                return JsonUtil.Dictionary(
                    "environment", environment, "replica_slug", slug, "phase", 4, "manifest_hash", manifest.hash,
                    "classification", analysis.blocked.Count == 0 ? "ready" : "blocked",
                    "components", PublicComponents(analysis.components), "blocked", analysis.blocked,
                    "required_references", PublicReferences(manifest), "approval_contract", analysis.approvalContract,
                    "negative_access_tests", manifest.negativeTests.Select(delegate(Dictionary<string, object> item) { return JsonUtil.Dictionary("key", JsonUtil.GetString(item, "key", true), "model", JsonUtil.GetString(item, "model", true), "operation", JsonUtil.GetString(item, "operation", true), "expected_allowed", JsonUtil.GetBool(item, "expected_allowed", false)); }).ToArray(),
                    "precondition_hash", analysis.preconditionHash, "capability_hash", analysis.capabilityHash);
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
                if (analysis.blocked.Count > 0) throw new ConsultantException("replica_blocked", "La réplica Fase 4 tiene componentes bloqueados: " + string.Join("; ", analysis.blocked.ToArray()));
                string id = "chg-" + Guid.NewGuid().ToString("N");
                DateTimeOffset now = DateTimeOffset.UtcNow;
                ChangeSet change = new ChangeSet
                {
                    schema_version = 3, id = id, client_slug = project.Config.client_slug, environment = environment,
                    category = "validated_replica_phase4", risk = "security_controlled_staging", status = "prepared",
                    model = manifest.targetModels.First(), target_models = manifest.targetModels.OrderBy(delegate(string x) { return x; }, StringComparer.Ordinal).ToList(),
                    replica_slug = manifest.slug, replica_manifest_hash = manifest.hash,
                    replica_descriptor = JsonUtil.Dictionary(
                        "manifest_schema", 2, "phase", 4, "required_references", PublicReferences(manifest),
                        "negative_access_test_keys", manifest.negativeTests.Select(delegate(Dictionary<string, object> x) { return JsonUtil.GetString(x, "key", true); }).ToArray()),
                    created_at = now.ToString("o", CultureInfo.InvariantCulture),
                    expires_at = now.AddMinutes(project.Config.package_confirmation_minutes).ToString("o", CultureInfo.InvariantCulture),
                    precondition_hash = analysis.preconditionHash, capability_hash = analysis.capabilityHash,
                    profile_fingerprint = profile.credential_fingerprint, requested_by = Environment.UserName,
                    policy_version = PolicyVersion, objective = "Instalar reportes, aprobaciones y seguridad declarados por la réplica " + manifest.slug + ".",
                    contract_hash = manifest.hash,
                    impact = "Crea o modifica metadatos QWeb, reportes, aprobaciones, ACL y reglas de registro exclusivamente en staging.",
                    rollback_strategy = "Restaura snapshots protegidos y desactiva artefactos creados; no elimina registros ni metadatos.",
                    preconditions = new List<Dictionary<string, object>> { JsonUtil.Dictionary("type", "destination_hash", "expected", analysis.preconditionHash), JsonUtil.Dictionary("type", "capabilities_hash", "expected", analysis.capabilityHash) },
                    validations = new List<Dictionary<string, object>> { JsonUtil.Dictionary("type", "schema_2_phase_4", "required", true), JsonUtil.Dictionary("type", "files_sha256_reread", "required", true), JsonUtil.Dictionary("type", "previous_hash_for_updates", "required", true), JsonUtil.Dictionary("type", "negative_access_tests_declared", "required", manifest.accessControls.Count + manifest.recordRules.Count > 0) },
                    operations = Compile(manifest, analysis),
                    warnings = new List<string> { "Los XML QWeb y dominios se releerán desde replicas/ y se comprobarán por SHA-256 antes de escribir.", "Las referencias de grupos, usuarios, formatos y acciones se resolverán en memoria antes de la primera escritura.", "Las pruebas negativas requieren una API key separada del usuario funcional; el perfil administrador no demuestra restricciones.", "Confirmación exacta: APLICAR " + id + "." }
                };
                change.integrity_hash = Integrity(change);
                ValidatePackage(change);
                JsonUtil.WriteJsonFile(project.ChangeSetPath(id), change);
                AuditLog.Append(project, environment, "replica_preparar_paquete", id, "success", "Paquete Fase 4 preparado; operaciones=" + change.operations.Count.ToString(CultureInfo.InvariantCulture) + ".", 0);
                return Summary(change, true);
            }
        }

        public object Apply(ChangeSet change, string confirmation, IDictionary<string, object> references)
        {
            ValidatePackage(change);
            StudioService.EnsureWriteAllowed(change.environment, change.category);
            if (change.status == "applied" || change.status == "verified") return Summary(change, false);
            if (change.status != "prepared" && change.status != "failed_partial") throw new ConsultantException("package_not_applicable", "El paquete Fase 4 no se puede aplicar en su estado actual.");
            ValidateConfirmation(change, confirmation, "APLICAR");
            ConnectionProfile profile = project.GetProfile(change.environment, true);
            if (profile.credential_fingerprint != change.profile_fingerprint) throw new ConsultantException("profile_changed", "La credencial cambió desde la preparación.");
            Manifest manifest = Load(change.replica_slug);
            if (manifest.hash != change.replica_manifest_hash) throw new ConsultantException("replica_integrity_failed", "El manifiesto o uno de sus archivos cambió desde la preparación.");
            using (OdooClient client = Open(change.environment))
            {
                Analysis current = AnalyzeInternal(client, manifest);
                if (change.status == "prepared" && (current.preconditionHash != change.precondition_hash || current.capabilityHash != change.capability_hash)) throw new ConsultantException("precondition_changed", "La configuración o los permisos cambiaron desde la vista previa.");
                Dictionary<string, long> resolved = ResolveReferences(client, manifest, references);
                if (change.status == "failed_partial") ValidateResume(client, manifest, change);
                change.status = "applying"; Save(change);
                try
                {
                    foreach (ChangeOperation operation in change.operations)
                    {
                        if (operation.status == "applied") continue;
                        ApplyOperation(client, manifest, change, operation, resolved);
                        Save(change);
                    }
                    change.status = "applied"; change.applied_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    if (manifest.negativeTests.Count > 0) change.uat_status = "negative_access_tests_pending";
                    Save(change);
                }
                catch (Exception error)
                {
                    change.status = "failed_partial"; change.last_error = SafeError(error); Save(change); throw;
                }
            }
            AuditLog.Append(project, change.environment, "studio_aplicar_paquete", change.id, "success", "Paquete Fase 4 aplicado; operaciones=" + change.operations.Count.ToString(CultureInfo.InvariantCulture) + "; pruebas=" + (change.uat_status ?? "none") + ".", 0);
            return Summary(change, false);
        }

        public object Verify(ChangeSet change)
        {
            ValidatePackage(change);
            if (change.status != "applied" && change.status != "verified" && change.status != "rollback_partial" && change.status != "rolled_back") throw new ConsultantException("package_not_verifiable", "El paquete Fase 4 todavía no se ha aplicado.");
            bool ok = true;
            Manifest manifest = Load(change.replica_slug);
            using (OdooClient client = Open(change.environment))
            {
                foreach (ChangeOperation operation in change.operations)
                {
                    bool match = VerifyOperation(client, manifest, operation, change.status);
                    operation.verification = match ? "verified" : "mismatch"; ok = ok && match;
                }
            }
            if (ok && change.status == "applied" && (change.uat_status == null || change.uat_status == "negative_access_tests_passed")) change.status = "verified";
            change.verified_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture); Save(change);
            return Summary(change, false);
        }

        public object VerifyNegativeAccess(string packageId, string testKey, string profileAlias)
        {
            ChangeSet change = LoadPackage(packageId); ValidatePackage(change);
            if (change.status != "applied" && change.status != "verified") throw new ConsultantException("package_not_testable", "El paquete Fase 4 debe estar aplicado antes de verificar accesos.");
            Manifest manifest = Load(change.replica_slug);
            Dictionary<string, object> test = manifest.negativeTests.FirstOrDefault(delegate(Dictionary<string, object> x) { return JsonUtil.GetString(x, "key", true) == testKey; });
            if (test == null) throw new ConsultantException("access_test_not_found", "La prueba negativa no está declarada por la réplica.");
            ConnectionProfile verifier = project.GetVerificationProfile(profileAlias, true);
            ConnectionProfile primary = project.GetProfile(change.environment, true);
            if (!SameInstance(primary, verifier)) throw new ConsultantException("verification_profile_mismatch", "El perfil de prueba no pertenece a la misma base staging.");
            bool expected = JsonUtil.GetBool(test, "expected_allowed", false);
            string model = JsonUtil.GetString(test, "model", true), operation = JsonUtil.GetString(test, "operation", true);
            bool actual;
            long visibleCount = -1;
            using (OdooClient client = ProjectStore.OpenClient(verifier, 30000))
                {
                    actual = SafeAccess(client, model, operation);
                    string domainFile = JsonUtil.GetString(test, "probe_domain_file", false);
                    if (!string.IsNullOrWhiteSpace(domainFile))
                    {
                        if (!actual || operation != "read") visibleCount = 0;
                        else
                        {
                            object[] domain = ReadDomain(manifest, domainFile, JsonUtil.GetString(test, "probe_domain_sha256", true));
                            visibleCount = client.SearchRead(model, domain, new[] { "id" }, 10, null).Count;
                        }
                    }
                }
            bool passed = actual == expected;
            if (passed && visibleCount >= 0 && test.ContainsKey("expected_visible_count")) passed = visibleCount == JsonUtil.GetLong(test, "expected_visible_count", true);
            string hash = JsonUtil.Sha256(profileAlias.ToLowerInvariant());
            AuditLog.Append(project, change.environment, "replica_verificar_acceso_negativo", change.id, passed ? "success" : "error", "Prueba=" + JsonUtil.Sha256(testKey) + "; perfil=" + hash + "; resultado=" + (passed ? "passed" : "failed") + ".", 0);
            if (!passed) throw new ConsultantException("negative_access_test_failed", "La prueba de acceso no produjo el resultado esperado.");
            MarkNegativeTest(change, testKey);
            return JsonUtil.Dictionary("package_id", packageId, "test_key", testKey, "profile_alias_hash", hash, "passed", true, "effective_access", actual, "visible_count", visibleCount < 0 ? null : (object)visibleCount, "uat_status", change.uat_status);
        }

        public object Rollback(ChangeSet change, string confirmation)
        {
            ValidatePackage(change); StudioService.EnsureWriteAllowed(change.environment);
            if (confirmation != "REVERTIR " + change.id) throw new ConsultantException("confirmation_mismatch", "La confirmación debe ser exactamente REVERTIR " + change.id + ".");
            if (change.status == "rolled_back" || change.status == "rollback_partial") return Summary(change, false);
            if (change.status != "applied" && change.status != "verified") throw new ConsultantException("package_not_reversible", "Solo se pueden revertir paquetes aplicados.");
            bool partial = false;
            using (OdooClient client = Open(change.environment))
            {
                foreach (ChangeOperation operation in change.operations.AsEnumerable().Reverse())
                {
                    if (operation.status == "reused") continue;
                    try
                    {
                        string technical = TechnicalModel(operation.type);
                        string snapshot = JsonUtil.GetString(operation.parameters, "previous_snapshot", false);
                        if (!string.IsNullOrWhiteSpace(snapshot) && operation.result_id > 0)
                        {
                            Dictionary<string, object> previous = ProtectedSnapshot.Unprotect(snapshot);
                            operation.status = client.Write(technical, new[] { operation.result_id }, RestorableValues(technical, previous)) ? "rolled_back" : "rollback_failed";
                        }
                        else if (operation.result_id > 0)
                        {
                            Dictionary<string, object> neutral = NeutralValues(technical);
                            operation.status = client.Write(technical, new[] { operation.result_id }, neutral) ? "rolled_back" : "rollback_failed";
                        }
                        else { operation.status = "preserved_manual_cleanup"; partial = true; }
                    }
                    catch { operation.status = "rollback_failed"; }
                    if (operation.status == "rollback_failed") partial = true;
                }
            }
            change.status = partial ? "rollback_partial" : "rolled_back"; change.rolled_back_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture); Save(change);
            return Summary(change, false);
        }

        public static void ValidatePackage(ChangeSet change)
        {
            if (change == null || change.schema_version != 3 || (change.category != "validated_replica_phase4" && change.category != "production_promotion") || change.policy_version != PolicyVersion) throw new ConsultantException("unsupported_package_schema", "El paquete no corresponde a una réplica Fase 4.");
            if (change.operations == null || change.operations.Count == 0 || change.operations.Count > 250 || change.target_models == null || change.target_models.Count == 0) throw new ConsultantException("invalid_package", "El paquete Fase 4 está incompleto.");
            if (change.integrity_hash != Integrity(change)) throw new ConsultantException("package_integrity_failed", "La parte inmutable del paquete Fase 4 fue modificada.");
            HashSet<string> allowed = new HashSet<string>(new[] { "create_qweb_view", "update_qweb_view", "deactivate_qweb_view", "create_report", "update_report", "deactivate_report", "create_approval_rule", "update_approval_rule", "deactivate_approval_rule", "create_model_access", "update_model_access", "deactivate_model_access", "create_record_rule", "update_record_rule", "deactivate_record_rule" }, StringComparer.Ordinal);
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (ChangeOperation operation in change.operations)
            {
                if (!allowed.Contains(operation.type) || string.IsNullOrWhiteSpace(operation.operation_id) || !ids.Add(operation.operation_id)) throw new ConsultantException("tool_contract_violation", "El paquete Fase 4 contiene una operación no permitida o repetida.");
                StudioService.ValidateModel(operation.model);
            }
        }

        public static void ValidateQwebArchitecture(string xml, string templateName)
        {
            if (string.IsNullOrWhiteSpace(xml) || xml.Length > 250000) throw new ConsultantException("invalid_qweb_xml", "La plantilla QWeb está vacía o supera 250.000 caracteres.");
            XmlDocument document = new XmlDocument(); document.XmlResolver = null;
            try
            {
                XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 250000 };
                using (StringReader input = new StringReader(xml)) using (XmlReader reader = XmlReader.Create(input, settings)) document.Load(reader);
            }
            catch { throw new ConsultantException("invalid_qweb_xml", "La plantilla QWeb no es XML válido."); }
            bool named = false;
            foreach (XmlNode node in document.SelectNodes("//*"))
            {
                string element = node.LocalName.ToLowerInvariant();
                if (element == "script" || element == "iframe" || element == "object" || element == "embed") throw new ConsultantException("unsafe_qweb", "La plantilla QWeb contiene un elemento ejecutable no permitido.");
                if (node.Attributes == null) continue;
                foreach (XmlAttribute attribute in node.Attributes)
                {
                    string name = attribute.Name.ToLowerInvariant(), value = attribute.Value ?? string.Empty;
                    if (name.StartsWith("on", StringComparison.Ordinal) || name == "t-js" || name == "t-raw" || name == "t-debug" || name == "t-log") throw new ConsultantException("unsafe_qweb", "La plantilla QWeb contiene una directiva no permitida.");
                    if ((name == "href" || name == "src") && Regex.IsMatch(value, "^\\s*(?:javascript|data):", RegexOptions.IgnoreCase)) throw new ConsultantException("unsafe_qweb", "La plantilla QWeb contiene una URL insegura.");
                    if (name == "t-name" && value == templateName) named = true;
                }
            }
            if (!named) throw new ConsultantException("qweb_template_name_mismatch", "El XML firmado debe declarar t-name con el template_name exacto.");
        }

        private Manifest Load(string slug)
        {
            string root = project.ReplicaPath(slug);
            Dictionary<string, object> raw = JsonUtil.ReadJsonFile<Dictionary<string, object>>(Path.Combine(root, "manifest.json"));
            if (JsonUtil.GetLong(raw, "schema_version", true) != 2 || JsonUtil.GetLong(raw, "phase", true) != 4) throw new ConsultantException("unsupported_replica_schema", "La Fase 4 exige schema_version 2 y phase 4.");
            EnsureAllowedKeys(raw, new[] { "schema_version", "phase", "name", "description", "source", "dependencies", "references", "qweb_views", "reports", "approvals", "access_controls", "record_rules", "negative_access_tests", "deployment_order", "excluded_artifacts" }, "manifest.json Fase 4");
            Dictionary<string, object> source = Dict(raw, "source", true);
            EnsureAllowedKeys(source, new[] { "environment", "odoo_version", "captured_on", "contains_business_records", "metadata_hashes" }, "source");
            if (JsonUtil.GetBool(source, "contains_business_records", true)) throw new ConsultantException("business_records_forbidden", "La réplica no puede contener registros de negocio.");
            string version = JsonUtil.GetString(source, "odoo_version", false);
            if (!string.IsNullOrWhiteSpace(version) && version != "19") throw new ConsultantException("unsupported_odoo_version", "La réplica Fase 4 solo admite Odoo 19.");
            Manifest manifest = new Manifest { slug = slug, root = root, raw = raw, references = Dict(raw, "references", false) ?? new Dictionary<string, object>(StringComparer.Ordinal), qwebViews = DictList(raw, "qweb_views"), reports = DictList(raw, "reports"), approvals = DictList(raw, "approvals"), accessControls = DictList(raw, "access_controls"), recordRules = DictList(raw, "record_rules"), negativeTests = DictList(raw, "negative_access_tests"), targetModels = new List<string>(), fileHashes = new Dictionary<string, string>(StringComparer.Ordinal) };
            if (manifest.qwebViews.Count + manifest.reports.Count + manifest.approvals.Count + manifest.accessControls.Count + manifest.recordRules.Count == 0) throw new ConsultantException("empty_replica", "La réplica Fase 4 no contiene componentes.");
            ValidateReferences(manifest);
            foreach (Dictionary<string, object> item in manifest.qwebViews) ValidateQweb(manifest, item);
            foreach (Dictionary<string, object> item in manifest.reports) ValidateReport(manifest, item);
            foreach (Dictionary<string, object> item in manifest.approvals) ValidateApproval(manifest, item);
            foreach (Dictionary<string, object> item in manifest.accessControls) ValidateAccess(manifest, item);
            foreach (Dictionary<string, object> item in manifest.recordRules) ValidateRule(manifest, item);
            foreach (Dictionary<string, object> item in manifest.negativeTests) ValidateNegativeTest(manifest, item);
            ValidateKeys(manifest);
            if (manifest.accessControls.Count + manifest.recordRules.Count > 0 && manifest.negativeTests.Count == 0) throw new ConsultantException("negative_access_test_required", "Toda réplica que cambie seguridad debe declarar al menos una prueba negativa de acceso.");
            string[] order = new[] { "qweb_views", "reports", "approvals", "access_controls", "record_rules", "negative_access_tests" };
            List<string> deployment = StringList(raw, "deployment_order");
            if (deployment.Count > 0 && !deployment.SequenceEqual(order)) throw new ConsultantException("invalid_deployment_order", "deployment_order debe respetar qweb_views, reports, approvals, access_controls, record_rules y negative_access_tests.");
            List<string> excluded = StringList(raw, "excluded_artifacts");
            if (excluded.Count == 0 || !excluded.Any(delegate(string x) { return Regex.IsMatch(x, "business|negocio|record", RegexOptions.IgnoreCase); })) throw new ConsultantException("business_exclusion_required", "excluded_artifacts debe excluir explícitamente los registros de negocio.");
            if (manifest.targetModels.Count == 0) throw new ConsultantException("missing_target_model", "La réplica Fase 4 debe declarar al menos un modelo objetivo.");
            manifest.hash = JsonUtil.CanonicalHash(JsonUtil.Dictionary("manifest", raw, "file_hashes", manifest.fileHashes));
            return manifest;
        }

        private void ValidateReferences(Manifest manifest)
        {
            foreach (KeyValuePair<string, object> item in manifest.references)
            {
                if (!RefPattern.IsMatch(item.Key)) throw new ConsultantException("invalid_reference", "Una clave de referencia no es válida.");
                Dictionary<string, object> descriptor = item.Value as Dictionary<string, object>;
                if (descriptor == null) throw new ConsultantException("invalid_reference", "Cada referencia debe ser un objeto tipado.");
                EnsureAllowedKeys(descriptor, new[] { "type", "model", "description" }, "references");
                string type = JsonUtil.GetString(descriptor, "type", true), model = JsonUtil.GetString(descriptor, "model", true);
                if (!ReferenceTypes.Contains(type) || !ReferenceModels.Contains(model)) throw new ConsultantException("invalid_reference", "La referencia usa un tipo o modelo no permitido.");
                if (type == "user_login" && model != "res.users") throw new ConsultantException("invalid_reference", "user_login solo puede resolver res.users.");
            }
        }

        private void ValidateQweb(Manifest manifest, Dictionary<string, object> item)
        {
            EnsureAllowedKeys(item, new[] { "key", "name", "template_name", "mode", "previous_hash", "arch_file", "arch_sha256", "active" }, "qweb_views");
            Require(item, "key", "name", "template_name"); ValidateKey(JsonUtil.GetString(item, "key", true));
            string template = JsonUtil.GetString(item, "template_name", true); if (!TemplatePattern.IsMatch(template)) throw new ConsultantException("invalid_qweb_template", "template_name debe comenzar por odooai.");
            string mode = Mode(item); if (mode != "create") RequirePreviousHash(item);
            if (mode != "deactivate")
            {
                string file = JsonUtil.GetString(item, "arch_file", true), hash = JsonUtil.GetString(item, "arch_sha256", true); ValidateFile(manifest, file, hash);
                ValidateQwebArchitecture(File.ReadAllText(ContainedFile(manifest.root, file), Encoding.UTF8), template);
            }
        }

        private void ValidateReport(Manifest manifest, Dictionary<string, object> item)
        {
            EnsureAllowedKeys(item, new[] { "key", "name", "model", "mode", "previous_hash", "template_key", "report_type", "binding_view_types", "multi", "paperformat_ref", "group_refs", "print_report_name_file", "print_report_name_sha256", "attachment_use", "attachment_file", "attachment_sha256", "domain_file", "domain_sha256" }, "reports");
            Require(item, "key", "name", "model", "template_key"); ValidateKey(JsonUtil.GetString(item, "key", true)); AddModel(manifest, JsonUtil.GetString(item, "model", true));
            if (!ReportTypes.Contains(JsonUtil.GetString(item, "report_type", false) ?? "qweb-pdf")) throw new ConsultantException("invalid_report_type", "report_type debe ser qweb-pdf o qweb-html.");
            string mode = Mode(item); if (mode != "create") RequirePreviousHash(item);
            foreach (string reference in StringList(item, "group_refs")) RequireReference(manifest, reference, "res.groups");
            string paper = JsonUtil.GetString(item, "paperformat_ref", false); if (!string.IsNullOrWhiteSpace(paper)) RequireReference(manifest, paper, "report.paperformat");
            ValidateOptionalFile(manifest, item, "print_report_name_file", "print_report_name_sha256"); ValidateOptionalFile(manifest, item, "attachment_file", "attachment_sha256"); ValidateDomainOptional(manifest, item, "domain_file", "domain_sha256");
        }

        private void ValidateApproval(Manifest manifest, Dictionary<string, object> item)
        {
            EnsureAllowedKeys(item, new[] { "key", "name", "model", "mode", "previous_hash", "active", "method", "action_ref", "message", "approver_refs", "approval_group_ref", "notify_user_refs", "notification_order", "exclusive_user", "domain_file", "domain_sha256" }, "approvals");
            Require(item, "key", "name", "model"); ValidateKey(JsonUtil.GetString(item, "key", true)); AddModel(manifest, JsonUtil.GetString(item, "model", true));
            string mode = Mode(item); if (mode != "create") RequirePreviousHash(item);
            string method = JsonUtil.GetString(item, "method", false), action = JsonUtil.GetString(item, "action_ref", false);
            if (mode != "deactivate" && (string.IsNullOrWhiteSpace(method) == string.IsNullOrWhiteSpace(action))) throw new ConsultantException("approval_target_required", "Una aprobación debe declarar exactamente method o action_ref.");
            if (!string.IsNullOrWhiteSpace(method) && !Regex.IsMatch(method, "^[a-z_][a-z0-9_]{1,100}$")) throw new ConsultantException("invalid_approval_method", "El método del botón no es válido.");
            if (!string.IsNullOrWhiteSpace(action)) RequireReferenceAny(manifest, action, new[] { "ir.actions.actions", "ir.actions.server" });
            foreach (string reference in StringList(item, "approver_refs").Concat(StringList(item, "notify_user_refs"))) RequireReference(manifest, reference, "res.users");
            string group = JsonUtil.GetString(item, "approval_group_ref", false); if (!string.IsNullOrWhiteSpace(group)) RequireReference(manifest, group, "res.groups");
            if (mode != "deactivate" && StringList(item, "approver_refs").Count == 0 && string.IsNullOrWhiteSpace(group)) throw new ConsultantException("approval_approver_required", "La aprobación necesita usuarios o un grupo aprobador.");
            ValidateDomainOptional(manifest, item, "domain_file", "domain_sha256");
        }

        private void ValidateAccess(Manifest manifest, Dictionary<string, object> item)
        {
            EnsureAllowedKeys(item, new[] { "key", "name", "model", "mode", "previous_hash", "active", "group_ref", "global", "perm_read", "perm_write", "perm_create", "perm_unlink" }, "access_controls");
            Require(item, "key", "name", "model"); ValidateKey(JsonUtil.GetString(item, "key", true)); AddModel(manifest, JsonUtil.GetString(item, "model", true));
            string mode = Mode(item); if (mode != "create") RequirePreviousHash(item);
            string group = JsonUtil.GetString(item, "group_ref", false); bool global = JsonUtil.GetBool(item, "global", false);
            if (global == !string.IsNullOrWhiteSpace(group)) throw new ConsultantException("invalid_security_scope", "Una ACL debe declarar group_ref o global:true, pero no ambos.");
            if (!string.IsNullOrWhiteSpace(group)) RequireReference(manifest, group, "res.groups");
            if (mode != "deactivate" && !AnyPermission(item)) throw new ConsultantException("empty_access_control", "La ACL debe conceder al menos un permiso.");
        }

        private void ValidateRule(Manifest manifest, Dictionary<string, object> item)
        {
            EnsureAllowedKeys(item, new[] { "key", "name", "model", "mode", "previous_hash", "active", "group_refs", "global", "domain_file", "domain_sha256", "perm_read", "perm_write", "perm_create", "perm_unlink" }, "record_rules");
            Require(item, "key", "name", "model"); ValidateKey(JsonUtil.GetString(item, "key", true)); AddModel(manifest, JsonUtil.GetString(item, "model", true));
            string mode = Mode(item); if (mode != "create") RequirePreviousHash(item);
            List<string> groups = StringList(item, "group_refs"); bool global = JsonUtil.GetBool(item, "global", false);
            if (global == (groups.Count > 0)) throw new ConsultantException("invalid_security_scope", "Una regla debe declarar group_refs o global:true, pero no ambos.");
            foreach (string reference in groups) RequireReference(manifest, reference, "res.groups");
            if (mode != "deactivate") { if (!AnyPermission(item)) throw new ConsultantException("empty_record_rule", "La regla debe aplicar a al menos una operación."); string file = JsonUtil.GetString(item, "domain_file", true), hash = JsonUtil.GetString(item, "domain_sha256", true); ValidateFile(manifest, file, hash); ReadDomain(manifest, file, hash); }
        }

        private void ValidateNegativeTest(Manifest manifest, Dictionary<string, object> item)
        {
            EnsureAllowedKeys(item, new[] { "key", "model", "operation", "expected_allowed", "probe_domain_file", "probe_domain_sha256", "expected_visible_count" }, "negative_access_tests");
            Require(item, "key", "model", "operation"); ValidateKey(JsonUtil.GetString(item, "key", true)); AddModel(manifest, JsonUtil.GetString(item, "model", true));
            string operation = JsonUtil.GetString(item, "operation", true); if (!Operations.Contains(operation)) throw new ConsultantException("invalid_access_operation", "La operación de prueba no es válida.");
            string file = JsonUtil.GetString(item, "probe_domain_file", false);
            if (!string.IsNullOrWhiteSpace(file)) { if (operation != "read") throw new ConsultantException("invalid_access_probe", "probe_domain solo se admite para lectura."); ValidateFile(manifest, file, JsonUtil.GetString(item, "probe_domain_sha256", true)); ReadDomain(manifest, file, JsonUtil.GetString(item, "probe_domain_sha256", true)); }
            long count = JsonUtil.GetLong(item, "expected_visible_count", false); if (item.ContainsKey("expected_visible_count") && (count < 0 || count > 10)) throw new ConsultantException("invalid_access_probe", "expected_visible_count debe estar entre 0 y 10.");
        }

        private Analysis AnalyzeInternal(OdooClient client, Manifest manifest)
        {
            Analysis result = new Analysis { components = new List<Dictionary<string, object>>(), blocked = new List<string>(), modelIds = new Dictionary<string, long>(StringComparer.Ordinal), approvalContract = JsonUtil.Dictionary("available", false) };
            foreach (string model in manifest.targetModels)
            {
                IList<Dictionary<string, object>> rows = client.SearchRead("ir.model", new object[] { new object[] { "model", "=", model } }, new[] { "id", "model", "name" }, 2, null);
                if (rows.Count != 1) result.blocked.Add("modelo ausente o ambiguo: " + model); else result.modelIds[model] = Id(rows[0]);
            }
            CheckTechnicalAccess(client, manifest, result.blocked);
            if (manifest.approvals.Count > 0) result.approvalContract = DiscoverApprovalContract(client, result.blocked);
            foreach (Dictionary<string, object> item in manifest.qwebViews) AnalyzeOne(client, result, "qweb_view", "ir.ui.view", item, new object[] { new object[] { "key", "=", JsonUtil.GetString(item, "template_name", true) } }, new[] { "id", "name", "key", "type", "arch", "active" });
            foreach (Dictionary<string, object> item in manifest.reports) AnalyzeOne(client, result, "report", "ir.actions.report", item, new object[] { new object[] { "name", "=", JsonUtil.GetString(item, "name", true) }, new object[] { "model", "=", JsonUtil.GetString(item, "model", true) } }, ReportFields());
            foreach (Dictionary<string, object> item in manifest.approvals)
            {
                long modelId; if (!result.modelIds.TryGetValue(JsonUtil.GetString(item, "model", true), out modelId)) continue;
                AnalyzeOne(client, result, "approval", "studio.approval.rule", item, new object[] { new object[] { "name", "=", JsonUtil.GetString(item, "name", true) }, new object[] { "model_id", "=", modelId } }, ApprovalFields());
            }
            foreach (Dictionary<string, object> item in manifest.accessControls)
            {
                long modelId; if (!result.modelIds.TryGetValue(JsonUtil.GetString(item, "model", true), out modelId)) continue;
                AnalyzeOne(client, result, "access_control", "ir.model.access", item, new object[] { new object[] { "name", "=", JsonUtil.GetString(item, "name", true) }, new object[] { "model_id", "=", modelId } }, AccessFields());
            }
            foreach (Dictionary<string, object> item in manifest.recordRules)
            {
                long modelId; if (!result.modelIds.TryGetValue(JsonUtil.GetString(item, "model", true), out modelId)) continue;
                AnalyzeOne(client, result, "record_rule", "ir.rule", item, new object[] { new object[] { "name", "=", JsonUtil.GetString(item, "name", true) }, new object[] { "model_id", "=", modelId } }, RuleFields());
            }
            object descriptor = result.components.Select(delegate(Dictionary<string, object> x) { return JsonUtil.Dictionary("key", x["key"], "kind", x["kind"], "classification", x["classification"], "technical_id", x["technical_id"], "actual_hash", x["actual_hash"]); }).ToArray();
            result.preconditionHash = JsonUtil.CanonicalHash(descriptor);
            result.capabilityHash = JsonUtil.CanonicalHash(JsonUtil.Dictionary("policy", PolicyVersion, "models", result.modelIds.Keys.OrderBy(delegate(string x) { return x; }, StringComparer.Ordinal).ToArray(), "approval_contract", result.approvalContract, "technical_access", TechnicalAccessDescriptor(client)));
            return result;
        }

        private void AnalyzeOne(OdooClient client, Analysis analysis, string kind, string technicalModel, Dictionary<string, object> desired, object[] domain, string[] fields)
        {
            IList<Dictionary<string, object>> rows = client.SearchRead(technicalModel, domain, fields, 2, null);
            string mode = Mode(desired), classification; long id = 0; string hash = null; Dictionary<string, object> snapshot = null;
            if (rows.Count > 1) { classification = "blocked"; analysis.blocked.Add("componente ambiguo: " + kind + ":" + JsonUtil.GetString(desired, "key", true)); }
            else if (rows.Count == 0) { classification = mode == "create" ? "absent" : "blocked"; if (classification == "blocked") analysis.blocked.Add("componente a modificar ausente: " + kind + ":" + JsonUtil.GetString(desired, "key", true)); }
            else
            {
                id = Id(rows[0]); snapshot = SafeSnapshot(rows[0]); hash = JsonUtil.CanonicalHash(snapshot);
                if (mode == "create") { classification = DesiredMatches(kind, desired, snapshot) && IsCustomOwned(client, technicalModel, id) ? "compatible" : "blocked"; if (classification == "blocked") analysis.blocked.Add("colisión incompatible: " + kind + ":" + JsonUtil.GetString(desired, "key", true)); }
                else if (!IsCustomOwned(client, technicalModel, id)) { classification = "blocked"; analysis.blocked.Add("artefacto estándar no modificable: " + kind + ":" + JsonUtil.GetString(desired, "key", true)); }
                else if (JsonUtil.GetString(desired, "previous_hash", true) != hash) { classification = "blocked"; analysis.blocked.Add("previous_hash no coincide: " + kind + ":" + JsonUtil.GetString(desired, "key", true)); }
                else classification = "modifiable";
            }
            analysis.components.Add(JsonUtil.Dictionary("key", kind + ":" + JsonUtil.GetString(desired, "key", true), "kind", kind, "mode", mode, "classification", classification, "technical_model", technicalModel, "technical_id", id, "actual_hash", hash, "snapshot", snapshot));
        }

        private List<ChangeOperation> Compile(Manifest manifest, Analysis analysis)
        {
            Dictionary<string, Dictionary<string, object>> components = analysis.components.ToDictionary(delegate(Dictionary<string, object> x) { return Convert.ToString(x["key"], CultureInfo.InvariantCulture); }, StringComparer.Ordinal);
            List<ChangeOperation> result = new List<ChangeOperation>();
            foreach (Dictionary<string, object> item in manifest.qwebViews) result.Add(CompileOne("qweb_view", item, components));
            foreach (Dictionary<string, object> item in manifest.reports) result.Add(CompileOne("report", item, components));
            foreach (Dictionary<string, object> item in manifest.approvals) result.Add(CompileOne("approval", item, components));
            foreach (Dictionary<string, object> item in manifest.accessControls) result.Add(CompileOne("access_control", item, components));
            foreach (Dictionary<string, object> item in manifest.recordRules) result.Add(CompileOne("record_rule", item, components));
            return result;
        }

        private ChangeOperation CompileOne(string kind, Dictionary<string, object> item, Dictionary<string, Dictionary<string, object>> components)
        {
            Dictionary<string, object> component = components[kind + ":" + JsonUtil.GetString(item, "key", true)];
            string mode = Mode(item), prefix = mode == "create" ? "create_" : mode == "update" ? "update_" : "deactivate_";
            string suffix = kind == "qweb_view" ? "qweb_view" : kind == "report" ? "report" : kind == "approval" ? "approval_rule" : kind == "access_control" ? "model_access" : "record_rule";
            Dictionary<string, object> parameters = item.Where(delegate(KeyValuePair<string, object> x) { return x.Key != "previous_hash" && x.Key != "mode"; }).ToDictionary(delegate(KeyValuePair<string, object> x) { return x.Key; }, delegate(KeyValuePair<string, object> x) { return x.Value; }, StringComparer.Ordinal);
            parameters["component_key"] = component["key"];
            Dictionary<string, object> snapshot = component["snapshot"] as Dictionary<string, object>;
            if (snapshot != null) parameters["previous_snapshot"] = ProtectedSnapshot.Protect(snapshot);
            long id = Convert.ToInt64(component["technical_id"], CultureInfo.InvariantCulture);
            return new ChangeOperation { operation_id = "op-" + Guid.NewGuid().ToString("N"), type = prefix + suffix, model = JsonUtil.GetString(item, "model", false) ?? "ir.ui.view", parameters = parameters, result_id = id, status = Convert.ToString(component["classification"], CultureInfo.InvariantCulture) == "compatible" ? "reused" : "pending" };
        }

        private void ApplyOperation(OdooClient client, Manifest manifest, ChangeSet change, ChangeOperation operation, Dictionary<string, long> refs)
        {
            string technical = TechnicalModel(operation.type);
            Dictionary<string, object> values = BuildValues(client, manifest, change, operation, refs);
            if (!operation.type.StartsWith("deactivate_", StringComparison.Ordinal)) operation.expected_relations_hash = ExpectedRelationsHash(technical, values);
            if (operation.type.StartsWith("deactivate_", StringComparison.Ordinal))
            {
                if (operation.result_id <= 0 || !client.Write(technical, new[] { operation.result_id }, values)) throw new ConsultantException("metadata_write_failed", "Odoo no confirmó la desactivación de " + technical + ".");
                operation.status = "applied"; return;
            }
            if (operation.status == "reused")
            {
                if (!VerifyValues(client, technical, operation.result_id, values)) throw new ConsultantException("reused_component_mismatch", "Un componente reutilizado no coincide después de resolver referencias.");
                return;
            }
            if (operation.type.StartsWith("create_", StringComparison.Ordinal) && operation.result_id <= 0)
            {
                operation.result_id = client.Create(technical, values); operation.status = "applied";
            }
            else
            {
                if (operation.result_id <= 0 || !client.Write(technical, new[] { operation.result_id }, values)) throw new ConsultantException("metadata_write_failed", "Odoo no confirmó la actualización de " + technical + ".");
                operation.status = "applied";
            }
            if (!VerifyValues(client, technical, operation.result_id, values)) throw new ConsultantException("metadata_verification_failed", "Odoo no devolvió la configuración esperada para " + technical + ".");
        }

        private Dictionary<string, object> BuildValues(OdooClient client, Manifest manifest, ChangeSet change, ChangeOperation operation, Dictionary<string, long> refs)
        {
            Dictionary<string, object> p = operation.parameters;
            if (operation.type == "deactivate_qweb_view" || operation.type == "deactivate_approval_rule" || operation.type == "deactivate_model_access" || operation.type == "deactivate_record_rule") return JsonUtil.Dictionary("active", false);
            if (operation.type == "deactivate_report") return JsonUtil.Dictionary("binding_model_id", false, "binding_view_types", string.Empty);
            long modelId = operation.model == "ir.ui.view" ? 0 : ResolveModelId(client, operation.model);
            if (operation.type.Contains("qweb_view"))
            {
                string file = JsonUtil.GetString(p, "arch_file", true); string xml = ReadSigned(manifest, file, JsonUtil.GetString(p, "arch_sha256", true));
                ValidateQwebArchitecture(xml, JsonUtil.GetString(p, "template_name", true));
                return JsonUtil.Dictionary("name", JsonUtil.GetString(p, "name", true), "key", JsonUtil.GetString(p, "template_name", true), "type", "qweb", "arch", JsonUtil.XmlNumericEntities(xml), "active", JsonUtil.GetBool(p, "active", true));
            }
            if (operation.type.Contains("report"))
            {
                string templateKey = JsonUtil.GetString(p, "template_key", true);
                Dictionary<string, object> template = manifest.qwebViews.First(delegate(Dictionary<string, object> x) { return JsonUtil.GetString(x, "key", true) == templateKey; });
                string templateName = JsonUtil.GetString(template, "template_name", true);
                Dictionary<string, object> values = JsonUtil.Dictionary("name", JsonUtil.GetString(p, "name", true), "model", operation.model, "report_type", JsonUtil.GetString(p, "report_type", false) ?? "qweb-pdf", "report_name", templateName, "report_file", templateName, "binding_model_id", modelId, "binding_type", "report", "binding_view_types", JsonUtil.GetString(p, "binding_view_types", false) ?? "list,form", "multi", JsonUtil.GetBool(p, "multi", false), "attachment_use", JsonUtil.GetBool(p, "attachment_use", false));
                AddReference(values, "paperformat_id", JsonUtil.GetString(p, "paperformat_ref", false), refs);
                values["group_ids"] = Many2Many(ResolveMany(StringList(p, "group_refs"), refs));
                AddSignedText(manifest, p, values, "print_report_name_file", "print_report_name_sha256", "print_report_name"); AddSignedText(manifest, p, values, "attachment_file", "attachment_sha256", "attachment"); AddSignedDomain(manifest, p, values, "domain_file", "domain_sha256", "domain");
                return values;
            }
            if (operation.type.Contains("approval_rule"))
            {
                Dictionary<string, object> values = JsonUtil.Dictionary("name", JsonUtil.GetString(p, "name", true), "model_id", modelId, "active", JsonUtil.GetBool(p, "active", true), "message", JsonUtil.GetString(p, "message", false) ?? string.Empty, "notification_order", JsonUtil.GetString(p, "notification_order", false) ?? "1", "exclusive_user", JsonUtil.GetBool(p, "exclusive_user", false));
                string method = JsonUtil.GetString(p, "method", false), action = JsonUtil.GetString(p, "action_ref", false);
                if (!string.IsNullOrWhiteSpace(method)) { values["method"] = method; values["action_id"] = false; } else { values["method"] = false; values["action_id"] = refs[action]; }
                values["approver_ids"] = Many2Many(ResolveMany(StringList(p, "approver_refs"), refs)); values["users_to_notify"] = Many2Many(ResolveMany(StringList(p, "notify_user_refs"), refs));
                AddReference(values, "approval_group_id", JsonUtil.GetString(p, "approval_group_ref", false), refs); AddSignedDomain(manifest, p, values, "domain_file", "domain_sha256", "domain");
                return values;
            }
            if (operation.type.Contains("model_access"))
            {
                Dictionary<string, object> values = PermissionValues(p); values["name"] = JsonUtil.GetString(p, "name", true); values["model_id"] = modelId; values["active"] = JsonUtil.GetBool(p, "active", true);
                string group = JsonUtil.GetString(p, "group_ref", false); values["group_id"] = string.IsNullOrWhiteSpace(group) ? (object)false : refs[group]; return values;
            }
            Dictionary<string, object> rule = PermissionValues(p); rule["name"] = JsonUtil.GetString(p, "name", true); rule["model_id"] = modelId; rule["active"] = JsonUtil.GetBool(p, "active", true); rule["groups"] = Many2Many(ResolveMany(StringList(p, "group_refs"), refs)); AddSignedDomain(manifest, p, rule, "domain_file", "domain_sha256", "domain_force"); return rule;
        }

        private Dictionary<string, long> ResolveReferences(OdooClient client, Manifest manifest, IDictionary<string, object> supplied)
        {
            supplied = supplied ?? new Dictionary<string, object>(StringComparer.Ordinal);
            if (supplied.Keys.Any(delegate(string key) { return !manifest.references.ContainsKey(key); })) throw new ConsultantException("unexpected_reference", "Se recibió una referencia no declarada.");
            Dictionary<string, long> result = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> item in manifest.references)
            {
                if (!supplied.ContainsKey(item.Key)) throw new ConsultantException("reference_required", "Falta una referencia requerida por la réplica: " + item.Key + ".");
                string value = Convert.ToString(supplied[item.Key], CultureInfo.InvariantCulture); Dictionary<string, object> descriptor = item.Value as Dictionary<string, object>;
                string type = JsonUtil.GetString(descriptor, "type", true), model = JsonUtil.GetString(descriptor, "model", true);
                if (type == "xml_id") { if (!XmlIdPattern.IsMatch(value ?? string.Empty)) throw new ConsultantException("invalid_reference_value", "Una referencia XML ID no es válida."); result[item.Key] = ResolveXmlId(client, value, model); }
                else
                {
                    if (string.IsNullOrWhiteSpace(value) || value.Length > 254) throw new ConsultantException("invalid_reference_value", "El login de referencia no es válido.");
                    IList<Dictionary<string, object>> rows = client.SearchRead("res.users", new object[] { new object[] { "login", "=", value } }, new[] { "id" }, 2, null);
                    if (rows.Count != 1) throw new ConsultantException("reference_not_found", "No se resolvió un usuario de referencia del destino."); result[item.Key] = Id(rows[0]);
                }
            }
            return result;
        }

        private static Dictionary<string, object> DiscoverApprovalContract(OdooClient client, List<string> blocked)
        {
            try
            {
                Dictionary<string, object> fields = client.FieldsGet("studio.approval.rule", new[] { "type", "relation", "required", "readonly", "store" });
                string[] required = new[] { "active", "model_id", "method", "action_id", "name", "message", "approver_ids", "approval_group_id", "users_to_notify", "notification_order", "exclusive_user", "domain" };
                List<string> missing = required.Where(delegate(string x) { return !fields.ContainsKey(x); }).ToList();
                Dictionary<string, string[]> expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    { "active", new[] { "boolean", null } }, { "model_id", new[] { "many2one", "ir.model" } },
                    { "method", new[] { "char", null } }, { "action_id", new[] { "many2one", "ir.actions.actions" } },
                    { "name", new[] { "char", null } }, { "message", new[] { "text", null } },
                    { "approver_ids", new[] { "many2many", "res.users" } }, { "approval_group_id", new[] { "many2one", "res.groups" } },
                    { "users_to_notify", new[] { "many2many", "res.users" } }, { "notification_order", new[] { "selection", null } },
                    { "exclusive_user", new[] { "boolean", null } }, { "domain", new[] { "char", null } }
                };
                List<string> incompatible = new List<string>();
                foreach (KeyValuePair<string, string[]> contract in expected)
                {
                    Dictionary<string, object> descriptor = fields.ContainsKey(contract.Key) ? fields[contract.Key] as Dictionary<string, object> : null;
                    if (descriptor == null) continue;
                    string type = JsonUtil.GetString(descriptor, "type", false), relation = JsonUtil.GetString(descriptor, "relation", false);
                    bool typeMatches = type == contract.Value[0] || (contract.Key == "message" && (type == "char" || type == "text"));
                    if (!typeMatches || (contract.Value[1] != null && relation != contract.Value[1])) incompatible.Add(contract.Key);
                }

                bool create = SafeAccess(client, "studio.approval.rule", "create"), write = SafeAccess(client, "studio.approval.rule", "write");
                if (missing.Count > 0 || incompatible.Count > 0 || !create || !write) blocked.Add("contrato Enterprise de aprobaciones no disponible, incompatible o sin permisos");
                return JsonUtil.Dictionary("available", missing.Count == 0 && incompatible.Count == 0, "model", "studio.approval.rule", "field_hash", JsonUtil.CanonicalHash(fields), "missing_fields", missing.ToArray(), "incompatible_fields", incompatible.ToArray(), "create", create, "write", write);
            }
            catch { blocked.Add("modelo Enterprise studio.approval.rule no disponible"); return JsonUtil.Dictionary("available", false, "model", "studio.approval.rule"); }
        }

        private static void CheckTechnicalAccess(OdooClient client, Manifest manifest, List<string> blocked)
        {
            List<string> models = new List<string>();
            if (manifest.qwebViews.Count > 0) models.Add("ir.ui.view"); if (manifest.reports.Count > 0) models.Add("ir.actions.report"); if (manifest.approvals.Count > 0) models.Add("studio.approval.rule"); if (manifest.accessControls.Count > 0) models.Add("ir.model.access"); if (manifest.recordRules.Count > 0) models.Add("ir.rule");
            foreach (string model in models.Distinct(StringComparer.Ordinal)) if (!SafeAccess(client, model, "read") || !SafeAccess(client, model, "create") || !SafeAccess(client, model, "write")) blocked.Add("permisos técnicos insuficientes: " + model);
        }

        private static Dictionary<string, object> TechnicalAccessDescriptor(OdooClient client)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (string model in new[] { "ir.ui.view", "ir.actions.report", "studio.approval.rule", "ir.model.access", "ir.rule" }) result[model] = JsonUtil.Dictionary("read", SafeAccess(client, model, "read"), "create", SafeAccess(client, model, "create"), "write", SafeAccess(client, model, "write"));
            return result;
        }

        private bool VerifyOperation(OdooClient client, Manifest manifest, ChangeOperation operation, string packageStatus)
        {
            if (operation.result_id <= 0) return false;
            string model = TechnicalModel(operation.type); IList<Dictionary<string, object>> rows = client.Read(model, new[] { operation.result_id }, VerificationFields(model)); if (rows.Count != 1) return false;
            if (packageStatus == "rolled_back" || packageStatus == "rollback_partial")
            {
                if (model == "ir.actions.report") return RelationId(rows[0]["binding_model_id"]) == 0;
                return rows[0].ContainsKey("active") && !Convert.ToBoolean(rows[0]["active"], CultureInfo.InvariantCulture);
            }
            Dictionary<string, object> row = rows[0]; Dictionary<string, object> p = operation.parameters;
            if (model == "ir.ui.view")
            {
                if (Text(row, "name") != JsonUtil.GetString(p, "name", true) || Text(row, "key") != JsonUtil.GetString(p, "template_name", true) || Text(row, "type") != "qweb") return false;
                string file = JsonUtil.GetString(p, "arch_file", false);
                if (!string.IsNullOrWhiteSpace(file) && NormalizeXml(Text(row, "arch")) != NormalizeXml(ReadSigned(manifest, file, JsonUtil.GetString(p, "arch_sha256", true)))) return false;
            }
            else if (model == "ir.actions.report")
            {
                if (Text(row, "name") != JsonUtil.GetString(p, "name", true) || Text(row, "model") != operation.model || Text(row, "report_type") != (JsonUtil.GetString(p, "report_type", false) ?? "qweb-pdf")) return false;
                Dictionary<string, object> template = manifest.qwebViews.First(delegate(Dictionary<string, object> x) { return JsonUtil.GetString(x, "key", true) == JsonUtil.GetString(p, "template_key", true); });
                if (Text(row, "report_name") != JsonUtil.GetString(template, "template_name", true)) return false;
            }
            else
            {
                if (Text(row, "name") != JsonUtil.GetString(p, "name", true)) return false;
                foreach (string permission in new[] { "perm_read", "perm_write", "perm_create", "perm_unlink" }) if (p.ContainsKey(permission) && row.ContainsKey(permission) && Convert.ToBoolean(row[permission], CultureInfo.InvariantCulture) != JsonUtil.GetBool(p, permission, false)) return false;
                if (model == "studio.approval.rule" && !string.IsNullOrWhiteSpace(JsonUtil.GetString(p, "method", false)) && Text(row, "method") != JsonUtil.GetString(p, "method", true)) return false;
            }
            if (!string.IsNullOrWhiteSpace(operation.expected_relations_hash) && operation.expected_relations_hash != ActualRelationsHash(model, row)) return false;
            return true;
        }

        private static bool VerifyValues(OdooClient client, string model, long id, Dictionary<string, object> values)
        {
            IList<Dictionary<string, object>> rows = client.Read(model, new[] { id }, VerificationFields(model)); if (rows.Count != 1) return false;
            Dictionary<string, object> row = rows[0];
            foreach (KeyValuePair<string, object> item in values)
            {
                if (item.Key == "group_ids" || item.Key == "groups" || item.Key == "approver_ids" || item.Key == "users_to_notify") { if (!Many2ManyMatches(item.Value, row.ContainsKey(item.Key) ? row[item.Key] : null)) return false; continue; }
                if (!row.ContainsKey(item.Key)) continue;
                if (item.Key == "arch") { if (NormalizeXml(Convert.ToString(item.Value, CultureInfo.InvariantCulture)) != NormalizeXml(Convert.ToString(row[item.Key], CultureInfo.InvariantCulture))) return false; }
                else if (item.Value is bool && row[item.Key] is bool && (bool)item.Value != (bool)row[item.Key]) return false;
                else if (item.Value is long || item.Value is int) { if (Convert.ToInt64(item.Value, CultureInfo.InvariantCulture) != RelationOrLong(row[item.Key])) return false; }
                else if (!(item.Value is IEnumerable) && Convert.ToString(item.Value, CultureInfo.InvariantCulture) != Convert.ToString(row[item.Key] is bool ? null : row[item.Key], CultureInfo.InvariantCulture)) return false;
            }
            return true;
        }

        private static bool DesiredMatches(string kind, Dictionary<string, object> desired, Dictionary<string, object> actual)
        {
            if (kind == "qweb_view") return Text(actual, "name") == JsonUtil.GetString(desired, "name", true) && Text(actual, "key") == JsonUtil.GetString(desired, "template_name", true) && Text(actual, "type") == "qweb";
            if (Text(actual, "name") != JsonUtil.GetString(desired, "name", true)) return false;
            if (kind == "report") return Text(actual, "model") == JsonUtil.GetString(desired, "model", true) && Text(actual, "report_type") == (JsonUtil.GetString(desired, "report_type", false) ?? "qweb-pdf");
            foreach (string permission in new[] { "perm_read", "perm_write", "perm_create", "perm_unlink" }) if (desired.ContainsKey(permission) && Convert.ToBoolean(actual[permission], CultureInfo.InvariantCulture) != JsonUtil.GetBool(desired, permission, false)) return false;
            return true;
        }

        private void ValidateResume(OdooClient client, Manifest manifest, ChangeSet change)
        {
            foreach (ChangeOperation operation in change.operations.Where(delegate(ChangeOperation x) { return x.status == "applied" || x.status == "reused"; })) if (!VerifyOperation(client, manifest, operation, "failed_partial")) throw new ConsultantException("precondition_changed", "Un componente ya aplicado cambió antes del reintento.");
        }

        private void MarkNegativeTest(ChangeSet change, string key)
        {
            string token = "access-test:" + JsonUtil.Sha256(key);
            if (change.warnings == null) change.warnings = new List<string>();
            if (!change.warnings.Contains(token)) change.warnings.Add(token);
            Manifest manifest = Load(change.replica_slug);
            bool all = manifest.negativeTests.All(delegate(Dictionary<string, object> x) { return change.warnings.Contains("access-test:" + JsonUtil.Sha256(JsonUtil.GetString(x, "key", true))); });
            change.uat_status = all ? "negative_access_tests_passed" : "negative_access_tests_partial";
            if (all && change.status == "applied") change.status = "verified";
            Save(change);
        }

        internal ChangeSet PreparePromotion(string slug, string backupReference, IDictionary<string, object> references)
        {
            Manifest manifest = Load(slug);
            ConnectionProfile profile = project.GetProfile("production", true);
            using (OdooClient client = Open("production"))
            {
                Analysis analysis = AnalyzeInternal(client, manifest);
                if (analysis.blocked.Count > 0) throw new ConsultantException("replica_blocked", "La réplica Fase 4 tiene componentes bloqueados en producción: " + string.Join("; ", analysis.blocked.ToArray()));
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
                        "manifest_schema", 2, "phase", 4, "required_references", PublicReferences(manifest),
                        "negative_access_test_keys", manifest.negativeTests.Select(delegate(Dictionary<string, object> x) { return JsonUtil.GetString(x, "key", true); }).ToArray()),
                    created_at = now.ToString("o", CultureInfo.InvariantCulture),
                    expires_at = now.AddHours(2).ToString("o", CultureInfo.InvariantCulture),
                    precondition_hash = analysis.preconditionHash, capability_hash = analysis.capabilityHash,
                    profile_fingerprint = profile.credential_fingerprint, requested_by = Environment.UserName,
                    policy_version = PolicyVersion, objective = "Promover a producción de forma segura la réplica Fase 4 " + manifest.slug + ".",
                    contract_hash = manifest.hash,
                    impact = "Crea o actualiza reportes, aprobaciones y reglas de seguridad en producción.",
                    rollback_strategy = "Desactiva artefactos creados; no elimina registros ni metadatos.",
                    preconditions = new List<Dictionary<string, object>> { JsonUtil.Dictionary("type", "destination_hash", "expected", analysis.preconditionHash), JsonUtil.Dictionary("type", "capabilities_hash", "expected", analysis.capabilityHash) },
                    validations = new List<Dictionary<string, object>> { JsonUtil.Dictionary("type", "schema_2_phase_4", "required", true), JsonUtil.Dictionary("type", "files_sha256_reread", "required", true), JsonUtil.Dictionary("type", "previous_hash_for_updates", "required", true) },
                    operations = Compile(manifest, analysis),
                    warnings = new List<string> { "Producción: Se requiere confirmación reforzada con respaldo verificado.", "Respaldo registrado: " + backupReference, "Ventana de vigencia reducida: 2 horas.", "Confirmación exacta requerida: PROMOVER-PRODUCCION " + id + " RESPALDO-CONFIRMADO" }
                };
                change.integrity_hash = Integrity(change);
                ValidatePackage(change);
                JsonUtil.WriteJsonFile(project.ChangeSetPath(id), change);
                AuditLog.Append(project, "production", "promocion_preparar_paquete", id, "success", "Paquete de promoción Fase 4 preparado; operaciones=" + change.operations.Count.ToString(CultureInfo.InvariantCulture) + "; respaldo=" + backupReference + ".", 0);
                return change;
            }
        }

        private object ProductionRunbook(Manifest manifest)
        {
            string id = "run-" + Guid.NewGuid().ToString("N"); StringBuilder text = new StringBuilder();
            text.AppendLine("# Promoción manual de réplica Fase 4 " + manifest.slug).AppendLine().AppendLine("- Entorno: `production`").AppendLine("- Hash: `" + manifest.hash + "`").AppendLine("- Escritura automática: bloqueada").AppendLine().AppendLine("## Componentes").AppendLine().AppendLine("- Plantillas QWeb: " + manifest.qwebViews.Count).AppendLine("- Reportes: " + manifest.reports.Count).AppendLine("- Aprobaciones: " + manifest.approvals.Count).AppendLine("- ACL: " + manifest.accessControls.Count).AppendLine("- Reglas: " + manifest.recordRules.Count).AppendLine("- Pruebas negativas: " + manifest.negativeTests.Count);
            File.WriteAllText(project.RunbookPath(id), text.ToString(), new UTF8Encoding(false)); AuditLog.Append(project, "production", "replica_preparar_paquete", id, "success", "Runbook Fase 4 generado sin red.", 0);
            return JsonUtil.Dictionary("environment", "production", "replica_slug", manifest.slug, "phase", 4, "execution", "manual_only", "runbook_id", id);
        }

        private OdooClient Open(string environment) { return project.OpenClient(environment, 30000); }
        private void Save(ChangeSet change) { JsonUtil.WriteJsonFile(project.ChangeSetPath(change.id), change); }
        private ChangeSet LoadPackage(string id) { return JsonUtil.ReadJsonFile<ChangeSet>(project.ChangeSetPath(id)); }
        private static void ValidateConfirmation(ChangeSet change, string confirmation, string verb) { DateTimeOffset expires; if (!DateTimeOffset.TryParse(change.expires_at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expires) || DateTimeOffset.UtcNow > expires) throw new ConsultantException("confirmation_expired", "La confirmación venció. Prepara un paquete nuevo."); string expected = string.Equals(change.category, "production_promotion", StringComparison.Ordinal) ? "PROMOVER-PRODUCCION " + change.id + " RESPALDO-CONFIRMADO" : verb + " " + change.id; if (confirmation != expected) throw new ConsultantException("confirmation_mismatch", "La confirmación debe ser exactamente " + expected + "."); }
        private static string SafeError(Exception error) { return Redactor.SafeError(error); }
        private static bool SameInstance(ConnectionProfile a, ConnectionProfile b) { return a != null && b != null && string.Equals(a.base_url, b.base_url, StringComparison.OrdinalIgnoreCase) && string.Equals(a.database ?? string.Empty, b.database ?? string.Empty, StringComparison.Ordinal); }
        private static bool SafeAccess(OdooClient client, string model, string operation) { try { return client.CheckAccessRights(model, operation); } catch { return false; } }
        private static long ResolveModelId(OdooClient client, string model) { IList<Dictionary<string, object>> rows = client.SearchRead("ir.model", new object[] { new object[] { "model", "=", model } }, new[] { "id" }, 2, null); if (rows.Count != 1) throw new ConsultantException("model_not_found", "No se resolvió el modelo objetivo."); return Id(rows[0]); }
        private static long ResolveXmlId(OdooClient client, string xmlid, string expectedModel) { int separator = xmlid.IndexOf('.'); string module = xmlid.Substring(0, separator), name = xmlid.Substring(separator + 1); IList<Dictionary<string, object>> rows = client.SearchRead("ir.model.data", new object[] { new object[] { "module", "=", module }, new object[] { "name", "=", name }, new object[] { "model", "=", expectedModel } }, new[] { "id", "res_id" }, 2, null); if (rows.Count != 1) throw new ConsultantException("reference_not_found", "No se resolvió una referencia XML ID del destino."); return Convert.ToInt64(rows[0]["res_id"], CultureInfo.InvariantCulture); }
        private static bool IsCustomOwned(OdooClient client, string model, long id) { IList<Dictionary<string, object>> rows = client.SearchRead("ir.model.data", new object[] { new object[] { "model", "=", model }, new object[] { "res_id", "=", id } }, new[] { "id", "module" }, 10, null); if (rows.Count == 0) return true; return rows.All(delegate(Dictionary<string, object> row) { string module = Convert.ToString(row["module"], CultureInfo.InvariantCulture); return module == "studio_customization" || module == "__export__"; }); }
        private static long Id(Dictionary<string, object> row) { return Convert.ToInt64(row["id"], CultureInfo.InvariantCulture); }
        private static long RelationId(object value) { IEnumerable sequence = value as IEnumerable; if (sequence != null && !(value is string)) foreach (object item in sequence) { long parsed; if (long.TryParse(Convert.ToString(item, CultureInfo.InvariantCulture), out parsed)) return parsed; } long direct; return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out direct) ? direct : 0; }
        private static long RelationOrLong(object value) { return RelationId(value); }
        private static string Text(IDictionary<string, object> source, string key) { object value; return source != null && source.TryGetValue(key, out value) && value != null && !(value is bool) ? Convert.ToString(value, CultureInfo.InvariantCulture) : string.Empty; }
        private static Dictionary<string, object> SafeSnapshot(Dictionary<string, object> row) { return row.Where(delegate(KeyValuePair<string, object> x) { return x.Key != "id" && x.Key != "create_uid" && x.Key != "create_date" && x.Key != "write_uid" && x.Key != "write_date"; }).ToDictionary(delegate(KeyValuePair<string, object> x) { return x.Key; }, delegate(KeyValuePair<string, object> x) { return x.Value; }, StringComparer.Ordinal); }
        private static Dictionary<string, object> PermissionValues(Dictionary<string, object> p) { return JsonUtil.Dictionary("perm_read", JsonUtil.GetBool(p, "perm_read", false), "perm_write", JsonUtil.GetBool(p, "perm_write", false), "perm_create", JsonUtil.GetBool(p, "perm_create", false), "perm_unlink", JsonUtil.GetBool(p, "perm_unlink", false)); }
        private static bool AnyPermission(Dictionary<string, object> p) { return JsonUtil.GetBool(p, "perm_read", false) || JsonUtil.GetBool(p, "perm_write", false) || JsonUtil.GetBool(p, "perm_create", false) || JsonUtil.GetBool(p, "perm_unlink", false); }
        private static object Many2Many(IEnumerable<long> ids) { return new object[] { new object[] { 6, 0, ids.Distinct().ToArray() } }; }
        private static bool Many2ManyMatches(object expectedCommand, object actual) { return JsonUtil.CanonicalHash(IdsFromCommand(expectedCommand)) == JsonUtil.CanonicalHash(IdsFromRelation(actual)); }
        private static long[] IdsFromCommand(object value) { IEnumerable outer = value as IEnumerable; if (outer != null) foreach (object commandRaw in outer) { IEnumerable command = commandRaw as IEnumerable; if (command == null) continue; List<object> parts = command.Cast<object>().ToList(); if (parts.Count >= 3 && Convert.ToInt64(parts[0], CultureInfo.InvariantCulture) == 6) return IdsFromRelation(parts[2]); } return new long[0]; }
        private static long[] IdsFromRelation(object value) { List<long> ids = new List<long>(); IEnumerable sequence = value as IEnumerable; if (sequence != null && !(value is string)) foreach (object item in sequence) { long parsed; if (item is IEnumerable && !(item is string)) { foreach (object nested in (IEnumerable)item) { if (long.TryParse(Convert.ToString(nested, CultureInfo.InvariantCulture), out parsed)) { ids.Add(parsed); break; } } } else if (long.TryParse(Convert.ToString(item, CultureInfo.InvariantCulture), out parsed)) ids.Add(parsed); } return ids.Distinct().OrderBy(delegate(long x) { return x; }).ToArray(); }
        private static string ExpectedRelationsHash(string model, Dictionary<string, object> values) { Dictionary<string, object> relations = new Dictionary<string, object>(StringComparer.Ordinal); foreach (string field in RelationFields(model)) if (values.ContainsKey(field)) relations[field] = field == "group_ids" || field == "groups" || field == "approver_ids" || field == "users_to_notify" ? (object)IdsFromCommand(values[field]) : RelationId(values[field]); return relations.Count == 0 ? null : JsonUtil.CanonicalHash(relations); }
        private static string ActualRelationsHash(string model, Dictionary<string, object> row) { Dictionary<string, object> relations = new Dictionary<string, object>(StringComparer.Ordinal); foreach (string field in RelationFields(model)) if (row.ContainsKey(field)) relations[field] = field == "group_ids" || field == "groups" || field == "approver_ids" || field == "users_to_notify" ? (object)IdsFromRelation(row[field]) : RelationId(row[field]); return relations.Count == 0 ? null : JsonUtil.CanonicalHash(relations); }
        private static string[] RelationFields(string model) { if (model == "ir.actions.report") return new[] { "binding_model_id", "paperformat_id", "group_ids" }; if (model == "studio.approval.rule") return new[] { "model_id", "action_id", "approver_ids", "approval_group_id", "users_to_notify" }; if (model == "ir.model.access") return new[] { "model_id", "group_id" }; if (model == "ir.rule") return new[] { "model_id", "groups" }; return new string[0]; }
        private static List<long> ResolveMany(IEnumerable<string> keys, Dictionary<string, long> refs) { return keys.Select(delegate(string x) { return refs[x]; }).ToList(); }
        private static void AddReference(Dictionary<string, object> values, string field, string key, Dictionary<string, long> refs) { values[field] = string.IsNullOrWhiteSpace(key) ? (object)false : refs[key]; }
        private static void AddSignedText(Manifest manifest, Dictionary<string, object> source, Dictionary<string, object> values, string fileKey, string hashKey, string target) { string file = JsonUtil.GetString(source, fileKey, false); if (!string.IsNullOrWhiteSpace(file)) values[target] = ReadSigned(manifest, file, JsonUtil.GetString(source, hashKey, true)); }
        private static void AddSignedDomain(Manifest manifest, Dictionary<string, object> source, Dictionary<string, object> values, string fileKey, string hashKey, string target) { string file = JsonUtil.GetString(source, fileKey, false); if (!string.IsNullOrWhiteSpace(file)) values[target] = DomainLiteral(ReadDomain(manifest, file, JsonUtil.GetString(source, hashKey, true))); }
        private static string ReadSigned(Manifest manifest, string file, string hash) { string path = ContainedFile(manifest.root, file); if (FileHash(path) != hash) throw new ConsultantException("replica_file_hash_mismatch", "Un archivo firmado cambió antes de aplicar."); return File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n").Replace("\r", "\n"); }
        private static object[] ReadDomain(Manifest manifest, string file, string hash) { string text = ReadSigned(manifest, file, hash); object[] domain; try { domain = JsonUtil.Deserialize<object[]>(text); } catch { throw new ConsultantException("invalid_domain_file", "El archivo de dominio debe contener una lista JSON."); } if (domain == null || domain.Length > 30) throw new ConsultantException("invalid_domain_file", "El dominio está vacío o excede 30 términos."); return domain; }
        private static string DomainLiteral(object value) { if (value == null) return "False"; if (value is bool) return (bool)value ? "True" : "False"; if (value is string) return "'" + ((string)value).Replace("\\", "\\\\").Replace("'", "\\'") + "'"; IDictionary dictionary = value as IDictionary; if (dictionary != null) throw new ConsultantException("invalid_domain_file", "El dominio no admite objetos JSON."); IEnumerable sequence = value as IEnumerable; if (sequence != null) { List<string> parts = new List<string>(); foreach (object item in sequence) parts.Add(DomainLiteral(item)); return "[" + string.Join(",", parts.ToArray()) + "]"; } if (value is IFormattable) return ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture); throw new ConsultantException("invalid_domain_file", "El dominio contiene un valor no permitido."); }
        private static string FileHash(string path) { return JsonUtil.Sha256(File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n").Replace("\r", "\n")); }
        private static void ValidateFile(Manifest manifest, string relative, string expected) { if (!ShaPattern.IsMatch(expected ?? string.Empty)) throw new ConsultantException("invalid_file_hash", "El SHA-256 declarado no es válido."); string path = ContainedFile(manifest.root, relative); string actual = FileHash(path); if (actual != expected) throw new ConsultantException("replica_file_hash_mismatch", "Un archivo de la réplica no coincide con su SHA-256."); manifest.fileHashes[relative] = actual; }
        private static void ValidateOptionalFile(Manifest manifest, Dictionary<string, object> source, string fileKey, string hashKey) { string file = JsonUtil.GetString(source, fileKey, false), hash = JsonUtil.GetString(source, hashKey, false); if (string.IsNullOrWhiteSpace(file) != string.IsNullOrWhiteSpace(hash)) throw new ConsultantException("incomplete_signed_file", "Un archivo firmado requiere ruta y SHA-256."); if (!string.IsNullOrWhiteSpace(file)) ValidateFile(manifest, file, hash); }
        private static void ValidateDomainOptional(Manifest manifest, Dictionary<string, object> source, string fileKey, string hashKey) { ValidateOptionalFile(manifest, source, fileKey, hashKey); string file = JsonUtil.GetString(source, fileKey, false); if (!string.IsNullOrWhiteSpace(file)) ReadDomain(manifest, file, JsonUtil.GetString(source, hashKey, true)); }
        private static string ContainedFile(string root, string relative) { if (Path.IsPathRooted(relative ?? string.Empty)) throw new ConsultantException("external_replica_path", "La réplica no admite rutas absolutas."); string path = Path.GetFullPath(Path.Combine(root, relative ?? string.Empty)); string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) throw new ConsultantException("external_replica_path", "El archivo queda fuera de la réplica o no existe."); return path; }
        private static string Mode(Dictionary<string, object> item) { string mode = JsonUtil.GetString(item, "mode", false) ?? "create"; if (!Modes.Contains(mode)) throw new ConsultantException("invalid_component_mode", "mode debe ser create, update o deactivate."); return mode; }
        private static void RequirePreviousHash(Dictionary<string, object> item) { if (!ShaPattern.IsMatch(JsonUtil.GetString(item, "previous_hash", true))) throw new ConsultantException("invalid_previous_hash", "previous_hash debe ser SHA-256 hexadecimal."); }
        private static void ValidateKey(string key) { if (!KeyPattern.IsMatch(key ?? string.Empty)) throw new ConsultantException("invalid_component_key", "La clave lógica del componente no es válida."); }
        private static void Require(Dictionary<string, object> source, params string[] keys) { foreach (string key in keys) JsonUtil.GetString(source, key, true); }
        private static void AddModel(Manifest manifest, string model) { StudioService.ValidateModel(model); if (!manifest.targetModels.Contains(model)) manifest.targetModels.Add(model); }
        private static void RequireReference(Manifest manifest, string key, string model) { object raw; Dictionary<string, object> descriptor; if (!manifest.references.TryGetValue(key ?? string.Empty, out raw) || (descriptor = raw as Dictionary<string, object>) == null || JsonUtil.GetString(descriptor, "model", true) != model) throw new ConsultantException("invalid_reference", "Una referencia requerida no existe o apunta a otro modelo."); }
        private static void RequireReferenceAny(Manifest manifest, string key, IEnumerable<string> models) { object raw; Dictionary<string, object> descriptor; if (!manifest.references.TryGetValue(key ?? string.Empty, out raw) || (descriptor = raw as Dictionary<string, object>) == null || !models.Contains(JsonUtil.GetString(descriptor, "model", true))) throw new ConsultantException("invalid_reference", "Una referencia de acción no existe o apunta a otro modelo."); }
        private static void ValidateKeys(Manifest manifest) { HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal); foreach (Dictionary<string, object> item in manifest.qwebViews.Concat(manifest.reports).Concat(manifest.approvals).Concat(manifest.accessControls).Concat(manifest.recordRules)) if (!keys.Add(JsonUtil.GetString(item, "key", true))) throw new ConsultantException("duplicate_component_key", "La réplica repite una clave lógica."); foreach (Dictionary<string, object> report in manifest.reports) if (!manifest.qwebViews.Any(delegate(Dictionary<string, object> x) { return JsonUtil.GetString(x, "key", true) == JsonUtil.GetString(report, "template_key", true); })) throw new ConsultantException("missing_qweb_dependency", "Un reporte referencia una plantilla QWeb ajena a la réplica."); }
        private static Dictionary<string, object> Dict(IDictionary<string, object> source, string key, bool required) { object value; Dictionary<string, object> result = source != null && source.TryGetValue(key, out value) ? value as Dictionary<string, object> : null; if (required && result == null) throw new ConsultantException("invalid_replica_manifest", "Falta el objeto " + key + "."); return result; }
        private static List<Dictionary<string, object>> DictList(IDictionary<string, object> source, string key) { object value = null; List<Dictionary<string, object>> result = new List<Dictionary<string, object>>(); IEnumerable sequence = source != null && source.TryGetValue(key, out value) ? value as IEnumerable : null; if (sequence != null && !(value is string)) foreach (object item in sequence) { Dictionary<string, object> row = item as Dictionary<string, object>; if (row == null) throw new ConsultantException("invalid_replica_manifest", key + " debe contener objetos."); result.Add(row); } return result; }
        private static List<string> StringList(IDictionary<string, object> source, string key) { object value = null; List<string> result = new List<string>(); IEnumerable sequence = source != null && source.TryGetValue(key, out value) ? value as IEnumerable : null; if (sequence != null && !(value is string)) foreach (object item in sequence) { string text = Convert.ToString(item, CultureInfo.InvariantCulture); if (string.IsNullOrWhiteSpace(text)) throw new ConsultantException("invalid_replica_manifest", key + " contiene un valor vacío."); result.Add(text); } return result; }
        private static void EnsureAllowedKeys(Dictionary<string, object> source, IEnumerable<string> allowed, string group) { HashSet<string> accepted = new HashSet<string>(allowed, StringComparer.Ordinal); string unknown = source.Keys.FirstOrDefault(delegate(string key) { return !accepted.Contains(key); }); if (unknown != null) throw new ConsultantException("unknown_component_parameter", "El parámetro " + unknown + " no está permitido en " + group + "."); }
        private static string NormalizeXml(string value) { return Regex.Replace(System.Net.WebUtility.HtmlDecode(JsonUtil.SanitizeMojibake(value ?? string.Empty)).Replace("\r", string.Empty).Replace("\n", string.Empty), ">\\s+<", "><").Trim(); }
        private static string TechnicalModel(string type) { if (type.Contains("qweb_view")) return "ir.ui.view"; if (type.Contains("report")) return "ir.actions.report"; if (type.Contains("approval_rule")) return "studio.approval.rule"; if (type.Contains("model_access")) return "ir.model.access"; return "ir.rule"; }
        private static string[] VerificationFields(string model) { if (model == "ir.ui.view") return new[] { "id", "name", "key", "type", "arch", "active" }; if (model == "ir.actions.report") return ReportFields(); if (model == "studio.approval.rule") return ApprovalFields(); if (model == "ir.model.access") return AccessFields(); return RuleFields(); }
        private static string[] ReportFields() { return new[] { "id", "name", "model", "report_type", "report_name", "report_file", "binding_model_id", "binding_type", "binding_view_types", "group_ids", "multi", "paperformat_id", "print_report_name", "attachment_use", "attachment", "domain" }; }
        private static string[] ApprovalFields() { return new[] { "id", "name", "active", "model_id", "method", "action_id", "message", "approver_ids", "approval_group_id", "users_to_notify", "notification_order", "exclusive_user", "domain" }; }
        private static string[] AccessFields() { return new[] { "id", "name", "active", "model_id", "group_id", "perm_read", "perm_write", "perm_create", "perm_unlink" }; }
        private static string[] RuleFields() { return new[] { "id", "name", "active", "model_id", "groups", "domain_force", "perm_read", "perm_write", "perm_create", "perm_unlink" }; }
        private static Dictionary<string, object> RestorableValues(string model, Dictionary<string, object> previous) { HashSet<string> fields = new HashSet<string>(VerificationFields(model).Where(delegate(string x) { return x != "id" && x != "key" && x != "type" && x != "model"; }), StringComparer.Ordinal); return previous.Where(delegate(KeyValuePair<string, object> x) { return fields.Contains(x.Key); }).ToDictionary(delegate(KeyValuePair<string, object> x) { return x.Key; }, delegate(KeyValuePair<string, object> x) { return x.Value; }, StringComparer.Ordinal); }
        private static Dictionary<string, object> NeutralValues(string model) { if (model == "ir.actions.report") return JsonUtil.Dictionary("binding_model_id", false, "binding_view_types", string.Empty); return JsonUtil.Dictionary("active", false); }
        private static object PublicComponents(IEnumerable<Dictionary<string, object>> components) { return components.Select(delegate(Dictionary<string, object> x) { return x.Where(delegate(KeyValuePair<string, object> p) { return p.Key != "snapshot"; }).ToDictionary(delegate(KeyValuePair<string, object> p) { return p.Key; }, delegate(KeyValuePair<string, object> p) { return p.Value; }, StringComparer.Ordinal); }).ToArray(); }
        private static object PublicReferences(Manifest manifest) { return manifest.references.Select(delegate(KeyValuePair<string, object> x) { Dictionary<string, object> d = x.Value as Dictionary<string, object>; return JsonUtil.Dictionary("key", x.Key, "type", JsonUtil.GetString(d, "type", true), "model", JsonUtil.GetString(d, "model", true), "description", JsonUtil.GetString(d, "description", false)); }).ToArray(); }
        private static object Summary(ChangeSet change, bool confirmation) { Dictionary<string, object> result = JsonUtil.Dictionary("package_id", change.id, "environment", change.environment, "replica_slug", change.replica_slug, "phase", 4, "status", change.status, "uat_status", change.uat_status, "created_at", change.created_at, "expires_at", change.expires_at, "manifest_hash", change.replica_manifest_hash, "precondition_hash", change.precondition_hash, "capability_hash", change.capability_hash, "operations", change.operations, "warnings", change.warnings, "last_error", change.last_error); if (confirmation) result["required_confirmation"] = string.Equals(change.category, "production_promotion", StringComparison.Ordinal) ? "PROMOVER-PRODUCCION " + change.id + " RESPALDO-CONFIRMADO" : "APLICAR " + change.id; if (!string.IsNullOrEmpty(change.backup_reference)) result["backup_reference"] = change.backup_reference; return result; }
        private static string Integrity(ChangeSet change) { Dictionary<string, object> d = JsonUtil.Dictionary("schema_version", change.schema_version, "id", change.id, "client_slug", change.client_slug, "environment", change.environment, "category", change.category, "model", change.model, "target_models", change.target_models, "replica_slug", change.replica_slug, "replica_manifest_hash", change.replica_manifest_hash, "replica_descriptor", change.replica_descriptor, "created_at", change.created_at, "expires_at", change.expires_at, "precondition_hash", change.precondition_hash, "capability_hash", change.capability_hash, "profile_fingerprint", change.profile_fingerprint, "policy_version", change.policy_version, "operations", change.operations.Select(delegate(ChangeOperation x) { return JsonUtil.Dictionary("operation_id", x.operation_id, "type", x.type, "model", x.model, "parameters", x.parameters); }).ToArray()); if (!string.IsNullOrEmpty(change.backup_reference)) d["backup_reference"] = change.backup_reference; return JsonUtil.CanonicalHash(d); }

        private sealed class Manifest { public string slug; public string root; public string hash; public Dictionary<string, object> raw; public Dictionary<string, object> references; public List<Dictionary<string, object>> qwebViews; public List<Dictionary<string, object>> reports; public List<Dictionary<string, object>> approvals; public List<Dictionary<string, object>> accessControls; public List<Dictionary<string, object>> recordRules; public List<Dictionary<string, object>> negativeTests; public List<string> targetModels; public Dictionary<string, string> fileHashes; }
        private sealed class Analysis { public List<Dictionary<string, object>> components; public List<string> blocked; public Dictionary<string, long> modelIds; public Dictionary<string, object> approvalContract; public string preconditionHash; public string capabilityHash; }
    }
}
