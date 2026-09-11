using System;
using System.Threading.Tasks;

namespace RevitMCP.Plugin.Services
{
    /// <summary>
    /// Revit.Async 2.1.1's async event handler calls the delegate before
    /// attaching its result handler. A synchronous throw can escape to Revit
    /// and leave RunAsync incomplete forever (and a write gate held).
    /// Its Await continuation checks IsCompleted before IsFaulted, so a faulted
    /// Task can orphan the result too. Transport errors as successful outcomes
    /// and rethrow OUTSIDE Revit.Async after its result task completes.
    /// Execute inline: moving this callback to Task.Run would violate Revit's
    /// main-thread API requirement.
    /// </summary>
    internal static class ExternalEventTaskBoundary
    {
        public static async Task<Outcome<T>> Capture<T>(Func<Task<T>> callback)
        {
            try
            {
                var task = callback() ?? throw new InvalidOperationException(
                    "External event callback returned no Task.");
                return new Outcome<T>(await task.ConfigureAwait(false), null);
            }
            catch (Exception ex)
            {
                return new Outcome<T>(default(T), ex);
            }
        }

        internal sealed class Outcome<T>
        {
            private readonly T _value;
            private readonly Exception _error;
            public Outcome(T value, Exception error) { _value = value; _error = error; }
            public T GetResult()
            {
                if (_error != null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_error).Throw();
                return _value;
            }
        }
    }
}
