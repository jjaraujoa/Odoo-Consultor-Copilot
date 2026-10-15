using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace OdooConsultor
{
    public sealed partial class PromotionService
    {
        private readonly ProjectStore project;

        public PromotionService(ProjectStore value)
        {
            project = value;
        }

        public object PreparePackage(string replicaSlug, string backupReference, IDictionary<string, object> references)
        {
            replicaSlug = (replicaSlug ?? string.Empty).Trim().ToLowerInvariant();
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                if (string.IsNullOrWhiteSpace(backupReference) || backupReference.Trim().Length < 5)
                {
                    throw new ConsultantException("backup_reference_required", "Se requiere una referencia o identificador de snapshot de respaldo válido para promover a producción (mínimo 5 caracteres).");
                }
                backupReference = backupReference.Trim();

                string replicaPath = project.ReplicaPath(replicaSlug);
                string manifestPath = Path.Combine(replicaPath, "manifest.json");
                if (!File.Exists(manifestPath)) throw new ConsultantException("manifest_not_found", "No existe manifest.json en la réplica " + replicaSlug + ".");
                Dictionary<string, object> manifest = JsonUtil.ReadJsonFile<Dictionary<string, object>>(manifestPath);

                // Zero Demo Assertion
                string serializedManifest = JsonUtil.Serialize(manifest);
                if (serializedManifest.IndexOf("odoo_ai_demo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    serializedManifest.IndexOf("synthetic_record", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    serializedManifest.IndexOf("demo_record", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    throw new ConsultantException("demo_data_prohibited", "Está estrictamente prohibido incluir o crear registros de ejemplo o de prueba en Producción.");
                }

                Dictionary<string, object> impact = (Dictionary<string, object>)AnalyzeImpact(replicaSlug);
                string impactStatus = Convert.ToString(impact["status"], CultureInfo.InvariantCulture);
                if (impactStatus == "conflicts_detected")
                {
                    throw new ConsultantException("promotion_blocked_conflicts", "No se puede promover el paquete: existen conflictos o colisiones en producción. " + Convert.ToString(impact["message"], CultureInfo.InvariantCulture));
                }
                if (impactStatus == "not_applied_in_staging")
                {
                    throw new ConsultantException("staging_not_verified", "La réplica debe estar 100% aplicada y verificada en Staging antes de promover a Producción. " + Convert.ToString(impact["message"], CultureInfo.InvariantCulture));
                }

                ReplicaService replicaService = new ReplicaService(project);
                ChangeSet change = replicaService.PreparePromotion(replicaSlug, backupReference, references);

                // Zero Demo Assertion on Operations
                foreach (ChangeOperation op in change.operations)
                {
                    if (op.type.StartsWith("demo_", StringComparison.Ordinal) ||
                        string.Equals(op.model, "odoo_ai_demo", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(op.model, "synthetic_record", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ConsultantException("demo_data_prohibited", "Está estrictamente prohibido incluir o crear registros de ejemplo o de prueba en Producción.");
                    }
                }

                int phase = (int)JsonUtil.GetLong(manifest, "phase", false);
                if (phase == 0) phase = (int)JsonUtil.GetLong(manifest, "schema_version", false);
                if (phase == 0) phase = 2;

                project.PromotionDirectory(replicaSlug, true);
                Dictionary<string, object> promotionData = JsonUtil.Dictionary(
                    "promotion_id", change.id,
                    "package_id", change.id,
                    "replica_slug", replicaSlug,
                    "phase", phase,
                    "status", "prepared",
                    "backup_reference", backupReference,
                    "created_at", change.created_at,
                    "expires_at", change.expires_at,
                    "staging_hash", impact.ContainsKey("staging_hash") ? impact["staging_hash"] : null,
                    "production_pre_hash", impact.ContainsKey("production_precondition_hash") ? impact["production_precondition_hash"] : null,
                    "operations_count", change.operations.Count);
                JsonUtil.WriteJsonFile(project.PromotionPath(replicaSlug), promotionData);

                AuditLog.Append(project, "production", "promocion_preparar_paquete", change.id, "success",
                    "Paquete de promoción a producción preparado; réplica=" + replicaSlug + "; operaciones=" + change.operations.Count.ToString(CultureInfo.InvariantCulture) + "; respaldo=" + backupReference + ".", timer.ElapsedMilliseconds);

                return JsonUtil.Dictionary(
                    "package_id", change.id,
                    "replica_slug", replicaSlug,
                    "environment", "production",
                    "status", "prepared",
                    "backup_reference", backupReference,
                    "created_at", change.created_at,
                    "expires_at", change.expires_at,
                    "operations_count", change.operations.Count,
                    "required_confirmation", "PROMOVER-PRODUCCION " + change.id + " RESPALDO-CONFIRMADO",
                    "warnings", change.warnings,
                    "operations", change.operations,
                    "next_step", "Ejecuta 'promocion_aplicar_paquete' con confirmation: 'PROMOVER-PRODUCCION " + change.id + " RESPALDO-CONFIRMADO' dentro de las 2 horas de vigencia.");
            }
            catch (Exception error)
            {
                AuditLog.Append(project, "production", "promocion_preparar_paquete", null, "error", SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }

        public object ApplyPackage(string packageId, string confirmation, IDictionary<string, object> references)
        {
            packageId = (packageId ?? string.Empty).Trim();
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                string path = project.ChangeSetPath(packageId);
                if (!File.Exists(path)) throw new ConsultantException("package_not_found", "No existe el paquete de cambio " + packageId + ".");
                ChangeSet change = JsonUtil.ReadJsonFile<ChangeSet>(path);

                if (!string.Equals(change.category, "production_promotion", StringComparison.Ordinal) || !string.Equals(change.environment, "production", StringComparison.Ordinal))
                {
                    throw new ConsultantException("not_a_promotion_package", "El paquete no es un paquete de promoción a producción.");
                }
                if (string.IsNullOrWhiteSpace(change.backup_reference))
                {
                    throw new ConsultantException("backup_reference_missing", "El paquete de promoción no tiene un respaldo registrado.");
                }

                string expectedConfirmation = "PROMOVER-PRODUCCION " + change.id + " RESPALDO-CONFIRMADO";
                if (!string.Equals(confirmation, expectedConfirmation, StringComparison.Ordinal))
                {
                    throw new ConsultantException("confirmation_mismatch", "La confirmación debe ser exactamente '" + expectedConfirmation + "'.");
                }

                DateTimeOffset expires;
                if (!DateTimeOffset.TryParse(change.expires_at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expires) || DateTimeOffset.UtcNow > expires)
                {
                    throw new ConsultantException("confirmation_expired", "La ventana de vigencia de 2 horas para promover a producción ha expirado. Prepara un nuevo paquete.");
                }

                ReplicaService replicaService = new ReplicaService(project);
                object applySummary = replicaService.Apply(change, confirmation, 0, references);

                // Reload change in case it was updated
                change = JsonUtil.ReadJsonFile<ChangeSet>(path);

                Dictionary<string, object> uiVisibility = null;
                if (change.status == "applied" || change.status == "verified")
                {
                    try
                    {
                        uiVisibility = EnsurePromotedFieldsVisible(change);
                    }
                    catch (Exception ex)
                    {
                        AuditLog.Append(project, "production", "promocion_aplicar_paquete_ui_sync_warn", change.id, "warning", SafeError(ex), 0);
                        uiVisibility = JsonUtil.Dictionary("status", "error_during_view_sync", "error", SafeError(ex));
                    }
                }

                object verifyResult = null;
                bool certified = false;
                string certPath = null;
                if (change.status == "applied" || change.status == "verified")
                {
                    try
                    {
                        verifyResult = VerifyProduction(change.replica_slug);
                        Dictionary<string, object> vDict = verifyResult as Dictionary<string, object>;
                        if (vDict != null && JsonUtil.GetBool(vDict, "certified", false))
                        {
                            certified = true;
                            certPath = JsonUtil.GetString(vDict, "certificate_path", false);
                        }
                    }
                    catch (Exception ex)
                    {
                        AuditLog.Append(project, "production", "promocion_aplicar_paquete_post_verify_warn", change.id, "warning", SafeError(ex), 0);
                    }
                }

                AuditLog.Append(project, "production", "promocion_aplicar_paquete", change.id, certified ? "success" : "warning",
                    "Paquete de promoción aplicado en producción; réplica=" + change.replica_slug + "; certificado=" + certified.ToString() + "; visibilidad_ui=" + (uiVisibility != null ? Convert.ToString(uiVisibility["status"], CultureInfo.InvariantCulture) : "omitida") + ".", timer.ElapsedMilliseconds);

                return JsonUtil.Dictionary(
                    "package_id", change.id,
                    "replica_slug", change.replica_slug,
                    "environment", "production",
                    "status", change.status,
                    "applied_at", change.applied_at,
                    "verified_at", change.verified_at,
                    "backup_reference", change.backup_reference,
                    "certified", certified,
                    "certificate_path", certPath,
                    "apply_summary", applySummary,
                    "ui_visibility", uiVisibility,
                    "verification_summary", verifyResult,
                    "next_step", certified
                        ? "Promoción aplicada y certificada al 100% en Producción con vistas de interfaz sincronizadas. Procede a generar la documentación formal de entrega con el skill 'odoo-documentar-personalizacion'."
                        : "El paquete fue procesado con estado '" + change.status + "'. Revisa el diagnóstico detallado.");
            }
            catch (Exception error)
            {
                AuditLog.Append(project, "production", "promocion_aplicar_paquete", packageId, "error", SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }

        public object ActivateExternalEffects(string promotionId, string confirmation)
        {
            promotionId = (promotionId ?? string.Empty).Trim();
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                ChangeSet change = null;
                if (File.Exists(project.ChangeSetPath(promotionId)))
                {
                    change = JsonUtil.ReadJsonFile<ChangeSet>(project.ChangeSetPath(promotionId));
                }
                else if (File.Exists(project.PromotionPath(promotionId)))
                {
                    Dictionary<string, object> promoData = JsonUtil.ReadJsonFile<Dictionary<string, object>>(project.PromotionPath(promotionId));
                    string pkgId = JsonUtil.GetString(promoData, "package_id", false) ?? JsonUtil.GetString(promoData, "promotion_id", false);
                    if (!string.IsNullOrEmpty(pkgId) && File.Exists(project.ChangeSetPath(pkgId)))
                    {
                        change = JsonUtil.ReadJsonFile<ChangeSet>(project.ChangeSetPath(pkgId));
                    }
                }
                if (change == null) throw new ConsultantException("promotion_not_found", "No se encontró el paquete o promoción " + promotionId + ".");

                string expectedConfirmation = "ACTIVAR-EFECTOS-PRODUCCION " + change.id;
                if (!string.Equals(confirmation, expectedConfirmation, StringComparison.Ordinal))
                {
                    throw new ConsultantException("confirmation_mismatch", "La confirmación debe ser exactamente '" + expectedConfirmation + "'.");
                }

                if (change.status != "applied" && change.status != "verified")
                {
                    throw new ConsultantException("package_not_applied", "El paquete de promoción debe estar aplicado antes de activar efectos externos.");
                }

                if (!string.Equals(change.policy_version, ReplicaPhase3Service.PolicyVersion, StringComparison.Ordinal))
                {
                    return JsonUtil.Dictionary("status", "no_external_effects", "message", "Este paquete no contiene automatizaciones ni efectos externos que requieran compuerta de activación separada.");
                }

                ReplicaPhase3Service phase3 = new ReplicaPhase3Service(project);
                object result = phase3.ActivateExternal(change.id, confirmation, "PRUEBA-MANUAL " + change.id);

                AuditLog.Append(project, "production", "promocion_activar_efectos_externos", change.id, "success",
                    "Efectos externos activados en producción para " + change.replica_slug + ".", timer.ElapsedMilliseconds);

                return result;
            }
            catch (Exception error)
            {
                AuditLog.Append(project, "production", "promocion_activar_efectos_externos", promotionId, "error", SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }

        private OdooClient Open(string environment)
        {
            return project.OpenClient(environment, 30000);
        }

        private static string SafeError(Exception error)
        {
            return Redactor.SafeError(error);
        }
    }
}
