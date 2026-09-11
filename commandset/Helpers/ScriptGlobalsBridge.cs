#if !NETFRAMEWORK
using System;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace RevitMCP.CommandSet.Helpers
{
    /// <summary>
    /// A locationless, collectible CommandSet cannot be a script's globals type.
    /// Use a stable BCL carrier and bootstrap the unchanged public script API.
    /// User code stays in its own submission to preserve diagnostic line numbers.
    /// </summary>
    internal static class ScriptGlobalsBridge
    {
        public static Script<object> Create<TDocument>(string code)
        {
            if (typeof(TDocument).Assembly.IsCollectible)
                throw new NotSupportedException("The script document type must belong to a stable host assembly.");

            var options = ScriptOptions.Default
                .WithReferences(typeof(object).Assembly, typeof(Enumerable).Assembly,
                    typeof(TDocument).Assembly)
                .WithImports("System", "System.Collections.Generic", "System.Linq");
            if (!string.IsNullOrEmpty(typeof(TDocument).Namespace))
                options = options.AddImports(typeof(TDocument).Namespace);

            const string bootstrap =
                "var doc = Item1; var print = Item2; " +
                "double MmToFt(double value) => value / 304.8; " +
                "double FtToMm(double value) => value * 304.8;";
            return CSharpScript.Create(bootstrap, options,
                    typeof(Tuple<TDocument, Action<object>>))
                .ContinueWith(code, options);
        }

        public static Tuple<TDocument, Action<object>> Globals<TDocument>(
            TDocument document, Action<object> print) => Tuple.Create(document, print);
    }
}
#endif
