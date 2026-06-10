#if NETFRAMEWORK
// ─────────────────────────────────────────────────────────────────────────
// net48 stub (Revit 2023/2024): Roslyn scripting is not bundled for the
// .NET Framework build. The command exists so the dispatcher resolves it,
// but it always returns a clear "not supported" error.
// ─────────────────────────────────────────────────────────────────────────
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.Script
{
    public class ExecuteScriptCommand : IRevitCommand
    {
        public string Name => "execute_script";
        public string Category => "Script";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(CommandResult.Fail(
                "execute_script is only available on Revit 2025+ (net8 plugin build).",
                "Use the dedicated tools (query/modify/batch) instead, or upgrade to Revit 2025."));
        }
    }
}
#else
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.Script
{
    /// <summary>
    /// Globals injected into every script. Must be public for Roslyn access.
    /// </summary>
    public class ScriptGlobals
    {
        /// <summary>The active Revit document.</summary>
        public Document doc;

        /// <summary>Append a line to the prints[] array in the response.</summary>
        public Action<object> print;

        /// <summary>Convert millimeters to Revit internal feet.</summary>
        public double MmToFt(double mm) => mm / 304.8;

        /// <summary>Convert Revit internal feet to millimeters.</summary>
        public double FtToMm(double ft) => ft * 304.8;
    }

    /// <summary>
    /// Execute an arbitrary C# script against the live Revit document.
    /// This is the "escape hatch" tool: requests with no dedicated tool are
    /// served by Claude writing Revit API code directly.
    ///
    /// Parameters:
    ///   code  (string, required) — C# script body. The last expression (no
    ///                              trailing semicolon) becomes return_value.
    ///   mode  (string, optional) — "query" (default) | "modify".
    ///         query : NO transaction is opened. The Revit API itself throws
    ///                 on any model mutation → physically read-only.
    ///         modify: wrapped in a single Transaction "MCP: Script";
    ///                 any runtime exception rolls back ALL changes.
    ///
    /// Script environment:
    ///   Globals : doc (Document), print(object), MmToFt(d), FtToMm(d)
    ///   Imports : System, System.Collections.Generic, System.Linq,
    ///             Autodesk.Revit.DB
    ///
    /// Safety: a denylist blocks file/network/process/reflection access and
    /// manual Transaction/Save calls (transactions are managed by this tool).
    /// Compile errors return line-numbered diagnostics for self-repair.
    /// </summary>
    public class ExecuteScriptCommand : IRevitCommand
    {
        public string Name => "execute_script";
        public string Category => "Script";

        private const int MaxCodeLength = 50_000;
        private const int MaxPrints = 500;
        private const int MaxCollectionItems = 1000;
        private const int MaxSerializeDepth = 4;

        // Patterns that must not appear in scripts (case-insensitive).
        // Not a sandbox — a guard against accidents and obvious misuse.
        private static readonly string[] DeniedPatterns = new[]
        {
            "System.IO",              // file access
            "System.Net",             // network access
            "System.Diagnostics.Process",
            "Process.Start",
            "Environment.Exit",
            "System.Reflection",
            "Assembly.Load",
            "AppDomain",
            "Marshal.",
            "DllImport",
            "unsafe",
            "new Transaction",        // transactions are managed by this tool
            "new SubTransaction",
            "new TransactionGroup",
            "SaveAs(",                // never persist from a script
            ".Save(",
            "SynchronizeWithCentral", // workshared safety
        };

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                // ─── Parse + validate input ───
                var code = GetParam<string>(parameters, "code", null);
                if (string.IsNullOrWhiteSpace(code))
                    return Task.FromResult(CommandResult.Fail(
                        "Missing required parameter: code",
                        "Provide a C# script body. The last expression becomes the return value."));

                if (code.Length > MaxCodeLength)
                    return Task.FromResult(CommandResult.Fail(
                        $"Script too long: {code.Length} chars (max {MaxCodeLength}).",
                        "Split the work into multiple smaller scripts."));

                var mode = (GetParam<string>(parameters, "mode", "query") ?? "query")
                    .ToLowerInvariant();
                if (mode != "query" && mode != "modify")
                    return Task.FromResult(CommandResult.Fail(
                        $"Invalid mode: '{mode}'",
                        "Use 'query' (read-only, no transaction) or 'modify' (single transaction)."));

                foreach (var pattern in DeniedPatterns)
                {
                    if (code.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                        return Task.FromResult(CommandResult.Fail(
                            $"Script contains a denied pattern: '{pattern}'",
                            pattern.Contains("Transaction")
                                ? "Transactions are managed by this tool — use mode=\"modify\" instead of opening your own."
                                : "File/network/process/reflection access and document saving are not allowed in scripts."));
                }

                cancellationToken.ThrowIfCancellationRequested();

                // ─── Compile ───
                var options = ScriptOptions.Default
                    .WithReferences(
                        typeof(object).Assembly,            // System.Private.CoreLib
                        typeof(Enumerable).Assembly,        // System.Linq
                        typeof(Document).Assembly,          // RevitAPI
                        Assembly.GetExecutingAssembly())    // ScriptGlobals
                    .WithImports(
                        "System",
                        "System.Collections.Generic",
                        "System.Linq",
                        "Autodesk.Revit.DB");

                var script = CSharpScript.Create(code, options, typeof(ScriptGlobals));
                var diagnostics = script.Compile(cancellationToken);
                var compileErrors = diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Take(20)
                    .Select(d =>
                    {
                        var line = d.Location.GetLineSpan().StartLinePosition;
                        return $"line {line.Line + 1}, col {line.Character + 1}: {d.Id} {d.GetMessage()}";
                    })
                    .ToList();

                if (compileErrors.Count > 0)
                    return Task.FromResult(CommandResult.Fail(
                        "Script compile error:\n" + string.Join("\n", compileErrors),
                        "Fix the code at the indicated lines and call execute_script again. " +
                        "Remember: imports already include System / System.Collections.Generic / " +
                        "System.Linq / Autodesk.Revit.DB; globals are doc, print(), MmToFt(), FtToMm()."));

                // ─── Run ───
                var prints = new List<string>();
                var printsTruncated = false;
                var globals = new ScriptGlobals
                {
                    doc = doc,
                    print = o =>
                    {
                        if (prints.Count < MaxPrints) prints.Add(o?.ToString() ?? "null");
                        else printsTruncated = true;
                    }
                };

                object returnValue;
                var sw = Stopwatch.StartNew();

                if (mode == "modify")
                {
                    using (var tx = new Transaction(doc, "MCP: Script"))
                    {
                        tx.Start();
                        try
                        {
                            // Scripts without awaits run synchronously on this
                            // (Revit API) thread — required for API access.
                            returnValue = script.RunAsync(globals, cancellationToken)
                                .GetAwaiter().GetResult().ReturnValue;
                        }
                        catch (Exception ex)
                        {
                            tx.RollBack();
                            return Task.FromResult(CommandResult.Fail(
                                FormatRuntimeError(ex, prints),
                                "Transaction rolled back — NO changes were committed. " +
                                "Fix the script and retry."));
                        }
                        tx.Commit();
                    }
                }
                else
                {
                    try
                    {
                        returnValue = script.RunAsync(globals, cancellationToken)
                            .GetAwaiter().GetResult().ReturnValue;
                    }
                    catch (Exception ex)
                    {
                        return Task.FromResult(CommandResult.Fail(
                            FormatRuntimeError(ex, prints),
                            ex.Message.IndexOf("transaction", StringComparison.OrdinalIgnoreCase) >= 0
                                ? "The script tried to modify the model in query mode. Re-run with mode=\"modify\"."
                                : "Fix the script and retry (query mode — nothing was changed)."));
                    }
                }

                sw.Stop();

                var data = new Dictionary<string, object>
                {
                    ["mode"] = mode,
                    ["execution_ms"] = sw.ElapsedMilliseconds,
                    ["transaction"] = mode == "modify" ? "committed" : "none (query mode)",
                    ["return_type"] = returnValue?.GetType().Name ?? "null",
                    ["return_value"] = ToJsonSafe(returnValue, 0),
                    ["prints"] = prints
                };
                if (printsTruncated)
                    data["prints_truncated"] = $"print() capped at {MaxPrints} lines";

                return Task.FromResult(CommandResult.Ok(data));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(CommandResult.Fail(
                    "Script execution was cancelled (timeout).",
                    "Narrow the element set or raise timeout_ms. Long loops block Revit's UI thread."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"execute_script failed: {ex.Message}",
                    "Check the script syntax and retry."));
            }
        }

        private static string FormatRuntimeError(Exception ex, List<string> prints)
        {
            var inner = ex.InnerException != null ? $" → {ex.InnerException.Message}" : "";
            var msg = $"Script runtime error: {ex.GetType().Name}: {ex.Message}{inner}";
            if (prints.Count > 0)
            {
                var tail = prints.Skip(Math.Max(0, prints.Count - 10));
                msg += "\nLast prints before failure:\n" + string.Join("\n", tail);
            }
            return msg;
        }

        /// <summary>
        /// Convert an arbitrary script return value into a JSON-safe tree.
        /// Caps depth and collection sizes so responses stay within budget.
        /// </summary>
        private static object ToJsonSafe(object value, int depth)
        {
            if (value == null) return null;
            if (depth > MaxSerializeDepth) return value.ToString();

            switch (value)
            {
                case string s: return s;
                case bool b: return b;
                case int or long or short or byte or double or float or decimal:
                    return value;
                case ElementId eid:
                    return eid.IntegerValue;
                case XYZ p:
                    return new Dictionary<string, object>
                    {
                        ["x"] = Math.Round(p.X, 4),
                        ["y"] = Math.Round(p.Y, 4),
                        ["z"] = Math.Round(p.Z, 4)
                    };
                case Element e:
                    return new Dictionary<string, object>
                    {
                        ["id"] = e.Id.IntegerValue,
                        ["name"] = e.Name ?? "",
                        ["category"] = e.Category?.Name ?? ""
                    };
                case System.Collections.IDictionary dict:
                {
                    var d = new Dictionary<string, object>();
                    var n = 0;
                    foreach (System.Collections.DictionaryEntry kv in dict)
                    {
                        if (n++ >= MaxCollectionItems)
                        {
                            d["__truncated__"] = $"{dict.Count - MaxCollectionItems} more entries";
                            break;
                        }
                        d[kv.Key?.ToString() ?? "null"] = ToJsonSafe(kv.Value, depth + 1);
                    }
                    return d;
                }
                case System.Collections.IEnumerable seq:
                {
                    var list = new List<object>();
                    var n = 0;
                    foreach (var item in seq)
                    {
                        if (n++ >= MaxCollectionItems)
                        {
                            list.Add($"... (truncated at {MaxCollectionItems} items)");
                            break;
                        }
                        list.Add(ToJsonSafe(item, depth + 1));
                    }
                    return list;
                }
                default:
                    return value.ToString();
            }
        }

        private static T GetParam<T>(Dictionary<string, object> parameters, string key, T defaultValue = default)
        {
            if (parameters == null || !parameters.TryGetValue(key, out var value) || value == null)
                return defaultValue;
            try
            {
                if (value is T typed) return typed;
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }
    }
}
#endif
