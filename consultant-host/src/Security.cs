using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace OdooConsultor
{
    public static class Redactor
    {
        private static readonly Regex Bearer = new Regex("(?i)(bearer\\s+)[^\\s\\\"']+", RegexOptions.Compiled);
        private static readonly Regex BasicAuth = new Regex("(?i)(basic\\s+)[A-Za-z0-9+/=]+", RegexOptions.Compiled);
        private static readonly Regex SecretField = new Regex("(?i)(api[_ -]?key|token|secret|password|credencial)(\\s*[:=]\\s*)(?:\"[^\"]*\"|'[^']*'|[^,;\\s&]+)", RegexOptions.Compiled);

        public static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            string clean = Bearer.Replace(value, "$1[REDACTADO]");
            clean = BasicAuth.Replace(clean, "$1[REDACTADO]");
            clean = SecretField.Replace(clean, "$1$2[REDACTADO]");
            return clean.Length > 1200 ? clean.Substring(0, 1200) + "…" : clean;
        }

        public static string SafeError(Exception error)
        {
            if (error == null) return string.Empty;
            ConsultantException known = error as ConsultantException;
            return Clean((known == null ? "internal_error" : known.Code) + ": " + error.Message);
        }
    }

    public static class SecretPrompt
    {
        public static string Show(string title, string message)
        {
            using (Form form = new Form())
            using (Label label = new Label())
            using (TextBox input = new TextBox())
            using (Button ok = new Button())
            using (Button cancel = new Button())
            {
                form.Text = title;
                form.Width = 520;
                form.Height = 180;
                form.StartPosition = FormStartPosition.CenterScreen;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                label.Left = 16;
                label.Top = 16;
                label.Width = 470;
                label.Height = 36;
                label.Text = message;
                input.Left = 16;
                input.Top = 58;
                input.Width = 470;
                input.UseSystemPasswordChar = true;
                ok.Text = "Guardar y validar";
                ok.Left = 258;
                ok.Top = 94;
                ok.Width = 110;
                ok.DialogResult = DialogResult.OK;
                cancel.Text = "Cancelar";
                cancel.Left = 376;
                cancel.Top = 94;
                cancel.Width = 110;
                cancel.DialogResult = DialogResult.Cancel;
                form.Controls.Add(label);
                form.Controls.Add(input);
                form.Controls.Add(ok);
                form.Controls.Add(cancel);
                form.AcceptButton = ok;
                form.CancelButton = cancel;
                DialogResult result = form.ShowDialog();
                if (result != DialogResult.OK || string.IsNullOrWhiteSpace(input.Text)) return null;
                return input.Text.Trim();
            }
        }
    }

    public static class CredentialStore
    {
        private const int CredTypeGeneric = 1;
        private const int CredPersistLocalMachine = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeCredential
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref NativeCredential credential, int flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, int type, int flags, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, int type, int flags);

        [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
        private static extern void CredFree(IntPtr credentialPtr);

        public static void Write(string target, string userName, string secret)
        {
            if (string.IsNullOrWhiteSpace(secret)) throw new ConsultantException("empty_secret", "La API key está vacía.");
            byte[] blob = Encoding.UTF8.GetBytes(secret);
            IntPtr blobPtr = Marshal.AllocHGlobal(blob.Length);
            try
            {
                Marshal.Copy(blob, 0, blobPtr, blob.Length);
                NativeCredential credential = new NativeCredential();
                credential.Type = CredTypeGeneric;
                credential.TargetName = target;
                credential.Comment = "OdooConsultor: API key individual de Odoo";
                credential.CredentialBlobSize = blob.Length;
                credential.CredentialBlob = blobPtr;
                credential.Persist = CredPersistLocalMachine;
                credential.UserName = userName;
                if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows no pudo guardar la credencial.");
            }
            finally
            {
                for (int i = 0; i < blob.Length; i++) blob[i] = 0;
                for (int i = 0; i < blob.Length; i++) Marshal.WriteByte(blobPtr, i, 0);
                Marshal.FreeHGlobal(blobPtr);
            }
        }

        public static string Read(string target)
        {
            IntPtr pointer;
            if (!CredRead(target, CredTypeGeneric, 0, out pointer))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 1168) throw new ConsultantException("credential_not_found", "La credencial fue revocada o no existe en Windows.");
                throw new Win32Exception(error, "Windows no pudo leer la credencial.");
            }
            try
            {
                NativeCredential credential = (NativeCredential)Marshal.PtrToStructure(pointer, typeof(NativeCredential));
                byte[] blob = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
                try { return Encoding.UTF8.GetString(blob); }
                finally { Array.Clear(blob, 0, blob.Length); }
            }
            finally
            {
                CredFree(pointer);
            }
        }

        public static bool Exists(string target)
        {
            IntPtr pointer;
            if (!CredRead(target, CredTypeGeneric, 0, out pointer))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 1168) return false;
                throw new Win32Exception(error, "Windows no pudo verificar la credencial.");
            }
            CredFree(pointer);
            return true;
        }

        public static void Delete(string target)
        {
            if (!CredDelete(target, CredTypeGeneric, 0))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 1168) throw new Win32Exception(error, "Windows no pudo eliminar la credencial.");
            }
        }
    }

    public static class ProtectedSnapshot
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OdooConsultor/metadata-snapshot/v1");

        public static string Protect(IDictionary<string, object> values)
        {
            if (values == null) return null;
            byte[] plain = Encoding.UTF8.GetBytes(JsonUtil.Serialize(values));
            byte[] encrypted = null;
            try
            {
                encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(encrypted);
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
                if (encrypted != null) Array.Clear(encrypted, 0, encrypted.Length);
            }
        }

        public static Dictionary<string, object> Unprotect(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            byte[] encrypted;
            try { encrypted = Convert.FromBase64String(value); }
            catch { throw new ConsultantException("snapshot_integrity_failed", "El snapshot protegido no es válido."); }
            byte[] plain = null;
            try
            {
                plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                return JsonUtil.DeserializeObject(Encoding.UTF8.GetString(plain));
            }
            catch (ConsultantException) { throw; }
            catch { throw new ConsultantException("snapshot_integrity_failed", "Windows no pudo abrir el snapshot protegido del paquete."); }
            finally
            {
                Array.Clear(encrypted, 0, encrypted.Length);
                if (plain != null) Array.Clear(plain, 0, plain.Length);
            }
        }
    }
}
