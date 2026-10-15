using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace OdooConsultor
{
    public sealed class DemoAssetDescriptor
    {
        public string relative_file { get; set; }
        public string absolute_file { get; set; }
        public string sha256 { get; set; }
        public string mimetype { get; set; }
        public long size { get; set; }
        public string alt { get; set; }
    }

    public sealed class MailEditorValidation
    {
        public bool editor_ready { get; set; }
        public string contract { get; set; }
        public string personalization_variable { get; set; }
        public int snippet_count { get; set; }
        public bool internal_image { get; set; }
        public string error_code { get; set; }
    }

    public static class MailEditorContract
    {
        // Evidencia oficial fijada: addons/mass_mailing de Odoo Community 18.0.
        public const string CommunityCommit = "d9fae40571c4f10c17fe00efc087cb25b30b85ab";
        public const string ContractVersion = "odoo-community-18-mail-editor/1";
        public const string AssetPlaceholderPrefix = "{{DEMO_ASSET_URL:";
        public const int MaximumAssetBytes = 2 * 1024 * 1024;

        private static readonly HashSet<string> Snippets = new HashSet<string>(new[]
        {
            "s_text_block", "s_title", "s_image_text", "s_button", "s_hr",
            "s_mail_block_header_logo", "s_mail_block_footer_social"
        }, StringComparer.Ordinal);

        public static string Build(string tag, string headline, string body, string cta, string variant, string assetKey, bool imageRequired, string personalizationVariable, string imageAlt)
        {
            ValidateCommercialText(headline, "headline");
            ValidateCommercialText(body, "body");
            ValidateCommercialText(cta, "cta_label");
            string variable = NormalizePersonalization(personalizationVariable);
            string greeting = variable == "recipient_name" ? "<p class=\"o_default_snippet_text\">Hola {{ object.name or '' }},</p>" : string.Empty;
            string image = string.Empty;
            if (imageRequired)
            {
                if (string.IsNullOrWhiteSpace(assetKey)) throw new ConsultantException("demo_asset_required", "El mailing exige una imagen interna declarada.");
                image = "<div class=\"o_mail_block_image_text\" data-snippet=\"s_image_text\" data-name=\"Image - Text\"><div class=\"o_mail_snippet_general\" style=\"margin:0 auto;max-width:600px;width:100%;background-color:#ffffff\"><div class=\"container\" style=\"padding:20px;width:100%\"><img src=\"" + Escape(AssetPlaceholderPrefix + assetKey + "}}") + "\" alt=\"" + Escape(imageAlt) + "\" style=\"display:block;width:100%;height:auto;border:0\" /></div></div></div>";
            }
            string html =
                "<div class=\"o_layout o_default_theme oe_unremovable oe_unmovable\" data-name=\"Mailing\" data-variant=\"" + Escape(variant) + "\">" +
                "<style id=\"design-element\">h2{font-weight:700;color:#243746}p,p&gt;*,li,li&gt;*{color:#5d6770}.btn{background:#714b67;color:#fff}</style>" +
                "<div class=\"container o_mail_wrapper oe_unremovable oe_unmovable\" style=\"border-collapse:collapse\"><div class=\"row\"><div class=\"col o_mail_no_options o_mail_wrapper_td bg-white oe_structure o_editable\" style=\"text-align:left;width:100%\">" +
                image +
                "<div class=\"o_mail_block_text\" data-snippet=\"s_text_block\" data-name=\"Text\"><div class=\"o_mail_snippet_general\" style=\"margin:0 auto;max-width:600px;width:100%;background-color:#ffffff\"><div class=\"container\" style=\"padding:28px 24px;width:100%\">" +
                greeting + "<h2 class=\"o_default_snippet_text\">" + Escape(headline) + "</h2><p class=\"o_default_snippet_text\">" + Escape(body) + "</p></div></div></div>" +
                "<div class=\"s_button text-center pt16 pb32\" data-snippet=\"s_button\" data-name=\"Button\"><a href=\"https://example.invalid/conocer-mas\" class=\"btn btn-primary\">" + Escape(cta) + "</a></div>" +
                "<div class=\"o_mail_block_text\" data-snippet=\"s_text_block\" data-name=\"Footer\"><div class=\"o_mail_snippet_general\"><p class=\"o_default_snippet_text\"><small>Mensaje ficticio. No enviar. <a href=\"/unsubscribe_from_list\">Cancelar suscripción</a></small></p></div></div>" +
                "</div></div></div></div>";
            RequireValid(html, imageRequired, variable, true);
            return html;
        }

        public static MailEditorValidation Validate(string html, bool imageRequired, string personalizationVariable, bool allowAssetPlaceholder)
        {
            MailEditorValidation result = new MailEditorValidation { contract = ContractVersion, personalization_variable = NormalizePersonalization(personalizationVariable) };
            try
            {
                if (string.IsNullOrWhiteSpace(html)) throw new ConsultantException("mail_editor_body_invalid", "El cuerpo del mailing está vacío.");
                XmlDocument document = new XmlDocument { XmlResolver = null };
                document.LoadXml("<root>" + html + "</root>");
                XmlElement layout = Elements(document).FirstOrDefault(x => HasClass(x, "o_layout"));
                if (layout == null) throw new ConsultantException("mail_editor_layout_missing", "Falta el contenedor .o_layout requerido por el diseñador.");
                XmlElement style = Elements(document).FirstOrDefault(x => x.Name == "style" && x.GetAttribute("id") == "design-element");
                if (style == null || string.IsNullOrWhiteSpace(style.InnerText)) throw new ConsultantException("mail_editor_design_element_missing", "Falta #design-element con CSS; Odoo produciría null.textContent.");
                if (!Elements(layout).Any(x => HasClass(x, "oe_structure"))) throw new ConsultantException("mail_editor_structure_missing", "Falta la zona editable .oe_structure.");
                List<XmlElement> snippets = Elements(layout).Where(x => x.HasAttribute("data-snippet")).ToList();
                if (snippets.Count == 0 || snippets.Any(x => !Snippets.Contains(x.GetAttribute("data-snippet")) || string.IsNullOrWhiteSpace(x.GetAttribute("data-name")))) throw new ConsultantException("mail_editor_snippet_invalid", "El cuerpo necesita bloques con data-snippet y data-name del catálogo cerrado.");
                if (Elements(document).Any(x => x.Name == "script" || x.Name == "object" || x.Name == "iframe" || x.Name == "form")) throw new ConsultantException("mail_editor_unsafe_node", "El cuerpo incluye un nodo ejecutable no permitido.");
                ValidatePersonalization(html, result.personalization_variable);
                List<string> sources = Elements(document).Where(x => x.Name == "img").Select(x => x.GetAttribute("src")).ToList();
                if (imageRequired && sources.Count == 0) throw new ConsultantException("demo_asset_required", "El diseño exige una imagen editable.");
                foreach (string source in sources)
                {
                    bool placeholder = allowAssetPlaceholder && source.StartsWith(AssetPlaceholderPrefix, StringComparison.Ordinal) && source.EndsWith("}}", StringComparison.Ordinal);
                    bool internalUrl = Regex.IsMatch(source ?? string.Empty, "^/web/content/[1-9][0-9]*\\?download=false$");
                    if (!placeholder && !internalUrl) throw new ConsultantException("demo_external_image_not_allowed", "La imagen del mailing debe usar el recurso interno protegido de Odoo.");
                    if (source.IndexOf("example.invalid", StringComparison.OrdinalIgnoreCase) >= 0) throw new ConsultantException("demo_external_image_not_allowed", "Una imagen obligatoria no puede usar un host ficticio.");
                    result.internal_image = internalUrl;
                }
                result.snippet_count = snippets.Count;
                result.editor_ready = true;
                return result;
            }
            catch (ConsultantException error) { result.error_code = error.Code; return result; }
            catch (XmlException) { result.error_code = "mail_editor_body_invalid"; return result; }
        }

        public static void RequireValid(string html, bool imageRequired, string personalizationVariable, bool allowAssetPlaceholder)
        {
            MailEditorValidation result = Validate(html, imageRequired, personalizationVariable, allowAssetPlaceholder);
            if (!result.editor_ready) throw new ConsultantException(result.error_code ?? "mail_editor_body_invalid", "El cuerpo no puede inicializar de forma segura el editor nativo de Email Marketing.");
        }

        public static DemoAssetDescriptor ValidateAsset(string demoDirectory, IDictionary<string, object> configuration)
        {
            if (configuration == null) throw new ConsultantException("demo_asset_required", "Falta image_asset para la imagen requerida.");
            string file = JsonUtil.GetString(configuration, "file", true);
            string expectedHash = JsonUtil.GetString(configuration, "sha256", true).ToLowerInvariant();
            string alt = JsonUtil.GetString(configuration, "alt", true).Trim();
            if (Path.GetFileName(file) != file || file.IndexOf("..", StringComparison.Ordinal) >= 0) throw new ConsultantException("demo_asset_path_invalid", "La imagen debe ser un nombre de archivo dentro de assets.");
            if (!Regex.IsMatch(expectedHash, "^[a-f0-9]{64}$") || alt.Length == 0 || alt.Length > 180) throw new ConsultantException("demo_asset_invalid", "Hash o texto alternativo inválido.");
            string assetsRoot = Path.GetFullPath(Path.Combine(demoDirectory, "assets"));
            string path = Path.GetFullPath(Path.Combine(assetsRoot, file));
            if (!path.StartsWith(assetsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) throw new ConsultantException("demo_asset_missing", "La imagen no existe dentro de demostraciones/<slug>/assets.");
            FileInfo info = new FileInfo(path);
            if (info.Length < 16 || info.Length > MaximumAssetBytes) throw new ConsultantException("demo_asset_size_invalid", "La imagen debe pesar entre 16 bytes y 2 MiB.");
            byte[] bytes = File.ReadAllBytes(path);
            string mime = DetectMime(file, bytes);
            string actualHash;
            using (SHA256 sha = SHA256.Create()) actualHash = string.Concat(sha.ComputeHash(bytes).Select(x => x.ToString("x2", CultureInfo.InvariantCulture)).ToArray());
            Array.Clear(bytes, 0, bytes.Length);
            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal)) throw new ConsultantException("demo_asset_hash_changed", "El SHA-256 de la imagen no coincide.");
            return new DemoAssetDescriptor { relative_file = Path.Combine("assets", file).Replace('\\', '/'), absolute_file = path, sha256 = actualHash, mimetype = mime, size = info.Length, alt = alt };
        }

        public static string ResolveAssetUrl(string html, string assetKey, long attachmentId)
        {
            if (attachmentId <= 0) throw new ConsultantException("demo_dependency_missing", "No existe el recurso visual protegido.");
            string token = AssetPlaceholderPrefix + assetKey + "}}";
            if (html.IndexOf(token, StringComparison.Ordinal) < 0) throw new ConsultantException("demo_asset_placeholder_missing", "El mailing no referencia su recurso visual declarado.");
            return html.Replace(token, "/web/content/" + attachmentId.ToString(CultureInfo.InvariantCulture) + "?download=false");
        }

        public static int SelfTest()
        {
            int passed = 0;
            string valid = Build("ABC12345", "Una propuesta", "Contenido visual", "Conocer más", "A", "mailing_visual", true, "recipient_name", "Imagen sintética");
            MailEditorValidation prepared = Validate(valid, true, "recipient_name", true);
            if (!prepared.editor_ready || prepared.snippet_count < 1 || valid.IndexOf("id=\"design-element\"", StringComparison.Ordinal) < 0 || valid.IndexOf("class=\"o_layout", StringComparison.Ordinal) < 0 || valid.IndexOf("oe_structure", StringComparison.Ordinal) < 0) throw new Exception("Contrato DOM válido incompleto.");
            passed++;
            string resolved = ResolveAssetUrl(valid, "mailing_visual", 77);
            if (!Validate(resolved, true, "recipient_name", false).editor_ready || resolved.Contains("example.invalid/imagen")) throw new Exception("La imagen interna no quedó resuelta.");
            passed++;
            if (Regex.Matches(valid, "Hola ").Count != 1 || Regex.Matches(valid, "\\{\\{").Count != 2 || valid.Contains("{{ recipient_name }}")) throw new Exception("Personalización duplicada o mezclada.");
            passed++;
            if (Validate("<div><p>Plano</p></div>", false, "none", false).editor_ready) throw new Exception("El cuerpo plano fue aceptado.");
            passed++;
            if (Validate(valid.Replace("<style id=\"design-element\">", "<style id=\"other\">"), true, "recipient_name", true).error_code != "mail_editor_design_element_missing") throw new Exception("La regresión null.textContent no quedó cubierta.");
            passed++;
            bool mixed = false; try { RequireValid(valid.Replace("Contenido visual", "{{ recipient_name }}"), true, "recipient_name", true); } catch (ConsultantException error) { mixed = error.Code == "mail_personalization_mixed"; }
            if (!mixed) throw new Exception("La mezcla de variables fue aceptada.");
            passed++;
            if (valid.IndexOf("[DEMO:", StringComparison.OrdinalIgnoreCase) >= 0 || valid.IndexOf("DEMO 1", StringComparison.OrdinalIgnoreCase) >= 0 || Regex.IsMatch(valid, "demo-[a-f0-9]{8,}", RegexOptions.IgnoreCase)) throw new Exception("El cuerpo conserva un marcador visible de infraestructura.");
            passed++;
            return passed;
        }

        private static IEnumerable<XmlElement> Elements(XmlNode node)
        {
            foreach (XmlNode child in node.ChildNodes)
            {
                XmlElement element = child as XmlElement;
                if (element == null) continue;
                yield return element;
                foreach (XmlElement nested in Elements(element)) yield return nested;
            }
        }

        private static bool HasClass(XmlElement element, string expected)
        {
            return (element.GetAttribute("class") ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Contains(expected);
        }

        private static string NormalizePersonalization(string value)
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
            if (normalized != "none" && normalized != "recipient_name") throw new ConsultantException("mail_personalization_invalid", "La personalización solo admite none o recipient_name.");
            return normalized;
        }

        private static void ValidateCommercialText(string value, string label)
        {
            if ((value ?? string.Empty).IndexOf("{{", StringComparison.Ordinal) >= 0 || (value ?? string.Empty).IndexOf("}}", StringComparison.Ordinal) >= 0) throw new ConsultantException("mail_personalization_literal_blocked", "Los textos comerciales no aceptan variables literales en " + label + ".");
        }

        private static void ValidatePersonalization(string html, string variable)
        {
            int objectTokens = Regex.Matches(html, Regex.Escape("{{ object.name or '' }}")).Count;
            int recipientTokens = Regex.Matches(html, Regex.Escape("{{ recipient_name }}")).Count;
            int allTokens = Regex.Matches(html, "\\{\\{").Count;
            if (recipientTokens > 0 || objectTokens > 0 && recipientTokens > 0) throw new ConsultantException("mail_personalization_mixed", "El cuerpo mezcla sintaxis de personalización.");
            int assetTokens = Regex.Matches(html, Regex.Escape(AssetPlaceholderPrefix)).Count;
            int expectedObjectTokens = variable == "recipient_name" ? 1 : 0;
            if (objectTokens != expectedObjectTokens || allTokens != objectTokens + assetTokens) throw new ConsultantException("mail_personalization_invalid", "El cuerpo contiene variables no soportadas o sin resolver.");
            if (Regex.Matches(html, "Hola ", RegexOptions.IgnoreCase).Count != expectedObjectTokens) throw new ConsultantException("mail_greeting_duplicate", "El mailing debe tener un único saludo generado por el motor.");
        }

        private static string DetectMime(string file, byte[] bytes)
        {
            string extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension == ".png" && bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4e && bytes[3] == 0x47) return "image/png";
            if ((extension == ".jpg" || extension == ".jpeg") && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[bytes.Length - 2] == 0xff && bytes[bytes.Length - 1] == 0xd9) return "image/jpeg";
            if (extension == ".webp" && bytes.Length >= 12 && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP") return "image/webp";
            throw new ConsultantException("demo_asset_type_invalid", "Solo se admiten PNG, JPEG o WebP cuyo contenido coincida con su extensión.");
        }

        private static string Escape(string value) { return SecurityElement.Escape(value ?? string.Empty) ?? string.Empty; }
    }
}
