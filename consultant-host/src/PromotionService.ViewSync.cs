using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace OdooConsultor
{
    public sealed partial class PromotionService
    {
        private static long ParseInheritId(object value)
        {
            if (value == null || value is bool) return 0;
            IEnumerable seq = value as IEnumerable;
            if (seq != null && !(value is string))
            {
                foreach (object item in seq)
                {
                    long p;
                    if (long.TryParse(Convert.ToString(item, CultureInfo.InvariantCulture), out p) && p > 0) return p;
                }
                return 0;
            }
            long direct;
            if (long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out direct)) return direct;
            return 0;
        }

        private static long ResolveProductionParentView(OdooClient clientStaging, OdooClient clientProd, long stagingParentId, string model, string viewType)
        {
            if (stagingParentId > 0)
            {
                try
                {
                    IList<Dictionary<string, object>> xmlData = clientStaging.SearchRead(
                        "ir.model.data",
                        new object[] {
                            new object[] { "model", "=", "ir.ui.view" },
                            new object[] { "res_id", "=", stagingParentId }
                        },
                        new[] { "module", "name" },
                        1,
                        null
                    );
                    if (xmlData.Count > 0)
                    {
                        string mod = Convert.ToString(xmlData[0]["module"], CultureInfo.InvariantCulture);
                        string xName = Convert.ToString(xmlData[0]["name"], CultureInfo.InvariantCulture);
                        IList<Dictionary<string, object>> prodXmlData = clientProd.SearchRead(
                            "ir.model.data",
                            new object[] {
                                new object[] { "module", "=", mod },
                                new object[] { "name", "=", xName },
                                new object[] { "model", "=", "ir.ui.view" }
                            },
                            new[] { "res_id" },
                            1,
                            null
                        );
                        if (prodXmlData.Count > 0 && prodXmlData[0]["res_id"] != null)
                        {
                            long prodId;
                            if (long.TryParse(Convert.ToString(prodXmlData[0]["res_id"], CultureInfo.InvariantCulture), out prodId) && prodId > 0)
                            {
                                return prodId;
                            }
                        }
                    }
                }
                catch { }

                try
                {
                    IList<Dictionary<string, object>> parentRows = clientStaging.Read("ir.ui.view", new[] { stagingParentId }, new[] { "id", "name", "model" });
                    if (parentRows.Count > 0)
                    {
                        string pName = Convert.ToString(parentRows[0]["name"], CultureInfo.InvariantCulture);
                        string pModel = Convert.ToString(parentRows[0]["model"], CultureInfo.InvariantCulture);
                        IList<Dictionary<string, object>> prodParents = clientProd.SearchRead(
                            "ir.ui.view",
                            new object[] {
                                new object[] { "model", "=", pModel },
                                new object[] { "name", "=", pName }
                            },
                            new[] { "id", "name" },
                            1,
                            null
                        );
                        if (prodParents.Count > 0)
                        {
                            long prodId;
                            if (long.TryParse(Convert.ToString(prodParents[0]["id"], CultureInfo.InvariantCulture), out prodId) && prodId > 0)
                            {
                                return prodId;
                            }
                        }
                    }
                }
                catch { }
            }

            try
            {
                IList<Dictionary<string, object>> baseViews = clientProd.SearchRead(
                    "ir.ui.view",
                    new object[] {
                        new object[] { "model", "=", model },
                        new object[] { "type", "=", string.IsNullOrWhiteSpace(viewType) ? "form" : viewType },
                        new object[] { "inherit_id", "=", false }
                    },
                    new[] { "id", "name" },
                    1,
                    "id asc"
                );
                if (baseViews.Count > 0)
                {
                    long prodId;
                    if (long.TryParse(Convert.ToString(baseViews[0]["id"], CultureInfo.InvariantCulture), out prodId) && prodId > 0)
                    {
                        return prodId;
                    }
                }
            }
            catch { }

            return 0;
        }

        private Dictionary<string, object> EnsurePromotedFieldsVisible(ChangeSet change)
        {
            Dictionary<string, object> result = JsonUtil.Dictionary(
                "status", "not_applicable",
                "synced_views", new List<object>(),
                "synced_companion_fields", new List<object>(),
                "visible_fields", new List<string>(),
                "invisible_fields", new List<string>()
            );

            HashSet<string> targetModels = new HashSet<string>(change.target_models ?? new List<string>(), StringComparer.Ordinal);
            Dictionary<string, HashSet<string>> modelFields = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            if (change.operations != null)
            {
                foreach (ChangeOperation op in change.operations)
                {
                    if (op.parameters != null && op.parameters.ContainsKey("name"))
                    {
                        string fName = Convert.ToString(op.parameters["name"], CultureInfo.InvariantCulture);
                        if (fName.StartsWith("x_", StringComparison.OrdinalIgnoreCase))
                        {
                            string m = op.model ?? change.model;
                            if (!string.IsNullOrWhiteSpace(m))
                            {
                                if (!modelFields.ContainsKey(m)) modelFields[m] = new HashSet<string>(StringComparer.Ordinal);
                                modelFields[m].Add(fName);
                                targetModels.Add(m);
                            }
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(change.replica_slug))
            {
                string manifestPath = Path.Combine(project.ReplicaPath(change.replica_slug), "manifest.json");
                if (File.Exists(manifestPath))
                {
                    try
                    {
                        Dictionary<string, object> manifest = JsonUtil.ReadJsonFile<Dictionary<string, object>>(manifestPath);
                        IList fields = manifest.ContainsKey("fields") ? manifest["fields"] as IList : null;
                        if (fields != null)
                        {
                            foreach (object item in fields)
                            {
                                Dictionary<string, object> f = item as Dictionary<string, object>;
                                if (f == null) continue;
                                string fName = JsonUtil.GetString(f, "name", false);
                                string m = JsonUtil.GetString(f, "model", false);
                                if (!string.IsNullOrWhiteSpace(fName) && fName.StartsWith("x_", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(m))
                                {
                                    if (!modelFields.ContainsKey(m)) modelFields[m] = new HashSet<string>(StringComparer.Ordinal);
                                    modelFields[m].Add(fName);
                                    targetModels.Add(m);
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            if (modelFields.Count == 0)
            {
                result["status"] = "no_custom_fields";
                return result;
            }

            ConnectionProfile stagingProfile = project.GetProfile("staging", false);
            ConnectionProfile prodProfile = project.GetProfile("production", false);
            if (stagingProfile == null || prodProfile == null ||
                !CredentialStore.Exists(stagingProfile.credential_target) ||
                !CredentialStore.Exists(prodProfile.credential_target))
            {
                result["status"] = "profiles_unavailable_for_view_sync";
                return result;
            }

            List<object> syncedViews = new List<object>();
            List<object> syncedCompanionFields = new List<object>();
            List<string> visibleFields = new List<string>();
            List<string> invisibleFields = new List<string>();
            string clientSlug = project.Config.client_slug;

            using (OdooClient clientStaging = Open("staging"))
            using (OdooClient clientProd = Open("production"))
            {
                foreach (KeyValuePair<string, HashSet<string>> pair in modelFields)
                {
                    string model = pair.Key;
                    HashSet<string> fields = pair.Value;

                    IList<Dictionary<string, object>> stagingViews = clientStaging.SearchRead(
                        "ir.ui.view",
                        new object[] {
                            new object[] { "model", "=", model },
                            new object[] { "active", "=", true }
                        },
                        new[] { "id", "name", "model", "type", "inherit_id", "arch_db", "mode", "priority" },
                        100,
                        "id asc"
                    );

                    List<Dictionary<string, object>> matchingStagingViews = new List<Dictionary<string, object>>();
                    foreach (Dictionary<string, object> sv in stagingViews)
                    {
                        string arch = sv.ContainsKey("arch_db") && sv["arch_db"] != null ? Convert.ToString(sv["arch_db"], CultureInfo.InvariantCulture) : string.Empty;
                        string vn = sv.ContainsKey("name") && sv["name"] != null ? Convert.ToString(sv["name"], CultureInfo.InvariantCulture) : string.Empty;
                        if (string.IsNullOrWhiteSpace(arch)) continue;

                        bool referencesField = false;
                        foreach (string fName in fields)
                        {
                            if (arch.IndexOf(fName, StringComparison.Ordinal) >= 0)
                            {
                                referencesField = true;
                                break;
                            }
                        }
                        if (!referencesField && !string.IsNullOrWhiteSpace(clientSlug))
                        {
                            if (vn.IndexOf(clientSlug, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                vn.IndexOf("OdooAI", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                referencesField = true;
                            }
                        }
                        if (referencesField)
                        {
                            matchingStagingViews.Add(sv);
                        }
                    }

                    foreach (Dictionary<string, object> sv in matchingStagingViews)
                    {
                        string vName = Convert.ToString(sv["name"], CultureInfo.InvariantCulture);
                        string vType = Convert.ToString(sv["type"], CultureInfo.InvariantCulture);
                        string archDb = Convert.ToString(sv["arch_db"], CultureInfo.InvariantCulture);

                        IList<Dictionary<string, object>> prodViews = clientProd.SearchRead(
                            "ir.ui.view",
                            new object[] {
                                new object[] { "model", "=", model },
                                new object[] { "name", "=", vName }
                            },
                            new[] { "id", "name", "active" },
                            2,
                            null
                        );

                        if (prodViews.Count > 0)
                        {
                            long existingId = Convert.ToInt64(prodViews[0]["id"], CultureInfo.InvariantCulture);
                            bool isActive = Convert.ToBoolean(prodViews[0]["active"], CultureInfo.InvariantCulture);
                            if (!isActive)
                            {
                                clientProd.Write("ir.ui.view", new[] { existingId }, JsonUtil.Dictionary("active", true));
                                syncedViews.Add(JsonUtil.Dictionary(
                                    "view_id", existingId,
                                    "name", vName,
                                    "model", model,
                                    "action", "activated_existing"
                                ));
                            }
                            continue;
                        }

                        long parentIdInStaging = ParseInheritId(sv["inherit_id"]);
                        long prodParentId = ResolveProductionParentView(clientStaging, clientProd, parentIdInStaging, model, vType);

                        if (prodParentId > 0)
                        {
                            StudioService.ValidateSafeViewArchitecture(archDb);
                            string sanitizedArch = JsonUtil.XmlNumericEntities(archDb);
                            long priority = 16;
                            if (sv.ContainsKey("priority") && sv["priority"] != null)
                            {
                                long.TryParse(Convert.ToString(sv["priority"], CultureInfo.InvariantCulture), out priority);
                                if (priority <= 0) priority = 16;
                            }

                            EnsureCompanionFieldsExist(clientStaging, clientProd, model, sanitizedArch, syncedCompanionFields);

                            Dictionary<string, object> newView = JsonUtil.Dictionary(
                                "name", vName,
                                "model", model,
                                "type", vType,
                                "inherit_id", prodParentId,
                                "mode", "extension",
                                "priority", priority,
                                "arch", sanitizedArch,
                                "active", true
                            );

                            try
                            {
                                long newId = clientProd.Create("ir.ui.view", newView);
                                syncedViews.Add(JsonUtil.Dictionary(
                                    "view_id", newId,
                                    "name", vName,
                                    "model", model,
                                    "parent_view_id", prodParentId,
                                    "action", "created_in_production"
                                ));
                            }
                            catch (Exception viewError)
                            {
                                syncedViews.Add(JsonUtil.Dictionary(
                                    "name", vName,
                                    "model", model,
                                    "parent_view_id", prodParentId,
                                    "status", "failed",
                                    "error", SafeError(viewError)
                                ));
                            }
                        }
                    }

                    // Post-check in production
                    IList<Dictionary<string, object>> prodAllViews = clientProd.SearchRead(
                        "ir.ui.view",
                        new object[] {
                            new object[] { "model", "=", model },
                            new object[] { "active", "=", true }
                        },
                        new[] { "id", "name", "arch_db" },
                        200,
                        null
                    );

                    StringBuilder combinedArch = new StringBuilder();
                    foreach (Dictionary<string, object> pv in prodAllViews)
                    {
                        if (pv.ContainsKey("arch_db") && pv["arch_db"] != null)
                        {
                            combinedArch.Append(Convert.ToString(pv["arch_db"], CultureInfo.InvariantCulture));
                        }
                    }
                    string allProdArch = combinedArch.ToString();

                    foreach (string fName in fields)
                    {
                        if (allProdArch.IndexOf(fName, StringComparison.Ordinal) >= 0)
                        {
                            visibleFields.Add(fName);
                        }
                        else
                        {
                            invisibleFields.Add(fName);
                        }
                    }
                }
            }

            result["synced_views"] = syncedViews;
            result["synced_companion_fields"] = syncedCompanionFields;
            result["visible_fields"] = visibleFields;
            result["invisible_fields"] = invisibleFields;
            result["status"] = invisibleFields.Count == 0 ? "all_promoted_fields_visible" : "warning_fields_invisible";
            return result;
        }

        private static void EnsureCompanionFieldsExist(
            OdooClient clientStaging,
            OdooClient clientProd,
            string primaryModel,
            string archDb,
            List<object> syncedCompanionFields)
        {
            if (string.IsNullOrWhiteSpace(archDb)) return;

            MatchCollection matches = Regex.Matches(archDb, @"\b(x_[a-zA-Z0-9_]+)\b");
            if (matches.Count == 0) return;

            HashSet<string> fieldNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in matches)
            {
                if (m.Success && m.Groups.Count > 1)
                {
                    fieldNames.Add(m.Groups[1].Value);
                }
            }

            Dictionary<string, long> modelIdCache = new Dictionary<string, long>(StringComparer.Ordinal);
            List<Dictionary<string, object>> toCreate = new List<Dictionary<string, object>>();

            foreach (string fName in fieldNames)
            {
                IList<Dictionary<string, object>> prodExistPrimary = clientProd.SearchRead(
                    "ir.model.fields",
                    new object[] {
                        new object[] { "model", "=", primaryModel },
                        new object[] { "name", "=", fName }
                    },
                    new[] { "id" },
                    1,
                    null
                );
                if (prodExistPrimary.Count > 0) continue;

                IList<Dictionary<string, object>> stgRows = clientStaging.SearchRead(
                    "ir.model.fields",
                    new object[] {
                        new object[] { "model", "=", primaryModel },
                        new object[] { "name", "=", fName }
                    },
                    new[] { "id", "model", "name", "field_description", "help", "ttype", "required", "readonly", "store", "copied", "relation", "relation_field", "related", "depends", "currency_field", "selection" },
                    1,
                    null
                );

                if (stgRows.Count == 0)
                {
                    stgRows = clientStaging.SearchRead(
                        "ir.model.fields",
                        new object[] {
                            new object[] { "name", "=", fName }
                        },
                        new[] { "id", "model", "name", "field_description", "help", "ttype", "required", "readonly", "store", "copied", "relation", "relation_field", "related", "depends", "currency_field", "selection" },
                        5,
                        null
                    );
                }

                if (stgRows.Count > 0)
                {
                    Dictionary<string, object> chosen = stgRows[0];
                    string fieldModel = Convert.ToString(chosen["model"], CultureInfo.InvariantCulture);

                    IList<Dictionary<string, object>> prodExist = clientProd.SearchRead(
                        "ir.model.fields",
                        new object[] {
                            new object[] { "model", "=", fieldModel },
                            new object[] { "name", "=", fName }
                        },
                        new[] { "id" },
                        1,
                        null
                    );
                    if (prodExist.Count == 0)
                    {
                        toCreate.Add(chosen);
                    }
                }
            }

            if (toCreate.Count == 0) return;

            toCreate.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
            {
                string sA = a.ContainsKey("related") ? a["related"] as string : null;
                bool aIsRel = !string.IsNullOrWhiteSpace(sA);
                string sB = b.ContainsKey("related") ? b["related"] as string : null;
                bool bIsRel = !string.IsNullOrWhiteSpace(sB);
                return aIsRel.CompareTo(bIsRel);
            });

            foreach (Dictionary<string, object> sf in toCreate)
            {
                string fieldModel = Convert.ToString(sf["model"], CultureInfo.InvariantCulture);
                string fName = Convert.ToString(sf["name"], CultureInfo.InvariantCulture);

                long prodModelId;
                if (!modelIdCache.TryGetValue(fieldModel, out prodModelId))
                {
                    IList<Dictionary<string, object>> mRows = clientProd.SearchRead(
                        "ir.model",
                        new object[] { new object[] { "model", "=", fieldModel } },
                        new[] { "id" },
                        1,
                        null
                    );
                    if (mRows.Count == 0) continue;
                    prodModelId = Convert.ToInt64(mRows[0]["id"], CultureInfo.InvariantCulture);
                    modelIdCache[fieldModel] = prodModelId;
                }

                Dictionary<string, object> vals = new Dictionary<string, object>(StringComparer.Ordinal);
                vals["model_id"] = prodModelId;
                vals["name"] = fName;
                vals["field_description"] = JsonUtil.GetString(sf, "field_description", false) ?? fName;
                vals["ttype"] = JsonUtil.GetString(sf, "ttype", true);
                vals["state"] = "manual";
                vals["required"] = JsonUtil.GetBool(sf, "required", false);
                vals["readonly"] = JsonUtil.GetBool(sf, "readonly", false);
                vals["store"] = sf.ContainsKey("store") && sf["store"] != null ? Convert.ToBoolean(sf["store"], CultureInfo.InvariantCulture) : true;
                vals["copied"] = sf.ContainsKey("copied") && sf["copied"] != null ? Convert.ToBoolean(sf["copied"], CultureInfo.InvariantCulture) : true;

                string help = JsonUtil.GetString(sf, "help", false);
                if (!string.IsNullOrWhiteSpace(help)) vals["help"] = help;

                string relation = JsonUtil.GetString(sf, "relation", false);
                if (!string.IsNullOrWhiteSpace(relation)) vals["relation"] = relation;

                string relationField = JsonUtil.GetString(sf, "relation_field", false);
                if (!string.IsNullOrWhiteSpace(relationField)) vals["relation_field"] = relationField;

                string related = JsonUtil.GetString(sf, "related", false);
                if (!string.IsNullOrWhiteSpace(related)) vals["related"] = related;

                string depends = JsonUtil.GetString(sf, "depends", false);
                if (!string.IsNullOrWhiteSpace(depends)) vals["depends"] = depends;

                string currencyField = JsonUtil.GetString(sf, "currency_field", false);
                if (!string.IsNullOrWhiteSpace(currencyField)) vals["currency_field"] = currencyField;

                string selection = JsonUtil.GetString(sf, "selection", false);
                if (!string.IsNullOrWhiteSpace(selection)) vals["selection"] = selection;

                try
                {
                    long newFieldId = clientProd.Create("ir.model.fields", vals);
                    if (syncedCompanionFields != null)
                    {
                        syncedCompanionFields.Add(JsonUtil.Dictionary(
                            "model", fieldModel,
                            "field", fName,
                            "ttype", vals["ttype"],
                            "field_id", newFieldId,
                            "action", "created_in_production"
                        ));
                    }
                }
                catch (Exception error)
                {
                    if (syncedCompanionFields != null)
                    {
                        syncedCompanionFields.Add(JsonUtil.Dictionary(
                            "model", fieldModel,
                            "field", fName,
                            "status", "failed",
                            "error", SafeError(error)
                        ));
                    }
                }
            }
        }

        private static string ReadReplicaFile(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) return string.Empty;
            string full = Path.GetFullPath(Path.Combine(root, relative));
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                return string.Empty;
            }
            return File.ReadAllText(full, Encoding.UTF8);
        }

        private static string LoadArchText(string root, Dictionary<string, object> view)
        {
            string archTemplate = JsonUtil.GetString(view, "arch_template", false);
            if (!string.IsNullOrWhiteSpace(archTemplate)) return archTemplate;
            string archFile = JsonUtil.GetString(view, "arch_file", false);
            if (!string.IsNullOrWhiteSpace(archFile)) return ReadReplicaFile(root, archFile);
            return string.Empty;
        }
    }
}
