using System;
using Autodesk.AutoCAD.DatabaseServices;

namespace AutoCADMCP.CommandSet.Interfaces
{
    /// <summary>
    /// Per-request selection snapshot. The dispatcher captures the user's
    /// PICKFIRST set BEFORE entering ExecuteInCommandContextAsync (because
    /// the implied selection is unreliable inside that context — AutoCAD's
    /// runtime can clear it as part of "command-like" entry).
    ///
    /// Commands that need the selection read SelectionContext.Current
    /// instead of calling Editor.SelectImplied() themselves.
    ///
    /// Thread model: WebSocket handler is single-flight (one in-flight
    /// command per connection), so a plain static is fine. If we ever
    /// support concurrent connections, switch to AsyncLocal.
    /// </summary>
    public static class SelectionContext
    {
        /// <summary>
        /// ObjectIds captured from PICKFIRST at request entry. Empty array
        /// (not null) when no selection existed. Cleared after each request.
        /// </summary>
        public static ObjectId[] Current { get; set; } = Array.Empty<ObjectId>();

        public static bool HasSelection => Current != null && Current.Length > 0;
    }
}
