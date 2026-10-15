using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace OdooConsultor
{
    public static class JsonUtil
    {
        private static JavaScriptSerializer NewSerializer()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = 8 * 1024 * 1024;
            serializer.RecursionLimit = 100;
            return serializer;
        }

        public static string SanitizeMojibake(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.IndexOf('\u00C3') < 0 && text.IndexOf('\u00C2') < 0) return text;

            return text
                // Double mojibake
                .Replace("\u00C3\u0083\u00C2\u00A1", "\u00E1") // á
                .Replace("\u00C3\u0083\u00C2\u00A9", "\u00E9") // é
                .Replace("\u00C3\u0083\u00C2\u00AD", "\u00ED") // í
                .Replace("\u00C3\u0083\u00C2\u00B3", "\u00F3") // ó
                .Replace("\u00C3\u0083\u00C2\u00BA", "\u00FA") // ú
                .Replace("\u00C3\u0083\u00C2\u00B1", "\u00F1") // ñ
                .Replace("\u00C3\u0083\u00C2\u0081", "\u00C1") // Á
                .Replace("\u00C3\u0083\u00C2\u0089", "\u00C9") // É
                .Replace("\u00C3\u0083\u00C2\u008D", "\u00CD") // Í
                .Replace("\u00C3\u0083\u00C2\u0093", "\u00D3") // Ó
                .Replace("\u00C3\u0083\u00C2\u009A", "\u00DA") // Ú
                .Replace("\u00C3\u0083\u00C2\u0091", "\u00D1") // Ñ
                .Replace("\u00C3\u0083\u00C2\u00BC", "\u00FC") // ü
                .Replace("\u00C3\u0083\u00C2\u009C", "\u00DC") // Ü
                // Single mojibake (CP1252 / ISO-8859-1 decoding of UTF-8)
                .Replace("\u00C3\u00A1", "\u00E1") // á
                .Replace("\u00C3\u00A9", "\u00E9") // é
                .Replace("\u00C3\u00AD", "\u00ED") // í
                .Replace("\u00C3\u00B3", "\u00F3") // ó
                .Replace("\u00C3\u00BA", "\u00FA") // ú
                .Replace("\u00C3\u00B1", "\u00F1") // ñ
                .Replace("\u00C3\u0081", "\u00C1") // Á
                .Replace("\u00C3\u0089", "\u00C9") // É
                .Replace("\u00C3\u008D", "\u00CD") // Í
                .Replace("\u00C3\u0093", "\u00D3") // Ó
                .Replace("\u00C3\u009A", "\u00DA") // Ú
                .Replace("\u00C3\u0091", "\u00D1") // Ñ
                .Replace("\u00C3\u00BC", "\u00FC") // ü
                .Replace("\u00C3\u009C", "\u00DC") // Ü
                .Replace("\u00C2\u00BF", "\u00BF") // ¿
                .Replace("\u00C2\u00A1", "\u00A1") // ¡
                .Replace("\u00C2\u00B0", "\u00B0"); // °
        }

        public static string EscapeNonAscii(string json)
        {
            if (string.IsNullOrEmpty(json)) return json;
            bool hasNonAscii = false;
            for (int i = 0; i < json.Length; i++)
            {
                if (json[i] > 127) { hasNonAscii = true; break; }
            }
            if (!hasNonAscii) return json;

            StringBuilder sb = new StringBuilder(json.Length + 64);
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c > 127)
                {
                    sb.Append("\\u");
                    sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        public static string XmlNumericEntities(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return xml;
            string healed = SanitizeMojibake(xml);
            bool hasNonAscii = false;
            for (int i = 0; i < healed.Length; i++)
            {
                if (healed[i] > 127) { hasNonAscii = true; break; }
            }
            if (!hasNonAscii) return healed;

            StringBuilder sb = new StringBuilder(healed.Length + 64);
            for (int i = 0; i < healed.Length; i++)
            {
                char c = healed[i];
                if (c > 127)
                {
                    sb.Append("&#");
                    sb.Append(((int)c).ToString(CultureInfo.InvariantCulture));
                    sb.Append(';');
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        public static string Serialize(object value)
        {
            string serialized = NewSerializer().Serialize(value);
            return EscapeNonAscii(SanitizeMojibake(serialized));
        }

        public static string SerializePretty(object value)
        {
            string compact = Serialize(value);
            StringBuilder result = new StringBuilder();
            bool quoted = false;
            bool escaped = false;
            int indent = 0;
            for (int i = 0; i < compact.Length; i++)
            {
                char current = compact[i];
                if (quoted)
                {
                    result.Append(current);
                    if (escaped) escaped = false;
                    else if (current == '\\') escaped = true;
                    else if (current == '"') quoted = false;
                    continue;
                }
                if (current == '"')
                {
                    quoted = true;
                    result.Append(current);
                }
                else if (current == '{' || current == '[')
                {
                    result.Append(current).AppendLine();
                    indent++;
                    result.Append(new string(' ', indent * 2));
                }
                else if (current == '}' || current == ']')
                {
                    result.AppendLine();
                    indent--;
                    result.Append(new string(' ', indent * 2)).Append(current);
                }
                else if (current == ',')
                {
                    result.Append(current).AppendLine();
                    result.Append(new string(' ', indent * 2));
                }
                else if (current == ':')
                {
                    result.Append(": ");
                }
                else if (!char.IsWhiteSpace(current))
                {
                    result.Append(current);
                }
            }
            return result.ToString();
        }

        public static T Deserialize<T>(string text)
        {
            return NewSerializer().Deserialize<T>(SanitizeMojibake(text));
        }

        public static Dictionary<string, object> DeserializeObject(string text)
        {
            return NewSerializer().Deserialize<Dictionary<string, object>>(SanitizeMojibake(text));
        }

        public static void WriteJsonFile<T>(string path, T value)
        {
            string parent = Path.GetDirectoryName(path);
            if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, SerializePretty(value) + Environment.NewLine, new UTF8Encoding(false));
            try
            {
                if (!File.Exists(path)) File.Move(temporary, path);
                else
                {
                    bool replaced = false;
                    for (int attempt = 0; attempt < 3 && !replaced; attempt++)
                    {
                        try { File.Replace(temporary, path, null); replaced = true; }
                        catch (IOException) { if (attempt == 2) break; Thread.Sleep(25 * (attempt + 1)); }
                        catch (UnauthorizedAccessException) { break; }
                    }
                    if (!replaced)
                    {
                        File.Copy(temporary, path, true);
                        File.Delete(temporary);
                    }
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public static T ReadJsonFile<T>(string path)
        {
            if (!File.Exists(path)) throw new ConsultantException("file_not_found", "No existe el archivo requerido: " + Path.GetFileName(path));
            return Deserialize<T>(File.ReadAllText(path, Encoding.UTF8));
        }

        public static string Sha256(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
                byte[] hash = sha.ComputeHash(bytes);
                Array.Clear(bytes, 0, bytes.Length);
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        public static string CanonicalHash(object value)
        {
            return Sha256(Canonicalize(value));
        }

        public static string Canonicalize(object value)
        {
            if (value == null) return "null";
            IDictionary dictionary = value as IDictionary;
            if (dictionary != null)
            {
                List<string> keys = new List<string>();
                foreach (object key in dictionary.Keys) keys.Add(Convert.ToString(key, CultureInfo.InvariantCulture));
                keys.Sort(StringComparer.Ordinal);
                return "{" + string.Join(",", keys.Select(delegate(string key)
                {
                    return Serialize(key) + ":" + Canonicalize(dictionary[key]);
                }).ToArray()) + "}";
            }
            IEnumerable enumerable = value as IEnumerable;
            if (enumerable != null && !(value is string))
            {
                List<string> parts = new List<string>();
                foreach (object item in enumerable) parts.Add(Canonicalize(item));
                return "[" + string.Join(",", parts.ToArray()) + "]";
            }
            if (value is bool) return ((bool)value) ? "true" : "false";
            if (value is string || value is char || value is DateTime) return Serialize(value);
            if (value is IFormattable) return ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);

            string serialized = Serialize(value);
            object normalized = NewSerializer().DeserializeObject(serialized);
            return Canonicalize(normalized);
        }

        public static Dictionary<string, object> Dictionary(params object[] keyValues)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i < keyValues.Length; i += 2)
            {
                result[Convert.ToString(keyValues[i], CultureInfo.InvariantCulture)] = keyValues[i + 1];
            }
            return result;
        }

        public static string GetString(IDictionary<string, object> source, string key, bool required)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null || (value is bool) || string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture)))
            {
                if (required) throw new ConsultantException("invalid_input", "Falta el parámetro " + key + ".");
                return null;
            }
            string str = Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
            if (string.Equals(str, "false", StringComparison.OrdinalIgnoreCase))
            {
                if (required) throw new ConsultantException("invalid_input", "Falta el parámetro " + key + ".");
                return null;
            }
            return str;
        }

        public static long GetLong(IDictionary<string, object> source, string key, bool required)
        {
            object value;
            long parsed;
            if (source != null && source.TryGetValue(key, out value) && value != null && long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return parsed;
            if (required) throw new ConsultantException("invalid_input", key + " debe ser un entero.");
            return 0;
        }

        public static bool GetBool(IDictionary<string, object> source, string key, bool fallback)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null) return fallback;
            bool parsed;
            return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) ? parsed : fallback;
        }
    }
}
