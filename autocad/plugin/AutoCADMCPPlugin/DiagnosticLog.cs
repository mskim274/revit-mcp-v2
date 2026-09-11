using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AutoCADMCP.Plugin
{
    internal static class DiagnosticLog
    {
        private const long MaxLogBytes = 2 * 1024 * 1024;
        private static readonly object Gate = new object();

        // Local-only bounded logs. Do not log tokens, request parameters,
        // script source, or drawing contents. Exception messages may contain
        // local paths; do not automatically upload these logs.
        internal static void Write(
            string requestId, string command, string phase, int timeoutMs,
            long elapsedMs, Exception error)
        {
            try
            {
                var entry = JsonSerializer.Serialize(new
                {
                    utc = DateTime.UtcNow.ToString("O"),
                    pid = Environment.ProcessId,
                    thread_id = Environment.CurrentManagedThreadId,
                    request_id = requestId,
                    command,
                    phase,
                    timeout_ms = timeoutMs,
                    elapsed_ms = elapsedMs,
                    exception = error.ToString(),
                });
                lock (Gate)
                {
                    var directory = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "AutoCADMCP", "logs");
                    Directory.CreateDirectory(directory);
                    var path = Path.Combine(directory, $"errors-{Environment.ProcessId}.jsonl");
                    if (File.Exists(path) && new FileInfo(path).Length >= MaxLogBytes)
                        File.Move(path, path + ".previous", overwrite: true);
                    File.AppendAllText(path, entry + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch (Exception loggingError)
            {
                Trace.WriteLine($"[AutoCADMCP] Persistent logging failed: {loggingError}");
                Trace.WriteLine($"[AutoCADMCP] Original failure ({phase}): {error}");
            }
        }
    }
}
