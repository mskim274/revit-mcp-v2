using System;
using Autodesk.Revit.DB;

namespace RevitMCP.CommandSet.Interfaces
{
    /// <summary>
    /// Per-request selection snapshot. The dispatcher captures the user's
    /// current Revit UI selection (UIDocument.Selection.GetElementIds())
    /// inside RevitTask.RunAsync — i.e. on the Revit API thread — so
    /// commands that need it can read SelectionContext.Current without
    /// having to go through UIDocument themselves (CommandSet only gets
    /// Document, not UIDocument).
    ///
    /// Mirrors the AutoCAD CommandSet's SelectionContext for symmetry.
    /// Single-flight per WebSocket connection so a plain static is fine.
    /// </summary>
    public static class SelectionContext
    {
        /// <summary>
        /// ElementIds captured from UIDocument.Selection at request entry.
        /// Empty array (not null) when no selection existed. Cleared after
        /// each request.
        /// </summary>
        public static ElementId[] Current { get; set; } = Array.Empty<ElementId>();

        public static bool HasSelection => Current != null && Current.Length > 0;
    }
}
