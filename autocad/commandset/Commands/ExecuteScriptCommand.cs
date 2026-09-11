using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AutoCADMCP.CommandSet.Helpers;
using AutoCADMCP.CommandSet.Interfaces;

namespace AutoCADMCP.CommandSet.Commands
{
    /// <summary>Roslyn-free boundary: even a compiler load failure is recoverable.</summary>
    public class ExecuteScriptCommand : ICadCommand
    {
        public string Name => "execute_script";
        public string Category => "Script";

        private static readonly Lazy<Engine> ScriptEngine = new(() => new Engine());

        public Task<CommandResult> ExecuteAsync(Database db, Transaction tr,
            Dictionary<string, object> parameters, CancellationToken cancellationToken)
        {
            // This gate must run BEFORE JIT/loading any Roslyn-dependent method.
            if (Environment.GetEnvironmentVariable("AUTOCAD_MCP_ENABLE_SCRIPT") != "1")
                return Task.FromResult(CommandResult.Fail(
                    "execute_script is disabled by default.",
                    "Use dedicated AutoCAD MCP tools, or deliberately set AUTOCAD_MCP_ENABLE_SCRIPT=1 before starting AutoCAD."));

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var engine = ScriptEngine.Value;
                using (engine.Context.EnterContextualReflection())
                {
                    // AutoCAD API execution remains synchronous on the document
                    // thread. No worker tasks and no additional transactions.
                    return Task.FromResult(engine.Command.ExecuteAsync(
                        db, tr, parameters, cancellationToken).GetAwaiter().GetResult());
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Script engine loading/execution failed: {ex.GetBaseException().Message}",
                    "Check the plugin's script-engine subfolder and its four Microsoft.CodeAnalysis DLLs; deploy the complete matching build with AutoCAD closed, then restart AutoCAD. Do not replace Autodesk installation DLLs."));
            }
        }

        private sealed class Engine
        {
            public ScriptEngineLoadContext Context { get; }
            public ICadCommand Command { get; }

            public Engine()
            {
                var directory = Path.Combine(
                    Path.GetDirectoryName(typeof(ExecuteScriptCommand).Assembly.Location),
                    "script-engine");
                Context = new ScriptEngineLoadContext(directory,
                    typeof(ICadCommand).Assembly,
                    typeof(Database).Assembly,
                    typeof(Document).Assembly,
                    typeof(Application).Assembly);
                using (Context.EnterContextualReflection())
                {
                    var assembly = Context.LoadFromAssemblyPath(
                        Path.Combine(directory, "AutoCADMCP.ScriptEngine.dll"));
                    Command = (ICadCommand)Activator.CreateInstance(assembly.GetType(
                        "AutoCADMCP.ScriptEngine.CadScriptEngine", throwOnError: true));
                }
            }
        }
    }
}
