using System;

namespace Autodesk.Revit.DB
{
    /// <summary>
    /// Keeps write-command responses honest when Revit resolves a transaction
    /// to RolledBack or Pending without throwing from Commit().
    /// </summary>
    internal static class TransactionExtensions
    {
        public static void CommitOrThrow(this Transaction transaction)
        {
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));

            var status = transaction.Commit();
            if (status != TransactionStatus.Committed)
            {
                throw new InvalidOperationException(
                    $"Revit transaction did not commit (status: {status}).");
            }
        }
    }
}
