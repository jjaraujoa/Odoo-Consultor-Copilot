using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace OdooConsultor
{
    internal sealed class FunctionalApplicationDefinition
    {
        public string key;
        public string[] modules;
        public string[] models;
        public string[] adapters;
    }

    internal sealed class FunctionalAdapterDefinition
    {
        public string key;
        public string model;
        public string application;
        public Dictionary<string, string> fields;
        public string[] configurableFields;
        public string archiveField;
        public bool supportsCreate;
    }

    public sealed class FunctionalService
    {
        public const string PolicyVersion = "functional-configuration/1";
        private static readonly Regex KeyPattern = new Regex("^[a-z0-9][a-z0-9_-]{1,62}$", RegexOptions.Compiled);
        private static readonly Regex DiagnosticIdPattern = new Regex("^diag-[a-f0-9]{32}$", RegexOptions.Compiled);
        private static readonly HashSet<string> OperationTypes = new HashSet<string>(new[] { "functional_create", "functional_update", "functional_deactivate" }, StringComparer.Ordinal);
        private readonly ProjectStore project;

        private static readonly Dictionary<string, FunctionalApplicationDefinition> Applications = BuildApplications();
        private static readonly Dictionary<string, FunctionalAdapterDefinition> Adapters = BuildAdapters();

        public FunctionalService(ProjectStore projectStore)
        {
            project = projectStore;
        }

        public object Inventory(string environment, string application)
        {
            environment = ProjectStore.NormalizeEnvironment(environment);
            FunctionalApplicationDefinition definition = Application(application);
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                ConnectionProfile profile = project.GetProfile(environment, true);
                ValidateProfileVersion(profile);
                using (OdooClient client = Open(environment))
                {
                    Dictionary<string, object> inventory = BuildInventory(client, profile, environment, definition);
                    AuditLog.Append(project, environment, "funcional_inventariar_aplicacion", null, "success",
                        "Inventario funcional " + definition.key + "; hash " + Convert.ToString(inventory["inventory_hash"], CultureInfo.InvariantCulture).Substring(0, 12) + ".", timer.ElapsedMilliseconds);
                    return inventory;
                }
            }
            catch (Exception error)
            {
                AuditLog.Append(project, environment, "funcional_inventariar_aplicacion", null, "error", SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }

        public object Diagnose(string environment, string diagnosticSlug, IDictionary<string, object> objective)
        {
            environment = ProjectStore.NormalizeEnvironment(environment);
            string slug = ProjectStore.NormalizeSlug(diagnosticSlug, "diagnostico");
            Dictionary<string, object> normalizedObjective = NormalizeObjective(objective);
            FunctionalApplicationDefinition definition = Application(JsonUtil.GetString(normalizedObjective, "application", true));
            string diagnosticId = "diag-" + Guid.NewGuid().ToString("N");
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                ConnectionProfile profile = project.GetProfile(environment, true);
                ValidateProfileVersion(profile);
                using (OdooClient client = Open(environment))
                {
                    Dictionary<string, object> inventory = BuildInventory(client, profile, environment, definition);
                    List<Dictionary<string, object>> requirements = ObjectList(normalizedObjective["requirements"]);
                    List<FunctionalGap> gaps = EvaluateRequirements(client, definition, requirements, inventory);
                    string objectiveHash = JsonUtil.CanonicalHash(normalizedObjective);
                    string preconditionHash = JsonUtil.CanonicalHash(gaps.Select(GapPrecondition).ToArray());
                    FunctionalDiagnostic diagnostic = new FunctionalDiagnostic
                    {
                        schema_version = 1,
                        id = diagnosticId,
                        client_slug = project.Config.client_slug,
                        diagnostic_slug = slug,
                        environment = environment,
                        application = definition.key,
                        objective = JsonUtil.GetString(normalizedObjective, "objective", true),
                        status = gaps.Any(delegate(FunctionalGap gap) { return gap.status == "blocked"; }) ? "blocked" : (gaps.Any(delegate(FunctionalGap gap) { return gap.status != "compliant"; }) ? "gaps_found" : "compliant"),
                        created_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        profile_fingerprint = profile.credential_fingerprint,
                        server_series = profile.server_series,
                        policy_version = PolicyVersion,
                        inventory_hash = Convert.ToString(inventory["inventory_hash"], CultureInfo.InvariantCulture),
                        capability_hash = Convert.ToString(inventory["capability_hash"], CultureInfo.InvariantCulture),
                        precondition_hash = preconditionHash,
                        objective_hash = objectiveHash,
                        requirements = requirements,
                        gaps = gaps,
                        warnings = new List<string>
                        {
                            "La base real confirma instalación, metadatos y permisos; no demuestra por sí sola el proceso funcional esperado.",
                            "Las brechas de módulos y decisiones generan runbook. Solo los adaptadores declarados pueden producir un paquete ejecutable en staging.",
                            "Revisar la matriz antes de preparar una parametrización."
                        }
                    };
                    diagnostic.integrity_hash = DiagnosticIntegrityHash(diagnostic);
                    JsonUtil.WriteJsonFile(project.FunctionalDiagnosticPath(slug, diagnosticId, true), diagnostic);
                    AuditLog.Append(project, environment, "funcional_diagnosticar_objetivo", diagnosticId, "success",
                        "Diagnóstico " + objectiveHash.Substring(0, 12) + ": " + gaps.Count.ToString(CultureInfo.InvariantCulture) + " requisitos; " + gaps.Count(delegate(FunctionalGap gap) { return gap.status != "compliant"; }).ToString(CultureInfo.InvariantCulture) + " brechas.", timer.ElapsedMilliseconds);
                    return DiagnosticSummary(diagnostic);
                }
            }
            catch (Exception error)
            {
                AuditLog.Append(project, environment, "funcional_diagnosticar_objetivo", diagnosticId, "error", SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }

        public object Prepare(string diagnosticId, IEnumerable<string> selectedGapKeys)
        {
            FunctionalDiagnostic diagnostic = LoadDiagnostic(diagnosticId);
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                HashSet<string> selection = NormalizeSelection(selectedGapKeys);
                List<FunctionalGap> executable = diagnostic.gaps.Where(delegate(FunctionalGap gap)
                {
                    return gap.action == "confirmed_package" && (selection.Count == 0 || selection.Contains(gap.key));
                }).ToList();
                if (selection.Count > 0)
                {
                    foreach (string key in selection)
                    {
                        FunctionalGap selected = diagnostic.gaps.FirstOrDefault(delegate(FunctionalGap gap) { return gap.key == key; });
                        if (selected == null) throw new ConsultantException("unknown_gap", "La brecha " + key + " no pertenece al diagnóstico.");
                        if (selected.action != "confirmed_package") throw new ConsultantException("gap_not_executable", "La brecha " + key + " requiere decisión o runbook y no puede ejecutarse automáticamente.");
                    }
                }
                if (diagnostic.environment == "production")
                {
                    List<object> steps = diagnostic.gaps.Where(delegate(FunctionalGap gap) { return gap.status != "compliant"; })
                        .Select(delegate(FunctionalGap gap) { return (object)("Resolver " + gap.key + " (" + gap.action + ") y verificar el resultado en staging antes de promoverlo."); }).ToList();
                    return new StudioService(project).GenerateRunbook("production", "modificacion", "Parametrización funcional: " + diagnostic.application, diagnostic.objective, steps);
                }
                StudioService.EnsureWriteAllowed(diagnostic.environment);
                if (executable.Count == 0) throw new ConsultantException("no_executable_gaps", "El diagnóstico no contiene brechas ejecutables seleccionadas.");
                ConnectionProfile profile = project.GetProfile(diagnostic.environment, true);
                if (!string.Equals(profile.credential_fingerprint, diagnostic.profile_fingerprint, StringComparison.Ordinal)) throw new ConsultantException("profile_changed", "La credencial cambió desde el diagnóstico. Genera uno nuevo.");
                string packageId = "chg-" + Guid.NewGuid().ToString("N");
                List<ChangeOperation> operations = new List<ChangeOperation>();
                using (OdooClient client = Open(diagnostic.environment))
                {
                    ValidateProfileVersion(profile);
                    foreach (FunctionalGap gap in executable) operations.Add(CompileOperation(client, gap));
                    string capabilityHash = CapabilityHashForOperations(client, profile, operations);
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    ChangeSet change = new ChangeSet
                    {
                        schema_version = 4,
                        id = packageId,
                        client_slug = project.Config.client_slug,
                        environment = "staging",
                        category = "functional_parameterization",
                        risk = "medium",
                        status = "prepared",
                        model = diagnostic.application,
                        target_models = operations.Select(delegate(ChangeOperation operation) { return operation.model; }).Distinct(StringComparer.Ordinal).OrderBy(delegate(string value) { return value; }, StringComparer.Ordinal).ToList(),
                        created_at = now.ToString("o", CultureInfo.InvariantCulture),
                        expires_at = now.AddMinutes(project.Config.package_confirmation_minutes).ToString("o", CultureInfo.InvariantCulture),
                        precondition_hash = diagnostic.precondition_hash,
                        profile_fingerprint = profile.credential_fingerprint,
                        requested_by = Environment.UserName,
                        impact = "Crea, actualiza o desactiva únicamente registros de configuración cubiertos por adaptadores funcionales cerrados.",
                        rollback_strategy = "Los registros creados se archivan; las actualizaciones restauran un snapshot DPAPI. Nunca se elimina un registro.",
                        policy_version = PolicyVersion,
                        objective = diagnostic.objective,
                        contract_hash = diagnostic.objective_hash,
                        capability_hash = capabilityHash,
                        preconditions = new List<Dictionary<string, object>>
                        {
                            JsonUtil.Dictionary("type", "diagnostic_integrity", "expected", diagnostic.integrity_hash),
                            JsonUtil.Dictionary("type", "credential_fingerprint", "expected", profile.credential_fingerprint),
                            JsonUtil.Dictionary("type", "environment", "expected", "staging")
                        },
                        validations = new List<Dictionary<string, object>>
                        {
                            JsonUtil.Dictionary("type", "adapter_state", "required", true),
                            JsonUtil.Dictionary("type", "idempotency", "required", true),
                            JsonUtil.Dictionary("type", "rollback_without_unlink", "required", true)
                        },
                        operations = operations,
                        warnings = new List<string>
                        {
                            "Revisar los valores deseados y los IDs técnicos antes de confirmar.",
                            "Las brechas no seleccionadas permanecen en el diagnóstico.",
                            "La confirmación exacta es APLICAR " + packageId + "."
                        }
                    };
                    change.integrity_hash = PackageIntegrityHash(change);
                    ValidatePackage(change);
                    JsonUtil.WriteJsonFile(project.ChangeSetPath(packageId), change);
                    AuditLog.Append(project, "staging", "funcional_preparar_parametrizacion", packageId, "success",
                        "Diagnóstico " + diagnostic.id + " compilado en " + operations.Count.ToString(CultureInfo.InvariantCulture) + " operaciones funcionales.", timer.ElapsedMilliseconds);
                    return PackageSummary(change, true);
                }
            }
            catch (Exception error)
            {
                AuditLog.Append(project, diagnostic.environment, "funcional_preparar_parametrizacion", diagnosticId, "error", SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }

        public object Apply(ChangeSet change, string confirmation)
        {
            ValidatePackage(change);
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                StudioService.EnsureWriteAllowed(change.environment);
                if (change.status == "applied" || change.status == "verified") return PackageSummary(change, false);
                if (change.status != "prepared" && change.status != "failed_partial") throw new ConsultantException("package_not_applicable", "El paquete funcional no está preparado para aplicar.");
                DateTimeOffset expiresAt;
                if (!DateTimeOffset.TryParse(change.expires_at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expiresAt) || DateTimeOffset.UtcNow > expiresAt) throw new ConsultantException("confirmation_expired", "La confirmación venció. Prepara una parametrización nueva.");
                if (!string.Equals(confirmation, "APLICAR " + change.id, StringComparison.Ordinal)) throw new ConsultantException("confirmation_mismatch", "La confirmación debe ser exactamente APLICAR " + change.id + ".");
                ConnectionProfile profile = project.GetProfile("staging", true);
                if (!string.Equals(profile.credential_fingerprint, change.profile_fingerprint, StringComparison.Ordinal)) throw new ConsultantException("profile_changed", "La credencial cambió desde la preparación.");
                using (OdooClient client = Open("staging"))
                {
                    ValidateProfileVersion(profile);
                    string capabilityHash = CapabilityHashForOperations(client, profile, change.operations);
                    if (!string.Equals(capabilityHash, change.capability_hash, StringComparison.Ordinal)) throw new ConsultantException("capabilities_changed", "Los modelos, campos o permisos cambiaron desde la vista previa. No se aplicó nada.");
                    Dictionary<string, long> recoverable = ValidateAllPreconditions(client, change.operations);
                    change.status = "applying";
                    JsonUtil.WriteJsonFile(project.ChangeSetPath(change.id), change);
                    try
                    {
                        foreach (ChangeOperation operation in change.operations)
                        {
                            if (operation.status == "applied") continue;
                            long recovered;
                            if (recoverable.TryGetValue(operation.operation_id, out recovered)) operation.result_id = recovered;
                            else operation.result_id = ApplyOperation(client, operation);
                            operation.status = "applied";
                            JsonUtil.WriteJsonFile(project.ChangeSetPath(change.id), change);
                        }
                        change.status = "applied";
                        change.applied_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                        change.last_error = null;
                        JsonUtil.WriteJsonFile(project.ChangeSetPath(change.id), change);
                    }
                    catch (Exception operationError)
                    {
                        change.status = "failed_partial";
                        change.last_error = SafeError(operationError);
                        JsonUtil.WriteJsonFile(project.ChangeSetPath(change.id), change);
                        throw;
                    }
                }
                AuditLog.Append(project, "staging", "studio_aplicar_paquete", change.id, "success", "Paquete funcional aplicado; " + change.operations.Count.ToString(CultureInfo.InvariantCulture) + " operaciones.", timer.ElapsedMilliseconds);
                return PackageSummary(change, false);
            }
            catch (Exception error)
            {
                AuditLog.Append(project, "staging", "studio_aplicar_paquete", change.id, "error", SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }

        public object Verify(ChangeSet change)
        {
            ValidatePackage(change);
            if (change.status != "applied" && change.status != "verified" && change.status != "rolled_back" && change.status != "rollback_partial") throw new ConsultantException("package_not_verifiable", "El paquete funcional todavía no se ha aplicado.");
            bool rollback = change.status == "rolled_back" || change.status == "rollback_partial";
            bool all = true;
            using (OdooClient client = Open("staging"))
            {
                foreach (ChangeOperation operation in change.operations)
                {
                    bool valid = rollback ? VerifyRollback(client, operation) : VerifyDesired(client, operation);
                    operation.verification = valid ? "verified" : "mismatch";
                    if (!valid) all = false;
                }
            }
            if (all && change.status == "applied") change.status = "verified";
            change.verified_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            JsonUtil.WriteJsonFile(project.ChangeSetPath(change.id), change);
            AuditLog.Append(project, "staging", "studio_verificar_paquete", change.id, all ? "success" : "mismatch", "Verificación funcional por lectura: " + (all ? "completa" : "con diferencias") + ".", 0);
            return PackageSummary(change, false);
        }

        public object Rollback(ChangeSet change, string confirmation)
        {
            ValidatePackage(change);
            StudioService.EnsureWriteAllowed(change.environment);
            if (!string.Equals(confirmation, "REVERTIR " + change.id, StringComparison.Ordinal)) throw new ConsultantException("confirmation_mismatch", "La confirmación debe ser exactamente REVERTIR " + change.id + ".");
            if (change.status == "rolled_back" || change.status == "rollback_partial") return PackageSummary(change, false);
            if (change.status != "applied" && change.status != "verified") throw new ConsultantException("package_not_reversible", "Solo se pueden revertir paquetes aplicados o verificados.");
            bool partial = false;
            using (OdooClient client = Open("staging"))
            {
                foreach (ChangeOperation operation in Enumerable.Reverse(change.operations))
                {
                    try
                    {
                        Dictionary<string, object> parameters = operation.parameters;
                        if (operation.type == "functional_create")
                        {
                            FunctionalAdapterDefinition adapter = Adapter(JsonUtil.GetString(parameters, "adapter", true));
                            string deactField = adapter.archiveField ?? (adapter.fields.ContainsKey("active") ? "active" : (adapter.fields.ContainsKey("is_published") ? "is_published" : null));
                            bool ok = false;
                            if (deactField != null)
                            {
                                ok = client.Write(operation.model, new[] { operation.result_id }, JsonUtil.Dictionary(deactField, false));
                            }
                            operation.status = ok ? "rolled_back" : "rollback_failed";
                            partial = partial || !ok;
                        }
                        else
                        {
                            Dictionary<string, object> previous = ProtectedSnapshot.Unprotect(JsonUtil.GetString(parameters, "previous_snapshot", true));
                            FunctionalAdapterDefinition adapter = Adapter(JsonUtil.GetString(parameters, "adapter", true));
                            Dictionary<string, object> restore = FilterDesired(adapter, previous, true);
                            bool ok = client.Write(operation.model, new[] { operation.result_id }, restore);
                            operation.status = ok ? "rolled_back" : "rollback_failed";
                            partial = partial || !ok;
                        }
                    }
                    catch
                    {
                        operation.status = "rollback_failed";
                        partial = true;
                    }
                }
            }
            change.status = partial ? "rollback_partial" : "rolled_back";
            change.rolled_back_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            JsonUtil.WriteJsonFile(project.ChangeSetPath(change.id), change);
            AuditLog.Append(project, "staging", "studio_revertir_paquete_seguro", change.id, partial ? "partial" : "success", "Reversión funcional sin eliminar registros.", 0);
            return PackageSummary(change, false);
        }

        public static void ValidatePackage(ChangeSet change)
        {
            if (change == null || change.schema_version != 4 || change.category != "functional_parameterization" || change.policy_version != PolicyVersion) throw new ConsultantException("unsupported_package_schema", "El paquete no corresponde a parametrización funcional controlada.");
            StudioService.EnsureWriteAllowed(change.environment);
            if (!Regex.IsMatch(change.id ?? string.Empty, "^chg-[a-f0-9]{32}$") || string.IsNullOrWhiteSpace(change.client_slug) || string.IsNullOrWhiteSpace(change.profile_fingerprint)) throw new ConsultantException("tool_contract_violation", "El paquete funcional no contiene identidad e integridad válidas.");
            Application(change.model);
            if (change.operations == null || change.operations.Count < 1 || change.operations.Count > 20) throw new ConsultantException("tool_contract_violation", "El paquete funcional debe contener entre 1 y 20 operaciones.");
            if (string.IsNullOrWhiteSpace(change.integrity_hash) || !string.Equals(change.integrity_hash, PackageIntegrityHash(change), StringComparison.Ordinal)) throw new ConsultantException("package_integrity_failed", "La parte inmutable del paquete funcional fue modificada.");
            HashSet<string> operationIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (ChangeOperation operation in change.operations)
            {
                if (operation == null || !OperationTypes.Contains(operation.type ?? string.Empty) || !Regex.IsMatch(operation.operation_id ?? string.Empty, "^op-[a-f0-9]{32}$") || !operationIds.Add(operation.operation_id ?? string.Empty)) throw new ConsultantException("tool_contract_violation", "El paquete funcional contiene una operación no permitida o repetida.");
                Dictionary<string, object> parameters = operation.parameters;
                PolicyCompiler.EnsureOnlyKeys(parameters, new[] { "adapter", "gap_key", "match_name", "record_id", "expected_current_hash", "desired_values", "previous_snapshot" });
                FunctionalAdapterDefinition adapter = Adapter(JsonUtil.GetString(parameters, "adapter", true));
                if (!string.Equals(adapter.model, operation.model, StringComparison.Ordinal)) throw new ConsultantException("tool_contract_violation", "El modelo de la operación no coincide con su adaptador cerrado.");
                string gapKey = JsonUtil.GetString(parameters, "gap_key", true);
                string matchName = JsonUtil.GetString(parameters, "match_name", true);
                if (!KeyPattern.IsMatch(gapKey) || SafeText(matchName, 200, "match_name") != matchName) throw new ConsultantException("tool_contract_violation", "La identidad funcional de la operación no es válida.");
                Dictionary<string, object> desired = parameters.ContainsKey("desired_values") ? parameters["desired_values"] as Dictionary<string, object> : null;
                ValidateDesired(adapter, desired, true);
                if (!string.Equals(Convert.ToString(desired["name"], CultureInfo.InvariantCulture), matchName, StringComparison.Ordinal)) throw new ConsultantException("tool_contract_violation", "El nombre deseado no coincide con la precondición de búsqueda.");
                if (operation.type == "functional_create")
                {
                    bool activeState = desired.ContainsKey("active") ? Convert.ToBoolean(desired["active"], CultureInfo.InvariantCulture) : (desired.ContainsKey("is_published") ? Convert.ToBoolean(desired["is_published"], CultureInfo.InvariantCulture) : true);
                    if (JsonUtil.GetLong(parameters, "record_id", false) != 0 || !string.IsNullOrWhiteSpace(JsonUtil.GetString(parameters, "previous_snapshot", false)) || !activeState) throw new ConsultantException("tool_contract_violation", "Una creación funcional debe ser activa y no puede apuntar a un registro previo.");
                }
                else if (JsonUtil.GetLong(parameters, "record_id", true) <= 0 || string.IsNullOrWhiteSpace(JsonUtil.GetString(parameters, "expected_current_hash", true)) || string.IsNullOrWhiteSpace(JsonUtil.GetString(parameters, "previous_snapshot", true))) throw new ConsultantException("tool_contract_violation", "Una actualización funcional exige ID, precondición y snapshot DPAPI.");
                if (operation.type == "functional_deactivate")
                {
                    bool isInactive = false;
                    if (desired.ContainsKey("active")) isInactive = !Convert.ToBoolean(desired["active"], CultureInfo.InvariantCulture);
                    else if (desired.ContainsKey("is_published")) isInactive = !Convert.ToBoolean(desired["is_published"], CultureInfo.InvariantCulture);
                    if (!isInactive) throw new ConsultantException("tool_contract_violation", "La desactivación funcional debe terminar con active=false o is_published=false.");
                }
            }
            List<string> expectedModels = change.operations.Select(delegate(ChangeOperation operation) { return operation.model; }).Distinct(StringComparer.Ordinal).OrderBy(delegate(string value) { return value; }, StringComparer.Ordinal).ToList();
            List<string> actualModels = (change.target_models ?? new List<string>()).Distinct(StringComparer.Ordinal).OrderBy(delegate(string value) { return value; }, StringComparer.Ordinal).ToList();
            if (!expectedModels.SequenceEqual(actualModels, StringComparer.Ordinal)) throw new ConsultantException("tool_contract_violation", "Los modelos objetivo del paquete funcional no coinciden con sus operaciones.");
        }

        internal static Dictionary<string, object> NormalizeObjectiveForSelfTest(IDictionary<string, object> objective)
        {
            return NormalizeObjective(objective);
        }

        internal static bool SupportsApplicationForSelfTest(string application)
        {
            return Applications.ContainsKey((application ?? string.Empty).Trim().ToLowerInvariant());
        }

        internal static string[] AdapterKeysForSelfTest()
        {
            return Adapters.Keys.OrderBy(delegate(string value) { return value; }, StringComparer.Ordinal).ToArray();
        }

        internal static void SignPackageForSelfTest(ChangeSet change)
        {
            change.integrity_hash = PackageIntegrityHash(change);
        }

        private static Dictionary<string, FunctionalApplicationDefinition> BuildApplications()
        {
            Dictionary<string, FunctionalApplicationDefinition> result = new Dictionary<string, FunctionalApplicationDefinition>(StringComparer.Ordinal);
            result["crm"] = new FunctionalApplicationDefinition { key = "crm", modules = new[] { "crm", "sales_team" }, models = new[] { "crm.lead", "crm.stage", "crm.team" }, adapters = new[] { "crm_stage", "sales_team" } };
            result["ventas"] = new FunctionalApplicationDefinition { key = "ventas", modules = new[] { "sale", "sale_management", "sales_team" }, models = new[] { "sale.order", "sale.order.line", "crm.team", "product.pricelist" }, adapters = new[] { "sales_team" } };
            result["compras"] = new FunctionalApplicationDefinition { key = "compras", modules = new[] { "purchase", "purchase_stock" }, models = new[] { "purchase.order", "purchase.order.line", "product.supplierinfo" }, adapters = new string[0] };
            result["inventario"] = new FunctionalApplicationDefinition { key = "inventario", modules = new[] { "stock", "stock_account" }, models = new[] { "stock.picking", "stock.warehouse", "stock.location", "stock.picking.type" }, adapters = new string[0] };
            result["contabilidad"] = new FunctionalApplicationDefinition { key = "contabilidad", modules = new[] { "account", "account_accountant" }, models = new[] { "account.move", "account.journal", "account.payment.term", "account.tax" }, adapters = new string[0] };
            result["marketing"] = new FunctionalApplicationDefinition { key = "marketing", modules = new[] { "utm", "mass_mailing", "marketing_automation", "social" }, models = new[] { "utm.campaign", "mailing.list", "mailing.mailing", "marketing.campaign" }, adapters = new[] { "mailing_list" } };
            result["sitio_web"] = new FunctionalApplicationDefinition { key = "sitio_web", modules = new[] { "website", "website_crm", "website_blog", "website_mass_mailing" }, models = new[] { "website", "website.menu", "website.page" }, adapters = new[] { "website_setting", "website_menu", "website_page" } };
            result["ecommerce"] = new FunctionalApplicationDefinition { key = "ecommerce", modules = new[] { "website_sale", "website_sale_comparison", "website_sale_wishlist", "website_sale_stock", "website_sale_loyalty" }, models = new[] { "product.public.category", "product.ribbon", "product.tag", "product.template", "delivery.carrier", "payment.provider" }, adapters = new[] { "ecommerce_public_category", "ecommerce_ribbon", "ecommerce_product_tag", "ecommerce_product_publish", "ecommerce_delivery_carrier", "ecommerce_payment_provider" } };
            return result;
        }

        private static Dictionary<string, FunctionalAdapterDefinition> BuildAdapters()
        {
            Dictionary<string, FunctionalAdapterDefinition> result = new Dictionary<string, FunctionalAdapterDefinition>(StringComparer.Ordinal);
            result["crm_stage"] = new FunctionalAdapterDefinition
            {
                key = "crm_stage", model = "crm.stage", application = "crm",
                fields = new Dictionary<string, string>(StringComparer.Ordinal) { { "name", "char" }, { "sequence", "integer" }, { "fold", "boolean" }, { "active", "boolean" } },
                configurableFields = new[] { "name", "sequence", "fold", "active" },
                supportsCreate = true, archiveField = "active"
            };
            result["sales_team"] = new FunctionalAdapterDefinition
            {
                key = "sales_team", model = "crm.team", application = "ventas",
                fields = new Dictionary<string, string>(StringComparer.Ordinal) { { "name", "char" }, { "active", "boolean" } },
                configurableFields = new[] { "name", "active" },
                supportsCreate = true, archiveField = "active"
            };
            result["mailing_list"] = new FunctionalAdapterDefinition
            {
                key = "mailing_list", model = "mailing.list", application = "marketing",
                fields = new Dictionary<string, string>(StringComparer.Ordinal) { { "name", "char" }, { "active", "boolean" } },
                configurableFields = new[] { "name", "active" },
                supportsCreate = true, archiveField = "active"
            };
            result["website_setting"] = new FunctionalAdapterDefinition
            {
                key = "website_setting", model = "website", application = "sitio_web",
                fields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "name", "char" }, { "domain", "char" }, { "cookies_bar", "boolean" },
                    { "social_facebook", "char" }, { "social_twitter", "char" }, { "social_linkedin", "char" },
                    { "social_youtube", "char" }, { "social_instagram", "char" }
                },
                configurableFields = new[] { "name", "domain", "cookies_bar", "social_facebook", "social_twitter", "social_linkedin", "social_youtube", "social_instagram" },
                supportsCreate = false, archiveField = null
            };
            result["website_menu"] = new FunctionalAdapterDefinition
            {
                key = "website_menu", model = "website.menu", application = "sitio_web",
                fields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "name", "char" }, { "url", "char" }, { "sequence", "integer" }, { "new_window", "boolean" }, { "is_mega_menu", "boolean" }
                },
                configurableFields = new[] { "name", "url", "sequence", "new_window", "is_mega_menu" },
                supportsCreate = false, archiveField = null
            };
            result["website_page"] = new FunctionalAdapterDefinition
            {
                key = "website_page", model = "website.page", application = "sitio_web",
                fields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "name", "char" }, { "url", "char" }, { "is_published", "boolean" },
                    { "website_indexed", "boolean" }, { "active", "boolean" }
                },
                configurableFields = new[] { "name", "url", "is_published", "website_indexed", "active" },
                supportsCreate = true, archiveField = "active"
            };
            result["ecommerce_public_category"] = new FunctionalAdapterDefinition
            {
                key = "ecommerce_public_category", model = "product.public.category", application = "ecommerce",
                fields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "name", "char" }, { "sequence", "integer" }
                },
                configurableFields = new[] { "name", "sequence" },
                supportsCreate = false, archiveField = null
            };
            result["ecommerce_ribbon"] = new FunctionalAdapterDefinition
            {
                key = "ecommerce_ribbon", model = "product.ribbon", application = "ecommerce",
                fields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "name", "char" }, { "bg_color", "char" }, { "text_color", "char" }, { "position", "char" }, { "html", "char" }
                },
                configurableFields = new[] { "name", "bg_color", "text_color", "position", "html" },
                supportsCreate = false, archiveField = null
            };
            result["ecommerce_product_tag"] = new FunctionalAdapterDefinition
            {
                key = "ecommerce_product_tag", model = "product.tag", application = "ecommerce",
                fields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "name", "char" }, { "color", "integer" }, { "active", "boolean" }
                },
                configurableFields = new[] { "name", "color", "active" },
                supportsCreate = true, archiveField = "active"
            };
            result["ecommerce_product_publish"] = new FunctionalAdapterDefinition
            {
                key = "ecommerce_product_publish", model = "product.template", application = "ecommerce",
                fields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "name", "char" }, { "is_published", "boolean" }, { "website_sequence", "integer" }, { "active", "boolean" }
                },
                configurableFields = new[] { "name", "is_published", "website_sequence", "active" },
                supportsCreate = false, archiveField = "active"
            };
            result["ecommerce_delivery_carrier"] = new FunctionalAdapterDefinition
            {
                key = "ecommerce_delivery_carrier", model = "delivery.carrier", application = "ecommerce",
                fields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "name", "char" }, { "fixed_price", "integer" }, { "delivery_type", "char" }, { "is_published", "boolean" }, { "active", "boolean" }
                },
                configurableFields = new[] { "name", "fixed_price", "delivery_type", "is_published", "active" },
                supportsCreate = true, archiveField = "active"
            };
            result["ecommerce_payment_provider"] = new FunctionalAdapterDefinition
            {
                key = "ecommerce_payment_provider", model = "payment.provider", application = "ecommerce",
                fields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "name", "char" }, { "code", "char" }, { "state", "char" }, { "is_published", "boolean" }
                },
                configurableFields = new[] { "name", "code", "state", "is_published" },
                supportsCreate = false, archiveField = null
            };
            return result;
        }

        private Dictionary<string, object> BuildInventory(OdooClient client, ConnectionProfile profile, string environment, FunctionalApplicationDefinition definition)
        {
            IList<Dictionary<string, object>> moduleRows = client.SearchRead("ir.module.module", new object[] { new object[] { "name", "in", definition.modules } }, new[] { "name", "state", "installed_version" }, 50, "name asc");
            Dictionary<string, Dictionary<string, object>> moduleMap = moduleRows.ToDictionary(delegate(Dictionary<string, object> row) { return Convert.ToString(row["name"], CultureInfo.InvariantCulture); }, StringComparer.Ordinal);
            List<Dictionary<string, object>> modules = definition.modules.Select(delegate(string name)
            {
                Dictionary<string, object> row;
                return moduleMap.TryGetValue(name, out row)
                    ? JsonUtil.Dictionary("name", name, "state", row.ContainsKey("state") ? row["state"] : "unknown", "installed_version", row.ContainsKey("installed_version") ? row["installed_version"] : null)
                    : JsonUtil.Dictionary("name", name, "state", "not_found", "installed_version", null);
            }).ToList();
            IList<Dictionary<string, object>> modelRows = client.SearchRead("ir.model", new object[] { new object[] { "model", "in", definition.models } }, new[] { "model", "name", "state" }, 50, "model asc");
            HashSet<string> visibleModels = new HashSet<string>(modelRows.Select(delegate(Dictionary<string, object> row) { return Convert.ToString(row["model"], CultureInfo.InvariantCulture); }), StringComparer.Ordinal);
            Dictionary<string, Dictionary<string, object>> metadata = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            List<Dictionary<string, object>> models = new List<Dictionary<string, object>>();
            foreach (string model in definition.models)
            {
                if (!visibleModels.Contains(model))
                {
                    models.Add(JsonUtil.Dictionary("model", model, "available", false, "access", JsonUtil.Dictionary("read", false, "create", false, "write", false), "field_hash", null));
                    continue;
                }
                Dictionary<string, object> fields;
                try { fields = client.FieldsGet(model, new[] { "type", "required", "readonly", "relation", "selection" }); }
                catch (ConsultantException) { fields = new Dictionary<string, object>(StringComparer.Ordinal); }
                metadata[model] = fields;
                models.Add(JsonUtil.Dictionary("model", model, "available", true, "access", Access(client, model), "field_count", fields.Count, "field_hash", JsonUtil.CanonicalHash(fields)));
            }
            List<Dictionary<string, object>> adapters = new List<Dictionary<string, object>>();
            foreach (string key in definition.adapters)
            {
                FunctionalAdapterDefinition adapter = Adapter(key);
                Dictionary<string, object> fields;
                bool modelAvailable = metadata.TryGetValue(adapter.model, out fields);
                List<string> missing = new List<string>();
                if (modelAvailable)
                {
                    foreach (KeyValuePair<string, string> expected in adapter.fields)
                    {
                        Dictionary<string, object> actual = fields.ContainsKey(expected.Key) ? fields[expected.Key] as Dictionary<string, object> : null;
                        if (actual == null || Convert.ToString(actual.ContainsKey("type") ? actual["type"] : null, CultureInfo.InvariantCulture) != expected.Value) missing.Add(expected.Key);
                    }
                }
                Dictionary<string, object> access = Access(client, adapter.model);
                bool available = modelAvailable && missing.Count == 0 && Convert.ToBoolean(access["read"], CultureInfo.InvariantCulture);
                int sampleCount = 0;
                if (available)
                {
                    try { sampleCount = client.SearchRead(adapter.model, new object[0], new[] { "id" }, 200, "id asc").Count; }
                    catch (ConsultantException) { available = false; }
                }
                adapters.Add(JsonUtil.Dictionary(
                    "adapter", key, "model", adapter.model, "available", available, "missing_or_incompatible_fields", missing.ToArray(),
                    "access", access, "supported_modes", new[] { "create", "update", "deactivate" }, "sample_count", sampleCount, "sample_truncated", sampleCount == 200));
            }
            object capability = JsonUtil.Dictionary("profile_uid", profile.uid, "server_series", profile.server_series, "models", models, "adapters", adapters.Select(delegate(Dictionary<string, object> row) { return JsonUtil.Dictionary("adapter", row["adapter"], "available", row["available"], "missing", row["missing_or_incompatible_fields"], "access", row["access"]); }).ToArray());
            object inventoryPayload = JsonUtil.Dictionary("modules", modules, "models", models, "adapters", adapters);
            return JsonUtil.Dictionary(
                "evidence", "verificado_base", "client_slug", project.Config.client_slug, "environment", environment, "application", definition.key,
                "server_series", profile.server_series, "modules", modules, "models", models, "adapters", adapters,
                "capability_hash", JsonUtil.CanonicalHash(capability), "inventory_hash", JsonUtil.CanonicalHash(inventoryPayload),
                "notes", new[] { "Los conteos de configuración están limitados a 200 y no incluyen valores de negocio.", "Un adaptador disponible confirma contrato técnico y permisos actuales, no que deba aplicarse una configuración." });
        }

        private List<FunctionalGap> EvaluateRequirements(OdooClient client, FunctionalApplicationDefinition application, List<Dictionary<string, object>> requirements, Dictionary<string, object> inventory)
        {
            List<FunctionalGap> gaps = new List<FunctionalGap>();
            List<Dictionary<string, object>> modules = ObjectList(inventory["modules"]);
            List<Dictionary<string, object>> adapterStates = ObjectList(inventory["adapters"]);
            foreach (Dictionary<string, object> requirement in requirements)
            {
                string key = JsonUtil.GetString(requirement, "key", true);
                string kind = JsonUtil.GetString(requirement, "kind", true);
                if (kind == "module")
                {
                    string module = JsonUtil.GetString(requirement, "module", true);
                    Dictionary<string, object> row = modules.First(delegate(Dictionary<string, object> item) { return Convert.ToString(item["name"], CultureInfo.InvariantCulture) == module; });
                    string currentState = Convert.ToString(row["state"], CultureInfo.InvariantCulture);
                    string expectedState = JsonUtil.GetString(requirement, "expected_state", true);
                    bool compliant = currentState == expectedState;
                    gaps.Add(new FunctionalGap { key = key, kind = kind, module = module, status = compliant ? "compliant" : "gap", classification = "verificado_base", action = compliant ? "none" : "runbook", reason = compliant ? "El módulo coincide con el objetivo." : "Instalar, desinstalar o actualizar módulos no se ejecuta por la ruta funcional genérica.", current = JsonUtil.Dictionary("state", currentState), desired = JsonUtil.Dictionary("state", expectedState) });
                    continue;
                }
                if (kind == "decision")
                {
                    bool resolved = JsonUtil.GetBool(requirement, "resolved", false);
                    gaps.Add(new FunctionalGap { key = key, kind = kind, question = JsonUtil.GetString(requirement, "question", true), status = resolved ? "compliant" : "decision_pending", classification = "decision_consultor", action = resolved ? "none" : "consultant_decision", reason = resolved ? "La decisión está documentada en el objetivo." : "La base y las fuentes no pueden decidir una preferencia de negocio.", current = JsonUtil.Dictionary("resolved", resolved), desired = JsonUtil.Dictionary("decision", JsonUtil.GetString(requirement, "decision", false)) });
                    continue;
                }
                FunctionalAdapterDefinition adapter = Adapter(JsonUtil.GetString(requirement, "adapter", true));
                if (!application.adapters.Contains(adapter.key, StringComparer.Ordinal)) throw new ConsultantException("adapter_not_in_application", "El adaptador " + adapter.key + " no pertenece a la aplicación " + application.key + ".");
                Dictionary<string, object> adapterState = adapterStates.First(delegate(Dictionary<string, object> item) { return Convert.ToString(item["adapter"], CultureInfo.InvariantCulture) == adapter.key; });
                if (!Convert.ToBoolean(adapterState["available"], CultureInfo.InvariantCulture))
                {
                    gaps.Add(new FunctionalGap { key = key, kind = kind, adapter = adapter.key, status = "blocked", classification = "verificado_base", action = "blocked", reason = "El modelo, los campos o el permiso de lectura del adaptador no están disponibles.", current = adapterState, desired = JsonUtil.Dictionary("state", JsonUtil.GetString(requirement, "desired_state", true)) });
                    continue;
                }
                string matchName = JsonUtil.GetString(requirement, "match_name", true);
                string desiredState = JsonUtil.GetString(requirement, "desired_state", true);
                Dictionary<string, object> desiredInput = requirement.ContainsKey("desired") ? requirement["desired"] as Dictionary<string, object> : new Dictionary<string, object>();
                Dictionary<string, object> desired = NormalizeDesired(adapter, matchName, desiredState, desiredInput);
                IList<Dictionary<string, object>> rows = client.SearchRead(adapter.model, new object[] { new object[] { "name", "=", matchName } }, adapter.configurableFields.Concat(new[] { "id" }).Distinct(StringComparer.Ordinal).ToArray(), 2, "id asc");
                if (rows.Count > 1)
                {
                    gaps.Add(new FunctionalGap { key = key, kind = kind, adapter = adapter.key, status = "blocked", classification = "verificado_base", action = "blocked", reason = "Hay más de un registro con el nombre exacto y la coincidencia es ambigua.", current = JsonUtil.Dictionary("match_count", rows.Count), desired = desired });
                    continue;
                }
                if (rows.Count == 0)
                {
                    if (!adapter.supportsCreate)
                    {
                        gaps.Add(new FunctionalGap { key = key, kind = kind, adapter = adapter.key, status = "gap", classification = "verificado_base", action = "runbook", reason = "El registro no existe y este adaptador es de actualización para preservar la integridad sin eliminación.", current = JsonUtil.Dictionary("match_count", 0), desired = desired });
                        continue;
                    }
                    bool compliantMissing = desiredState == "inactive";
                    Dictionary<string, object> adapterAccess = adapterState["access"] as Dictionary<string, object>;
                    if (!compliantMissing && (adapterAccess == null || !Convert.ToBoolean(adapterAccess["create"], CultureInfo.InvariantCulture)))
                    {
                        gaps.Add(new FunctionalGap { key = key, kind = kind, adapter = adapter.key, status = "blocked", classification = "verificado_base", action = "blocked", reason = "La configuración falta, pero el usuario no tiene permiso de creación sobre el adaptador.", current = JsonUtil.Dictionary("match_count", 0, "create_access", false), desired = desired });
                        continue;
                    }
                    Dictionary<string, object> proposal = compliantMissing ? null : JsonUtil.Dictionary("adapter", adapter.key, "mode", "create", "match_name", matchName, "record_id", 0, "expected_current_hash", "missing", "desired_values", desired, "previous_snapshot", null);
                    gaps.Add(new FunctionalGap { key = key, kind = kind, adapter = adapter.key, status = compliantMissing ? "compliant" : "gap", classification = "verificado_base", action = compliantMissing ? "none" : "confirmed_package", reason = compliantMissing ? "No existe el registro y el objetivo es mantenerlo inactivo." : "Falta una configuración que el adaptador puede crear y archivar de forma controlada.", current = JsonUtil.Dictionary("match_count", 0), desired = desired, proposed_operation = proposal });
                    continue;
                }
                Dictionary<string, object> current = FilterDesired(adapter, rows[0], true);
                long recordId = Convert.ToInt64(rows[0]["id"], CultureInfo.InvariantCulture);
                bool equal = DesiredMatches(current, desired);
                string mode = desiredState == "inactive" ? "deactivate" : "update";
                Dictionary<string, object> existingAccess = adapterState["access"] as Dictionary<string, object>;
                if (!equal && (existingAccess == null || !Convert.ToBoolean(existingAccess["write"], CultureInfo.InvariantCulture)))
                {
                    gaps.Add(new FunctionalGap { key = key, kind = kind, adapter = adapter.key, status = "blocked", classification = "verificado_base", action = "blocked", reason = "La configuración difiere, pero el usuario no tiene permiso de escritura sobre el adaptador.", current = JsonUtil.Dictionary("record_id", recordId, "values", current, "hash", JsonUtil.CanonicalHash(current), "write_access", false), desired = desired });
                    continue;
                }
                Dictionary<string, object> operation = equal ? null : JsonUtil.Dictionary("adapter", adapter.key, "mode", mode, "match_name", matchName, "record_id", recordId, "expected_current_hash", JsonUtil.CanonicalHash(current), "desired_values", desired, "previous_snapshot", ProtectedSnapshot.Protect(current));
                gaps.Add(new FunctionalGap { key = key, kind = kind, adapter = adapter.key, status = equal ? "compliant" : "gap", classification = "verificado_base", action = equal ? "none" : "confirmed_package", reason = equal ? "La configuración coincide con el objetivo." : "La configuración existente difiere y puede restaurarse desde un snapshot protegido.", current = JsonUtil.Dictionary("record_id", recordId, "values", current, "hash", JsonUtil.CanonicalHash(current)), desired = desired, proposed_operation = operation });
            }
            return gaps;
        }

        private ChangeOperation CompileOperation(OdooClient client, FunctionalGap gap)
        {
            Dictionary<string, object> proposal = gap.proposed_operation;
            if (proposal == null) throw new ConsultantException("gap_not_executable", "La brecha no contiene una operación compilable.");
            FunctionalAdapterDefinition adapter = Adapter(JsonUtil.GetString(proposal, "adapter", true));
            string mode = JsonUtil.GetString(proposal, "mode", true);
            Dictionary<string, object> parameters = JsonUtil.Dictionary(
                "adapter", adapter.key, "gap_key", gap.key, "match_name", JsonUtil.GetString(proposal, "match_name", true),
                "record_id", JsonUtil.GetLong(proposal, "record_id", false), "expected_current_hash", JsonUtil.GetString(proposal, "expected_current_hash", false),
                "desired_values", proposal["desired_values"], "previous_snapshot", JsonUtil.GetString(proposal, "previous_snapshot", false));
            ChangeOperation operation = new ChangeOperation { operation_id = "op-" + Guid.NewGuid().ToString("N"), type = "functional_" + mode, model = adapter.model, parameters = parameters, status = "pending" };
            ValidateOperationPrecondition(client, operation);
            return operation;
        }

        private Dictionary<string, long> ValidateAllPreconditions(OdooClient client, IEnumerable<ChangeOperation> operations)
        {
            Dictionary<string, long> result = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (ChangeOperation operation in operations)
            {
                long recovered = ValidateOperationPrecondition(client, operation);
                if (recovered > 0) result[operation.operation_id] = recovered;
            }
            return result;
        }

        private long ValidateOperationPrecondition(OdooClient client, ChangeOperation operation)
        {
            FunctionalAdapterDefinition adapter = Adapter(JsonUtil.GetString(operation.parameters, "adapter", true));
            string matchName = JsonUtil.GetString(operation.parameters, "match_name", true);
            Dictionary<string, object> desired = operation.parameters["desired_values"] as Dictionary<string, object>;
            if (operation.type == "functional_create")
            {
                if (!client.CheckAccessRights(adapter.model, "create")) throw new ConsultantException("functional_permission_denied", "El usuario ya no tiene permiso para crear esta configuración.");
                IList<Dictionary<string, object>> rows = client.SearchRead(adapter.model, new object[] { new object[] { "name", "=", matchName } }, adapter.configurableFields.Concat(new[] { "id" }).ToArray(), 2, "id asc");
                if (rows.Count == 0) return 0;
                if (rows.Count == 1 && DesiredMatches(FilterDesired(adapter, rows[0], true), desired)) return Convert.ToInt64(rows[0]["id"], CultureInfo.InvariantCulture);
                throw new ConsultantException("configuration_collision", "La configuración a crear ya existe con valores incompatibles o ambiguos.");
            }
            if (!client.CheckAccessRights(adapter.model, "write")) throw new ConsultantException("functional_permission_denied", "El usuario ya no tiene permiso para modificar esta configuración.");
            long recordId = JsonUtil.GetLong(operation.parameters, "record_id", true);
            IList<Dictionary<string, object>> currentRows = client.Read(adapter.model, new[] { recordId }, adapter.configurableFields);
            if (currentRows.Count != 1) throw new ConsultantException("configuration_changed", "El registro de configuración dejó de existir o no es visible.");
            Dictionary<string, object> current = FilterDesired(adapter, currentRows[0], true);
            if (DesiredMatches(current, desired)) return recordId;
            if (!string.Equals(JsonUtil.CanonicalHash(current), JsonUtil.GetString(operation.parameters, "expected_current_hash", true), StringComparison.Ordinal)) throw new ConsultantException("precondition_changed", "La configuración cambió desde el diagnóstico. No se aplicó nada.");
            return 0;
        }

        private long ApplyOperation(OdooClient client, ChangeOperation operation)
        {
            FunctionalAdapterDefinition adapter = Adapter(JsonUtil.GetString(operation.parameters, "adapter", true));
            Dictionary<string, object> desired = operation.parameters["desired_values"] as Dictionary<string, object>;
            if (operation.type == "functional_create") return client.Create(adapter.model, desired);
            long recordId = JsonUtil.GetLong(operation.parameters, "record_id", true);
            if (!client.Write(adapter.model, new[] { recordId }, desired)) throw new ConsultantException("configuration_write_failed", "Odoo no confirmó la parametrización.");
            return recordId;
        }

        private bool VerifyDesired(OdooClient client, ChangeOperation operation)
        {
            FunctionalAdapterDefinition adapter = Adapter(JsonUtil.GetString(operation.parameters, "adapter", true));
            IList<Dictionary<string, object>> rows = client.Read(adapter.model, new[] { operation.result_id }, adapter.configurableFields);
            return rows.Count == 1 && DesiredMatches(FilterDesired(adapter, rows[0], true), operation.parameters["desired_values"] as Dictionary<string, object>);
        }

        private bool VerifyRollback(OdooClient client, ChangeOperation operation)
        {
            FunctionalAdapterDefinition adapter = Adapter(JsonUtil.GetString(operation.parameters, "adapter", true));
            IList<Dictionary<string, object>> rows = client.Read(adapter.model, new[] { operation.result_id }, adapter.configurableFields);
            if (rows.Count != 1) return false;
            Dictionary<string, object> current = FilterDesired(adapter, rows[0], true);
            if (operation.type == "functional_create")
            {
                string deactField = adapter.archiveField ?? (adapter.fields.ContainsKey("active") ? "active" : (adapter.fields.ContainsKey("is_published") ? "is_published" : null));
                return deactField != null && current.ContainsKey(deactField) && !Convert.ToBoolean(current[deactField], CultureInfo.InvariantCulture);
            }
            Dictionary<string, object> previous = ProtectedSnapshot.Unprotect(JsonUtil.GetString(operation.parameters, "previous_snapshot", true));
            return DesiredMatches(current, previous);
        }

        private static Dictionary<string, object> NormalizeObjective(IDictionary<string, object> objective)
        {
            PolicyCompiler.EnsureOnlyKeys(objective, new[] { "schema_version", "application", "objective", "requirements" });
            if (JsonUtil.GetLong(objective, "schema_version", true) != 1) throw new ConsultantException("unsupported_functional_schema", "El objetivo funcional debe usar schema_version 1.");
            string application = JsonUtil.GetString(objective, "application", true).ToLowerInvariant();
            FunctionalApplicationDefinition definition = Application(application);
            string description = JsonUtil.GetString(objective, "objective", true);
            if (description.Length > 1000) throw new ConsultantException("invalid_functional_objective", "El objetivo supera 1.000 caracteres.");
            List<Dictionary<string, object>> input = ObjectList(objective.ContainsKey("requirements") ? objective["requirements"] : null);
            if (input.Count < 1 || input.Count > 30) throw new ConsultantException("invalid_functional_objective", "El objetivo debe contener entre 1 y 30 requisitos.");
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            List<Dictionary<string, object>> normalized = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> raw in input)
            {
                string kind = JsonUtil.GetString(raw, "kind", true).ToLowerInvariant();
                string key = JsonUtil.GetString(raw, "key", true).ToLowerInvariant();
                if (!KeyPattern.IsMatch(key) || !keys.Add(key)) throw new ConsultantException("invalid_requirement_key", "Cada requisito necesita una clave única y segura.");
                if (kind == "module")
                {
                    PolicyCompiler.EnsureOnlyKeys(raw, new[] { "key", "kind", "module", "expected_state" });
                    string module = JsonUtil.GetString(raw, "module", true).ToLowerInvariant();
                    if (!definition.modules.Contains(module, StringComparer.Ordinal)) throw new ConsultantException("module_not_in_application", "El módulo " + module + " no pertenece al catálogo de " + application + ".");
                    string state = JsonUtil.GetString(raw, "expected_state", true).ToLowerInvariant();
                    if (state != "installed" && state != "uninstalled") throw new ConsultantException("invalid_expected_state", "expected_state debe ser installed o uninstalled.");
                    normalized.Add(JsonUtil.Dictionary("key", key, "kind", kind, "module", module, "expected_state", state));
                }
                else if (kind == "decision")
                {
                    PolicyCompiler.EnsureOnlyKeys(raw, new[] { "key", "kind", "question", "resolved", "decision" });
                    string question = SafeText(JsonUtil.GetString(raw, "question", true), 500, "question");
                    bool resolved = JsonUtil.GetBool(raw, "resolved", false);
                    string decision = JsonUtil.GetString(raw, "decision", false);
                    if (resolved && string.IsNullOrWhiteSpace(decision)) throw new ConsultantException("invalid_decision", "Una decisión resuelta debe indicar su resultado.");
                    normalized.Add(JsonUtil.Dictionary("key", key, "kind", kind, "question", question, "resolved", resolved, "decision", decision == null ? null : SafeText(decision, 1000, "decision")));
                }
                else if (kind == "configuration")
                {
                    PolicyCompiler.EnsureOnlyKeys(raw, new[] { "key", "kind", "adapter", "match_name", "desired_state", "desired" });
                    string adapterKey = JsonUtil.GetString(raw, "adapter", true).ToLowerInvariant();
                    if (!definition.adapters.Contains(adapterKey, StringComparer.Ordinal)) throw new ConsultantException("adapter_not_in_application", "El adaptador " + adapterKey + " no pertenece a " + application + ".");
                    string matchName = SafeText(JsonUtil.GetString(raw, "match_name", true), 200, "match_name");
                    string desiredState = (JsonUtil.GetString(raw, "desired_state", false) ?? "present").ToLowerInvariant();
                    if (desiredState != "present" && desiredState != "inactive") throw new ConsultantException("invalid_desired_state", "desired_state debe ser present o inactive.");
                    Dictionary<string, object> desired = raw.ContainsKey("desired") ? raw["desired"] as Dictionary<string, object> : new Dictionary<string, object>();
                    NormalizeDesired(Adapter(adapterKey), matchName, desiredState, desired);
                    normalized.Add(JsonUtil.Dictionary("key", key, "kind", kind, "adapter", adapterKey, "match_name", matchName, "desired_state", desiredState, "desired", desired));
                }
                else throw new ConsultantException("unsupported_requirement_kind", "El requisito debe ser module, configuration o decision.");
            }
            return JsonUtil.Dictionary("schema_version", 1, "application", application, "objective", description, "requirements", normalized);
        }

        private static Dictionary<string, object> NormalizeDesired(FunctionalAdapterDefinition adapter, string matchName, string desiredState, IDictionary<string, object> input)
        {
            Dictionary<string, object> source = input == null ? new Dictionary<string, object>() : new Dictionary<string, object>(input, StringComparer.Ordinal);
            PolicyCompiler.EnsureOnlyKeys(source, adapter.configurableFields.Where(delegate(string field) { return field != "name" && field != "active"; }));
            Dictionary<string, object> desired = new Dictionary<string, object>(StringComparer.Ordinal) { { "name", matchName } };
            if (adapter.fields.ContainsKey("active")) desired["active"] = desiredState == "present";
            else if (adapter.fields.ContainsKey("is_published") && desiredState == "inactive") desired["is_published"] = false;
            foreach (string field in adapter.configurableFields)
            {
                if (field == "name" || (field == "active" && desired.ContainsKey("active")) || !source.ContainsKey(field)) continue;
                string type = adapter.fields[field];
                if (type == "integer")
                {
                    long value;
                    if (!long.TryParse(Convert.ToString(source[field], CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < 0 || value > 9999) throw new ConsultantException("invalid_configuration_value", field + " debe estar entre 0 y 9999.");
                    desired[field] = value;
                }
                else if (type == "boolean")
                {
                    if (!(source[field] is bool)) throw new ConsultantException("invalid_configuration_value", field + " debe ser booleano.");
                    desired[field] = source[field];
                }
                else desired[field] = SafeText(Convert.ToString(source[field], CultureInfo.InvariantCulture), 500, field);
            }
            ValidateDesired(adapter, desired, false);
            return desired;
        }

        private static void ValidateDesired(FunctionalAdapterDefinition adapter, IDictionary<string, object> desired, bool package)
        {
            if (desired == null) throw new ConsultantException(package ? "tool_contract_violation" : "invalid_configuration_value", "Faltan valores deseados.");
            PolicyCompiler.EnsureOnlyKeys(desired, adapter.configurableFields);
            if (!desired.ContainsKey("name")) throw new ConsultantException(package ? "tool_contract_violation" : "invalid_configuration_value", "La configuración debe incluir name.");
            if (adapter.fields.ContainsKey("active"))
            {
                if (!desired.ContainsKey("active")) throw new ConsultantException(package ? "tool_contract_violation" : "invalid_configuration_value", "La configuración debe incluir active.");
                if (!(desired["active"] is bool)) throw new ConsultantException(package ? "tool_contract_violation" : "invalid_configuration_value", "active debe ser booleano.");
            }
            SafeText(Convert.ToString(desired["name"], CultureInfo.InvariantCulture), 200, "name");
            foreach (KeyValuePair<string, object> pair in desired)
            {
                string type = adapter.fields[pair.Key];
                if (type == "boolean" && !(pair.Value is bool)) throw new ConsultantException(package ? "tool_contract_violation" : "invalid_configuration_value", pair.Key + " debe ser booleano.");
                if (type == "integer")
                {
                    long value;
                    if (pair.Value is string || !long.TryParse(Convert.ToString(pair.Value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < 0 || value > 9999) throw new ConsultantException(package ? "tool_contract_violation" : "invalid_configuration_value", pair.Key + " debe ser un entero entre 0 y 9999.");
                }
                if (type == "char") SafeText(Convert.ToString(pair.Value, CultureInfo.InvariantCulture), pair.Key == "name" ? 200 : 500, pair.Key);
            }
        }

        private static Dictionary<string, object> FilterDesired(FunctionalAdapterDefinition adapter, IDictionary<string, object> source, bool includeDefaults)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (string field in adapter.configurableFields)
            {
                if (source != null && source.ContainsKey(field)) result[field] = source[field];
                else if (includeDefaults && field == "active" && adapter.fields.ContainsKey("active")) result[field] = true;
            }
            return result;
        }

        private static bool DesiredMatches(IDictionary<string, object> current, IDictionary<string, object> desired)
        {
            if (current == null || desired == null) return false;
            foreach (KeyValuePair<string, object> pair in desired)
            {
                if (!current.ContainsKey(pair.Key) || JsonUtil.CanonicalHash(current[pair.Key]) != JsonUtil.CanonicalHash(pair.Value)) return false;
            }
            return true;
        }

        private string CapabilityHashForOperations(OdooClient client, ConnectionProfile profile, IEnumerable<ChangeOperation> operations)
        {
            List<object> values = new List<object>();
            foreach (string adapterKey in operations.Select(delegate(ChangeOperation operation) { return JsonUtil.GetString(operation.parameters, "adapter", true); }).Distinct(StringComparer.Ordinal).OrderBy(delegate(string value) { return value; }, StringComparer.Ordinal))
            {
                FunctionalAdapterDefinition adapter = Adapter(adapterKey);
                Dictionary<string, object> fields = client.FieldsGet(adapter.model, new[] { "type", "required", "readonly" });
                Dictionary<string, object> selected = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (string field in adapter.configurableFields) selected[field] = fields.ContainsKey(field) ? fields[field] : null;
                values.Add(JsonUtil.Dictionary("adapter", adapter.key, "model", adapter.model, "fields", selected, "access", Access(client, adapter.model)));
            }
            return JsonUtil.CanonicalHash(JsonUtil.Dictionary("profile_uid", profile.uid, "server_series", profile.server_series, "adapters", values));
        }

        private FunctionalDiagnostic LoadDiagnostic(string diagnosticId)
        {
            if (!DiagnosticIdPattern.IsMatch(diagnosticId ?? string.Empty)) throw new ConsultantException("invalid_diagnostic_id", "El identificador de diagnóstico no es válido.");
            FunctionalDiagnostic diagnostic = JsonUtil.ReadJsonFile<FunctionalDiagnostic>(project.FunctionalDiagnosticPath(null, diagnosticId, false));
            if (diagnostic.schema_version != 1 || diagnostic.policy_version != PolicyVersion) throw new ConsultantException("unsupported_functional_schema", "El diagnóstico funcional no es compatible.");
            if (!string.Equals(diagnostic.client_slug, project.Config.client_slug, StringComparison.Ordinal)) throw new ConsultantException("diagnostic_tenant_mismatch", "El diagnóstico pertenece a otro cliente.");
            if (!string.Equals(diagnostic.integrity_hash, DiagnosticIntegrityHash(diagnostic), StringComparison.Ordinal)) throw new ConsultantException("diagnostic_integrity_failed", "El diagnóstico fue modificado después de generarse.");
            return diagnostic;
        }

        private static HashSet<string> NormalizeSelection(IEnumerable<string> values)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);
            if (values == null) return result;
            foreach (string value in values)
            {
                string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
                if (!KeyPattern.IsMatch(normalized) || !result.Add(normalized)) throw new ConsultantException("invalid_gap_selection", "La selección de brechas contiene una clave inválida o repetida.");
                if (result.Count > 20) throw new ConsultantException("invalid_gap_selection", "Solo se pueden seleccionar hasta 20 brechas.");
            }
            return result;
        }

        private static Dictionary<string, object> GapPrecondition(FunctionalGap gap)
        {
            return JsonUtil.Dictionary("key", gap.key, "status", gap.status, "current", gap.current, "action", gap.action);
        }

        private static object DiagnosticSummary(FunctionalDiagnostic diagnostic)
        {
            List<object> gaps = new List<object>();
            foreach (FunctionalGap gap in diagnostic.gaps)
            {
                Dictionary<string, object> proposal = null;
                if (gap.proposed_operation != null)
                {
                    proposal = new Dictionary<string, object>(gap.proposed_operation, StringComparer.Ordinal);
                    proposal.Remove("previous_snapshot");
                }
                gaps.Add(JsonUtil.Dictionary("key", gap.key, "kind", gap.kind, "status", gap.status, "classification", gap.classification, "adapter", gap.adapter, "module", gap.module, "question", gap.question, "action", gap.action, "reason", gap.reason, "current", gap.current, "desired", gap.desired, "proposed_operation", proposal));
            }
            return JsonUtil.Dictionary(
                "diagnostic_id", diagnostic.id, "client_slug", diagnostic.client_slug, "environment", diagnostic.environment, "application", diagnostic.application,
                "status", diagnostic.status, "objective", diagnostic.objective, "created_at", diagnostic.created_at, "policy_version", diagnostic.policy_version,
                "inventory_hash", diagnostic.inventory_hash, "capability_hash", diagnostic.capability_hash, "precondition_hash", diagnostic.precondition_hash,
                "counts", JsonUtil.Dictionary("requirements", diagnostic.gaps.Count, "compliant", diagnostic.gaps.Count(delegate(FunctionalGap gap) { return gap.status == "compliant"; }), "executable_gaps", diagnostic.gaps.Count(delegate(FunctionalGap gap) { return gap.action == "confirmed_package"; }), "runbooks", diagnostic.gaps.Count(delegate(FunctionalGap gap) { return gap.action == "runbook"; }), "decisions", diagnostic.gaps.Count(delegate(FunctionalGap gap) { return gap.action == "consultant_decision"; }), "blocked", diagnostic.gaps.Count(delegate(FunctionalGap gap) { return gap.status == "blocked"; })),
                "matrix", gaps, "warnings", diagnostic.warnings,
                "next_step", diagnostic.gaps.Any(delegate(FunctionalGap gap) { return gap.action == "confirmed_package"; }) ? "Revisa la matriz y usa funcional_preparar_parametrizacion con este diagnostic_id." : "Resuelve las decisiones o runbooks pendientes; no hay parametrizaciones automáticas disponibles.");
        }

        private static object PackageSummary(ChangeSet change, bool includeConfirmation)
        {
            List<object> operations = change.operations.Select(delegate(ChangeOperation operation)
            {
                Dictionary<string, object> safeParameters = new Dictionary<string, object>(operation.parameters, StringComparer.Ordinal);
                safeParameters.Remove("previous_snapshot");
                return (object)JsonUtil.Dictionary("operation_id", operation.operation_id, "type", operation.type, "model", operation.model, "parameters", safeParameters, "status", operation.status, "result_id", operation.result_id, "verification", operation.verification);
            }).ToList();
            Dictionary<string, object> summary = JsonUtil.Dictionary("package_id", change.id, "category", change.category, "environment", change.environment, "status", change.status, "application", change.model, "target_models", change.target_models, "created_at", change.created_at, "expires_at", change.expires_at, "objective", change.objective, "impact", change.impact, "rollback_strategy", change.rollback_strategy, "policy_version", change.policy_version, "capability_hash", change.capability_hash, "precondition_hash", change.precondition_hash, "operations", operations, "warnings", change.warnings, "last_error", change.last_error);
            if (includeConfirmation) summary["required_confirmation"] = "APLICAR " + change.id;
            return summary;
        }

        private static string DiagnosticIntegrityHash(FunctionalDiagnostic diagnostic)
        {
            return JsonUtil.CanonicalHash(JsonUtil.Dictionary("schema_version", diagnostic.schema_version, "id", diagnostic.id, "client_slug", diagnostic.client_slug, "diagnostic_slug", diagnostic.diagnostic_slug, "environment", diagnostic.environment, "application", diagnostic.application, "objective", diagnostic.objective, "status", diagnostic.status, "created_at", diagnostic.created_at, "profile_fingerprint", diagnostic.profile_fingerprint, "server_series", diagnostic.server_series, "policy_version", diagnostic.policy_version, "inventory_hash", diagnostic.inventory_hash, "precondition_hash", diagnostic.precondition_hash, "capability_hash", diagnostic.capability_hash, "objective_hash", diagnostic.objective_hash, "requirements", diagnostic.requirements, "gaps", diagnostic.gaps));
        }

        private static string PackageIntegrityHash(ChangeSet change)
        {
            List<object> operations = change.operations.Select(delegate(ChangeOperation operation) { return (object)JsonUtil.Dictionary("operation_id", operation.operation_id, "type", operation.type, "model", operation.model, "parameters", operation.parameters); }).ToList();
            return JsonUtil.CanonicalHash(JsonUtil.Dictionary("schema_version", change.schema_version, "id", change.id, "client_slug", change.client_slug, "environment", change.environment, "category", change.category, "risk", change.risk, "model", change.model, "target_models", change.target_models, "created_at", change.created_at, "expires_at", change.expires_at, "precondition_hash", change.precondition_hash, "profile_fingerprint", change.profile_fingerprint, "policy_version", change.policy_version, "objective", change.objective, "contract_hash", change.contract_hash, "capability_hash", change.capability_hash, "impact", change.impact, "rollback_strategy", change.rollback_strategy, "preconditions", change.preconditions, "validations", change.validations, "operations", operations));
        }

        private static FunctionalApplicationDefinition Application(string key)
        {
            FunctionalApplicationDefinition result;
            if (!Applications.TryGetValue((key ?? string.Empty).Trim().ToLowerInvariant(), out result)) throw new ConsultantException("unsupported_functional_application", "La aplicación debe ser crm, ventas, compras, inventario, contabilidad, marketing, sitio_web o ecommerce.");
            return result;
        }

        private static FunctionalAdapterDefinition Adapter(string key)
        {
            FunctionalAdapterDefinition result;
            if (!Adapters.TryGetValue((key ?? string.Empty).Trim().ToLowerInvariant(), out result)) throw new ConsultantException("unsupported_functional_adapter", "El adaptador funcional no pertenece al catálogo cerrado.");
            return result;
        }

        private static Dictionary<string, object> Access(OdooClient client, string model)
        {
            return JsonUtil.Dictionary("read", SafeAccess(client, model, "read"), "create", SafeAccess(client, model, "create"), "write", SafeAccess(client, model, "write"));
        }

        private static bool SafeAccess(OdooClient client, string model, string operation)
        {
            try { return client.CheckAccessRights(model, operation); }
            catch (ConsultantException) { return false; }
        }

        private void ValidateProfileVersion(ConnectionProfile profile)
        {
            string expected = ProjectStore.NormalizeOdooVersion(project.Config.odoo_version);
            string actual = ProjectStore.NormalizeOdooVersion(profile.server_series);
            if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new ConsultantException("project_version_mismatch", "La versión del perfil no coincide con proyecto.yaml.");
        }

        private OdooClient Open(string environment)
        {
            return project.OpenClient(environment, 30000);
        }

        private static string SafeText(string value, int maximum, string field)
        {
            string clean = (value ?? string.Empty).Trim();
            if (clean.Length == 0 || clean.Length > maximum || clean.Any(delegate(char character) { return char.IsControl(character) && character != '\t'; })) throw new ConsultantException("invalid_functional_text", field + " está vacío, excede el límite o contiene caracteres de control.");
            return clean;
        }

        private static List<Dictionary<string, object>> ObjectList(object value)
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            IEnumerable sequence = value as IEnumerable;
            if (sequence != null && !(value is string))
            {
                foreach (object item in sequence)
                {
                    Dictionary<string, object> dictionary = item as Dictionary<string, object>;
                    if (dictionary == null) throw new ConsultantException("invalid_functional_objective", "Cada elemento debe ser un objeto.");
                    result.Add(dictionary);
                }
            }
            return result;
        }

        private static string SafeError(Exception error)
        {
            return Redactor.SafeError(error);
        }
    }
}
