using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.Plugin.Services
{
    /// <summary>
    /// Owns the active CommandSet generation. Revit 2025+ loads generations
    /// into collectible AssemblyLoadContexts; older targets retain the
    /// historical load-once behavior.
    /// </summary>
    internal sealed class CommandSetRuntime : IDisposable
    {
        private const int MaxPendingRetiredContexts = 8;
        private static readonly HashSet<string> ReservedCommandNames =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "reload_commandset",
                "get_commandset_status"
            };

        private readonly object _sync = new object();
        private readonly string _baselineAssemblyPath;
        private bool _disposed;

#if NET8_0_OR_GREATER
        private readonly CommandSetGenerationStore _store;
        private readonly List<RetiredCommandSetContext> _retired =
            new List<RetiredCommandSetContext>();
        private LoadedCommandSet _active;
        private int _retiredCollectedCount;
        private string _startupWarning;
#else
        private readonly Dictionary<string, IRevitCommand> _commands;
        private readonly string _assemblyVersion;
        private readonly string _assemblySha256;
#endif

        public CommandSetRuntime(string revitVersion)
        {
            var hostDirectory = Path.GetDirectoryName(
                typeof(CommandSetRuntime).Assembly.Location);
            if (string.IsNullOrWhiteSpace(hostDirectory))
                throw new InvalidOperationException(
                    "Could not determine the Revit MCP host directory.");

            _baselineAssemblyPath = Path.Combine(
                hostDirectory,
                "RevitMCP.CommandSet.dll");
            if (!File.Exists(_baselineAssemblyPath))
                throw new FileNotFoundException(
                    "Baseline RevitMCP.CommandSet.dll was not found.",
                    _baselineAssemblyPath);

#if NET8_0_OR_GREATER
            _store = new CommandSetGenerationStore(revitVersion);
            var persisted = _store.TryResolvePersistedCandidate(
                out _startupWarning);
            if (persisted != null)
            {
                try
                {
                    _active = LoadCandidate(persisted);
                }
                catch (Exception ex)
                {
                    _startupWarning =
                        $"Persisted generation '{persisted.Generation}' failed " +
                        $"to load; baseline retained: {ex.Message}";
                }
            }

            _active ??= LoadBaseline();
#else
            var assembly = Assembly.LoadFrom(_baselineAssemblyPath);
            _commands = DiscoverCommands(assembly);
            _assemblyVersion = assembly.GetName().Version?.ToString() ?? "";
            _assemblySha256 = ComputeSha256(_baselineAssemblyPath);
#endif
        }

        public bool HasCommand(string name)
        {
            lock (_sync)
            {
                ThrowIfDisposed();
#if NET8_0_OR_GREATER
                return _active.Commands.ContainsKey(name);
#else
                return _commands.ContainsKey(name);
#endif
            }
        }

        public IRevitCommand GetCommand(string name)
        {
            lock (_sync)
            {
                ThrowIfDisposed();
#if NET8_0_OR_GREATER
                if (_active.Commands.TryGetValue(name, out var command))
                    return command;
#else
                if (_commands.TryGetValue(name, out var command))
                    return command;
#endif
                return null;
            }
        }

        public string[] GetCommandNames()
        {
            lock (_sync)
            {
                ThrowIfDisposed();
#if NET8_0_OR_GREATER
                return _active.Commands.Keys.OrderBy(name => name).ToArray();
#else
                return _commands.Keys.OrderBy(name => name).ToArray();
#endif
            }
        }

        public Dictionary<string, object> GetStatus()
        {
            lock (_sync)
            {
                ThrowIfDisposed();
#if NET8_0_OR_GREATER
                PruneRetiredContexts();
                var available = _store.ListCandidates(out var ignored)
                    .Take(20)
                    .Select(candidate => (object)new Dictionary<string, object>
                    {
                        ["generation"] = candidate.Generation,
                        ["created_at_utc"] = candidate.CreatedAtUtc.ToString("O"),
                        ["commandset_sha256"] = candidate.CommandSetSha256,
                        ["source_commit"] = candidate.SourceCommit
                    })
                    .ToList();
                var retiredAlive = _retired.Count(item => item.Context.IsAlive);
                return new Dictionary<string, object>
                {
                    ["hot_reload_supported"] = true,
                    ["active_generation"] = _active.Generation,
                    ["active_source"] = _active.IsStaged ? "staged" : "baseline",
                    ["loaded_at_utc"] = _active.LoadedAtUtc.ToString("O"),
                    ["command_count"] = _active.Commands.Count,
                    ["assembly_version"] = _active.AssemblyVersion,
                    ["commandset_sha256"] = _active.CommandSetSha256,
                    ["source_commit"] = _active.SourceCommit,
                    ["contracts_sha256"] = _store.ContractSha256,
                    ["staging_root"] = _store.StagedRoot,
                    ["persisted_generation"] =
                        _store.GetPersistedGeneration() ?? "",
                    ["startup_warning"] = _startupWarning ?? "",
                    ["retired_contexts_pending"] = retiredAlive,
                    ["retired_contexts_collected"] = _retiredCollectedCount,
                    ["available_generations"] = available,
                    ["ignored_generation_count"] = ignored.Count,
                    ["ignored_generations"] = ignored.Take(20).ToList(),
                    ["restart_required_for"] =
                        "Host, contracts, WebSocket, Revit.Async, and startup lifecycle changes."
                };
#else
                return new Dictionary<string, object>
                {
                    ["hot_reload_supported"] = false,
                    ["active_generation"] = "baseline",
                    ["active_source"] = "baseline",
                    ["command_count"] = _commands.Count,
                    ["assembly_version"] = _assemblyVersion,
                    ["commandset_sha256"] = _assemblySha256,
                    ["restart_required_for"] =
                        "All C# changes on Revit 2023/2024 (.NET Framework)."
                };
#endif
            }
        }

        public Dictionary<string, object> Reload(
            string generation,
            bool allowCommandRemoval,
            bool persist)
        {
            lock (_sync)
            {
                ThrowIfDisposed();
#if NET8_0_OR_GREATER
                PruneRetiredContexts();
                var pending = _retired.Count(item => item.Context.IsAlive);
                if (pending >= MaxPendingRetiredContexts)
                {
                    // Collectible ALC unload is cooperative. Avoid forcing a
                    // process-wide collection on every reload, but do one
                    // last two-pass collection before declaring a leak and
                    // asking the user to restart this Revit process.
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    PruneRetiredContexts();
                    pending = _retired.Count(item => item.Context.IsAlive);
                }
                if (pending >= MaxPendingRetiredContexts)
                {
                    throw new InvalidOperationException(
                        $"{pending} retired CommandSet contexts are still alive. " +
                        "Restart this Revit process before loading more generations; " +
                        "a command likely retained a static event, thread, or cached type.");
                }

                var candidate = _store.ResolveCandidate(generation);
                if (string.Equals(
                        candidate.CommandSetSha256,
                        _active.CommandSetSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    string persistWarning = null;
                    if (persist)
                    {
                        try { _store.PersistActive(candidate); }
                        catch (Exception ex) { persistWarning = ex.Message; }
                    }
                    return BuildReloadResult(
                        changed: false,
                        previous: _active,
                        current: _active,
                        added: Array.Empty<string>(),
                        removed: Array.Empty<string>(),
                        persist,
                        persistWarning,
                        pending);
                }

                var next = LoadCandidate(candidate);
                var currentNames = new HashSet<string>(
                    _active.Commands.Keys,
                    StringComparer.Ordinal);
                var nextNames = new HashSet<string>(
                    next.Commands.Keys,
                    StringComparer.Ordinal);
                var removed = currentNames.Except(nextNames)
                    .OrderBy(name => name)
                    .ToArray();
                var added = nextNames.Except(currentNames)
                    .OrderBy(name => name)
                    .ToArray();
                if (removed.Length > 0 && !allowCommandRemoval)
                {
                    next.Unload();
                    throw new InvalidOperationException(
                        "Candidate removes active commands: " +
                        string.Join(", ", removed) +
                        ". Retry only with allow_command_removal=true if this " +
                        "breaking change is intentional.");
                }

                var previous = _active;
                _active = next;
                var retired = previous.Unload();
                _retired.Add(new RetiredCommandSetContext
                {
                    Generation = previous.Generation,
                    RetiredAtUtc = DateTimeOffset.UtcNow,
                    Context = retired
                });

                string warning = null;
                if (persist)
                {
                    try { _store.PersistActive(candidate); }
                    catch (Exception ex)
                    {
                        warning =
                            "Generation is active for this Revit session but " +
                            $"could not be persisted: {ex.Message}";
                    }
                }

                PruneRetiredContexts();
                return BuildReloadResult(
                    changed: true,
                    previous,
                    _active,
                    added,
                    removed,
                    persist,
                    warning,
                    _retired.Count(item => item.Context.IsAlive));
#else
                throw new InvalidOperationException(
                    "CommandSet hot reload requires Revit 2025+ (.NET 8). " +
                    "Revit 2023/2024 must restart after C# changes.");
#endif
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
#if NET8_0_OR_GREATER
                _active?.Unload();
                _active = null;
                _retired.Clear();
#endif
            }
        }

#if NET8_0_OR_GREATER
        private LoadedCommandSet LoadBaseline()
        {
            var hash = CommandSetGenerationStore.ComputeSha256(
                _baselineAssemblyPath);
            return LoadedCommandSet.Load(
                _baselineAssemblyPath,
                $"baseline-{hash.Substring(0, 12)}",
                hash,
                sourceCommit: "",
                isStaged: false);
        }

        private static LoadedCommandSet LoadCandidate(
            CommandSetCandidate candidate)
        {
            return LoadedCommandSet.Load(
                candidate.AssemblyPath,
                candidate.Generation,
                candidate.CommandSetSha256,
                candidate.SourceCommit,
                isStaged: true);
        }

        private void PruneRetiredContexts()
        {
            for (var index = _retired.Count - 1; index >= 0; index--)
            {
                if (_retired[index].Context.IsAlive) continue;
                _retired.RemoveAt(index);
                _retiredCollectedCount++;
            }
        }

        private static Dictionary<string, object> BuildReloadResult(
            bool changed,
            LoadedCommandSet previous,
            LoadedCommandSet current,
            IReadOnlyCollection<string> added,
            IReadOnlyCollection<string> removed,
            bool persistRequested,
            string persistWarning,
            int pendingRetiredContexts)
        {
            return new Dictionary<string, object>
            {
                ["reloaded"] = changed,
                ["previous_generation"] = previous.Generation,
                ["active_generation"] = current.Generation,
                ["active_source"] = current.IsStaged ? "staged" : "baseline",
                ["command_count"] = current.Commands.Count,
                ["added_commands"] = added.ToList(),
                ["removed_commands"] = removed.ToList(),
                ["commandset_sha256"] = current.CommandSetSha256,
                ["assembly_version"] = current.AssemblyVersion,
                ["persist_requested"] = persistRequested,
                ["persisted"] = persistRequested &&
                                string.IsNullOrWhiteSpace(persistWarning),
                ["warning"] = persistWarning ?? "",
                ["retired_contexts_pending"] = pendingRetiredContexts,
                ["verification"] = new Dictionary<string, object>
                {
                    ["performed"] = true,
                    ["candidate_loaded_before_swap"] = true,
                    ["command_inventory_valid"] = true,
                    ["active_hash_matches_candidate"] = true
                }
            };
        }

        private sealed class LoadedCommandSet
        {
            private CommandSetLoadContext _context;

            private LoadedCommandSet()
            {
            }

            public string Generation { get; private set; }
            public string CommandSetSha256 { get; private set; }
            public string SourceCommit { get; private set; }
            public string AssemblyVersion { get; private set; }
            public bool IsStaged { get; private set; }
            public DateTimeOffset LoadedAtUtc { get; private set; }
            public Dictionary<string, IRevitCommand> Commands { get; private set; }

            public static LoadedCommandSet Load(
                string assemblyPath,
                string generation,
                string hash,
                string sourceCommit,
                bool isStaged)
            {
                var context = new CommandSetLoadContext(
                    assemblyPath,
                    generation);
                try
                {
                    var assembly = context.LoadMainAssembly(assemblyPath);
                    return new LoadedCommandSet
                    {
                        _context = context,
                        Generation = generation,
                        CommandSetSha256 = hash,
                        SourceCommit = sourceCommit ?? "",
                        AssemblyVersion =
                            assembly.GetName().Version?.ToString() ?? "",
                        IsStaged = isStaged,
                        LoadedAtUtc = DateTimeOffset.UtcNow,
                        Commands = DiscoverCommands(assembly)
                    };
                }
                catch
                {
                    context.Unload();
                    throw;
                }
            }

            public WeakReference Unload()
            {
                var context = _context;
                if (context == null)
                    return new WeakReference(null);

                Commands?.Clear();
                Commands = new Dictionary<string, IRevitCommand>(
                    StringComparer.Ordinal);
                _context = null;
                var weakReference = new WeakReference(context);
                context.Unload();
                return weakReference;
            }
        }

        private sealed class RetiredCommandSetContext
        {
            public string Generation { get; set; }
            public DateTimeOffset RetiredAtUtc { get; set; }
            public WeakReference Context { get; set; }
        }
#endif

        private static Dictionary<string, IRevitCommand> DiscoverCommands(
            Assembly assembly)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var loaderErrors = ex.LoaderExceptions
                    .Where(error => error != null)
                    .Select(error => error.Message)
                    .Take(10);
                throw new InvalidOperationException(
                    "CommandSet type discovery failed: " +
                    string.Join(" | ", loaderErrors),
                    ex);
            }

            var commandTypes = types
                .Where(type =>
                    typeof(IRevitCommand).IsAssignableFrom(type) &&
                    !type.IsAbstract &&
                    !type.IsInterface)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToList();
            var commands = new Dictionary<string, IRevitCommand>(
                StringComparer.Ordinal);

            foreach (var type in commandTypes)
            {
                IRevitCommand command;
                try
                {
                    command = (IRevitCommand)Activator.CreateInstance(type);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Could not create CommandSet type '{type.FullName}': " +
                        ex.GetBaseException().Message,
                        ex);
                }

                if (string.IsNullOrWhiteSpace(command.Name))
                    throw new InvalidOperationException(
                        $"Command type '{type.FullName}' has an empty Name.");
                if (ReservedCommandNames.Contains(command.Name))
                    throw new InvalidOperationException(
                        $"CommandSet cannot register reserved host command " +
                        $"'{command.Name}'.");
                if (commands.ContainsKey(command.Name))
                    throw new InvalidOperationException(
                        $"Duplicate CommandSet command name '{command.Name}'.");

                commands.Add(command.Name, command);
            }

            if (commands.Count == 0)
                throw new InvalidOperationException(
                    "CommandSet did not contain any IRevitCommand implementations.");

            return commands;
        }

#if !NET8_0_OR_GREATER
        private static string ComputeSha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                return BitConverter.ToString(sha256.ComputeHash(stream))
                    .Replace("-", "")
                    .ToLowerInvariant();
            }
        }
#endif

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(CommandSetRuntime));
        }
    }
}
