using AutoCADMCP.Plugin;

var count = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    count++;
}

async Task Test(string name, bool commit, string failure = null,
    bool cleanupFailure = false, bool loggerFailure = false)
{
    var calls = new List<string>();
    var originalError = failure == "cancel"
        ? (Exception)new OperationCanceledException("cancelled")
        : new InvalidOperationException(name);
    var cleanupError = new InvalidOperationException("cleanup failed");
    var tx = new FakeTransaction(calls, cleanupFailure ? cleanupError : null);
    var outcome = await TransactionBoundary.RunAsync(
        () =>
        {
            calls.Add("start");
            if (failure == "start") throw originalError;
            return tx;
        },
        async current =>
        {
            calls.Add("execute");
            current.Use();
            // A real asynchronous exception also exercises the helper's await.
            if (failure == "async") await Task.Yield();
            if (failure is "runtime" or "cancel" or "async") throw originalError;
            calls.Add("serialize");
            if (failure == "serialize") throw originalError;
            return commit;
        },
        current =>
        {
            current.Use();
            calls.Add("commit");
            if (failure == "commit") throw originalError;
            current.Committed = true;
        },
        current =>
        {
            current.Use();
            calls.Add("abort");
            if (failure == "abort") throw originalError;
        },
        (phase, error) =>
        {
            calls.Add("report:" + phase);
            if (loggerFailure) throw new Exception("logger unavailable");
        });

    var started = failure != "start";
    Check(tx.DisposeCalls == (started ? 1 : 0), name + ": dispose exactly once");
    Check(tx.AfterDisposeCalls == 0, name + ": never touch freed transaction");
    Check(calls.Count(c => c == "abort") <= 1, name + ": no abort retry");
    Check(calls.Count(c => c == "commit") <= 1, name + ": no commit retry");
    Check(outcome.Committed == tx.Committed, name + ": preserve actual commit state");
    Check(outcome.DisposalSucceeded == (started && !cleanupFailure), name + ": cleanup outcome");
    if (failure != null)
    {
        Check(ReferenceEquals(outcome.Error, originalError), name + ": preserve original exception");
        if (started)
            Check(calls.FindIndex(c => c.StartsWith("report:")) < calls.IndexOf("dispose"),
                name + ": report original error before cleanup");
    }
    else
        Check(outcome.Error == (cleanupFailure ? cleanupError : null), name + ": expected error");
    Check(outcome.CleanupError == (started && cleanupFailure ? cleanupError : null),
        name + ": separate cleanup error");
    if (failure is "runtime" or "cancel" or "async" or "serialize")
        Check(!calls.Contains("abort") && !calls.Contains("commit"),
            name + ": dispose handles uncommitted failure; no extra native call");
    if (failure == null && !cleanupFailure)
        Check(string.Join(",", calls) == (commit
            ? "start,execute,serialize,commit,dispose"
            : "start,execute,serialize,abort,dispose"), name + ": success sequence");
    Console.WriteLine("PASS " + name + " | " + string.Join(" -> ", calls));
}

await Test("query success", false);
await Test("modify success", true);
await Test("failed command rollback", false);
await Test("compile/runtime exception", false, "runtime");
await Test("modify exception", true, "runtime");
await Test("timeout cancellation", true, "cancel");
await Test("asynchronous exception", true, "async");
await Test("payload serialization exception", true, "serialize");
await Test("transaction start exception", true, "start");
await Test("abort exception", false, "abort");
await Test("commit exception", true, "commit");
await Test("dispose exception after commit", true, cleanupFailure: true);
await Test("dispose exception after query", false, cleanupFailure: true);
await Test("original plus cleanup exception", true, "runtime", cleanupFailure: true);
await Test("logging exception cannot break rollback", true, "runtime", loggerFailure: true);
await Test("logging and cleanup exceptions", true, "cancel", cleanupFailure: true, loggerFailure: true);
Console.WriteLine($"PASS: 16 scenarios, {count} assertions. No AutoCAD/Revit API loaded.");

sealed class FakeTransaction(List<string> calls, Exception cleanupError) : IDisposable
{
    public bool Committed;
    public int DisposeCalls;
    public int AfterDisposeCalls;
    private bool disposed;
    public void Use()
    {
        if (!disposed) return;
        AfterDisposeCalls++;
        throw new Exception("USE AFTER DISPOSE");
    }
    public void Dispose()
    {
        if (disposed) AfterDisposeCalls++;
        disposed = true;
        DisposeCalls++;
        calls.Add("dispose");
        if (cleanupError != null) throw cleanupError;
    }
}
