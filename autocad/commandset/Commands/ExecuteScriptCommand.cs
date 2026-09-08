using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoCADMCP.CommandSet.Interfaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace AutoCADMCP.CommandSet.Commands
{
    /// <summary>Globals available to cad_execute_script.</summary>
    public class CadScriptGlobals
    {
        public Database db;
        public Document doc;
        public Transaction tr;
        public Action<object> print;
    }

    /// <summary>
    /// Roslyn C# escape hatch for AutoCAD 2025/.NET 8. The dispatcher owns
    /// the only transaction. Query mode asks it to abort after success;
    /// modify mode asks it to commit after success.
    /// </summary>
    public class ExecuteScriptCommand : ICadCommand
    {
        public string Name => "execute_script";
        public string Category => "Script";

        private const int MaxCodeLength = 50_000;
        private const int MaxPrints = 500;
        private const int MaxCollectionItems = 1000;
        private const int MaxSerializeDepth = 4;

        // Best-effort accident prevention, not a security sandbox.
        private static readonly string[] DeniedPatterns =
        {
            "System.IO",
            "System.Net",
            "System.Diagnostics.Process",
            "Process.Start",
            "Environment.Exit",
            "System.Reflection",
            "Assembly.Load",
            "AppDomain",
            "Marshal.",
            "DllImport",
            "unsafe",
            "TransactionManager",
            "StartTransaction(",
            "StartOpenCloseTransaction(",
            "new Transaction",
            "tr.Commit(",
            "tr.Abort(",
            "SaveAs(",
            ".Save(",
        };

        public Task<CommandResult> ExecuteAsync(
            Database db,
            Transaction tr,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!string.Equals(
                        Environment.GetEnvironmentVariable(
                            "AUTOCAD_MCP_ENABLE_SCRIPT"),
                        "1",
                        StringComparison.Ordinal))
                {
                    return Fail(
                        "execute_script is disabled by default.",
                        "Use dedicated AutoCAD MCP tools, or deliberately set AUTOCAD_MCP_ENABLE_SCRIPT=1 before starting AutoCAD.");
                }

                var code = GetString(parameters, "code");
                if (string.IsNullOrWhiteSpace(code))
                {
                    return Fail(
                        "Missing required parameter: code",
                        "Provide a C# script body; its last expression becomes return_value.");
                }
                if (code.Length > MaxCodeLength)
                {
                    return Fail(
                        $"Script too long: {code.Length} chars (max {MaxCodeLength}).",
                        "Split the work into smaller scripts or promote the recurring pattern to a batch tool.");
                }

                var mode = (GetString(parameters, "mode") ?? "query")
                    .Trim()
                    .ToLowerInvariant();
                if (mode != "query" && mode != "modify")
                {
                    return Fail(
                        $"Invalid mode: '{mode}'.",
                        "Use mode='query' (dispatcher transaction is aborted) or mode='modify' (dispatcher transaction commits on success).");
                }

                var syntaxSafetyError = ValidateSyntaxSafety(code);
                if (syntaxSafetyError != null)
                {
                    return Fail(
                        syntaxSafetyError,
                        "Remove asynchronous, threaded, directive, or obvious unbounded-loop constructs; AutoCAD API scripts must finish synchronously on the document thread.");
                }

                foreach (var pattern in DeniedPatterns)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (code.IndexOf(
                            pattern,
                            StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    return Fail(
                        $"Script contains a denied pattern: '{pattern}'.",
                        pattern.IndexOf(
                            "Transaction",
                            StringComparison.OrdinalIgnoreCase) >= 0 ||
                        pattern.StartsWith("tr.", StringComparison.OrdinalIgnoreCase)
                            ? "Use the supplied tr global; transaction commit/abort is controlled by mode and the dispatcher."
                            : "File, network, process, reflection, native interop, document saving, and threading access are not allowed by this guardrail.");
                }

                cancellationToken.ThrowIfCancellationRequested();

                var options = ScriptOptions.Default
                    .WithReferences(
                        typeof(object).Assembly,
                        typeof(Enumerable).Assembly,
                        typeof(Database).Assembly,
                        typeof(Document).Assembly,
                        Assembly.GetExecutingAssembly())
                    .WithImports(
                        "System",
                        "System.Collections.Generic",
                        "System.Linq",
                        "Autodesk.AutoCAD.DatabaseServices",
                        "Autodesk.AutoCAD.Geometry");

                var script = CSharpScript.Create(
                    code,
                    options,
                    typeof(CadScriptGlobals));
                var diagnostics = script.Compile(cancellationToken);
                var compileErrors = diagnostics
                    .Where(diagnostic =>
                        diagnostic.Severity == DiagnosticSeverity.Error)
                    .Take(20)
                    .Select(diagnostic =>
                    {
                        var location = diagnostic.Location
                            .GetLineSpan()
                            .StartLinePosition;
                        return $"line {location.Line + 1}, col {location.Character + 1}: " +
                               $"{diagnostic.Id} {diagnostic.GetMessage()}";
                    })
                    .ToList();
                if (compileErrors.Count > 0)
                {
                    return Fail(
                        "Script compile error:\n" +
                        string.Join("\n", compileErrors),
                        "Fix the indicated lines and retry. Imports already include System, collections, LINQ, AutoCAD DatabaseServices, and Geometry; globals are db, doc, tr, and print().");
                }

                var prints = new List<string>();
                var printsTruncated = false;
                var globals = new CadScriptGlobals
                {
                    db = db,
                    doc = Application.DocumentManager.MdiActiveDocument,
                    tr = tr,
                    print = value =>
                    {
                        if (prints.Count < MaxPrints)
                            prints.Add(value?.ToString() ?? "null");
                        else
                            printsTruncated = true;
                    },
                };

                object returnValue;
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    returnValue = script
                        .RunAsync(globals, cancellationToken)
                        .GetAwaiter()
                        .GetResult()
                        .ReturnValue;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return Fail(
                        FormatRuntimeError(ex, prints),
                        mode == "modify"
                            ? "The dispatcher will abort the entire transaction. Fix the script and retry with a new idempotency_key unless the previous outcome was uncertain."
                            : "The dispatcher will abort query mode. Fix the script and retry; remember that this denylist is not a complete sandbox.");
                }
                stopwatch.Stop();
                cancellationToken.ThrowIfCancellationRequested();

                var data = new Dictionary<string, object>
                {
                    ["mode"] = mode,
                    ["execution_ms"] = stopwatch.ElapsedMilliseconds,
                    ["transaction"] = mode == "modify"
                        ? "pending dispatcher commit"
                        : "aborted after successful query",
                    ["mutation_committed"] = false,
                    ["return_type"] = returnValue?.GetType().Name ?? "null",
                    ["return_value"] = ToJsonSafe(
                        returnValue,
                        0,
                        cancellationToken),
                    ["prints"] = prints,
                };
                if (printsTruncated)
                    data["prints_truncated"] =
                        $"print() capped at {MaxPrints} lines";

                // The dispatcher aborts its transaction for a successful
                // query, guaranteeing that writes made through tr do not
                // persist. Modify mode uses the ordinary commit path.
                return Task.FromResult(CommandResult.Ok(
                    data,
                    commitTransaction: mode == "modify"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Fail(
                    $"execute_script failed: {ex.Message}",
                    "Check the script syntax and AutoCAD API usage, then retry with a narrower script.");
            }
        }

        private static string ValidateSyntaxSafety(string code)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(
                code,
                new CSharpParseOptions(kind: SourceCodeKind.Script));
            var root = syntaxTree.GetRoot();

            if (root.DescendantNodes().OfType<AwaitExpressionSyntax>().Any() ||
                root.DescendantTokens().Any(token =>
                    token.IsKind(SyntaxKind.AsyncKeyword)))
            {
                return "Async/await is not allowed in execute_script.";
            }

            var prohibitedIdentifiers = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase)
            {
                "Task",
                "ValueTask",
                "Thread",
                "ThreadPool",
                "Timer",
                "Parallel",
            };
            var prohibitedIdentifier = root.DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .FirstOrDefault(node =>
                    prohibitedIdentifiers.Contains(
                        node.Identifier.ValueText));
            if (prohibitedIdentifier != null)
            {
                return $"Threading/asynchronous identifier " +
                       $"'{prohibitedIdentifier.Identifier.ValueText}' is not allowed in execute_script.";
            }

            if (root.DescendantNodes()
                .OfType<WhileStatementSyntax>()
                .Any(statement =>
                    IsObviousTrueCondition(statement.Condition)))
            {
                return "An obvious infinite loop (while(true)) is not allowed.";
            }
            if (root.DescendantNodes()
                .OfType<ForStatementSyntax>()
                .Any(statement => statement.Condition == null))
            {
                return "An obvious infinite loop (for(;;)) is not allowed.";
            }
            if (root.DescendantNodes()
                .OfType<DoStatementSyntax>()
                .Any(statement =>
                    IsObviousTrueCondition(statement.Condition)))
            {
                return "An obvious infinite loop (do/while(true)) is not allowed.";
            }
            if (root.DescendantTrivia(descendIntoTrivia: true)
                .Any(trivia =>
                    trivia.IsKind(SyntaxKind.ReferenceDirectiveTrivia) ||
                    trivia.IsKind(SyntaxKind.LoadDirectiveTrivia)))
            {
                return "Roslyn #r and #load directives are not allowed.";
            }

            return null;
        }

        private static bool IsObviousTrueCondition(
            ExpressionSyntax condition)
        {
            while (condition is ParenthesizedExpressionSyntax parenthesized)
                condition = parenthesized.Expression;
            if (condition.IsKind(SyntaxKind.TrueLiteralExpression))
                return true;

            var compact = new string(
                condition.ToString()
                    .Where(character => !char.IsWhiteSpace(character))
                    .ToArray());
            return string.Equals(compact, "1==1", StringComparison.Ordinal) ||
                   string.Equals(
                       compact,
                       "true==true",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatRuntimeError(
            Exception exception,
            List<string> prints)
        {
            var inner = exception.InnerException == null
                ? ""
                : $" — {exception.InnerException.Message}";
            var message =
                $"Script runtime error: {exception.GetType().Name}: " +
                $"{exception.Message}{inner}";
            if (prints.Count > 0)
            {
                var tail = prints.Skip(Math.Max(0, prints.Count - 10));
                message += "\nLast prints before failure:\n" +
                           string.Join("\n", tail);
            }
            return message;
        }

        private static object ToJsonSafe(
            object value,
            int depth,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value == null)
                return null;
            if (depth > MaxSerializeDepth)
                return value.ToString();

            switch (value)
            {
                case string or bool or int or long or short or byte or
                     double or float or decimal:
                    return value;
                case Handle handle:
                    return handle.Value.ToString();
                case ObjectId objectId:
                    return new Dictionary<string, object>
                    {
                        ["handle"] = objectId.IsNull
                            ? ""
                            : objectId.Handle.Value.ToString(),
                        ["is_null"] = objectId.IsNull,
                        ["is_erased"] = !objectId.IsNull && objectId.IsErased,
                    };
                case Point2d point2d:
                    return new[] { point2d.X, point2d.Y };
                case Point3d point3d:
                    return new[] { point3d.X, point3d.Y, point3d.Z };
                case Vector2d vector2d:
                    return new[] { vector2d.X, vector2d.Y };
                case Vector3d vector3d:
                    return new[] { vector3d.X, vector3d.Y, vector3d.Z };
                case Entity entity:
                    return new Dictionary<string, object>
                    {
                        ["handle"] = entity.Handle.Value.ToString(),
                        ["type"] = entity.GetType().Name,
                        ["layer"] = entity.Layer,
                    };
                case DBObject databaseObject:
                    return new Dictionary<string, object>
                    {
                        ["handle"] = databaseObject.Handle.Value.ToString(),
                        ["type"] = databaseObject.GetType().Name,
                        ["is_erased"] = databaseObject.IsErased,
                    };
                case IDictionary dictionary:
                {
                    var result = new Dictionary<string, object>();
                    var count = 0;
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (count++ >= MaxCollectionItems)
                        {
                            result["__truncated__"] =
                                $"capped at {MaxCollectionItems} entries";
                            break;
                        }
                        result[entry.Key?.ToString() ?? "null"] =
                            ToJsonSafe(
                                entry.Value,
                                depth + 1,
                                cancellationToken);
                    }
                    return result;
                }
                case IEnumerable sequence:
                {
                    var result = new List<object>();
                    var count = 0;
                    foreach (var item in sequence)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (count++ >= MaxCollectionItems)
                        {
                            result.Add(
                                $"... (truncated at {MaxCollectionItems} items)");
                            break;
                        }
                        result.Add(ToJsonSafe(
                            item,
                            depth + 1,
                            cancellationToken));
                    }
                    return result;
                }
                default:
                    return value.ToString();
            }
        }

        private static string GetString(
            Dictionary<string, object> parameters,
            string key)
            => parameters.TryGetValue(key, out var raw) &&
               raw is string value
                ? value
                : null;

        private static Task<CommandResult> Fail(
            string message,
            string suggestion)
            => Task.FromResult(CommandResult.Fail(message, suggestion));
    }
}
