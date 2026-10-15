using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OdooConsultor
{
    internal sealed class ModuleRecord
    {
        public long id;
        public string name;
        public string displayName;
        public string summary;
        public string state;
        public string license;
        public string author;
        public string maintainer;
        public string website;
        public bool application;
        public bool autoInstall;
        public bool toBuy;
        public List<long> dependencyIds = new List<long>();
        public List<string> dependencies = new List<string>();
        public List<string> autoRequiredDependencies = new List<string>();
    }

    public sealed class ModuleService
    {
        public const string PolicyVersion = "official-module-installation/1";
        private static readonly Regex ModuleNamePattern = new Regex("^[a-z][a-z0-9_]{1,79}$", RegexOptions.Compiled);
        private static readonly Regex PlanIdPattern = new Regex("^chg-[a-f0-9]{32}$", RegexOptions.Compiled);
        private static readonly string[] ForbiddenExact = new[] { "base", "studio_customization", "base_import_module", "base_module_update", "base_module_upgrade" };
        private static readonly string[] ForbiddenExternalExact = new[] { "l10n_co_edi", "l10n_co_dian", "account_online_synchronization", "account_bank_statement_import" };
        private static readonly string[] ForbiddenExternalPrefixes = new[] { "whatsapp", "sms", "delivery_ups", "delivery_fedex", "delivery_dhl", "delivery_bpost", "delivery_sendcloud", "delivery_shiprocket", "delivery_starshipit", "delivery_easypost", "delivery_usps", "account_bank_statement_import_" };
        private readonly ProjectStore project;

        public ModuleService(ProjectStore store) { project = store; }

        public object Search(string environment, IEnumerable<string> terms, bool includeInstalled)
        {
            environment = ProjectStore.NormalizeEnvironment(environment);
            List<string> cleanTerms = NormalizeTerms(terms);
            using (OdooClient client = Open(environment, 30000))
            {
                EnsureProfileVersion(project.GetProfile(environment, true));
                Dictionary<string, Dictionary<string, object>> found = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
                foreach (string term in cleanTerms)
                {
                    object[] domain = includeInstalled
                        ? new object[] { "|", "|", new object[] { "name", "ilike", term }, new object[] { "shortdesc", "ilike", term }, new object[] { "summary", "ilike", term } }
                        : new object[] { new object[] { "state", "=", "uninstalled" }, "|", "|", new object[] { "name", "ilike", term }, new object[] { "shortdesc", "ilike", term }, new object[] { "summary", "ilike", term } };
                    IList<Dictionary<string, object>> rows = client.SearchRead("ir.module.module", domain, ModuleFields(client), 50, "application desc, name asc");
                    foreach (Dictionary<string, object> row in rows)
                    {
                        ModuleRecord record = ToRecord(row);
                        string origin = ClassifyOrigin(record);
                        found[record.name] = JsonUtil.Dictionary(
                            "name", record.name, "display_name", record.displayName, "summary", record.summary,
                            "state", record.state, "application", record.application, "license", record.license,
                            "origin", origin, "installable", IsDirectlyInstallable(record, origin),
                            "reason", InstallabilityReason(record, origin));
                    }
                }
                AuditLog.Append(project, environment, "modulos_buscar", null, "success", "Búsqueda de módulos: terms_hash=" + JsonUtil.CanonicalHash(cleanTerms).Substring(0, 12) + "; resultados=" + found.Count.ToString(CultureInfo.InvariantCulture) + ".", 0);
                return JsonUtil.Dictionary("schema_version", 1, "environment", environment, "source_tier", "verificado_base", "results", found.Values.OrderBy(x => Convert.ToString(x["name"], CultureInfo.InvariantCulture), StringComparer.Ordinal).ToArray());
            }
        }

        public object Prepare(string environment, string objective, IEnumerable<string> moduleNames, string demoSlug)
        {
            environment = ProjectStore.NormalizeEnvironment(environment);
            EnsureWriteEnvironment(environment);
            string cleanObjective = CleanObjective(objective);
            List<string> requested = NormalizeModuleNames(moduleNames);
            string normalizedDemo = string.IsNullOrWhiteSpace(demoSlug) ? null : ProjectStore.NormalizeSlug(demoSlug, "demo");
            ConnectionProfile profile = project.GetProfile(environment, true);
            EnsureProfileVersion(profile);
            string sourceLockHash = ReadSourceLockHash(normalizedDemo);
            using (OdooClient client = Open(environment, 30000))
            {
                EnsureAdministrator(client, profile);
                EnsureNoModuleOperation(client);
                List<ModuleRecord> roots = LoadModulesByNames(client, requested);
                EnsureRequestedFound(requested, roots);
                foreach (ModuleRecord root in roots) ValidateRequestedModule(root);
                List<ModuleRecord> closure = ExpandDependencies(client, roots);
                ValidateDependencyClosure(roots, closure);
                List<string> potentialAuto = ComputePotentialAutoInstall(client, closure);
                List<ModulePlanItem> inventory = closure.OrderBy(x => x.name, StringComparer.Ordinal).Select(delegate(ModuleRecord record)
                {
                    string origin = roots.Any(x => x.name == record.name) ? ClassifyOrigin(record) : DependencyOrigin(record);
                    return new ModulePlanItem
                    {
                        name = record.name, display_name = record.displayName, state = record.state, origin = origin,
                        license = record.license, application = record.application, requested = requested.Contains(record.name),
                        dependencies = record.dependencies.OrderBy(x => x, StringComparer.Ordinal).ToList(), structure_hash = StructureHash(record, origin)
                    };
                }).ToList();
                List<string> toInstall = inventory.Where(x => x.state == "uninstalled").Select(x => x.name).OrderBy(x => x, StringComparer.Ordinal).ToList();
                List<string> installed = inventory.Where(x => x.state == "installed").Select(x => x.name).OrderBy(x => x, StringComparer.Ordinal).ToList();
                DateTimeOffset now = DateTimeOffset.UtcNow;
                ModuleInstallationPlan plan = new ModuleInstallationPlan
                {
                    schema_version = 1, id = "chg-" + Guid.NewGuid().ToString("N"), client_slug = project.Config.client_slug,
                    environment = environment, objective = cleanObjective, demo_slug = normalizedDemo, status = "prepared",
                    created_at = now.ToString("o", CultureInfo.InvariantCulture),
                    expires_at = now.AddMinutes(project.Config.package_confirmation_minutes).ToString("o", CultureInfo.InvariantCulture),
                    profile_fingerprint = profile.credential_fingerprint, server_series = profile.server_series,
                    policy_version = PolicyVersion, source_lock_hash = sourceLockHash,
                    requested_modules = requested, already_installed = installed, modules_to_install = toInstall,
                    potential_auto_install = potentialAuto, installed_by_plan = new List<string>(), inventory = inventory,
                    warnings = new List<string>
                    {
                        "La instalación puede crear modelos, campos, menús, reglas, datos técnicos y tareas programadas definidas por Odoo.",
                        "No existe reversión automática: desinstalar un módulo puede eliminar tablas, columnas y datos. Para volver atrás, reconstruye el staging o restaura su respaldo.",
                        "Las cuentas, conectores y efectos externos no se configuran ni se activan mediante este plan.",
                        "Odoo volverá a validar dependencias, exclusiones y bloqueos concurrentes al ejecutar button_immediate_install."
                    }
                };
                plan.precondition_hash = JsonUtil.CanonicalHash(inventory.Select(x => JsonUtil.Dictionary("name", x.name, "structure_hash", x.structure_hash)).ToArray());
                plan.capability_hash = JsonUtil.CanonicalHash(JsonUtil.Dictionary("policy", PolicyVersion, "server_series", profile.server_series, "administrator", true, "requested_count", requested.Count, "install_count", toInstall.Count));
                Save(plan);
                AuditLog.Append(project, environment, "modulos_preparar_instalacion", plan.id, "success", "Plan de módulos preparado; requested=" + requested.Count.ToString(CultureInfo.InvariantCulture) + "; install=" + toInstall.Count.ToString(CultureInfo.InvariantCulture) + "; modules_hash=" + JsonUtil.CanonicalHash(requested).Substring(0, 12) + ".", 0);
                return Public(plan, "Revisa el impacto y confirma exactamente con APLICAR " + plan.id + ".");
            }
        }

        public object Apply(string planId, string confirmation)
        {
            ModuleInstallationPlan plan = Load(planId);
            ValidatePlan(plan, true);
            if (!string.Equals(confirmation, "APLICAR " + plan.id, StringComparison.Ordinal)) throw new ConsultantException("confirmation_mismatch", "La confirmación debe ser exactamente APLICAR " + plan.id + ".");
            DateTimeOffset expires;
            if (!DateTimeOffset.TryParse(plan.expires_at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expires) || expires < DateTimeOffset.UtcNow) throw new ConsultantException("confirmation_expired", "La confirmación del plan de módulos venció. Prepara un plan nuevo.");
            if (plan.status == "verified") return Public(plan, "Todos los módulos del plan ya están instalados y verificados.");
            if (plan.status == "applying" || plan.status == "verification_required" || plan.status == "installation_pending") return Verify(plan.id);
            if (plan.status != "prepared") throw new ConsultantException("module_plan_state_invalid", "El plan no está disponible para aplicar.");
            ConnectionProfile profile = project.GetProfile("staging", true);
            EnsureProfileVersion(profile);
            if (!string.Equals(profile.credential_fingerprint, plan.profile_fingerprint, StringComparison.Ordinal)) throw new ConsultantException("profile_changed", "La credencial cambió desde que se preparó el plan.");
            ValidateSourceLock(plan);
            List<long> rootIds = new List<long>();
            using (OdooClient client = Open("staging", 30000))
            {
                EnsureAdministrator(client, profile);
                EnsureNoModuleOperation(client);
                List<ModuleRecord> roots = LoadModulesByNames(client, plan.requested_modules);
                EnsureRequestedFound(plan.requested_modules, roots);
                foreach (ModuleRecord root in roots) ValidateRequestedModule(root);
                List<ModuleRecord> closure = ExpandDependencies(client, roots);
                ValidateCurrentStructure(plan, roots, closure);
                if (roots.Any(x => x.state == "uninstalled"))
                {
                    List<string> currentAuto = ComputePotentialAutoInstall(client, closure);
                    if (!currentAuto.SequenceEqual((plan.potential_auto_install ?? new List<string>()).OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal)) throw new ConsultantException("module_precondition_changed", "Las auto-instalaciones potenciales cambiaron desde la vista previa. Prepara un plan nuevo.");
                }
                foreach (ModuleRecord root in roots) if (root.state == "uninstalled") rootIds.Add(root.id);
            }
            if (rootIds.Count == 0)
            {
                plan.status = "verification_required";
                Save(plan);
                return Verify(plan.id);
            }
            plan.status = "applying";
            plan.last_error = null;
            Save(plan);
            try
            {
                using (OdooClient installer = Open("staging", 300000)) installer.InstallModules(rootIds);
            }
            catch (Exception error)
            {
                plan.status = "verification_required";
                ConsultantException known = error as ConsultantException;
                plan.last_error = known == null ? "module_install_interrupted" : known.Code;
                Save(plan);
                AuditLog.Append(project, "staging", "modulos_aplicar_instalacion", plan.id, "verification_required", "La instalación requiere verificación posterior; error_class=" + plan.last_error + ".", 0);
                throw new ConsultantException("module_install_verification_required", "Odoo no confirmó de forma concluyente el final de la instalación. No se repetirá la llamada: usa modulos_verificar_instalacion con el mismo plan.");
            }
            plan.applied_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            plan.status = "verification_required";
            Save(plan);
            return Verify(plan.id);
        }

        public object Verify(string planId)
        {
            ModuleInstallationPlan plan = Load(planId);
            ValidatePlan(plan, false);
            ConnectionProfile profile = project.GetProfile("staging", true);
            EnsureProfileVersion(profile);
            using (OdooClient client = Open("staging", 30000))
            {
                List<string> names = plan.modules_to_install.Concat(plan.potential_auto_install ?? new List<string>()).Distinct(StringComparer.Ordinal).ToList();
                List<ModuleRecord> current = names.Count == 0 ? new List<ModuleRecord>() : LoadModulesByNames(client, names);
                Dictionary<string, string> states = current.ToDictionary(x => x.name, x => x.state, StringComparer.Ordinal);
                List<string> missing = plan.modules_to_install.Where(x => !states.ContainsKey(x) || states[x] != "installed").ToList();
                List<string> pending = missing.Where(x => states.ContainsKey(x) && (states[x] == "to install" || states[x] == "to upgrade" || states[x] == "to remove")).ToList();
                plan.installed_by_plan = plan.modules_to_install.Concat(plan.potential_auto_install ?? new List<string>()).Where(x => states.ContainsKey(x) && states[x] == "installed").Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (missing.Count == 0)
                {
                    plan.status = "verified";
                    plan.verified_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    plan.last_error = null;
                }
                else if (pending.Count > 0) plan.status = "installation_pending";
                else if (plan.status == "prepared") plan.status = "prepared";
                else plan.status = plan.installed_by_plan.Count > 0 ? "failed_partial" : "failed";
                Save(plan);
                AuditLog.Append(project, "staging", "modulos_verificar_instalacion", plan.id, plan.status, "Verificación de módulos; planned=" + plan.modules_to_install.Count.ToString(CultureInfo.InvariantCulture) + "; installed=" + plan.installed_by_plan.Count.ToString(CultureInfo.InvariantCulture) + "; pending=" + missing.Count.ToString(CultureInfo.InvariantCulture) + ".", 0);
                Dictionary<string, object> result = Public(plan, missing.Count == 0 ? "Instalación verificada por lectura." : "La instalación todavía no está completa; no se repetirá automáticamente.");
                result["module_states"] = states;
                result["missing_or_pending"] = missing.ToArray();
                return result;
            }
        }

        private List<ModuleRecord> ExpandDependencies(OdooClient client, IEnumerable<ModuleRecord> roots)
        {
            Dictionary<string, ModuleRecord> all = roots.ToDictionary(x => x.name, x => x, StringComparer.Ordinal);
            List<ModuleRecord> frontier = all.Values.ToList();
            for (int depth = 0; frontier.Count > 0 && depth < 30; depth++)
            {
                LoadDependencies(client, frontier);
                List<string> missingNames = frontier.SelectMany(x => x.dependencies).Where(x => !all.ContainsKey(x)).Distinct(StringComparer.Ordinal).ToList();
                if (missingNames.Count == 0) { frontier.Clear(); break; }
                List<ModuleRecord> loaded = LoadModulesByNames(client, missingNames);
                EnsureRequestedFound(missingNames, loaded);
                foreach (ModuleRecord item in loaded) all[item.name] = item;
                frontier = loaded;
                if (all.Count > 300) throw new ConsultantException("module_dependency_limit", "El árbol de dependencias excede el límite seguro de 300 módulos.");
            }
            if (frontier.Count > 0) throw new ConsultantException("module_dependency_depth", "El árbol de dependencias excede la profundidad permitida.");
            return all.Values.ToList();
        }

        private void LoadDependencies(OdooClient client, IEnumerable<ModuleRecord> modules)
        {
            List<ModuleRecord> list = modules.ToList();
            List<long> ids = list.SelectMany(x => x.dependencyIds).Distinct().ToList();
            if (ids.Count == 0) return;
            IList<Dictionary<string, object>> rows = client.Read("ir.module.module.dependency", ids, new[] { "name", "module_id", "auto_install_required" });
            Dictionary<long, List<string>> byModule = new Dictionary<long, List<string>>();
            Dictionary<long, List<string>> requiredByModule = new Dictionary<long, List<string>>();
            foreach (Dictionary<string, object> row in rows)
            {
                long moduleId = Many2OneId(Value(row, "module_id"));
                string name = Convert.ToString(Value(row, "name"), CultureInfo.InvariantCulture);
                if (moduleId <= 0 || string.IsNullOrWhiteSpace(name)) continue;
                if (!byModule.ContainsKey(moduleId)) byModule[moduleId] = new List<string>();
                byModule[moduleId].Add(name);
                if (Boolean(Value(row, "auto_install_required")))
                {
                    if (!requiredByModule.ContainsKey(moduleId)) requiredByModule[moduleId] = new List<string>();
                    requiredByModule[moduleId].Add(name);
                }
            }
            foreach (ModuleRecord module in list)
            {
                module.dependencies = byModule.ContainsKey(module.id) ? byModule[module.id].Distinct(StringComparer.Ordinal).ToList() : new List<string>();
                module.autoRequiredDependencies = requiredByModule.ContainsKey(module.id) ? requiredByModule[module.id].Distinct(StringComparer.Ordinal).ToList() : new List<string>();
            }
        }

        private List<string> ComputePotentialAutoInstall(OdooClient client, IList<ModuleRecord> closure)
        {
            IList<Dictionary<string, object>> rows = ReadAllPages(client, "ir.module.module", new object[] { new object[] { "auto_install", "=", true }, new object[] { "state", "=", "uninstalled" } }, ModuleFields(client), "name asc", 500, 10000);
            List<ModuleRecord> autoModules = rows.Select(ToRecord).ToList();
            LoadDependencies(client, autoModules);
            IList<Dictionary<string, object>> installedRows = ReadAllPages(client, "ir.module.module", new object[] { new object[] { "state", "=", "installed" } }, new[] { "name" }, "name asc", 500, 10000);
            HashSet<string> available = new HashSet<string>(installedRows.Select(x => Convert.ToString(Value(x, "name"), CultureInfo.InvariantCulture)), StringComparer.Ordinal);
            HashSet<string> newlyAvailable = new HashSet<string>(closure.Where(x => x.state == "uninstalled").Select(x => x.name), StringComparer.Ordinal);
            foreach (string name in newlyAvailable) available.Add(name);
            List<string> potential = new List<string>();
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (ModuleRecord module in autoModules.Where(x => !available.Contains(x.name)))
                {
                    List<string> required = module.autoRequiredDependencies.Count > 0 ? module.autoRequiredDependencies : module.dependencies;
                    if (required.Count == 0 || !required.All(available.Contains) || !required.Any(newlyAvailable.Contains)) continue;
                    string origin = ClassifyOrigin(module);
                    if (!IsOfficialOrigin(origin)) throw new ConsultantException("custom_auto_install_blocked", "La instalación activaría potencialmente un módulo automático no verificado como oficial: " + module.name + ". Revise ese módulo antes de continuar.");
                    available.Add(module.name);
                    newlyAvailable.Add(module.name);
                    potential.Add(module.name);
                    foreach (string dependency in module.dependencies.Where(x => !available.Contains(x)))
                    {
                        available.Add(dependency);
                        newlyAvailable.Add(dependency);
                        potential.Add(dependency);
                    }
                    changed = true;
                }
            }
            return potential.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        }

        private static IList<Dictionary<string, object>> ReadAllPages(OdooClient client, string model, object[] domain, IEnumerable<string> fields, string order, int pageSize, int maximum)
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            int offset = 0;
            while (true)
            {
                IList<Dictionary<string, object>> page = client.SearchReadPage(model, domain, fields, pageSize, offset, order);
                result.AddRange(page);
                if (result.Count > maximum) throw new ConsultantException("module_catalog_too_large", "El catálogo de módulos supera el límite técnico seguro.");
                if (page.Count < pageSize) break;
                offset += page.Count;
            }
            return result;
        }

        private List<ModuleRecord> LoadModulesByNames(OdooClient client, IEnumerable<string> names)
        {
            List<string> distinct = names.Distinct(StringComparer.Ordinal).ToList();
            if (distinct.Count == 0) return new List<ModuleRecord>();
            IList<Dictionary<string, object>> rows = client.SearchRead("ir.module.module", new object[] { new object[] { "name", "in", distinct.ToArray() } }, ModuleFields(client), Math.Max(1, distinct.Count + 5), "name asc");
            return rows.Select(ToRecord).ToList();
        }

        private string[] ModuleFields(OdooClient client)
        {
            Dictionary<string, object> available = client.FieldsGet("ir.module.module", new[] { "type", "readonly" });
            string[] wanted = new[] { "id", "name", "shortdesc", "summary", "state", "license", "author", "maintainer", "website", "application", "auto_install", "to_buy", "dependencies_id" };
            return new[] { "id" }.Concat(wanted.Where(x => x != "id" && available.ContainsKey(x))).ToArray();
        }

        private ModuleRecord ToRecord(IDictionary<string, object> row)
        {
            ModuleRecord record = new ModuleRecord
            {
                id = Convert.ToInt64(Value(row, "id") ?? 0, CultureInfo.InvariantCulture),
                name = Convert.ToString(Value(row, "name"), CultureInfo.InvariantCulture),
                displayName = Convert.ToString(Value(row, "shortdesc"), CultureInfo.InvariantCulture),
                summary = Convert.ToString(Value(row, "summary"), CultureInfo.InvariantCulture),
                state = Convert.ToString(Value(row, "state"), CultureInfo.InvariantCulture),
                license = Convert.ToString(Value(row, "license"), CultureInfo.InvariantCulture),
                author = Convert.ToString(Value(row, "author"), CultureInfo.InvariantCulture),
                maintainer = Convert.ToString(Value(row, "maintainer"), CultureInfo.InvariantCulture),
                website = Convert.ToString(Value(row, "website"), CultureInfo.InvariantCulture),
                application = Boolean(Value(row, "application")), autoInstall = Boolean(Value(row, "auto_install")), toBuy = Boolean(Value(row, "to_buy"))
            };
            record.dependencyIds = LongList(Value(row, "dependencies_id"));
            if (string.IsNullOrWhiteSpace(record.displayName)) record.displayName = record.name;
            return record;
        }

        private void ValidateRequestedModule(ModuleRecord module)
        {
            string origin = ClassifyOrigin(module);
            if (!IsOfficialOrigin(origin)) throw new ConsultantException("module_origin_blocked", "El módulo " + module.name + " no está verificado como parte oficial de Odoo.");
            if (module.toBuy) throw new ConsultantException("module_purchase_required", "El módulo " + module.name + " requiere una compra o habilitación previa en Odoo.");
            if (module.state == "uninstallable") throw new ConsultantException("module_uninstallable", "El módulo " + module.name + " no es instalable en esta instancia.");
            if (module.state != "uninstalled" && module.state != "installed") throw new ConsultantException("module_operation_in_progress", "El módulo " + module.name + " está en estado " + module.state + ". Termine la operación existente antes de preparar otro plan.");
        }

        private void ValidateDependencyClosure(IEnumerable<ModuleRecord> roots, IEnumerable<ModuleRecord> closure)
        {
            HashSet<string> rootNames = new HashSet<string>(roots.Select(x => x.name), StringComparer.Ordinal);
            foreach (ModuleRecord module in closure)
            {
                if (IsExternalForbiddenName(module.name)) throw new ConsultantException("external_integration_module_blocked", "La instalación activaría un módulo de integración externa deshabilitado: " + module.name + ".");
                if (module.state == "uninstallable") throw new ConsultantException("module_dependency_unavailable", "La dependencia " + module.name + " no está disponible.");
                if (module.state != "installed" && module.state != "uninstalled") throw new ConsultantException("module_operation_in_progress", "La dependencia " + module.name + " está en una operación concurrente.");
                if (module.toBuy) throw new ConsultantException("module_purchase_required", "La dependencia " + module.name + " requiere una compra o habilitación previa.");
                if (rootNames.Contains(module.name) && !IsOfficialOrigin(ClassifyOrigin(module))) throw new ConsultantException("module_origin_blocked", "El módulo solicitado no es oficial.");
            }
        }

        private void ValidateCurrentStructure(ModuleInstallationPlan plan, IEnumerable<ModuleRecord> roots, IEnumerable<ModuleRecord> closure)
        {
            HashSet<string> rootNames = new HashSet<string>(roots.Select(x => x.name), StringComparer.Ordinal);
            List<ModulePlanItem> current = closure.OrderBy(x => x.name, StringComparer.Ordinal).Select(delegate(ModuleRecord record)
            {
                string origin = rootNames.Contains(record.name) ? ClassifyOrigin(record) : DependencyOrigin(record);
                return new ModulePlanItem { name = record.name, structure_hash = StructureHash(record, origin) };
            }).ToList();
            string hash = JsonUtil.CanonicalHash(current.Select(x => JsonUtil.Dictionary("name", x.name, "structure_hash", x.structure_hash)).ToArray());
            if (!string.Equals(hash, plan.precondition_hash, StringComparison.Ordinal)) throw new ConsultantException("module_precondition_changed", "Los módulos o sus dependencias cambiaron desde la vista previa. Prepara un plan nuevo.");
        }

        private string ClassifyOrigin(ModuleRecord module)
        {
            if (IsForbiddenName(module.name)) return "blocked_system";
            string version = project.Config.odoo_version;
            string root = project.Config.source_library_root;
            foreach (string relative in new[] { Path.Combine("community", version, "addons", module.name, "__manifest__.py"), Path.Combine("community", version, "odoo", "addons", module.name, "__manifest__.py") })
                if (File.Exists(Path.Combine(root, relative))) return "official_community_source";
            string metadata = ((module.author ?? string.Empty) + " " + (module.maintainer ?? string.Empty) + " " + (module.website ?? string.Empty)).ToLowerInvariant();
            if (metadata.Contains("odoo") && (metadata.Contains("odoo.com") || metadata.Contains("odoo s.a") || metadata.Contains("odoo sa"))) return "official_odoo_metadata";
            return "custom_or_unverified";
        }

        private string DependencyOrigin(ModuleRecord module)
        {
            string origin = ClassifyOrigin(module);
            return IsOfficialOrigin(origin) ? origin : "server_dependency_of_official";
        }

        private static bool IsOfficialOrigin(string origin) { return origin == "official_community_source" || origin == "official_odoo_metadata"; }
        private static bool IsForbiddenName(string name)
        {
            string value = name ?? string.Empty;
            return ForbiddenExact.Contains(value, StringComparer.Ordinal) || IsExternalForbiddenName(value) || value.StartsWith("test_", StringComparison.Ordinal);
        }
        private static bool IsExternalForbiddenName(string name) { string value = name ?? string.Empty; return ForbiddenExternalExact.Contains(value, StringComparer.Ordinal) || ForbiddenExternalPrefixes.Any(prefix => value.StartsWith(prefix, StringComparison.Ordinal)); }
        internal static bool ModuleNameAllowedForSelfTest(string name) { return !IsForbiddenName(name); }
        private static bool IsDirectlyInstallable(ModuleRecord record, string origin) { return IsOfficialOrigin(origin) && !record.toBuy && (record.state == "uninstalled" || record.state == "installed"); }
        private static string InstallabilityReason(ModuleRecord record, string origin)
        {
            if (!IsOfficialOrigin(origin)) return origin;
            if (record.toBuy) return "purchase_required";
            if (record.state == "uninstallable") return "uninstallable";
            if (record.state != "uninstalled" && record.state != "installed") return "operation_in_progress";
            return record.state == "installed" ? "already_installed" : "ready_for_plan";
        }

        private static string StructureHash(ModuleRecord record, string origin)
        {
            return JsonUtil.CanonicalHash(JsonUtil.Dictionary("name", record.name, "display_name", record.displayName, "license", record.license, "application", record.application, "origin", origin, "dependencies", record.dependencies.OrderBy(x => x, StringComparer.Ordinal).ToArray()));
        }

        private void EnsureNoModuleOperation(OdooClient client)
        {
            IList<Dictionary<string, object>> active = client.SearchRead("ir.module.module", new object[] { new object[] { "state", "in", new object[] { "to install", "to upgrade", "to remove" } } }, new[] { "name", "state" }, 20, "name asc");
            if (active.Count > 0) throw new ConsultantException("module_operation_in_progress", "Odoo ya está procesando otra instalación o actualización de módulos.");
        }

        private static void EnsureAdministrator(OdooClient client, ConnectionProfile profile)
        {
            if (!client.IsSystemAdministrator(profile.uid)) throw new ConsultantException("module_admin_required", "La instalación de módulos exige que el usuario de la API pertenezca a Ajustes / Administración.");
            if (!client.CheckAccessRights("ir.module.module", "write")) throw new ConsultantException("module_admin_required", "El usuario no tiene permiso efectivo para administrar módulos.");
        }

        private void EnsureProfileVersion(ConnectionProfile profile)
        {
            if (!string.Equals(profile.server_series, project.Config.odoo_version, StringComparison.Ordinal)) throw new ConsultantException("odoo_version_mismatch", "El perfil no coincide con la versión declarada del proyecto.");
        }

        private static void EnsureWriteEnvironment(string environment)
        {
            if (environment != "staging") throw new ConsultantException("production_write_blocked", "La instalación automática de módulos solo está permitida en staging. Producción permanece en lectura y runbook.");
        }

        private string ReadSourceLockHash(string demoSlug)
        {
            if (string.IsNullOrWhiteSpace(demoSlug)) return null;
            string path = Path.Combine(project.DemoDirectory(demoSlug, false), "source-lock.json");
            if (!File.Exists(path)) throw new ConsultantException("source_lock_required", "La demostración debe fijar sus fuentes antes de preparar módulos.");
            return JsonUtil.Sha256(File.ReadAllText(path, Encoding.UTF8));
        }

        private void ValidateSourceLock(ModuleInstallationPlan plan)
        {
            if (string.IsNullOrWhiteSpace(plan.demo_slug)) return;
            string current = ReadSourceLockHash(plan.demo_slug);
            if (!string.Equals(current, plan.source_lock_hash, StringComparison.Ordinal)) throw new ConsultantException("source_lock_changed", "El bloqueo de fuentes de la demostración cambió desde la vista previa.");
        }

        private ModuleInstallationPlan Load(string planId)
        {
            if (!PlanIdPattern.IsMatch(planId ?? string.Empty)) throw new ConsultantException("invalid_package_id", "El identificador del plan no es válido.");
            string path = project.ModulePlanPath(planId);
            if (!File.Exists(path)) throw new ConsultantException("module_plan_not_found", "No existe el plan de instalación solicitado.");
            ModuleInstallationPlan plan = JsonUtil.ReadJsonFile<ModuleInstallationPlan>(path);
            ValidatePlan(plan, false);
            return plan;
        }

        private void ValidatePlan(ModuleInstallationPlan plan, bool requireStaging)
        {
            if (plan == null || plan.schema_version != 1 || !PlanIdPattern.IsMatch(plan.id ?? string.Empty) || plan.policy_version != PolicyVersion) throw new ConsultantException("unsupported_module_plan", "El plan de instalación no está soportado.");
            if (!string.Equals(plan.client_slug, project.Config.client_slug, StringComparison.Ordinal)) throw new ConsultantException("package_tenant_mismatch", "El plan pertenece a otro cliente.");
            if (requireStaging) EnsureWriteEnvironment(plan.environment);
            string expected = Integrity(plan);
            if (string.IsNullOrWhiteSpace(plan.integrity_hash) || !string.Equals(plan.integrity_hash, expected, StringComparison.Ordinal)) throw new ConsultantException("package_integrity_failed", "El plan de módulos fue modificado después de prepararse.");
        }

        private void Save(ModuleInstallationPlan plan)
        {
            plan.integrity_hash = Integrity(plan);
            JsonUtil.WriteJsonFile(project.ModulePlanPath(plan.id), plan);
        }

        private static string Integrity(ModuleInstallationPlan plan)
        {
            Dictionary<string, object> value = JsonUtil.DeserializeObject(JsonUtil.Serialize(plan));
            value.Remove("integrity_hash");
            return JsonUtil.CanonicalHash(value);
        }

        private static Dictionary<string, object> Public(ModuleInstallationPlan plan, string message)
        {
            return JsonUtil.Dictionary(
                "schema_version", plan.schema_version, "plan_id", plan.id, "environment", plan.environment,
                "status", plan.status, "objective", plan.objective, "demo_slug", plan.demo_slug,
                "created_at", plan.created_at, "expires_at", plan.expires_at, "verified_at", plan.verified_at,
                "requested_modules", plan.requested_modules, "already_installed", plan.already_installed,
                "modules_to_install", plan.modules_to_install, "potential_auto_install", plan.potential_auto_install,
                "installed_by_plan", plan.installed_by_plan, "inventory", plan.inventory,
                "rollback", JsonUtil.Dictionary("automatic", false, "strategy", "rebuild_or_restore_staging"),
                "external_effects", "not_configured_or_activated", "warnings", plan.warnings,
                "required_confirmation", plan.status == "prepared" ? "APLICAR " + plan.id : null,
                "message", message, "last_error", plan.last_error);
        }

        private OdooClient Open(string environment, int timeout)
        {
            return project.OpenClient(environment, timeout);
        }

        private static string CleanObjective(string value)
        {
            string clean = (value ?? string.Empty).Trim();
            if (clean.Length < 1 || clean.Length > 1000) throw new ConsultantException("invalid_objective", "El objetivo debe tener entre 1 y 1000 caracteres.");
            if (clean.IndexOf('\0') >= 0) throw new ConsultantException("invalid_objective", "El objetivo contiene caracteres no válidos.");
            return clean;
        }

        private static List<string> NormalizeTerms(IEnumerable<string> values)
        {
            List<string> result = new List<string>();
            foreach (string raw in values ?? new string[0])
            {
                string value = (raw ?? string.Empty).Trim();
                if (value.Length < 2 || value.Length > 80 || value.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0) throw new ConsultantException("invalid_module_search", "Cada término debe tener entre 2 y 80 caracteres en una sola línea.");
                if (!result.Contains(value, StringComparer.OrdinalIgnoreCase)) result.Add(value);
            }
            if (result.Count < 1 || result.Count > 10) throw new ConsultantException("invalid_module_search", "Debes indicar entre 1 y 10 términos de búsqueda.");
            return result;
        }

        private static List<string> NormalizeModuleNames(IEnumerable<string> values)
        {
            List<string> result = new List<string>();
            foreach (string raw in values ?? new string[0])
            {
                string value = (raw ?? string.Empty).Trim().ToLowerInvariant();
                if (!ModuleNamePattern.IsMatch(value) || IsForbiddenName(value)) throw new ConsultantException("invalid_module_name", "Nombre técnico de módulo no permitido: " + value + ".");
                if (!result.Contains(value, StringComparer.Ordinal)) result.Add(value);
            }
            if (result.Count < 1 || result.Count > 30) throw new ConsultantException("invalid_module_list", "Debes indicar entre 1 y 30 módulos técnicos.");
            return result.OrderBy(x => x, StringComparer.Ordinal).ToList();
        }

        private static void EnsureRequestedFound(IEnumerable<string> requested, IEnumerable<ModuleRecord> found)
        {
            HashSet<string> names = new HashSet<string>(found.Select(x => x.name), StringComparer.Ordinal);
            List<string> missing = requested.Where(x => !names.Contains(x)).ToList();
            if (missing.Count > 0) throw new ConsultantException("module_not_found", "No se encontraron estos módulos en la base: " + string.Join(", ", missing.ToArray()) + ".");
        }

        private static object Value(IDictionary<string, object> value, string key) { object result; return value != null && value.TryGetValue(key, out result) ? result : null; }
        private static bool Boolean(object value) { bool result; return value is bool ? (bool)value : bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out result) && result; }
        private static long Many2OneId(object value)
        {
            IEnumerable sequence = value as IEnumerable;
            if (sequence != null && !(value is string)) foreach (object item in sequence) return Convert.ToInt64(item, CultureInfo.InvariantCulture);
            long id; return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out id) ? id : 0;
        }
        private static List<long> LongList(object value)
        {
            List<long> result = new List<long>(); IEnumerable sequence = value as IEnumerable;
            if (sequence != null && !(value is string)) foreach (object item in sequence) { long parsed; if (long.TryParse(Convert.ToString(item, CultureInfo.InvariantCulture), out parsed) && parsed > 0) result.Add(parsed); }
            return result;
        }

        internal static bool ValidModuleNameForSelfTest(string name) { return ModuleNamePattern.IsMatch(name ?? string.Empty) && !IsForbiddenName(name); }
        internal static string IntegrityForSelfTest(ModuleInstallationPlan plan) { return Integrity(plan); }
        internal static void SignForSelfTest(ModuleInstallationPlan plan) { plan.integrity_hash = Integrity(plan); }
        internal static void ValidateForSelfTest(ProjectStore store, ModuleInstallationPlan plan) { new ModuleService(store).ValidatePlan(plan, true); }
    }
}
