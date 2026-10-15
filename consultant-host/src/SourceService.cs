using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace OdooConsultor
{
    public sealed class SourceService
    {
        private readonly ProjectStore project;
        private static readonly Regex QueryPattern = new Regex("^[^\\r\\n]{2,120}$", RegexOptions.Compiled);
        private static readonly Regex ModulePattern = new Regex("^[a-z0-9_]{1,80}$", RegexOptions.Compiled);
        private static readonly HashSet<string> PublicRepositories = new HashSet<string>(StringComparer.Ordinal) { "documentation", "community" };
        private static readonly HashSet<string> AllRepositories = new HashSet<string>(StringComparer.Ordinal) { "documentation", "community", "enterprise", "client" };
        private static readonly HashSet<string> SearchExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".md", ".rst", ".txt", ".py", ".xml", ".js", ".ts", ".csv" };

        public SourceService(ProjectStore store) { project = store; }

        public object Status(string version)
        {
            string series = NormalizeVersion(version);
            List<object> repositories = new List<object>();
            foreach (string name in new[] { "documentation", "community", "enterprise", "client" })
            {
                string path = RepositoryPath(name, series);
                bool configured = !string.IsNullOrWhiteSpace(path);
                bool installed = configured && Directory.Exists(path) && Directory.Exists(Path.Combine(path, ".git"));
                repositories.Add(JsonUtil.Dictionary(
                    "repository", name,
                    "tier", Tier(name),
                    "configured", configured,
                    "installed", installed,
                    "content_access", PublicRepositories.Contains(name) ? "allowed" : name == "enterprise" ? project.Config.enterprise_source_processing : project.Config.client_source_processing,
                    "path", configured ? path : null,
                    "commit", installed ? Git(path, new[] { "rev-parse", "HEAD" }, 30000, false).Trim() : null,
                    "synchronized_at", installed ? LastSync(name, series) : null));
            }
            return JsonUtil.Dictionary("version", series, "profile", project.Config.source_profile, "library_root", project.Config.source_library_root, "enterprise_processing", project.Config.enterprise_source_processing, "client_processing", project.Config.client_source_processing, "repositories", repositories.ToArray());
        }

        public object Synchronize(string version, bool includeEnterprise)
        {
            string series = NormalizeVersion(version);
            Directory.CreateDirectory(project.Config.source_library_root);
            List<object> results = new List<object>();
            results.Add(SynchronizeOne("documentation", series));
            results.Add(SynchronizeOne("community", series));
            if (includeEnterprise)
            {
                EnsureSourceAllowed("enterprise");
                results.Add(SynchronizeOne("enterprise", series));
            }
            string index = Path.Combine(project.Config.source_library_root, "index");
            Directory.CreateDirectory(index);
            JsonUtil.WriteJsonFile(Path.Combine(index, "catalog.json"), JsonUtil.Dictionary("schema_version", 1, "updated_at", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture), "repositories", results.ToArray()));
            AuditLog.Append(project, null, "fuentes_sincronizar", null, "success", "Fuentes oficiales sincronizadas: " + results.Count, 0);
            return JsonUtil.Dictionary("version", series, "synchronized", results.ToArray(), "note", "Los commits quedan fijados al crear el bloqueo de una investigación.");
        }

        public object CreateLock(string demoSlug, string version, bool includeEnterprise, bool includeClient)
        {
            string series = NormalizeVersion(version);
            List<object> entries = new List<object>();
            foreach (string name in new[] { "documentation", "community" }) entries.Add(LockEntry(name, series));
            if (includeEnterprise)
            {
                EnsureSourceAllowed("enterprise");
                entries.Add(LockEntry("enterprise", series));
            }
            if (includeClient)
            {
                EnsureSourceAllowed("client");
                entries.Add(LockEntry("client", series));
            }
            Dictionary<string, object> sourceLock = JsonUtil.Dictionary(
                "schema_version", 1,
                "client_slug", project.Config.client_slug,
                "odoo_version", series,
                "created_at", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                "repositories", entries.ToArray(),
                "trust_policy", new object[] { "verificado_base", "oficial_documentacion", "oficial_codigo", "codigo_cliente", "comunidad_no_verificada" });
            sourceLock["lock_hash"] = JsonUtil.Sha256(JsonUtil.Serialize(sourceLock));
            string directory = project.DemoDirectory(demoSlug, true);
            JsonUtil.WriteJsonFile(Path.Combine(directory, "source-lock.json"), sourceLock);
            AuditLog.Append(project, null, "fuentes_crear_bloqueo", null, "success", "Bloqueo de fuentes creado", 0);
            return sourceLock;
        }

        public object Search(string repository, string version, string query, string module, string symbol, int limit)
        {
            string name = (repository ?? string.Empty).Trim().ToLowerInvariant();
            if (!AllRepositories.Contains(name)) throw new ConsultantException("source_repository_not_allowed", "El repositorio no pertenece al catálogo registrado.");
            if (!PublicRepositories.Contains(name)) EnsureSourceAllowed(name);
            string series = NormalizeVersion(version);
            if (!QueryPattern.IsMatch(query ?? string.Empty) || (query ?? string.Empty).Contains("..")) throw new ConsultantException("invalid_source_query", "La búsqueda debe ser un término acotado, sin saltos de línea ni recorridos de ruta.");
            if (!string.IsNullOrWhiteSpace(module) && !ModulePattern.IsMatch(module)) throw new ConsultantException("invalid_source_module", "El módulo indicado no es válido.");
            if (!string.IsNullOrWhiteSpace(symbol) && !QueryPattern.IsMatch(symbol)) throw new ConsultantException("invalid_source_symbol", "El símbolo indicado no es válido.");
            limit = Math.Max(1, Math.Min(limit, 25));
            string root = RepositoryPath(name, series);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(Path.Combine(root, ".git"))) throw new ConsultantException("source_not_synchronized", "La fuente solicitada no está sincronizada.");
            string commit = Git(root, new[] { "rev-parse", "HEAD" }, 30000, false).Trim();
            string needle = string.IsNullOrWhiteSpace(symbol) ? query.Trim() : symbol.Trim();
            List<object> matches = new List<object>();
            foreach (string file in EnumerateSafeFiles(root, module))
            {
                if (matches.Count >= limit) break;
                int lineNumber = 0;
                try
                {
                    foreach (string line in File.ReadLines(file, Encoding.UTF8))
                    {
                        lineNumber++;
                        if (line.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0 && line.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) < 0) continue;
                        matches.Add(JsonUtil.Dictionary("tier", Tier(name), "repository", name, "version", series, "commit", commit, "path", RelativePath(root, file), "line", lineNumber, "excerpt", Redactor.Clean(line.Trim()).Substring(0, Math.Min(400, Redactor.Clean(line.Trim()).Length))));
                        if (matches.Count >= limit) break;
                    }
                }
                catch (IOException) { }
            }
            return JsonUtil.Dictionary("query", query.Trim(), "repository", name, "tier", Tier(name), "version", series, "commit", commit, "results", matches.ToArray(), "interpretation_rule", name == "documentation" ? "Comportamiento previsto para la versión." : name == "client" ? "Código privado autorizado; debe validarse en la base." : "Implementación oficial; no prueba que el commit esté desplegado.");
        }

        public object Identify(string environment)
        {
            ConnectionProfile profile = project.GetProfile(environment, true);
            Dictionary<string, object> detected = OdooClient.DetectInstance(profile.base_url, 30000);
            string series = Convert.ToString(detected["server_series"], CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(series) || Convert.ToString(detected["transport"], CultureInfo.InvariantCulture) == "unsupported") throw new ConsultantException("unsupported_odoo_version", "Solo se admiten versiones de Odoo de la 14 a la 19.");
            if (!string.Equals(series, project.Config.odoo_version, StringComparison.Ordinal)) throw new ConsultantException("odoo_version_mismatch", "La base es Odoo " + series + " pero proyecto.yaml declara " + project.Config.odoo_version + ".");
            detected["environment"] = profile.environment;
            detected["edition_declared"] = project.Config.odoo_edition;
            detected["hosting_declared"] = project.Config.hosting;
            detected["database_configured"] = !string.IsNullOrWhiteSpace(profile.database);
            AuditLog.Append(project, profile.environment, "odoo_identificar_instancia", null, "success", "Instancia identificada como Odoo " + series, 0);
            return detected;
        }

        private object SynchronizeOne(string name, string series)
        {
            string path = RepositoryPath(name, series);
            string url = RepositoryUrl(name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (!Directory.Exists(path)) Git(Path.GetDirectoryName(path), new[] { "clone", "--branch", series, "--single-branch", url, path }, 900000, true);
            else
            {
                if (!Directory.Exists(Path.Combine(path, ".git"))) throw new ConsultantException("invalid_source_checkout", "La ruta registrada existe pero no es un checkout Git.");
                string dirty = Git(path, new[] { "status", "--porcelain" }, 30000, false);
                if (!string.IsNullOrWhiteSpace(dirty)) throw new ConsultantException("source_checkout_dirty", "La fuente " + name + " tiene cambios locales; no se actualizará.");
                Git(path, new[] { "pull", "--ff-only" }, 900000, true);
            }
            string stamp = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            string index = Path.Combine(project.Config.source_library_root, "index");
            Directory.CreateDirectory(index);
            File.WriteAllText(Path.Combine(index, name + "-" + series + ".sync"), stamp, new UTF8Encoding(false));
            return JsonUtil.Dictionary("repository", name, "version", series, "commit", Git(path, new[] { "rev-parse", "HEAD" }, 30000, false).Trim(), "synchronized_at", stamp);
        }

        private object LockEntry(string name, string series)
        {
            string path = RepositoryPath(name, series);
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(Path.Combine(path, ".git"))) throw new ConsultantException("source_not_synchronized", "Debes sincronizar " + name + " antes de crear el bloqueo.");
            return JsonUtil.Dictionary("repository", name, "tier", Tier(name), "version", series, "commit", Git(path, new[] { "rev-parse", "HEAD" }, 30000, false).Trim(), "path", path);
        }

        private string RepositoryPath(string name, string series)
        {
            if (name == "client") return string.IsNullOrWhiteSpace(project.Config.custom_source_path) ? null : Path.GetFullPath(project.Config.custom_source_path);
            return Path.Combine(project.Config.source_library_root, name, series);
        }

        private static string RepositoryUrl(string name)
        {
            if (name == "documentation") return "https://github.com/odoo/documentation.git";
            if (name == "community") return "https://github.com/odoo/odoo.git";
            if (name == "enterprise") return "https://github.com/odoo/enterprise.git";
            throw new ConsultantException("source_repository_not_allowed", "No existe URL registrada para esta fuente.");
        }

        private static string Tier(string name)
        {
            if (name == "documentation") return "oficial_documentacion";
            if (name == "client") return "codigo_cliente";
            return "oficial_codigo";
        }

        private void EnsureSourceAllowed(string name)
        {
            string policy = name == "enterprise" ? project.Config.enterprise_source_processing : project.Config.client_source_processing;
            if (string.Equals(policy, "allowed", StringComparison.Ordinal)) return;
            if (name == "enterprise" && string.Equals(policy, "unavailable", StringComparison.Ordinal)) throw new ConsultantException("enterprise_source_unavailable", "El repositorio Enterprise no está disponible. Esto no bloquea los flujos cerrados basados en la base real, documentación y Community.");
            throw new ConsultantException("private_source_blocked", "El contenido de " + (name == "enterprise" ? "Enterprise" : "código del cliente") + " no puede procesarse hasta que su política separada sea allowed.");
        }

        private string NormalizeVersion(string version)
        {
            string value = string.IsNullOrWhiteSpace(version) ? project.Config.odoo_version : version.Trim();
            if (value == "17") value = "17.0";
            if (value == "18") value = "18.0";
            if (value == "19") value = "19.0";
            if (value == "14" || value == "14.0" || value == "15" || value == "15.0" || value == "16" || value == "16.0")
                throw new ConsultantException("unsupported_source_version", "No se tiene la documentación oficial para Odoo " + value + ". Solicita que se amplíe la documentación para esta versión o consulta una versión disponible (17.0, 18.0 o 19.0).");
            if (value != "17.0" && value != "18.0" && value != "19.0") throw new ConsultantException("unsupported_odoo_version", "Las fuentes oficiales admiten versiones 17.0, 18.0 o 19.0.");
            return value;
        }

        private static IEnumerable<string> EnumerateSafeFiles(string root, string module)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string full = Path.GetFullPath(file);
                if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || full.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar)) continue;
                if (!SearchExtensions.Contains(Path.GetExtension(full))) continue;
                string relative = RelativePath(root, full).Replace('\\', '/');
                if (!string.IsNullOrWhiteSpace(module) && relative.IndexOf("/" + module + "/", StringComparison.OrdinalIgnoreCase) < 0 && !relative.StartsWith(module + "/", StringComparison.OrdinalIgnoreCase)) continue;
                FileInfo info = new FileInfo(full);
                if (info.Length <= 2 * 1024 * 1024) yield return full;
            }
        }

        private static string RelativePath(string root, string file)
        {
            Uri rootUri = new Uri(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(new Uri(Path.GetFullPath(file))).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private string LastSync(string name, string series)
        {
            string stamp = Path.Combine(project.Config.source_library_root, "index", name + "-" + series + ".sync");
            return File.Exists(stamp) ? File.ReadAllText(stamp, Encoding.UTF8).Trim() : null;
        }

        private static string Git(string workingDirectory, string[] args, int timeoutMs, bool includeOutputOnError)
        {
            ProcessStartInfo start = new ProcessStartInfo(ResolveGitExecutable());
            start.WorkingDirectory = workingDirectory;
            start.UseShellExecute = false;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.CreateNoWindow = true;
            start.Arguments = string.Join(" ", args.Select(QuoteArgument).ToArray());
            using (Process process = Process.Start(start))
            {
                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeoutMs)) { try { process.Kill(); } catch { } throw new ConsultantException("source_sync_timeout", "Git excedió el tiempo permitido."); }
                Task.WaitAll(new Task[] { outputTask, errorTask }, 30000);
                string output = outputTask.Result;
                string error = errorTask.Result;
                if (process.ExitCode != 0) throw new ConsultantException("source_git_failed", includeOutputOnError ? "Git no pudo sincronizar la fuente: " + Redactor.Clean(error) : "Git no pudo consultar la fuente registrada.");
                return output;
            }
        }

        private static string ResolveGitExecutable()
        {
            string profileRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] candidates = new[]
            {
                Path.Combine(profileRoot, ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "native", "git", "cmd", "git.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "cmd", "git.exe")
            };
            foreach (string candidate in candidates) if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return candidate;
            return "git";
        }

        private static string QuoteArgument(string value)
        {
            string safe = value ?? string.Empty;
            if (safe.IndexOfAny(new[] { ' ', '\t', '\"' }) < 0) return safe;
            return "\"" + safe.Replace("\"", "\\\"") + "\"";
        }
    }
}
