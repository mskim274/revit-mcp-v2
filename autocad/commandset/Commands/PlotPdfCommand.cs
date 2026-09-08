using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.PlottingServices;
using AutoCADMCP.CommandSet.Interfaces;

namespace AutoCADMCP.CommandSet.Commands
{
    /// <summary>Plot one existing layout to a verified PDF file.</summary>
    public class PlotPdfCommand : ICadCommand
    {
        public string Name => "plot_pdf";
        public string Category => "Export";

        private const string PdfDeviceName = "DWG To PDF.pc3";

        public Task<CommandResult> ExecuteAsync(
            Database db,
            Transaction tr,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            string stagingPath = null;
            try
            {
                parameters ??= new Dictionary<string, object>();
                var overwrite = GetOptionalBool(
                    parameters,
                    "overwrite",
                    false);
                var layout = ResolveLayout(
                    db,
                    tr,
                    parameters,
                    cancellationToken);
                var outputPath = ResolveOutputPath(
                    db,
                    layout.LayoutName,
                    parameters);
                var outputDirectory = Path.GetDirectoryName(outputPath);
                if (string.IsNullOrWhiteSpace(outputDirectory))
                    throw new InvalidOperationException(
                        "Could not determine the output directory.");
                Directory.CreateDirectory(outputDirectory);
                if (File.Exists(outputPath) && !overwrite)
                {
                    return Task.FromResult(CommandResult.Fail(
                        $"Output file already exists: {outputPath}",
                        "Choose a different absolute .pdf output_path, or set overwrite=true explicitly."));
                }

                if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                {
                    return Task.FromResult(CommandResult.Fail(
                        "AutoCAD already has a plot in progress.",
                        "Wait for the current plot to finish, then retry with the same inputs and a new idempotency_key."));
                }

                cancellationToken.ThrowIfCancellationRequested();
                stagingPath = Path.Combine(
                    outputDirectory,
                    $".{Path.GetFileNameWithoutExtension(outputPath)}-{Guid.NewGuid():N}.staging.pdf");
                PlotLayout(
                    db,
                    layout,
                    stagingPath,
                    cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(stagingPath))
                    throw new IOException(
                        "AutoCAD completed plotting but did not create the staging PDF.");
                var stagedBytes = new FileInfo(stagingPath).Length;
                if (stagedBytes <= 0)
                    throw new IOException("AutoCAD created an empty staging PDF.");

                File.Move(stagingPath, outputPath, overwrite);
                stagingPath = null;
                var finalInfo = new FileInfo(outputPath);
                if (!finalInfo.Exists || finalInfo.Length <= 0)
                    throw new IOException(
                        "The final PDF could not be verified after placement.");

                return Task.FromResult(CommandResult.Ok(
                    new Dictionary<string, object>
                    {
                        ["layout_name"] = layout.LayoutName,
                        ["output_path"] = outputPath,
                        ["overwrite"] = overwrite,
                        ["file_size_bytes"] = finalInfo.Length,
                        ["verification"] = new Dictionary<string, object>
                        {
                            ["performed"] = true,
                            ["exists"] = true,
                            ["size_gt_zero"] = finalInfo.Length > 0,
                            ["file_size_bytes"] = finalInfo.Length,
                            ["match"] = finalInfo.Length > 0,
                        },
                    },
                    commitTransaction: false));
            }
            catch (OperationCanceledException)
            {
                DeleteStagingFile(stagingPath);
                throw;
            }
            catch (Exception ex)
            {
                DeleteStagingFile(stagingPath);
                return Task.FromResult(CommandResult.Fail(
                    $"plot_pdf failed: {ex.Message}",
                    "Confirm the layout exists, DWG To PDF.pc3 is available, the absolute output directory is writable, and no other plot is active."));
            }
        }

