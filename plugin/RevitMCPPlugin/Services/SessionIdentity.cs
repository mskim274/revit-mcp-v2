using System;
using System.IO;
using System.Runtime.CompilerServices;
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

            // Include runtime identity even for saved documents.  Reopening the
            // same path produces a fresh Document object and must invalidate a
            // previously pinned request rather than silently retargeting it.
            var runtimeIdentity = RuntimeHelpers.GetHashCode(document).ToString();

            return Sha256Hex(string.Join(
                "\n",
                "revit-document-v1",
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
