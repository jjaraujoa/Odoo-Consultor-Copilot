using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace OdooConsultor
{
    public sealed partial class PromotionService
    {
        public object AnalyzeImpact(string replicaSlug)
        {
            replicaSlug = (replicaSlug ?? string.Empty).Trim().ToLowerInvariant();
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                string replicaPath = project.ReplicaPath(replicaSlug);
                string manifestPath = Path.Combine(replicaPath, "manifest.json");
                if (!File.Exists(manifestPath)) throw new ConsultantException("manifest_not_found", "No existe manifest.json en la réplica " + replicaSlug + ".");
                Dictionary<string, object> manifest = JsonUtil.ReadJsonFile<Dictionary<string, object>>(manifestPath);
                int phase = (int)JsonUtil.GetLong(manifest, "phase", false);
                if (phase == 0) phase = (int)JsonUtil.GetLong(manifest, "schema_version", false);
                if (phase == 0) phase = 1;

                ReplicaService replicaService = new ReplicaService(project);
                Dictionary<string, object> stagingAnalysis = (Dictionary<string, object>)replicaService.Analyze("staging", replicaSlug);
                Dictionary<string, object> productionAnalysis = (Dictionary<string, object>)replicaService.Analyze("production", replicaSlug);

                IList stagingComponents = stagingAnalysis.ContainsKey("components") ? stagingAnalysis["components"] as IList : null;
                IList productionComponents = productionAnalysis.ContainsKey("components") ? productionAnalysis["components"] as IList : null;

                bool appliedInStaging = true;
                List<string> stagingUnapplied = new List<string>();
                if (stagingComponents != null)
                {
                    foreach (object item in stagingComponents)
                    {
                        Dictionary<string, object> comp = item as Dictionary<string, object>;
                        if (comp == null) continue;
                        string classification = Convert.ToString(comp["classification"], CultureInfo.InvariantCulture);
                        string key = Convert.ToString(comp["key"], CultureInfo.InvariantCulture);
                        if (classification != "compatible" && classification != "modifiable")
                        {
                            appliedInStaging = false;
                            stagingUnapplied.Add(key + " (" + classification + ")");
                        }
                    }
                }

                List<Dictionary<string, object>> evaluatedComponents = new List<Dictionary<string, object>>();
                int missingInProduction = 0;
                int identicalInProduction = 0;
                int conflictsInProduction = 0;
                List<string> productionConflicts = new List<string>();

                if (productionComponents != null)
                {
                    foreach (object item in productionComponents)
                    {
                        Dictionary<string, object> comp = item as Dictionary<string, object>;
                        if (comp == null) continue;
                        string key = Convert.ToString(comp["key"], CultureInfo.InvariantCulture);
                        string kind = Convert.ToString(comp["kind"], CultureInfo.InvariantCulture);
                        string classification = Convert.ToString(comp["classification"], CultureInfo.InvariantCulture);
                        string reason = comp.ContainsKey("reason") ? Convert.ToString(comp["reason"], CultureInfo.InvariantCulture) : null;

                        string promotionStatus;
                        if (classification == "compatible")
                        {
                            promotionStatus = "present_identical";
                            identicalInProduction++;
                        }
                        else if (classification == "absent")
                        {
                            promotionStatus = "missing_in_production";
                            missingInProduction++;
                        }
                        else
                        {
                            promotionStatus = "conflict_in_production";
                            conflictsInProduction++;
                            productionConflicts.Add(key + " (" + (reason ?? classification) + ")");
                        }

                        evaluatedComponents.Add(JsonUtil.Dictionary(
                            "key", key,
                            "kind", kind,
                            "staging_state", appliedInStaging ? "verified_in_staging" : "unverified",
                            "production_state", promotionStatus,
                            "classification", classification,
                            "reason", reason));
                    }
                }

                string overallStatus;
                string readinessMessage;
                if (conflictsInProduction > 0)
                {
                    overallStatus = "conflicts_detected";
                    readinessMessage = "Existen colisiones o incompatibilidades en Producción que impiden la promoción directa: " + string.Join("; ", productionConflicts.ToArray());
                }
                else if (!appliedInStaging)
                {
                    overallStatus = "not_applied_in_staging";
                    readinessMessage = "La réplica no está completamente aplicada o verificada en Staging: " + string.Join("; ", stagingUnapplied.ToArray()) + ". Aplica la réplica en Staging antes de promoverla.";
                }
                else if (missingInProduction == 0 && conflictsInProduction == 0)
                {
                    overallStatus = "already_promoted";
                    readinessMessage = "Todos los componentes de la réplica ya están presentes e idénticos en Producción.";
                }
                else
                {
                    overallStatus = "ready_for_promotion";
                    readinessMessage = "La réplica está probada en Staging y lista para promocionar. " + missingInProduction.ToString(CultureInfo.InvariantCulture) + " componente(s) pendientes de aplicar en Producción.";
                }

                List<string> stagingViewsDetected = new List<string>();
                try
                {
                    ConnectionProfile stgProf = project.GetProfile("staging", false);
                    if (stgProf != null && CredentialStore.Exists(stgProf.credential_target))
                    {
                        IList manifestFields = manifest.ContainsKey("fields") ? manifest["fields"] as IList : null;
                        if (manifestFields != null && manifestFields.Count > 0)
                        {
                            HashSet<string> customFieldNames = new HashSet<string>(StringComparer.Ordinal);
                            HashSet<string> targetModels = new HashSet<string>(StringComparer.Ordinal);
                            foreach (object item in manifestFields)
                            {
                                Dictionary<string, object> f = item as Dictionary<string, object>;
                                if (f == null) continue;
                                string fn = JsonUtil.GetString(f, "name", false);
                                string fm = JsonUtil.GetString(f, "model", false);
                                if (!string.IsNullOrWhiteSpace(fn) && fn.StartsWith("x_", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(fm))
                                {
                                    customFieldNames.Add(fn);
                                    targetModels.Add(fm);
                                }
                            }

                            if (customFieldNames.Count > 0)
                            {
                                using (OdooClient clientStg = Open("staging"))
                                {
                                    foreach (string tm in targetModels)
                                    {
                                        IList<Dictionary<string, object>> sViews = clientStg.SearchRead(
                                            "ir.ui.view",
                                            new object[] {
                                                new object[] { "model", "=", tm },
                                                new object[] { "active", "=", true }
                                            },
                                            new[] { "id", "name", "arch_db" },
                                            50,
                                            null
                                        );
                                        foreach (Dictionary<string, object> sv in sViews)
                                        {
                                            string arch = sv.ContainsKey("arch_db") && sv["arch_db"] != null ? Convert.ToString(sv["arch_db"], CultureInfo.InvariantCulture) : string.Empty;
                                            string vn = sv.ContainsKey("name") && sv["name"] != null ? Convert.ToString(sv["name"], CultureInfo.InvariantCulture) : string.Empty;
                                            foreach (string fn in customFieldNames)
                                            {
                                                if (arch.IndexOf(fn, StringComparison.Ordinal) >= 0)
                                                {
                                                    if (!stagingViewsDetected.Contains(vn)) stagingViewsDetected.Add(vn);
                                                    break;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }

                AuditLog.Append(project, "production", "promocion_analizar_impacto", null, overallStatus == "conflicts_detected" ? "blocked" : "success",
                    "Impacto de promoción analizado para " + replicaSlug + "; estado=" + overallStatus + "; pendientes=" + missingInProduction.ToString(CultureInfo.InvariantCulture) + ".", timer.ElapsedMilliseconds);

                return JsonUtil.Dictionary(
                    "replica_slug", replicaSlug,
                    "phase", phase,
                    "status", overallStatus,
                    "ready_for_promotion", overallStatus == "ready_for_promotion" || overallStatus == "already_promoted",
                    "message", readinessMessage,
                    "counts", JsonUtil.Dictionary(
                        "total", evaluatedComponents.Count,
                        "missing_in_production", missingInProduction,
                        "present_identical", identicalInProduction,
                        "conflicts_in_production", conflictsInProduction),
                    "staging_hash", stagingAnalysis.ContainsKey("manifest_hash") ? stagingAnalysis["manifest_hash"] : null,
                    "production_precondition_hash", productionAnalysis.ContainsKey("precondition_hash") ? productionAnalysis["precondition_hash"] : null,
                    "components", evaluatedComponents,
                    "ui_views_detected_in_staging", stagingViewsDetected,
                    "ui_visibility_guarantee", stagingViewsDetected.Count > 0
                        ? "Se detectaron " + stagingViewsDetected.Count.ToString(CultureInfo.InvariantCulture) + " vistas en Staging que extienden estos campos en interfaz. promocion_aplicar_paquete las sincronizará automáticamente para garantizar visibilidad en Producción."
                        : "No se detectaron vistas dependientes en Staging o los componentes no son campos visuales.",
                    "next_step", overallStatus == "ready_for_promotion"
                        ? "Ejecuta promocion_preparar_runbook con replica_slug '" + replicaSlug + "' para generar la guía de despliegue manual."
                        : (overallStatus == "not_applied_in_staging" ? "Aplica y verifica la réplica en staging primero con replica_preparar_paquete y studio_aplicar_paquete." : "Revisa las colisiones reportadas en producción antes de continuar."));
            }
            catch (Exception error)
            {
                AuditLog.Append(project, "production", "promocion_analizar_impacto", null, "error", SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }

        public object VerifyProduction(string replicaSlug)
        {
            replicaSlug = (replicaSlug ?? string.Empty).Trim().ToLowerInvariant();
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                string promotionPath = project.PromotionPath(replicaSlug);
                if (!File.Exists(promotionPath)) throw new ConsultantException("promotion_not_prepared", "No existe un paquete de promoción preparado para " + replicaSlug + ". Ejecuta primero promocion_preparar_runbook.");
                Dictionary<string, object> promotionData = JsonUtil.ReadJsonFile<Dictionary<string, object>>(promotionPath);
                string promotionId = JsonUtil.GetString(promotionData, "promotion_id", true);

                ReplicaService replicaService = new ReplicaService(project);
                Dictionary<string, object> prodAnalysis = (Dictionary<string, object>)replicaService.Analyze("production", replicaSlug);
                IList prodComponents = prodAnalysis.ContainsKey("components") ? prodAnalysis["components"] as IList : null;

                List<Dictionary<string, object>> verified = new List<Dictionary<string, object>>();
                List<Dictionary<string, object>> missing = new List<Dictionary<string, object>>();
                List<Dictionary<string, object>> mismatch = new List<Dictionary<string, object>>();

                if (prodComponents != null)
                {
                    foreach (object item in prodComponents)
                    {
                        Dictionary<string, object> comp = item as Dictionary<string, object>;
                        if (comp == null) continue;
                        string key = Convert.ToString(comp["key"], CultureInfo.InvariantCulture);
                        string kind = Convert.ToString(comp["kind"], CultureInfo.InvariantCulture);
                        string classification = Convert.ToString(comp["classification"], CultureInfo.InvariantCulture);
                        string reason = comp.ContainsKey("reason") ? Convert.ToString(comp["reason"], CultureInfo.InvariantCulture) : null;

                        if (classification == "compatible")
                        {
                            verified.Add(JsonUtil.Dictionary("key", key, "kind", kind, "status", "verified"));
                        }
                        else if (classification == "absent")
                        {
                            missing.Add(JsonUtil.Dictionary("key", key, "kind", kind, "status", "missing", "reason", "El componente aún no ha sido creado en Producción."));
                        }
                        else
                        {
                            mismatch.Add(JsonUtil.Dictionary("key", key, "kind", kind, "status", "mismatch", "reason", reason ?? classification));
                        }
                    }
                }

                bool fullyVerified = (missing.Count == 0 && mismatch.Count == 0);
                string resultStatus = fullyVerified ? "promoted_verified" : "mismatch_detected";
                DateTimeOffset now = DateTimeOffset.UtcNow;

                promotionData["status"] = resultStatus;
                promotionData["last_verified_at"] = now.ToString("o", CultureInfo.InvariantCulture);
                promotionData["verified_components"] = verified.Count;
                promotionData["missing_components"] = missing.Count;
                promotionData["mismatch_components"] = mismatch.Count;

                string certificateId = null;
                string certificatePath = null;

                if (fullyVerified)
                {
                    certificateId = "cert-" + Guid.NewGuid().ToString("N");
                    string certPath = project.PromotionCertificatePath(replicaSlug);
                    certificatePath = Path.Combine("promociones", replicaSlug, "certificado_trazabilidad.json");

                    Dictionary<string, object> certificate = JsonUtil.Dictionary(
                        "schema_version", 1,
                        "certificate_id", certificateId,
                        "promotion_id", promotionId,
                        "replica_slug", replicaSlug,
                        "environment", "production",
                        "certified_at", now.ToString("o", CultureInfo.InvariantCulture),
                        "status", "certified_100_percent",
                        "component_count", verified.Count,
                        "staging_hash", promotionData["staging_hash"],
                        "production_hash", prodAnalysis.ContainsKey("precondition_hash") ? prodAnalysis["precondition_hash"] : null,
                        "verified_components", verified);

                    string certHash = JsonUtil.CanonicalHash(certificate);
                    certificate["signature_hash"] = certHash;
                    JsonUtil.WriteJsonFile(certPath, certificate);
                    promotionData["certificate_id"] = certificateId;
                    promotionData["certificate_hash"] = certHash;
                }

                JsonUtil.WriteJsonFile(promotionPath, promotionData);

                AuditLog.Append(project, "production", "promocion_verificar_produccion", promotionId, fullyVerified ? "success" : "mismatch",
                    "Verificación de producción para " + replicaSlug + "; estado=" + resultStatus + "; verificados=" + verified.Count.ToString(CultureInfo.InvariantCulture) + "; faltantes=" + missing.Count.ToString(CultureInfo.InvariantCulture) + "; discrepancias=" + mismatch.Count.ToString(CultureInfo.InvariantCulture) + ".", timer.ElapsedMilliseconds);

                return JsonUtil.Dictionary(
                    "promotion_id", promotionId,
                    "replica_slug", replicaSlug,
                    "status", resultStatus,
                    "certified", fullyVerified,
                    "certificate_id", certificateId,
                    "certificate_path", certificatePath,
                    "counts", JsonUtil.Dictionary(
                        "verified", verified.Count,
                        "missing", missing.Count,
                        "mismatch", mismatch.Count),
                    "missing", missing,
                    "mismatch", mismatch,
                    "next_step", fullyVerified
                        ? "Promoción a Producción certificada exitosamente al 100%. Consulta el certificado en " + certificatePath + "."
                        : "Existen componentes pendientes o con diferencias. Corrige los elementos en Producción siguiendo el runbook y reejecuta promocion_verificar_produccion.");
            }
            catch (Exception error)
            {
                AuditLog.Append(project, "production", "promocion_verificar_produccion", null, "error", SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }
    }
}
