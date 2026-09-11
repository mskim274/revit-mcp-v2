using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.Export
{
    /// <summary>
    /// Exports one active or explicitly resolved non-template view with
    /// Document.ExportImage. SetOfViews avoids changing the Revit UI view.
    /// </summary>
    public class ExportViewCommand : IRevitCommand
    {
        public string Name => "export_view";
        public string Category => "Export";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            string temporaryExportPath = null;
            string temporaryDirectory = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                parameters = parameters ?? new Dictionary<string, object>();

                if (!TryGetOptionalNonBlankString(
                        parameters,
                        "view_name",
                        out var viewName,
                        out var viewNameError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        viewNameError,
                        "Provide a non-empty view_name, or omit it to use view_id or the active view."));
                }

                var view = ResolveView(doc, parameters, viewName, cancellationToken, out var failReason);
                if (view == null)
                {
                    var available = ListAvailableViews(doc, 8, cancellationToken);
                    var availableText = available.Count == 0
                        ? "No non-template views are available."
                        : $"Available views: {string.Join(", ", available)}.";
                    return Task.FromResult(CommandResult.Fail(
                        failReason,
                        $"{availableText} Use revit_get_views for the full list. view_name matches exact first, then contains (case-insensitive)."));
                }
                if (view.IsTemplate)
                {
                    return Task.FromResult(CommandResult.Fail(
                        $"View '{view.Name}' is a template and cannot be exported.",
                        "Choose a non-template view from revit_get_views, or omit the target to export the active view."));
                }
                if (!view.CanBePrinted)
                {
                    return Task.FromResult(CommandResult.Fail(
                        $"View '{view.Name}' cannot be printed or exported as an image.",
                        "Choose a printable non-template model, drafting, legend, schedule, or sheet view."));
                }

                var format = parameters.TryGetValue("format", out var formatObject) && formatObject != null
                    ? formatObject.ToString().ToLowerInvariant()
                    : "png";
                if (format != "png" && format != "jpg")
                {
                    return Task.FromResult(CommandResult.Fail(
                        $"Invalid format '{format}'.",
                        "Use format='png' (default) or format='jpg'."));
                }

                if (!TryGetOptionalStrictBool(
                        parameters,
                        "overwrite",
                        false,
                        out var overwrite,
                        out var overwriteError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        overwriteError,
                        "Use overwrite=true or overwrite=false. The safe default is false."));
                }

                var outputDir = parameters.TryGetValue("output_dir", out var outputDirObject)
                    ? outputDirObject as string
                    : null;
                if (outputDirObject != null && string.IsNullOrWhiteSpace(outputDir))
                {
                    return Task.FromResult(CommandResult.Fail(
                        "output_dir must be a non-empty string when supplied.",
                        "Provide a valid directory path, or omit output_dir to use %TEMP%\\revit-mcp-exports."));
                }
                if (string.IsNullOrWhiteSpace(outputDir))
                    outputDir = Path.Combine(Path.GetTempPath(), "revit-mcp-exports");
                outputDir = Path.GetFullPath(outputDir.Trim());

                var extension = format == "png" ? ".png" : ".jpg";
                var safeName = SanitizeFileName(view.Name);
                if (string.IsNullOrWhiteSpace(safeName))
                    safeName = $"view-{view.Id.GetValue()}";
                var finalPath = Path.Combine(outputDir, safeName + extension);

                if (File.Exists(finalPath) && !overwrite)
                {
                    return Task.FromResult(CommandResult.Fail(
                        $"Export target already exists: {finalPath}",
                        "Set overwrite=true to replace it, choose another output_dir, or rename the existing file."));
                }

                Directory.CreateDirectory(outputDir);
                temporaryDirectory = Path.Combine(
                    outputDir,
                    "revit-mcp-export-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporaryDirectory);
                var temporaryBase = Path.Combine(temporaryDirectory, "view");
                var fileType = format == "png"
                    ? ImageFileType.PNG
                    : ImageFileType.JPEGLossless;

                using (var options = new ImageExportOptions
                {
                    ExportRange = ExportRange.SetOfViews,
                    FilePath = temporaryBase,
                    ShouldCreateWebSite = false,
                    HLRandWFViewsFileType = fileType,
                    ShadowViewsFileType = fileType,
                    ZoomType = ZoomFitType.FitToPage,
                    FitDirection = FitDirectionType.Horizontal,
                    PixelSize = 1920
                })
                {
                    options.SetViewsAndSheets(new List<ElementId> { view.Id });
                    cancellationToken.ThrowIfCancellationRequested();
                    doc.ExportImage(options);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var exportedImages = Directory.GetFiles(temporaryDirectory)
                    .Where(path => IsRequestedImage(path, format))
                    .ToList();
                if (exportedImages.Count != 1)
                {
                    return Task.FromResult(CommandResult.Fail(
                        $"Revit produced {exportedImages.Count} candidate image files for view '{view.Name}'; expected exactly one.",
                        "Check that the view is printable and has visible content, then retry with a new idempotency_key."));
                }
                temporaryExportPath = exportedImages[0];

                if (File.Exists(finalPath))
                    File.Replace(temporaryExportPath, finalPath, null);
                else
                    File.Move(temporaryExportPath, finalPath);
                temporaryExportPath = null;

                var fileInfo = new FileInfo(finalPath);
                var verified = fileInfo.Exists && fileInfo.Length > 0;
                var verification = new Dictionary<string, object>
                {
                    ["performed"] = true,
                    ["file_exists"] = fileInfo.Exists,
                    ["file_size_bytes"] = fileInfo.Exists ? fileInfo.Length : 0,
                    ["non_empty"] = verified,
                    ["overwrite"] = overwrite
                };
                if (!verified)
                {
                    return Task.FromResult(CommandResult.Fail(
                        $"Image export verification failed for: {finalPath}",
                        "Retry with overwrite=true and a new idempotency_key, or choose another output_dir."));
                }

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["view_id"] = view.Id.GetValue(),
                    ["view_name"] = view.Name,
                    ["view_type"] = view.ViewType.ToString(),
                    ["format"] = format,
                    ["image_path"] = finalPath,
                    ["file_written"] = true,
                    ["verification"] = verification
                }));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(CommandResult.Fail(
                    "View export was cancelled due to timeout.",
                    "Check the output directory before retrying, then reuse the same idempotency_key to avoid duplicate side effects."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Failed to export view: {ex.Message}",
                    "Ensure the view is a printable non-template view and the output directory is writable. Use overwrite=true only when replacement is intended."));
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(temporaryExportPath))
                {
                    try
                    {
                        if (File.Exists(temporaryExportPath))
                            File.Delete(temporaryExportPath);
                    }
                    catch
                    {
                        // Best-effort cleanup of this command's GUID-named temp file.
                    }
                }
                if (!string.IsNullOrWhiteSpace(temporaryDirectory))
                {
                    try
                    {
                        if (Directory.Exists(temporaryDirectory))
                        {
                            foreach (var path in Directory.GetFiles(temporaryDirectory))
                                File.Delete(path);
                            Directory.Delete(temporaryDirectory, false);
                        }
                    }
                    catch
                    {
                        // Best-effort cleanup; never hide the command result.
                    }
                }
            }
        }

        private static bool IsRequestedImage(string path, string format)
        {
            var extension = Path.GetExtension(path);
            if (format == "png")
                return extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
            return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        private static global::Autodesk.Revit.DB.View ResolveView(
            Document doc,
            Dictionary<string, object> parameters,
            string viewName,
            CancellationToken cancellationToken,
            out string failReason)
        {
            failReason = null;
            if (parameters.TryGetValue("view_id", out var viewIdObject) && viewIdObject != null)
            {
                if (!TryParseElementId(viewIdObject, out var viewId))
                {
                    failReason = $"Invalid view_id: {viewIdObject}";
                    return null;
                }

                var element = doc.GetElement(ElementIdCompatibility.Create(viewId));
                if (!(element is global::Autodesk.Revit.DB.View idView))
                {
                    failReason = $"Element id {viewId} is not a Revit view (got {element?.GetType().Name ?? "null"}).";
                    return null;
                }
                if (idView.IsTemplate)
                {
                    failReason = $"View id {viewId} ('{idView.Name}') is a template.";
                    return null;
                }
                return idView;
            }

            if (string.IsNullOrWhiteSpace(viewName))
            {
                var activeView = doc.ActiveView;
                if (activeView == null)
                    failReason = "The document has no active view.";
                return activeView;
            }

            var exact = new List<global::Autodesk.Revit.DB.View>();
            var contains = new List<global::Autodesk.Revit.DB.View>();
            foreach (var candidate in new FilteredElementCollector(doc)
                .OfClass(typeof(global::Autodesk.Revit.DB.View))
                .Cast<global::Autodesk.Revit.DB.View>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate.IsTemplate)
                    continue;
                if (candidate.Name.Equals(viewName, StringComparison.OrdinalIgnoreCase))
                    exact.Add(candidate);
                else if (candidate.Name.IndexOf(viewName, StringComparison.OrdinalIgnoreCase) >= 0)
                    contains.Add(candidate);
            }

            if (exact.Count == 1)
                return exact[0];
            if (exact.Count > 1)
            {
                failReason = $"View name '{viewName}' has {exact.Count} exact matches; use view_id.";
                return null;
            }
            if (contains.Count == 1)
                return contains[0];
            if (contains.Count > 1)
            {
                failReason = $"View name '{viewName}' is ambiguous; matches {contains.Count}: " +
                    string.Join(", ", contains.Take(8).Select(item => $"'{item.Name}' (id {item.Id.GetValue()})"));
                return null;
            }

            failReason = $"View '{viewName}' was not found.";
            return null;
        }

        private static List<string> ListAvailableViews(
            Document doc,
            int limit,
            CancellationToken cancellationToken)
        {
            var names = new List<string>();
            foreach (var view in new FilteredElementCollector(doc)
                .OfClass(typeof(global::Autodesk.Revit.DB.View))
                .Cast<global::Autodesk.Revit.DB.View>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!view.IsTemplate && view.CanBePrinted)
                    names.Add(view.Name);
            }
            return names
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(name?.Length ?? 0);
            foreach (var character in name ?? "")
                builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
            return builder.ToString().Trim().TrimEnd('.');
        }

        private static bool TryParseElementId(object raw, out long value)
        {
            value = 0;
            switch (raw)
            {
                case int intValue when intValue > 0:
                    value = intValue;
                    return true;
                case long longValue when longValue > 0:
                    value = longValue;
                    return true;
                case double doubleValue
                    when !double.IsNaN(doubleValue) &&
                         !double.IsInfinity(doubleValue) &&
                         doubleValue == Math.Truncate(doubleValue) &&
                         doubleValue > 0 &&
                         doubleValue <= long.MaxValue:
                    value = (long)doubleValue;
                    return true;
                case string text when long.TryParse(text, out var parsed) && parsed > 0:
                    value = parsed;
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryGetOptionalNonBlankString(
            Dictionary<string, object> parameters,
            string key,
            out string value,
            out string error)
        {
            value = null;
            error = null;
            if (!parameters.TryGetValue(key, out var raw) || raw == null)
                return true;
            if (!(raw is string text) || string.IsNullOrWhiteSpace(text))
            {
                error = $"{key} must be a non-empty string when supplied.";
                return false;
            }
            value = text.Trim();
            return true;
        }

        private static bool TryGetOptionalStrictBool(
            Dictionary<string, object> parameters,
            string key,
            bool defaultValue,
            out bool value,
            out string error)
        {
            value = defaultValue;
            error = null;
            if (!parameters.TryGetValue(key, out var raw))
                return true;
            if (raw is bool boolValue)
            {
                value = boolValue;
                return true;
            }
            error = $"{key} must be true or false when supplied.";
            return false;
        }
    }
}
