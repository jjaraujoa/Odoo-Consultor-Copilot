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
    public sealed class StudioService
    {
        private static readonly Regex ModelPattern = new Regex("^[a-z0-9_.]+$", RegexOptions.Compiled);
        private static readonly Regex CustomFieldPattern = new Regex("^x_[a-z0-9_]{2,60}$", RegexOptions.Compiled);
        private static readonly HashSet<string> AllowedFieldTypes = new HashSet<string>(new[] { "char", "text", "integer", "float", "boolean", "date", "datetime", "many2one" }, StringComparer.Ordinal);
        private readonly ProjectStore project;

        public StudioService(ProjectStore projectStore)
        {
            project = projectStore;
        }

        public object ConnectionStatus(string environment)
        {
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                if (string.IsNullOrWhiteSpace(environment))
                {
                    List<object> results = new List<object>();
                    foreach (string current in new[] { "staging", "production" }) results.Add(ProfileStatus(current));
                    AuditLog.Append(project, "all", "conexion_estado", null, "success", "Estado local de perfiles consultado.", timer.ElapsedMilliseconds);
                    return JsonUtil.Dictionary("client_slug", project.Config.client_slug, "profiles", results.ToArray());
                }
                object status = ProfileStatus(ProjectStore.NormalizeEnvironment(environment));
                AuditLog.Append(project, environment, "conexion_estado", null, "success", "Estado local de perfil consultado.", timer.ElapsedMilliseconds);
                return status;
            }
            catch (Exception error)
            {
                AuditLog.Append(project, environment ?? "all", "conexion_estado", null, "error", SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }

        private object ProfileStatus(string environment)
        {
            ConnectionProfile profile = project.GetProfile(environment, false);
            if (profile == null) return JsonUtil.Dictionary("environment", environment, "configured", false, "credential_available", false, "write_allowed", false);
            bool available = CredentialStore.Exists(profile.credential_target);
            return JsonUtil.Dictionary(
                "environment", environment,
                "configured", true,
                "credential_available", available,
                "write_allowed", environment == "staging" && !profile.read_only,
                "base_url", profile.base_url,
                "database", profile.database,
                "login", profile.login,
                "uid", profile.uid,
                "verified_at", profile.verified_at,
                "credential_fingerprint", profile.credential_fingerprint);
        }

        public object TestConnection(string environment)
        {
            if (string.IsNullOrWhiteSpace(environment))
            {
                List<object> results = new List<object>();
                foreach (string current in new[] { "staging", "production" })
                {
                    results.Add(TestSingleConnection(current));
                }
                return JsonUtil.Dictionary(
                    "client_slug", project.Config.client_slug,
                    "results", results.ToArray());
            }
            return TestSingleConnection(ProjectStore.NormalizeEnvironment(environment));
        }

        private object TestSingleConnection(string environment)
        {
            ConnectionProfile profile = project.GetProfile(environment, false);
            if (profile == null)
            {
                return JsonUtil.Dictionary(
                    "environment", environment,
                    "configured", false,
                    "ok", false,
                    "error", "El entorno '" + environment + "' no está configurado en el proyecto local. Configure la conexión previamente con OdooConsultor.ps1 conexion agregar.");
            }

            bool credentialAvailable = CredentialStore.Exists(profile.credential_target);
            if (!credentialAvailable)
            {
                return JsonUtil.Dictionary(
                    "environment", environment,
                    "configured", true,
                    "credential_available", false,
                    "ok", false,
                    "base_url", profile.base_url,
                    "database", profile.database,
                    "login", profile.login,
                    "error", "La credencial (API key) no fue encontrada en el Administrador de Credenciales de Windows (DPAPI).");
            }

            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                using (OdooClient client = Open(environment))
                {
                    Dictionary<string, object> context = client.ContextGet();
                    long uid = profile.uid;
                    if (context.ContainsKey("uid"))
                    {
                        long parsedUid;
                        if (long.TryParse(Convert.ToString(context["uid"], CultureInfo.InvariantCulture), out parsedUid) && parsedUid > 0)
                        {
                            uid = parsedUid;
                        }
                    }

                    string userName = profile.user_name;
                    string login = profile.login;
                    try
                    {
                        IList<Dictionary<string, object>> users = client.Read("res.users", new[] { uid }, new[] { "login", "name" });
                        if (users != null && users.Count > 0)
                        {
                            if (users[0].ContainsKey("name")) userName = Convert.ToString(users[0]["name"], CultureInfo.InvariantCulture);
                            if (users[0].ContainsKey("login")) login = Convert.ToString(users[0]["login"], CultureInfo.InvariantCulture);
                        }
                    }
                    catch (Exception) { }

                    bool isAdmin = false;
                    try
                    {
                        isAdmin = client.IsSystemAdministrator(uid);
                    }
                    catch (Exception) { }

                    timer.Stop();
                    long latency = timer.ElapsedMilliseconds;

                    AuditLog.Append(project, environment, "conexion_probar", null, "success", "Conexión en vivo exitosa para UID " + uid + " (" + latency + " ms).", latency);

                    return JsonUtil.Dictionary(
                        "environment", environment,
                        "configured", true,
                        "credential_available", true,
                        "ok", true,
                        "latency_ms", latency,
                        "base_url", profile.base_url,
                        "database", profile.database,
                        "user_name", userName,
                        "login", login,
                        "uid", uid,
                        "is_admin", isAdmin,
                        "server_version", profile.server_version,
                        "server_series", profile.server_series,
                        "api_transport", profile.api_transport,
                        "read_only", profile.read_only);
                }
            }
            catch (Exception error)
            {
                timer.Stop();
                long latency = timer.ElapsedMilliseconds;
                string safeErrorMessage = error is ConsultantException ? error.Message : Redactor.Clean(error.Message);
                AuditLog.Append(project, environment, "conexion_probar", null, "error", safeErrorMessage, latency);

                return JsonUtil.Dictionary(
                    "environment", environment,
                    "configured", true,
                    "credential_available", true,
                    "ok", false,
                    "latency_ms", latency,
                    "base_url", profile.base_url,
                    "database", profile.database,
                    "login", profile.login,
                    "error", "Fallo al conectar con Odoo: " + safeErrorMessage);
            }
        }

        public object InspectModel(string environment, string model)
        {
            environment = ProjectStore.NormalizeEnvironment(environment);
            ValidateModel(model);
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                using (OdooClient client = Open(environment))
                {
                    Dictionary<string, object> fields = client.FieldsGet(model, new[] { "string", "type", "required", "readonly", "store", "help", "relation" });
                    IList<Dictionary<string, object>> customFields = QueryCustomFields(client, model);
                    IList<Dictionary<string, object>> views = QueryInheritedViews(client, model);
                    object result = JsonUtil.Dictionary(
                        "environment", environment,
                        "model", model,
                        "field_count", fields.Count,
                        "fields", fields,
                        "custom_fields", customFields,
                        "inherited_views", views,
                        "metadata_hash", SnapshotHash(customFields, views));
                    AuditLog.Append(project, environment, "odoo_inspeccionar_modelo", null, "success", "Modelo inspeccionado: " + model, timer.ElapsedMilliseconds);
                    return result;
                }
            }
            catch (Exception error)
            {
                AuditLog.Append(project, environment, "odoo_inspeccionar_modelo", null, "error", SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }

        public object Inventory(string environment, string model)
        {
            environment = ProjectStore.NormalizeEnvironment(environment);
            if (!string.IsNullOrWhiteSpace(model)) ValidateModel(model);
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                using (OdooClient client = Open(environment))
                {
                    object[] fieldDomain = string.IsNullOrWhiteSpace(model)
                        ? new object[] { new object[] { "name", "ilike", "x_" } }
                        : new object[] { new object[] { "model", "=", model }, new object[] { "name", "ilike", "x_" } };
                    object[] viewDomain = string.IsNullOrWhiteSpace(model)
                        ? new object[] { new object[] { "name", "ilike", "studio" } }
                        : new object[] { new object[] { "model", "=", model }, new object[] { "inherit_id", "!=", false } };
                    IList<Dictionary<string, object>> customFields = client.SearchRead("ir.model.fields", fieldDomain,
                        new[] { "id", "model", "name", "field_description", "ttype", "required", "readonly", "state", "relation" }, 200, "model, name");
                    IList<Dictionary<string, object>> views = client.SearchRead("ir.ui.view", viewDomain,
                        new[] { "id", "name", "model", "type", "inherit_id", "mode", "priority", "active" }, 200, "model, name");
                    object result = JsonUtil.Dictionary(
                        "environment", environment,
                        "model_filter", model,
                        "custom_field_count", customFields.Count,
                        "inherited_view_count", views.Count,
                        "custom_fields", customFields,
                        "inherited_views", views,
                        "inventory_hash", SnapshotHash(customFields, views),
                        "coverage", JsonUtil.Dictionary(
                            "executable", new[] { "campo_personalizado_declarativo", "vista_heredada_declarativa" },
                            "validated_replica_phase_1", new[] { "char", "binary", "selection", "many2many", "opciones_seleccion", "accion_servidor_firmada", "vista_con_accion_interna" },
                            "validated_replica_phase_2", new[] { "modelo_personalizado", "campos_studio", "campo_relacionado", "campo_calculado_nuevo_firmado", "vista_base_personalizada", "vista_heredada", "accion_ventana", "menu", "actualizacion_con_previous_hash", "desactivacion_segura" },
                            "runbook_only", new[] { "reporte", "automatizacion_general", "webhook", "seguridad", "actualizacion_codigo_calculado", "migracion_estructural_campo", "llamada_metodo", "eliminacion_datos" }));
                    AuditLog.Append(project, environment, "studio_inventariar", null, "success", "Inventario Studio consultado.", timer.ElapsedMilliseconds);
                    return result;
                }
            }
            catch (Exception error)
            {
                AuditLog.Append(project, environment, "studio_inventariar", null, "error", SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }

        public object PrepareChange(string environment, string type, string model, IDictionary<string, object> specification)
        {
            environment = ProjectStore.NormalizeEnvironment(environment);
            EnsureWriteAllowed(environment);
            ValidateModel(model);
            string normalizedType = (type ?? string.Empty).Trim().ToLowerInvariant();
            if (normalizedType != "campo" && normalizedType != "vista") throw new ConsultantException("runbook_required", "Esta categoría es de alto riesgo o no es aditiva. Usa studio_generar_runbook.");
            Stopwatch timer = Stopwatch.StartNew();
            string packageId = "chg-" + Guid.NewGuid().ToString("N");
            try
            {
                ConnectionProfile profile = project.GetProfile(environment, true);
                using (OdooClient client = Open(environment))
                {
                    IList<Dictionary<string, object>> fields = QueryCustomFields(client, model);
                    IList<Dictionary<string, object>> views = QueryInheritedViews(client, model);
                    string preconditionHash = SnapshotHash(fields, views);
                    ChangeOperation operation = normalizedType == "campo"
                        ? PrepareFieldOperation(client, model, specification)
                        : PrepareViewOperation(client, model, specification, packageId);
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    ChangeSet change = new ChangeSet
                    {
                        schema_version = 1,
                        id = packageId,
                        client_slug = project.Config.client_slug,
                        environment = environment,
                        category = normalizedType == "campo" ? "studio_custom_field" : "studio_inherited_view",
                        risk = normalizedType == "campo" ? "medium" : "medium",
                        status = "prepared",
                        model = model,
                        created_at = now.ToString("o", CultureInfo.InvariantCulture),
                        expires_at = now.AddMinutes(project.Config.package_confirmation_minutes).ToString("o", CultureInfo.InvariantCulture),
                        precondition_hash = preconditionHash,
                        profile_fingerprint = profile.credential_fingerprint,
                        requested_by = Environment.UserName,
                        impact = normalizedType == "campo"
                            ? "Añade un campo personalizado sin código a un modelo existente. No elimina datos."
                            : "Añade una vista heredada declarativa. No modifica directamente la vista estándar.",
                        rollback_strategy = normalizedType == "campo"
                            ? "El campo se conserva para evitar pérdida de datos; se retira de las vistas mediante runbook."
                            : "La vista creada puede desactivarse sin eliminarla.",
                        operations = new List<ChangeOperation> { operation },
                        warnings = new List<string> { "Revisar el paquete antes de confirmar.", "La confirmación exacta es APLICAR " + packageId + "." }
                    };
                    JsonUtil.WriteJsonFile(project.ChangeSetPath(packageId), change);
                    AuditLog.Append(project, environment, "studio_preparar_cambio", packageId, "success", "Paquete preparado para " + normalizedType + " en " + model + ".", timer.ElapsedMilliseconds);
                    return PackageSummary(change, true);
                }
            }
            catch (Exception error)
            {
                AuditLog.Append(project, environment, "studio_preparar_cambio", packageId, "error", SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }

        public object PrepareDeclarativeChange(string environment, IDictionary<string, object> contract, PolicyDecision decision)
        {
            environment = ProjectStore.NormalizeEnvironment(environment);
            EnsureWriteAllowed(environment);
            if (decision == null || !decision.executable) throw new ConsultantException("runbook_required", "El contrato no puede compilarse como paquete ejecutable.");
            ValidateModel(decision.target_model);
            Stopwatch timer = Stopwatch.StartNew();
            string packageId = "chg-" + Guid.NewGuid().ToString("N");
            try
            {
                ConnectionProfile profile = project.GetProfile(environment, true);
                using (OdooClient client = Open(environment))
                {
                    IList<Dictionary<string, object>> models = client.SearchRead("ir.model", new object[] { new object[] { "model", "=", decision.target_model } }, new[] { "id", "model", "name" }, 2, null);
                    if (models.Count != 1) throw new ConsultantException("model_not_found", "El modelo objetivo no existe o no es visible.");
                    string preconditionHash = ConfigurationSnapshotHash(client, decision.target_model);
                    List<ChangeOperation> operations = new List<ChangeOperation>();
                    HashSet<string> fieldNames = new HashSet<string>(StringComparer.Ordinal);
                    HashSet<string> viewKeys = new HashSet<string>(StringComparer.Ordinal);
                    foreach (Dictionary<string, object> requested in decision.operations)
                    {
                        string operationType = Convert.ToString(requested["type"], CultureInfo.InvariantCulture);
                        Dictionary<string, object> parameters = requested["parameters"] as Dictionary<string, object>;
                        ChangeOperation operation;
                        if (operationType == "create_custom_field")
                        {
                            if (!client.CheckAccessRights("ir.model.fields", "create")) throw new ConsultantException("technical_create_denied", "El usuario no tiene permiso para crear metadatos de campos.");
                            operation = PrepareDeclarativeFieldOperation(client, decision.target_model, parameters);
                            string fieldName = Convert.ToString(operation.parameters["name"], CultureInfo.InvariantCulture);
                            if (!fieldNames.Add(fieldName)) throw new ConsultantException("duplicate_operation", "El contrato repite el campo " + fieldName + ".");
                        }
                        else if (operationType == "create_inherited_view")
                        {
                            if (!client.CheckAccessRights("ir.ui.view", "create")) throw new ConsultantException("technical_create_denied", "El usuario no tiene permiso para crear vistas heredadas.");
                            operation = PrepareDeclarativeViewOperation(client, decision.target_model, parameters, packageId);
                            string viewKey = Convert.ToString(operation.parameters["inherit_id"], CultureInfo.InvariantCulture) + "|" + Convert.ToString(operation.parameters["name"], CultureInfo.InvariantCulture);
                            if (!viewKeys.Add(viewKey)) throw new ConsultantException("duplicate_operation", "El contrato repite la misma vista heredada.");
                        }
                        else throw new ConsultantException("tool_contract_violation", "El compilador recibió una operación no ejecutable.");
                        operations.Add(operation);
                    }
                    string capabilityHash = CapabilityHash(client, profile, decision.target_model);
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    ChangeSet change = new ChangeSet
                    {
                        schema_version = 2,
                        id = packageId,
                        client_slug = project.Config.client_slug,
                        environment = environment,
                        category = "declarative_change",
                        risk = decision.risk,
                        status = "prepared",
                        model = decision.target_model,
                        created_at = now.ToString("o", CultureInfo.InvariantCulture),
                        expires_at = now.AddMinutes(project.Config.package_confirmation_minutes).ToString("o", CultureInfo.InvariantCulture),
                        precondition_hash = preconditionHash,
                        profile_fingerprint = profile.credential_fingerprint,
                        requested_by = Environment.UserName,
                        impact = JsonUtil.GetString(contract, "impact", false) ?? "Añade metadatos declarativos a un modelo existente sin eliminar datos.",
                        rollback_strategy = "Las vistas creadas pueden desactivarse. Los campos y sus datos se preservan y requieren limpieza manual.",
                        policy_version = PolicyCompiler.Version,
                        objective = decision.objective,
                        contract_hash = decision.contract_hash,
                        capability_hash = capabilityHash,
                        preconditions = new List<Dictionary<string, object>>
                        {
                            JsonUtil.Dictionary("type", "configuration_hash", "expected", preconditionHash),
                            JsonUtil.Dictionary("type", "credential_fingerprint", "expected", profile.credential_fingerprint),
                            JsonUtil.Dictionary("type", "environment", "expected", "staging")
                        },
                        validations = new List<Dictionary<string, object>>
                        {
                            JsonUtil.Dictionary("type", "metadata_exists", "required", true),
                            JsonUtil.Dictionary("type", "idempotency_recovery", "required", true)
                        },
                        operations = operations,
                        warnings = new List<string> { "Revisar todas las operaciones compiladas antes de confirmar.", "La confirmación exacta es APLICAR " + packageId + "." }
                    };
                    change.integrity_hash = PackageIntegrityHash(change);
                    ValidatePackageContract(change);
                    JsonUtil.WriteJsonFile(project.ChangeSetPath(packageId), change);
                    AuditLog.Append(project, environment, "cambio_preparar_paquete", packageId, "success", "Contrato " + decision.contract_hash.Substring(0, 12) + " compilado en " + operations.Count.ToString(CultureInfo.InvariantCulture) + " operaciones.", timer.ElapsedMilliseconds);
                    return PackageSummary(change, true);
                }
            }
            catch (Exception error)
            {
                AuditLog.Append(project, environment, "cambio_preparar_paquete", packageId, "error", SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }

        public object ApplyPackage(string packageId, string confirmation)
        {
            return ApplyPackage(packageId, confirmation, 0, null);
        }

        public object ApplyPackage(string packageId, string confirmation, long testRecordId, IDictionary<string, object> references)
        {
            Stopwatch timer = Stopwatch.StartNew();
            ChangeSet change = LoadPackage(packageId);
            if (change.schema_version == 4) return new FunctionalService(project).Apply(change, confirmation);
            if (change.schema_version == 3) return new ReplicaService(project).Apply(change, confirmation, testRecordId, references);
            try
            {
                EnsureWriteAllowed(change.environment, change.category);
                if (change.status == "applied" || change.status == "verified")
                {
                    AuditLog.Append(project, change.environment, "studio_aplicar_paquete", packageId, "success", "Reintento idempotente; el paquete ya estaba aplicado.", timer.ElapsedMilliseconds);
                    return PackageSummary(change, false);
                }
                if (change.status == "applying" || change.status == "failed")
                {
                    ConnectionProfile recoveryProfile = project.GetProfile(change.environment, true);
                    if (!string.Equals(recoveryProfile.credential_fingerprint, change.profile_fingerprint, StringComparison.Ordinal)) throw new ConsultantException("profile_changed", "La credencial cambió desde la preparación. Se requiere revisión manual.");
                    using (OdooClient recoveryClient = Open(change.environment))
                    {
                        if (!TryRecoverApplied(recoveryClient, change)) throw new ConsultantException("package_recovery_required", "El resultado del intento anterior no pudo comprobarse. No se reintentó ninguna escritura.");
                    }
                    change.status = "applied";
                    change.applied_at = change.applied_at ?? DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    change.last_error = null;
                    JsonUtil.WriteJsonFile(project.ChangeSetPath(packageId), change);
                    AuditLog.Append(project, change.environment, "studio_aplicar_paquete", packageId, "success", "Resultado previo recuperado sin duplicar la escritura.", timer.ElapsedMilliseconds);
                    return PackageSummary(change, false);
                }
                if (change.status != "prepared") throw new ConsultantException("package_not_applicable", "El paquete no está en estado prepared.");
                DateTimeOffset expiresAt;
                if (!DateTimeOffset.TryParse(change.expires_at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expiresAt) || DateTimeOffset.UtcNow > expiresAt) throw new ConsultantException("confirmation_expired", "La confirmación venció. Prepara un paquete nuevo.");
                if (!string.Equals(confirmation, "APLICAR " + packageId, StringComparison.Ordinal)) throw new ConsultantException("confirmation_mismatch", "La confirmación debe ser exactamente APLICAR " + packageId + ".");
                ConnectionProfile profile = project.GetProfile(change.environment, true);
                if (!string.Equals(profile.credential_fingerprint, change.profile_fingerprint, StringComparison.Ordinal)) throw new ConsultantException("profile_changed", "La credencial cambió desde la preparación. Prepara un paquete nuevo.");
                using (OdooClient client = Open(change.environment))
                {
                    if (change.schema_version >= 2)
                    {
                        string currentCapabilityHash = CapabilityHash(client, profile, change.model);
                        if (!string.Equals(currentCapabilityHash, change.capability_hash, StringComparison.Ordinal)) throw new ConsultantException("capabilities_changed", "Los permisos o capacidades cambiaron desde la vista previa. No se aplicó nada.");
                    }
                    string currentHash = change.schema_version >= 2
                        ? ConfigurationSnapshotHash(client, change.model)
                        : SnapshotHash(QueryCustomFields(client, change.model), QueryInheritedViews(client, change.model));
                    if (!string.Equals(currentHash, change.precondition_hash, StringComparison.Ordinal)) throw new ConsultantException("precondition_changed", "La configuración cambió desde la vista previa. No se aplicó nada.");
                    change.status = "applying";
                    JsonUtil.WriteJsonFile(project.ChangeSetPath(packageId), change);
                    try
                    {
                        foreach (ChangeOperation operation in change.operations)
                        {
                            if (operation.status == "applied") continue;
                            operation.result_id = ApplyOperation(client, operation);
                            operation.status = "applied";
                            JsonUtil.WriteJsonFile(project.ChangeSetPath(packageId), change);
                        }
                        change.status = "applied";
                        change.applied_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                        JsonUtil.WriteJsonFile(project.ChangeSetPath(packageId), change);
                    }
                    catch (Exception operationError)
                    {
                        change.status = "failed";
                        change.last_error = SafeError(operationError);
                        JsonUtil.WriteJsonFile(project.ChangeSetPath(packageId), change);
                        throw;
                    }
                }
                AuditLog.Append(project, change.environment, "studio_aplicar_paquete", packageId, "success", "Paquete aplicado en staging.", timer.ElapsedMilliseconds);
                return PackageSummary(change, false);
            }
            catch (Exception error)
            {
                AuditLog.Append(project, change.environment, "studio_aplicar_paquete", packageId, "error", SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }

        public object VerifyPackage(string packageId)
        {
            Stopwatch timer = Stopwatch.StartNew();
            ChangeSet change = LoadPackage(packageId);
            if (change.schema_version == 4) return new FunctionalService(project).Verify(change);
            if (change.schema_version == 3) return new ReplicaService(project).Verify(change);
            try
            {
                if (change.status != "applied" && change.status != "verified" && change.status != "rolled_back" && change.status != "rollback_partial") throw new ConsultantException("package_not_verifiable", "El paquete todavía no se ha aplicado.");
                using (OdooClient client = Open(change.environment))
                {
                    bool allVerified = true;
                    foreach (ChangeOperation operation in change.operations)
                    {
                        bool verified = VerifyOperation(client, operation, change.status);
                        operation.verification = verified ? "verified" : "not_found_or_inactive";
                        if (!verified && change.status != "rolled_back") allVerified = false;
                    }
                    if (allVerified && change.status == "applied") change.status = "verified";
                    change.verified_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    JsonUtil.WriteJsonFile(project.ChangeSetPath(packageId), change);
                }
                AuditLog.Append(project, change.environment, "studio_verificar_paquete", packageId, "success", "Verificación técnica completada.", timer.ElapsedMilliseconds);
                return PackageSummary(change, false);
            }
            catch (Exception error)
            {
                AuditLog.Append(project, change.environment, "studio_verificar_paquete", packageId, "error", SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }

        public object RollbackPackage(string packageId, string confirmation)
        {
            Stopwatch timer = Stopwatch.StartNew();
            ChangeSet change = LoadPackage(packageId);
            if (change.schema_version == 4) return new FunctionalService(project).Rollback(change, confirmation);
            if (change.schema_version == 3) return new ReplicaService(project).Rollback(change, confirmation);
            try
            {
                EnsureWriteAllowed(change.environment);
                if (!string.Equals(confirmation, "REVERTIR " + packageId, StringComparison.Ordinal)) throw new ConsultantException("confirmation_mismatch", "La confirmación debe ser exactamente REVERTIR " + packageId + ".");
                if (change.status == "rolled_back" || change.status == "rollback_partial") return PackageSummary(change, false);
                if (change.status != "applied" && change.status != "verified") throw new ConsultantException("package_not_reversible", "Solo se pueden revertir paquetes aplicados.");
                bool partial = false;
                using (OdooClient client = Open(change.environment))
                {
                    foreach (ChangeOperation operation in change.operations)
                    {
                        if (operation.type == "create_inherited_view" && operation.result_id > 0)
                        {
                            bool written = client.Write("ir.ui.view", new[] { operation.result_id }, JsonUtil.Dictionary("active", false));
                            operation.status = written ? "rolled_back" : "rollback_failed";
                            partial = partial || !written;
                        }
                        else
                        {
                            operation.status = "preserved_manual_cleanup";
                            partial = true;
                        }
                    }
                }
                change.status = partial ? "rollback_partial" : "rolled_back";
                change.rolled_back_at = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                JsonUtil.WriteJsonFile(project.ChangeSetPath(packageId), change);
                AuditLog.Append(project, change.environment, "studio_revertir_paquete_seguro", packageId, "success", partial ? "Reversión parcial; los campos se preservaron." : "Vista desactivada de forma reversible.", timer.ElapsedMilliseconds);
                return PackageSummary(change, false);
            }
            catch (Exception error)
            {
                AuditLog.Append(project, change.environment, "studio_revertir_paquete_seguro", packageId, "error", SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }

        public object GenerateRunbook(string environment, string category, string title, string objective, IList<object> requestedSteps)
        {
            environment = ProjectStore.NormalizeEnvironment(environment);
            string runbookId = "run-" + Guid.NewGuid().ToString("N");
            string safeCategory = (category ?? string.Empty).Trim().ToLowerInvariant();
            HashSet<string> allowed = new HashSet<string>(new[] { "modelo_nuevo", "reporte", "automatizacion", "webhook", "seguridad", "accion_servidor", "codigo_calculado", "llamada_metodo", "modificacion", "eliminacion", "promocion_produccion" }, StringComparer.Ordinal);
            if (!allowed.Contains(safeCategory)) throw new ConsultantException("invalid_runbook_category", "La categoría de runbook no está permitida.");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(objective)) throw new ConsultantException("invalid_input", "El título y el objetivo son obligatorios.");
            StringBuilder markdown = new StringBuilder();
            markdown.AppendLine("# " + SanitizeMarkdown(title));
            markdown.AppendLine();
            markdown.AppendLine("- ID: `" + runbookId + "`");
            markdown.AppendLine("- Cliente: `" + project.Config.client_slug + "`");
            markdown.AppendLine("- Entorno de preparación: `" + environment + "`");
            markdown.AppendLine("- Categoría: `" + safeCategory + "`");
            markdown.AppendLine("- Creado: " + DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            markdown.AppendLine();
            markdown.AppendLine("## Objetivo");
            markdown.AppendLine();
            markdown.AppendLine(SanitizeMarkdown(objective));
            markdown.AppendLine();
            markdown.AppendLine("## Controles obligatorios");
            markdown.AppendLine();
            markdown.AppendLine("1. Ejecutar primero en staging y comprobar permisos del usuario individual.");
            markdown.AppendLine("2. Capturar el estado previo sin datos sensibles.");
            markdown.AppendLine("3. Revisar efectos laterales, seguridad, automatizaciones y posibilidad de reversión.");
            markdown.AppendLine("4. Ejecutar manualmente en Odoo; este runbook no concede una herramienta genérica al agente.");
            markdown.AppendLine("5. Validar visual y funcionalmente antes de documentar la promoción a producción.");
            markdown.AppendLine();
            markdown.AppendLine("## Pasos solicitados");
            markdown.AppendLine();
            if (requestedSteps == null || requestedSteps.Count == 0) markdown.AppendLine("1. Completar los pasos detallados durante la revisión funcional.");
            else
            {
                for (int i = 0; i < requestedSteps.Count; i++) markdown.AppendLine((i + 1).ToString(CultureInfo.InvariantCulture) + ". " + SanitizeMarkdown(Convert.ToString(requestedSteps[i], CultureInfo.InvariantCulture)));
            }
            markdown.AppendLine();
            markdown.AppendLine("## Resultado y evidencias");
            markdown.AppendLine();
            markdown.AppendLine("Registrar únicamente IDs técnicos, hashes, fechas y resultados anonimizados.");
            File.WriteAllText(project.RunbookPath(runbookId), markdown.ToString(), new UTF8Encoding(false));
            AuditLog.Append(project, environment, "studio_generar_runbook", runbookId, "success", "Runbook generado para " + safeCategory + ".", 0);
            return JsonUtil.Dictionary("runbook_id", runbookId, "category", safeCategory, "path", Path.Combine("cambios", "runbooks", runbookId + ".md"), "execution", "manual_only");
        }

        private ChangeOperation PrepareFieldOperation(OdooClient client, string model, IDictionary<string, object> specification)
        {
            string name = JsonUtil.GetString(specification, "nombre_tecnico", true).ToLowerInvariant();
            if (!CustomFieldPattern.IsMatch(name)) throw new ConsultantException("invalid_custom_field", "El nombre técnico debe comenzar por x_ y usar minúsculas, números o guion bajo.");
            string label = JsonUtil.GetString(specification, "etiqueta", true);
            string fieldType = JsonUtil.GetString(specification, "tipo_campo", true).ToLowerInvariant();
            if (!AllowedFieldTypes.Contains(fieldType)) throw new ConsultantException("unsafe_field_type", "El tipo de campo no está permitido en ejecución automática v1.");
            if (JsonUtil.GetBool(specification, "requerido", false)) throw new ConsultantException("required_field_runbook", "Un campo requerido sobre registros existentes exige un runbook y estrategia de datos.");
            string relation = JsonUtil.GetString(specification, "relacion", false);
            if (fieldType == "many2one")
            {
                ValidateModel(relation);
            }
            else if (!string.IsNullOrWhiteSpace(relation)) throw new ConsultantException("invalid_relation", "relacion solo se admite para many2one.");
            IList<Dictionary<string, object>> existing = client.SearchRead("ir.model.fields", new object[] { new object[] { "model", "=", model }, new object[] { "name", "=", name } }, new[] { "id", "name" }, 2, null);
            if (existing.Count > 0) throw new ConsultantException("field_exists", "El campo ya existe; la primera versión no modifica campos existentes.");
            Dictionary<string, object> parameters = JsonUtil.Dictionary(
                "name", name,
                "field_description", label,
                "ttype", fieldType,
                "required", false,
                "readonly", false,
                "help", JsonUtil.GetString(specification, "ayuda", false));
            if (!string.IsNullOrWhiteSpace(relation)) parameters["relation"] = relation;
            return new ChangeOperation { operation_id = "op-" + Guid.NewGuid().ToString("N"), type = "create_custom_field", model = model, parameters = parameters, status = "pending" };
        }

        private ChangeOperation PrepareViewOperation(OdooClient client, string model, IDictionary<string, object> specification, string packageId)
        {
            string name = JsonUtil.GetString(specification, "nombre", true);
            long parentId = JsonUtil.GetLong(specification, "vista_padre_id", true);
            string architecture = JsonUtil.GetString(specification, "arquitectura_xml", true);
            ValidateSafeViewArchitecture(architecture);
            IList<Dictionary<string, object>> parent = client.Read("ir.ui.view", new[] { parentId }, new[] { "id", "name", "model", "active" });
            if (parent.Count != 1 || !string.Equals(Convert.ToString(parent[0]["model"], CultureInfo.InvariantCulture), model, StringComparison.Ordinal)) throw new ConsultantException("parent_view_mismatch", "La vista padre no existe o no pertenece al modelo indicado.");
            Dictionary<string, object> parameters = JsonUtil.Dictionary(
                "name", "OdooAI - " + packageId + " - " + name,
                "inherit_id", parentId,
                "arch", architecture,
                "mode", "extension",
                "active", true);
            return new ChangeOperation { operation_id = "op-" + Guid.NewGuid().ToString("N"), type = "create_inherited_view", model = model, parameters = parameters, status = "pending" };
        }

        private ChangeOperation PrepareDeclarativeFieldOperation(OdooClient client, string model, IDictionary<string, object> parameters)
        {
            PolicyCompiler.EnsureOnlyKeys(parameters, new[] { "name", "label", "field_type", "help", "relation", "required" });
            Dictionary<string, object> legacy = JsonUtil.Dictionary(
                "nombre_tecnico", JsonUtil.GetString(parameters, "name", true),
                "etiqueta", JsonUtil.GetString(parameters, "label", true),
                "tipo_campo", JsonUtil.GetString(parameters, "field_type", true),
                "ayuda", JsonUtil.GetString(parameters, "help", false),
                "relacion", JsonUtil.GetString(parameters, "relation", false),
                "requerido", JsonUtil.GetBool(parameters, "required", false));
            return PrepareFieldOperation(client, model, legacy);
        }

        private ChangeOperation PrepareDeclarativeViewOperation(OdooClient client, string model, IDictionary<string, object> parameters, string packageId)
        {
            PolicyCompiler.EnsureOnlyKeys(parameters, new[] { "name", "parent_view_id", "architecture_xml" });
            Dictionary<string, object> legacy = JsonUtil.Dictionary(
                "nombre", JsonUtil.GetString(parameters, "name", true),
                "vista_padre_id", JsonUtil.GetLong(parameters, "parent_view_id", true),
                "arquitectura_xml", JsonUtil.GetString(parameters, "architecture_xml", true));
            return PrepareViewOperation(client, model, legacy, packageId);
        }

        private long ApplyOperation(OdooClient client, ChangeOperation operation)
        {
            if (operation.type == "create_custom_field")
            {
                IList<Dictionary<string, object>> models = client.SearchRead("ir.model", new object[] { new object[] { "model", "=", operation.model } }, new[] { "id", "model" }, 2, null);
                if (models.Count != 1) throw new ConsultantException("model_not_found", "No se encontró un único modelo técnico para crear el campo.");
                long modelId = Convert.ToInt64(models[0]["id"], CultureInfo.InvariantCulture);
                Dictionary<string, object> values = new Dictionary<string, object>(operation.parameters, StringComparer.Ordinal);
                values["model_id"] = modelId;
                values["state"] = "manual";
                return client.Create("ir.model.fields", values);
            }
            if (operation.type == "create_inherited_view")
            {
                Dictionary<string, object> values = new Dictionary<string, object>(operation.parameters, StringComparer.Ordinal);
                values["model"] = operation.model;
                return client.Create("ir.ui.view", values);
            }
            throw new ConsultantException("tool_contract_violation", "El paquete contiene una operación no permitida.");
        }

        private bool VerifyOperation(OdooClient client, ChangeOperation operation, string packageStatus)
        {
            if (operation.type == "create_custom_field")
            {
                string name = Convert.ToString(operation.parameters["name"], CultureInfo.InvariantCulture);
                IList<Dictionary<string, object>> records = client.SearchRead("ir.model.fields", new object[] { new object[] { "model", "=", operation.model }, new object[] { "name", "=", name } }, new[] { "id", "name", "ttype", "state" }, 2, null);
                return records.Any(delegate(Dictionary<string, object> row) { return Convert.ToInt64(row["id"], CultureInfo.InvariantCulture) == operation.result_id; });
            }
            if (operation.type == "create_inherited_view")
            {
                IList<Dictionary<string, object>> records = client.Read("ir.ui.view", new[] { operation.result_id }, new[] { "id", "active", "model", "inherit_id" });
                if (records.Count != 1) return false;
                bool active = Convert.ToBoolean(records[0]["active"], CultureInfo.InvariantCulture);
                return packageStatus == "rolled_back" || packageStatus == "rollback_partial" ? !active : active;
            }
            return false;
        }

        private bool TryRecoverApplied(OdooClient client, ChangeSet change)
        {
            foreach (ChangeOperation operation in change.operations)
            {
                if (operation.type == "create_custom_field")
                {
                    string name = Convert.ToString(operation.parameters["name"], CultureInfo.InvariantCulture);
                    IList<Dictionary<string, object>> records = client.SearchRead("ir.model.fields",
                        new object[] { new object[] { "model", "=", operation.model }, new object[] { "name", "=", name } },
                        new[] { "id", "name" }, 2, null);
                    if (records.Count != 1) return false;
                    operation.result_id = Convert.ToInt64(records[0]["id"], CultureInfo.InvariantCulture);
                    operation.status = "applied";
                }
                else if (operation.type == "create_inherited_view")
                {
                    string name = Convert.ToString(operation.parameters["name"], CultureInfo.InvariantCulture);
                    long parentId = Convert.ToInt64(operation.parameters["inherit_id"], CultureInfo.InvariantCulture);
                    IList<Dictionary<string, object>> records = client.SearchRead("ir.ui.view",
                        new object[] { new object[] { "model", "=", operation.model }, new object[] { "name", "=", name }, new object[] { "inherit_id", "=", parentId } },
                        new[] { "id", "name", "active" }, 2, null);
                    if (records.Count != 1) return false;
                    operation.result_id = Convert.ToInt64(records[0]["id"], CultureInfo.InvariantCulture);
                    operation.status = "applied";
                }
                else return false;
            }
            return true;
        }

        private IList<Dictionary<string, object>> QueryCustomFields(OdooClient client, string model)
        {
            return client.SearchRead("ir.model.fields", new object[] { new object[] { "model", "=", model }, new object[] { "name", "ilike", "x_" } },
                new[] { "id", "model", "name", "field_description", "ttype", "required", "readonly", "state", "relation" }, 200, "name");
        }

        private IList<Dictionary<string, object>> QueryInheritedViews(OdooClient client, string model)
        {
            return client.SearchRead("ir.ui.view", new object[] { new object[] { "model", "=", model }, new object[] { "inherit_id", "!=", false } },
                new[] { "id", "name", "model", "type", "inherit_id", "mode", "priority", "active" }, 200, "name");
        }

        private static string SnapshotHash(IList<Dictionary<string, object>> fields, IList<Dictionary<string, object>> views)
        {
            List<Dictionary<string, object>> orderedFields = fields.OrderBy(delegate(Dictionary<string, object> row) { return Convert.ToString(row.ContainsKey("name") ? row["name"] : string.Empty, CultureInfo.InvariantCulture); }, StringComparer.Ordinal).ToList();
            List<Dictionary<string, object>> orderedViews = views.OrderBy(delegate(Dictionary<string, object> row) { return Convert.ToInt64(row.ContainsKey("id") ? row["id"] : 0, CultureInfo.InvariantCulture); }).ToList();
            return JsonUtil.CanonicalHash(JsonUtil.Dictionary("fields", orderedFields, "views", orderedViews));
        }

        private static string ConfigurationSnapshotHash(OdooClient client, string model)
        {
            Dictionary<string, object> fields = client.FieldsGet(model, new[] { "string", "type", "required", "readonly", "store", "relation" });
            IList<Dictionary<string, object>> views = client.SearchRead("ir.ui.view", new object[] { new object[] { "model", "=", model } },
                new[] { "id", "name", "type", "inherit_id", "mode", "priority", "active", "write_date" }, 500, "name asc");
            return JsonUtil.CanonicalHash(JsonUtil.Dictionary("fields", fields, "views", views));
        }

        private OdooClient Open(string environment)
        {
            return project.OpenClient(environment, 30000);
        }

        private ChangeSet LoadPackage(string packageId)
        {
            ChangeSet change = JsonUtil.ReadJsonFile<ChangeSet>(project.ChangeSetPath(packageId));
            if (change.schema_version != 1 && change.schema_version != 2 && change.schema_version != 3 && change.schema_version != 4) throw new ConsultantException("unsupported_package_schema", "La versión del paquete no está soportada.");
            if (!string.Equals(change.client_slug, project.Config.client_slug, StringComparison.Ordinal)) throw new ConsultantException("package_tenant_mismatch", "El paquete pertenece a otro cliente.");
            if (change.operations == null || change.operations.Count == 0) throw new ConsultantException("invalid_package", "El paquete no contiene operaciones.");
            if (change.schema_version == 4) FunctionalService.ValidatePackage(change);
            else if (change.schema_version == 3) ValidateReplicaPackage(change);
            else ValidatePackageContract(change);
            return change;
        }

        private static void ValidateReplicaPackage(ChangeSet change)
        {
            if (string.Equals(change.category, "validated_replica_phase3", StringComparison.Ordinal))
            {
                ReplicaPhase3Service.ValidatePackage(change);
                return;
            }
            if (string.Equals(change.category, "validated_replica_phase4", StringComparison.Ordinal))
            {
                ReplicaPhase4Service.ValidatePackage(change);
                return;
            }
            if (string.Equals(change.category, "validated_replica_phase2", StringComparison.Ordinal))
            {
                ReplicaPhase2Service.ValidatePackage(change);
                return;
            }
            ReplicaService.ValidatePackage(change);
        }

        private static void ValidatePackageContract(ChangeSet change)
        {
            EnsureWriteAllowed(change.environment);
            ValidateModel(change.model);
            int maximum = change.schema_version >= 2 ? 10 : 1;
            if (change.operations.Count < 1 || change.operations.Count > maximum) throw new ConsultantException("tool_contract_violation", "El paquete supera el número de operaciones permitido por su versión.");
            if (change.schema_version >= 2)
            {
                if (!string.Equals(change.policy_version, PolicyCompiler.Version, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(change.contract_hash) || string.IsNullOrWhiteSpace(change.capability_hash) || string.IsNullOrWhiteSpace(change.integrity_hash)) throw new ConsultantException("tool_contract_violation", "El paquete declarativo no contiene las huellas de política, capacidades e integridad requeridas.");
                if (!string.Equals(change.integrity_hash, PackageIntegrityHash(change), StringComparison.Ordinal)) throw new ConsultantException("package_integrity_failed", "La parte inmutable del paquete fue modificada después de su preparación.");
            }
            HashSet<string> operationIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (ChangeOperation operation in change.operations)
            {
                if (!string.Equals(operation.model, change.model, StringComparison.Ordinal)) throw new ConsultantException("tool_contract_violation", "El modelo del paquete no coincide con su operación.");
                if (string.IsNullOrWhiteSpace(operation.operation_id) || !operationIds.Add(operation.operation_id)) throw new ConsultantException("tool_contract_violation", "Los IDs de operación deben existir y ser únicos.");
                PolicyCompiler.ValidateExecutableOperation(change.id, operation);
            }
        }

        private static void EnsureOnlyKeys(IDictionary<string, object> values, IEnumerable<string> allowed)
        {
            HashSet<string> allowlist = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (string key in values.Keys) if (!allowlist.Contains(key)) throw new ConsultantException("tool_contract_violation", "El paquete contiene el parámetro no permitido " + key + ".");
        }

        private static object PackageSummary(ChangeSet change, bool includeConfirmation)
        {
            Dictionary<string, object> summary = JsonUtil.Dictionary(
                "package_id", change.id,
                "client_slug", change.client_slug,
                "environment", change.environment,
                "category", change.category,
                "risk", change.risk,
                "status", change.status,
                "model", change.model,
                "created_at", change.created_at,
                "expires_at", change.expires_at,
                "impact", change.impact,
                "rollback_strategy", change.rollback_strategy,
                "precondition_hash", change.precondition_hash,
                "policy_version", change.policy_version,
                "objective", change.objective,
                "contract_hash", change.contract_hash,
                "capability_hash", change.capability_hash,
                "integrity_hash", change.integrity_hash,
                "preconditions", change.preconditions,
                "validations", change.validations,
                "operations", change.operations,
                "warnings", change.warnings,
                "last_error", change.last_error);
            if (includeConfirmation) summary["required_confirmation"] = "APLICAR " + change.id;
            return summary;
        }

        public static void EnsureWriteAllowed(string environment)
        {
            EnsureWriteAllowed(environment, null);
        }

        public static void EnsureWriteAllowed(string environment, string category)
        {
            if (string.Equals(environment, "staging", StringComparison.Ordinal)) return;
            if (string.Equals(environment, "production", StringComparison.Ordinal) && string.Equals(category, "production_promotion", StringComparison.Ordinal)) return;
            throw new ConsultantException("production_write_blocked", "Las escrituras en producción están bloqueadas antes de realizar cualquier llamada de red.");
        }

        internal static void ValidateModel(string model)
        {
            if (!ModelPattern.IsMatch(model ?? string.Empty)) throw new ConsultantException("invalid_model", "El nombre técnico del modelo no es válido.");
        }

        internal static void ValidateSafeViewArchitecture(string xml)
        {
            if (xml.Length > 50000) throw new ConsultantException("view_too_large", "La arquitectura supera el máximo de 50.000 caracteres.");
            XmlDocument document = new XmlDocument();
            document.XmlResolver = null;
            try
            {
                XmlReaderSettings settings = new XmlReaderSettings();
                settings.DtdProcessing = DtdProcessing.Prohibit;
                settings.XmlResolver = null;
                settings.MaxCharactersInDocument = 50000;
                using (StringReader input = new StringReader(xml))
                using (XmlReader reader = XmlReader.Create(input, settings)) document.Load(reader);
            }
            catch (Exception) { throw new ConsultantException("invalid_view_xml", "La arquitectura de la vista no es XML válido."); }
            string lower = xml.ToLowerInvariant();
            string[] prohibited = new[] { "<!doctype", "<!entity", "<script", "<button", "<attribute", "t-raw=", "t-call=", "type=\"object\"", "type='object'", "type=\"action\"", "type='action'", "position=\"replace\"", "position='replace'", "position=\"attributes\"", "position='attributes'", "<form action=" };
            foreach (string token in prohibited) if (lower.Contains(token)) throw new ConsultantException("unsafe_view_xml", "La vista contiene elementos ejecutables o no permitidos: " + token + ".");
            string root = document.DocumentElement == null ? string.Empty : document.DocumentElement.Name;
            if (root != "xpath" && root != "data" && root != "field") throw new ConsultantException("unsafe_view_root", "La vista heredada debe comenzar por xpath, data o field.");
        }

        private static string CapabilityHash(OdooClient client, ConnectionProfile profile, string model)
        {
            return JsonUtil.CanonicalHash(JsonUtil.Dictionary(
                "profile_uid", profile.uid,
                "target_model", model,
                "model_read", SafeAccess(client, model, "read"),
                "field_metadata_create", SafeAccess(client, "ir.model.fields", "create"),
                "view_metadata_create", SafeAccess(client, "ir.ui.view", "create")));
        }

        private static bool SafeAccess(OdooClient client, string model, string operation)
        {
            try { return client.CheckAccessRights(model, operation); }
            catch (ConsultantException) { return false; }
        }

        private static string PackageIntegrityHash(ChangeSet change)
        {
            List<object> operations = new List<object>();
            foreach (ChangeOperation operation in change.operations)
            {
                operations.Add(JsonUtil.Dictionary(
                    "operation_id", operation.operation_id,
                    "type", operation.type,
                    "model", operation.model,
                    "parameters", operation.parameters));
            }
            return JsonUtil.CanonicalHash(JsonUtil.Dictionary(
                "schema_version", change.schema_version,
                "id", change.id,
                "client_slug", change.client_slug,
                "environment", change.environment,
                "category", change.category,
                "risk", change.risk,
                "model", change.model,
                "created_at", change.created_at,
                "expires_at", change.expires_at,
                "precondition_hash", change.precondition_hash,
                "profile_fingerprint", change.profile_fingerprint,
                "policy_version", change.policy_version,
                "objective", change.objective,
                "contract_hash", change.contract_hash,
                "capability_hash", change.capability_hash,
                "impact", change.impact,
                "rollback_strategy", change.rollback_strategy,
                "preconditions", change.preconditions,
                "validations", change.validations,
                "operations", operations));
        }

        private static string SanitizeMarkdown(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string result = value.Replace("\r", " ").Replace("\n", " ").Trim();
            return result.Length > 2000 ? result.Substring(0, 2000) + "…" : result;
        }

        private static string SafeError(Exception error)
        {
            return Redactor.SafeError(error);
        }
    }
}
