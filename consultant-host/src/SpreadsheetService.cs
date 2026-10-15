using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OdooConsultor
{
    public sealed class SpreadsheetService
    {
        private static readonly Regex LinkPattern = new Regex(@"\[(?<text>[^\]]*)\]\(<?(?<url>odoo://view/[^>)]+)>?\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex BareLinkPattern = new Regex(@"(?<url>odoo://view/[^\s\)""'>]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MojibakePattern = new Regex(@"(?:Ã[³¡éíñÚ‘“•\u0080-\u00bf]|Â[°¡¿])", RegexOptions.Compiled);
        private static readonly Regex TechnicalLabelPattern = new Regex(@"\((?:draft|done|cancel|waiting|confirmed|assigned|on_track|at_risk|off_track|on_hold|in_progress|to_approve|closed|open|new|won|lost|[a-z0-9_]{3,25})\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly ProjectStore project;

        public SpreadsheetService(ProjectStore projectStore)
        {
            project = projectStore;
        }

        public object Inspect(string environment, string model, long? dashboardId, string name)
        {
            environment = ProjectStore.NormalizeEnvironment(environment);
            model = NormalizeModel(model);
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                using (OdooClient client = Open(environment))
                {
                    List<object> domain = new List<object>();
                    if (dashboardId.HasValue && dashboardId.Value > 0)
                    {
                        domain.Add(new object[] { "id", "=", dashboardId.Value });
                    }
                    else if (!string.IsNullOrWhiteSpace(name))
                    {
                        domain.Add(new object[] { "name", "ilike", name.Trim() });
                    }
                    else
                    {
                        throw new ConsultantException("missing_dashboard_identifier", "Debe proporcionar el ID numérico o el nombre del tablero a inspeccionar.");
                    }

                    string dataField = model == "documents.document" ? "raw" : "spreadsheet_data";
                    List<string> fieldsToRead = new List<string> { "id", "name", dataField, "write_date" };
                    if (model == "spreadsheet.dashboard")
                    {
                        fieldsToRead.Add("dashboard_group_id");
                        fieldsToRead.Add("group_ids");
                        fieldsToRead.Add("sequence");
                    }
                    else if (model == "documents.document")
                    {
                        fieldsToRead.Add("folder_id");
                        fieldsToRead.Add("handler");
                    }

                    IList<Dictionary<string, object>> rows = client.SearchRead(model, domain.ToArray(), fieldsToRead.ToArray(), 1, "id asc");
                    if (rows == null || rows.Count == 0)
                    {
                        throw new ConsultantException("dashboard_not_found", "No se encontró ningún registro en " + model + " con los criterios especificados.");
                    }

                    Dictionary<string, object> record = rows[0];
                    long id = Convert.ToInt64(record["id"], CultureInfo.InvariantCulture);
                    string recordName = Convert.ToString(record["name"], CultureInfo.InvariantCulture);
                    object rawData = record.ContainsKey(dataField) ? record[dataField] : null;

                    Dictionary<string, object> spreadsheetData = ParseSpreadsheetJson(rawData);
                    Dictionary<string, object> fixedDef;
                    Dictionary<string, object> validationReport = ValidateInternal(spreadsheetData, false, out fixedDef);

                    List<Dictionary<string, object>> sheetSummaries = new List<Dictionary<string, object>>();
                    IList sheetsList = spreadsheetData.ContainsKey("sheets") ? spreadsheetData["sheets"] as IList : null;
                    int totalCells = 0;
                    int totalFigures = 0;
                    int formulaCount = 0;
                    List<string> referencedModels = new List<string>();

                    if (sheetsList != null)
                    {
                        foreach (object sheetObj in sheetsList)
                        {
                            Dictionary<string, object> sheet = sheetObj as Dictionary<string, object>;
                            if (sheet == null) continue;
                            string sheetName = JsonUtil.GetString(sheet, "name", false) ?? "Hoja";
                            int rowsCount = (int)JsonUtil.GetLong(sheet, "rowNumber", false);
                            int colsCount = (int)JsonUtil.GetLong(sheet, "colNumber", false);
                            Dictionary<string, object> cells = sheet.ContainsKey("cells") ? sheet["cells"] as Dictionary<string, object> : null;
                            IList figures = sheet.ContainsKey("figures") ? sheet["figures"] as IList : null;
                            int cellCount = cells != null ? cells.Count : 0;
                            int figureCount = figures != null ? figures.Count : 0;
                            totalCells += cellCount;
                            totalFigures += figureCount;

                            if (cells != null)
                            {
                                foreach (KeyValuePair<string, object> cellKvp in cells)
                                {
                                    string cellVal = Convert.ToString(cellKvp.Value, CultureInfo.InvariantCulture);
                                    if (cellVal != null && cellVal.StartsWith("=", StringComparison.Ordinal)) formulaCount++;
                                }
                            }

                            if (figures != null)
                            {
                                foreach (object figObj in figures)
                                {
                                    Dictionary<string, object> fig = figObj as Dictionary<string, object>;
                                    if (fig == null) continue;
                                    Dictionary<string, object> data = fig.ContainsKey("data") ? fig["data"] as Dictionary<string, object> : null;
                                    if (data != null)
                                    {
                                        Dictionary<string, object> searchParams = data.ContainsKey("searchParams") ? data["searchParams"] as Dictionary<string, object> : null;
                                        string figModel = searchParams != null ? JsonUtil.GetString(searchParams, "model", false) : null;
                                        if (string.IsNullOrWhiteSpace(figModel)) figModel = JsonUtil.GetString(data, "model", false);
                                        if (!string.IsNullOrWhiteSpace(figModel) && !referencedModels.Contains(figModel)) referencedModels.Add(figModel);
                                    }
                                }
                            }

                            sheetSummaries.Add(JsonUtil.Dictionary(
                                "name", sheetName,
                                "row_count", rowsCount,
                                "col_count", colsCount,
                                "cell_count", cellCount,
                                "figure_count", figureCount
                            ));
                        }
                    }

                    AuditLog.Append(project, environment, "tablero_inspeccionar", id.ToString(CultureInfo.InvariantCulture), "success", "Tablero '" + recordName + "' inspeccionado: " + sheetSummaries.Count.ToString(CultureInfo.InvariantCulture) + " hojas, " + totalCells.ToString(CultureInfo.InvariantCulture) + " celdas.", timer.ElapsedMilliseconds);

                    return JsonUtil.Dictionary(
                        "ok", true,
                        "tablero_id", id,
                        "nombre", recordName,
                        "modelo", model,
                        "entorno", environment,
                        "fecha_modificacion", record.ContainsKey("write_date") ? record["write_date"] : null,
                        "resumen_hojas", sheetSummaries,
                        "total_celdas", totalCells,
                        "total_figuras", totalFigures,
                        "total_formulas", formulaCount,
                        "modelos_referenciados", referencedModels,
                        "auditoria_conformidad", validationReport
                    );
                }
            }
            catch (Exception error)
            {
                AuditLog.Append(project, environment, "tablero_inspeccionar", null, "error", SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }

        public object ValidateDefinition(object rawDefinition, bool autoFix)
        {
            Dictionary<string, object> spreadsheetData = ParseSpreadsheetJson(rawDefinition);
            Dictionary<string, object> fixedDefinition;
            Dictionary<string, object> report = ValidateInternal(spreadsheetData, autoFix, out fixedDefinition);
            if (autoFix && fixedDefinition != null)
            {
                report["definicion_reparada"] = fixedDefinition;
                report["spreadsheet_data_reparada"] = SerializeSpreadsheetJson(fixedDefinition);
            }
            return report;
        }

        public object PreparePackage(string environment, string model, string mode, long? targetId, string name, long? groupId, object definition, bool autoFix)
        {
            environment = ProjectStore.NormalizeEnvironment(environment);
            StudioService.EnsureWriteAllowed(environment);
            model = NormalizeModel(model);
            mode = (mode ?? string.Empty).Trim().ToLowerInvariant();
            if (mode != "create" && mode != "update") throw new ConsultantException("invalid_mode", "El modo debe ser 'create' o 'update'.");
            if (mode == "update" && (!targetId.HasValue || targetId.Value <= 0)) throw new ConsultantException("missing_target_id", "El modo 'update' exige un targetId válido.");
            if (string.IsNullOrWhiteSpace(name)) throw new ConsultantException("missing_name", "Debe proporcionar un nombre para el tablero.");

            Stopwatch timer = Stopwatch.StartNew();
            string packageId = "chg-" + Guid.NewGuid().ToString("N");
            try
            {
                Dictionary<string, object> spreadsheetData = ParseSpreadsheetJson(definition);
                Dictionary<string, object> fixedDefinition;
                Dictionary<string, object> validation = ValidateInternal(spreadsheetData, autoFix, out fixedDefinition);

                bool valid = Convert.ToBoolean(validation["valid"], CultureInfo.InvariantCulture);
                if (!valid)
                {
                    IList fatalErrors = validation["fatal_errors"] as IList;
                    string errorsSummary = fatalErrors != null ? string.Join("; ", fatalErrors.Cast<object>().Select(delegate(object x) { return Convert.ToString(x, CultureInfo.InvariantCulture); })) : "Errores de validación.";
                    throw new ConsultantException("invalid_spreadsheet_definition", "La definición del tablero no cumple las reglas críticas: " + errorsSummary);
                }

                Dictionary<string, object> finalData = autoFix && fixedDefinition != null ? fixedDefinition : spreadsheetData;
                string serializedJson = SerializeSpreadsheetJson(finalData);

                string dataField = model == "documents.document" ? "raw" : "spreadsheet_data";
                Dictionary<string, object> parameters = JsonUtil.Dictionary(
                    "name", name.Trim(),
                    dataField, serializedJson,
                    "target_id", targetId.HasValue ? targetId.Value : 0,
                    "mode", mode
                );

                if (model == "spreadsheet.dashboard" && groupId.HasValue && groupId.Value > 0)
                {
                    parameters["dashboard_group_id"] = groupId.Value;
                }

                ChangeOperation op = new ChangeOperation
                {
                    operation_id = "op-" + Guid.NewGuid().ToString("N"),
                    type = (mode == "create" ? "create_" : "update_") + model.Replace('.', '_'),
                    model = model,
                    parameters = parameters,
                    status = "pending",
                    result_id = targetId.HasValue ? targetId.Value : 0
                };

                DateTimeOffset now = DateTimeOffset.UtcNow;
                ChangeSet change = new ChangeSet
                {
                    schema_version = 2,
                    id = packageId,
                    client_slug = project.Config.client_slug,
                    environment = environment,
                    category = "spreadsheet_dashboard",
                    risk = "medium",
                    status = "prepared",
                    model = model,
                    created_at = now.ToString("o", CultureInfo.InvariantCulture),
                    expires_at = now.AddMinutes(project.Config.package_confirmation_minutes).ToString("o", CultureInfo.InvariantCulture),
                    requested_by = Environment.UserName,
                    impact = (mode == "create" ? "Creación" : "Actualización") + " de hoja de cálculo / tablero '" + name.Trim() + "' en " + model + ".",
                    rollback_strategy = mode == "create" ? "El tablero creado puede archivarse o desactivarse en staging." : "Restaura la versión previa del spreadsheet_data.",
                    operations = new List<ChangeOperation> { op },
                    warnings = new List<string>
                    {
                        "Revisar las estadísticas de validación y la estructura de hojas antes de aplicar.",
                        "La confirmación exacta requerida es APLICAR-TABLERO " + packageId + "."
                    }
                };

                JsonUtil.WriteJsonFile(project.ChangeSetPath(packageId), change);
                AuditLog.Append(project, environment, "tablero_preparar_paquete", packageId, "success", "Paquete preparado para " + mode + " en " + model + " ('" + name + "').", timer.ElapsedMilliseconds);

                return JsonUtil.Dictionary(
                    "ok", true,
                    "paquete_id", packageId,
                    "confirmacion_requerida", "APLICAR-TABLERO " + packageId,
                    "expires_at", change.expires_at,
                    "modo", mode,
                    "modelo", model,
                    "nombre", name.Trim(),
                    "validacion", validation
                );
            }
            catch (Exception error)
            {
                AuditLog.Append(project, environment, "tablero_preparar_paquete", packageId, "error", SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }

        public object ApplyPackage(string packageId, string confirmation)
        {
            packageId = (packageId ?? string.Empty).Trim().ToLowerInvariant();
            if (!Regex.IsMatch(packageId, "^chg-[a-f0-9]{32}$")) throw new ConsultantException("invalid_package_id", "El identificador de paquete no es válido.");

            string expectedConfirmation = "APLICAR-TABLERO " + packageId;
            if (!string.Equals((confirmation ?? string.Empty).Trim(), expectedConfirmation, StringComparison.Ordinal))
            {
                throw new ConsultantException("confirmation_mismatch", "La confirmación debe ser exactamente: " + expectedConfirmation);
            }

            string packagePath = project.ChangeSetPath(packageId);
            if (!File.Exists(packagePath)) throw new ConsultantException("package_not_found", "No existe el paquete " + packageId + ".");

            ChangeSet change = JsonUtil.ReadJsonFile<ChangeSet>(packagePath);
            if (change.category != "spreadsheet_dashboard") throw new ConsultantException("package_category_mismatch", "El paquete no es de tipo spreadsheet_dashboard.");
            if (change.status != "prepared") throw new ConsultantException("package_not_prepared", "El paquete no está en estado 'prepared' (estado actual: " + change.status + ").");

            DateTimeOffset expiresAt;
            if (!DateTimeOffset.TryParse(change.expires_at, CultureInfo.InvariantCulture, DateTimeStyles.None, out expiresAt) || DateTimeOffset.UtcNow > expiresAt)
            {
                throw new ConsultantException("package_expired", "El paquete ha vencido. Prepare un nuevo paquete.");
            }

            StudioService.EnsureWriteAllowed(change.environment);
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                using (OdooClient client = Open(change.environment))
                {
                    ChangeOperation op = change.operations[0];
                    string model = op.model;
                    string mode = JsonUtil.GetString(op.parameters, "mode", true);
                    string name = JsonUtil.GetString(op.parameters, "name", true);
                    string dataField = model == "documents.document" ? "raw" : "spreadsheet_data";
                    string dataJson = JsonUtil.GetString(op.parameters, dataField, true);

                    Dictionary<string, object> writeValues = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "name", name },
                        { dataField, dataJson }
                    };

                    if (model == "spreadsheet.dashboard" && op.parameters.ContainsKey("dashboard_group_id"))
                    {
                        long groupId = JsonUtil.GetLong(op.parameters, "dashboard_group_id", false);
                        if (groupId > 0) writeValues["dashboard_group_id"] = groupId;
                    }

                    long resultId = 0;
                    if (mode == "create")
                    {
                        resultId = client.Create(model, writeValues);
                        op.result_id = resultId;
                    }
                    else if (mode == "update")
                    {
                        resultId = JsonUtil.GetLong(op.parameters, "target_id", true);
                        bool updated = client.Write(model, new long[] { resultId }, writeValues);
                        if (!updated) throw new ConsultantException("write_failed", "Odoo no confirmó la actualización del tablero.");
                        op.result_id = resultId;
                    }

                    IList<Dictionary<string, object>> verifyRows = client.SearchRead(model, new object[] { new object[] { "id", "=", resultId } }, new[] { "id", "name", "write_date" }, 1, null);
                    if (verifyRows == null || verifyRows.Count != 1) throw new ConsultantException("verification_failed", "No se pudo verificar el registro creado/actualizado en Odoo.");

                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    change.status = "applied";
                    change.applied_at = now.ToString("o", CultureInfo.InvariantCulture);
                    change.verified_at = now.ToString("o", CultureInfo.InvariantCulture);
                    op.status = "applied";
                    JsonUtil.WriteJsonFile(packagePath, change);

                    AuditLog.Append(project, change.environment, "tablero_aplicar_paquete", packageId, "success", "Tablero " + mode + " exitoso en " + model + " (ID " + resultId.ToString(CultureInfo.InvariantCulture) + ").", timer.ElapsedMilliseconds);

                    return JsonUtil.Dictionary(
                        "ok", true,
                        "status", "applied_and_verified",
                        "paquete_id", packageId,
                        "tablero_id", resultId,
                        "modelo", model,
                        "nombre", name,
                        "entorno", change.environment,
                        "verificado_en", change.verified_at
                    );
                }
            }
            catch (Exception error)
            {
                AuditLog.Append(project, change.environment, "tablero_aplicar_paquete", packageId, "error", SafeError(error), timer.ElapsedMilliseconds);
                throw;
            }
        }

        public static Dictionary<string, object> ValidateInternal(Dictionary<string, object> spreadsheet, bool autoFix, out Dictionary<string, object> fixedSpreadsheet)
        {
            fixedSpreadsheet = null;
            if (spreadsheet == null) throw new ConsultantException("invalid_input", "La definición no puede ser nula.");

            Dictionary<string, object> root = autoFix ? CloneDictionary(spreadsheet) : spreadsheet;
            List<string> fatalErrors = new List<string>();
            List<string> warnings = new List<string>();
            List<string> repairNotes = new List<string>();

            if (!root.ContainsKey("sheets") || !(root["sheets"] is IList))
            {
                fatalErrors.Add("La propiedad raíz 'sheets' está ausente o no es una lista válida.");
                return BuildReport(false, fatalErrors, warnings, repairNotes, 0, 0, 0, 0);
            }

            if (!root.ContainsKey("styles") || !(root["styles"] is Dictionary<string, object>))
            {
                if (autoFix) { root["styles"] = new Dictionary<string, object>(); repairNotes.Add("Inicializado diccionario 'styles' en la raíz."); }
                else warnings.Add("No se encontró el diccionario 'styles' en la raíz del spreadsheet.");
            }
            if (!root.ContainsKey("formats") || !(root["formats"] is Dictionary<string, object>))
            {
                if (autoFix) { root["formats"] = new Dictionary<string, object>(); repairNotes.Add("Inicializado diccionario 'formats' en la raíz."); }
                else warnings.Add("No se encontró el diccionario 'formats' en la raíz del spreadsheet.");
            }

            IList sheetsList = root["sheets"] as IList;
            int totalCells = 0;
            int totalFigures = 0;
            int totalLinks = 0;

            for (int sIdx = 0; sIdx < sheetsList.Count; sIdx++)
            {
                Dictionary<string, object> sheet = sheetsList[sIdx] as Dictionary<string, object>;
                if (sheet == null) continue;
                string sheetName = JsonUtil.GetString(sheet, "name", false) ?? ("Hoja " + (sIdx + 1).ToString(CultureInfo.InvariantCulture));

                string[] requiredSheetKeys = new[] { "borders", "merges", "figures", "tables", "headerGroups", "comments" };
                foreach (string key in requiredSheetKeys)
                {
                    if (!sheet.ContainsKey(key))
                    {
                        if (autoFix)
                        {
                            if (key == "merges" || key == "figures" || key == "tables") sheet[key] = new List<object>();
                            else if (key == "headerGroups") sheet[key] = JsonUtil.Dictionary("ROW", new List<object>(), "COL", new List<object>());
                            else sheet[key] = new Dictionary<string, object>();
                            repairNotes.Add("Hoja '" + sheetName + "': inicializada clave requerida '" + key + "'.");
                        }
                    }
                }
                if (!sheet.ContainsKey("areGridLinesVisible") && autoFix) sheet["areGridLinesVisible"] = true;
                if (!sheet.ContainsKey("isVisible") && autoFix) sheet["isVisible"] = true;

                if (sheet.ContainsKey("cells") && sheet["cells"] is Dictionary<string, object>)
                {
                    Dictionary<string, object> cells = sheet["cells"] as Dictionary<string, object>;
                    List<string> cellKeys = new List<string>(cells.Keys);
                    foreach (string cellKey in cellKeys)
                    {
                        totalCells++;
                        object cellValObj = cells[cellKey];

                        if (cellValObj is Dictionary<string, object>)
                        {
                            Dictionary<string, object> cellDict = cellValObj as Dictionary<string, object>;
                            if (autoFix)
                            {
                                string extracted = "";
                                if (cellDict.ContainsKey("content")) extracted = Convert.ToString(cellDict["content"], CultureInfo.InvariantCulture) ?? "";
                                else if (cellDict.ContainsKey("value")) extracted = Convert.ToString(cellDict["value"], CultureInfo.InvariantCulture) ?? "";
                                cells[cellKey] = extracted;
                                cellValObj = extracted;
                                repairNotes.Add("Hoja '" + sheetName + "', celda '" + cellKey + "': convertido objeto a string directo ('" + extracted + "').");
                            }
                            else
                            {
                                fatalErrors.Add("Estructura inválida en hoja '" + sheetName + "', celda '" + cellKey + "': el valor es un objeto. En Odoo 18/19 debe ser una cadena (string) directa para evitar TypeError: content.startsWith is not a function.");
                                continue;
                            }
                        }

                        string cellStr = Convert.ToString(cellValObj, CultureInfo.InvariantCulture) ?? "";

                        if (MojibakePattern.IsMatch(cellStr))
                        {
                            warnings.Add("Posible mojibake detectado en hoja '" + sheetName + "', celda '" + cellKey + "': '" + cellStr + "'.");
                        }

                        if (!cellStr.StartsWith("=", StringComparison.Ordinal) && TechnicalLabelPattern.IsMatch(cellStr))
                        {
                            warnings.Add("Etiqueta con código técnico entre paréntesis en hoja '" + sheetName + "', celda '" + cellKey + "': '" + cellStr + "'. Use nombres limpios orientados al usuario.");
                        }

                        Match match = LinkPattern.Match(cellStr);
                        if (!match.Success) match = BareLinkPattern.Match(cellStr);
                        if (match.Success)
                        {
                            totalLinks++;
                            string rawUrl = match.Groups["url"].Value;
                            string jsonPart = rawUrl.Substring("odoo://view/".Length);
                            try
                            {
                                string unescaped = jsonPart.StartsWith("%", StringComparison.Ordinal) || jsonPart.Contains("%7B") || jsonPart.Contains("%22")
                                    ? Uri.UnescapeDataString(jsonPart)
                                    : jsonPart;

                                Dictionary<string, object> linkObj = JsonUtil.Deserialize<Dictionary<string, object>>(unescaped);
                                if (linkObj != null)
                                {
                                    if (!linkObj.ContainsKey("action") || !(linkObj["action"] is Dictionary<string, object>))
                                    {
                                        if (autoFix)
                                        {
                                            Dictionary<string, object> innerAction = new Dictionary<string, object>(StringComparer.Ordinal);
                                            foreach (string k in new[] { "xmlId", "modelName", "views", "domain", "context" })
                                            {
                                                if (linkObj.ContainsKey(k)) { innerAction[k] = linkObj[k]; linkObj.Remove(k); }
                                            }
                                            linkObj["action"] = innerAction;
                                            string fixedJson = JsonUtil.Serialize(linkObj);
                                            string fixedUrl = "odoo://view/" + fixedJson;
                                            string linkText = match.Groups["text"].Success ? match.Groups["text"].Value : "Ver registros";
                                            cells[cellKey] = "[" + linkText + "](<" + fixedUrl + ">)";
                                            repairNotes.Add("Hoja '" + sheetName + "', celda '" + cellKey + "': envuelto objeto de enlace dentro de 'action' requerido.");
                                        }
                                        else
                                        {
                                            fatalErrors.Add("Enlace odoo://view en hoja '" + sheetName + "', celda '" + cellKey + "' no contiene el objeto anidado obligatorio 'action'. Provoca TypeError: Cannot read properties of undefined (reading 'xmlId') en el frontend de Odoo.");
                                        }
                                    }
                                    else
                                    {
                                        Dictionary<string, object> actionDict = linkObj["action"] as Dictionary<string, object>;
                                        if (actionDict != null && actionDict.ContainsKey("domain"))
                                        {
                                            object domainObj = actionDict["domain"];
                                            string domainError;
                                            object fixedDomain;
                                            if (!ValidateAndFixDomain(domainObj, autoFix, out domainError, out fixedDomain))
                                            {
                                                fatalErrors.Add("Dominio en enlace odoo://view de hoja '" + sheetName + "', celda '" + cellKey + "': " + domainError);
                                            }
                                            else if (autoFix && fixedDomain != null)
                                            {
                                                actionDict["domain"] = fixedDomain;
                                                string fixedJson = JsonUtil.Serialize(linkObj);
                                                string fixedUrl = "odoo://view/" + fixedJson;
                                                string linkText = match.Groups["text"].Success ? match.Groups["text"].Value : "Ver registros";
                                                cells[cellKey] = "[" + linkText + "](<" + fixedUrl + ">)";
                                                repairNotes.Add("Hoja '" + sheetName + "', celda '" + cellKey + "': corregido dominio plano a arreglo bidimensional AST.");
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception parseEx)
                            {
                                fatalErrors.Add("Enlace odoo://view en hoja '" + sheetName + "', celda '" + cellKey + "' contiene JSON malformado: " + parseEx.Message);
                            }
                        }
                    }
                }

                if (sheet.ContainsKey("figures") && sheet["figures"] is IList)
                {
                    IList figuresList = sheet["figures"] as IList;
                    foreach (object figObj in figuresList)
                    {
                        Dictionary<string, object> fig = figObj as Dictionary<string, object>;
                        if (fig == null) continue;
                        totalFigures++;
                        Dictionary<string, object> figData = fig.ContainsKey("data") ? fig["data"] as Dictionary<string, object> : null;
                        if (figData != null)
                        {
                            Dictionary<string, object> searchParams = figData.ContainsKey("searchParams") ? figData["searchParams"] as Dictionary<string, object> : null;
                            if (searchParams != null && searchParams.ContainsKey("domain"))
                            {
                                object domainObj = searchParams["domain"];
                                string domainError;
                                object fixedDomain;
                                if (!ValidateAndFixDomain(domainObj, autoFix, out domainError, out fixedDomain))
                                {
                                    fatalErrors.Add("Dominio en figura de hoja '" + sheetName + "': " + domainError);
                                }
                                else if (autoFix && fixedDomain != null)
                                {
                                    searchParams["domain"] = fixedDomain;
                                    repairNotes.Add("Hoja '" + sheetName + "', figura: corregido dominio de búsqueda a 2D AST.");
                                }
                            }
                            else if (figData.ContainsKey("domain"))
                            {
                                object domainObj = figData["domain"];
                                string domainError;
                                object fixedDomain;
                                if (!ValidateAndFixDomain(domainObj, autoFix, out domainError, out fixedDomain))
                                {
                                    fatalErrors.Add("Dominio en figura de hoja '" + sheetName + "': " + domainError);
                                }
                                else if (autoFix && fixedDomain != null)
                                {
                                    figData["domain"] = fixedDomain;
                                    repairNotes.Add("Hoja '" + sheetName + "', figura: corregido dominio directo a 2D AST.");
                                }
                            }
                        }
                    }
                }
            }

            if (autoFix && repairNotes.Count > 0) fixedSpreadsheet = root;

            bool isValid = fatalErrors.Count == 0;
            return BuildReport(isValid, fatalErrors, warnings, repairNotes, sheetsList.Count, totalCells, totalFigures, totalLinks);
        }

        private static bool ValidateAndFixDomain(object domainObj, bool autoFix, out string errorMessage, out object fixedDomain)
        {
            errorMessage = null;
            fixedDomain = null;

            if (domainObj == null) return true;
            IList list = domainObj as IList;
            if (list == null)
            {
                errorMessage = "El dominio no es un arreglo.";
                return false;
            }

            if (list.Count == 0) return true;

            if (list.Count == 3 && list[0] is string && list[1] is string && !(list[0] is IList))
            {
                if (autoFix)
                {
                    List<object> tuple = new List<object>();
                    foreach (object item in list) tuple.Add(item);
                    fixedDomain = new List<object> { tuple };
                    return true;
                }
                errorMessage = "Dominio 1D plano detectado [" + list[0] + ", " + list[1] + ", " + list[2] + "]. Debe ser un arreglo bidimensional AST: [[\"campo\", \"operador\", valor]].";
                return false;
            }

            for (int i = 0; i < list.Count; i++)
            {
                object elem = list[i];
                if (elem is string)
                {
                    string op = (string)elem;
                    if (op == "&" || op == "|" || op == "!") continue;
                    errorMessage = "Elemento plano '" + op + "' en posición " + i + " no es tupla ni operador Odoo ('&', '|', '!').";
                    return false;
                }
                if (!(elem is IList))
                {
                    errorMessage = "Elemento en posición " + i + " no es un arreglo de tupla.";
                    return false;
                }
            }

            return true;
        }

        private static Dictionary<string, object> BuildReport(bool valid, List<string> fatalErrors, List<string> warnings, List<string> repairNotes, int sheets, int cells, int figures, int links)
        {
            return JsonUtil.Dictionary(
                "valid", valid,
                "fatal_errors", fatalErrors,
                "warnings", warnings,
                "repair_notes", repairNotes,
                "repaired", repairNotes.Count > 0,
                "stats", JsonUtil.Dictionary(
                    "sheets_count", sheets,
                    "cells_count", cells,
                    "figures_count", figures,
                    "links_count", links
                )
            );
        }

        public static Dictionary<string, object> ParseSpreadsheetJson(object raw)
        {
            if (raw == null) throw new ConsultantException("invalid_spreadsheet_data", "La definición de hoja de cálculo no puede ser nula.");
            if (raw is Dictionary<string, object>)
            {
                Dictionary<string, object> dict = raw as Dictionary<string, object>;
                if (dict.ContainsKey("spreadsheet_data")) return ParseSpreadsheetJson(dict["spreadsheet_data"]);
                if (dict.ContainsKey("raw")) return ParseSpreadsheetJson(dict["raw"]);
                return dict;
            }
            string str = raw as string;
            if (str != null)
            {
                str = str.Trim();
                if (!str.StartsWith("{", StringComparison.Ordinal) && !str.StartsWith("[", StringComparison.Ordinal))
                {
                    try
                    {
                        byte[] bytes = Convert.FromBase64String(str);
                        str = Encoding.UTF8.GetString(bytes).Trim();
                    }
                    catch { }
                }
                return JsonUtil.Deserialize<Dictionary<string, object>>(str);
            }
            throw new ConsultantException("invalid_spreadsheet_data", "Formato de definición de hoja de cálculo no reconocido.");
        }

        public static string SerializeSpreadsheetJson(Dictionary<string, object> spreadsheet)
        {
            return JsonUtil.Serialize(spreadsheet);
        }

        private static Dictionary<string, object> CloneDictionary(Dictionary<string, object> source)
        {
            string json = JsonUtil.Serialize(source);
            return JsonUtil.Deserialize<Dictionary<string, object>>(json);
        }

        private static string NormalizeModel(string model)
        {
            string m = (model ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(m) || m == "spreadsheet.dashboard") return "spreadsheet.dashboard";
            if (m == "documents.document") return "documents.document";
            throw new ConsultantException("invalid_model", "El modelo debe ser 'spreadsheet.dashboard' o 'documents.document'.");
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
