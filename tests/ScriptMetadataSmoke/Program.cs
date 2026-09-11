using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Scripting;
using RevitMCP.CommandSet.Helpers;

// Run the actual helper from two collectible, locationless copies of this
// test assembly. Dependencies are shared, as in the plugin host.
var ownPath = Assembly.GetExecutingAssembly().Location;
void TestGeneration(int generation)
{
    var context = new AssemblyLoadContext("generation-" + generation, isCollectible: true);
    context.Resolving += (_, name) => AssemblyLoadContext.Default.Assemblies
        .FirstOrDefault(a => AssemblyName.ReferenceMatchesDefinition(a.GetName(), name));
    try
    {
        using var stream = File.OpenRead(ownPath);
        var assembly = context.LoadFromStream(stream);
        if (assembly.Location.Length != 0) throw new Exception("Expected locationless assembly");
        try
        {
            ScriptOptions.Default.WithReferences(assembly);
            throw new Exception("Regression setup did not reproduce original failure");
        }
        catch (NotSupportedException) { }
        assembly.GetType("SmokeEntry", true).GetMethod("Run").Invoke(null, new object[] { generation });
        Console.WriteLine($"PASS collectible generation {generation}");
    }
    finally { context.Unload(); }
}
TestGeneration(1);
TestGeneration(2);
SmokeEntry.Run(3);
Console.WriteLine("PASS disk-backed bridge");

public static class SmokeEntry
{
    public static void Run(int generation)
    {
        var prints = new List<object>();
        var globals = ScriptGlobalsBridge.Globals<object>("document-" + generation, prints.Add);
        var script = ScriptGlobalsBridge.Create<object>(
            "using System;\nprint(doc);\nFtToMm(MmToFt(150)) + " + generation);
        var errors = script.Compile().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0) throw new Exception(string.Join("\n", errors.Select(e=>e.ToString())));
        var actual = (double)script.RunAsync(globals).GetAwaiter().GetResult().ReturnValue;
        if (Math.Abs(actual - (150 + generation)) > 1e-9 ||
            prints.Count != 1 || (string)prints[0] != "document-" + generation)
            throw new Exception("Wrong globals or script result");
        var invalid = ScriptGlobalsBridge.Create<object>("var a = 1;\nunknown_symbol");
        var error = invalid.Compile().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
        if (error == null || error.Location.GetLineSpan().StartLinePosition.Line != 1)
            throw new Exception("User line numbers changed");
        var runtimeError = ScriptGlobalsBridge.Create<object>("throw new InvalidOperationException(\"test\");");
        try { runtimeError.RunAsync(globals).GetAwaiter().GetResult(); throw new Exception("Expected runtime error"); }
        catch (InvalidOperationException ex) when (ex.Message == "test") { }
    }
}
