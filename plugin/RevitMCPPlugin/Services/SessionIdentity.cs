using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;

namespace RevitMCP.Plugin.Services
{
    /// <summary>
    /// Builds the document identity shared by the instance registry and the
    /// request execution guard.  It deliberately contains no element ids or
    /// other mutable model state, so ordinary edits do not invalidate a target.
    /// Save As, closing/reopening, and switching the active document invalidate
    /// it.  The latter two are enforced by the per-process runtime identity.
    /// </summary>
    internal static class SessionIdentity
    {
        public static string ComputeDocumentFingerprint(Document document)
        {
            if (document == null)
                return "";

            var path = Safe(() => document.PathName);
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    path = Path.GetFullPath(path)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .ToUpperInvariant();
                }
                catch
                {
                    path = path.Trim().ToUpperInvariant();
                }
            }

            var title = Safe(() => document.Title).Trim();
            var projectUniqueId = Safe(() => document.ProjectInformation?.UniqueId);

            // Revit overrides Document.GetHashCode() specifically for this
            // lifetime identity: wrappers for the same currently open native
            // document return the same value, while reopening the same file
            // produces a new value.  The CLR object-identity hash must not be
            // used here because it hashes the transient managed wrapper.
            var runtimeIdentity = document.GetHashCode()
                .ToString(CultureInfo.InvariantCulture);

            return Sha256Hex(string.Join(
                "\n",
                "revit-document-v2",
                path,
                title,
                projectUniqueId,
                runtimeIdentity));
        }

        private static string Safe(Func<string> getter)
        {
            try { return getter() ?? ""; }
            catch { return ""; }
        }

        private static string Sha256Hex(string value)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var valueByte in hash)
                builder.Append(valueByte.ToString("x2"));
            return builder.ToString();
        }
    }
}
