using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace RevitMCP.Updater;

internal static class Program
{
    private static readonly TimeSpan WaitPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromMinutes(5);
    private const int MaxArchiveEntries = 10_000;
    private const long MaxArchiveUncompressedBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxSingleEntryBytes = 512L * 1024 * 1024;
    private const string RevitReleaseManifestFileName =
        "RevitMCP.release-manifest.json";
    private const string AutoCadReleaseManifestFileName =
        "AutoCADMCP.release-manifest.json";

    public static int Main(string[] args)
    {
        try
        {
            var options = ParseArgs(args);
            if (options == null) return 2;

            var releaseManifestFileName =
                ResolveReleaseManifestFileName(options.Product);
            if (releaseManifestFileName == null)
            {
                Log(
                    $"ERROR: unsupported product '{options.Product}'. " +
                    "Expected 'revit' or 'autocad'.");
                return 2;
            }

            var resolved = ResolveProfile(options);
            if (resolved == null) return 2;

            Log("CAD MCP Updater starting.");
            Log($"  Zip:           {options.ZipPath}");
            Log($"  Product:       {options.Product}");
            Log($"  Process name:  {resolved.ProcessName}");
            Log($"  Target:        {resolved.AddinsDir}");
            Log($"  Wait first:    {options.WaitForExit}");

            if (!File.Exists(options.ZipPath))
            {
                Log($"ERROR: zip not found at {options.ZipPath}");
                return 3;
            }

            if (options.WaitForExit &&
                !WaitForProcessExit(resolved.ProcessName))
            {
                Log(
                    $"ERROR: {resolved.ProcessName} did not exit within timeout. " +
                    "Aborting update.");
                return 4;
            }

            VerifyExpectedArchive(
                options.ZipPath,
                options.ExpectedSha256,
                options.ExpectedSize);
            ApplyUpdate(
                options.ZipPath,
                resolved.AddinsDir,
                releaseManifestFileName);
            Log($"Update complete. You can start {resolved.FriendlyName} now.");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private sealed record ResolvedProfile(
        string ProcessName,
        string AddinsDir,
        string FriendlyName);

    private static string? ResolveReleaseManifestFileName(string product)
    {
        return product.Trim().ToLowerInvariant() switch
        {
            "revit" => RevitReleaseManifestFileName,
            "autocad" => AutoCadReleaseManifestFileName,
            _ => null
        };
    }

    public static (string processName, string addinsDir, string friendlyName)?
        ResolveTarget(
            string product,
            string? revitYear,
            string? bundleName,
            string appDataRoot)
    {
        if (string.IsNullOrWhiteSpace(product) ||
            string.IsNullOrWhiteSpace(appDataRoot))
        {
            return null;
        }

        switch (product.ToLowerInvariant())
        {
            case "revit":
                if (!IsFourDigitYear(revitYear))
                    return null;
                var revitDirectory = Path.Combine(
                    appDataRoot,
                    "Autodesk",
                    "Revit",
                    "Addins",
                    revitYear!);
                return ("Revit", revitDirectory, $"Revit {revitYear}");

            case "autocad":
                var bundle = string.IsNullOrWhiteSpace(bundleName)
                    ? "AutoCADMCP"
                    : bundleName!;
                if (!IsSafeSinglePathSegment(bundle))
                    return null;
                if (!bundle.EndsWith(
                        ".bundle",
                        StringComparison.OrdinalIgnoreCase))
                {
                    bundle += ".bundle";
                }
                var autocadDirectory = Path.Combine(
                    appDataRoot,
                    "Autodesk",
                    "ApplicationPlugins",
                    bundle);
                return ("acad", autocadDirectory, "AutoCAD");

            default:
                return null;
        }
    }

    private static ResolvedProfile? ResolveProfile(Options options)
    {
        if (!string.IsNullOrWhiteSpace(options.AddinsDirOverride))
        {
            if (!Path.IsPathFullyQualified(options.AddinsDirOverride))
            {
                Log("ERROR: --addins-dir must be an absolute path.");
                return null;
            }

            var overrideProcessName = options.ProcessNameOverride ?? "Revit";
            return new ResolvedProfile(
                overrideProcessName,
                Path.GetFullPath(options.AddinsDirOverride),
                "the CAD application");
        }

        var appData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
        var resolved = ResolveTarget(
            options.Product,
            options.RevitYear,
            options.BundleName,
            appData);
        if (resolved == null)
        {
            Log(
                $"ERROR: cannot resolve target for product '{options.Product}'. " +
                "Check --revit-year or --bundle-name.");
            return null;
        }

        var (processName, addinsDirectory, friendlyName) = resolved.Value;
        if (!string.IsNullOrWhiteSpace(options.ProcessNameOverride))
            processName = options.ProcessNameOverride!;

        return new ResolvedProfile(
            processName,
            Path.GetFullPath(addinsDirectory),
            friendlyName);
    }

    private sealed class ArchiveFile
    {
        public required ZipArchiveEntry Entry { get; init; }
        public required string RelativePath { get; init; }
        public required string DestinationPath { get; init; }
        public required string StagedPath { get; set; }
        public required long Length { get; init; }
        public string Sha256 { get; set; } = "";
    }

    private sealed class AppliedFile
    {
        public required string DestinationPath { get; init; }
        public required string RollbackPath { get; init; }
        public bool HadOriginal { get; init; }
        public bool RemovedByUpdate { get; init; }
        public bool Applied { get; set; }
    }

    private sealed class StaleManifestFile
    {
        public required string RelativePath { get; init; }
        public required string DestinationPath { get; init; }
    }

    private sealed class ReleaseManifest
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("product")]
        public string Product { get; set; } = "";

        [JsonPropertyName("files")]
        public List<ReleaseManifestFile> Files { get; set; } = new();
    }

