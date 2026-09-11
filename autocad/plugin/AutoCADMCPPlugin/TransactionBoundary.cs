using System;
using System.Threading.Tasks;

namespace AutoCADMCP.Plugin
{
    // One owner for the native transaction. In particular, never call Abort
    // from a catch outside Dispose: that can dereference a freed native handle.
    // No ConfigureAwait(false): the caller owns the AutoCAD command context.
    internal static class TransactionBoundary
    {
        internal sealed class Outcome
        {
            public bool Committed { get; internal set; }
            public bool DisposalSucceeded { get; internal set; }
            public Exception Error { get; internal set; }
            public Exception CleanupError { get; internal set; }
        }

        public static async Task<Outcome> RunAsync<T>(
            Func<T> start,
            Func<T, Task<bool>> execute,
            Action<T> commit,
            Action<T> abort,
            Action<string, Exception> reportError) where T : class, IDisposable
        {
            T transaction = null;
            var outcome = new Outcome();
            var phase = "start_transaction";
            try
            {
                transaction = start()
                    ?? throw new InvalidOperationException("No transaction was created.");
                phase = "execute_and_serialize";
                var shouldCommit = await execute(transaction);
                if (shouldCommit)
                {
                    phase = "commit";
                    commit(transaction);
                    outcome.Committed = true;
                }
                else
                {
                    phase = "abort";
                    abort(transaction);
                }
            }
            catch (Exception error)
            {
                outcome.Error = error;
                // Record the original exception BEFORE native cleanup.
                Report(reportError, phase, error);
            }
            finally
            {
                if (transaction != null)
                {
                    try
                    {
                        // AutoCAD rolls back uncommitted transactions on Dispose.
                        // Never retry Abort/Commit/Dispose, even if cleanup fails.
                        transaction.Dispose();
                        outcome.DisposalSucceeded = true;
                    }
                    catch (Exception cleanupError)
                    {
                        outcome.CleanupError = cleanupError;
                        outcome.Error ??= cleanupError;
                        Report(reportError, "dispose", cleanupError);
                    }
                }
            }
            return outcome;
        }

        private static void Report(
            Action<string, Exception> reportError, string phase, Exception error)
        {
            try { reportError?.Invoke(phase, error); }
            catch (Exception loggingError)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[AutoCADMCP] Error logging failed: {loggingError}");
            }
        }
    }
}
