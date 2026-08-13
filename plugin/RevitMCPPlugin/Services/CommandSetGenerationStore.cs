#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.Plugin.Services
{
    internal sealed class CommandSetGenerationStore
    {
        private const int MaxManifestBytes = 64 * 1024;
        private const string ManifestFileName = "commandset-manifest.json";
        private readonly string _revitYear;
        private readonly string _contractSha256;
        private readonly string _activePointerPath;

        public CommandSetGenerationStore(string revitYear)
        {
            _revitYear = NormalizeRevitYear(revitYear);
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                throw new InvalidOperationException(
                    "LOCALAPPDATA is unavailable; CommandSet staging cannot be initialized.");

            Root = Path.GetFullPath(Path.Combine(
                localAppData,
                "RevitMCP",
                "CommandSets"));
            StagedRoot = Path.Combine(Root, "staged");
            _activePointerPath = Path.Combine(
                Root,
                $"active-{_revitYear}.json");
            _contractSha256 = ComputeSha256(
                typeof(IRevitCommand).Assembly.Location);
        }

        public string Root { get; }
        public string StagedRoot { get; }
        public string ContractSha256 => _contractSha256;

        public IReadOnlyList<CommandSetCandidate> ListCandidates(
            out List<string> ignored)
        {
            ignored = new List<string>();
            if (!Directory.Exists(StagedRoot))
                return Array.Empty<CommandSetCandidate>();

            var stagedRootInfo = new DirectoryInfo(StagedRoot);
            if (IsReparsePoint(stagedRootInfo.Attributes))
                throw new InvalidOperationException(
                    $"CommandSet staging root must not be a reparse point: {StagedRoot}");

            var candidates = new List<CommandSetCandidate>();
            foreach (var directory in stagedRootInfo.EnumerateDirectories())
            {
                try
                {
                    candidates.Add(ReadCandidate(directory));
                }
                catch (Exception ex)
                {
                    ignored.Add($"{directory.Name}: {ex.Message}");
                }
            }

            return candidates
                .OrderByDescending(candidate => candidate.CreatedAtUtc)
                .ThenByDescending(candidate => candidate.Generation,
                    StringComparer.Ordinal)
                .ToList();
        }

        public CommandSetCandidate ResolveCandidate(string generation)
        {
            var candidates = ListCandidates(out var ignored);
            if (string.IsNullOrWhiteSpace(generation))
            {
                var latest = candidates.FirstOrDefault();
                if (latest != null)
                    return latest;

                var ignoredSummary = ignored.Count == 0
                    ? "No staged generations were found."
                    : $"All staged generations were invalid: {string.Join(" | ", ignored.Take(5))}";
                throw new InvalidOperationException(ignoredSummary);
            }

            var normalized = ValidateGenerationName(generation);
            var candidate = candidates.FirstOrDefault(item =>
                string.Equals(
                    item.Generation,
                    normalized,
                    StringComparison.Ordinal));
            if (candidate != null)
                return candidate;

            var matchingIgnored = ignored.FirstOrDefault(item =>
                item.StartsWith(normalized + ":", StringComparison.Ordinal));
            throw new InvalidOperationException(
                matchingIgnored ??
                $"Staged CommandSet generation '{normalized}' was not found.");
        }

        public CommandSetCandidate TryResolvePersistedCandidate(
            out string warning)
        {
            warning = null;
            if (!File.Exists(_activePointerPath))
                return null;

            try
            {
                var pointerInfo = new FileInfo(_activePointerPath);
                ValidateRegularFile(pointerInfo, "active generation pointer");
                if (pointerInfo.Length <= 0 || pointerInfo.Length > MaxManifestBytes)
                    throw new InvalidOperationException(
                        "Active generation pointer has an invalid size.");

                var pointer = JsonSerializer.Deserialize<ActiveGenerationPointer>(
                    File.ReadAllText(pointerInfo.FullName));
                if (pointer == null || pointer.SchemaVersion != 1)
                    throw new InvalidOperationException(
                        "Active generation pointer has an unsupported schema.");
                if (!string.Equals(pointer.RevitYear, _revitYear,
                        StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Active generation pointer targets another Revit year.");
                if (!FixedHashEquals(pointer.ContractsSha256, _contractSha256))
                    throw new InvalidOperationException(
                        "Active generation pointer targets a different host contract.");

                return ResolveCandidate(pointer.Generation);
            }
            catch (Exception ex)
            {
                warning =
                    $"Persisted CommandSet generation was ignored: {ex.Message}";
                return null;
            }
        }

        public string GetPersistedGeneration()
        {
            var candidate = TryResolvePersistedCandidate(out _);
            return candidate?.Generation;
        }

        public void PersistActive(CommandSetCandidate candidate)
        {
            if (candidate == null)
                throw new ArgumentNullException(nameof(candidate));

            Directory.CreateDirectory(Root);
            var pointer = new ActiveGenerationPointer
            {
                SchemaVersion = 1,
                RevitYear = _revitYear,
                Generation = candidate.Generation,
                ContractsSha256 = _contractSha256,
                UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O")
            };
            var temporaryPath = Path.Combine(
                Root,
                $".{Path.GetFileName(_activePointerPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(
                    temporaryPath,
                    JsonSerializer.Serialize(
                        pointer,
                        new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporaryPath, _activePointerPath, true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch
                {
                    // Best-effort cleanup of a uniquely named temporary file.
                }
            }
        }

        private CommandSetCandidate ReadCandidate(DirectoryInfo directory)
        {
            if (IsReparsePoint(directory.Attributes))
                throw new InvalidOperationException(
                    "Generation directory must not be a reparse point.");

            var generation = ValidateGenerationName(directory.Name);
            var fullDirectory = EnsureUnderStagedRoot(directory.FullName);
            var manifestPath = Path.Combine(fullDirectory, ManifestFileName);
            var manifestInfo = new FileInfo(manifestPath);
            ValidateRegularFile(manifestInfo, "CommandSet manifest");
            if (manifestInfo.Length <= 0 || manifestInfo.Length > MaxManifestBytes)
                throw new InvalidOperationException(
                    "CommandSet manifest has an invalid size.");

            var manifest = JsonSerializer.Deserialize<CommandSetManifest>(
                File.ReadAllText(manifestInfo.FullName));
            if (manifest == null || manifest.SchemaVersion != 1)
                throw new InvalidOperationException(
                    "CommandSet manifest has an unsupported schema.");
            if (!string.Equals(manifest.Generation, generation,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Manifest generation does not match its directory name.");
            if (!string.Equals(manifest.TargetFramework, "net8.0-windows",
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Only net8.0-windows CommandSet generations are supported.");
            if (!string.Equals(manifest.RevitYear, _revitYear,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Generation targets Revit {manifest.RevitYear}, not {_revitYear}.");
            if (!FixedHashEquals(manifest.ContractsSha256, _contractSha256))
                throw new InvalidOperationException(
                    "Generation was built against a different RevitMCP.Contracts.dll.");
            if (!string.Equals(
                    manifest.CommandSetAssembly,
                    "RevitMCP.CommandSet.dll",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Manifest commandset_assembly must be RevitMCP.CommandSet.dll.");
            if (!DateTimeOffset.TryParse(
                    manifest.CreatedAtUtc,
                    out var createdAtUtc) ||
                createdAtUtc.Offset != TimeSpan.Zero)
                throw new InvalidOperationException(
                    "Manifest created_at_utc must be an ISO-8601 UTC timestamp.");

            var assemblyPath = Path.Combine(
                fullDirectory,
                manifest.CommandSetAssembly);
            var assemblyInfo = new FileInfo(assemblyPath);
            ValidateRegularFile(assemblyInfo, "CommandSet assembly");
            var depsPath = Path.ChangeExtension(
                assemblyPath,
                ".deps.json");
            ValidateRegularFile(
                new FileInfo(depsPath),
                "CommandSet dependency manifest");

            var actualHash = ComputeSha256(assemblyPath);
            if (!FixedHashEquals(manifest.CommandSetSha256, actualHash))
                throw new InvalidOperationException(
                    "CommandSet assembly hash does not match its manifest.");

            return new CommandSetCandidate
            {
                Generation = generation,
                DirectoryPath = fullDirectory,
                AssemblyPath = assemblyPath,
                CommandSetSha256 = actualHash,
                ContractsSha256 = _contractSha256,
                CreatedAtUtc = createdAtUtc,
                SourceCommit = manifest.SourceCommit ?? ""
            };
        }

        private string EnsureUnderStagedRoot(string path)
        {
            var root = Path.GetFullPath(StagedRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Generation path escapes the CommandSet staging root.");
            return fullPath;
        }

        private static void ValidateRegularFile(
            FileInfo file,
            string description)
        {
            if (!file.Exists)
                throw new InvalidOperationException(
                    $"Missing {description}: {file.Name}");
            if (IsReparsePoint(file.Attributes))
                throw new InvalidOperationException(
                    $"{description} must not be a reparse point: {file.Name}");
        }

        private static bool IsReparsePoint(FileAttributes attributes)
        {
            return (attributes & FileAttributes.ReparsePoint) != 0;
        }

        private static string ValidateGenerationName(string generation)
        {
            var normalized = generation?.Trim();
            if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 128)
                throw new InvalidOperationException(
                    "Generation must contain 1-128 characters.");
            if (normalized.Any(character =>
                    !(char.IsLetterOrDigit(character) ||
                      character == '.' ||
                      character == '_' ||
                      character == '-')))
                throw new InvalidOperationException(
                    "Generation may contain only letters, digits, '.', '_' and '-'.");
            return normalized;
        }

        private static string NormalizeRevitYear(string value)
        {
            var trimmed = value?.Trim();
            if (trimmed == null || trimmed.Length < 4 ||
                !int.TryParse(trimmed.Substring(0, 4), out var year) ||
                year < 2025 || year > 9999)
                throw new InvalidOperationException(
                    $"Unsupported Revit version '{value}' for CommandSet hot reload.");
            return year.ToString();
        }

        internal static string ComputeSha256(string path)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var sha256 = SHA256.Create();
            return BitConverter.ToString(sha256.ComputeHash(stream))
                .Replace("-", "")
                .ToLowerInvariant();
        }

        private static bool FixedHashEquals(string left, string right)
        {
            if (left == null || right == null ||
                left.Length != 64 || right.Length != 64)
                return false;

            var difference = 0;
            for (var index = 0; index < left.Length; index++)
                difference |= char.ToLowerInvariant(left[index]) ^
                              char.ToLowerInvariant(right[index]);
            return difference == 0;
        }

        private sealed class CommandSetManifest
        {
            [JsonPropertyName("schema_version")]
            public int SchemaVersion { get; set; }

            [JsonPropertyName("generation")]
            public string Generation { get; set; }

            [JsonPropertyName("created_at_utc")]
            public string CreatedAtUtc { get; set; }

            [JsonPropertyName("target_framework")]
            public string TargetFramework { get; set; }

            [JsonPropertyName("revit_year")]
            public string RevitYear { get; set; }

            [JsonPropertyName("commandset_assembly")]
            public string CommandSetAssembly { get; set; }

            [JsonPropertyName("commandset_sha256")]
            public string CommandSetSha256 { get; set; }

            [JsonPropertyName("contracts_sha256")]
            public string ContractsSha256 { get; set; }

            [JsonPropertyName("source_commit")]
            public string SourceCommit { get; set; }
        }

        private sealed class ActiveGenerationPointer
        {
            [JsonPropertyName("schema_version")]
            public int SchemaVersion { get; set; }

            [JsonPropertyName("revit_year")]
            public string RevitYear { get; set; }

            [JsonPropertyName("generation")]
            public string Generation { get; set; }

            [JsonPropertyName("contracts_sha256")]
            public string ContractsSha256 { get; set; }

            [JsonPropertyName("updated_at_utc")]
            public string UpdatedAtUtc { get; set; }
        }
    }

    internal sealed class CommandSetCandidate
    {
        public string Generation { get; set; }
        public string DirectoryPath { get; set; }
        public string AssemblyPath { get; set; }
        public string CommandSetSha256 { get; set; }
        public string ContractsSha256 { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public string SourceCommit { get; set; }
    }
}
#endif