    private sealed class ReleaseManifestFile
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = "";
    }

    /// <summary>
    /// Validates every archive path before touching plugin files, extracts into
    /// a same-volume staging directory, then replaces each file atomically.
    /// Any failure restores all files already replaced during this transaction.
    /// </summary>
    private static void ApplyUpdate(
        string zipPath,
        string targetDirectory,
        string releaseManifestFileName)
    {
        var targetRoot = EnsureTrailingSeparator(
            Path.GetFullPath(targetDirectory));
        var targetWithoutSeparator = targetRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var volumeRoot = Path.GetPathRoot(targetWithoutSeparator);
        if (string.Equals(
                targetWithoutSeparator,
                volumeRoot?.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Refusing to update a filesystem root directory.");
        }

        var targetParent = Directory.GetParent(targetWithoutSeparator)?.FullName
                           ?? throw new InvalidOperationException(
                               "Target directory must have a parent.");
        Directory.CreateDirectory(targetParent);

        var transactionId = Guid.NewGuid().ToString("N");
        var workingRoot = Path.Combine(
            targetParent,
            ".revit-mcp-update-" + transactionId);
        var stagingRoot = Path.Combine(workingRoot, "staging");
        var rollbackRoot = Path.Combine(workingRoot, "rollback");
        var updateSucceeded = false;
        var preserveRecoveryFiles = false;

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var files = ValidateArchive(archive, targetRoot);
            if (files.Count == 0)
                throw new InvalidDataException("Update archive contains no files.");

            Directory.CreateDirectory(stagingRoot);
            ExtractToStaging(files, stagingRoot);
            var staleFiles = FindStaleManifestFiles(
                files,
                targetRoot,
                releaseManifestFileName);

            Directory.CreateDirectory(targetWithoutSeparator);
            EnsureNoReparsePoint(targetWithoutSeparator);
            ApplyStagedFiles(
                files,
                staleFiles,
                targetRoot,
                rollbackRoot);
            updateSucceeded = true;
        }
        catch (AggregateException)
        {
            preserveRecoveryFiles = true;
            Log(
                $"WARNING: rollback was incomplete. Recovery files were kept at " +
                $"{rollbackRoot}");
            throw;
        }
        finally
        {
            if (!preserveRecoveryFiles)
                SafeDeleteWorkingDirectory(workingRoot, targetParent);
            if (!updateSucceeded && !preserveRecoveryFiles)
                Log("Update did not complete; staged files were discarded.");
        }
    }

    private static List<ArchiveFile> ValidateArchive(
        ZipArchive archive,
        string targetRoot)
    {
        if (archive.Entries.Count > MaxArchiveEntries)
        {
            throw new InvalidDataException(
                $"Archive has {archive.Entries.Count} entries; " +
                $"maximum is {MaxArchiveEntries}.");
        }

        var files = new List<ArchiveFile>();
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            targetRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
        };
        long totalLength = 0;

        foreach (var entry in archive.Entries)
        {
            if (IsSymbolicLinkOrReparsePoint(entry))
            {
                throw new InvalidDataException(
                    $"Archive contains a link/reparse entry: {entry.FullName}");
            }

            var relativePath = NormalizeAndValidateRelativePath(entry.FullName);
            if (relativePath.Length == 0)
                continue;

            var destinationPath = Path.GetFullPath(
                Path.Combine(targetRoot, relativePath));
            EnsureContainedPath(targetRoot, destinationPath, entry.FullName);

            var isDirectory = string.IsNullOrEmpty(entry.Name);
            if (isDirectory)
            {
                if (filePaths.Contains(destinationPath))
                {
                    throw new InvalidDataException(
                        $"Archive path is both file and directory: {entry.FullName}");
                }
                directoryPaths.Add(destinationPath);
                continue;
            }

            if (entry.Length < 0 || entry.Length > MaxSingleEntryBytes)
            {
                throw new InvalidDataException(
                    $"Archive entry is too large: {entry.FullName}");
            }

            checked { totalLength += entry.Length; }
            if (totalLength > MaxArchiveUncompressedBytes)
            {
                throw new InvalidDataException(
                    "Archive exceeds the maximum uncompressed size.");
            }

            if (!filePaths.Add(destinationPath) ||
                directoryPaths.Contains(destinationPath))
            {
                throw new InvalidDataException(
                    $"Archive contains a duplicate/colliding path: {entry.FullName}");
            }

            var parent = Path.GetDirectoryName(destinationPath);
            while (!string.IsNullOrEmpty(parent) &&
                   parent.StartsWith(
                       targetRoot,
                       StringComparison.OrdinalIgnoreCase))
            {
                if (filePaths.Contains(parent))
                {
                    throw new InvalidDataException(
                        $"Archive file blocks a child path: {entry.FullName}");
                }
                directoryPaths.Add(parent);
                if (string.Equals(
                        EnsureTrailingSeparator(parent),
                        targetRoot,
                        StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                parent = Path.GetDirectoryName(parent);
            }

            files.Add(new ArchiveFile
            {
                Entry = entry,
                RelativePath = relativePath,
                DestinationPath = destinationPath,
                StagedPath = "",
                Length = entry.Length
            });
        }

        // Detect a file entry that appeared before a later child file.
        foreach (var file in files)
        {
            var parent = Path.GetDirectoryName(file.DestinationPath);
            while (!string.IsNullOrEmpty(parent) &&
                   parent.StartsWith(
                       targetRoot,
                       StringComparison.OrdinalIgnoreCase))
            {
                if (filePaths.Contains(parent))
                {
                    throw new InvalidDataException(
                        $"Archive has a file/directory collision at {parent}.");
                }
                if (string.Equals(
                        EnsureTrailingSeparator(parent),
                        targetRoot,
                        StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                parent = Path.GetDirectoryName(parent);
            }
        }

        return files;
    }

    private static void ExtractToStaging(
        IEnumerable<ArchiveFile> files,
        string stagingRoot)
    {
        var stagingPrefix = EnsureTrailingSeparator(
            Path.GetFullPath(stagingRoot));

        foreach (var file in files)
        {
            var stagedPath = Path.GetFullPath(
                Path.Combine(stagingPrefix, file.RelativePath));
            EnsureContainedPath(
                stagingPrefix,
                stagedPath,
                file.Entry.FullName);
            Directory.CreateDirectory(
                Path.GetDirectoryName(stagedPath)
                ?? throw new InvalidDataException("Invalid staged path."));

            using (var source = file.Entry.Open())
            using (var destination = new FileStream(
                       stagedPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                source.CopyTo(destination);
            }

            var actualLength = new FileInfo(stagedPath).Length;
            if (actualLength != file.Length)
            {
                throw new InvalidDataException(
                    $"Extracted length mismatch for {file.Entry.FullName}.");
            }

            file.StagedPath = stagedPath;
            file.Sha256 = ComputeFileSha256(stagedPath);
            Log($"  Staged:       {file.RelativePath}");
        }
    }

    private static List<StaleManifestFile> FindStaleManifestFiles(
        IReadOnlyList<ArchiveFile> files,
        string targetRoot,
        string releaseManifestFileName)
    {
        var staleFiles = new List<StaleManifestFile>();
        try
        {
            var newManifestFile = files.FirstOrDefault(file =>
                string.Equals(
                    file.RelativePath,
                    releaseManifestFileName,
                    StringComparison.OrdinalIgnoreCase));
            var oldManifestPath = Path.Combine(
                targetRoot,
                releaseManifestFileName);
            if (newManifestFile == null ||
                !File.Exists(newManifestFile.StagedPath) ||
                !File.Exists(oldManifestPath))
            {
                return staleFiles;
            }

            var newManifest = JsonSerializer.Deserialize<ReleaseManifest>(
                File.ReadAllText(newManifestFile.StagedPath));
            var oldManifest = JsonSerializer.Deserialize<ReleaseManifest>(
                File.ReadAllText(oldManifestPath));
            if (!IsCompatibleManifest(oldManifest) ||
                !IsCompatibleManifest(newManifest) ||
                !string.Equals(
                    oldManifest!.Product,
                    newManifest!.Product,
                    StringComparison.OrdinalIgnoreCase))
            {
                Log(
                    "  Stale prune: skipped because release manifests are " +
                    "missing, invalid, or for different products.");
                return staleFiles;
            }

            if (!ManifestMatchesStagedFiles(
                    newManifest!,
                    files,
                    releaseManifestFileName))
            {
                Log(
                    "  Stale prune: skipped because the new release manifest " +
                    "does not match the staged archive.");
                return staleFiles;
            }

            var currentNames = new HashSet<string>(
                newManifest.Files.Select(file => file.Name),
                StringComparer.OrdinalIgnoreCase);
            foreach (var oldFile in oldManifest.Files)
            {
                if (currentNames.Contains(oldFile.Name))
                    continue;
                if (!IsSha256Hex(oldFile.Sha256) || oldFile.Size < 0)
                {
                    Log(
                        $"  Stale prune: skipped invalid manifest entry " +
                        $"'{oldFile.Name}'.");
                    continue;
                }

                var relativePath =
                    NormalizeAndValidateRelativePath(oldFile.Name);
                var destinationPath = Path.GetFullPath(
                    Path.Combine(targetRoot, relativePath));
                EnsureContainedPath(
                    targetRoot,
                    destinationPath,
                    oldFile.Name);
                if (!File.Exists(destinationPath))
                    continue;

                var actualInfo = new FileInfo(destinationPath);
                if (actualInfo.Length != oldFile.Size ||
                    !string.Equals(
                        ComputeFileSha256(destinationPath),
                        oldFile.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    Log(
                        $"  Stale prune: preserved locally modified file " +
                        $"'{relativePath}'.");
                    continue;
                }

                staleFiles.Add(new StaleManifestFile
                {
                    RelativePath = relativePath,
                    DestinationPath = destinationPath
                });
            }
        }
        catch (Exception manifestError)
        {
            // Pruning is an upgrade hygiene feature. A damaged/legacy
            // manifest must not prevent safe replacement of current files.
            Log(
                $"  Stale prune: skipped ({manifestError.Message}).");
            staleFiles.Clear();
        }

        return staleFiles;
    }

    private static bool IsCompatibleManifest(ReleaseManifest? manifest)
    {
        return manifest != null &&
               manifest.SchemaVersion == 1 &&
               !string.IsNullOrWhiteSpace(manifest.Product) &&
               manifest.Files != null &&
               manifest.Files.All(file =>
                   file != null &&
                   !string.IsNullOrWhiteSpace(file.Name));
    }

    private static bool ManifestMatchesStagedFiles(
        ReleaseManifest manifest,
        IReadOnlyList<ArchiveFile> files,
        string releaseManifestFileName)
    {
        var stagedFiles = files
            .Where(file => !string.Equals(
                file.RelativePath,
                releaseManifestFileName,
                StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                file => file.RelativePath,
                StringComparer.OrdinalIgnoreCase);
        if (manifest.Files.Count != stagedFiles.Count)
            return false;

        var manifestPaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var manifestFile in manifest.Files)
        {
            string relativePath;
            try
            {
                relativePath =
                    NormalizeAndValidateRelativePath(manifestFile.Name);
            }
            catch
            {
                return false;
            }

            if (!manifestPaths.Add(relativePath) ||
                !stagedFiles.TryGetValue(relativePath, out var staged) ||
                manifestFile.Size != staged.Length ||
                !IsSha256Hex(manifestFile.Sha256) ||
                !string.Equals(
                    manifestFile.Sha256,
                    staged.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static void ApplyStagedFiles(
        IReadOnlyList<ArchiveFile> files,
        IReadOnlyList<StaleManifestFile> staleFiles,
        string targetRoot,
        string rollbackRoot)
    {
        var journal = new List<AppliedFile>(files.Count);
        try
        {
            foreach (var file in files)
            {
                EnsureContainedPath(
                    targetRoot,
                    file.DestinationPath,
                    file.RelativePath);
                EnsureExistingParentsAreSafe(
                    targetRoot,
                    file.DestinationPath);

                if (Directory.Exists(file.DestinationPath))
                {
                    throw new IOException(
                        $"A directory blocks update file {file.RelativePath}.");
                }

                var destinationParent =
                    Path.GetDirectoryName(file.DestinationPath)
                    ?? throw new InvalidDataException("Invalid destination path.");
                Directory.CreateDirectory(destinationParent);
                EnsureNoReparsePoint(destinationParent);

                var hadOriginal = File.Exists(file.DestinationPath);
                if (hadOriginal)
                    EnsureNoReparsePoint(file.DestinationPath);

                var rollbackPath = Path.Combine(
                    rollbackRoot,
                    file.RelativePath);
                var applied = new AppliedFile
                {
                    DestinationPath = file.DestinationPath,
                    RollbackPath = rollbackPath,
                    HadOriginal = hadOriginal
                };
                journal.Add(applied);

                if (hadOriginal)
                {
                    Directory.CreateDirectory(
                        Path.GetDirectoryName(rollbackPath)!);
                    File.Copy(
                        file.DestinationPath,
                        rollbackPath,
                        overwrite: false);

                    // Preserve the documented manual recovery copy.
                    CreateManualBackup(
                        file.DestinationPath,
                        targetRoot);

                    File.Replace(
                        file.StagedPath,
                        file.DestinationPath,
                        null,
                        ignoreMetadataErrors: true);
                    Log($"  Replaced:     {file.RelativePath}");
                }
                else
                {
                    File.Move(file.StagedPath, file.DestinationPath);
                    Log($"  Installed:    {file.RelativePath}");
                }

                applied.Applied = true;
                var installedHash = ComputeFileSha256(file.DestinationPath);
                if (!string.Equals(
                        installedHash,
                        file.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException(
                        $"Post-install hash verification failed for {file.RelativePath}.");
                }
            }

            foreach (var staleFile in staleFiles)
            {
                EnsureContainedPath(
                    targetRoot,
                    staleFile.DestinationPath,
                    staleFile.RelativePath);
                EnsureExistingParentsAreSafe(
                    targetRoot,
                    staleFile.DestinationPath);
                if (!File.Exists(staleFile.DestinationPath))
                    continue;
                EnsureNoReparsePoint(staleFile.DestinationPath);

                var rollbackPath = Path.Combine(
                    rollbackRoot,
                    "__stale__",
                    staleFile.RelativePath);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(rollbackPath)!);
                File.Copy(
                    staleFile.DestinationPath,
                    rollbackPath,
                    overwrite: false);
                CreateManualBackup(
                    staleFile.DestinationPath,
                    targetRoot);

                var applied = new AppliedFile
                {
                    DestinationPath = staleFile.DestinationPath,
                    RollbackPath = rollbackPath,
                    HadOriginal = true,
                    RemovedByUpdate = true
                };
                journal.Add(applied);
                File.Delete(staleFile.DestinationPath);
                applied.Applied = true;
                Log($"  Pruned stale: {staleFile.RelativePath}");
            }
        }
        catch (Exception updateError)
        {
            var rollbackErrors = RollBackAppliedFiles(journal);
            if (rollbackErrors.Count > 0)
            {
                var allErrors = new List<Exception> { updateError };
                allErrors.AddRange(rollbackErrors);
                throw new AggregateException(
                    "Update failed and one or more rollback operations also failed.",
                    allErrors);
            }

            throw new InvalidOperationException(
                "Update failed; all applied files were rolled back.",
                updateError);
        }
    }

    private static List<Exception> RollBackAppliedFiles(
        IReadOnlyList<AppliedFile> journal)
    {
        var errors = new List<Exception>();
        for (var index = journal.Count - 1; index >= 0; index--)
        {
            var item = journal[index];
            if (!item.Applied) continue;

            try
            {
                if (item.HadOriginal)
                {
                    if (!File.Exists(item.RollbackPath))
                    {
                        throw new FileNotFoundException(
                            "Rollback copy is missing.",
                            item.RollbackPath);
                    }

                    var restorePath =
                        item.DestinationPath + ".restore-" +
                        Guid.NewGuid().ToString("N");
                    File.Copy(item.RollbackPath, restorePath, overwrite: false);
                    if (File.Exists(item.DestinationPath))
                    {
                        File.Replace(
                            restorePath,
                            item.DestinationPath,
                            null,
                            ignoreMetadataErrors: true);
                    }
                    else
                    {
                        File.Move(restorePath, item.DestinationPath);
                    }

                    Log(
                        item.RemovedByUpdate
                            ? $"  Restored:     {Path.GetFileName(item.DestinationPath)}"
                            : $"  Rolled back:  {Path.GetFileName(item.DestinationPath)}");
                }
                else if (File.Exists(item.DestinationPath))
                {
                    File.Delete(item.DestinationPath);
                    Log($"  Removed:      {Path.GetFileName(item.DestinationPath)}");
                }
            }
            catch (Exception rollbackError)
            {
                errors.Add(rollbackError);
            }
        }
        return errors;
    }

    private static string NormalizeAndValidateRelativePath(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            return "";

        var normalized = fullName
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
        {
            throw new InvalidDataException(
                $"Archive contains a rooted path: {fullName}");
        }

        var segments = normalized.Split(
            new[] { Path.DirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return "";

        foreach (var segment in segments)
        {
            if (segment == "." || segment == ".." ||
                !IsSafeSinglePathSegment(segment))
            {
                throw new InvalidDataException(
                    $"Archive contains an unsafe path segment: {fullName}");
            }
        }

        return string.Join(
            Path.DirectorySeparatorChar.ToString(),
            segments);
    }

    private static bool IsSafeSinglePathSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) ||
            segment.EndsWith(" ", StringComparison.Ordinal) ||
            segment.EndsWith(".", StringComparison.Ordinal) ||
            segment.IndexOf(':') >= 0 ||
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            segment.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
            segment.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
        {
            return false;
        }

        var stem = segment.Split('.')[0];
        var reserved = new[]
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5",
            "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5",
            "LPT6", "LPT7", "LPT8", "LPT9"
        };
        return !reserved.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsSymbolicLinkOrReparsePoint(ZipArchiveEntry entry)
    {
        const int unixFileTypeMask = 0xF000;
        const int unixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & unixFileTypeMask;
        var windowsAttributes =
            (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        return unixMode == unixSymbolicLink ||
               (windowsAttributes & FileAttributes.ReparsePoint) != 0;
    }

    private static void EnsureContainedPath(
        string root,
        string candidate,
        string archiveName)
    {
        var rootPrefix = EnsureTrailingSeparator(Path.GetFullPath(root));
        var fullCandidate = Path.GetFullPath(candidate);
        if (!fullCandidate.StartsWith(
                rootPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Archive entry escapes the target directory: {archiveName}");
        }
    }

    private static void EnsureExistingParentsAreSafe(
        string root,
        string destination)
    {
        var rootWithoutSeparator = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        EnsureNoReparsePoint(rootWithoutSeparator);

        var parent = Path.GetDirectoryName(destination);
        var existing = new Stack<string>();
        while (!string.IsNullOrEmpty(parent) &&
               parent.StartsWith(
                   rootWithoutSeparator,
                   StringComparison.OrdinalIgnoreCase))
        {
            existing.Push(parent);
            if (string.Equals(
                    parent,
                    rootWithoutSeparator,
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            parent = Path.GetDirectoryName(parent);
        }

        while (existing.Count > 0)
        {
            var path = existing.Pop();
            if (Directory.Exists(path) || File.Exists(path))
                EnsureNoReparsePoint(path);
        }
    }

    private static void CreateManualBackup(
        string sourcePath,
        string targetRoot)
    {
        var backupPath = sourcePath + ".bak";
        EnsureContainedPath(targetRoot, backupPath, backupPath);
        EnsureExistingParentsAreSafe(targetRoot, backupPath);

        if (Directory.Exists(backupPath))
        {
            throw new IOException(
                $"Refusing to replace a backup path that is a directory: " +
                $"{backupPath}");
        }

        if (File.Exists(backupPath))
            EnsureNoReparsePoint(backupPath);

        File.Copy(sourcePath, backupPath, overwrite: true);
    }

    private static void EnsureNoReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return;

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                $"Refusing to update through a reparse point: {path}");
        }
    }

    private static string EnsureTrailingSeparator(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return fullPath.EndsWith(
                   Path.DirectorySeparatorChar.ToString(),
                   StringComparison.Ordinal)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(stream);
        return string.Concat(hash.Select(value => value.ToString("x2")));
    }

    private static void VerifyExpectedArchive(
        string zipPath,
        string? expectedSha256,
        long? expectedSize)
    {
        if (expectedSize.HasValue)
        {
            var actualSize = new FileInfo(zipPath).Length;
            if (actualSize != expectedSize.Value)
            {
                throw new InvalidDataException(
                    $"Update archive size changed after download. Expected " +
                    $"{expectedSize.Value}, found {actualSize} bytes.");
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            var actualSha256 = ComputeFileSha256(zipPath);
            if (!string.Equals(
                    actualSha256,
                    expectedSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Update archive SHA-256 changed after download.");
            }
        }
    }

    private static void SafeDeleteWorkingDirectory(
        string workingRoot,
        string expectedParent)
    {
        try
        {
            var fullWorkingRoot = Path.GetFullPath(workingRoot);
            var parentPrefix = EnsureTrailingSeparator(expectedParent);
            var expectedName = Path.GetFileName(fullWorkingRoot);
            if (!fullWorkingRoot.StartsWith(
                    parentPrefix,
                    StringComparison.OrdinalIgnoreCase) ||
                !expectedName.StartsWith(
                    ".revit-mcp-update-",
                    StringComparison.Ordinal))
            {
                Log(
                    $"WARNING: refused to clean unexpected working directory " +
                    $"{fullWorkingRoot}");
                return;
            }

            if (Directory.Exists(fullWorkingRoot))
                Directory.Delete(fullWorkingRoot, recursive: true);
        }
        catch (Exception cleanupError)
        {
            Log(
                $"WARNING: could not clean update staging directory: " +
                $"{cleanupError.Message}");
        }
    }

    private static bool WaitForProcessExit(string processName)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < WaitTimeout)
        {
            var processes = Process.GetProcessesByName(processName);
            if (processes.Length == 0)
                return true;

            foreach (var process in processes)
                process.Dispose();

            Log(
                $"  Waiting for {processName} to close " +
                $"({processes.Length} instance(s))…");
            Thread.Sleep(WaitPollInterval);
        }
        return false;
    }

    private sealed class Options
    {
        public string ZipPath { get; set; } = "";
        public string Product { get; set; } = "revit";
        public string RevitYear { get; set; } = "2025";
        public string? BundleName { get; set; }
        public string? ProcessNameOverride { get; set; }
        public string? AddinsDirOverride { get; set; }
        public string? ExpectedSha256 { get; set; }
        public long? ExpectedSize { get; set; }
        public bool WaitForExit { get; set; } = true;
    }

    private static Options? ParseArgs(string[] args)
    {
        var options = new Options();
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--zip" when index + 1 < args.Length:
                    options.ZipPath = args[++index];
                    break;
                case "--product" when index + 1 < args.Length:
                    options.Product = args[++index];
                    break;
                case "--revit-year" when index + 1 < args.Length:
                    options.RevitYear = args[++index];
                    break;
                case "--bundle-name" when index + 1 < args.Length:
                    options.BundleName = args[++index];
                    break;
                case "--process-name" when index + 1 < args.Length:
                    options.ProcessNameOverride = args[++index];
                    break;
                case "--addins-dir" when index + 1 < args.Length:
                    options.AddinsDirOverride = args[++index];
                    break;
                case "--sha256" when index + 1 < args.Length:
                    options.ExpectedSha256 = args[++index].Trim();
                    break;
                case "--size" when index + 1 < args.Length:
                    if (!long.TryParse(args[++index], out var expectedSize) ||
                        expectedSize <= 0)
                    {
                        Log("Invalid --size value.");
                        return null;
                    }
                    options.ExpectedSize = expectedSize;
                    break;
                case "--wait":
                    options.WaitForExit = true;
                    break;
                case "--no-wait":
                    options.WaitForExit = false;
                    break;
                default:
                    Log($"Unknown argument: {args[index]}");
                    PrintUsage();
                    return null;
            }
        }

        if (string.IsNullOrWhiteSpace(options.ZipPath))
        {
            PrintUsage();
            return null;
        }

        if (!string.IsNullOrWhiteSpace(options.ExpectedSha256) &&
            !IsSha256Hex(options.ExpectedSha256))
        {
            Log("--sha256 must be exactly 64 hexadecimal characters.");
            return null;
        }
        return options;
    }

    private static bool IsFourDigitYear(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Length == 4 &&
               value.All(character => character >= '0' && character <= '9');
    }

    private static bool IsSha256Hex(string value)
    {
        return value.Length == 64 &&
               value.All(character =>
                   (character >= '0' && character <= '9') ||
                   (character >= 'a' && character <= 'f') ||
                   (character >= 'A' && character <= 'F'));
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            @"Usage:
  RevitMCPUpdater.exe --zip <path> --product revit   --revit-year 2025 [--wait]
  RevitMCPUpdater.exe --zip <path> --product autocad --bundle-name <name> [--wait]
  RevitMCPUpdater.exe --zip <path> --revit-year 2025 [--wait]

Options:
  --product <revit|autocad>   Default: revit
  --revit-year <YYYY>         Required when product=revit
  --bundle-name <name>        AutoCAD bundle folder name
  --process-name <name>       Override exe name to wait for
  --addins-dir <path>         Absolute target dir
  --sha256 <hex>              Re-verify archive immediately before install
  --size <bytes>              Re-verify archive size before install
  --wait | --no-wait          Wait for the CAD process to exit before updating");
    }

    private static void Log(string line)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}");
    }
}
