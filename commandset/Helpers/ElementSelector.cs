using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMCP.CommandSet.Helpers
{
    /// <summary>
    /// Shared element-selection logic used by visualization/tagging commands
    /// (apply_color_filter, tag_by_filter, etc.).
    ///
    /// Selector dimensions (all optional unless noted):
    ///   - element_ids:                   explicit list (takes priority over filters)
    ///   - category:                      Revit category name, e.g. "Walls"
    ///   - type_name_contains:            element type Name contains (case-insensitive)
    ///   - type_name_starts_with:         element type Name starts-with (case-insensitive)
    ///   - mark_contains:                 ALL_MODEL_MARK / mark instance parameter contains
    ///   - parameter_name + parameter_value_contains: any parameter value contains
    ///   - level_name:                    only elements whose level == level_name
    ///   - view_id:                       only elements visible in the given view
    ///
    /// Returns Element list capped at max_count (default 10,000). Returns counts
    /// of both matched and unfiltered candidates so callers can warn on caps.
    /// </summary>
    public class ElementSelectorOptions
    {
        public List<int> ElementIds { get; set; }
        public string Category { get; set; }
        public string TypeNameContains { get; set; }
        public string TypeNameStartsWith { get; set; }
        public string MarkContains { get; set; }
        public string ParameterName { get; set; }
        public string ParameterValueContains { get; set; }
        public string LevelName { get; set; }
        public ElementId ViewId { get; set; }
        public int MaxCount { get; set; } = 10000;
    }

    public class ElementSelectorResult
    {
        public List<Element> Elements { get; set; } = new List<Element>();
        public int TotalCandidatesBeforeFilter { get; set; }
        public bool TruncatedToMaxCount { get; set; }
        public List<string> AppliedFilters { get; set; } = new List<string>();
    }

    public static class ElementSelector
    {
        public static ElementSelectorResult Resolve(Document doc, ElementSelectorOptions opts)
        {
            var result = new ElementSelectorResult();

            // ─── 1) Direct ID path (priority) ───
            if (opts.ElementIds != null && opts.ElementIds.Count > 0)
            {
                foreach (var id in opts.ElementIds)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem != null) result.Elements.Add(elem);
                }
                result.TotalCandidatesBeforeFilter = result.Elements.Count;
                result.AppliedFilters.Add($"element_ids ({opts.ElementIds.Count})");
                return result;
            }

            // ─── 2) Filtered collector path ───
            FilteredElementCollector collector;
            if (opts.ViewId != null && opts.ViewId != ElementId.InvalidElementId)
            {
                collector = new FilteredElementCollector(doc, opts.ViewId);
                result.AppliedFilters.Add($"view_id={opts.ViewId.IntegerValue}");
            }
            else
            {
                collector = new FilteredElementCollector(doc);
            }

            collector = collector.WhereElementIsNotElementType();

            // Category — apply at collector level for performance
            if (!string.IsNullOrWhiteSpace(opts.Category))
            {
                var bic = ResolveCategoryFilter(doc, opts.Category);
                if (bic.HasValue)
                {
                    collector = collector.OfCategory(bic.Value);
                    result.AppliedFilters.Add($"category={opts.Category}");
                }
                else
                {
                    // Fall back to name match on Element.Category
                    result.AppliedFilters.Add($"category={opts.Category} (post-filter)");
                }
            }

            // Materialize once — downstream filters are LINQ over this list
            var candidates = collector.ToList();
            result.TotalCandidatesBeforeFilter = candidates.Count;

            // Category name post-filter (fallback for non-BIC categories)
            if (!string.IsNullOrWhiteSpace(opts.Category)
                && !result.AppliedFilters.Any(f => f.StartsWith("category=" + opts.Category + (opts.Category.EndsWith(")") ? "" : ""))))
            {
                candidates = candidates
                    .Where(e => e.Category?.Name?.Equals(opts.Category, StringComparison.OrdinalIgnoreCase) == true)
                    .ToList();
            }

            // Type-name filters
            if (!string.IsNullOrEmpty(opts.TypeNameContains))
            {
                candidates = candidates.Where(e =>
                {
                    var typeName = GetTypeName(doc, e);
                    return typeName != null && typeName.IndexOf(opts.TypeNameContains, StringComparison.OrdinalIgnoreCase) >= 0;
                }).ToList();
                result.AppliedFilters.Add($"type_name~\"{opts.TypeNameContains}\"");
            }

            if (!string.IsNullOrEmpty(opts.TypeNameStartsWith))
            {
                candidates = candidates.Where(e =>
                {
                    var typeName = GetTypeName(doc, e);
                    return typeName != null && typeName.StartsWith(opts.TypeNameStartsWith, StringComparison.OrdinalIgnoreCase);
                }).ToList();
                result.AppliedFilters.Add($"type_name^\"{opts.TypeNameStartsWith}\"");
            }

            // Mark filter (instance parameter, falls back to LookupParameter("Mark"))
            if (!string.IsNullOrEmpty(opts.MarkContains))
            {
                candidates = candidates.Where(e =>
                {
                    var mark = GetMark(e);
                    return mark != null && mark.IndexOf(opts.MarkContains, StringComparison.OrdinalIgnoreCase) >= 0;
                }).ToList();
                result.AppliedFilters.Add($"mark~\"{opts.MarkContains}\"");
            }

            // Arbitrary parameter filter
            if (!string.IsNullOrEmpty(opts.ParameterName) && !string.IsNullOrEmpty(opts.ParameterValueContains))
            {
                candidates = candidates.Where(e =>
                {
                    var p = e.LookupParameter(opts.ParameterName);
                    if (p == null) return false;
                    var v = GetParameterDisplayValue(p);
                    return v != null && v.IndexOf(opts.ParameterValueContains, StringComparison.OrdinalIgnoreCase) >= 0;
                }).ToList();
                result.AppliedFilters.Add($"{opts.ParameterName}~\"{opts.ParameterValueContains}\"");
            }

            // Level filter
            if (!string.IsNullOrEmpty(opts.LevelName))
            {
                candidates = candidates.Where(e =>
                {
                    var lvlName = GetElementLevelName(doc, e);
                    return string.Equals(lvlName, opts.LevelName, StringComparison.OrdinalIgnoreCase);
                }).ToList();
                result.AppliedFilters.Add($"level={opts.LevelName}");
            }

            // Cap
            if (candidates.Count > opts.MaxCount)
            {
                result.TruncatedToMaxCount = true;
                candidates = candidates.Take(opts.MaxCount).ToList();
            }

            result.Elements = candidates;
            return result;
        }

        private static BuiltInCategory? ResolveCategoryFilter(Document doc, string categoryName)
        {
            // Try common BuiltInCategory by name match against Category.Name
            foreach (BuiltInCategory bic in Enum.GetValues(typeof(BuiltInCategory)))
            {
                try
                {
                    var cat = Autodesk.Revit.DB.Category.GetCategory(doc, bic);
                    if (cat != null && string.Equals(cat.Name, categoryName, StringComparison.OrdinalIgnoreCase))
                        return bic;
                }
                catch { /* some BICs throw — skip */ }
            }
            return null;
        }

        private static string GetTypeName(Document doc, Element e)
        {
            try
            {
                var typeId = e.GetTypeId();
                if (typeId == null || typeId == ElementId.InvalidElementId) return null;
                var typeElem = doc.GetElement(typeId);
                return typeElem?.Name;
            }
            catch { return null; }
        }

        private static string GetMark(Element e)
        {
            try
            {
                var p = e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK) ?? e.LookupParameter("Mark");
                return p?.AsString();
            }
            catch { return null; }
        }

        private static string GetParameterDisplayValue(Parameter p)
        {
            if (p == null || !p.HasValue) return null;
            var vs = p.AsValueString();
            if (!string.IsNullOrEmpty(vs)) return vs;
            switch (p.StorageType)
            {
                case StorageType.String: return p.AsString();
                case StorageType.Integer: return p.AsInteger().ToString();
                case StorageType.Double: return p.AsDouble().ToString("F4");
                case StorageType.ElementId: return p.AsElementId().IntegerValue.ToString();
                default: return null;
            }
        }

        private static string GetElementLevelName(Document doc, Element e)
        {
            try
            {
                var levelId = e.LevelId;
                if (levelId == null || levelId == ElementId.InvalidElementId)
                {
                    // Some elements (e.g. structural framing) hide level under "Reference Level"
                    var refLvl = e.LookupParameter("Reference Level") ?? e.LookupParameter("참조 레벨");
                    if (refLvl != null && refLvl.StorageType == StorageType.ElementId)
                        levelId = refLvl.AsElementId();
                }
                if (levelId == null || levelId == ElementId.InvalidElementId) return null;
                return (doc.GetElement(levelId) as Level)?.Name;
            }
            catch { return null; }
        }
    }
}
