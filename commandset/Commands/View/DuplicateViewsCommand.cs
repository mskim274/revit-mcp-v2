using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.View
{
    /// <summary>
    /// Duplicate one or more views in a SINGLE transaction, choosing among
    /// Duplicate / Duplicate with Detailing / Duplicate as Dependent.
    ///
    /// AI-First: batch-composable (N views, one call, one transaction),
    /// honest contract (CanViewBeDuplicated pre-check → per-item skip with reason,
    /// never a silent no-op), retry-safe (duplicate_* is a side-effect command,
    /// so idempotency_key is honored by the plugin cache).
    ///
    /// Parameters:
    ///   view_ids    (int[], optional)    — target view IDs
    ///   view_names  (string[], optional) — target view names (exact → contains); templates excluded
    ///   option      (string, optional)   — "duplicate" (default) | "with_detailing" | "as_dependent"
    ///   name_suffix (string, optional)   — appended to source name for the new view (auto-increments on collision)
    ///   activate    (bool, optional)     — switch to the first new view after duplication (default false)
    /// </summary>
    public class DuplicateViewsCommand : IRevitCommand
    {
        public string Name => "duplicate_views";
        public string Category => "View";
        private const int MaxViewsPerCall = 100;
        private const int MaxRenameAttempts = 100;

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                if (parameters == null)
                    return Task.FromResult(CommandResult.Fail(
                        "No parameters provided.",
                        "Provide view_ids and/or view_names, plus an option."));

                // ── option mapping (accepts English + Korean synonyms) ──
                var optionStr = "duplicate";
                if (parameters.TryGetValue("option", out var optObj) && optObj != null)
                    optionStr = optObj.ToString().Trim().ToLowerInvariant();

                ViewDuplicateOption option;
                switch (optionStr)
                {
                    case "duplicate":
                    case "복제":
                        option = ViewDuplicateOption.Duplicate;
                        optionStr = "duplicate";
                        break;
                    case "with_detailing":
                    case "withdetailing":
                    case "detailing":
                    case "상세복제":
                    case "상세 복제":
                        option = ViewDuplicateOption.WithDetailing;
                        optionStr = "with_detailing";
                        break;
                    case "as_dependent":
                    case "asdependent":
                    case "dependent":
                    case "의존적복제":
                    case "의존적 복제":
                        option = ViewDuplicateOption.AsDependent;
                        optionStr = "as_dependent";
                        break;
                    default:
                        return Task.FromResult(CommandResult.Fail(
                            $"Unknown option '{optionStr}'.",
                            "Use one of: duplicate, with_detailing, as_dependent."));
                }

                // ── resolve target views (dedupe by id, drop templates) ──
                var requestedIds = ParseIntList(parameters, "view_ids");
                var requestedNames = ParseStringList(parameters, "view_names");
                var requestedCount = requestedIds.Count + requestedNames.Count;
                if (requestedCount == 0)
                    return Task.FromResult(CommandResult.Fail(
                        "No view_ids or view_names were provided.",
                        "Provide 1-100 view IDs or names. Use revit_get_views to resolve exact IDs."));
                if (requestedCount > MaxViewsPerCall)
                    return Task.FromResult(CommandResult.Fail(
                        $"Too many requested views: {requestedCount} (max {MaxViewsPerCall}).",
                        "Split the duplication into batches of at most 100 views."));

                var targets = new List<global::Autodesk.Revit.DB.View>();
                var seen = new HashSet<long>();
                var resolutions = new List<Dictionary<string, object>>();

                foreach (var id in requestedIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = new Dictionary<string, object>
                    {
                        ["input_kind"] = "view_id",
                        ["input"] = id
                    };
                    var view = doc.GetElement(ElementIdCompatibility.Create(id)) as global::Autodesk.Revit.DB.View;
                    if (view == null)
                    {
                        row["status"] = "not_found";
                        row["reason"] = "ID is not a view.";
                    }
                    else if (view.IsTemplate)
                    {
                        row["status"] = "template";
                        row["reason"] = "View templates cannot be duplicated by this command.";
                    }
                    else if (!seen.Add(view.Id.GetValue()))
                    {
                        row["status"] = "duplicate_input";
                        row["resolved_view_id"] = view.Id.GetValue();
                    }
                    else
                    {
                        targets.Add(view);
                        row["status"] = "resolved";
                        row["resolved_view_id"] = view.Id.GetValue();
                        row["resolved_view_name"] = view.Name;
                    }
                    resolutions.Add(row);
                }

                var allViews = requestedNames.Count == 0
                    ? new List<global::Autodesk.Revit.DB.View>()
                    : new FilteredElementCollector(doc)
                        .OfClass(typeof(global::Autodesk.Revit.DB.View))
                        .Cast<global::Autodesk.Revit.DB.View>()
                        .Where(v => !v.IsTemplate)
                        .OrderBy(v => v.Name)
                        .ThenBy(v => v.Id.GetValue())
                        .ToList();

                foreach (var name in requestedNames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = new Dictionary<string, object>
                    {
                        ["input_kind"] = "view_name",
                        ["input"] = name
                    };
                    var matches = allViews
                        .Where(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var matchMode = "exact";
                    if (matches.Count == 0)
                    {
                        matches = allViews
                            .Where(v => v.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                            .ToList();
                        matchMode = "contains";
                    }

                    if (matches.Count == 0)
                    {
                        row["status"] = "not_found";
                        row["reason"] = "No non-template view matched the name.";
                    }
                    else if (matches.Count > 1)
                    {
                        row["status"] = "ambiguous";
                        row["match_mode"] = matchMode;
                        row["reason"] = "More than one view matched; retry with view_id.";
                        row["candidates"] = matches.Take(10).Select(v =>
                            new Dictionary<string, object>
                            {
                                ["id"] = v.Id.GetValue(),
                                ["name"] = v.Name
                            }).ToList();
                    }
                    else
                    {
                        var view = matches[0];
                        row["match_mode"] = matchMode;
                        if (!seen.Add(view.Id.GetValue()))
                        {
                            row["status"] = "duplicate_input";
                            row["resolved_view_id"] = view.Id.GetValue();
                        }
                        else
                        {
                            targets.Add(view);
                            row["status"] = "resolved";
                            row["resolved_view_id"] = view.Id.GetValue();
                            row["resolved_view_name"] = view.Name;
                        }
                    }
                    resolutions.Add(row);
                }

                if (targets.Count == 0)
                    return Task.FromResult(CommandResult.Fail(
                        "No unambiguous non-template target views were resolved.",
                        "Inspect the input names and retry ambiguous matches with exact view_ids from revit_get_views."));

                var suffix = "";
                if (parameters.TryGetValue("name_suffix", out var sfxObj) && sfxObj != null)
                    suffix = sfxObj.ToString();

                var activate = false;
                if (parameters.TryGetValue("activate", out var actObj) && actObj != null)
                    bool.TryParse(actObj.ToString(), out activate);

                // ── duplicate in a single transaction, per-item failure isolation ──
                var results = new List<Dictionary<string, object>>();
                int duplicated = 0, skipped = 0;
                long firstNewViewId = -1;

                using (var tx = new Transaction(doc, $"MCP: Duplicate {targets.Count} views ({optionStr})"))
                {
                    tx.Start();
                    foreach (var v in targets)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var row = new Dictionary<string, object>
                        {
                            ["source_id"] = v.Id.GetValue(),
                            ["source_name"] = v.Name,
                            ["option"] = optionStr
                        };
                        try
                        {
                            if (!v.CanViewBeDuplicated(option))
                            {
                                row["ok"] = false;
                                row["reason"] = $"View '{v.Name}' ({v.ViewType}) does not support option '{optionStr}'.";
                                skipped++;
                                results.Add(row);
                                continue;
                            }

                            var newId = v.Duplicate(option);
                            var newView = doc.GetElement(newId) as global::Autodesk.Revit.DB.View;

                            if (!string.IsNullOrEmpty(suffix) && newView != null)
                            {
                                if (TryRename(newView, v.Name + suffix, out var renameWarning))
                                    row["rename_status"] = "renamed";
                                else
                                {
                                    row["rename_status"] = "failed";
                                    row["rename_warning"] = renameWarning;
                                }
                            }

                            row["ok"] = true;
                            row["new_view_id"] = newId.GetValue();
                            row["new_view_name"] = newView != null ? newView.Name : "(unknown)";
                            duplicated++;
                            if (firstNewViewId < 0) firstNewViewId = newId.GetValue();
                        }
                        catch (Exception exItem)
                        {
                            row["ok"] = false;
                            row["reason"] = exItem.Message;
                            skipped++;
                        }
                        results.Add(row);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    tx.CommitOrThrow();
                }

                var verification = new Dictionary<string, object>();
                try
                {
                    var verified = 0;
                    var issues = new List<Dictionary<string, object>>();
                    foreach (var row in results.Where(r =>
                        r.TryGetValue("ok", out var ok) && ok is bool success && success))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var newId = ElementIdCompatibility.Create(Convert.ToInt64(row["new_view_id"]));
                        var newView = doc.GetElement(newId) as global::Autodesk.Revit.DB.View;
                        var exists = newView != null && !newView.IsTemplate;
                        var dependentMatch = true;
                        if (exists && option == ViewDuplicateOption.AsDependent)
                        {
                            var sourceId = ElementIdCompatibility.Create(Convert.ToInt64(row["source_id"]));
                            dependentMatch = newView.GetPrimaryViewId() == sourceId;
                        }
                        if (exists && dependentMatch)
                        {
                            verified++;
                        }
                        else
                        {
                            issues.Add(new Dictionary<string, object>
                            {
                                ["new_view_id"] = newId.GetValue(),
                                ["reason"] = !exists
                                    ? "Duplicated view was not found after commit."
                                    : "Dependent view does not reference the requested source."
                            });
                        }
                    }
                    verification["performed"] = true;
                    verification["expected_count"] = duplicated;
                    verification["verified_count"] = verified;
                    verification["match"] = verified == duplicated;
                    verification["issues"] = issues;
                }
                catch (Exception verificationError)
                {
                    verification["performed"] = false;
                    verification["match"] = false;
                    verification["error"] = verificationError.Message;
                }

                var data = new Dictionary<string, object>
                {
                    ["option"] = optionStr,
                    ["requested"] = requestedCount,
                    ["resolved"] = targets.Count,
                    ["duplicated"] = duplicated,
                    ["skipped"] = skipped,
                    ["resolutions"] = resolutions,
                    ["results"] = results,
                    ["mutation_committed"] = duplicated > 0,
                    ["verification"] = verification
                };

                // UIDocument action descriptor — plugin activates the view post-execution.
                if (activate && firstNewViewId > 0)
                {
                    data["action"] = "activate_view";
                    data["view_id"] = firstNewViewId;
                }

                return Task.FromResult(CommandResult.Ok(data));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(CommandResult.Fail(
                    "Operation cancelled.", "Try again with fewer views."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Failed to duplicate views: {ex.Message}",
                    "Verify view IDs with revit_get_views. 'as_dependent' is only valid for plan/section/elevation-type views."));
            }
        }

        /// <summary>Set the view name, auto-incrementing a numeric suffix on name collisions.</summary>
        private static bool TryRename(global::Autodesk.Revit.DB.View view, string baseName, out string warning)
        {
            warning = null;
            Exception lastError = null;
            for (int i = 0; i < MaxRenameAttempts; i++)
            {
                var candidate = i == 0 ? baseName : $"{baseName} ({i + 1})";
                try
                {
                    view.Name = candidate;
                    return true;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }
            warning = $"Could not assign a unique name after {MaxRenameAttempts} attempts. "
                + $"Revit kept '{view.Name}'. Last error: {lastError?.Message ?? "unknown"}";
            return false;
        }

        private static List<long> ParseIntList(Dictionary<string, object> parameters, string key)
        {
            var result = new List<long>();
            if (!parameters.TryGetValue(key, out var obj) || obj == null) return result;
            if (obj is IEnumerable<object> en)
            {
                foreach (var item in en)
                {
                    if (item == null || !long.TryParse(item.ToString(), out var id) || id <= 0)
                        throw new ArgumentException(
                            $"{key} contains an invalid positive integer element ID: '{item}'.");
                    result.Add(id);
                }
            }
            else if (obj is string s)
            {
                foreach (var part in s.Split(','))
                {
                    if (!long.TryParse(part.Trim(), out var id) || id <= 0)
                        throw new ArgumentException(
                            $"{key} contains an invalid positive integer element ID: '{part}'.");
                    result.Add(id);
                }
            }
            else if (long.TryParse(obj.ToString(), out var single) && single > 0)
            {
                result.Add(single);
            }
            else
            {
                throw new ArgumentException($"{key} must contain positive integer element IDs.");
            }
            return result;
        }

        private static List<string> ParseStringList(Dictionary<string, object> parameters, string key)
        {
            var result = new List<string>();
            if (!parameters.TryGetValue(key, out var obj) || obj == null) return result;
            if (obj is IEnumerable<object> en)
            {
                foreach (var item in en)
                {
                    var value = item?.ToString();
                    if (string.IsNullOrWhiteSpace(value))
                        throw new ArgumentException($"{key} contains an empty view name.");
                    result.Add(value.Trim());
                }
            }
            else if (obj is string s && !string.IsNullOrWhiteSpace(s))
            {
                result.Add(s.Trim());
            }
            return result;
        }
    }
}
