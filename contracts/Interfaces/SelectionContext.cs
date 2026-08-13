using System;
using Autodesk.Revit.DB;

namespace RevitMCP.CommandSet.Interfaces
{
    /// <summary>
    /// Per-request selection snapshot shared across the host/CommandSet load
    /// boundary. The host sets it on Revit's API thread and clears it after the
    /// command finishes.
    /// </summary>
    public static class SelectionContext
    {
        public static ElementId[] Current { get; set; } = Array.Empty<ElementId>();

        public static bool HasSelection => Current != null && Current.Length > 0;
    }
}
