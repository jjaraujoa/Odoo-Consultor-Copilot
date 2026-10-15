using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace OdooConsultor
{
    public sealed partial class PromotionService
    {
        public object PrepareRunbook(string replicaSlug)
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

                string promotionDir = project.PromotionDirectory(replicaSlug, true);
                string promotionId = "prom-" + replicaSlug + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

                Dictionary<string, object> impact = null;
                try
                {
                    impact = (Dictionary<string, object>)AnalyzeImpact(replicaSlug);
                    string impactStatus = Convert.ToString(impact["status"], CultureInfo.InvariantCulture);
                    if (impactStatus == "conflicts_detected")
                    {
                        throw new ConsultantException("promotion_blocked_conflicts", "No se puede generar el runbook: se detectaron conflictos en producción. " + Convert.ToString(impact["message"], CultureInfo.InvariantCulture));
                    }
                }
                catch (ConsultantException ex)
                {
                    if (ex.Code == "promotion_blocked_conflicts" || ex.Code == "manifest_not_found") throw;
                    // Si no hay perfil o credencial de conexión, se continúa con el manifiesto local validado
                }
                catch (Exception)
                {
                    // Si no hay conexión activa para el análisis de impacto online, se continúa con el manifiesto local validado
                }

                StringBuilder markdown = new StringBuilder();
                markdown.AppendLine("# Runbook de Promoción a Producción: " + replicaSlug);
                markdown.AppendLine();
                markdown.AppendLine("> [!IMPORTANT]");
                markdown.AppendLine("> **Entorno Objetivo:** Producción (`production`) — Acceso estrictamente manual.");
                markdown.AppendLine("> La plataforma y el copiloto funcional mantienen producción en modo **solo lectura**.");
                markdown.AppendLine("> Siga los pasos numerados en orden estricto y luego ejecute `promocion_verificar_produccion` para certificar el despliegue.");
                markdown.AppendLine();
                markdown.AppendLine("## 1. Metadatos de la Promoción");
                markdown.AppendLine();
                markdown.AppendLine("- **ID de Promoción:** `" + promotionId + "`");
                markdown.AppendLine("- **Réplica Origen:** `replicas/" + replicaSlug + "/`");
                markdown.AppendLine("- **Fase Studio:** Fase " + phase.ToString(CultureInfo.InvariantCulture));
                markdown.AppendLine("- **Fecha de Preparación:** `" + DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC`");
                markdown.AppendLine("- **Hash Manifiesto Staging:** `" + (impact != null && impact.ContainsKey("staging_hash") && impact["staging_hash"] != null ? Convert.ToString(impact["staging_hash"], CultureInfo.InvariantCulture) : JsonUtil.Sha256(JsonUtil.CanonicalHash(manifest))) + "`");
                markdown.AppendLine();
                markdown.AppendLine("> [!WARNING]");
                markdown.AppendLine("> **Respaldo Previo Recomendado:**");
                markdown.AppendLine("> Antes de realizar cualquier modificación estructural o técnica en la base de datos de producción, asegúrese de contar con un respaldo completo reciente de Odoo (o un snapshot en Odoo.sh).");
                markdown.AppendLine();
                markdown.AppendLine("---");
                markdown.AppendLine();
                markdown.AppendLine("## 2. Guía de Despliegue Manual Paso a Paso");
                markdown.AppendLine();

                int stepNumber = 1;

                // 1. Modelos
                IList rawModels = manifest.ContainsKey("models") ? manifest["models"] as IList : null;
                if (rawModels != null && rawModels.Count > 0)
                {
                    markdown.AppendLine("### Bloque 1: Modelos Personalizados");
                    markdown.AppendLine();
                    foreach (object item in rawModels)
                    {
                        Dictionary<string, object> model = item as Dictionary<string, object>;
                        if (model == null) continue;
                        string key = JsonUtil.GetString(model, "key", false) ?? JsonUtil.GetString(model, "model", true);
                        string modelName = JsonUtil.GetString(model, "model", true);
                        string name = JsonUtil.GetString(model, "name", true);

                        markdown.AppendLine("#### Paso " + (stepNumber++).ToString(CultureInfo.InvariantCulture) + ": Crear Modelo `" + modelName + "` (" + name + ")");
                        markdown.AppendLine();
                        markdown.AppendLine("**Modalidad A — Interfaz Studio:**");
                        markdown.AppendLine("1. Abrir Odoo Studio (icono de lápiz superior derecho).");
                        markdown.AppendLine("2. En la pantalla principal de Studio, hacer clic en **Nueva App** o seleccionar el módulo correspondiente.");
                        markdown.AppendLine("3. Crear un nuevo modelo asignando el nombre: **" + name + "**.");
                        markdown.AppendLine();
                        markdown.AppendLine("**Modalidad B — Ajustes Técnicos de Odoo:**");
                        markdown.AppendLine("1. Navegar a **Ajustes > Técnico > Estructura de la base de datos > Modelos**.");
                        markdown.AppendLine("2. Hacer clic en **Nuevo** e ingresar:");
                        markdown.AppendLine("   - **Descripción del modelo:** `" + name + "`");
                        markdown.AppendLine("   - **Modelo (`model`):** `" + modelName + "`");
                        markdown.AppendLine("3. Guardar el registro.");
                        markdown.AppendLine();
                    }
                }

                // 2. Campos
                IList rawFields = manifest.ContainsKey("fields") ? manifest["fields"] as IList : null;
                if (rawFields != null && rawFields.Count > 0)
                {
                    markdown.AppendLine("### Bloque 2: Campos de Datos");
                    markdown.AppendLine();
                    foreach (object item in rawFields)
                    {
                        Dictionary<string, object> field = item as Dictionary<string, object>;
                        if (field == null) continue;
                        string model = JsonUtil.GetString(field, "model", true);
                        string name = JsonUtil.GetString(field, "name", true);
                        string key = JsonUtil.GetString(field, "key", false) ?? (model + "." + name);
                        string label = JsonUtil.GetString(field, "label", false) ?? JsonUtil.GetString(field, "field_description", false) ?? name;
                        string type = JsonUtil.GetString(field, "type", false) ?? JsonUtil.GetString(field, "ttype", true);
                        bool required = JsonUtil.GetBool(field, "required", false);
                        bool readonlyField = JsonUtil.GetBool(field, "readonly", false);
                        bool stored = JsonUtil.GetBool(field, "stored", true);

                        markdown.AppendLine("#### Paso " + (stepNumber++).ToString(CultureInfo.InvariantCulture) + ": Crear/Verificar Campo `" + name + "` en `" + model + "`");
                        markdown.AppendLine();
                        markdown.AppendLine("- **Modelo:** `" + model + "`");
                        markdown.AppendLine("- **Nombre Técnico:** `" + name + "`");
                        markdown.AppendLine("- **Etiqueta (`field_description`):** `" + label + "`");
                        markdown.AppendLine("- **Tipo de Dato:** `" + type + "`");
                        markdown.AppendLine("- **Requerido:** `" + (required ? "Sí" : "No") + "` | **Solo Lectura:** `" + (readonlyField ? "Sí" : "No") + "` | **Almacenado:** `" + (stored ? "Sí" : "No") + "`");

                        if (type == "selection" && field.ContainsKey("selection"))
                        {
                            IList options = field["selection"] as IList;
                            if (options != null)
                            {
                                markdown.AppendLine("- **Valores de Selección Técnica:**");
                                foreach (object opt in options)
                                {
                                    Dictionary<string, object> o = opt as Dictionary<string, object>;
                                    if (o != null) markdown.AppendLine("  - Clave: `" + Convert.ToString(o["value"], CultureInfo.InvariantCulture) + "` — Etiqueta: `" + Convert.ToString(o["label"], CultureInfo.InvariantCulture) + "`");
                                }
                            }
                        }

                        string computeFile = JsonUtil.GetString(field, "compute_file", false);
                        if (!string.IsNullOrWhiteSpace(computeFile))
                        {
                            string code = ReadReplicaFile(replicaPath, computeFile);
                            markdown.AppendLine();
                            markdown.AppendLine("**Código Python Calculado (`compute`):**");
                            markdown.AppendLine("```python");
                            markdown.AppendLine(code.Trim());
                            markdown.AppendLine("```");
                        }

                        markdown.AppendLine();
                        markdown.AppendLine("**Modalidad A — Interfaz Studio:**");
                        markdown.AppendLine("1. En Studio, abrir la vista formulario del modelo `" + model + "`.");
                        markdown.AppendLine("2. En la pestaña **Añadir**, arrastrar un campo de tipo **" + type + "**.");
                        markdown.AppendLine("3. En las propiedades de la barra izquierda, fijar la etiqueta en **" + label + "** y el nombre técnico en **" + name + "**.");
                        markdown.AppendLine();
                        markdown.AppendLine("**Modalidad B — Ajustes Técnicos de Odoo:**");
                        markdown.AppendLine("1. Navegar a **Ajustes > Técnico > Estructura de la base de datos > Campos**.");
                        markdown.AppendLine("2. Hacer clic en **Nuevo** y completar los atributos indicados arriba.");
                        markdown.AppendLine("3. Guardar el registro.");
                        markdown.AppendLine();
                    }
                }

                // 3. Vistas
                IList rawViews = manifest.ContainsKey("views") ? manifest["views"] as IList : null;
                if (rawViews != null && rawViews.Count > 0)
                {
                    markdown.AppendLine("### Bloque 3: Vistas XML");
                    markdown.AppendLine();
                    foreach (object item in rawViews)
                    {
                        Dictionary<string, object> view = item as Dictionary<string, object>;
                        if (view == null) continue;
                        string key = JsonUtil.GetString(view, "key", false) ?? JsonUtil.GetString(view, "name", false) ?? "view";
                        string model = JsonUtil.GetString(view, "model", true);
                        string type = JsonUtil.GetString(view, "type", true);
                        string arch = LoadArchText(replicaPath, view);
                        string inherit = JsonUtil.GetString(view, "inherit_view_name", false);

                        markdown.AppendLine("#### Paso " + (stepNumber++).ToString(CultureInfo.InvariantCulture) + ": Configurar Vista XML `" + key + "` en `" + model + "`");
                        markdown.AppendLine();
                        markdown.AppendLine("- **Modelo:** `" + model + "`");
                        markdown.AppendLine("- **Tipo de Vista:** `" + type + "`");
                        if (!string.IsNullOrWhiteSpace(inherit)) markdown.AppendLine("- **Hereda de:** `" + inherit + "`");
                        markdown.AppendLine();
                        markdown.AppendLine("**Arquitectura XML Limpia (Entidades Numéricas 7-bit):**");
                        markdown.AppendLine("```xml");
                        markdown.AppendLine(JsonUtil.XmlNumericEntities(arch).Trim());
                        markdown.AppendLine("```");
                        markdown.AppendLine();
                        markdown.AppendLine("**Ruta de Aplicación en Ajustes Técnicos:**");
                        markdown.AppendLine("1. Ir a **Ajustes > Técnico > Interfaz de usuario > Vistas**.");
                        markdown.AppendLine("2. Buscar la vista o crear una nueva para `" + model + "`.");
                        markdown.AppendLine("3. Pegar el bloque XML anterior en el campo **Arquitectura (`arch`)**.");
                        markdown.AppendLine("4. Guardar y verificar que la sintaxis XML sea aceptada sin errores.");
                        markdown.AppendLine();
                    }
                }

                // 4. Acciones y Automatizaciones
                IList rawActions = manifest.ContainsKey("server_actions") ? manifest["server_actions"] as IList : null;
                if (rawActions != null && rawActions.Count > 0)
                {
                    markdown.AppendLine("### Bloque 4: Acciones de Servidor y Código Python");
                    markdown.AppendLine();
                    foreach (object item in rawActions)
                    {
                        Dictionary<string, object> action = item as Dictionary<string, object>;
                        if (action == null) continue;
                        string key = JsonUtil.GetString(action, "key", false) ?? JsonUtil.GetString(action, "name", true);
                        string name = JsonUtil.GetString(action, "name", true);
                        string model = JsonUtil.GetString(action, "model", true);
                        string sourceFile = JsonUtil.GetString(action, "source_file", false);
                        string code = !string.IsNullOrWhiteSpace(sourceFile) ? ReadReplicaFile(replicaPath, sourceFile) : string.Empty;

                        markdown.AppendLine("#### Paso " + (stepNumber++).ToString(CultureInfo.InvariantCulture) + ": Configurar Acción de Servidor `" + name + "`");
                        markdown.AppendLine();
                        markdown.AppendLine("- **Nombre:** `" + name + "`");
                        markdown.AppendLine("- **Modelo:** `" + model + "`");
                        markdown.AppendLine("- **Acción a Realizar:** `Ejecutar código Python`");
                        if (!string.IsNullOrWhiteSpace(code))
                        {
                            markdown.AppendLine();
                            markdown.AppendLine("**Código Python:**");
                            markdown.AppendLine("```python");
                            markdown.AppendLine(code.Trim());
                            markdown.AppendLine("```");
                        }
                        markdown.AppendLine();
                        markdown.AppendLine("1. Ir a **Ajustes > Técnico > Acciones > Acciones de servidor**.");
                        markdown.AppendLine("2. Crear o actualizar el registro con los datos anteriores.");
                        markdown.AppendLine("3. Guardar.");
                        markdown.AppendLine();
                    }
                }

                IList rawAutomations = manifest.ContainsKey("automations") ? manifest["automations"] as IList : null;
                if (rawAutomations != null && rawAutomations.Count > 0)
                {
                    markdown.AppendLine("### Bloque 5: Reglas de Automatización");
                    markdown.AppendLine();
                    foreach (object item in rawAutomations)
                    {
                        Dictionary<string, object> auto = item as Dictionary<string, object>;
                        if (auto == null) continue;
                        string name = JsonUtil.GetString(auto, "name", true);
                        string model = JsonUtil.GetString(auto, "model", true);
                        string trigger = JsonUtil.GetString(auto, "trigger", true);

                        markdown.AppendLine("#### Paso " + (stepNumber++).ToString(CultureInfo.InvariantCulture) + ": Configurar Automatización `" + name + "`");
                        markdown.AppendLine();
                        markdown.AppendLine("- **Nombre:** `" + name + "`");
                        markdown.AppendLine("- **Modelo:** `" + model + "`");
                        markdown.AppendLine("- **Disparador:** `" + trigger + "`");
                        markdown.AppendLine("1. Ir a **Ajustes > Técnico > Automatización > Acciones automáticas**.");
                        markdown.AppendLine("2. Crear la regla vinculándola a su acción correspondiente.");
                        markdown.AppendLine("3. Dejarla activa tras verificar su funcionamiento.");
                        markdown.AppendLine();
                    }
                }

                // 5. QWeb y Reportes
                IList rawQweb = manifest.ContainsKey("qweb_views") ? manifest["qweb_views"] as IList : null;
                if (rawQweb != null && rawQweb.Count > 0)
                {
                    markdown.AppendLine("### Bloque 6: Reportes y Plantillas QWeb");
                    markdown.AppendLine();
                    foreach (object item in rawQweb)
                    {
                        Dictionary<string, object> qweb = item as Dictionary<string, object>;
                        if (qweb == null) continue;
                        string key = JsonUtil.GetString(qweb, "key", false) ?? JsonUtil.GetString(qweb, "template_name", true);
                        string name = JsonUtil.GetString(qweb, "name", true);
                        string templateName = JsonUtil.GetString(qweb, "template_name", true);
                        string arch = LoadArchText(replicaPath, qweb);

                        markdown.AppendLine("#### Paso " + (stepNumber++).ToString(CultureInfo.InvariantCulture) + ": Plantilla QWeb `" + templateName + "`");
                        markdown.AppendLine();
                        markdown.AppendLine("- **Nombre:** `" + name + "`");
                        markdown.AppendLine("- **Clave de Plantilla:** `" + templateName + "`");
                        markdown.AppendLine();
                        markdown.AppendLine("```xml");
                        markdown.AppendLine(JsonUtil.XmlNumericEntities(arch).Trim());
                        markdown.AppendLine("```");
                        markdown.AppendLine();
                    }
                }

                // Plan de contingencia
                markdown.AppendLine("---");
                markdown.AppendLine();
                markdown.AppendLine("## 3. Plan de Contingencia y Reversión Segura (Rollback)");
                markdown.AppendLine();
                markdown.AppendLine("En caso de presentarse errores de interfaz o inconsistencias durante la aplicación:");
                markdown.AppendLine("1. **Vistas:** Desmarcar la casilla `Activo` en la vista agregada (`ir.ui.view`), para que Odoo restaure inmediatamente la vista estándar sin borrar registros.");
                markdown.AppendLine("2. **Automatizaciones:** Desactivar la regla en `Ajustes > Técnico > Acciones automáticas`.");
                markdown.AppendLine("3. **Campos:** **NUNCA** eliminar (`unlink`) campos que ya contengan datos. Si un campo no debe visualizarse, desactive la vista que lo contiene o márquelo como no visible.");
                markdown.AppendLine("4. Si el problema persiste, restaurar el respaldo previo realizado en el paso preliminar.");
                markdown.AppendLine();
                markdown.AppendLine("---");
                markdown.AppendLine();
                markdown.AppendLine("## 4. Certificación y Cierre");
                markdown.AppendLine();
                markdown.AppendLine("Una vez finalizada la aplicación manual en Producción, ejecute en Codex:");
                markdown.AppendLine("```text");
                markdown.AppendLine("promocion_verificar_produccion con replica_slug: \"" + replicaSlug + "\"");
                markdown.AppendLine("```");
                markdown.AppendLine("El copiloto auditará la base de Producción en modo solo lectura y generará el **Certificado de Trazabilidad Criptográfico** definitivo.");

                string runbookPath = project.PromotionRunbookPath(replicaSlug);
                File.WriteAllText(runbookPath, markdown.ToString(), new UTF8Encoding(false));

                // Guardar promocion.json
                Dictionary<string, object> promotionData = JsonUtil.Dictionary(
                    "promotion_id", promotionId,
                    "replica_slug", replicaSlug,
                    "phase", phase,
                    "status", "prepared",
                    "created_at", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    "staging_hash", impact != null && impact.ContainsKey("staging_hash") ? impact["staging_hash"] : JsonUtil.Sha256(JsonUtil.CanonicalHash(manifest)),
                    "production_pre_hash", impact != null && impact.ContainsKey("production_precondition_hash") ? impact["production_precondition_hash"] : null,
                    "runbook_path", Path.Combine("promociones", replicaSlug, "runbook.md"),
                    "components_count", impact != null && impact.ContainsKey("counts") ? ((Dictionary<string, object>)impact["counts"])["total"] : (manifest.ContainsKey("fields") ? ((IList)manifest["fields"]).Count : 0));

                JsonUtil.WriteJsonFile(project.PromotionPath(replicaSlug), promotionData);

                AuditLog.Append(project, "production", "promocion_preparar_runbook", promotionId, "success",
                    "Runbook de promoción preparado para réplica " + replicaSlug + "; id=" + promotionId + ".", timer.ElapsedMilliseconds);

                return JsonUtil.Dictionary(
                    "promotion_id", promotionId,
                    "replica_slug", replicaSlug,
                    "status", "prepared",
                    "runbook_path", Path.Combine("promociones", replicaSlug, "runbook.md"),
                    "next_step", "Revisa el runbook generado en " + Path.Combine("promociones", replicaSlug, "runbook.md") + ", ejecuta los pasos en Producción y confirma con promocion_verificar_produccion.");
            }
            catch (Exception error)
            {
                AuditLog.Append(project, "production", "promocion_preparar_runbook", null, "error", SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }
    }
}
