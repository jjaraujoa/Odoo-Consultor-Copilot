using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace OdooConsultor
{
    public sealed class DemoService
    {
        private readonly ProjectStore project;
        private static readonly string[] MarketingModules = new[] { "crm", "utm", "mass_mailing", "marketing_automation", "social", "website", "website_crm", "survey" };
        private static readonly HashSet<string> QuestionTypes = new HashSet<string>(new[] { "simple_choice", "multiple_choice", "text_box", "char_box", "numerical_box", "scale", "date", "datetime", "matrix" }, StringComparer.Ordinal);
        private static readonly HashSet<string> AutomationTriggers = new HashSet<string>(new[] { "begin", "mail_open", "mail_not_open", "mail_click", "mail_not_click" }, StringComparer.Ordinal);

        public DemoService(ProjectStore store) { project = store; }

        public object Inventory(string environment, string application)
        {
            environment = ProjectStore.NormalizeEnvironment(environment);
            string app = NormalizeApplication(application);
            Stopwatch timer = Stopwatch.StartNew();
            using (OdooClient client = Open(environment))
            {
                Dictionary<string, string> states = ReadModuleStates(client);
                List<object> modules = states.Select(x => (object)JsonUtil.Dictionary("module", x.Key, "installed", x.Value == "installed", "state", x.Value)).ToList();
                List<object> adapters = new List<object>();
                foreach (DemoAdapterContract contract in DemoAdapterCatalog.All)
                {
                    string missingModule = contract.modules.FirstOrDefault(x => !states.ContainsKey(x) || states[x] != "installed");
                    if (missingModule != null)
                    {
                        adapters.Add(JsonUtil.Dictionary("adapter", contract.key, "model", contract.model, "available", false, "reason", "module_not_installed", "module", missingModule));
                        continue;
                    }
                    try
                    {
                        Dictionary<string, object> fields = Fields(client, contract.model);
                        bool read = SafeAccess(client, contract.model, "read");
                        bool create = SafeAccess(client, contract.model, "create");
                        bool write = SafeAccess(client, contract.model, "write");
                        string[] missingFields = contract.required_contract_fields.Where(x => !fields.ContainsKey(x)).ToArray();
                        Dictionary<string, object> descriptor = DemoAdapterCatalog.CapabilityDescriptor(contract, fields, read, create, write);
                        adapters.Add(JsonUtil.Dictionary(
                            "adapter", contract.key, "model", contract.model, "available", read && create && write && missingFields.Length == 0,
                            "read", read, "create", create, "write", write,
                            "missing_required_fields", missingFields,
                            "allowed_fields", contract.allowed_fields,
                            "cleanup_strategy", contract.cleanup_strategy,
                            "capability_hash", JsonUtil.CanonicalHash(descriptor)));
                    }
                    catch (ConsultantException error) { adapters.Add(JsonUtil.Dictionary("adapter", contract.key, "model", contract.model, "available", false, "reason", error.Code)); }
                }
                DemoRegistryCapability registry = DemoOwnershipRegistry.Probe(client);
                timer.Stop();
                AuditLog.Append(project, environment, "demo_inventariar_aplicacion", null, "success", "Inventario comercial de " + app + " completado", timer.ElapsedMilliseconds);
                return JsonUtil.Dictionary(
                    "schema_version", 2, "application", app, "environment", environment,
                    "source_tier", "verificado_base", "module_states_hash", JsonUtil.CanonicalHash(states),
                    "modules", modules.ToArray(), "adapters", adapters.ToArray(), "external_effects_enabled", false,
                    "ownership_registry", JsonUtil.Dictionary("strategy", "hybrid", "status", registry.status, "model", DemoOwnershipRegistry.RegistryModel, "read", registry.read, "create", registry.create, "capability_hash", registry.capability_hash),
                    "future_runbook_only", new[] { "social_publication", "whatsapp_send", "sms_send", "external_ads", "analytics_connectors" });
            }
        }

        public object Prepare(string environment, string demoSlug, string application, string objective, IList<string> requirements)
        {
            return Prepare(environment, demoSlug, application, objective, requirements, null);
        }

        public object Prepare(string environment, string demoSlug, string application, string objective, IList<string> requirements, IDictionary<string, object> configuration)
        {
            environment = ProjectStore.NormalizeEnvironment(environment);
            if (environment != "staging") throw new ConsultantException("production_write_blocked", "Los escenarios con datos sintéticos solo se preparan para staging.");
            string app = NormalizeApplication(application);
            string slug = ProjectStore.NormalizeSlug(demoSlug, "demo");
            string cleanObjective = CleanText(objective, 500, "objetivo");
            List<string> cleanRequirements = CleanList(requirements, 1, 20, 300, "requerimientos");
            ValidateConfiguration(configuration);
            string directory = project.DemoDirectory(slug, true);
            string lockPath = Path.Combine(directory, "source-lock.json");
            if (!File.Exists(lockPath)) throw new ConsultantException("source_lock_required", "Debes crear primero el bloqueo de fuentes de esta demostración.");
            Dictionary<string, object> sourceLock = JsonUtil.ReadJsonFile<Dictionary<string, object>>(lockPath);
            string lockHash = JsonUtil.GetString(sourceLock, "lock_hash", true);
            string lockFileHash = JsonUtil.Sha256(File.ReadAllText(lockPath, Encoding.UTF8));
            ConnectionProfile profile = project.GetProfile(environment, true);
            if (!string.Equals(profile.server_series, project.Config.odoo_version, StringComparison.Ordinal)) throw new ConsultantException("odoo_version_mismatch", "El perfil verificado no coincide con la versión declarada del proyecto.");
            string id = "demo-" + Guid.NewGuid().ToString("N");
            int demoNumber = project.NextDemoNumber(environment);
            string demoLabel = "DEMO " + demoNumber.ToString(CultureInfo.InvariantCulture);
            string demoKey = "d" + demoNumber.ToString(CultureInfo.InvariantCulture);
            List<DemoComponent> components = new List<DemoComponent>();
            List<string> warnings = new List<string>();
            List<Dictionary<string, object>> referencePlan = new List<Dictionary<string, object>>();
            Dictionary<string, string> capabilityParts = new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<string, string> moduleStates;
            DemoRegistryCapability registryCapability;
            using (OdooClient client = Open(environment))
            {
                moduleStates = ReadModuleStates(client);
                registryCapability = DemoOwnershipRegistry.Probe(client);
                AddCoreMarketing(client, moduleStates, components, capabilityParts, warnings, demoKey, configuration);
                AddWebsite(client, moduleStates, components, capabilityParts, warnings, referencePlan, demoKey, configuration);
                AddSurvey(client, moduleStates, components, capabilityParts, warnings, demoKey, configuration);
                AddMailingAndAutomation(client, moduleStates, components, capabilityParts, warnings, demoKey, directory, configuration);
            }
            if (!registryCapability.available) warnings.Add("ownership_registry: el usuario no puede crear identidades técnicas; se usará protección local DPAPI.");
            if (components.Count == 0) throw new ConsultantException("demo_no_components", "La base no ofrece ningún adaptador compatible con el escenario solicitado.");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DemoScenario scenario = new DemoScenario
            {
                schema_version = 4, id = id, client_slug = project.Config.client_slug, demo_slug = slug,
                demo_number = demoNumber, demo_label = demoLabel, delivery_mode = "database_records", ownership_registry = "hybrid",
                registry_status = registryCapability.status, registry_capability_hash = registryCapability.capability_hash,
                environment = environment, application = app, objective = cleanObjective, requirements = cleanRequirements,
                status = "prepared", created_at = now.ToString("o", CultureInfo.InvariantCulture),
                expires_at = now.AddMinutes(project.Config.demo_confirmation_minutes).ToString("o", CultureInfo.InvariantCulture),
                capability_hash = JsonUtil.CanonicalHash(capabilityParts), source_lock_hash = lockHash,
                source_lock_file_hash = lockFileHash, profile_fingerprint = profile.credential_fingerprint,
                server_series = profile.server_series, adapter_contract_version = DemoAdapterCatalog.ScenarioContractVersion,
                modules_hash = JsonUtil.CanonicalHash(moduleStates), module_states = moduleStates,
                components = components, steps = CommercialSteps(components), reference_plan = referencePlan,
                initial_states = components.Select(x => JsonUtil.Dictionary("component", x.key, "state", x.initial_state)).ToList(),
                verification_procedure = components.Select(x => JsonUtil.Dictionary("component", x.key, "adapter", x.adapter, "expected", x.expected_result)).ToList(),
                cleanup_procedure = components.Select(x => JsonUtil.Dictionary("component", x.key, "strategy", x.cleanup_strategy)).ToList(),
                record_plan = components.Select(x => JsonUtil.Dictionary("component", x.key, "adapter", x.adapter, "model", DemoAdapterCatalog.Get(x.adapter).model, "operation", "create", "references", x.references)).ToList(),
                readiness_criteria = new List<string> { "staging_records_created", "functional_relationships_verified", "external_effects_disabled", "idempotent_retry", "safe_cleanup_defined" },
                external_effects = new List<string> { "email_send:disabled", "sms_send:disabled", "whatsapp_send:disabled", "social_publish:disabled", "webhooks:disabled", "paid_ads:manual_only" },
                manual_operations = new List<string> { "Enviar el formulario web con datos ficticios y correo example.invalid.", "Navegar y ensayar la interfaz en modo de solo lectura.", "Conectar redes, WhatsApp, publicidad y analítica externa solo mediante un runbook futuro." },
                warnings = warnings, protected_records = null
            };
            scenario.integrity_hash = Integrity(scenario);
            JsonUtil.WriteJsonFile(Path.Combine(directory, id + ".scenario.json"), scenario);
            WriteArtifacts(directory, scenario, sourceLock);
            AuditLog.Append(project, environment, "demo_preparar_escenario", id, "success", "Escenario comercial v4 preparado con " + components.Count + " registros planificados", 0);
            return PublicScenario(scenario, "Revisa la vista previa y confirma con CREAR-DEMO " + id + ".");
        }

        public object PrepareOperational(string environment, string demoSlug, string objective, IList<string> requirements, IList<object> requestedRecords)
        {
            environment = ProjectStore.NormalizeEnvironment(environment);
            if (environment != "staging") throw new ConsultantException("production_write_blocked", "Los registros operativos de demostración solo se preparan para staging.");
            string slug = ProjectStore.NormalizeSlug(demoSlug, "demo");
            string cleanObjective = CleanText(objective, 1000, "objetivo");
            List<string> cleanRequirements = CleanList(requirements, 1, 100, 400, "requerimientos");
            List<Dictionary<string, object>> specifications = new List<Dictionary<string, object>>();
            foreach (object raw in requestedRecords ?? new object[0])
            {
                Dictionary<string, object> item = raw as Dictionary<string, object>;
                if (item == null) throw new ConsultantException("invalid_operational_demo", "Cada registro operativo debe ser un objeto cerrado.");
                specifications.Add(item);
            }
            if (specifications.Count < 1 || specifications.Count > 100) throw new ConsultantException("invalid_operational_demo", "Debes indicar entre 1 y 100 registros operativos por escenario.");

            string directory = project.DemoDirectory(slug, true);
            string lockPath = Path.Combine(directory, "source-lock.json");
            if (!File.Exists(lockPath)) throw new ConsultantException("source_lock_required", "Debes crear primero el bloqueo de fuentes de esta demostración.");
            Dictionary<string, object> sourceLock = JsonUtil.ReadJsonFile<Dictionary<string, object>>(lockPath);
            string lockHash = JsonUtil.GetString(sourceLock, "lock_hash", true);
            string lockFileHash = JsonUtil.Sha256(File.ReadAllText(lockPath, Encoding.UTF8));
            ConnectionProfile profile = project.GetProfile(environment, true);
            if (!string.Equals(profile.server_series, project.Config.odoo_version, StringComparison.Ordinal)) throw new ConsultantException("odoo_version_mismatch", "El perfil verificado no coincide con la versión declarada del proyecto.");

            string id = "demo-" + Guid.NewGuid().ToString("N");
            int demoNumber = project.NextDemoNumber(environment);
            List<DemoComponent> components = new List<DemoComponent>();
            List<string> warnings = new List<string>();
            List<Dictionary<string, object>> referencePlan = new List<Dictionary<string, object>>();
            Dictionary<string, string> capabilities = new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<string, string> moduleStates;
            DemoRegistryCapability registryCapability;
            using (OdooClient client = Open(environment))
            {
                moduleStates = ReadModuleStates(client);
                registryCapability = DemoOwnershipRegistry.Probe(client);
                HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (Dictionary<string, object> specification in specifications)
                {
                    string key = NormalizeComponentKey(JsonUtil.GetString(specification, "clave", true));
                    if (!keys.Add(key)) throw new ConsultantException("demo_component_duplicate", "La clave " + key + " está repetida.");
                    DemoComponent component = CompileOperationalComponent(specification, key, referencePlan, directory);
                    foreach (string dependency in component.references.Values.Where(x => !x.StartsWith("runtime:", StringComparison.Ordinal) && !x.StartsWith("lookup:", StringComparison.Ordinal) && !x.StartsWith("analytic:", StringComparison.Ordinal) && !x.StartsWith("analytic_runtime:", StringComparison.Ordinal)))
                        if (!keys.Contains(dependency)) throw new ConsultantException("demo_dependency_order_invalid", "La dependencia " + dependency + " debe aparecer antes que " + key + ".");
                    foreach (string analytic in component.references.Values.Where(x => x.StartsWith("analytic:", StringComparison.Ordinal)))
                    {
                        string[] parts = analytic.Split(':');
                        if (parts.Length != 3 || !keys.Contains(parts[1])) throw new ConsultantException("demo_dependency_order_invalid", "La cuenta analítica debe aparecer antes que " + key + ".");
                    }
                    int before = components.Count;
                    AddIfAvailable(client, moduleStates, components, capabilities, warnings, component.key, component.adapter, component.values, component.references, component.initial_state, component.expected_result);
                    if (components.Count != before + 1) throw new ConsultantException("demo_operational_adapter_unavailable", "El adaptador " + component.adapter + " no está disponible en esta base. Instala el módulo o corrige permisos antes de preparar la demo.");
                }
            }
            if (!registryCapability.available) warnings.Add("ownership_registry: el usuario no puede crear identidades técnicas; se usará protección local DPAPI.");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DemoScenario scenario = new DemoScenario
            {
                schema_version = 5, id = id, client_slug = project.Config.client_slug, demo_slug = slug,
                demo_number = demoNumber, demo_label = "DEMO " + demoNumber.ToString(CultureInfo.InvariantCulture),
                delivery_mode = "database_records", ownership_registry = "hybrid", registry_status = registryCapability.status,
                registry_capability_hash = registryCapability.capability_hash, environment = environment, application = "operaciones",
                objective = cleanObjective, requirements = cleanRequirements, status = "prepared",
                created_at = now.ToString("o", CultureInfo.InvariantCulture),
                expires_at = now.AddMinutes(project.Config.demo_confirmation_minutes).ToString("o", CultureInfo.InvariantCulture),
                capability_hash = JsonUtil.CanonicalHash(capabilities), source_lock_hash = lockHash, source_lock_file_hash = lockFileHash,
                profile_fingerprint = profile.credential_fingerprint, server_series = profile.server_series,
                adapter_contract_version = DemoAdapterCatalog.ScenarioContractVersion, modules_hash = JsonUtil.CanonicalHash(moduleStates),
                module_states = moduleStates, components = components, steps = OperationalSteps(components), reference_plan = referencePlan,
                initial_states = components.Select(x => JsonUtil.Dictionary("component", x.key, "state", x.initial_state)).ToList(),
                verification_procedure = components.Select(x => JsonUtil.Dictionary("component", x.key, "adapter", x.adapter, "expected", x.expected_result)).ToList(),
                cleanup_procedure = components.Select(x => JsonUtil.Dictionary("component", x.key, "strategy", x.cleanup_strategy)).ToList(),
                record_plan = components.Select(x => JsonUtil.Dictionary("component", x.key, "adapter", x.adapter, "model", DemoAdapterCatalog.Get(x.adapter).model, "operation", "create", "references", x.references)).ToList(),
                readiness_criteria = new List<string> { "staging_records_created", "functional_relationships_verified", "draft_states_verified", "idempotent_retry", "safe_cleanup_or_staging_rebuild_defined" },
                external_effects = new List<string> { "email_send:disabled", "external_payment:disabled", "bank_connector:disabled", "production_write:disabled", "external_connectors:disabled" },
                manual_operations = new List<string> { "Confirmar, contabilizar, reservar o validar documentos únicamente mediante demo_preparar_acciones y EJECUTAR-DEMO <id>.", "Los asistentes adicionales que devuelva Odoo requieren intervención del consultor; no se repite una acción ambigua.", "Los documentos contables y logísticos creados se conservan para la demo; la reconstrucción del staging es la recuperación final." },
                warnings = warnings, protected_records = null
            };
            scenario.integrity_hash = Integrity(scenario);
            JsonUtil.WriteJsonFile(Path.Combine(directory, id + ".scenario.json"), scenario);
            WriteOperationalArtifacts(directory, scenario);
            AuditLog.Append(project, environment, "demo_preparar_registros_operativos", id, "success", "Escenario operativo preparado; records=" + components.Count.ToString(CultureInfo.InvariantCulture) + ".", 0);
            return PublicScenario(scenario, "Revisa los documentos y relaciones. Confirma con CREAR-DEMO " + id + ".");
        }

        public object PrepareMailingReplacement(string previousScenarioId, IDictionary<string, object> imageAsset, string personalizationVariable)
        {
            DemoScenario previous = Load(previousScenarioId);
            EnsureIntegrity(previous);
            if (previous.environment != "staging") throw new ConsultantException("production_write_blocked", "Los reemplazos de demo solo se preparan para staging.");
            Dictionary<string, object> previousRecords = ProtectedSnapshot.Unprotect(previous.protected_records) ?? new Dictionary<string, object>(StringComparer.Ordinal);
            List<DemoComponent> targets = (previous.components ?? new List<DemoComponent>()).Where(x => x.adapter == "mailing_draft" && previousRecords.ContainsKey(x.key)).ToList();
            if (targets.Count == 0) throw new ConsultantException("demo_replacement_target_missing", "El escenario anterior no contiene mailings protegidos para reemplazar.");
            string directory = project.DemoDirectory(previous.demo_slug, false);
            DemoAssetDescriptor asset = MailEditorContract.ValidateAsset(directory, imageAsset);
            string variable = string.IsNullOrWhiteSpace(personalizationVariable) ? "recipient_name" : personalizationVariable.Trim();
            if (variable != "none" && variable != "recipient_name") throw new ConsultantException("mail_personalization_invalid", "Solo se admite none o recipient_name.");
            string lockPath = Path.Combine(directory, "source-lock.json");
            if (!File.Exists(lockPath)) throw new ConsultantException("source_lock_required", "La demostración no conserva su bloqueo de fuentes.");
            Dictionary<string, object> sourceLock = JsonUtil.ReadJsonFile<Dictionary<string, object>>(lockPath);
            ConnectionProfile profile = project.GetProfile("staging", true);
            string id = "demo-" + Guid.NewGuid().ToString("N");
            int demoNumber = project.NextDemoNumber("staging");
            string demoLabel = "DEMO " + demoNumber.ToString(CultureInfo.InvariantCulture);
            string demoKey = "d" + demoNumber.ToString(CultureInfo.InvariantCulture);
            List<DemoComponent> components = new List<DemoComponent>();
            List<string> warnings = new List<string>();
            Dictionary<string, string> capabilities = new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<string, string> moduleStates;
            DemoRegistryCapability registryCapability;
            using (OdooClient client = Open("staging"))
            {
                moduleStates = ReadModuleStates(client);
                registryCapability = DemoOwnershipRegistry.Probe(client);
                AddIfAvailable(client, moduleStates, components, capabilities, warnings, "mailing_visual", "mailing_visual_asset", Values("name", "Imagen principal del mailing", "type", "binary", "mimetype", asset.mimetype, "public", false, "res_model", "mailing.mailing", "res_id", 0), null, "recurso privado sin publicar", "Imagen sintética interna del mailing de reemplazo.");
                DemoComponent visual = components.FirstOrDefault(x => x.key == "mailing_visual");
                if (visual == null) throw new ConsultantException("demo_asset_capability_missing", "La base no permite preparar el recurso visual interno: " + string.Join(", ", warnings.Where(x => x.StartsWith("mailing_visual_asset", StringComparison.Ordinal)).ToArray()));
                visual.asset_file = asset.relative_file; visual.asset_sha256 = asset.sha256; visual.asset_mimetype = asset.mimetype; visual.asset_size = asset.size;
                foreach (DemoComponent target in targets)
                {
                    string key = "replacement_" + target.key;
                    string suffix = target.key.IndexOf("mailing_b", StringComparison.Ordinal) >= 0 ? "B" : target.key.IndexOf("automation", StringComparison.Ordinal) >= 0 ? "AUTO" : "A";
                    Dictionary<string, object> desired = Values(
                        "subject", "Versión editable — " + suffix,
                        "preview", "Diseño corregido para el editor nativo de Odoo 18.",
                        "email_from", "replacement-" + demoKey + "@example.invalid",
                        "mailing_type", "mail",
                        "body_arch", MailEditorContract.Build(demoKey, "Una experiencia comercial conectada", "Descubra cómo captar, nutrir y medir oportunidades en un flujo visual preparado para esta demostración.", "Conocer la propuesta", suffix, "mailing_visual", true, variable, asset.alt),
                        "state", "draft", "active", true);
                    if (target.values != null && target.values.ContainsKey("ab_testing_enabled")) desired["ab_testing_enabled"] = target.values["ab_testing_enabled"];
                    if (target.values != null && target.values.ContainsKey("ab_testing_pc")) desired["ab_testing_pc"] = target.values["ab_testing_pc"];
                    if (target.values != null && target.values.ContainsKey("mailing_domain")) desired["mailing_domain"] = target.values["mailing_domain"];
                    AddMailingComponent(client, moduleStates, components, capabilities, warnings, key, desired, target.references == null ? null : new Dictionary<string, string>(target.references, StringComparer.Ordinal), true, variable, "Reemplazo editable de " + target.key + "; el original permanece intacto hasta ARCHIVAR-REEMPLAZO.");
                }
            }
            if (!registryCapability.available) warnings.Add("ownership_registry: el usuario no puede crear identidades técnicas; se usará protección local DPAPI.");
            if (components.Count != targets.Count + 1) throw new ConsultantException("demo_replacement_incomplete", "No se pudieron compilar todos los mailings de reemplazo.");
            Dictionary<string, object> protectedRecords = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (DemoComponent target in targets)
            {
                Dictionary<string, object> targetRecord = previousRecords[target.key] as Dictionary<string, object>;
                if (targetRecord == null || JsonUtil.GetString(targetRecord, "model", true) != "mailing.mailing") throw new ConsultantException("demo_replacement_target_invalid", "Un objetivo de reemplazo no corresponde a mailing.mailing.");
                protectedRecords["replacement_target:" + target.key] = JsonUtil.Dictionary("model", "mailing.mailing", "id", JsonUtil.GetLong(targetRecord, "id", true), "created_by_scenario", false, "source_scenario", previous.id);
                foreach (string dependency in (target.references ?? new Dictionary<string, string>()).Values.Where(x => !x.StartsWith("lookup:", StringComparison.Ordinal) && !x.StartsWith("runtime:", StringComparison.Ordinal)).Distinct())
                {
                    object raw; if (protectedRecords.ContainsKey(dependency) || !previousRecords.TryGetValue(dependency, out raw)) continue;
                    Dictionary<string, object> dependencyRecord = raw as Dictionary<string, object>;
                    if (dependencyRecord != null) protectedRecords[dependency] = JsonUtil.Dictionary("model", JsonUtil.GetString(dependencyRecord, "model", true), "id", JsonUtil.GetLong(dependencyRecord, "id", true), "created_by_scenario", false, "source_scenario", previous.id);
                }
            }
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DemoScenario scenario = new DemoScenario
            {
                schema_version = 4, id = id, client_slug = project.Config.client_slug, demo_slug = previous.demo_slug,
                demo_number = demoNumber, demo_label = demoLabel, delivery_mode = "database_records", ownership_registry = "hybrid",
                registry_status = registryCapability.status, registry_capability_hash = registryCapability.capability_hash,
                environment = "staging", application = "marketing", objective = "Reemplazar mailings defectuosos del escenario " + previous.id + " sin alterar otros registros.",
                requirements = new List<string> { "Editor nativo Odoo 18 inicializable", "Imagen interna protegida", "Personalización única", "Borrador sin efectos externos" },
                status = "prepared", created_at = now.ToString("o", CultureInfo.InvariantCulture), expires_at = now.AddMinutes(project.Config.demo_confirmation_minutes).ToString("o", CultureInfo.InvariantCulture),
                capability_hash = JsonUtil.CanonicalHash(capabilities), source_lock_hash = JsonUtil.GetString(sourceLock, "lock_hash", true), source_lock_file_hash = JsonUtil.Sha256(File.ReadAllText(lockPath, Encoding.UTF8)),
                profile_fingerprint = profile.credential_fingerprint, server_series = profile.server_series, adapter_contract_version = DemoAdapterCatalog.ScenarioContractVersion,
                modules_hash = JsonUtil.CanonicalHash(moduleStates), module_states = moduleStates, components = components, steps = CommercialSteps(components),
                reference_plan = new List<Dictionary<string, object>>(), initial_states = components.Select(x => JsonUtil.Dictionary("component", x.key, "state", x.initial_state)).ToList(),
                verification_procedure = components.Select(x => JsonUtil.Dictionary("component", x.key, "database_verified", true, "editor_ready", x.adapter == "mailing_draft")).ToList(),
                cleanup_procedure = components.Select(x => JsonUtil.Dictionary("component", x.key, "strategy", x.cleanup_strategy)).ToList(),
                record_plan = components.Select(x => JsonUtil.Dictionary("component", x.key, "adapter", x.adapter, "model", DemoAdapterCatalog.Get(x.adapter).model, "operation", "create", "references", x.references)).ToList(),
                readiness_criteria = new List<string> { "staging_records_created", "database_verified", "editor_ready", "external_effects_disabled", "safe_replacement" },
                external_effects = new List<string> { "email_send:disabled", "email_test:disabled", "email_schedule:disabled" },
                manual_operations = new List<string> { "Revisar la vista previa; CREAR-DEMO crea el reemplazo sin archivar el original.", "Solo después de verificar, ARCHIVAR-REEMPLAZO desactiva los mailings indicados." },
                warnings = warnings, protected_records = ProtectedSnapshot.Protect(protectedRecords), replacement_keys = targets.Select(x => x.key).ToList(), replacement_state = "prepared"
            };
            scenario.integrity_hash = Integrity(scenario);
            JsonUtil.WriteJsonFile(Path.Combine(directory, id + ".scenario.json"), scenario);
            WriteArtifacts(directory, scenario, sourceLock);
            AuditLog.Append(project, "staging", "demo_preparar_reemplazo_mailing", id, "success", "Reemplazo preparado para " + targets.Count + " mailings; sin escrituras", 0);
            return PublicScenario(scenario, "Vista previa preparada. No se aplicó ni se archivó ningún registro. Si se aprueba, usar CREAR-DEMO " + id + ".");
        }

        public object ArchiveReplacedMailings(string scenarioId, string confirmation)
        {
            DemoScenario scenario = Load(scenarioId); EnsureIntegrity(scenario);
            if (scenario.environment != "staging") throw new ConsultantException("production_write_blocked", "Producción se bloquea antes de cualquier llamada de red.");
            if (confirmation != "ARCHIVAR-REEMPLAZO " + scenario.id) throw new ConsultantException("confirmation_required", "La confirmación exacta es ARCHIVAR-REEMPLAZO " + scenario.id + ".");
            if (scenario.status != "verified" || scenario.replacement_keys == null || scenario.replacement_keys.Count == 0) throw new ConsultantException("demo_replacement_not_verified", "Primero verifica database_verified y editor_ready del reemplazo.");
            DateTimeOffset expires; if (!DateTimeOffset.TryParse(scenario.expires_at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expires) || expires < DateTimeOffset.UtcNow) throw new ConsultantException("confirmation_expired", "La autorización del reemplazo venció.");
            if (scenario.replacement_state == "archived") return PublicScenario(scenario, "Los mailings defectuosos ya estaban archivados; no se repitieron escrituras.");
            Dictionary<string, object> records = ProtectedSnapshot.Unprotect(scenario.protected_records) ?? new Dictionary<string, object>(StringComparer.Ordinal);
            Dictionary<string, object> snapshots = new Dictionary<string, object>(StringComparer.Ordinal);
            using (OdooClient client = Open("staging"))
            {
                foreach (string key in scenario.replacement_keys)
                {
                    Dictionary<string, object> record = records["replacement_target:" + key] as Dictionary<string, object>;
                    if (record == null || JsonUtil.GetString(record, "model", true) != "mailing.mailing") throw new ConsultantException("demo_replacement_target_invalid", "El objetivo protegido dejó de ser válido.");
                    long id = JsonUtil.GetLong(record, "id", true);
                    IList<Dictionary<string, object>> rows = client.Read("mailing.mailing", new[] { id }, new[] { "state", "active" });
                    if (rows.Count != 1 || StringValue(rows[0], "state") != "draft") throw new ConsultantException("demo_replacement_target_changed", "El mailing original ya no está en borrador; no se archivará.");
                    bool active = rows[0].ContainsKey("active") && Convert.ToBoolean(rows[0]["active"], CultureInfo.InvariantCulture);
                    snapshots[key] = JsonUtil.Dictionary("active", active);
                    if (active && !client.Write("mailing.mailing", new[] { id }, JsonUtil.Dictionary("active", false))) throw new ConsultantException("odoo_write_failed", "Odoo no confirmó el archivado del mailing defectuoso.");
                }
            }
            scenario.replacement_snapshot = ProtectedSnapshot.Protect(snapshots); scenario.replacement_state = "archived"; scenario.replacement_archived_at = Now(); Save(scenario);
            AuditLog.Append(project, "staging", "demo_archivar_reemplazados", scenario.id, "success", "Mailings defectuosos archivados: " + scenario.replacement_keys.Count, 0);
            return PublicScenario(scenario, "Solo se archivaron los mailings defectuosos declarados; RESTAURAR-REEMPLAZO permite revertirlo.");
        }

        public object RestoreReplacedMailings(string scenarioId, string confirmation)
        {
            DemoScenario scenario = Load(scenarioId); EnsureIntegrity(scenario);
            if (scenario.environment != "staging") throw new ConsultantException("production_write_blocked", "Producción se bloquea antes de cualquier llamada de red.");
            if (confirmation != "RESTAURAR-REEMPLAZO " + scenario.id) throw new ConsultantException("confirmation_required", "La confirmación exacta es RESTAURAR-REEMPLAZO " + scenario.id + ".");
            if (scenario.replacement_state == "restored") return PublicScenario(scenario, "Los originales ya estaban restaurados; no se repitieron escrituras.");
            if (scenario.replacement_state != "archived") throw new ConsultantException("demo_replacement_not_archived", "El escenario no tiene mailings originales archivados por esta ruta.");
            Dictionary<string, object> records = ProtectedSnapshot.Unprotect(scenario.protected_records) ?? new Dictionary<string, object>(StringComparer.Ordinal);
            Dictionary<string, object> snapshots = ProtectedSnapshot.Unprotect(scenario.replacement_snapshot) ?? new Dictionary<string, object>(StringComparer.Ordinal);
            using (OdooClient client = Open("staging"))
            {
                foreach (string key in scenario.replacement_keys)
                {
                    Dictionary<string, object> record = records["replacement_target:" + key] as Dictionary<string, object>;
                    Dictionary<string, object> snapshot = snapshots[key] as Dictionary<string, object>;
                    if (record == null || snapshot == null) throw new ConsultantException("demo_replacement_snapshot_missing", "Falta el snapshot protegido del original.");
                    if (!client.Write("mailing.mailing", new[] { JsonUtil.GetLong(record, "id", true) }, JsonUtil.Dictionary("active", JsonUtil.GetBool(snapshot, "active", false)))) throw new ConsultantException("odoo_write_failed", "Odoo no confirmó la restauración del mailing original.");
                }
            }
            scenario.replacement_state = "restored"; scenario.replacement_restored_at = Now(); Save(scenario);
            AuditLog.Append(project, "staging", "demo_restaurar_reemplazados", scenario.id, "success", "Mailings originales restaurados: " + scenario.replacement_keys.Count, 0);
            return PublicScenario(scenario, "Se restauró únicamente el estado anterior protegido de los mailings reemplazados.");
        }

        public object Apply(string scenarioId, string confirmation) { return Apply(scenarioId, confirmation, null); }

        public object Apply(string scenarioId, string confirmation, IDictionary<string, object> runtimeReferences)
        {
            DemoScenario scenario = Load(scenarioId);
            EnsureIntegrity(scenario);
            if (scenario.environment != "staging") throw new ConsultantException("production_write_blocked", "Producción se bloquea antes de cualquier llamada de red.");
            if (confirmation != "CREAR-DEMO " + scenario.id) throw new ConsultantException("confirmation_required", "La confirmación exacta es CREAR-DEMO " + scenario.id + ".");
            DateTimeOffset expires;
            bool confirmationExpired = !DateTimeOffset.TryParse(scenario.expires_at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expires) || expires < DateTimeOffset.UtcNow;
            if (scenario.status == "applied" || scenario.status == "verified")
            {
                if (confirmationExpired) throw new ConsultantException("confirmation_expired", "La confirmación del escenario venció; prepara uno nuevo.");
                return PublicScenario(scenario, "El escenario ya estaba aplicado; no se duplicaron registros.");
            }
            bool recoverableVerification = scenario.status == "verification_failed" && !string.IsNullOrWhiteSpace(scenario.last_error) && scenario.components != null && scenario.components.Any(x => x.status == "planned");
            if (scenario.status != "prepared" && scenario.status != "failed_partial" && !recoverableVerification) throw new ConsultantException("invalid_demo_state", "El escenario no se puede aplicar desde su estado actual.");
            bool freshReconfirmationAllowed = confirmationExpired && scenario.status == "failed_partial" && scenario.components != null && scenario.components.Any(x => x.status == "planned");
            if (confirmationExpired && !freshReconfirmationAllowed) throw new ConsultantException("confirmation_expired", "La confirmación del escenario venció; prepara uno nuevo.");
            ConnectionProfile profile = project.GetProfile(scenario.environment, true);
            if (scenario.schema_version >= 2)
            {
                if (!string.Equals(profile.credential_fingerprint, scenario.profile_fingerprint, StringComparison.Ordinal)) throw new ConsultantException("demo_profile_changed", "La credencial verificada cambió desde la vista previa.");
                ValidateSourceLock(scenario);
                Dictionary<string, object> detected = OdooClient.DetectInstance(profile.base_url, 30000);
                if (!string.Equals(Convert.ToString(detected["server_series"], CultureInfo.InvariantCulture), scenario.server_series, StringComparison.Ordinal) || !string.Equals(scenario.server_series, project.Config.odoo_version, StringComparison.Ordinal)) throw new ConsultantException("odoo_version_mismatch", "La versión real de Odoo cambió desde la preparación del escenario.");
            }
            Dictionary<string, object> protectedRecords = ProtectedSnapshot.Unprotect(scenario.protected_records) ?? new Dictionary<string, object>(StringComparer.Ordinal);
            Dictionary<string, long> ids = RestoreIds(protectedRecords);
            scenario.status = "applying";
            Save(scenario);
            try
            {
                using (OdooClient client = Open(scenario.environment))
                {
                    if (scenario.schema_version >= 2)
                    {
                        Dictionary<string, string> currentModules = ReadModuleStates(client);
                        if (JsonUtil.CanonicalHash(currentModules) != scenario.modules_hash) throw new ConsultantException("demo_modules_changed", "Los módulos instalados cambiaron desde la vista previa.");
                    }
                    string currentHash = scenario.schema_version == 1 ? CurrentLegacyCapabilities(client, scenario.components) : CurrentCapabilities(client, scenario.components);
                    if (currentHash != scenario.capability_hash) throw new ConsultantException("demo_capabilities_changed", "Los permisos o campos permitidos cambiaron desde la vista previa.");
                    DemoRegistryCapability registryCapability = scenario.schema_version >= 4 ? DemoOwnershipRegistry.Probe(client) : null;
                    if (scenario.schema_version >= 4)
                    {
                        scenario.registry_status = registryCapability.status;
                        scenario.registry_capability_hash = registryCapability.capability_hash;
                        Save(scenario);
                    }
                    if (freshReconfirmationAllowed)
                    {
                        scenario.expires_at = DateTimeOffset.UtcNow.AddMinutes(project.Config.demo_confirmation_minutes).ToString("o", CultureInfo.InvariantCulture);
                        Save(scenario);
                        AuditLog.Append(project, scenario.environment, "demo_reconfirmar_escenario", scenario.id, "success", "Escenario parcial revalidado con confirmación nueva", 0);
                    }
                    string firstImageMailingKey = scenario.components.Where(x => x.adapter == "mailing_draft" && x.image_required).Select(x => x.key).FirstOrDefault();
                    foreach (DemoComponent component in scenario.components)
                    {
                        long protectedId;
                        bool hasProtectedId = ids.TryGetValue(component.key, out protectedId);
                        if (!hasProtectedId && scenario.schema_version >= 4 && registryCapability.read)
                        {
                            DemoRegistryRecord recovered = DemoOwnershipRegistry.Find(client, scenario, component);
                            if (recovered != null)
                            {
                                protectedId = recovered.record_id;
                                hasProtectedId = true;
                                ids[component.key] = recovered.record_id;
                                protectedRecords[component.key] = JsonUtil.Dictionary("model", recovered.model, "id", recovered.record_id, "created_by_scenario", true, "adapter", component.adapter, "registry_id", recovered.registry_id, "registry_name", recovered.name);
                                scenario.protected_records = ProtectedSnapshot.Protect(protectedRecords);
                                component.status = "recovered_registry";
                                component.write_fingerprint = null;
                                Save(scenario);
                            }
                        }
                        bool resumeSurveyQuestion = hasProtectedId && component.adapter == "survey_question" && component.status == "created_base";
                        if (hasProtectedId && !resumeSurveyQuestion) continue;
                        if (scenario.schema_version >= 4 && component.status == "creating") throw new ConsultantException("demo_creation_ambiguous", "La ejecución anterior se interrumpió durante una creación. El motor no adoptará por nombre un registro potencialmente ajeno; revisa el componente antes de continuar.");
                        if (ShouldSkipSurveySection(scenario.server_series, component.adapter))
                        {
                            component.status = "skipped_optional";
                            string warning = "survey_section: omitida en Odoo 18; las preguntas se asocian directamente a la encuesta.";
                            if (!scenario.warnings.Contains(warning)) scenario.warnings.Add(warning);
                            Save(scenario);
                            continue;
                        }
                        DemoAdapterContract contract = DemoAdapterCatalog.Get(component.adapter);
                        try
                        {
                            Dictionary<string, object> fields = Fields(client, contract.model);
                            Dictionary<string, object> values = DemoAdapterCatalog.FilterAndValidateValues(contract, component.values, fields, scenario.schema_version >= 2);
                            if (contract.key == "mailing_visual_asset" || contract.key == "demo_private_attachment") AddAssetBinaryAtApply(scenario, component, values);
                            ApplyFixedTechnicalValues(contract, values, fields);
                            ApplyRuntimeTechnicalValues(contract, values, fields, ids);
                            if (scenario.schema_version >= 2) EnsureRequiredValues(contract, values, component.references);
                            ValidateSelections(values, fields);
                            ResolveReferences(client, contract, values, component.references, ids, fields, runtimeReferences);
                            if (contract.key == "mailing_draft") ResolveAndValidateMailBody(component, values, ids);
                            ValidateSyntheticValues(contract, values, scenario.schema_version);
                            IList<Dictionary<string, object>> existing = resumeSurveyQuestion ? new List<Dictionary<string, object>>() : FindExisting(client, contract, values, fields);
                            if (scenario.schema_version >= 4 && existing.Count > 0) throw new ConsultantException("demo_business_collision", "Ya existe un registro con la misma identidad comercial. El escenario no lo reutilizará ni lo modificará; usa un nombre comercial distinto.");
                            if (scenario.schema_version <= 3 && existing.Count > 1) throw new ConsultantException("demo_marker_collision", "Hay más de un registro con el marcador sintético; el paquete no elegirá uno silenciosamente.");
                            bool created = scenario.schema_version >= 4 || resumeSurveyQuestion || existing.Count == 0;
                            long recordId;
                            if (resumeSurveyQuestion) recordId = protectedId;
                            else
                            {
                                component.status = "creating";
                                component.write_fingerprint = JsonUtil.CanonicalHash(JsonUtil.Dictionary("model", contract.model, "values", values, "references", component.references));
                                Save(scenario);
                                if (created && contract.key == "survey_question" && scenario.server_series != "19.0") recordId = CreateSurveyQuestionThroughParent(client, contract, values, fields);
                                else if (created && contract.key == "survey_question")
                                {
                                    Dictionary<string, object> initialValues = SurveyQuestionInitialValues(values);
                                    recordId = client.CreateSurveyComponent(contract.model, initialValues);
                                    ids[component.key] = recordId;
                                    protectedRecords[component.key] = JsonUtil.Dictionary("model", contract.model, "id", recordId, "created_by_scenario", true, "adapter", contract.key);
                                    scenario.protected_records = ProtectedSnapshot.Protect(protectedRecords);
                                    component.status = "created_base";
                                    Save(scenario);
                                }
                                else if (created && contract.model == "survey.question.answer") recordId = client.CreateSurveyComponent(contract.model, values);
                                else if (created) recordId = client.Create(contract.model, values);
                                else recordId = LongValue(existing[0], "id");
                            }
                            if (created && contract.key == "survey_question" && scenario.server_series == "19.0")
                            {
                                Dictionary<string, object> finalValues = SurveyQuestionFinalValues(values);
                                if (finalValues.Count > 0 && !client.Write(contract.model, new[] { recordId }, finalValues)) throw new ConsultantException("odoo_write_failed", "Odoo no confirmó la configuración final de la pregunta de encuesta.");
                            }
                            ids[component.key] = recordId;
                            protectedRecords[component.key] = JsonUtil.Dictionary("model", contract.model, "id", recordId, "created_by_scenario", created, "adapter", contract.key, "asset_sha256", (contract.key == "mailing_visual_asset" || contract.key == "demo_private_attachment") ? component.asset_sha256 : null);
                            scenario.protected_records = ProtectedSnapshot.Protect(protectedRecords);
                            component.status = created ? "created_pending_registry" : "reused_preserved";
                            Save(scenario);
                            if (created && scenario.schema_version >= 4 && registryCapability.available)
                            {
                                try
                                {
                                    DemoRegistryRecord registered = DemoOwnershipRegistry.Register(client, scenario, component, recordId, registryCapability);
                                    if (registered != null)
                                    {
                                        Dictionary<string, object> protectedEntry = protectedRecords[component.key] as Dictionary<string, object>;
                                        protectedEntry["registry_id"] = registered.registry_id;
                                        protectedEntry["registry_name"] = registered.name;
                                    }
                                }
                                catch (ConsultantException registryError)
                                {
                                    if (registryError.Code == "demo_registry_collision" || registryError.Code == "demo_registry_stale") throw;
                                    scenario.registry_status = "local_only";
                                    string registryWarning = "ownership_registry: no se pudo registrar " + component.key + "; continúa protegido localmente.";
                                    if (!scenario.warnings.Contains(registryWarning)) scenario.warnings.Add(registryWarning);
                                }
                            }
                            if (component.key == firstImageMailingKey)
                            {
                                long attachmentId;
                                if (!ids.TryGetValue("mailing_visual", out attachmentId) || !client.Write("ir.attachment", new[] { attachmentId }, JsonUtil.Dictionary("res_model", "mailing.mailing", "res_id", recordId))) throw new ConsultantException("demo_asset_link_failed", "Odoo no confirmó el vínculo del recurso visual con el mailing.");
                            }
                            scenario.protected_records = ProtectedSnapshot.Protect(protectedRecords);
                            component.status = created ? "created" : "reused_preserved";
                            component.write_fingerprint = null;
                            Save(scenario);
                        }
                        catch (ConsultantException error)
                        {
                            if (scenario.schema_version >= 4 && component.status == "creating" && error.Code == "odoo_request_failed" && error.Message.StartsWith("La llamada ", StringComparison.Ordinal))
                            {
                                component.status = "planned";
                                component.write_fingerprint = null;
                                Save(scenario);
                            }
                            throw new ConsultantException(error.Code, "Falló el componente " + component.key + " (" + contract.key + " sobre " + contract.model + "). " + error.Message);
                        }
                    }
                }
                scenario.status = "applied";
                scenario.applied_at = Now();
                Save(scenario);
                AuditLog.Append(project, scenario.environment, "demo_aplicar_escenario", scenario.id, "success", "Escenario aplicado; componentes protegidos: " + ids.Count, 0);
                return PublicScenario(scenario, "Escenario aplicado sin activar efectos externos.");
            }
            catch (Exception error)
            {
                scenario.status = "failed_partial";
                scenario.last_error = SafeError(error);
                Save(scenario);
                AuditLog.Append(project, scenario.environment, "demo_aplicar_escenario", scenario.id, "failed", scenario.last_error, 0);
                throw;
            }
        }

        public object VerifyStep(string scenarioId, string stepKey)
        {
            DemoScenario scenario = Load(scenarioId);
            EnsureIntegrity(scenario);
            DemoStep step = scenario.steps.FirstOrDefault(x => x.key == stepKey);
            if (step == null) throw new ConsultantException("demo_step_not_found", "El paso no pertenece al escenario.");
            if (string.IsNullOrWhiteSpace(step.component_key)) return JsonUtil.Dictionary("scenario_id", scenario.id, "step", step.key, "result", "manual_read_only", "expected", step.expected);
            Dictionary<string, long> ids = RestoreIds(ProtectedSnapshot.Unprotect(scenario.protected_records));
            long id;
            if (!ids.TryGetValue(step.component_key, out id)) return JsonUtil.Dictionary("scenario_id", scenario.id, "step", step.key, "result", "pending");
            DemoComponent component = scenario.components.First(x => x.key == step.component_key);
            using (OdooClient client = Open(scenario.environment))
            {
                ComponentVerification result = VerifyComponent(client, component, id, ids);
                return JsonUtil.Dictionary("scenario_id", scenario.id, "step", step.key, "result", result.database_verified && result.editor_ready ? "verified" : "mismatch", "database_verified", result.database_verified, "editor_ready", result.editor_ready, "editor_validation", result.detail, "expected", step.expected);
            }
        }

        public object Verify(string scenarioId)
        {
            DemoScenario scenario = Load(scenarioId);
            EnsureIntegrity(scenario);
            Dictionary<string, long> ids = RestoreIds(ProtectedSnapshot.Unprotect(scenario.protected_records));
            int verified = 0;
            int databaseVerified = 0;
            int editorReady = 0;
            int editorExpected = 0;
            List<string> mismatches = new List<string>();
            using (OdooClient client = Open(scenario.environment))
            {
                foreach (DemoComponent component in scenario.components)
                {
                    if (component.status == "skipped_optional") { verified++; databaseVerified++; continue; }
                    if (component.status == "created_base") { mismatches.Add(component.key); continue; }
                    long id;
                    if (!ids.TryGetValue(component.key, out id)) { mismatches.Add(component.key); continue; }
                    ComponentVerification result = VerifyComponent(client, component, id, ids);
                    component.database_verified = result.database_verified;
                    component.editor_ready = result.editor_ready;
                    component.editor_validation = result.detail;
                    if (component.adapter == "mailing_draft") { editorExpected++; if (result.editor_ready) editorReady++; }
                    if (result.database_verified) databaseVerified++;
                    if (result.database_verified && result.editor_ready) { component.status = "verified"; verified++; }
                    else { component.status = "verification_mismatch"; mismatches.Add(component.key); }
                }
            }
            bool incompleteApplication = ids.Count < scenario.components.Count;
            bool allDatabase = databaseVerified == scenario.components.Count;
            bool allEditors = editorReady == editorExpected;
            scenario.status = allDatabase && allEditors ? "verified" : incompleteApplication ? "failed_partial" : "verification_failed";
            if (scenario.status == "verified") scenario.last_error = null;
            scenario.verified_at = Now();
            Save(scenario);
            string resultsPath = Path.Combine(project.DemoDirectory(scenario.demo_slug, false), "resultados.md");
            File.WriteAllText(resultsPath, "# Resultados\n\n- database_verified: " + databaseVerified + " de " + scenario.components.Count + "\n- editor_ready: " + editorReady + " de " + editorExpected + " mailings\n- Prueba de interfaz: pendiente opcional, solo lectura y sin guardar ni enviar\n- Diferencias técnicas: " + (mismatches.Count == 0 ? "ninguna" : string.Join(", ", mismatches.ToArray())) + "\n- Efectos externos: desactivados\n- Estado: " + scenario.status + "\n", new UTF8Encoding(false));
            AuditLog.Append(project, scenario.environment, "demo_verificar_escenario", scenario.id, scenario.status == "verified" ? "success" : "failed", "Componentes verificados: " + verified + "/" + scenario.components.Count, 0);
            return PublicScenario(scenario, "Verificación cerrada completada.");
        }

        public object Clean(string scenarioId, string confirmation)
        {
            DemoScenario scenario = Load(scenarioId);
            EnsureIntegrity(scenario);
            if (scenario.environment != "staging") throw new ConsultantException("production_write_blocked", "Producción se bloquea antes de cualquier llamada de red.");
            if (confirmation != "LIMPIAR-DEMO " + scenario.id) throw new ConsultantException("confirmation_required", "La confirmación exacta es LIMPIAR-DEMO " + scenario.id + ".");
            Dictionary<string, object> records = ProtectedSnapshot.Unprotect(scenario.protected_records) ?? new Dictionary<string, object>(StringComparer.Ordinal);
            int deactivated = 0;
            int preserved = 0;
            List<string> manual = new List<string>();
            using (OdooClient client = Open(scenario.environment))
            {
                foreach (DemoComponent component in Enumerable.Reverse(scenario.components))
                {
                    if (component.status == "skipped_optional") { preserved++; continue; }
                    object raw;
                    if (!records.TryGetValue(component.key, out raw)) continue;
                    Dictionary<string, object> record = raw as Dictionary<string, object>;
                    if (record == null) { manual.Add(component.key); continue; }
                    bool created = record.ContainsKey("created_by_scenario") && JsonUtil.GetBool(record, "created_by_scenario", false);
                    if (!created)
                    {
                        component.status = "preserved_reused";
                        preserved++;
                        continue;
                    }
                    long id = JsonUtil.GetLong(record, "id", true);
                    DemoAdapterContract contract = DemoAdapterCatalog.Get(component.adapter);
                    Dictionary<string, object> fields = Fields(client, contract.model);
                    Dictionary<string, object> values = CleanupValues(contract, fields);
                    if (values.Count > 0 && SafeAccess(client, contract.model, "write") && client.Write(contract.model, new[] { id }, values))
                    {
                        component.status = contract.cleanup_strategy == "unpublish_and_deactivate" ? "unpublished_deactivated" : contract.cleanup_strategy == "detach_and_unpublish" ? "detached_unpublished" : "deactivated";
                        deactivated++;
                    }
                    else
                    {
                        component.status = "manual_cleanup";
                        manual.Add(component.key);
                    }
                }
            }
            scenario.status = manual.Count == 0 ? "cleaned" : "cleanup_partial";
            scenario.cleaned_at = Now();
            scenario.warnings.AddRange(manual.Select(x => "Limpieza manual o contención por padre requerida: " + x));
            Save(scenario);
            AuditLog.Append(project, scenario.environment, "demo_limpiar_escenario", scenario.id, manual.Count == 0 ? "success" : "partial", "Desactivados: " + deactivated + "; preservados: " + preserved + "; manuales: " + manual.Count, 0);
            return PublicScenario(scenario, "No se eliminó ningún registro; los reutilizados se preservaron.");
        }

        private void AddCoreMarketing(OdooClient client, IDictionary<string, string> modules, IList<DemoComponent> components, IDictionary<string, string> capabilities, IList<string> warnings, string tag, IDictionary<string, object> configuration)
        {
            Dictionary<string, object> utm = Child(configuration, "utm");
            string sourceName = ConfigText(utm, "source_name", "Fuente landing", 120);
            string mediumName = ConfigText(utm, "medium_name", "Medio digital", 120);
            string campaignName = ConfigText(utm, "campaign_name", "Campaña lanzamiento", 120);
            string leadName = ConfigText(utm, "lead_name", "Lead de muestra", 120);
            string contactName = ConfigText(utm, "contact_name", "Contacto ficticio", 120);
            AddIfAvailable(client, modules, components, capabilities, warnings, "source", "utm_source", Values("name", sourceName), null, "activo", "Fuente UTM disponible para atribución.");
            AddIfAvailable(client, modules, components, capabilities, warnings, "medium", "utm_medium", Values("name", mediumName), null, "activo", "Medio UTM disponible para atribución.");
            AddIfAvailable(client, modules, components, capabilities, warnings, "campaign", "utm_campaign", Values("name", campaignName), null, "activo", "Campaña UTM visible en el lead y los borradores.");
            if (components.Any(x => x.key == "source") && components.Any(x => x.key == "medium") && components.Any(x => x.key == "campaign")) AddIfAvailable(client, modules, components, capabilities, warnings, "lead", "crm_lead", Values("name", leadName, "type", "lead", "contact_name", contactName, "email_from", "lead-" + tag.ToLowerInvariant() + "@example.invalid", "description", "Dato sintético de demostración. No contactar."), new Dictionary<string, string> { { "source_id", "source" }, { "medium_id", "medium" }, { "campaign_id", "campaign" } }, "lead activo sintético", "Lead trazable a campaña, medio y fuente.");
            else warnings.Add("crm_lead: requiere source, medium y campaign disponibles");
        }

        private void AddWebsite(OdooClient client, IDictionary<string, string> modules, IList<DemoComponent> components, IDictionary<string, string> capabilities, IList<string> warnings, IList<Dictionary<string, object>> referencePlan, string tag, IDictionary<string, object> configuration)
        {
            Dictionary<string, object> website = Child(configuration, "website");
            if (!Enabled(website)) return;
            if (!Installed(modules, "website") || !Installed(modules, "website_crm")) { warnings.Add("website_landing: website o website_crm no está instalado"); return; }
            IList<Dictionary<string, object>> websites = client.SearchRead("website", new object[0], new[] { "id", "name" }, 3, "id asc");
            if (websites.Count == 0) { warnings.Add("website_landing: no existe un sitio web visible"); return; }
            string websiteReference = websites.Count == 1 ? "lookup:website_single" : "runtime:WEBSITE_ID";
            if (websites.Count > 1) referencePlan.Add(JsonUtil.Dictionary("key", "WEBSITE_ID", "type", "record_id", "model", "website", "required_at_apply", true, "persisted", false));
            string headline = ConfigText(website, "headline", "Conozca nuestra propuesta", 120);
            string subtitle = ConfigText(website, "subtitle", "Una experiencia comercial preparada con datos completamente ficticios.", 300);
            string cta = ConfigText(website, "cta_label", "Solicitar información", 60);
            string legal = ConfigText(website, "legal_text", "Al continuar confirma que estos datos son ficticios y se usan únicamente para la demostración.", 300);
            string layout = ConfigText(website, "layout", "hero_form", 30);
            if (layout != "hero_form" && layout != "split_form") throw new ConsultantException("invalid_demo_configuration", "El layout de Website no pertenece al catálogo aprobado.");
            List<string> benefits = ConfigList(website, "benefits", new[] { "Respuesta ágil", "Seguimiento trazable", "Información centralizada" }, 1, 6, 120);
            string key = "odoo_ai_demo." + project.Config.client_slug + "_" + tag + "_landing";
            string url = "/marketing-" + tag.ToLowerInvariant();
            Dictionary<string, object> values = Values("name", headline, "url", url, "key", key, "type", "qweb", "track", true, "website_indexed", false, "is_published", false, "active", true, "arch", BuildLandingArch(key, headline, subtitle, benefits, cta, legal, layout));
            AddIfAvailable(client, modules, components, capabilities, warnings, "landing", "website_landing_unpublished", values, new Dictionary<string, string> { { "website_id", websiteReference } }, "activa pero no publicada ni indexada", "Landing visible en el editor; el formulario nativo apunta exclusivamente a crm.lead.");
        }

        private void AddSurvey(OdooClient client, IDictionary<string, string> modules, IList<DemoComponent> components, IDictionary<string, string> capabilities, IList<string> warnings, string tag, IDictionary<string, object> configuration)
        {
            Dictionary<string, object> survey = Child(configuration, "survey");
            if (!Enabled(survey)) return;
            string title = ConfigText(survey, "title", "Encuesta de necesidades", 120);
            string intro = ConfigText(survey, "description", "Ayúdenos a entender sus prioridades. Esta encuesta usa respuestas ficticias durante la demostración.", 500);
            AddIfAvailable(client, modules, components, capabilities, warnings, "survey", "survey_draft", Values("title", title, "description", Paragraph(intro), "access_mode", "token", "users_login_required", true, "scoring_type", "no_scoring", "certification", false, "active", false), null, "inactiva y acceso por invitación", "Encuesta preparada sin publicarla.");
            if (!components.Any(x => x.key == "survey")) return;
            if (project.Config.odoo_version != "19.0") warnings.Add("survey_section: omitida en Odoo " + project.Config.odoo_version + "; las preguntas se asocian directamente a la encuesta.");
            else AddIfAvailable(client, modules, components, capabilities, warnings, "survey_section", "survey_section", Values("title", "Contexto y necesidades", "sequence", 5, "is_page", true), new Dictionary<string, string> { { "survey_id", "survey" } }, "sección contenida por encuesta inactiva", "Sección inicial ordenada antes de las preguntas.");
            List<Dictionary<string, object>> questions = Questions(survey);
            int questionIndex = 0;
            foreach (Dictionary<string, object> question in questions)
            {
                questionIndex++;
                string questionKey = "survey_q" + questionIndex.ToString(CultureInfo.InvariantCulture);
                string type = JsonUtil.GetString(question, "type", true);
                string label = JsonUtil.GetString(question, "label", true);
                bool required = JsonUtil.GetBool(question, "required", false);
                AddIfAvailable(client, modules, components, capabilities, warnings, questionKey, "survey_question", Values("title", label, "question_type", type, "sequence", questionIndex * 10, "constr_mandatory", required, "is_page", false), new Dictionary<string, string> { { "survey_id", "survey" } }, "contenido por encuesta inactiva", "Pregunta ordenada y asociada a la encuesta.");
                if (!components.Any(x => x.key == questionKey)) continue;
                List<string> options = StringList(question.ContainsKey("options") ? question["options"] : null, 0, 10, 120, "opciones de encuesta");
                int optionIndex = 0;
                foreach (string option in options)
                {
                    optionIndex++;
                    AddIfAvailable(client, modules, components, capabilities, warnings, questionKey + "_a" + optionIndex.ToString(CultureInfo.InvariantCulture), "survey_answer", Values("value", option, "sequence", optionIndex * 10, "is_correct", false, "answer_score", 0), new Dictionary<string, string> { { "question_id", questionKey } }, "contenido por encuesta inactiva", "Opción ordenada de la pregunta.");
                }
                if (type == "matrix")
                {
                    List<string> rows = StringList(question.ContainsKey("rows") ? question["rows"] : null, 2, 10, 120, "filas de matriz");
                    int rowIndex = 0;
                    foreach (string row in rows)
                    {
                        rowIndex++;
                        AddIfAvailable(client, modules, components, capabilities, warnings, questionKey + "_r" + rowIndex.ToString(CultureInfo.InvariantCulture), "survey_matrix_row", Values("value", row, "sequence", rowIndex * 10), new Dictionary<string, string> { { "matrix_question_id", questionKey } }, "contenido por encuesta inactiva", "Fila ordenada de la matriz.");
                    }
                }
            }
        }

        private void AddMailingAndAutomation(OdooClient client, IDictionary<string, string> modules, IList<DemoComponent> components, IDictionary<string, string> capabilities, IList<string> warnings, string tag, string demoDirectory, IDictionary<string, object> configuration)
        {
            Dictionary<string, object> mailing = Child(configuration, "mailing");
            if (Enabled(mailing))
            {
                AddIfAvailable(client, modules, components, capabilities, warnings, "mailing_list", "mailing_list", Values("name", "Interesados en transformación comercial"), null, "activa sin envíos", "Lista disponible solo para los borradores de la demo.");
                if (components.Any(x => x.key == "mailing_list")) AddIfAvailable(client, modules, components, capabilities, warnings, "mailing_contact", "mailing_contact", Values("name", "Laura Gómez", "email", "contacto." + tag.ToLowerInvariant() + "@example.invalid"), new Dictionary<string, string> { { "list_ids", "mailing_list" } }, "contacto ficticio", "Contacto no entregable asociado a la lista.");
                string preheader = ConfigText(mailing, "preheader", "Conozca la propuesta preparada para esta demostración.", 150);
                string headline = ConfigText(mailing, "headline", "Una mejor forma de gestionar sus oportunidades", 120);
                string body = ConfigText(mailing, "body", "Centralice la captación, el seguimiento y la medición en un mismo flujo.", 800);
                string cta = ConfigText(mailing, "cta_label", "Conocer más", 60);
                if (mailing.ContainsKey("image_url")) throw new ConsultantException("demo_external_image_not_allowed", "Los mailings nuevos no aceptan URL de imagen; usa image_asset dentro de demostraciones/<slug>/assets.");
                Dictionary<string, object> imageAsset = Child(mailing, "image_asset");
                bool imageRequired = mailing.ContainsKey("image_required") ? JsonUtil.GetBool(mailing, "image_required", false) : imageAsset.Count > 0;
                DemoAssetDescriptor asset = imageRequired ? MailEditorContract.ValidateAsset(demoDirectory, imageAsset) : null;
                string personalization = mailing.ContainsKey("personalization_variable") ? ConfigText(mailing, "personalization_variable", "none", 30) : "none";
                if (mailing.ContainsKey("variables"))
                {
                    if (mailing.ContainsKey("personalization_variable")) throw new ConsultantException("mail_personalization_mixed", "Declara personalización una sola vez.");
                    List<string> legacyVariables = StringList(mailing["variables"], 0, 1, 30, "variables");
                    personalization = legacyVariables.Count == 0 ? "none" : legacyVariables[0];
                }
                if (personalization != "none" && personalization != "recipient_name") throw new ConsultantException("mail_personalization_invalid", "Solo se admite none o recipient_name.");
                if (imageRequired)
                {
                    AddIfAvailable(client, modules, components, capabilities, warnings, "mailing_visual", "mailing_visual_asset", Values("name", "Imagen principal de campaña", "type", "binary", "mimetype", asset.mimetype, "public", false, "res_model", "mailing.mailing", "res_id", 0), null, "recurso privado sin publicar", "Imagen sintética interna para el diseñador de Email Marketing.");
                    DemoComponent assetComponent = components.FirstOrDefault(x => x.key == "mailing_visual");
                    if (assetComponent == null) throw new ConsultantException("demo_asset_capability_missing", "La base no permite crear el recurso visual interno requerido: " + string.Join(", ", warnings.Where(x => x.StartsWith("mailing_visual_asset", StringComparison.Ordinal)).ToArray()));
                    assetComponent.asset_file = asset.relative_file;
                    assetComponent.asset_sha256 = asset.sha256;
                    assetComponent.asset_mimetype = asset.mimetype;
                    assetComponent.asset_size = asset.size;
                }
                int percentage = ConfigInt(mailing, "ab_percentage", 50, 1, 100);
                string subjectA = ConfigText(mailing, "subject_a", "Descubra una experiencia comercial conectada", 150);
                string subjectB = ConfigText(mailing, "subject_b", "Convierta interés en oportunidades medibles", 150);
                if (components.Any(x => x.key == "mailing_list") && components.Any(x => x.key == "campaign"))
                {
                    AddMailingComponent(client, modules, components, capabilities, warnings, "mailing_a", Values("subject", "A — " + subjectA, "preview", preheader, "email_from", "campana-" + tag.ToLowerInvariant() + "@example.invalid", "mailing_type", "mail", "body_arch", MailEditorContract.Build(tag, headline, body, cta, "A", "mailing_visual", imageRequired, personalization, asset == null ? string.Empty : asset.alt), "state", "draft", "ab_testing_enabled", true, "ab_testing_pc", percentage, "active", true), new Dictionary<string, string> { { "contact_list_ids", "mailing_list" }, { "campaign_id", "campaign" } }, imageRequired, personalization, "Variante A en borrador, asociada a lista y campaña UTM.");
                    AddMailingComponent(client, modules, components, capabilities, warnings, "mailing_b", Values("subject", "B — " + subjectB, "preview", preheader, "email_from", "campana-" + tag.ToLowerInvariant() + "@example.invalid", "mailing_type", "mail", "body_arch", MailEditorContract.Build(tag, headline, body, cta, "B", "mailing_visual", imageRequired, personalization, asset == null ? string.Empty : asset.alt), "state", "draft", "ab_testing_enabled", true, "ab_testing_pc", percentage, "active", true), new Dictionary<string, string> { { "contact_list_ids", "mailing_list" }, { "campaign_id", "campaign" } }, imageRequired, personalization, "Variante B en borrador, asociada a lista y campaña UTM.");
                }
            }
            Dictionary<string, object> automation = Child(configuration, "automation");
            if (!Enabled(automation)) return;
            string campaignTitle = ConfigText(automation, "title", "Nutrición comercial", 120);
            int delay = ConfigInt(automation, "delay_days", 2, 0, 365);
            string trigger = ConfigText(automation, "trigger", "begin", 30);
            if (!AutomationTriggers.Contains(trigger)) throw new ConsultantException("invalid_demo_configuration", "El disparador de automatización no está permitido.");
            string targetModel = ConfigText(automation, "target_model", "mailing.contact", 40);
            if (targetModel != "mailing.contact" && targetModel != "crm.lead") throw new ConsultantException("invalid_demo_configuration", "El modelo objetivo solo puede ser crm.lead o mailing.contact.");
            string audienceFilter = ConfigText(automation, "audience_filter", "all_demo_records", 40);
            string compiledDomain = CompileAudienceDomain(targetModel, audienceFilter);
            string templateKey = "mailing_a";
            if (targetModel == "crm.lead")
            {
                templateKey = "automation_mailing";
                AddMailingComponent(client, modules, components, capabilities, warnings, templateKey, Values("subject", "Seguimiento de oportunidad", "preview", "Borrador interno de automatización.", "email_from", "automation-" + tag.ToLowerInvariant() + "@example.invalid", "mailing_type", "mail", "body_arch", MailEditorContract.Build(tag, "Seguimiento comercial", "Este mensaje permanece en borrador y nunca se envía durante la demostración.", "Revisar oportunidad", "AUTO", null, false, "none", string.Empty), "mailing_domain", compiledDomain, "state", "draft", "active", true), new Dictionary<string, string> { { "mailing_model_id", "lookup:model:crm.lead" }, { "campaign_id", "campaign" } }, false, "none", "Plantilla cerrada para crm.lead sin programación ni envío.");
            }
            AddIfAvailable(client, modules, components, capabilities, warnings, "marketing_campaign", "marketing_campaign_inactive", Values("name", "Flujo comercial", "title", campaignTitle, "active", false, "state", "draft", "domain", compiledDomain), new Dictionary<string, string> { { "model_id", "lookup:model:" + targetModel } }, "inactiva y en borrador", "Campaña de automatización instalada sin procesar registros.");
            if (components.Any(x => x.key == "marketing_campaign") && components.Any(x => x.key == templateKey)) AddIfAvailable(client, modules, components, capabilities, warnings, "marketing_activity", "marketing_activity_inactive", Values("activity_type", "email", "interval_number", delay, "interval_type", "days", "trigger_type", trigger), new Dictionary<string, string> { { "campaign_id", "marketing_campaign" }, { "mass_mailing_id", templateKey } }, "contenida por campaña inactiva", "Actividad de correo enlazada a un borrador; no se ejecuta.");
            else warnings.Add("marketing_activity_inactive: requiere campaña inactiva y borrador A disponibles");
            string branch = ConfigText(automation, "branch_trigger", "mail_open", 30);
            if (branch != "mail_open" && branch != "mail_not_open" && branch != "mail_click" && branch != "mail_not_click") throw new ConsultantException("invalid_demo_configuration", "La rama de automatización no está permitida.");
            if (components.Any(x => x.key == "marketing_activity") && components.Any(x => x.key == templateKey)) AddIfAvailable(client, modules, components, capabilities, warnings, "marketing_activity_branch", "marketing_activity_inactive", Values("activity_type", "email", "interval_number", 1, "interval_type", "days", "trigger_type", branch), new Dictionary<string, string> { { "campaign_id", "marketing_campaign" }, { "parent_id", "marketing_activity" }, { "mass_mailing_id", templateKey } }, "rama contenida por campaña inactiva", "Rama visual por apertura o clic; permanece sin ejecutar.");
        }

        private void AddMailingComponent(OdooClient client, IDictionary<string, string> modules, IList<DemoComponent> components, IDictionary<string, string> capabilities, IList<string> warnings, string key, Dictionary<string, object> desired, Dictionary<string, string> references, bool imageRequired, string personalization, string expected)
        {
            MailEditorContract.RequireValid(Convert.ToString(desired["body_arch"], CultureInfo.InvariantCulture), imageRequired, personalization, imageRequired);
            AddIfAvailable(client, modules, components, capabilities, warnings, key, "mailing_draft", desired, references, "borrador editable, no enviado", expected);
            DemoComponent component = components.FirstOrDefault(x => x.key == key);
            if (component != null)
            {
                component.image_required = imageRequired;
                component.personalization_variable = personalization;
                component.editor_contract = MailEditorContract.ContractVersion;
                component.editor_validation = "expected_editor_ready";
            }
        }

        private void AddIfAvailable(OdooClient client, IDictionary<string, string> moduleStates, IList<DemoComponent> components, IDictionary<string, string> capabilities, IList<string> warnings, string key, string adapter, Dictionary<string, object> desired, Dictionary<string, string> references, string initialState, string expected)
        {
            DemoAdapterContract contract = DemoAdapterCatalog.Get(adapter);
            string missingModule = contract.modules.FirstOrDefault(x => !Installed(moduleStates, x));
            if (missingModule != null) { warnings.Add(adapter + ": módulo no instalado " + missingModule); return; }
            try
            {
                Dictionary<string, object> fields = Fields(client, contract.model);
                if (adapter == "stock_move_draft" && desired.ContainsKey("name") && !fields.ContainsKey("name") && fields.ContainsKey("description_picking"))
                {
                    desired["description_picking"] = desired["name"];
                    desired.Remove("name");
                }
                bool read = SafeAccess(client, contract.model, "read");
                bool create = SafeAccess(client, contract.model, "create");
                bool write = SafeAccess(client, contract.model, "write");
                if (!read || !create || !write) { warnings.Add(adapter + ": permisos efectivos insuficientes"); return; }
                Dictionary<string, object> values = DemoAdapterCatalog.FilterAndValidateValues(contract, desired, fields, true);
                EnsureRequiredValues(contract, values, references);
                ValidateSelections(values, fields);
                Dictionary<string, object> descriptor = DemoAdapterCatalog.CapabilityDescriptor(contract, fields, read, create, write);
                string capabilityHash = JsonUtil.CanonicalHash(descriptor);
                capabilities[adapter] = capabilityHash;
                components.Add(new DemoComponent
                {
                    key = key, adapter = adapter, status = "planned", values = values,
                    references = references ?? new Dictionary<string, string>(StringComparer.Ordinal),
                    initial_state = initialState, expected_result = expected, cleanup_strategy = contract.cleanup_strategy,
                    capability_hash = capabilityHash,
                    commercial_fields = values.Keys.Where(x => x != "arch" && x != "body_arch" && x != "domain").OrderBy(x => x, StringComparer.Ordinal).ToList()
                });
            }
            catch (ConsultantException error) { warnings.Add(adapter + ": " + error.Code); }
        }

        private string CurrentCapabilities(OdooClient client, IEnumerable<DemoComponent> components)
        {
            Dictionary<string, string> parts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (DemoComponent component in components)
            {
                DemoAdapterContract contract = DemoAdapterCatalog.Get(component.adapter);
                Dictionary<string, object> fields = Fields(client, contract.model);
                bool read = SafeAccess(client, contract.model, "read");
                bool create = SafeAccess(client, contract.model, "create");
                bool write = SafeAccess(client, contract.model, "write");
                parts[component.adapter] = JsonUtil.CanonicalHash(DemoAdapterCatalog.CapabilityDescriptor(contract, fields, read, create, write));
            }
            return JsonUtil.CanonicalHash(parts);
        }

        private static string CurrentLegacyCapabilities(OdooClient client, IEnumerable<DemoComponent> components)
        {
            Dictionary<string, string> parts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (DemoComponent component in components)
            {
                DemoAdapterContract contract = DemoAdapterCatalog.Get(component.adapter);
                Dictionary<string, object> fields = client.FieldsGet(contract.model, new[] { "type", "required", "readonly", "relation" });
                parts[component.adapter] = JsonUtil.CanonicalHash(JsonUtil.Dictionary("model", contract.model, "fields", fields));
            }
            return JsonUtil.CanonicalHash(parts);
        }

        private static void ResolveReferences(OdooClient client, DemoAdapterContract contract, IDictionary<string, object> values, IDictionary<string, string> references, IDictionary<string, long> ids, IDictionary<string, object> fields, IDictionary<string, object> runtimeReferences)
        {
            if (references == null) return;
            HashSet<string> requiredContract = new HashSet<string>(contract != null && contract.required_contract_fields != null ? contract.required_contract_fields : new string[0], StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> reference in references)
            {
                object rawField;
                if (!fields.TryGetValue(reference.Key, out rawField))
                {
                    if (requiredContract.Contains(reference.Key)) throw new ConsultantException("demo_capability_missing", "Falta un campo relacional requerido por el escenario.");
                    continue;
                }
                Dictionary<string, object> definition = rawField as Dictionary<string, object>;
                string type = definition == null ? string.Empty : JsonUtil.GetString(definition, "type", false);
                string relation = definition == null ? string.Empty : JsonUtil.GetString(definition, "relation", false);
                long target;
                if (reference.Key == "analytic_distribution" && (reference.Value.StartsWith("analytic:", StringComparison.Ordinal) || reference.Value.StartsWith("analytic_runtime:", StringComparison.Ordinal)))
                {
                    string[] analyticParts = reference.Value.Split(':');
                    decimal percentage;
                    if (analyticParts.Length != 3 || type != "json" || !decimal.TryParse(analyticParts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out percentage) || percentage <= 0 || percentage > 100) throw new ConsultantException("demo_analytic_reference_invalid", "La distribución analítica no cumple el contrato cerrado.");
                    if (reference.Value.StartsWith("analytic_runtime:", StringComparison.Ordinal))
                    {
                        object supplied;
                        if (runtimeReferences == null || !runtimeReferences.TryGetValue(analyticParts[1], out supplied) || !long.TryParse(Convert.ToString(supplied, CultureInfo.InvariantCulture), out target) || target <= 0) throw new ConsultantException("demo_runtime_reference_required", "Falta la cuenta analítica tipada " + analyticParts[1] + ".");
                        if (client.Read("account.analytic.account", new[] { target }, new[] { "display_name" }).Count != 1) throw new ConsultantException("demo_runtime_reference_invalid", "La cuenta analítica tipada no existe o no es visible.");
                    }
                    else if (!ids.TryGetValue(analyticParts[1], out target)) throw new ConsultantException("demo_dependency_missing", "No se pudo resolver la cuenta analítica interna.");
                    values[reference.Key] = JsonUtil.Dictionary(target.ToString(CultureInfo.InvariantCulture), (double)percentage);
                    continue;
                }
                if (reference.Value.StartsWith("lookup:model:", StringComparison.Ordinal))
                {
                    string modelName = reference.Value.Substring("lookup:model:".Length);
                    IList<Dictionary<string, object>> rows = client.SearchRead("ir.model", new object[] { new object[] { "model", "=", modelName } }, new[] { "id" }, 2, null);
                    if (rows.Count != 1) throw new ConsultantException("demo_reference_ambiguous", "No se pudo resolver de forma única el modelo técnico del escenario.");
                    target = LongValue(rows[0], "id");
                }
                else if (reference.Value == "lookup:website_single")
                {
                    IList<Dictionary<string, object>> rows = client.SearchRead("website", new object[0], new[] { "id" }, 2, "id asc");
                    if (rows.Count != 1) throw new ConsultantException("demo_reference_ambiguous", "La base ya no tiene un único sitio web; vuelve a preparar el escenario.");
                    target = LongValue(rows[0], "id");
                }
                else if (reference.Value.StartsWith("runtime:", StringComparison.Ordinal))
                {
                    string key = reference.Value.Substring("runtime:".Length);
                    object supplied;
                    if (runtimeReferences == null || !runtimeReferences.TryGetValue(key, out supplied) || !long.TryParse(Convert.ToString(supplied, CultureInfo.InvariantCulture), out target) || target <= 0) throw new ConsultantException("demo_runtime_reference_required", "Falta la referencia tipada " + key + " para aplicar el escenario.");
                    if (string.IsNullOrWhiteSpace(relation) || client.Read(relation, new[] { target }, new[] { "display_name" }).Count != 1) throw new ConsultantException("demo_runtime_reference_invalid", "La referencia tipada no existe o no es visible en el modelo esperado.");
                }
                else if (!ids.TryGetValue(reference.Value, out target)) throw new ConsultantException("demo_dependency_missing", "No se pudo resolver una dependencia interna del escenario.");
                values[reference.Key] = type == "many2many" || type == "one2many" ? new object[] { new object[] { 6, 0, new object[] { target } } } : (object)target;
            }
        }

        private static IList<Dictionary<string, object>> FindExisting(OdooClient client, DemoAdapterContract contract, IDictionary<string, object> values, IDictionary<string, object> fields)
        {
            List<object> domain = new List<object>();
            string identity = DemoAdapterCatalog.IdentityField(contract, values);
            if (identity != null) domain.Add(new object[] { identity, "=", values[identity] });
            foreach (KeyValuePair<string, object> pair in values)
            {
                if (pair.Key == identity || domain.Count >= 3) continue;
                Dictionary<string, object> definition = fields.ContainsKey(pair.Key) ? fields[pair.Key] as Dictionary<string, object> : null;
                string type = definition == null ? string.Empty : JsonUtil.GetString(definition, "type", false);
                if (type == "many2one" && pair.Value is long) domain.Add(new object[] { pair.Key, "=", pair.Value });
            }
            if (domain.Count == 0) return new List<Dictionary<string, object>>();
            return client.SearchRead(contract.model, domain.ToArray(), new[] { "id" }, 2, null);
        }

        private sealed class ComponentVerification
        {
            public bool database_verified;
            public bool editor_ready;
            public string detail;
        }

        private static ComponentVerification VerifyComponent(OdooClient client, DemoComponent component, long id, IDictionary<string, long> ids)
        {
            DemoAdapterContract contract = DemoAdapterCatalog.Get(component.adapter);
            Dictionary<string, object> metadata = Fields(client, contract.model);
            List<string> fields = contract.verification_fields.Where(x => metadata.ContainsKey(x)).ToList();
            if (!fields.Contains("display_name") && metadata.ContainsKey("display_name")) fields.Add("display_name");
            IList<Dictionary<string, object>> rows = client.Read(contract.model, new[] { id }, fields);
            if (rows.Count != 1) return new ComponentVerification { database_verified = false, editor_ready = contract.key != "mailing_draft", detail = "record_missing" };
            Dictionary<string, object> row = rows[0];
            foreach (KeyValuePair<string, object> expected in component.values)
            {
                if (contract.key == "mailing_draft" && expected.Key == "body_arch") continue;
                if ((contract.key == "mailing_visual_asset" || contract.key == "demo_private_attachment") && (expected.Key == "res_model" || expected.Key == "res_id" || expected.Key == "datas")) continue;
                if (!fields.Contains(expected.Key) || (component.references != null && component.references.ContainsKey(expected.Key))) continue;
                object actual;
                if (!row.TryGetValue(expected.Key, out actual)) return new ComponentVerification { database_verified = false, editor_ready = contract.key != "mailing_draft", detail = "field_missing" };
                Dictionary<string, object> definition = metadata.ContainsKey(expected.Key) ? metadata[expected.Key] as Dictionary<string, object> : null;
                if (!EquivalentFieldValue(actual, expected.Value, definition)) return new ComponentVerification { database_verified = false, editor_ready = contract.key != "mailing_draft", detail = "field_mismatch" };
            }
            foreach (KeyValuePair<string, string> reference in component.references ?? new Dictionary<string, string>())
            {
                if (reference.Value.StartsWith("runtime:", StringComparison.Ordinal) || reference.Value.StartsWith("analytic_runtime:", StringComparison.Ordinal)) continue;
                if (!fields.Contains(reference.Key)) continue;
                object actual;
                if (!row.TryGetValue(reference.Key, out actual)) return new ComponentVerification { database_verified = false, editor_ready = contract.key != "mailing_draft", detail = "relation_missing" };
                if (reference.Value.StartsWith("analytic:", StringComparison.Ordinal))
                {
                    string[] parts = reference.Value.Split(':'); long expectedAnalytic;
                    if (parts.Length != 3 || !ids.TryGetValue(parts[1], out expectedAnalytic) || !AnalyticContains(actual, expectedAnalytic, parts[2])) return new ComponentVerification { database_verified = false, editor_ready = contract.key != "mailing_draft", detail = "analytic_relation_mismatch" };
                    continue;
                }
                long expectedId;
                Dictionary<string, object> relationDefinition = metadata.ContainsKey(reference.Key) ? metadata[reference.Key] as Dictionary<string, object> : null;
                string relationType = relationDefinition == null ? string.Empty : JsonUtil.GetString(relationDefinition, "type", false);
                if (ids.TryGetValue(reference.Value, out expectedId) && (relationType == "many2many" || relationType == "one2many" ? !ReferenceContains(actual, expectedId) : ReferenceId(actual) != expectedId)) return new ComponentVerification { database_verified = false, editor_ready = contract.key != "mailing_draft", detail = "relation_mismatch" };
            }
            if (contract.key != "mailing_draft") return new ComponentVerification { database_verified = true, editor_ready = true, detail = (contract.key == "mailing_visual_asset" || contract.key == "demo_private_attachment") ? "internal_resource_exists" : "database_verified" };
            object rawBody;
            string body = row.TryGetValue("body_arch", out rawBody) ? Convert.ToString(rawBody, CultureInfo.InvariantCulture) : string.Empty;
            MailEditorValidation editor = MailEditorContract.Validate(body, component.image_required, component.personalization_variable ?? "none", false);
            if (component.image_required)
            {
                long assetId;
                if (!ids.TryGetValue("mailing_visual", out assetId) || body.IndexOf("/web/content/" + assetId.ToString(CultureInfo.InvariantCulture) + "?download=false", StringComparison.Ordinal) < 0) editor.editor_ready = false;
            }
            return new ComponentVerification { database_verified = true, editor_ready = editor.editor_ready, detail = editor.editor_ready ? "dom_read_verified" : editor.error_code ?? "internal_asset_mismatch" };
        }

        private static long ReferenceId(object value)
        {
            IEnumerable sequence = value as IEnumerable;
            if (sequence != null && !(value is string)) foreach (object item in sequence) { long id; return long.TryParse(Convert.ToString(item, CultureInfo.InvariantCulture), out id) ? id : 0; }
            long scalar; return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out scalar) ? scalar : 0;
        }

        private static bool ReferenceContains(object value, long expected)
        {
            IEnumerable sequence = value as IEnumerable;
            if (sequence == null || value is string) return ReferenceId(value) == expected;
            foreach (object item in sequence)
            {
                if (item is IEnumerable && !(item is string)) { if (ReferenceContains(item, expected)) return true; }
                else { long parsed; if (long.TryParse(Convert.ToString(item, CultureInfo.InvariantCulture), out parsed) && parsed == expected) return true; }
            }
            return false;
        }

        private static bool AnalyticContains(object value, long expectedId, string expectedPercentage)
        {
            IDictionary dictionary = value as IDictionary;
            if (dictionary == null) return false;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (!string.Equals(Convert.ToString(entry.Key, CultureInfo.InvariantCulture), expectedId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)) continue;
                decimal actual, expected;
                return decimal.TryParse(Convert.ToString(entry.Value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out actual)
                    && decimal.TryParse(expectedPercentage, NumberStyles.Float, CultureInfo.InvariantCulture, out expected) && actual == expected;
            }
            return false;
        }

        private static Dictionary<string, object> CleanupValues(DemoAdapterContract contract, IDictionary<string, object> fields)
        {
            Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.Ordinal);
            if (contract.cleanup_strategy == "unpublish_and_deactivate")
            {
                if (fields.ContainsKey("is_published")) values["is_published"] = false;
                if (fields.ContainsKey("active")) values["active"] = false;
            }
            else if (contract.cleanup_strategy == "deactivate" && fields.ContainsKey("active")) values["active"] = false;
            else if (contract.cleanup_strategy == "detach_and_unpublish")
            {
                if (fields.ContainsKey("public")) values["public"] = false;
                if (fields.ContainsKey("res_model")) values["res_model"] = false;
                if (fields.ContainsKey("res_id")) values["res_id"] = 0;
            }
            return values;
        }

        private void AddAssetBinaryAtApply(DemoScenario scenario, DemoComponent component, IDictionary<string, object> values)
        {
            Dictionary<string, object> current = ValidateOperationalAsset(project.DemoDirectory(scenario.demo_slug, false), JsonUtil.Dictionary("archivo", Path.GetFileName(component.asset_file ?? string.Empty), "sha256", component.asset_sha256, "mimetype", component.asset_mimetype));
            if (Convert.ToInt64(current["size"], CultureInfo.InvariantCulture) != component.asset_size) throw new ConsultantException("demo_asset_changed", "El tamaño del recurso cambió desde la vista previa.");
            byte[] bytes = File.ReadAllBytes(Convert.ToString(current["absolute_file"], CultureInfo.InvariantCulture));
            try { values["datas"] = Convert.ToBase64String(bytes); }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }

        private static Dictionary<string, object> ValidateOperationalAsset(string demoDirectory, IDictionary<string, object> source)
        {
            if (string.IsNullOrWhiteSpace(demoDirectory)) throw new ConsultantException("demo_asset_directory_required", "El recurso requiere la carpeta real de la demostración.");
            string file = JsonUtil.GetString(source, source.ContainsKey("archivo") ? "archivo" : "file", true);
            string hash = (JsonUtil.GetString(source, "sha256", true) ?? string.Empty).ToLowerInvariant();
            string mime = JsonUtil.GetString(source, "mimetype", false);
            if (!Regex.IsMatch(file ?? string.Empty, "^[A-Za-z0-9][A-Za-z0-9._-]{0,119}\\.(png|jpg|jpeg|webp|pdf)$", RegexOptions.IgnoreCase) || !Regex.IsMatch(hash, "^[a-f0-9]{64}$")) throw new ConsultantException("demo_asset_invalid", "El nombre o hash del recurso sintético no es válido.");
            string extension = Path.GetExtension(file).ToLowerInvariant();
            string expectedMime = extension == ".png" ? "image/png" : (extension == ".jpg" || extension == ".jpeg") ? "image/jpeg" : extension == ".webp" ? "image/webp" : extension == ".pdf" ? "application/pdf" : null;
            if (expectedMime == null || (!string.IsNullOrWhiteSpace(mime) && mime != expectedMime)) throw new ConsultantException("demo_asset_mimetype_invalid", "El tipo declarado no coincide con la extensión permitida.");
            string assets = Path.GetFullPath(Path.Combine(demoDirectory, "assets"));
            string path = Path.GetFullPath(Path.Combine(assets, file));
            string prefix = assets.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) throw new ConsultantException("demo_asset_not_found", "El recurso debe existir bajo demostraciones/<slug>/assets.");
            FileInfo info = new FileInfo(path);
            if (info.Length < 1 || info.Length > 10 * 1024 * 1024) throw new ConsultantException("demo_asset_size_invalid", "El recurso debe medir entre 1 byte y 10 MB.");
            string actual;
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            using (FileStream stream = File.OpenRead(path)) actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            if (actual != hash) throw new ConsultantException("demo_asset_hash_mismatch", "El recurso cambió o no coincide con el SHA-256 declarado.");
            return JsonUtil.Dictionary("file", file, "absolute_file", path, "sha256", hash, "mimetype", expectedMime, "size", info.Length);
        }

        private static void ResolveAndValidateMailBody(DemoComponent component, IDictionary<string, object> values, IDictionary<string, long> ids)
        {
            string body = Convert.ToString(values["body_arch"], CultureInfo.InvariantCulture);
            if (component.image_required)
            {
                long attachmentId;
                if (!ids.TryGetValue("mailing_visual", out attachmentId)) throw new ConsultantException("demo_dependency_missing", "La imagen interna debe crearse antes del mailing.");
                body = MailEditorContract.ResolveAssetUrl(body, "mailing_visual", attachmentId);
                values["body_arch"] = body;
            }
            MailEditorContract.RequireValid(body, component.image_required, component.personalization_variable ?? "none", false);
        }

        private static void EnsureRequiredValues(DemoAdapterContract contract, IDictionary<string, object> values, IDictionary<string, string> references)
        {
            foreach (string field in contract.required_contract_fields)
            {
                if (!values.ContainsKey(field) && (references == null || !references.ContainsKey(field))) throw new ConsultantException("demo_required_value_missing", "Falta el valor o referencia segura " + field + " para el adaptador " + contract.key + ".");
            }
        }

        private void ValidateSourceLock(DemoScenario scenario)
        {
            string path = Path.Combine(project.DemoDirectory(scenario.demo_slug, false), "source-lock.json");
            if (!File.Exists(path)) throw new ConsultantException("source_lock_changed", "Ya no existe el bloqueo de fuentes usado por el escenario.");
            Dictionary<string, object> current = JsonUtil.ReadJsonFile<Dictionary<string, object>>(path);
            if (JsonUtil.GetString(current, "lock_hash", true) != scenario.source_lock_hash || JsonUtil.Sha256(File.ReadAllText(path, Encoding.UTF8)) != scenario.source_lock_file_hash) throw new ConsultantException("source_lock_changed", "El bloqueo de fuentes cambió desde la vista previa.");
        }

        private static void ValidateSyntheticValues(DemoAdapterContract contract, IDictionary<string, object> values, int schemaVersion)
        {
            if (schemaVersion < 2) return;
            string identity = DemoAdapterCatalog.IdentityField(contract, values);
            if (schemaVersion <= 3 && identity != null && !Convert.ToString(values[identity], CultureInfo.InvariantCulture).StartsWith("[DEMO:", StringComparison.Ordinal)) throw new ConsultantException("demo_marker_required", "Los escenarios heredados v2/v3 requieren su marcador original.");
            foreach (KeyValuePair<string, object> pair in values)
            {
                string scalar = pair.Value as string;
                if (schemaVersion >= 4 && scalar != null && (scalar.IndexOf("[DEMO:", StringComparison.OrdinalIgnoreCase) >= 0 || Regex.IsMatch(scalar, "\\bDEMO\\s+[0-9]+\\b", RegexOptions.IgnoreCase) || Regex.IsMatch(scalar, "demo-[a-f0-9]{8,}", RegexOptions.IgnoreCase))) throw new ConsultantException("demo_visible_marker_blocked", "Los escenarios v4 no permiten identificadores técnicos en campos visibles.");
                if (pair.Key.IndexOf("email", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string email = Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
                    if (!email.EndsWith("@example.invalid", StringComparison.OrdinalIgnoreCase)) throw new ConsultantException("demo_email_not_safe", "Los correos de demo deben usar example.invalid.");
                }
            }
            string websitePrefix = schemaVersion >= 4 ? "/marketing-" : "/demo-";
            if (contract.key == "website_landing_unpublished" && (JsonUtil.GetBool(values, "is_published", false) || !Convert.ToString(values["url"], CultureInfo.InvariantCulture).StartsWith(websitePrefix, StringComparison.Ordinal))) throw new ConsultantException("demo_website_state_unsafe", "La landing debe quedar no publicada y usar la ruta técnica aprobada.");
            if (contract.key == "mailing_draft" && Convert.ToString(values["state"], CultureInfo.InvariantCulture) != "draft") throw new ConsultantException("demo_external_effect_blocked", "Los mailings solo pueden crearse en borrador.");
            if (contract.key == "marketing_campaign_inactive" && (JsonUtil.GetBool(values, "active", false) || Convert.ToString(values["state"], CultureInfo.InvariantCulture) != "draft")) throw new ConsultantException("demo_external_effect_blocked", "La automatización debe permanecer inactiva y en borrador.");
        }

        private static string CompileAudienceDomain(string model, string filter)
        {
            if (filter == "all_demo_records") return "[]";
            if (filter == "has_email") return model == "crm.lead" ? "[('email_from', '!=', False)]" : "[('email', '!=', False)]";
            throw new ConsultantException("invalid_demo_configuration", "El filtro de audiencia no pertenece al DSL cerrado.");
        }

        private static void ValidateSelections(IDictionary<string, object> values, IDictionary<string, object> fields)
        {
            foreach (KeyValuePair<string, object> pair in values)
            {
                Dictionary<string, object> definition = fields.ContainsKey(pair.Key) ? fields[pair.Key] as Dictionary<string, object> : null;
                if (definition == null || JsonUtil.GetString(definition, "type", false) != "selection" || !definition.ContainsKey("selection")) continue;
                HashSet<string> allowed = new HashSet<string>(StringComparer.Ordinal);
                IEnumerable options = definition["selection"] as IEnumerable;
                if (options != null) foreach (object option in options)
                {
                    IEnumerable pairValues = option as IEnumerable;
                    if (pairValues == null || option is string) continue;
                    foreach (object item in pairValues) { allowed.Add(Convert.ToString(item, CultureInfo.InvariantCulture)); break; }
                }
                if (pair.Value == null || pair.Value is bool && !(bool)pair.Value) continue;
                if (allowed.Count > 0 && !allowed.Contains(Convert.ToString(pair.Value, CultureInfo.InvariantCulture))) throw new ConsultantException("demo_selection_not_allowed", "Un valor técnico de selección no es válido en la base destino.");
            }
        }

        private static void ApplyFixedTechnicalValues(DemoAdapterContract contract, IDictionary<string, object> values, IDictionary<string, object> fields)
        {
            // En Odoo 18 una sección es una survey.question cuya selección
            // question_type debe enviarse explícitamente vacía. Este valor es
            // fijo del adaptador y nunca puede ser elegido por el modelo.
            if (contract.key == "survey_section" && fields.ContainsKey("question_type")) values["question_type"] = false;
        }

        private static void ApplyRuntimeTechnicalValues(DemoAdapterContract contract, IDictionary<string, object> values, IDictionary<string, object> fields, IDictionary<string, long> ids)
        {
            if (contract.key != "marketing_activity_inactive") return;
            object rawSource;
            Dictionary<string, object> sourceField = fields.TryGetValue("source_id", out rawSource) ? rawSource as Dictionary<string, object> : null;
            if (sourceField != null && JsonUtil.GetBool(sourceField, "required", false))
            {
                long sourceId;
                if (!ids.TryGetValue("source", out sourceId) || sourceId <= 0) throw new ConsultantException("demo_dependency_missing", "La actividad requiere la fuente UTM sintética del escenario.");
                if (JsonUtil.GetString(sourceField, "type", false) != "many2one" || JsonUtil.GetString(sourceField, "relation", false) != "utm.source") throw new ConsultantException("demo_capability_missing", "source_id no tiene la relación UTM esperada.");
                values["source_id"] = sourceId;
            }
            object rawValidity;
            Dictionary<string, object> validityField = fields.TryGetValue("validity_duration_type", out rawValidity) ? rawValidity as Dictionary<string, object> : null;
            if (validityField != null && JsonUtil.GetBool(validityField, "required", false))
            {
                if (JsonUtil.GetString(validityField, "type", false) != "selection" || !SelectionContains(validityField, "days")) throw new ConsultantException("demo_selection_not_allowed", "La base no admite el valor técnico days para la vigencia de la actividad.");
                values["validity_duration_type"] = "days";
            }
        }

        private static bool SelectionContains(IDictionary<string, object> definition, string expected)
        {
            object raw;
            IEnumerable options = definition != null && definition.TryGetValue("selection", out raw) ? raw as IEnumerable : null;
            if (options == null) return false;
            foreach (object option in options)
            {
                IEnumerable pair = option as IEnumerable;
                if (pair == null || option is string) continue;
                foreach (object item in pair)
                {
                    if (string.Equals(Convert.ToString(item, CultureInfo.InvariantCulture), expected, StringComparison.Ordinal)) return true;
                    break;
                }
            }
            return false;
        }

        private static bool EquivalentFieldValue(object actual, object expected, IDictionary<string, object> definition)
        {
            string type = definition == null ? string.Empty : JsonUtil.GetString(definition, "type", false);
            if (type == "integer")
            {
                long left, right;
                return long.TryParse(Convert.ToString(actual, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out left)
                    && long.TryParse(Convert.ToString(expected, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out right) && left == right;
            }
            if (type == "float" || type == "monetary")
            {
                decimal left, right;
                return decimal.TryParse(Convert.ToString(actual, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out left)
                    && decimal.TryParse(Convert.ToString(expected, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out right) && left == right;
            }
            if (type == "boolean")
            {
                bool left, right;
                return bool.TryParse(Convert.ToString(actual, CultureInfo.InvariantCulture), out left)
                    && bool.TryParse(Convert.ToString(expected, CultureInfo.InvariantCulture), out right) && left == right;
            }
            return JsonUtil.CanonicalHash(actual) == JsonUtil.CanonicalHash(expected);
        }

        private static Dictionary<string, object> SurveyQuestionInitialValues(IDictionary<string, object> values)
        {
            Dictionary<string, object> initial = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (string key in new[] { "survey_id", "title", "sequence" }) if (values.ContainsKey(key)) initial[key] = values[key];
            if (!initial.ContainsKey("survey_id") || !initial.ContainsKey("title")) throw new ConsultantException("demo_required_value_missing", "La pregunta no contiene encuesta y título para su creación mínima segura.");
            return initial;
        }

        private static Dictionary<string, object> SurveyQuestionFinalValues(IDictionary<string, object> values)
        {
            Dictionary<string, object> final = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> pair in values) if (pair.Key != "survey_id" && pair.Key != "title" && pair.Key != "sequence") final[pair.Key] = pair.Value;
            return final;
        }

        private static long CreateSurveyQuestionThroughParent(OdooClient client, DemoAdapterContract contract, IDictionary<string, object> values, IDictionary<string, object> questionFields)
        {
            object rawSurveyId;
            long surveyId;
            if (!values.TryGetValue("survey_id", out rawSurveyId) || !long.TryParse(Convert.ToString(rawSurveyId, CultureInfo.InvariantCulture), out surveyId) || surveyId <= 0) throw new ConsultantException("demo_dependency_missing", "No se pudo resolver la encuesta padre para crear la pregunta.");
            Dictionary<string, object> surveyFields = Fields(client, "survey.survey");
            object rawRelation;
            Dictionary<string, object> relation = surveyFields.TryGetValue("question_and_page_ids", out rawRelation) ? rawRelation as Dictionary<string, object> : null;
            if (relation == null || JsonUtil.GetString(relation, "type", false) != "one2many" || JsonUtil.GetString(relation, "relation", false) != "survey.question") throw new ConsultantException("demo_capability_missing", "La encuesta destino no expone la relación nativa de preguntas esperada.");
            if (!client.CheckAccessRights("survey.survey", "write")) throw new ConsultantException("odoo_permission_denied", "El usuario no tiene permiso efectivo para añadir preguntas a la encuesta.");
            if (client.Read("survey.survey", new[] { surveyId }, new[] { "id" }).Count != 1) throw new ConsultantException("demo_dependency_missing", "La encuesta padre ya no existe o no es visible.");
            Dictionary<string, object> nestedValues = new Dictionary<string, object>(values, StringComparer.Ordinal);
            nestedValues.Remove("survey_id");
            object[] commands = new object[] { new object[] { 0, 0, nestedValues } };
            if (!client.Write("survey.survey", new[] { surveyId }, JsonUtil.Dictionary("question_and_page_ids", commands))) throw new ConsultantException("odoo_write_failed", "Odoo no confirmó la creación de la pregunta desde la encuesta padre.");
            IList<Dictionary<string, object>> created = FindExisting(client, contract, values, questionFields);
            if (created.Count != 1) throw new ConsultantException("demo_created_record_unresolved", "Odoo creó la pregunta pero no pudo resolverla de forma única por su marcador y encuesta.");
            return LongValue(created[0], "id");
        }

        private DemoComponent CompileOperationalComponent(IDictionary<string, object> source, string key, IList<Dictionary<string, object>> referencePlan, string demoDirectory)
        {
            string type = (JsonUtil.GetString(source, "tipo", true) ?? string.Empty).Trim().ToLowerInvariant();
            HashSet<string> common = new HashSet<string>(new[] { "clave", "tipo", "nombre", "correo", "telefono", "ciudad", "direccion", "referencia", "codigo", "cantidad", "precio_unitario", "descuento", "porcentaje_analitico", "porcentaje", "valor_residual", "fecha", "fecha_limite", "fecha_alerta", "descripcion", "tipo_producto", "seguimiento", "secuencia", "dias", "tipo_ubicacion", "subtipo", "placa", "vin", "servicio_seguimiento", "periodicidad", "recepcion", "entrega", "archivo", "sha256", "mimetype", "relaciones" }, StringComparer.Ordinal);
            foreach (string field in source.Keys) if (!common.Contains(field)) throw new ConsultantException("invalid_operational_demo", "El parámetro " + field + " no pertenece al contrato operativo cerrado.");
            Dictionary<string, object> relations = Child(source, "relaciones");
            Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.Ordinal);
            Dictionary<string, string> references = new Dictionary<string, string>(StringComparer.Ordinal);
            string adapter;
            string state;
            string expected;
            string name = OptionalText(source, "nombre", 300);
            string description = OptionalText(source, "descripcion", 2000);
            Dictionary<string, object> assetDescriptor = null;

            if (type == "empresa_cliente" || type == "empresa_proveedor")
            {
                RequireRelations(relations, new string[0], type);
                adapter = "business_company"; Require(name, "nombre", type);
                values["name"] = name; values["company_type"] = "company"; values["active"] = true;
                values["customer_rank"] = type == "empresa_cliente" ? 1 : 0; values["supplier_rank"] = type == "empresa_proveedor" ? 1 : 0;
                AddOptional(values, "email", SafeDemoEmail(source)); AddOptional(values, "phone", OptionalText(source, "telefono", 80));
                AddOptional(values, "city", OptionalText(source, "ciudad", 120)); AddOptional(values, "street", OptionalText(source, "direccion", 300));
                state = "contacto comercial activo"; expected = "Tercero sintético disponible con su rol comercial.";
            }
            else if (type == "contacto")
            {
                RequireRelations(relations, new[] { "empresa" }, type); adapter = "business_contact"; Require(name, "nombre", type);
                values["name"] = name; values["company_type"] = "person"; values["type"] = "contact"; values["active"] = true;
                AddOptional(values, "email", SafeDemoEmail(source)); AddOptional(values, "phone", OptionalText(source, "telefono", 80));
                AddOptional(values, "function", description); AddReference(references, "parent_id", relations, "empresa", referencePlan, "res.partner");
                state = "contacto activo"; expected = "Contacto sintético relacionado con su empresa.";
            }
            else if (type == "plan_analitico")
            {
                RequireRelations(relations, new[] { "padre" }, type, true); adapter = "analytic_plan"; Require(name, "nombre", type);
                values["name"] = name; AddOptional(values, "description", description);
                AddReferenceOptional(references, "parent_id", relations, "padre", referencePlan, "account.analytic.plan");
                state = "plan analítico disponible"; expected = "Plan analítico visible en la jerarquía de la empresa demo.";
            }
            else if (type == "cuenta_analitica")
            {
                RequireRelations(relations, new[] { "plan", "cliente" }, type, true); adapter = "analytic_account"; Require(name, "nombre", type);
                values["name"] = name; values["active"] = true; AddOptional(values, "code", OptionalText(source, "codigo", 80));
                AddReference(references, "plan_id", relations, "plan", referencePlan, "account.analytic.plan");
                AddReferenceOptional(references, "partner_id", relations, "cliente", referencePlan, "res.partner");
                state = "cuenta analítica activa"; expected = "Proyecto o pozo disponible para distribuciones analíticas.";
            }
            else if (type == "modelo_distribucion_analitica")
            {
                RequireRelations(relations, new[] { "cuenta_analitica", "cliente" }, type, true); adapter = "analytic_distribution_rule";
                values["sequence"] = source.ContainsKey("secuencia") ? (int)Number(source, "secuencia", 0, 9999) : 10;
                AddReferenceOptional(references, "partner_id", relations, "cliente", referencePlan, "res.partner");
                if (!relations.ContainsKey("cuenta_analitica")) throw new ConsultantException("demo_required_value_missing", "Falta la cuenta analítica del modelo de distribución.");
                AddAnalyticReference(references, relations, source, referencePlan);
                state = "modelo de distribución activo"; expected = "Distribución analítica automática disponible para el tercero seleccionado.";
            }
            else if (type == "categoria_valoracion_inventario")
            {
                RequireRelations(relations, new[] { "padre", "cuenta_valoracion", "cuenta_entrada", "cuenta_salida" }, type, true); adapter = "valuation_product_category"; values["name"] = Require(name, "nombre", type); string method = OptionalText(source, "subtipo", 20) ?? "fifo"; if (method != "fifo" && method != "average") throw new ConsultantException("invalid_operational_demo", "subtipo de valoración debe ser fifo o average."); values["property_cost_method"] = method; values["property_valuation"] = "real_time";
                AddReferenceOptional(references, "parent_id", relations, "padre", referencePlan, "product.category"); AddReferenceOptional(references, "property_stock_valuation_account_id", relations, "cuenta_valoracion", referencePlan, "account.account"); AddReferenceOptional(references, "property_stock_account_input_categ_id", relations, "cuenta_entrada", referencePlan, "account.account"); AddReferenceOptional(references, "property_stock_account_output_categ_id", relations, "cuenta_salida", referencePlan, "account.account"); state = "categoría con valoración automática"; expected = "Categoría sintética disponible con método FIFO o promedio.";
            }
            else if (type == "producto_servicio" || type == "producto_almacenable" || type == "producto_servicio_proyecto" || type == "producto_costo_destino")
            {
                RequireRelations(relations, new[] { "proyecto_plantilla", "categoria_producto", "ruta" }, type, true); adapter = type == "producto_servicio_proyecto" ? "demo_service_project_product" : type == "producto_costo_destino" ? "landed_cost_product" : "demo_product"; Require(name, "nombre", type);
                string tracking = OptionalText(source, "seguimiento", 20) ?? "none";
                if (tracking != "none" && tracking != "lot" && tracking != "serial") throw new ConsultantException("invalid_operational_demo", "seguimiento debe ser none, lot o serial.");
                values["name"] = name; values["default_code"] = RequireText(source, "codigo", 80, type); values["sale_ok"] = true; values["purchase_ok"] = true; values["active"] = true;
                values["type"] = type == "producto_almacenable" ? "consu" : "service";
                if (adapter == "demo_product") { values["is_storable"] = type == "producto_almacenable"; values["tracking"] = type == "producto_almacenable" ? tracking : "none"; AddReferenceOptional(references, "categ_id", relations, "categoria_producto", referencePlan, "product.category"); AddReferenceOptional(references, "route_ids", relations, "ruta", referencePlan, "stock.route"); }
                else if (adapter == "landed_cost_product") { values["landed_cost_ok"] = true; values["split_method_landed_cost"] = OptionalText(source, "subtipo", 40) ?? "by_quantity"; }
                else
                {
                    string serviceTracking = OptionalText(source, "servicio_seguimiento", 40) ?? "project_only";
                    if (serviceTracking != "project_only" && serviceTracking != "task_in_project") throw new ConsultantException("invalid_operational_demo", "servicio_seguimiento debe ser project_only o task_in_project.");
                    values["service_tracking"] = serviceTracking; AddReferenceOptional(references, "project_template_id", relations, "proyecto_plantilla", referencePlan, "project.project");
                }
                if (source.ContainsKey("precio_unitario")) values["list_price"] = Number(source, "precio_unitario", 0, 1000000000000m);
                state = type == "producto_almacenable" ? "producto almacenable" : type == "producto_servicio_proyecto" ? "servicio que genera proyecto" : type == "producto_costo_destino" ? "servicio de costo en destino" : "servicio vendible y comprable"; expected = "Producto sintético listo para documentos comerciales.";
            }
            else if (type == "tarifa_proveedor")
            {
                RequireRelations(relations, new[] { "proveedor", "producto", "moneda" }, type, true); adapter = "supplier_price";
                values["product_code"] = RequireText(source, "codigo", 120, type); values["product_name"] = Require(name, "nombre", type);
                values["price"] = (double)Number(source, "precio_unitario", 0, 1000000000000m); values["min_qty"] = source.ContainsKey("cantidad") ? (double)Number(source, "cantidad", 0, 1000000000m) : 0d;
                values["delay"] = source.ContainsKey("dias") ? (int)Number(source, "dias", 0, 3650) : 1; values["sequence"] = source.ContainsKey("secuencia") ? (int)Number(source, "secuencia", 0, 9999) : 1;
                if (source.ContainsKey("descuento")) values["discount"] = (double)Number(source, "descuento", 0, 100);
                AddOptional(values, "date_start", OptionalDate(source, "fecha")); AddOptional(values, "date_end", OptionalDate(source, "fecha_limite"));
                AddReference(references, "partner_id", relations, "proveedor", referencePlan, "res.partner"); AddReference(references, "product_id", relations, "producto", referencePlan, "product.product"); AddReferenceOptional(references, "currency_id", relations, "moneda", referencePlan, "res.currency");
                state = "tarifa de proveedor activa"; expected = "Precio, plazo y cantidad mínima del proveedor disponibles para compras.";
            }
            else if (type == "lista_precios")
            {
                RequireRelations(relations, new[] { "moneda" }, type); adapter = "product_pricelist"; values["name"] = Require(name, "nombre", type); values["active"] = true; values["sequence"] = source.ContainsKey("secuencia") ? (int)Number(source, "secuencia", 0, 9999) : 16;
                AddReference(references, "currency_id", relations, "moneda", referencePlan, "res.currency"); state = "lista de precios activa"; expected = "Lista de precios comercial disponible en la moneda elegida.";
            }
            else if (type == "regla_lista_precios")
            {
                RequireRelations(relations, new[] { "lista_precios", "producto" }, type); adapter = "product_pricelist_item";
                values["applied_on"] = "0_product_variant"; values["compute_price"] = "fixed"; values["fixed_price"] = (double)Number(source, "precio_unitario", 0, 1000000000000m); values["min_quantity"] = source.ContainsKey("cantidad") ? (double)Number(source, "cantidad", 0, 1000000000m) : 0d;
                AddOptional(values, "date_start", OptionalDateTime(source, "fecha")); AddOptional(values, "date_end", OptionalDateTime(source, "fecha_limite"));
                AddReference(references, "pricelist_id", relations, "lista_precios", referencePlan, "product.pricelist"); AddReference(references, "product_id", relations, "producto", referencePlan, "product.product");
                state = "regla de precio activa"; expected = "Precio fijo del producto disponible en la lista seleccionada.";
            }
            else if (type == "proyecto")
            {
                RequireRelations(relations, new[] { "cliente", "cuenta_analitica" }, type, true); adapter = "project_project"; Require(name, "nombre", type);
                values["name"] = name; values["active"] = true; values["privacy_visibility"] = "employees"; AddOptional(values, "description", description);
                AddReferenceOptional(references, "partner_id", relations, "cliente", referencePlan, "res.partner"); AddReferenceOptional(references, "account_id", relations, "cuenta_analitica", referencePlan, "account.analytic.account");
                state = "proyecto activo"; expected = "Proyecto sintético relacionado con cliente y analítica.";
            }
            else if (type == "tarea")
            {
                RequireRelations(relations, new[] { "proyecto", "cliente" }, type, true); adapter = "project_task"; Require(name, "nombre", type);
                values["name"] = name; values["active"] = true; AddOptional(values, "description", description); AddOptional(values, "date_deadline", OptionalDate(source, "fecha_limite"));
                if (source.ContainsKey("cantidad")) values["planned_hours"] = (double)Number(source, "cantidad", 0, 100000m);
                AddReference(references, "project_id", relations, "proyecto", referencePlan, "project.project"); AddReferenceOptional(references, "partner_id", relations, "cliente", referencePlan, "res.partner");
                state = "tarea abierta"; expected = "Tarea de campo visible dentro del proyecto.";
            }
            else if (type == "oportunidad" || type == "iniciativa")
            {
                RequireRelations(relations, new[] { "cliente", "equipo_ventas", "fuente", "medio", "campana" }, type, true); adapter = "crm_opportunity"; Require(name, "nombre", type);
                values["name"] = name; values["type"] = type == "iniciativa" ? "lead" : "opportunity"; values["active"] = true; AddOptional(values, "description", description); AddOptional(values, "email_from", SafeDemoEmail(source)); AddOptional(values, "phone", OptionalText(source, "telefono", 80));
                if (source.ContainsKey("precio_unitario")) values["expected_revenue"] = (double)Number(source, "precio_unitario", 0, 1000000000000m);
                AddReferenceOptional(references, "partner_id", relations, "cliente", referencePlan, "res.partner"); AddReferenceOptional(references, "team_id", relations, "equipo_ventas", referencePlan, "crm.team");
                AddReferenceOptional(references, "source_id", relations, "fuente", referencePlan, "utm.source"); AddReferenceOptional(references, "medium_id", relations, "medio", referencePlan, "utm.medium"); AddReferenceOptional(references, "campaign_id", relations, "campana", referencePlan, "utm.campaign");
                state = type == "iniciativa" ? "iniciativa activa" : "oportunidad activa"; expected = type == "iniciativa" ? "Iniciativa sintética lista para convertir y vincular al cliente mediante un flujo confirmado." : "Oportunidad comercial relacionada con el cliente y su atribución.";
            }
            else if (type == "cotizacion_venta")
            {
                RequireRelations(relations, new[] { "cliente", "oportunidad", "equipo_ventas", "fuente", "medio", "campana", "lista_precios", "posicion_fiscal", "metodo_entrega", "proyecto", "cuenta_analitica" }, type, true); adapter = "sale_quotation";
                values["client_order_ref"] = RequireText(source, "referencia", 120, type); AddOptional(values, "note", description); AddOptional(values, "date_order", OptionalDateTime(source, "fecha")); AddOptional(values, "validity_date", OptionalDate(source, "fecha_limite"));
                AddReference(references, "partner_id", relations, "cliente", referencePlan, "res.partner"); AddReferenceOptional(references, "opportunity_id", relations, "oportunidad", referencePlan, "crm.lead"); AddReferenceOptional(references, "team_id", relations, "equipo_ventas", referencePlan, "crm.team");
                AddReferenceOptional(references, "source_id", relations, "fuente", referencePlan, "utm.source"); AddReferenceOptional(references, "medium_id", relations, "medio", referencePlan, "utm.medium"); AddReferenceOptional(references, "campaign_id", relations, "campana", referencePlan, "utm.campaign");
                AddReferenceOptional(references, "pricelist_id", relations, "lista_precios", referencePlan, "product.pricelist"); AddReferenceOptional(references, "fiscal_position_id", relations, "posicion_fiscal", referencePlan, "account.fiscal.position"); AddReferenceOptional(references, "carrier_id", relations, "metodo_entrega", referencePlan, "delivery.carrier"); AddReferenceOptional(references, "project_id", relations, "proyecto", referencePlan, "project.project"); AddReferenceOptional(references, "project_account_id", relations, "cuenta_analitica", referencePlan, "account.analytic.account");
                state = "cotización en borrador"; expected = "Cotización sintética lista para mostrar sin confirmar.";
            }
            else if (type == "linea_venta")
            {
                RequireRelations(relations, new[] { "documento", "producto", "cuenta_analitica" }, type, true); adapter = "sale_order_line";
                values["name"] = Require(name, "nombre", type); values["product_uom_qty"] = (double)Number(source, "cantidad", 0.0001m, 1000000000m); values["price_unit"] = (double)Number(source, "precio_unitario", 0, 1000000000000m);
                if (source.ContainsKey("descuento")) values["discount"] = (double)Number(source, "descuento", 0, 100);
                AddReference(references, "order_id", relations, "documento", referencePlan, "sale.order"); AddReference(references, "product_id", relations, "producto", referencePlan, "product.product"); AddAnalyticReference(references, relations, source, referencePlan);
                state = "línea de cotización"; expected = "Línea comercial con cantidad, precio y trazabilidad analítica.";
            }
            else if (type == "solicitud_cotizacion_compra")
            {
                RequireRelations(relations, new[] { "proveedor", "acuerdo_compra" }, type, true); adapter = "purchase_rfq";
                values["partner_ref"] = RequireText(source, "referencia", 120, type); AddOptional(values, "notes", description); AddOptional(values, "date_order", OptionalDateTime(source, "fecha")); AddOptional(values, "date_planned", OptionalDateTime(source, "fecha_limite"));
                AddReference(references, "partner_id", relations, "proveedor", referencePlan, "res.partner"); AddReferenceOptional(references, "requisition_id", relations, "acuerdo_compra", referencePlan, "purchase.requisition"); state = "solicitud de cotización en borrador"; expected = "Solicitud de cotización sintética lista para comparar y confirmar manualmente.";
            }
            else if (type == "linea_compra")
            {
                RequireRelations(relations, new[] { "documento", "producto", "cuenta_analitica" }, type, true); adapter = "purchase_order_line";
                values["name"] = Require(name, "nombre", type); values["product_qty"] = (double)Number(source, "cantidad", 0.0001m, 1000000000m); values["price_unit"] = (double)Number(source, "precio_unitario", 0, 1000000000000m); AddOptional(values, "date_planned", OptionalDateTime(source, "fecha_limite"));
                if (source.ContainsKey("descuento")) values["discount"] = (double)Number(source, "descuento", 0, 100);
                AddReference(references, "order_id", relations, "documento", referencePlan, "purchase.order"); AddReference(references, "product_id", relations, "producto", referencePlan, "product.product"); AddAnalyticReference(references, relations, source, referencePlan);
                state = "línea de compra"; expected = "Línea de compra con cantidad, precio y trazabilidad analítica.";
            }
            else if (type == "acuerdo_compra")
            {
                RequireRelations(relations, new[] { "proveedor", "usuario" }, type, true); adapter = "purchase_agreement"; values["reference"] = RequireText(source, "referencia", 200, type); values["requisition_type"] = "blanket_order"; values["active"] = true; AddOptional(values, "description", description); AddOptional(values, "date_start", OptionalDate(source, "fecha")); AddOptional(values, "date_end", OptionalDate(source, "fecha_limite"));
                AddReferenceOptional(references, "vendor_id", relations, "proveedor", referencePlan, "res.partner"); AddReferenceOptional(references, "user_id", relations, "usuario", referencePlan, "res.users"); state = "acuerdo de compra en borrador"; expected = "Acuerdo marco sintético listo para comparar ofertas y confirmar.";
            }
            else if (type == "linea_acuerdo_compra")
            {
                RequireRelations(relations, new[] { "documento", "producto", "cuenta_analitica" }, type, true); adapter = "purchase_agreement_line"; values["product_qty"] = (double)Number(source, "cantidad", 0.0001m, 1000000000m); values["price_unit"] = (double)Number(source, "precio_unitario", 0.0001m, 1000000000000m); AddOptional(values, "product_description_variants", name);
                AddReference(references, "requisition_id", relations, "documento", referencePlan, "purchase.requisition"); AddReference(references, "product_id", relations, "producto", referencePlan, "product.product"); AddAnalyticReference(references, relations, source, referencePlan); state = "línea de acuerdo"; expected = "Producto, cantidad y precio incluidos en el acuerdo de compra.";
            }
            else if (type == "factura_cliente_borrador" || type == "factura_proveedor_borrador" || type == "nota_credito_cliente_borrador" || type == "nota_credito_proveedor_borrador")
            {
                RequireRelations(relations, new[] { "cliente", "proveedor", "diario" }, type, true); adapter = "account_invoice_draft";
                bool customerDocument = type == "factura_cliente_borrador" || type == "nota_credito_cliente_borrador";
                bool refund = type == "nota_credito_cliente_borrador" || type == "nota_credito_proveedor_borrador";
                values["move_type"] = customerDocument ? (refund ? "out_refund" : "out_invoice") : (refund ? "in_refund" : "in_invoice"); values["invoice_date"] = RequireDate(source, "fecha", type); values["ref"] = RequireText(source, "referencia", 200, type); AddOptional(values, "narration", description); AddOptional(values, "invoice_date_due", OptionalDate(source, "fecha_limite"));
                AddReference(references, "partner_id", relations, customerDocument ? "cliente" : "proveedor", referencePlan, "res.partner"); AddReferenceOptional(references, "journal_id", relations, "diario", referencePlan, "account.journal");
                state = refund ? "nota crédito en borrador" : "factura en borrador"; expected = "Documento contable sintético visible sin publicar ni afectar pagos.";
            }
            else if (type == "linea_factura")
            {
                RequireRelations(relations, new[] { "documento", "producto", "cuenta", "cuenta_analitica" }, type, true); adapter = "account_invoice_line";
                values["name"] = Require(name, "nombre", type); values["quantity"] = (double)Number(source, "cantidad", 0.0001m, 1000000000m); values["price_unit"] = (double)Number(source, "precio_unitario", 0, 1000000000000m);
                if (source.ContainsKey("descuento")) values["discount"] = (double)Number(source, "descuento", 0, 100);
                AddReference(references, "move_id", relations, "documento", referencePlan, "account.move"); AddReferenceOptional(references, "product_id", relations, "producto", referencePlan, "product.product"); AddReferenceOptional(references, "account_id", relations, "cuenta", referencePlan, "account.account"); AddAnalyticReference(references, relations, source, referencePlan);
                state = "línea de factura en borrador"; expected = "Línea contable calculable y vinculada al documento padre.";
            }
            else if (type == "pago_cliente_borrador" || type == "pago_proveedor_borrador")
            {
                RequireRelations(relations, new[] { "cliente", "proveedor", "diario", "metodo_pago", "cuenta", "moneda", "documento" }, type, true); adapter = "account_payment_draft"; bool inbound = type == "pago_cliente_borrador";
                values["payment_type"] = inbound ? "inbound" : "outbound"; values["partner_type"] = inbound ? "customer" : "supplier"; values["amount"] = (double)Number(source, "precio_unitario", 0.01m, 1000000000000m); values["date"] = RequireDate(source, "fecha", type); values["payment_reference"] = RequireText(source, "referencia", 200, type); AddOptional(values, "memo", name);
                AddReference(references, "partner_id", relations, inbound ? "cliente" : "proveedor", referencePlan, "res.partner"); AddReference(references, "journal_id", relations, "diario", referencePlan, "account.journal"); AddReferenceOptional(references, "payment_method_line_id", relations, "metodo_pago", referencePlan, "account.payment.method.line"); AddReferenceOptional(references, "destination_account_id", relations, "cuenta", referencePlan, "account.account"); AddReferenceOptional(references, "currency_id", relations, "moneda", referencePlan, "res.currency"); AddReferenceOptional(references, "invoice_ids", relations, "documento", referencePlan, "account.move");
                state = "pago en borrador"; expected = "Pago sintético vinculado al tercero y listo para contabilizar por autorización separada.";
            }
            else if (type == "movimiento_extracto_bancario")
            {
                RequireRelations(relations, new[] { "diario", "cliente" }, type, true); adapter = "bank_statement_line"; values["payment_ref"] = RequireText(source, "referencia", 200, type); decimal bankAmount = Number(source, "precio_unitario", 0.01m, 1000000000000m); string direction = OptionalText(source, "subtipo", 20) ?? "entrada"; if (direction != "entrada" && direction != "salida") throw new ConsultantException("invalid_operational_demo", "subtipo del movimiento bancario debe ser entrada o salida."); values["amount"] = (double)(direction == "salida" ? -bankAmount : bankAmount); values["date"] = RequireDate(source, "fecha", type);
                AddReference(references, "journal_id", relations, "diario", referencePlan, "account.journal"); AddReferenceOptional(references, "partner_id", relations, "cliente", referencePlan, "res.partner"); state = "movimiento bancario sin conciliar"; expected = "Movimiento de extracto sintético disponible para demostrar conciliación.";
            }
            else if (type == "asiento_contable_borrador")
            {
                RequireRelations(relations, new[] { "diario" }, type); adapter = "account_journal_entry_draft";
                values["move_type"] = "entry"; values["date"] = RequireDate(source, "fecha", type); values["ref"] = RequireText(source, "referencia", 200, type); AddOptional(values, "narration", description); AddReference(references, "journal_id", relations, "diario", referencePlan, "account.journal");
                state = "asiento en borrador"; expected = "Asiento contable balanceado preparado sin publicar.";
            }
            else if (type == "linea_asiento_debito" || type == "linea_asiento_credito")
            {
                RequireRelations(relations, new[] { "documento", "cuenta", "cliente", "cuenta_analitica" }, type, true); adapter = "account_journal_line";
                values["name"] = Require(name, "nombre", type); decimal amount = Number(source, "precio_unitario", 0.01m, 1000000000000m); values[type == "linea_asiento_debito" ? "debit" : "credit"] = (double)amount; values[type == "linea_asiento_debito" ? "credit" : "debit"] = 0d;
                AddReference(references, "move_id", relations, "documento", referencePlan, "account.move"); AddReference(references, "account_id", relations, "cuenta", referencePlan, "account.account"); AddReferenceOptional(references, "partner_id", relations, "cliente", referencePlan, "res.partner"); AddAnalyticReference(references, relations, source, referencePlan);
                state = "apunte en borrador"; expected = "Apunte contable sintético relacionado y verificable.";
            }
            else if (type == "transferencia_inventario_borrador")
            {
                RequireRelations(relations, new[] { "cliente", "tipo_operacion", "ubicacion_origen", "ubicacion_destino" }, type, true); adapter = "stock_picking_draft";
                values["origin"] = RequireText(source, "referencia", 200, type); values["move_type"] = "direct"; AddOptional(values, "note", description); AddOptional(values, "scheduled_date", OptionalDateTime(source, "fecha"));
                AddReferenceOptional(references, "partner_id", relations, "cliente", referencePlan, "res.partner"); AddReference(references, "picking_type_id", relations, "tipo_operacion", referencePlan, "stock.picking.type"); AddReference(references, "location_id", relations, "ubicacion_origen", referencePlan, "stock.location"); AddReference(references, "location_dest_id", relations, "ubicacion_destino", referencePlan, "stock.location");
                state = "transferencia en borrador"; expected = "Transferencia sintética lista para explicar reserva y validación manual.";
            }
            else if (type == "movimiento_inventario")
            {
                RequireRelations(relations, new[] { "documento", "producto", "ubicacion_origen", "ubicacion_destino" }, type); adapter = "stock_move_draft";
                values["name"] = Require(name, "nombre", type); values["product_uom_qty"] = (double)Number(source, "cantidad", 0.0001m, 1000000000m);
                AddReference(references, "picking_id", relations, "documento", referencePlan, "stock.picking"); AddReference(references, "product_id", relations, "producto", referencePlan, "product.product"); AddReference(references, "location_id", relations, "ubicacion_origen", referencePlan, "stock.location"); AddReference(references, "location_dest_id", relations, "ubicacion_destino", referencePlan, "stock.location");
                state = "movimiento en borrador"; expected = "Movimiento de inventario asociado a la transferencia.";
            }
            else if (type == "cantidad_ejecutada_movimiento")
            {
                RequireRelations(relations, new[] { "documento", "movimiento", "producto", "ubicacion_origen", "ubicacion_destino", "lote" }, type, true); adapter = "stock_move_line_done";
                values["quantity"] = (double)Number(source, "cantidad", 0.0001m, 1000000000m); values["picked"] = true;
                AddReference(references, "picking_id", relations, "documento", referencePlan, "stock.picking"); AddReference(references, "move_id", relations, "movimiento", referencePlan, "stock.move"); AddReference(references, "product_id", relations, "producto", referencePlan, "product.product");
                AddReference(references, "location_id", relations, "ubicacion_origen", referencePlan, "stock.location"); AddReference(references, "location_dest_id", relations, "ubicacion_destino", referencePlan, "stock.location"); AddReferenceOptional(references, "lot_id", relations, "lote", referencePlan, "stock.lot");
                state = "cantidad ejecutada preparada"; expected = "Cantidad realizada lista para validar una entrega total o parcial.";
            }
            else if (type == "ubicacion_inventario")
            {
                RequireRelations(relations, new[] { "ubicacion_padre" }, type); adapter = "stock_location"; values["name"] = Require(name, "nombre", type); values["active"] = true;
                string usage = OptionalText(source, "tipo_ubicacion", 30) ?? "internal";
                if (usage != "internal" && usage != "transit" && usage != "inventory") throw new ConsultantException("invalid_operational_demo", "tipo_ubicacion debe ser internal, transit o inventory.");
                values["usage"] = usage; AddOptional(values, "barcode", OptionalText(source, "codigo", 80)); AddReference(references, "location_id", relations, "ubicacion_padre", referencePlan, "stock.location");
                state = "ubicación activa"; expected = "Ubicación logística disponible dentro de la jerarquía seleccionada.";
            }
            else if (type == "almacen")
            {
                RequireRelations(relations, new[] { "empresa" }, type); adapter = "stock_warehouse"; values["name"] = Require(name, "nombre", type); values["code"] = RequireText(source, "codigo", 5, type).ToUpperInvariant(); values["active"] = true;
                string reception = OptionalText(source, "recepcion", 30) ?? "one_step"; string delivery = OptionalText(source, "entrega", 30) ?? "ship_only";
                if (!new[] { "one_step", "two_steps", "three_steps" }.Contains(reception, StringComparer.Ordinal)) throw new ConsultantException("invalid_operational_demo", "recepcion debe ser one_step, two_steps o three_steps.");
                if (!new[] { "ship_only", "pick_ship", "pick_pack_ship" }.Contains(delivery, StringComparer.Ordinal)) throw new ConsultantException("invalid_operational_demo", "entrega debe ser ship_only, pick_ship o pick_pack_ship.");
                values["reception_steps"] = reception; values["delivery_steps"] = delivery; values["buy_to_resupply"] = true; AddReference(references, "company_id", relations, "empresa", referencePlan, "res.company");
                state = "almacén activo"; expected = "Almacén sintético con pasos de recepción y entrega configurados.";
            }
            else if (type == "ruta_logistica")
            {
                RequireRelations(relations, new[] { "empresa" }, type, true); adapter = "stock_route"; values["name"] = Require(name, "nombre", type); values["active"] = true; values["sequence"] = source.ContainsKey("secuencia") ? (int)Number(source, "secuencia", 0, 9999) : 10;
                string scope = OptionalText(source, "subtipo", 30) ?? "product"; if (scope != "product" && scope != "sale" && scope != "warehouse") throw new ConsultantException("invalid_operational_demo", "subtipo de ruta debe ser product, sale o warehouse.");
                values["product_selectable"] = scope == "product"; values["sale_selectable"] = scope == "sale"; values["warehouse_selectable"] = scope == "warehouse"; AddReferenceOptional(references, "company_id", relations, "empresa", referencePlan, "res.company");
                state = "ruta logística activa"; expected = "Ruta sintética disponible para productos, ventas o almacenes según el alcance elegido.";
            }
            else if (type == "regla_ruta_logistica")
            {
                RequireRelations(relations, new[] { "ruta", "ubicacion_origen", "ubicacion_destino", "tipo_operacion", "empresa" }, type, true); adapter = "stock_rule"; values["name"] = Require(name, "nombre", type); values["active"] = true;
                string ruleAction = OptionalText(source, "subtipo", 30) ?? "pull"; if (!new[] { "pull", "push", "pull_push", "buy", "manufacture" }.Contains(ruleAction, StringComparer.Ordinal)) throw new ConsultantException("invalid_operational_demo", "subtipo de regla logística no pertenece al catálogo cerrado.");
                values["action"] = ruleAction; values["procure_method"] = "make_to_stock"; values["delay"] = source.ContainsKey("dias") ? (int)Number(source, "dias", 0, 3650) : 0; values["propagate_cancel"] = true;
                AddReference(references, "route_id", relations, "ruta", referencePlan, "stock.route"); AddReferenceOptional(references, "location_src_id", relations, "ubicacion_origen", referencePlan, "stock.location"); AddReference(references, "location_dest_id", relations, "ubicacion_destino", referencePlan, "stock.location"); AddReferenceOptional(references, "picking_type_id", relations, "tipo_operacion", referencePlan, "stock.picking.type"); AddReferenceOptional(references, "company_id", relations, "empresa", referencePlan, "res.company");
                state = "regla logística activa"; expected = "Regla sintética relacionada con su ruta y ubicaciones.";
            }
            else if (type == "lote_con_vencimiento")
            {
                RequireRelations(relations, new[] { "producto" }, type); adapter = "stock_lot_expiring"; values["name"] = RequireText(source, "codigo", 120, type); values["expiration_date"] = Require(OptionalDateTime(source, "fecha_limite"), "fecha_limite", type);
                AddOptional(values, "alert_date", OptionalDateTime(source, "fecha_alerta")); AddOptional(values, "use_date", OptionalDateTime(source, "fecha")); AddReference(references, "product_id", relations, "producto", referencePlan, "product.product");
                state = "lote trazable con vencimiento"; expected = "Lote sintético disponible para mostrar caducidad y alerta.";
            }
            else if (type == "existencia_inicial")
            {
                RequireRelations(relations, new[] { "producto", "ubicacion_origen", "lote", "usuario" }, type, true); adapter = "stock_inventory_quant"; values["inventory_quantity"] = (double)Number(source, "cantidad", 0.0001m, 1000000000m); AddOptional(values, "inventory_date", OptionalDate(source, "fecha"));
                AddReference(references, "product_id", relations, "producto", referencePlan, "product.product"); AddReference(references, "location_id", relations, "ubicacion_origen", referencePlan, "stock.location"); AddReferenceOptional(references, "lot_id", relations, "lote", referencePlan, "stock.lot"); AddReferenceOptional(references, "user_id", relations, "usuario", referencePlan, "res.users"); state = "conteo de inventario preparado"; expected = "Cantidad sintética lista para aplicar al inventario con autorización separada.";
            }
            else if (type == "metodo_entrega")
            {
                RequireRelations(relations, new[] { "producto_entrega" }, type); adapter = "delivery_method"; values["name"] = Require(name, "nombre", type); values["delivery_type"] = "fixed"; values["active"] = true;
                if (source.ContainsKey("precio_unitario")) values["fixed_price"] = (double)Number(source, "precio_unitario", 0, 1000000000000m);
                AddReference(references, "product_id", relations, "producto_entrega", referencePlan, "product.product"); state = "método de entrega activo"; expected = "Método de entrega de precio fijo disponible para cotizaciones.";
            }
            else if (type == "vehiculo_flota")
            {
                RequireRelations(relations, new[] { "modelo_vehiculo", "conductor" }, type, true); adapter = "fleet_vehicle"; values["license_plate"] = RequireText(source, "placa", 80, type); values["active"] = true; AddOptional(values, "vin_sn", OptionalText(source, "vin", 120));
                if (source.ContainsKey("cantidad")) values["odometer"] = (double)Number(source, "cantidad", 0, 1000000000m);
                AddReference(references, "model_id", relations, "modelo_vehiculo", referencePlan, "fleet.vehicle.model"); AddReferenceOptional(references, "driver_id", relations, "conductor", referencePlan, "res.partner");
                state = "vehículo activo"; expected = "Vehículo sintético disponible en Flota con placa y modelo.";
            }
            else if (type == "costo_destino_borrador")
            {
                RequireRelations(relations, new[] { "documento", "diario", "factura_proveedor" }, type, true); adapter = "stock_landed_cost"; values["date"] = RequireDate(source, "fecha", type); values["target_model"] = "picking"; AddOptional(values, "description", description);
                AddReference(references, "picking_ids", relations, "documento", referencePlan, "stock.picking"); AddReference(references, "account_journal_id", relations, "diario", referencePlan, "account.journal"); AddReferenceOptional(references, "vendor_bill_id", relations, "factura_proveedor", referencePlan, "account.move"); state = "costo en destino en borrador"; expected = "Costo en destino relacionado con una recepción y listo para calcular.";
            }
            else if (type == "linea_costo_destino")
            {
                RequireRelations(relations, new[] { "documento", "producto", "cuenta" }, type, true); adapter = "stock_landed_cost_line"; values["name"] = Require(name, "nombre", type); values["price_unit"] = (double)Number(source, "precio_unitario", 0.01m, 1000000000000m); string split = OptionalText(source, "subtipo", 40) ?? "by_quantity";
                if (split != "equal" && split != "by_quantity" && split != "by_current_cost_price" && split != "by_weight" && split != "by_volume") throw new ConsultantException("invalid_operational_demo", "subtipo de distribución de costo no permitido."); values["split_method"] = split;
                AddReference(references, "cost_id", relations, "documento", referencePlan, "stock.landed.cost"); AddReference(references, "product_id", relations, "producto", referencePlan, "product.product"); AddReferenceOptional(references, "account_id", relations, "cuenta", referencePlan, "account.account"); state = "línea de costo en destino"; expected = "Importe adicional preparado con su método de distribución.";
            }
            else if (type == "actividad_oportunidad")
            {
                RequireRelations(relations, new[] { "oportunidad", "tipo_actividad", "usuario" }, type); adapter = "crm_activity"; values["summary"] = Require(name, "nombre", type); values["note"] = description ?? string.Empty; values["date_deadline"] = RequireDate(source, "fecha_limite", type);
                references["res_model_id"] = "lookup:model:crm.lead"; AddReference(references, "res_id", relations, "oportunidad", referencePlan, "crm.lead"); AddReference(references, "activity_type_id", relations, "tipo_actividad", referencePlan, "mail.activity.type"); AddReference(references, "user_id", relations, "usuario", referencePlan, "res.users");
                state = "actividad planificada"; expected = "Actividad comercial pendiente y relacionada con la oportunidad.";
            }
            else if (type == "diario_contable")
            {
                RequireRelations(relations, new[] { "moneda" }, type, true); adapter = "account_journal"; values["name"] = Require(name, "nombre", type); values["code"] = RequireText(source, "codigo", 10, type).ToUpperInvariant(); values["active"] = true;
                string journalType = OptionalText(source, "subtipo", 30) ?? "general";
                if (journalType != "general" && journalType != "bank" && journalType != "cash" && journalType != "credit" && journalType != "sale" && journalType != "purchase") throw new ConsultantException("invalid_operational_demo", "subtipo de diario no permitido.");
                values["type"] = journalType; AddReferenceOptional(references, "currency_id", relations, "moneda", referencePlan, "res.currency"); state = "diario activo"; expected = "Diario contable sintético disponible para movimientos de demostración.";
            }
            else if (type == "motivo_perdida")
            {
                RequireRelations(relations, new string[0], type); adapter = "crm_lost_reason"; values["name"] = Require(name, "nombre", type); values["active"] = true;
                state = "motivo de pérdida activo"; expected = "Motivo sintético disponible para cerrar oportunidades perdidas.";
            }
            else if (type == "posicion_fiscal")
            {
                RequireRelations(relations, new string[0], type); adapter = "fiscal_position"; values["name"] = Require(name, "nombre", type); values["active"] = true; values["sequence"] = source.ContainsKey("secuencia") ? (int)Number(source, "secuencia", 0, 9999) : 10; values["auto_apply"] = false; AddOptional(values, "note", description);
                state = "posición fiscal manual activa"; expected = "Posición fiscal sintética disponible sin aplicación automática.";
            }
            else if (type == "mapeo_cuenta_posicion_fiscal")
            {
                RequireRelations(relations, new[] { "posicion_fiscal", "cuenta_origen", "cuenta_destino" }, type); adapter = "fiscal_position_account_map";
                AddReference(references, "position_id", relations, "posicion_fiscal", referencePlan, "account.fiscal.position"); AddReference(references, "account_src_id", relations, "cuenta_origen", referencePlan, "account.account"); AddReference(references, "account_dest_id", relations, "cuenta_destino", referencePlan, "account.account");
                state = "mapeo contable configurado"; expected = "Mapeo de cuentas de la posición fiscal disponible para la demostración.";
            }
            else if (type == "mapeo_impuesto_posicion_fiscal")
            {
                RequireRelations(relations, new[] { "posicion_fiscal", "impuesto_origen", "empresa", "pais" }, type, true); adapter = "fiscal_position_tax_map"; values["name"] = Require(name, "nombre", type); values["active"] = true; values["amount"] = (double)Number(source, "porcentaje", -100, 100); values["amount_type"] = "percent";
                string taxUse = OptionalText(source, "subtipo", 20) ?? "sale"; if (taxUse != "sale" && taxUse != "purchase" && taxUse != "none") throw new ConsultantException("invalid_operational_demo", "subtipo del impuesto debe ser sale, purchase o none."); values["type_tax_use"] = taxUse;
                AddReference(references, "original_tax_ids", relations, "impuesto_origen", referencePlan, "account.tax"); AddReference(references, "fiscal_position_ids", relations, "posicion_fiscal", referencePlan, "account.fiscal.position"); AddReferenceOptional(references, "company_id", relations, "empresa", referencePlan, "res.company"); AddReferenceOptional(references, "country_id", relations, "pais", referencePlan, "res.country");
                state = "impuesto sintético mapeado"; expected = "Impuesto destino relacionado con el impuesto origen y la posición fiscal sin modificar impuestos estándar.";
            }
            else if (type == "marca_vehiculo")
            {
                RequireRelations(relations, new string[0], type); adapter = "fleet_vehicle_brand"; values["name"] = Require(name, "nombre", type); values["active"] = true;
                state = "marca activa"; expected = "Marca sintética disponible en Flota.";
            }
            else if (type == "modelo_vehiculo")
            {
                RequireRelations(relations, new[] { "marca_vehiculo" }, type); adapter = "fleet_vehicle_model"; values["name"] = Require(name, "nombre", type); values["active"] = true; AddReference(references, "brand_id", relations, "marca_vehiculo", referencePlan, "fleet.vehicle.model.brand");
                state = "modelo de vehículo activo"; expected = "Modelo sintético relacionado con su marca.";
            }
            else if (type == "plan_comision")
            {
                RequireRelations(relations, new[] { "empresa" }, type); adapter = "commission_plan"; values["name"] = Require(name, "nombre", type); values["active"] = true; values["state"] = "draft";
                string commissionType = OptionalText(source, "subtipo", 30) ?? "target"; if (commissionType == "achievement" || commissionType == "logro") commissionType = "achieve"; if (commissionType == "objetivo") commissionType = "target"; if (commissionType != "target" && commissionType != "achieve") throw new ConsultantException("invalid_operational_demo", "subtipo del plan de comisión debe ser target/objetivo o achieve/logro."); values["type"] = commissionType;
                string frequency = OptionalText(source, "periodicidad", 30) ?? "month"; if (frequency == "monthly" || frequency == "mensual") frequency = "month"; if (frequency == "quarterly" || frequency == "trimestral") frequency = "quarter"; if (frequency == "yearly" || frequency == "anual") frequency = "year"; if (!new[] { "month", "quarter", "year" }.Contains(frequency, StringComparer.Ordinal)) throw new ConsultantException("invalid_operational_demo", "periodicidad del plan debe ser month/mensual, quarter/trimestral o year/anual."); values["periodicity"] = frequency; values["date_from"] = RequireDate(source, "fecha", type); values["date_to"] = RequireDate(source, "fecha_limite", type); if (source.ContainsKey("precio_unitario")) values["commission_amount"] = (double)Number(source, "precio_unitario", 0, 1000000000000m);
                AddReference(references, "company_id", relations, "empresa", referencePlan, "res.company"); state = "plan de comisiones en borrador"; expected = "Plan sintético con periodo, frecuencia y base definidos, listo para aprobación separada.";
            }
            else if (type == "objetivo_comision")
            {
                RequireRelations(relations, new[] { "plan_comision" }, type); adapter = "commission_target"; values["amount"] = (double)Number(source, "precio_unitario", 0, 1000000000000m); AddOptional(values, "payment_date", OptionalDate(source, "fecha")); AddReference(references, "plan_id", relations, "plan_comision", referencePlan, "sale.commission.plan");
                state = "objetivo de comisión"; expected = "Objetivo monetario relacionado con el plan de comisión.";
            }
            else if (type == "nivel_comision")
            {
                RequireRelations(relations, new[] { "plan_comision" }, type); adapter = "commission_level"; values["target_rate"] = (double)Number(source, "porcentaje", 0, 100000m); values["amount"] = (double)Number(source, "precio_unitario", 0, 1000000000000m); if (source.ContainsKey("descuento")) values["amount_rate"] = (double)Number(source, "descuento", 0, 100); AddReference(references, "plan_id", relations, "plan_comision", referencePlan, "sale.commission.plan");
                state = "tramo de comisión"; expected = "Tramo progresivo relacionado con el plan de comisión.";
            }
            else if (type == "logro_comision")
            {
                RequireRelations(relations, new[] { "plan_comision", "producto", "categoria_logro" }, type, true); adapter = "commission_achievement"; string achievementType = RequireText(source, "subtipo", 40, type); values["type"] = achievementType; values["rate"] = (double)Number(source, "porcentaje", 0, 100000m); AddReference(references, "plan_id", relations, "plan_comision", referencePlan, "sale.commission.plan"); AddReferenceOptional(references, "product_id", relations, "producto", referencePlan, "product.product"); AddReferenceOptional(references, "product_categ_id", relations, "categoria_logro", referencePlan, "product.category");
                state = "métrica de logro"; expected = "Métrica de logro cerrada relacionada con el plan de comisión.";
            }
            else if (type == "participante_comision")
            {
                RequireRelations(relations, new[] { "plan_comision", "usuario" }, type); adapter = "commission_participant"; AddReference(references, "plan_id", relations, "plan_comision", referencePlan, "sale.commission.plan"); AddReference(references, "user_id", relations, "usuario", referencePlan, "res.users"); AddOptional(values, "date_from", OptionalDate(source, "fecha")); AddOptional(values, "date_to", OptionalDate(source, "fecha_limite"));
                state = "participante del plan"; expected = "Vendedor sintético o autorizado relacionado con el plan.";
            }
            else if (type == "presupuesto_analitico")
            {
                RequireRelations(relations, new[] { "usuario", "empresa" }, type, true); adapter = "analytic_budget"; values["name"] = Require(name, "nombre", type); values["date_from"] = RequireDate(source, "fecha", type); values["date_to"] = RequireDate(source, "fecha_limite", type); values["budget_type"] = OptionalText(source, "subtipo", 30) ?? "expense"; AddReferenceOptional(references, "user_id", relations, "usuario", referencePlan, "res.users"); AddReferenceOptional(references, "company_id", relations, "empresa", referencePlan, "res.company");
                state = "presupuesto analítico en borrador"; expected = "Presupuesto sintético con periodo y tipo definidos.";
            }
            else if (type == "linea_presupuesto_analitico")
            {
                RequireRelations(relations, new[] { "presupuesto", "cuenta_analitica" }, type); adapter = "analytic_budget_line"; values["budget_amount"] = (double)Number(source, "precio_unitario", 0, 1000000000000m); values["sequence"] = source.ContainsKey("secuencia") ? (int)Number(source, "secuencia", 0, 9999) : 10; AddReference(references, "budget_analytic_id", relations, "presupuesto", referencePlan, "budget.analytic"); AddReference(references, "account_id", relations, "cuenta_analitica", referencePlan, "account.analytic.account");
                state = "línea presupuestaria"; expected = "Importe presupuestado relacionado con la cuenta analítica.";
            }
            else if (type == "activo_contable")
            {
                RequireRelations(relations, new[] { "empresa", "diario", "modelo_activo", "cuenta_analitica" }, type, true); adapter = "account_asset_demo"; values["name"] = Require(name, "nombre", type); values["active"] = true; values["original_value"] = (double)Number(source, "precio_unitario", 0.01m, 1000000000000m); values["salvage_value"] = source.ContainsKey("valor_residual") ? (double)Number(source, "valor_residual", 0, 1000000000000m) : 0d; values["acquisition_date"] = RequireDate(source, "fecha", type); values["method"] = OptionalText(source, "subtipo", 30) ?? "linear"; values["method_number"] = source.ContainsKey("cantidad") ? (int)Number(source, "cantidad", 1, 1200) : 12; string assetPeriod = OptionalText(source, "periodicidad", 30) ?? "1"; if (assetPeriod == "month") assetPeriod = "1"; if (assetPeriod == "year") assetPeriod = "12"; if (assetPeriod != "1" && assetPeriod != "12") throw new ConsultantException("invalid_operational_demo", "periodicidad del activo debe ser 1 (meses) o 12 (años)."); values["method_period"] = assetPeriod; AddReference(references, "company_id", relations, "empresa", referencePlan, "res.company"); AddReferenceOptional(references, "journal_id", relations, "diario", referencePlan, "account.journal"); AddReferenceOptional(references, "model_id", relations, "modelo_activo", referencePlan, "account.asset"); AddAnalyticReference(references, relations, source, referencePlan);
                state = "activo en borrador"; expected = "Activo sintético listo para calcular y validar su plan en un flujo confirmado.";
            }
            else if (type == "prestamo")
            {
                RequireRelations(relations, new[] { "empresa", "diario", "cuenta_largo_plazo", "cuenta_corto_plazo", "cuenta_gasto", "grupo_activo" }, type, true); adapter = "account_loan_demo"; values["name"] = Require(name, "nombre", type); values["active"] = true; values["state"] = "draft"; values["date"] = RequireDate(source, "fecha", type); values["amount_borrowed"] = (double)Number(source, "precio_unitario", 0.01m, 1000000000000m); values["interest"] = source.ContainsKey("porcentaje") ? (double)Number(source, "porcentaje", 0, 100000m) : 0d; values["duration"] = source.ContainsKey("cantidad") ? (int)Number(source, "cantidad", 1, 1200) : 12; AddReference(references, "company_id", relations, "empresa", referencePlan, "res.company"); AddReferenceOptional(references, "journal_id", relations, "diario", referencePlan, "account.journal"); AddReferenceOptional(references, "long_term_account_id", relations, "cuenta_largo_plazo", referencePlan, "account.account"); AddReferenceOptional(references, "short_term_account_id", relations, "cuenta_corto_plazo", referencePlan, "account.account"); AddReferenceOptional(references, "expense_account_id", relations, "cuenta_gasto", referencePlan, "account.account"); AddReferenceOptional(references, "asset_group_id", relations, "grupo_activo", referencePlan, "account.asset.group");
                state = "préstamo en borrador"; expected = "Préstamo sintético listo para calcular y activar su cronograma.";
            }
            else if (type == "linea_prestamo")
            {
                RequireRelations(relations, new[] { "documento" }, type); adapter = "account_loan_line_demo"; values["date"] = RequireDate(source, "fecha", type); values["principal"] = (double)Number(source, "precio_unitario", 0, 1000000000000m); values["interest"] = source.ContainsKey("valor_residual") ? (double)Number(source, "valor_residual", 0, 1000000000000m) : 0d; AddReference(references, "loan_id", relations, "documento", referencePlan, "account.loan");
                state = "cuota de préstamo"; expected = "Cuota sintética relacionada con el préstamo.";
            }
            else if (type == "adjunto_sintetico_privado")
            {
                RequireRelations(relations, new string[0], type); adapter = "demo_private_attachment"; values["name"] = Require(name, "nombre", type); values["type"] = "binary"; values["public"] = false; assetDescriptor = ValidateOperationalAsset(demoDirectory, source); values["datas"] = "__ASSET_AT_APPLY__"; values["mimetype"] = assetDescriptor["mimetype"];
                state = "adjunto privado"; expected = "Archivo sintético privado disponible para vincular a un registro protegido.";
            }
            else if (type == "equipo_mantenimiento")
            {
                RequireRelations(relations, new[] { "cliente" }, type, true); adapter = "maintenance_equipment"; values["name"] = Require(name, "nombre", type); values["serial_no"] = RequireText(source, "codigo", 120, type); values["active"] = true; AddOptional(values, "note", description); AddReferenceOptional(references, "partner_id", relations, "cliente", referencePlan, "res.partner");
                state = "equipo activo"; expected = "Equipo sintético disponible para mantenimiento.";
            }
            else if (type == "solicitud_mantenimiento")
            {
                RequireRelations(relations, new[] { "equipo" }, type); adapter = "maintenance_request"; values["name"] = Require(name, "nombre", type); values["maintenance_type"] = "corrective"; values["active"] = true; AddOptional(values, "description", description); AddOptional(values, "request_date", OptionalDate(source, "fecha")); AddOptional(values, "schedule_date", OptionalDateTime(source, "fecha_limite")); AddReference(references, "equipment_id", relations, "equipo", referencePlan, "maintenance.equipment");
                state = "solicitud correctiva abierta"; expected = "Solicitud de mantenimiento vinculada al equipo.";
            }
            else throw new ConsultantException("demo_operational_type_not_allowed", "El tipo operativo " + type + " no pertenece al catálogo cerrado.");

            DemoComponent compiled = new DemoComponent { key = key, adapter = adapter, status = "planned", values = values, references = references, initial_state = state, expected_result = expected, cleanup_strategy = DemoAdapterCatalog.Get(adapter).cleanup_strategy, commercial_fields = values.Keys.Where(x => x != "active" && x != "datas").OrderBy(x => x, StringComparer.Ordinal).ToList(), database_verified = false, editor_ready = true };
            if (assetDescriptor != null)
            {
                compiled.asset_file = Convert.ToString(assetDescriptor["file"], CultureInfo.InvariantCulture); compiled.asset_sha256 = Convert.ToString(assetDescriptor["sha256"], CultureInfo.InvariantCulture); compiled.asset_mimetype = Convert.ToString(assetDescriptor["mimetype"], CultureInfo.InvariantCulture); compiled.asset_size = Convert.ToInt64(assetDescriptor["size"], CultureInfo.InvariantCulture);
            }
            return compiled;
        }

        private static List<DemoStep> OperationalSteps(IEnumerable<DemoComponent> components)
        {
            return components.Select(x => new DemoStep { key = x.key, title = "Revisar " + x.expected_result.TrimEnd('.').ToLowerInvariant(), actor = "consultor", expected = x.expected_result, component_key = x.key }).ToList();
        }

        private static void WriteOperationalArtifacts(string directory, DemoScenario scenario)
        {
            File.WriteAllText(Path.Combine(directory, "checklist-registros-operativos.md"), "# Checklist de registros operativos\n\n- [ ] Revisar los " + scenario.components.Count + " registros y sus relaciones.\n- [ ] Resolver las referencias tipadas indicadas en memoria.\n- [ ] Confirmar una sola vez con `CREAR-DEMO " + scenario.id + "`.\n- [ ] Verificar cada documento, línea, relación y estado de borrador.\n- [ ] Si el guion exige una transición, preparar un plan separado y esperar `EJECUTAR-DEMO <id>`.\n- [ ] No conectar bancos ni pasarelas; conservar documentos transaccionales o reconstruir staging al finalizar.\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "resultados-operativos.md"), "# Resultados operativos\n\nPendiente de crear y verificar en staging los " + scenario.components.Count + " registros operativos planificados.\n", new UTF8Encoding(false));
        }

        private static string NormalizeComponentKey(string value)
        {
            string key = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (!Regex.IsMatch(key, "^[a-z][a-z0-9_]{1,62}$")) throw new ConsultantException("invalid_operational_demo", "La clave de componente no es válida.");
            return key;
        }

        private static void RequireRelations(IDictionary<string, object> relations, IEnumerable<string> allowed, string type, bool optional = false)
        {
            HashSet<string> safe = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (string key in relations.Keys) if (!safe.Contains(key)) throw new ConsultantException("invalid_operational_demo", "La relación " + key + " no está permitida para " + type + ".");
            if (!optional) foreach (string key in safe) if (!relations.ContainsKey(key)) throw new ConsultantException("demo_required_value_missing", "Falta la relación " + key + " para " + type + ".");
        }

        private static void AddReference(IDictionary<string, string> target, string field, IDictionary<string, object> relations, string logical, IList<Dictionary<string, object>> plan, string model)
        {
            object raw; if (!relations.TryGetValue(logical, out raw) || string.IsNullOrWhiteSpace(Convert.ToString(raw, CultureInfo.InvariantCulture))) throw new ConsultantException("demo_required_value_missing", "Falta la relación " + logical + ".");
            string value = Convert.ToString(raw, CultureInfo.InvariantCulture).Trim(); ValidateReferenceValue(value); target[field] = value; AddRuntimePlan(value, plan, model);
        }

        private static void AddReferenceOptional(IDictionary<string, string> target, string field, IDictionary<string, object> relations, string logical, IList<Dictionary<string, object>> plan, string model)
        {
            if (relations == null || !relations.ContainsKey(logical) || relations[logical] == null || string.IsNullOrWhiteSpace(Convert.ToString(relations[logical], CultureInfo.InvariantCulture))) return;
            AddReference(target, field, relations, logical, plan, model);
        }

        private static void AddAnalyticReference(IDictionary<string, string> target, IDictionary<string, object> relations, IDictionary<string, object> source, IList<Dictionary<string, object>> plan)
        {
            if (relations == null || !relations.ContainsKey("cuenta_analitica")) return;
            string value = Convert.ToString(relations["cuenta_analitica"], CultureInfo.InvariantCulture).Trim(); ValidateReferenceValue(value);
            decimal percentage = source.ContainsKey("porcentaje_analitico") ? Number(source, "porcentaje_analitico", 0.01m, 100m) : 100m;
            target["analytic_distribution"] = value.StartsWith("runtime:", StringComparison.Ordinal)
                ? "analytic_runtime:" + value.Substring("runtime:".Length) + ":" + percentage.ToString(CultureInfo.InvariantCulture)
                : "analytic:" + value + ":" + percentage.ToString(CultureInfo.InvariantCulture);
            AddRuntimePlan(value, plan, "account.analytic.account");
        }

        private static void ValidateReferenceValue(string value)
        {
            if (Regex.IsMatch(value ?? string.Empty, "^[a-z][a-z0-9_]{1,62}$")) return;
            if (Regex.IsMatch(value ?? string.Empty, "^runtime:[A-Z][A-Z0-9_]{1,62}$")) return;
            throw new ConsultantException("invalid_operational_demo", "Una referencia no es una clave interna ni una referencia tipada válida.");
        }

        private static void AddRuntimePlan(string reference, IList<Dictionary<string, object>> plan, string model)
        {
            if (reference == null || !reference.StartsWith("runtime:", StringComparison.Ordinal)) return;
            string key = reference.Substring("runtime:".Length);
            if (!plan.Any(x => JsonUtil.GetString(x, "key", false) == key)) plan.Add(JsonUtil.Dictionary("key", key, "type", "record_id", "model", model, "required_at_apply", true, "persisted", false));
        }

        private static string Require(string value, string field, string type) { if (string.IsNullOrWhiteSpace(value)) throw new ConsultantException("demo_required_value_missing", "Falta " + field + " para " + type + "."); return value; }
        private static string RequireText(IDictionary<string, object> source, string field, int maximum, string type) { string value = OptionalText(source, field, maximum); return Require(value, field, type); }
        private static string OptionalText(IDictionary<string, object> source, string field, int maximum) { if (source == null || !source.ContainsKey(field) || source[field] == null || string.IsNullOrWhiteSpace(Convert.ToString(source[field], CultureInfo.InvariantCulture))) return null; return CleanText(Convert.ToString(source[field], CultureInfo.InvariantCulture), maximum, field); }
        private static void AddOptional(IDictionary<string, object> values, string field, object value) { if (value != null && !(value is string && string.IsNullOrWhiteSpace((string)value))) values[field] = value; }
        private static string SafeDemoEmail(IDictionary<string, object> source) { string value = OptionalText(source, "correo", 254); if (value != null && !value.EndsWith("@example.invalid", StringComparison.OrdinalIgnoreCase)) throw new ConsultantException("demo_email_not_safe", "Los correos operativos deben terminar en @example.invalid."); return value; }
        private static decimal Number(IDictionary<string, object> source, string field, decimal minimum, decimal maximum) { object raw; decimal value; if (source == null || !source.TryGetValue(field, out raw) || !decimal.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out value) || value < minimum || value > maximum) throw new ConsultantException("invalid_operational_demo", field + " está ausente o fuera del rango permitido."); return value; }
        private static string OptionalDate(IDictionary<string, object> source, string field) { string value = OptionalText(source, field, 40); if (value == null) return null; DateTime parsed; if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)) throw new ConsultantException("invalid_operational_demo", field + " debe usar yyyy-MM-dd."); return value; }
        private static string RequireDate(IDictionary<string, object> source, string field, string type) { return Require(OptionalDate(source, field), field, type); }
        private static string OptionalDateTime(IDictionary<string, object> source, string field) { string value = OptionalText(source, field, 40); if (value == null) return null; DateTimeOffset parsed; if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed)) throw new ConsultantException("invalid_operational_demo", field + " debe ser una fecha u hora ISO-8601."); return parsed.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }

        private Dictionary<string, string> ReadModuleStates(OdooClient client)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string module in DemoAdapterCatalog.All.SelectMany(x => x.modules).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
            {
                IList<Dictionary<string, object>> rows = client.SearchRead("ir.module.module", new object[] { new object[] { "name", "=", module } }, new[] { "name", "state" }, 1, null);
                result[module] = rows.Count == 1 ? StringValue(rows[0], "state") : "not_found";
            }
            return result;
        }

        private static List<DemoStep> CommercialSteps(IList<DemoComponent> components)
        {
            return new List<DemoStep>
            {
                Step("landing", "Revisar landing no publicada y formulario CRM", "landing", components, "La página está no publicada; el formulario nativo apunta a CRM y no se envía automáticamente."),
                Step("captacion", "Captación y atribución de un lead ficticio", "lead", components, "El lead muestra campaña, medio y fuente UTM."),
                Step("encuesta", "Revisar encuesta y preguntas", "survey", components, "La encuesta permanece inactiva, ordenada y con acceso controlado."),
                Step("mailing", "Comparar borradores A/B", "mailing_a", components, "Ambas variantes están en borrador, usan lista ficticia y no se envían."),
                Step("automatizacion", "Revisar automatización de marketing", "marketing_campaign", components, "La campaña y su actividad están instaladas pero inactivas."),
                new DemoStep { key = "redes", title = "Explicar redes, WhatsApp y publicidad", actor = "consultor", expected = "Se presentan como conectores o runbooks futuros; no se publica ni se conecta una cuenta real.", component_key = null },
                new DemoStep { key = "metricas", title = "Revisar métricas y atribución", actor = "consultor", expected = "Se diferencia atribución UTM nativa de analítica externa.", component_key = components.Any(x => x.key == "campaign") ? "campaign" : null }
            };
        }

        private static DemoStep Step(string key, string title, string componentKey, IList<DemoComponent> components, string expected)
        {
            return new DemoStep { key = key, title = title, actor = "consultor", expected = expected, component_key = components.Any(x => x.key == componentKey) ? componentKey : null };
        }

        private void WriteArtifacts(string directory, DemoScenario scenario, IDictionary<string, object> sourceLock)
        {
            JsonUtil.WriteJsonFile(Path.Combine(directory, "escenario.json"), scenario);
            File.WriteAllText(Path.Combine(directory, "brief.yaml"), "schema_version: " + scenario.schema_version + "\ndemo_label: \"" + (scenario.demo_label ?? string.Empty) + "\"\ndelivery_mode: database_records\nobjetivo: \"" + scenario.objective.Replace("\"", string.Empty) + "\"\naplicacion: " + scenario.application + "\nversion_odoo: \"" + project.Config.odoo_version + "\"\nentorno: staging\nestado_inicial: seguro_sin_efectos_externos\n", new UTF8Encoding(false));
            JsonUtil.WriteJsonFile(Path.Combine(directory, "fuentes.json"), JsonUtil.Dictionary("source_lock_hash", scenario.source_lock_hash, "hierarchy", new object[] { "verificado_base", "oficial_documentacion", "oficial_codigo", "codigo_cliente", "comunidad_no_verificada" }, "conflicts", new object[0]));
            File.WriteAllText(Path.Combine(directory, "matriz-capacidades.md"), MarketingMatrix(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "checklist-preparacion.md"), "# Checklist de preparación\n\n- [ ] Revisar los " + scenario.components.Count + " registros y sus relaciones planificadas.\n- [ ] Confirmar que los nombres visibles sean naturales y no contengan identificadores técnicos.\n- [ ] Resolver referencias tipadas sin guardar IDs en archivos.\n- [ ] Confirmar `editor_ready esperado`, contrato `" + MailEditorContract.ContractVersion + "` y estrategia de imagen interna.\n- [ ] Aplicar con `CREAR-DEMO " + scenario.id + "` dentro de las 12 horas.\n- [ ] Confirmar que landing, encuesta y automatización no estén activas públicamente.\n- [ ] Confirmar que los correos sigan en borrador y no estén probados, programados ni enviados.\n- [ ] Ensayar el formulario únicamente con `example.invalid`.\n- [ ] Verificar los registros reales, sus relaciones, `database_verified` y `editor_ready`; limpiar al terminar.\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "guion-consultor.md"), "# Guion del consultor\n\n1. Explica el objetivo comercial sin mostrar identificadores técnicos.\n2. Muestra la landing sin publicarla y explica el formulario nativo hacia CRM.\n3. Revisa el lead sintético y la atribución UTM.\n4. Presenta la encuesta inactiva, sus preguntas y opciones.\n5. Compara asunto, preheader, cuerpo, CTA y porcentaje de las variantes A/B.\n6. Explica la actividad de Marketing Automation sin activarla.\n7. Separa lo nativo de redes, WhatsApp, publicidad y analítica externa.\n8. Verifica los registros creados y ejecuta la limpieza segura.\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "guion-cliente.md"), "# Guion para el cliente\n\nLa demostración recorre captación web, CRM, encuestas, campañas A/B, automatización y medición usando exclusivamente datos ficticios. Cada elemento se mostrará en un estado seguro: página no publicada, encuesta y automatización inactivas, y correos en borrador.\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "componentes-comerciales.md"), CommercialComponentDocument(scenario), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "resultados.md"), "# Resultados\n\nPendiente de crear y verificar en staging los " + scenario.components.Count + " registros planificados del escenario comercial v" + scenario.schema_version + ". La documentación por sí sola no constituye una demo lista.\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "limpieza.md"), "# Limpieza\n\nUsar `LIMPIAR-DEMO " + scenario.id + "`. El motor solo desactiva, despublica o separa recursos internos que él mismo creó; conserva los reutilizados y nunca ejecuta `unlink`. Para un reemplazo verificado, `ARCHIVAR-REEMPLAZO " + scenario.id + "` afecta únicamente los originales declarados y `RESTAURAR-REEMPLAZO " + scenario.id + "` revierte su estado. Preguntas, respuestas y actividades sin archivo seguro quedan contenidas por su padre inactivo y se identifican para revisión manual.\n", new UTF8Encoding(false));
        }

        private static string MarketingMatrix()
        {
            return "# Matriz de capacidades — Marketing comercial\n\n| Requerimiento | Cobertura | Estado seguro | Verificación |\n|---|---|---|---|\n| Landing y formulario CRM | Nativo con Website + Website CRM | No publicada y no indexada | Revisar página y destino `crm.lead` |\n| Encuestas | Nativo con Surveys | Inactiva y por invitación | Revisar orden, obligatoriedad y opciones |\n| Email Marketing A/B | Nativo con Email Marketing | Borradores, direcciones `example.invalid` | Comparar asunto, preheader, cuerpo, CTA y porcentaje |\n| Marketing Automation | Enterprise | Campaña inactiva y en borrador | Revisar disparador, demora y plantilla enlazada |\n| Redes, WhatsApp y SMS | Enterprise o integración según canal | Solo lectura o runbook futuro | No conectar ni publicar automáticamente |\n| Publicidad y analítica externa | Integración | Manual | Validar conectores y gobierno del cliente |\n\nLas fuentes comunitarias se etiquetan siempre como `comunidad_no_verificada`.\n";
        }

        private static string CommercialComponentDocument(DemoScenario scenario)
        {
            StringBuilder text = new StringBuilder("# Componentes comerciales\n\n");
            foreach (DemoComponent component in scenario.components)
            {
                text.AppendLine("## " + component.key + " — `" + component.adapter + "`");
                text.AppendLine();
                text.AppendLine("### Problema que resuelve");
                text.AppendLine();
                text.AppendLine(component.expected_result);
                text.AppendLine();
                text.AppendLine("### Cómo se configuró");
                text.AppendLine();
                text.AppendLine("Adaptador cerrado `" + component.adapter + "`; estado inicial: " + component.initial_state + ". Campos comerciales: " + string.Join(", ", (component.commercial_fields ?? new List<string>()).ToArray()) + ".");
                text.AppendLine();
                text.AppendLine("### Cómo replicarlo");
                text.AppendLine();
                text.AppendLine("Inventariar la aplicación, fijar fuentes y volver a preparar el mismo escenario declarativo; no copiar IDs ni editar el archivo firmado.");
                text.AppendLine();
                text.AppendLine("### Escenarios donde funciona");
                text.AppendLine();
                text.AppendLine("Staging Odoo 18/19 cuando módulos, campos y permisos coinciden con el hash de capacidades.");
                text.AppendLine();
                text.AppendLine("### Argumento comercial");
                text.AppendLine();
                text.AppendLine("Permite explicar " + component.expected_result.ToLowerInvariant() + " sin habilitar efectos externos.");
                text.AppendLine();
                text.AppendLine("### Limitaciones");
                text.AppendLine();
                text.AppendLine("No activa, publica, distribuye, programa ni envía. La cobertura depende de módulos y edición instalados.");
                text.AppendLine();
                text.AppendLine("### Métricas");
                text.AppendLine();
                text.AppendLine("Existencia, estado y relaciones esperadas por lectura; las métricas comerciales reales se explican sin generarlas artificialmente.");
                text.AppendLine();
                text.AppendLine("### Riesgos");
                text.AppendLine();
                text.AppendLine("Cambio de permisos, módulos, fuentes o metadatos invalida la aplicación. Los registros reutilizados no se limpian automáticamente.");
                text.AppendLine();
                text.AppendLine("### Verificación");
                text.AppendLine();
                text.AppendLine(component.expected_result + " Confirmar además el estado seguro por interfaz.");
                text.AppendLine();
                text.AppendLine("### Limpieza");
                text.AppendLine();
                text.AppendLine("Estrategia `" + component.cleanup_strategy + "`; nunca `unlink`.\n");
            }
            return text.ToString();
        }

        private static string BuildLandingArch(string key, string headline, string subtitle, IEnumerable<string> benefits, string cta, string legal, string layout)
        {
            string items = string.Join(string.Empty, benefits.Select(x => "<li>" + Escape(x) + "</li>").ToArray());
            string sectionClass = layout == "split_form" ? "row align-items-center pt64 pb64" : "pt64 pb64";
            return "<t name=\"" + Escape(headline) + "\" t-name=\"" + Escape(key) + "\"><t t-call=\"website.layout\"><div id=\"wrap\" class=\"oe_structure\"><section class=\"" + sectionClass + "\" data-name=\"Propuesta principal\"><div class=\"container\"><h1>" + Escape(headline) + "</h1><p>" + Escape(subtitle) + "</p><ul>" + items + "</ul></div></section><section class=\"s_website_form pt32 pb32\" data-vcss=\"001\" data-snippet=\"s_website_form\" data-name=\"Formulario de contacto\"><div class=\"container\"><form action=\"/website/form/\" method=\"post\" enctype=\"multipart/form-data\" class=\"o_mark_required\" data-mark=\"*\" data-model_name=\"crm.lead\" data-success-mode=\"message\"><div class=\"s_website_form_rows row\"><input type=\"hidden\" class=\"s_website_form_input\" name=\"name\" value=\"Solicitud desde landing\"/>" + FormField("contact_name", "Nombre ficticio", "text", true, false) + FormField("partner_name", "Empresa ficticia", "text", false, false) + FormField("email_from", "Correo de demostración", "email", true, false) + FormField("phone", "Teléfono ficticio", "tel", false, false) + FormArea("description", "Necesidad", false, false) + FormField("Plazo", "Plazo", "text", false, true) + FormField("Solución de interés", "Solución de interés", "text", false, true) + FormField("Consentimiento", "Consentimiento", "checkbox", true, true) + "<div class=\"col-12 mb-3\"><small>" + Escape(legal) + "</small></div><div class=\"s_website_form_submit col-12\"><span id=\"s_website_form_result\"></span><a href=\"#\" role=\"button\" class=\"btn btn-primary s_website_form_send\">" + Escape(cta) + "</a></div></div></form></div></section></div></t></t>";
        }

        private static string FormField(string name, string label, string type, bool required, bool custom)
        {
            return "<div class=\"s_website_form_field col-12 mb-3 " + (required ? "s_website_form_required " : string.Empty) + (custom ? "s_website_form_custom" : "s_website_form_model_required") + "\"><label>" + Escape(label) + (required ? " *" : string.Empty) + "</label><input type=\"" + type + "\" class=\"form-control s_website_form_input\" name=\"" + Escape(name) + "\"" + (required ? " required=\"1\"" : string.Empty) + "/></div>";
        }

        private static string FormArea(string name, string label, bool required, bool custom)
        {
            return "<div class=\"s_website_form_field col-12 mb-3 " + (required ? "s_website_form_required " : string.Empty) + (custom ? "s_website_form_custom" : string.Empty) + "\"><label>" + Escape(label) + "</label><textarea class=\"form-control s_website_form_input\" name=\"" + Escape(name) + "\" rows=\"3\"></textarea></div>";
        }

        private static string Paragraph(string text) { return "<p>" + Escape(text) + "</p>"; }
        private static string Escape(string value) { return SecurityElement.Escape(value ?? string.Empty) ?? string.Empty; }
        private static Dictionary<string, object> Fields(OdooClient client, string model) { return client.FieldsGet(model, new[] { "type", "required", "readonly", "relation", "selection", "store", "string" }); }
        private static bool SafeAccess(OdooClient client, string model, string operation) { try { return client.CheckAccessRights(model, operation); } catch (ConsultantException) { return false; } }
        private static bool Installed(IDictionary<string, string> states, string module) { return states.ContainsKey(module) && states[module] == "installed"; }

        private static void ValidateConfiguration(IDictionary<string, object> configuration)
        {
            if (configuration == null) return;
            EnsureKeys(configuration, new[] { "utm", "website", "survey", "mailing", "automation" }, "configuracion");
            EnsureKeys(Child(configuration, "utm"), new[] { "source_name", "medium_name", "campaign_name", "lead_name", "contact_name" }, "utm");
            EnsureKeys(Child(configuration, "website"), new[] { "enabled", "headline", "subtitle", "benefits", "cta_label", "legal_text", "layout" }, "website");
            EnsureKeys(Child(configuration, "survey"), new[] { "enabled", "title", "description", "questions" }, "survey");
            EnsureKeys(Child(configuration, "mailing"), new[] { "enabled", "subject_a", "subject_b", "preheader", "headline", "body", "cta_label", "ab_percentage", "image_url", "image_required", "image_asset", "personalization_variable", "variables" }, "mailing");
            EnsureKeys(Child(Child(configuration, "mailing"), "image_asset"), new[] { "file", "sha256", "alt" }, "mailing.image_asset");
            EnsureKeys(Child(configuration, "automation"), new[] { "enabled", "title", "delay_days", "trigger", "target_model", "audience_filter", "branch_trigger" }, "automation");
        }

        private static void EnsureKeys(IDictionary<string, object> value, IEnumerable<string> allowed, string scope)
        {
            if (value == null) return;
            HashSet<string> safe = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (string key in value.Keys) if (!safe.Contains(key)) throw new ConsultantException("invalid_demo_configuration", "El parámetro " + scope + "." + key + " no está permitido.");
        }

        private static Dictionary<string, object> Child(IDictionary<string, object> root, string key)
        {
            if (root == null || !root.ContainsKey(key) || root[key] == null) return new Dictionary<string, object>(StringComparer.Ordinal);
            Dictionary<string, object> result = root[key] as Dictionary<string, object>;
            if (result == null) throw new ConsultantException("invalid_demo_configuration", key + " debe ser un objeto cerrado.");
            return result;
        }

        private static bool Enabled(IDictionary<string, object> value) { return value == null || !value.ContainsKey("enabled") || JsonUtil.GetBool(value, "enabled", false); }
        private static string ConfigText(IDictionary<string, object> value, string key, string fallback, int maximum) { return value != null && value.ContainsKey(key) ? CleanText(Convert.ToString(value[key], CultureInfo.InvariantCulture), maximum, key) : fallback; }

        private static int ConfigInt(IDictionary<string, object> value, string key, int fallback, int minimum, int maximum)
        {
            if (value == null || !value.ContainsKey(key)) return fallback;
            long raw = JsonUtil.GetLong(value, key, true);
            if (raw < minimum || raw > maximum) throw new ConsultantException("invalid_demo_configuration", key + " está fuera del rango permitido.");
            return (int)raw;
        }

        private static List<string> ConfigList(IDictionary<string, object> value, string key, IEnumerable<string> fallback, int minimum, int maximum, int textMaximum)
        {
            return value != null && value.ContainsKey(key) ? StringList(value[key], minimum, maximum, textMaximum, key) : fallback.ToList();
        }

        private static List<Dictionary<string, object>> Questions(IDictionary<string, object> survey)
        {
            if (survey == null || !survey.ContainsKey("questions"))
            {
                return new List<Dictionary<string, object>>
                {
                    JsonUtil.Dictionary("type", "simple_choice", "label", "¿Cuál es su prioridad principal?", "required", true, "options", new object[] { "Captación", "Automatización", "Medición" }),
                    JsonUtil.Dictionary("type", "scale", "label", "¿Qué tan importante es centralizar el seguimiento?", "required", true),
                    JsonUtil.Dictionary("type", "text_box", "label", "¿Qué resultado espera del proyecto?", "required", false)
                };
            }
            IEnumerable sequence = survey["questions"] as IEnumerable;
            if (sequence == null || survey["questions"] is string) throw new ConsultantException("invalid_demo_configuration", "questions debe ser una lista.");
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            foreach (object item in sequence)
            {
                Dictionary<string, object> question = item as Dictionary<string, object>;
                if (question == null) throw new ConsultantException("invalid_demo_configuration", "Cada pregunta debe ser un objeto.");
                EnsureKeys(question, new[] { "type", "label", "required", "options", "rows" }, "survey.questions");
                string type = JsonUtil.GetString(question, "type", true);
                if (!QuestionTypes.Contains(type)) throw new ConsultantException("invalid_demo_configuration", "El tipo de pregunta no pertenece al catálogo de Odoo 18.");
                CleanText(JsonUtil.GetString(question, "label", true), 300, "label");
                List<string> options = StringList(question.ContainsKey("options") ? question["options"] : null, 0, 10, 120, "options");
                if ((type == "simple_choice" || type == "multiple_choice") && options.Count < 2) throw new ConsultantException("invalid_demo_configuration", "Las preguntas de selección requieren al menos dos opciones.");
                if (type == "matrix" && (options.Count < 2 || StringList(question.ContainsKey("rows") ? question["rows"] : null, 0, 10, 120, "rows").Count < 2)) throw new ConsultantException("invalid_demo_configuration", "Las matrices requieren al menos dos columnas y dos filas.");
                result.Add(question);
            }
            if (result.Count < 1 || result.Count > 10) throw new ConsultantException("invalid_demo_configuration", "Debes indicar entre 1 y 10 preguntas.");
            return result;
        }

        private static List<string> CleanList(IEnumerable<string> values, int minimum, int maximum, int textMaximum, string label)
        {
            List<string> result = (values ?? new string[0]).Select(x => CleanText(x, textMaximum, label)).ToList();
            if (result.Count < minimum || result.Count > maximum) throw new ConsultantException("invalid_demo_configuration", label + " debe contener entre " + minimum + " y " + maximum + " elementos.");
            return result;
        }

        private static List<string> StringList(object value, int minimum, int maximum, int textMaximum, string label)
        {
            List<string> result = new List<string>();
            if (value != null)
            {
                IEnumerable sequence = value as IEnumerable;
                if (sequence == null || value is string) throw new ConsultantException("invalid_demo_configuration", label + " debe ser una lista.");
                foreach (object item in sequence) result.Add(CleanText(Convert.ToString(item, CultureInfo.InvariantCulture), textMaximum, label));
            }
            if (result.Count < minimum || result.Count > maximum) throw new ConsultantException("invalid_demo_configuration", label + " debe contener entre " + minimum + " y " + maximum + " elementos.");
            return result;
        }

        private static string CleanText(string value, int maximum, string label)
        {
            string clean = (value ?? string.Empty).Trim();
            if (clean.Length == 0 || clean.Length > maximum || clean.Any(x => char.IsControl(x) && x != '\r' && x != '\n' && x != '\t')) throw new ConsultantException("invalid_demo_configuration", label + " no tiene una longitud o caracteres válidos.");
            return clean;
        }

        private DemoScenario Load(string id) { return JsonUtil.ReadJsonFile<DemoScenario>(project.DemoScenarioPath(id)); }
        private void Save(DemoScenario scenario) { scenario.integrity_hash = Integrity(scenario); JsonUtil.WriteJsonFile(project.DemoScenarioPath(scenario.id), scenario); }
        private static string Integrity(DemoScenario scenario) { Dictionary<string, object> value = JsonUtil.DeserializeObject(JsonUtil.Serialize(scenario)); value.Remove("integrity_hash"); return JsonUtil.CanonicalHash(value); }
        private static void EnsureIntegrity(DemoScenario scenario)
        {
            if (scenario == null || (scenario.schema_version != 1 && scenario.schema_version != 2 && scenario.schema_version != 3 && scenario.schema_version != 4 && scenario.schema_version != 5)) throw new ConsultantException("demo_integrity_failed", "La versión del escenario no está soportada.");
            if (scenario.integrity_hash == Integrity(scenario)) return;
            if (scenario.schema_version <= 3 && scenario.integrity_hash == LegacyV3Integrity(scenario)) return;
            if (scenario.schema_version <= 2 && scenario.integrity_hash == LegacyIntegrity(scenario)) return;
            throw new ConsultantException("demo_integrity_failed", "El escenario fue modificado fuera del motor.");
        }

        private static string LegacyIntegrity(DemoScenario scenario)
        {
            Dictionary<string, object> value = JsonUtil.DeserializeObject(JsonUtil.Serialize(scenario));
            value.Remove("integrity_hash");
            foreach (string key in new[] { "replacement_keys", "replacement_state", "replacement_snapshot", "replacement_archived_at", "replacement_restored_at", "demo_number", "demo_label", "delivery_mode", "ownership_registry", "registry_status", "registry_capability_hash", "record_plan", "readiness_criteria" }) value.Remove(key);
            object rawComponents;
            IEnumerable components = value.TryGetValue("components", out rawComponents) ? rawComponents as IEnumerable : null;
            if (components != null) foreach (object raw in components)
            {
                Dictionary<string, object> component = raw as Dictionary<string, object>;
                if (component == null) continue;
                foreach (string key in new[] { "asset_file", "asset_sha256", "asset_mimetype", "asset_size", "image_required", "personalization_variable", "database_verified", "editor_ready", "editor_contract", "editor_validation", "write_fingerprint" }) component.Remove(key);
            }
            return JsonUtil.CanonicalHash(value);
        }
        private static string LegacyV3Integrity(DemoScenario scenario)
        {
            Dictionary<string, object> value = JsonUtil.DeserializeObject(JsonUtil.Serialize(scenario));
            value.Remove("integrity_hash");
            foreach (string key in new[] { "demo_number", "demo_label", "delivery_mode", "ownership_registry", "registry_status", "registry_capability_hash", "record_plan", "readiness_criteria" }) value.Remove(key);
            object rawComponents;
            IEnumerable components = value.TryGetValue("components", out rawComponents) ? rawComponents as IEnumerable : null;
            if (components != null) foreach (object raw in components)
            {
                Dictionary<string, object> component = raw as Dictionary<string, object>;
                if (component != null) component.Remove("write_fingerprint");
            }
            return JsonUtil.CanonicalHash(value);
        }
        internal static void SignForSelfTest(DemoScenario scenario) { scenario.integrity_hash = Integrity(scenario); }
        internal static void SignLegacyForSelfTest(DemoScenario scenario) { scenario.integrity_hash = LegacyIntegrity(scenario); }
        internal static void SignLegacyV3ForSelfTest(DemoScenario scenario) { scenario.integrity_hash = LegacyV3Integrity(scenario); }
        internal static bool IntegrityValidForSelfTest(DemoScenario scenario) { try { EnsureIntegrity(scenario); return true; } catch (ConsultantException) { return false; } }
        internal static void EnsureIntegrityForService(DemoScenario scenario) { EnsureIntegrity(scenario); }
        internal static void SaveScenarioForService(ProjectStore project, DemoScenario scenario)
        {
            scenario.integrity_hash = Integrity(scenario);
            JsonUtil.WriteJsonFile(project.DemoScenarioPath(scenario.id), scenario);
        }
        internal static bool SupportsSchemaForSelfTest(int schemaVersion) { return schemaVersion == 1 || schemaVersion == 2 || schemaVersion == 3 || schemaVersion == 4 || schemaVersion == 5; }
        internal static bool UsesSurveyParentWriteForSelfTest(string serverSeries, string adapter) { return (serverSeries == "17.0" || serverSeries == "18.0") && adapter == "survey_question"; }
        internal static bool EquivalentFieldValueForSelfTest(object actual, object expected, string type) { return EquivalentFieldValue(actual, expected, JsonUtil.Dictionary("type", type)); }
        internal static Dictionary<string, object> MarketingActivityCompatibilityForSelfTest()
        {
            Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.Ordinal);
            Dictionary<string, object> fields = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "source_id", JsonUtil.Dictionary("type", "many2one", "required", true, "relation", "utm.source") },
                { "validity_duration_type", JsonUtil.Dictionary("type", "selection", "required", true, "selection", new object[] { new object[] { "hours", "Hours" }, new object[] { "days", "Days" } }) }
            };
            ApplyRuntimeTechnicalValues(DemoAdapterCatalog.Get("marketing_activity_inactive"), values, fields, new Dictionary<string, long>(StringComparer.Ordinal) { { "source", 77L } });
            return values;
        }
        internal static string EscapeForSelfTest(string value) { return Escape(value); }
        internal static bool ShouldSkipSurveySectionForSelfTest(string serverSeries, string adapter) { return ShouldSkipSurveySection(serverSeries, adapter); }
        internal static DemoComponent CompileOperationalForSelfTest(IDictionary<string, object> specification)
        {
            string key = NormalizeComponentKey(JsonUtil.GetString(specification, "clave", true));
            return new DemoService(null).CompileOperationalComponent(specification, key, new List<Dictionary<string, object>>(), null);
        }

        private static Dictionary<string, long> RestoreIds(IDictionary<string, object> protectedRecords)
        {
            Dictionary<string, long> result = new Dictionary<string, long>(StringComparer.Ordinal);
            if (protectedRecords == null) return result;
            foreach (KeyValuePair<string, object> pair in protectedRecords) { Dictionary<string, object> record = pair.Value as Dictionary<string, object>; if (record != null) result[pair.Key] = JsonUtil.GetLong(record, "id", true); }
            return result;
        }

        private static object PublicScenario(DemoScenario scenario, string next)
        {
            return JsonUtil.Dictionary(
                "schema_version", scenario.schema_version, "scenario_id", scenario.id, "demo_slug", scenario.demo_slug,
                "demo_number", scenario.demo_number, "demo_label", scenario.demo_label,
                "deliverable", scenario.delivery_mode ?? (scenario.schema_version >= 4 ? "database_records" : "legacy_scenario"),
                "ownership_registry", scenario.ownership_registry ?? "legacy_visible_marker", "registry_status", scenario.registry_status,
                "registry_capability_hash", scenario.registry_capability_hash, "visible_name_policy", scenario.schema_version >= 4 ? "clean_business_names" : "legacy_marker",
                "status", scenario.status, "application", scenario.application, "expires_at", scenario.expires_at,
                "database_verified", scenario.components != null && scenario.components.Count > 0 && scenario.components.All(x => x.status == "skipped_optional" || x.database_verified),
                "editor_ready", scenario.components != null && scenario.components.Where(x => x.adapter == "mailing_draft").All(x => x.editor_ready),
                "editor_ready_expected", scenario.components != null && scenario.components.Any(x => x.adapter == "mailing_draft"),
                "mail_editor_contract", MailEditorContract.ContractVersion,
                "image_strategy", scenario.components != null && scenario.components.Any(x => x.adapter == "mailing_visual_asset") ? "private_ir_attachment_internal_web_content" : "no_image_requested",
                "personalization_variable", scenario.components == null ? "none" : scenario.components.Where(x => x.adapter == "mailing_draft").Select(x => x.personalization_variable ?? "none").Distinct().Count() == 1 ? scenario.components.Where(x => x.adapter == "mailing_draft").Select(x => x.personalization_variable ?? "none").FirstOrDefault() : "per_mailing",
                "replacement_records", scenario.replacement_keys == null ? new object[0] : scenario.replacement_keys.ToArray(),
                "replacement_state", scenario.replacement_state,
                "previous_scenario_cleanup_required", scenario.replacement_keys != null && scenario.replacement_keys.Count > 0,
                "component_count", scenario.components == null ? 0 : scenario.components.Count,
                "planned_records", scenario.record_plan == null ? new object[0] : scenario.record_plan.ToArray(),
                "planned_records_by_model", scenario.components == null ? new object[0] : scenario.components.GroupBy(x => DemoAdapterCatalog.Get(x.adapter).model).Select(x => JsonUtil.Dictionary("model", x.Key, "count", x.Count())).ToArray(),
                "readiness_criteria", scenario.readiness_criteria == null ? new object[0] : scenario.readiness_criteria.ToArray(),
                "components", scenario.components == null ? new object[0] : scenario.components.Select(x => JsonUtil.Dictionary("key", x.key, "adapter", x.adapter, "status", x.status, "initial_state", x.initial_state, "expected", x.expected_result, "cleanup", x.cleanup_strategy, "commercial_fields", x.commercial_fields, "values_preview", SafePreviewValues(x.values), "references", x.references, "asset", (x.adapter == "mailing_visual_asset" || x.adapter == "demo_private_attachment") ? JsonUtil.Dictionary("file", x.asset_file, "sha256", x.asset_sha256, "mimetype", x.asset_mimetype, "size", x.asset_size) : null, "database_verified", x.database_verified, "editor_ready", x.editor_ready, "editor_contract", x.editor_contract, "personalization_variable", x.personalization_variable)).ToArray(),
                "steps", scenario.steps == null ? new object[0] : scenario.steps.Select(x => JsonUtil.Dictionary("key", x.key, "title", x.title, "actor", x.actor, "expected", x.expected)).ToArray(),
                "reference_plan", scenario.reference_plan == null ? new object[0] : scenario.reference_plan.ToArray(),
                "warnings", scenario.warnings == null ? new object[0] : scenario.warnings.ToArray(),
                "manual_operations", scenario.manual_operations == null ? new object[0] : scenario.manual_operations.ToArray(),
                "next_step", next, "external_effects_enabled", false);
        }

        private static Dictionary<string, object> SafePreviewValues(IDictionary<string, object> values)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> pair in values ?? new Dictionary<string, object>())
            {
                if (pair.Key == "arch" || pair.Key == "body_arch" || pair.Key == "domain" || pair.Key == "mailing_domain" || pair.Key == "datas") result[pair.Key] = JsonUtil.Dictionary("generated", true, "sha256", JsonUtil.CanonicalHash(pair.Value));
                else result[pair.Key] = pair.Value;
            }
            return result;
        }

        private OdooClient Open(string environment)
        {
            ConnectionProfile profile = project.GetProfile(environment, true);
            string secret = CredentialStore.Read(profile.credential_target);
            try { return new OdooClient(profile, secret, 30000); }
            finally { secret = null; }
        }

        private static string NormalizeApplication(string application)
        {
            string value = (application ?? string.Empty).Trim().ToLowerInvariant();
            if (!new[] { "marketing", "operaciones", "crm", "ventas", "compras", "inventario", "contabilidad", "proyectos", "mantenimiento" }.Contains(value, StringComparer.Ordinal)) throw new ConsultantException("demo_application_not_supported", "La aplicación no pertenece al inventario cerrado de demostraciones.");
            return value;
        }
        private static bool ShouldSkipSurveySection(string serverSeries, string adapter) { return (string.Equals(serverSeries, "17.0", StringComparison.Ordinal) || string.Equals(serverSeries, "18.0", StringComparison.Ordinal)) && string.Equals(adapter, "survey_section", StringComparison.Ordinal); }
        private static Dictionary<string, object> Values(params object[] pairs) { return JsonUtil.Dictionary(pairs); }
        private static string StringValue(IDictionary<string, object> row, string key) { object value; return row.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : string.Empty; }
        private static long LongValue(IDictionary<string, object> row, string key) { object value; long parsed; return row.TryGetValue(key, out value) && long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) ? parsed : 0; }
        private static string Now() { return DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture); }
        private static string SafeError(Exception error) { ConsultantException known = error as ConsultantException; return Redactor.Clean((known == null ? "internal_error" : known.Code) + ": " + error.Message); }
    }
}
