using System.Reflection;
using System.Runtime.Loader;
using AutoCADMCP.CommandSet.Commands;
using AutoCADMCP.CommandSet.Interfaces;
using Autodesk.AutoCAD.DatabaseServices;

var assertions = 0;
void Check(bool value, string name)
{
    assertions++;
    if (!value) throw new Exception("FAIL: " + name);
}
var command = new ExecuteScriptCommand();
CommandResult Run(string code, string mode = "query", CancellationToken token = default) =>
    command.ExecuteAsync(new Database(), new Transaction(), new()
    {
        ["code"] = code, ["mode"] = mode
    }, token).GetAwaiter().GetResult();
Dictionary<string, object> Data(CommandResult result)
{
    Check(result.Success, result.ErrorMessage ?? "script succeeded");
    return (Dictionary<string, object>)result.Data;
}

var priorGate = Environment.GetEnvironmentVariable("AUTOCAD_MCP_ENABLE_SCRIPT");
try
{
    Environment.SetEnvironmentVariable("AUTOCAD_MCP_ENABLE_SCRIPT", null);
    Check(!Run("1+2").Success, "disabled before loading compiler");
    Check(!AssemblyLoadContext.All.Any(c => c.Name == "AutoCADMCP.ScriptEngine"),
        "disabled gate did not create compiler context");

    // Use actual host DLLs without starting AutoCAD or loading native API DLLs.
    if (args.Length != 1) throw new ArgumentException("Pass the directory containing host Roslyn 4.0 DLLs.");
    var hostCommon = AssemblyLoadContext.Default.LoadFromAssemblyPath(
        Path.GetFullPath(Path.Combine(args[0], "Microsoft.CodeAnalysis.dll")));
    var hostCSharp = AssemblyLoadContext.Default.LoadFromAssemblyPath(
        Path.GetFullPath(Path.Combine(args[0], "Microsoft.CodeAnalysis.CSharp.dll")));
    Check(hostCommon.GetName().Version == new Version(4, 0, 0, 0), "host compiler is 4.0");
    Check(hostCSharp.GetName().Version == new Version(4, 0, 0, 0), "host CSharp compiler is 4.0");
    Environment.SetEnvironmentVariable("AUTOCAD_MCP_ENABLE_SCRIPT", "1");

    var query = Run("1+2");
    var data = Data(query);
    Check((int)data["return_value"] == 3, "expression compilation/result");
    Check((string)data["compiler_version"] == "4.9.0.0", "private compiler is 4.9");
    Check(!query.CommitTransaction, "query requests abort");
    Check(!(bool)data["mutation_committed"], "no false commit claim");

    data = Data(Run("new Dictionary<string, object> {{\"document\", doc.Name}, {\"filename\", db.Filename}, {\"tr_exists\", tr != null}}"));
    var globals = (Dictionary<string, object>)data["return_value"];
    Check((string)globals["document"] == "test.dwg", "document global crosses contexts");
    Check((string)globals["filename"] == "test.dwg", "database global crosses contexts");
    Check((bool)globals["tr_exists"], "transaction global crosses contexts");
    data = Data(Run("print(\"ok\"); Enumerable.Range(1, 4).Sum()"));
    Check((int)data["return_value"] == 10, "LINQ import and references");
    Check(((List<string>)data["prints"]).Single() == "ok", "print delegate");
    data = Data(Run("new Point3d(1, 2, 3)"));
    Check(((double[])data["return_value"]).SequenceEqual(new double[] {1,2,3}), "geometry type serialization");
    var modify = Run("42", "modify");
    Data(modify);
    Check(modify.CommitTransaction, "modify requests dispatcher commit only");
    Check(!Run("1", "invalid").Success, "invalid mode rejected");
    var compile = Run("var x = ;");
    Check(!compile.Success && compile.ErrorMessage.Contains("Script compile error"), "compile failure returned");
    Check(!string.IsNullOrWhiteSpace(compile.Suggestion), "compile recovery suggestion");
    var runtime = Run("print(\"before\"); throw new Exception(\"expected\");");
    Check(!runtime.Success && runtime.ErrorMessage.Contains("before"), "runtime failure retains prints");
    foreach (var code in new[] {"await Task.Delay(1);", "while(true) {}", "for(;;) {}",
        "#r \"test.dll\"", "tr.Commit();", "System.IO.File.ReadAllText(\"a\");"})
        Check(!Run(code).Success, "guard rejects " + code);
    using (var cts = new CancellationTokenSource())
    {
        cts.Cancel();
        try { Run("1", token: cts.Token); throw new Exception("cancellation was swallowed"); }
        catch (OperationCanceledException) { assertions++; }
    }
    Check((int)Data(Run("6*7"))["return_value"] == 42, "works after failures");
    var context = AssemblyLoadContext.All.Single(c => c.Name == "AutoCADMCP.ScriptEngine");
    Check(context.Assemblies.Count(a => a.GetName().Name.StartsWith("Microsoft.CodeAnalysis")) == 4,
        "all four private compiler assemblies loaded");
    Check(!context.Assemblies.Any(a => a.GetName().Name == "AutoCADMCP.CommandSet"), "contracts shared, not duplicated");
    Check(AssemblyLoadContext.Default.Assemblies.Single(a => a.GetName().Name == "Microsoft.CodeAnalysis") == hostCommon,
        "host compiler remains untouched");
    Console.WriteLine($"PASS: {assertions} assertions; actual host Roslyn 4.0 + private Roslyn 4.9 coexist. CAD API uses test stubs, not native model verification.");
}
finally
{
    Environment.SetEnvironmentVariable("AUTOCAD_MCP_ENABLE_SCRIPT", priorGate);
}
