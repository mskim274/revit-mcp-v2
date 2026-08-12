using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCP.Plugin.Services
{
    /// <summary>
    /// Publishes one atomic, per-process discovery record for the local MCP
    /// router.  The registry contains routing metadata only; authentication
    /// continues to use the shared RevitMCP auth-token file.
    /// </summary>
    internal sealed class RevitInstanceRegistry : IDisposable
    {
        private const int SchemaVersion = 1;
        private const int HeartbeatIntervalMilliseconds = 2000;

        private readonly string _sessionId;
        private readonly int _pid;
        private readonly string _revitVersion;
        private readonly string _revitBuild;
        private readonly string _startedAtUtc;
        private readonly string _registryPath;
        private readonly object _sync = new object();
        private readonly Timer _heartbeatTimer;
        private CachedSnapshot _snapshot;
        private bool _heartbeatStarted;
        private bool _disposed;

        /// <summary>
        /// Immutable values captured on Revit's UI thread.  Timer callbacks may
        /// use this object, but must never dereference UIApplication, UIDocument,
        /// Document, Element, or any other Revit API object.
        /// </summary>
        private sealed class CachedSnapshot
        {
            public int Port { get; set; }
            public string ActiveDocumentTitle { get; set; }
            public string ActiveDocumentPath { get; set; }
            public string DocumentFingerprint { get; set; }

            public bool HasSameValues(CachedSnapshot other)
            {
                return other != null &&
                    Port == other.Port &&
                    string.Equals(
                        ActiveDocumentTitle,
                        other.ActiveDocumentTitle,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        ActiveDocumentPath,
                        other.ActiveDocumentPath,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        DocumentFingerprint,
                        other.DocumentFingerprint,
                        StringComparison.Ordinal);
            }
        }

        public RevitInstanceRegistry(
            string sessionId,
            string revitVersion,
            string revitBuild)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session id is required.", nameof(sessionId));

            _sessionId = sessionId.Trim();
            _pid = Process.GetCurrentProcess().Id;
            _revitVersion = revitVersion ?? "";
            _revitBuild = revitBuild ?? "";
            _startedAtUtc = DateTime.UtcNow.ToString("o");

            var rootDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitMCP");
            Directory.CreateDirectory(rootDirectory);
            EnsurePhysicalDirectory(rootDirectory);

            var directory = Path.Combine(rootDirectory, "instances");
            Directory.CreateDirectory(directory);
            EnsurePhysicalDirectory(directory);
            _registryPath = Path.Combine(directory, _pid + ".json");

            // A PID can be reused after an unclean shutdown.  Do not let the
            // stale record masquerade as this new session before first publish.
            EnsureRegularFileIfPresent(_registryPath);
            TryDelete(_registryPath);

            // Disabled until the WebSocket server has successfully bound and a
            // UI-thread snapshot is supplied.  Afterwards the timer is the sole
            // heartbeat publisher, independent of Revit's Idling frequency.
            _heartbeatTimer = new Timer(
                OnHeartbeatTimer,
                null,
                Timeout.Infinite,
                Timeout.Infinite);
        }

        /// <summary>
        /// Capture active-document identity.  This method must be called on the
        /// Revit UI thread.  It performs no registry file write; the timer only
        /// consumes the resulting string/value snapshot.
        /// </summary>
        public void UpdateSnapshot(UIApplication uiApplication, int port)
        {
            if (uiApplication == null || port < 1 || port > 65535)
                return;

            var document = uiApplication.ActiveUIDocument?.Document;
            CachedSnapshot nextSnapshot = null;
            // A session without an active document is not a safe command
            // target.  Leaving nextSnapshot null makes the timer withdraw the
            // record until a document is active again.
            if (document != null)
            {
                var documentTitle = SafeDocumentValue(document, d => d.Title);
                if (string.IsNullOrWhiteSpace(documentTitle))
                    documentTitle = "(Untitled)";

                nextSnapshot = new CachedSnapshot
                {
                    Port = port,
                    ActiveDocumentTitle = documentTitle,
                    ActiveDocumentPath =
                        SafeDocumentValue(document, d => d.PathName),
                    DocumentFingerprint =
                        SessionIdentity.ComputeDocumentFingerprint(document)
                };
            }

            lock (_sync)
            {
                if (_disposed)
                    return;

                var changed = nextSnapshot == null
                    ? _snapshot != null
                    : !nextSnapshot.HasSameValues(_snapshot);
                _snapshot = nextSnapshot;

                if (!_heartbeatStarted)
                {
                    _heartbeatStarted = true;
                    _heartbeatTimer.Change(
                        0,
                        Timeout.Infinite);
                }
                else if (changed)
                {
                    // Publish document switches/withdrawal promptly without
                    // performing any file I/O on Revit's UI thread.
                    _heartbeatTimer.Change(
                        0,
                        Timeout.Infinite);
                }
            }
        }

        /// <summary>
        /// Withdraw the current record without allowing a queued heartbeat to
        /// recreate it.  Used for explicit lifecycle cleanup.
        /// </summary>
        public void Remove()
        {
            lock (_sync)
            {
                _snapshot = null;
                TryDelete(_registryPath);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _snapshot = null;
                try
                {
                    _heartbeatTimer.Change(
                        Timeout.Infinite,
                        Timeout.Infinite);
                }
                catch (ObjectDisposedException)
                {
                    // A duplicate shutdown path is harmless.
                }

                // The same lock is held by the timer for its complete write.
                // Therefore no in-flight write can race this final removal.
                TryDelete(_registryPath);
            }

            _heartbeatTimer.Dispose();
        }

        private void OnHeartbeatTimer(object state)
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                try
                {
                    if (_snapshot == null)
                    {
                        TryDelete(_registryPath);
                        return;
                    }

                    PublishCachedSnapshot(_snapshot, DateTime.UtcNow);
                }
                catch (Exception ex)
                {
                    // Heartbeat is optional discovery infrastructure.  Leave
                    // the previous record intact and retry on the next tick.
                    Debug.WriteLine(
                        $"[RevitMCP] Instance heartbeat failed: {ex.Message}");
                }
                finally
                {
                    if (!_disposed)
                    {
                        // One-shot scheduling prevents callbacks from piling up
                        // if antivirus or disk contention makes an atomic write
                        // take longer than the normal heartbeat interval.
                        _heartbeatTimer.Change(
                            HeartbeatIntervalMilliseconds,
                            Timeout.Infinite);
                    }
                }
            }
        }

        private void PublishCachedSnapshot(
            CachedSnapshot snapshot,
            DateTime nowUtc)
        {
            var record = new
            {
                schema_version = SchemaVersion,
                session_id = _sessionId,
                pid = _pid,
                port = snapshot.Port,
                revit_version = _revitVersion,
                revit_build = _revitBuild,
                started_at_utc = _startedAtUtc,
                last_seen_utc = nowUtc.ToString("o"),
                active_document_title = snapshot.ActiveDocumentTitle,
                active_document_path = snapshot.ActiveDocumentPath,
                document_fingerprint = snapshot.DocumentFingerprint
            };

            var json = JsonSerializer.Serialize(record, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            AtomicWrite(_registryPath, json);
        }

        private static string SafeDocumentValue(
            Document document,
            Func<Document, string> getter)
        {
            if (document == null)
                return "";
            try { return getter(document) ?? ""; }
            catch { return ""; }
        }

        private static void AtomicWrite(string path, string content)
        {
            var directory = Path.GetDirectoryName(path);
            EnsurePhysicalDirectory(Path.GetDirectoryName(directory));
            Directory.CreateDirectory(directory);
            EnsurePhysicalDirectory(directory);
            EnsureRegularFileIfPresent(path);
            var temporaryPath = Path.Combine(
                directory,
                "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(content ?? "");
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                if (File.Exists(path))
                {
                    EnsureRegularFileIfPresent(path);
                    File.Replace(temporaryPath, path, null, true);
                }
                else
                    File.Move(temporaryPath, path);
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }

        private static void EnsurePhysicalDirectory(string directory)
        {
            var attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Instance registry path is not a safe physical directory: " +
                    $"{directory}");
            }
        }

        private static void EnsureRegularFileIfPresent(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                return;

            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Instance registry target is not a regular file: {path}");
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) &&
                    (File.Exists(path) || Directory.Exists(path)))
                {
                    EnsureRegularFileIfPresent(path);
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[RevitMCP] Could not remove instance registry file '{path}': {ex.Message}");
            }
        }
    }
}