        private static Layout ResolveLayout(
            Database db,
            Transaction tr,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            string requestedName = null;
            if (parameters.TryGetValue("layout_name", out var rawName) &&
                rawName != null)
            {
                if (!(rawName is string supplied) ||
                    string.IsNullOrWhiteSpace(supplied))
                {
                    throw new ArgumentException(
                        "'layout_name' must be a non-empty string when supplied.");
                }
                requestedName = supplied.Trim();
            }

            if (requestedName == null)
            {
                var currentSpace = tr.GetObject(
                    db.CurrentSpaceId,
                    OpenMode.ForRead) as BlockTableRecord;
                if (currentSpace == null ||
                    !currentSpace.IsLayout ||
                    currentSpace.LayoutId.IsNull)
                {
                    throw new InvalidOperationException(
                        "The current space does not resolve to an AutoCAD layout.");
                }
                return (Layout)tr.GetObject(
                    currentSpace.LayoutId,
                    OpenMode.ForRead);
            }

            var layoutDictionary = (DBDictionary)tr.GetObject(
                db.LayoutDictionaryId,
                OpenMode.ForRead);
            Layout match = null;
            foreach (DBDictionaryEntry entry in layoutDictionary)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = tr.GetObject(
                    entry.Value,
                    OpenMode.ForRead) as Layout;
                if (candidate == null ||
                    !string.Equals(
                        candidate.LayoutName,
                        requestedName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (match != null)
                    throw new InvalidOperationException(
                        $"Layout name '{requestedName}' is ambiguous.");
                match = candidate;
            }
            if (match == null)
                throw new ArgumentException(
                    $"No layout exactly matches '{requestedName}'.");
            return match;
        }

        private static string ResolveOutputPath(
            Database db,
            string layoutName,
            Dictionary<string, object> parameters)
        {
            if (parameters.TryGetValue("output_path", out var rawPath) &&
                rawPath != null)
            {
                if (!(rawPath is string supplied) ||
                    string.IsNullOrWhiteSpace(supplied))
                {
                    throw new ArgumentException(
                        "'output_path' must be a non-empty absolute .pdf path when supplied.");
                }
                var expanded = Environment.ExpandEnvironmentVariables(
                    supplied.Trim());
                if (!Path.IsPathRooted(expanded))
                    throw new ArgumentException(
                        "'output_path' must be an absolute path.");
                if (!string.Equals(
                        Path.GetExtension(expanded),
                        ".pdf",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        "'output_path' must end in .pdf.");
                }
                return Path.GetFullPath(expanded);
            }

            var directory = Path.Combine(
                Path.GetTempPath(),
                "cad-mcp-exports");
            var drawingName = string.IsNullOrWhiteSpace(db.Filename)
                ? "drawing"
                : Path.GetFileNameWithoutExtension(db.Filename);
            var fileName = $"{SanitizeFileName(drawingName)}-" +
                           $"{SanitizeFileName(layoutName)}-" +
                           $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.pdf";
            return Path.Combine(directory, fileName);
        }

        private static void PlotLayout(
            Database db,
            Layout layout,
            string outputPath,
            CancellationToken cancellationToken)
        {
            using (var settings = new PlotSettings(layout.ModelType))
            {
                settings.CopyFrom(layout);
                var validator = PlotSettingsValidator.Current;
                var previousMedia = settings.CanonicalMediaName;
                validator.SetPlotConfigurationName(
                    settings,
                    PdfDeviceName,
                    null);
                validator.RefreshLists(settings);

                var mediaNames = validator
                    .GetCanonicalMediaNameList(settings)
                    .Cast<string>()
                    .ToList();
                if (mediaNames.Count == 0)
                    throw new InvalidOperationException(
                        $"{PdfDeviceName} exposes no media sizes.");
                var media = mediaNames.FirstOrDefault(name =>
                                string.Equals(
                                    name,
                                    previousMedia,
                                    StringComparison.OrdinalIgnoreCase)) ??
                            mediaNames[0];
                validator.SetCanonicalMediaName(settings, media);

                if (layout.ModelType)
                {
                    validator.SetPlotType(
                        settings,
                        Autodesk.AutoCAD.DatabaseServices.PlotType.Extents);
                    validator.SetUseStandardScale(settings, true);
                    validator.SetStdScaleType(
                        settings,
                        StdScaleType.ScaleToFit);
                    validator.SetPlotCentered(settings, true);
                }
                else
                {
                    validator.SetPlotType(
                        settings,
                        Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);
                }

                using (var plotInfo = new PlotInfo
                {
                    Layout = layout.ObjectId,
                    OverrideSettings = settings,
                })
                {
                    var infoValidator = new PlotInfoValidator
                    {
                        MediaMatchingPolicy = MatchingPolicy.MatchEnabled,
                    };
                    infoValidator.Validate(plotInfo);
                    cancellationToken.ThrowIfCancellationRequested();

                    using (var engine = PlotFactory.CreatePublishEngine())
                    using (var progress = new PlotProgressDialog(
                               false,
                               1,
                               true))
                    {
                        progress.set_PlotMsgString(
                            PlotMessageIndex.DialogTitle,
                            "AutoCAD MCP PDF Plot");
                        progress.OnBeginPlot();
                        progress.IsVisible = false;
                        engine.BeginPlot(progress, null);
                        engine.BeginDocument(
                            plotInfo,
                            string.IsNullOrWhiteSpace(db.Filename)
                                ? "Drawing"
                                : db.Filename,
                            null,
                            1,
                            true,
                            outputPath);
                        using (var pageInfo = new PlotPageInfo())
                        {
                            engine.BeginPage(
                                pageInfo,
                                plotInfo,
                                true,
                                null);
                            engine.BeginGenerateGraphics(null);
                            engine.EndGenerateGraphics(null);
                            engine.EndPage(null);
                        }
                        engine.EndDocument(null);
                        engine.EndPlot(null);
                        progress.OnEndPlot();
                    }
                }
            }
        }

        private static bool GetOptionalBool(
            Dictionary<string, object> parameters,
            string key,
            bool defaultValue)
        {
            if (!parameters.TryGetValue(key, out var raw) || raw == null)
                return defaultValue;
            if (raw is bool value)
                return value;
            throw new ArgumentException($"'{key}' must be a boolean.");
        }

        private static string SanitizeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(value
                .Select(character => invalid.Contains(character)
                    ? '_'
                    : character)
                .ToArray());
        }

        private static void DeleteStagingFile(string stagingPath)
        {
            if (string.IsNullOrWhiteSpace(stagingPath))
                return;
            try
            {
                if (File.Exists(stagingPath))
                    File.Delete(stagingPath);
            }
            catch
            {
                // Preserve the original plot error; the exact staging path is
                // unique and is never the caller's requested output file.
            }
        }
    }
}
