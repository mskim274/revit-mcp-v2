using RevitMCP.Plugin.Services;
using Revit.Async.Extensions;
using Revit.Async.Interfaces;

// Actual 2.1.1 forwarding; no Revit API calls or fake forwarding implementation.
static void Dispatch<T>(Func<Task<T>> callback, ResultHandler<T> result)
    => result.Await(callback(), result.SetResult);
static void Check(bool ok, string message)
{
    if (!ok) throw new Exception(message);
}

var abandoned = new ResultHandler<string>();
try { Dispatch<string>(() => throw new OperationCanceledException(), abandoned); }
catch (OperationCanceledException) { }
Check(!abandoned.Task.IsCompleted, "Expected synchronous-throw orphan");
Console.WriteLine("PASS reproduced original synchronous-throw orphan");

var faultOrphan = new ResultHandler<string>();
Dispatch(() => Task.FromException<string>(new OperationCanceledException()), faultOrphan);
await Task.Delay(100);
Check(!faultOrphan.Task.IsCompleted, "Expected 2.1.1 faulted-task forwarding defect");
Console.WriteLine("PASS reproduced actual Revit.Async 2.1.1 faulted-task orphan (first patch insufficient)");

var callerThread = Environment.CurrentManagedThreadId;
var inline = ExternalEventTaskBoundary.Capture(() =>
{
    Check(callerThread == Environment.CurrentManagedThreadId, "Callback left the Revit thread");
    return Task.FromResult("ok");
});
Check((await inline).GetResult() == "ok", "Success changed");
Console.WriteLine("PASS callback stays inline on calling thread");

using var gate = new SemaphoreSlim(1, 1);
foreach (var kind in new[] { "cancel", "throw", "faulted-task", "canceled-task", "null-task" })
{
    await gate.WaitAsync();
    var result = new ResultHandler<ExternalEventTaskBoundary.Outcome<string>>();
    Dispatch(() => ExternalEventTaskBoundary.Capture<string>(() => kind switch
    {
        "cancel" => throw new OperationCanceledException(),
        "throw" => throw new InvalidOperationException("sync"),
        "faulted-task" => Task.FromException<string>(new InvalidOperationException("async")),
        "canceled-task" => Task.FromCanceled<string>(new CancellationToken(true)),
        _ => null
    }), result);
    try
    {
        var outcome = await result.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(result.Task.IsCompletedSuccessfully, "Transport task must succeed");
        outcome.GetResult(); // Host unwraps outside the Revit external event.
        throw new Exception("Expected command failure");
    }
    catch (OperationCanceledException) when (kind is "cancel" or "canceled-task") { }
    catch (InvalidOperationException) when (kind is not ("cancel" or "canceled-task")) { }
    finally { gate.Release(); }
    Check(await gate.WaitAsync(TimeSpan.FromSeconds(1)), "Write gate not released");
    gate.Release();
    Console.WriteLine($"PASS actual 2.1.1 bridge: {kind}, next write admitted");
}

using var expired = new CancellationTokenSource();
expired.Cancel();
var ran = false;
var expiredTask = ExternalEventTaskBoundary.Capture(() =>
{
    expired.Token.ThrowIfCancellationRequested();
    ran = true;
    return Task.FromResult("must not run");
});
try { (await expiredTask).GetResult(); throw new Exception("Expected cancellation"); }
catch (OperationCanceledException) { }
Check(!ran, "Expired command ran");
Console.WriteLine("PASS expired callback does not invoke command");

var inFlight = new TaskCompletionSource<string>();
var preserved = ExternalEventTaskBoundary.Capture(() => inFlight.Task);
Check(!preserved.IsCompleted, "In-flight work must not be force-completed");
inFlight.SetResult("committed");
Check((await preserved).GetResult() == "committed", "In-flight result lost");
Console.WriteLine("PASS pending task finishes only when actual callback finishes");

public sealed class ResultHandler<T> : IExternalEventResultHandler<T>
{
    private readonly TaskCompletionSource<T> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<T> Task => _result.Task;
    public void SetResult(T value) => _result.TrySetResult(value);
    public void ThrowException(Exception exception) => _result.TrySetException(exception);
    public void Cancel() => _result.TrySetCanceled();
}
