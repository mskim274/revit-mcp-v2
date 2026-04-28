// CAD MCP Updater — external DLL/bundle replacer.
//
// Purpose: each MCP plugin's UpdateChecker downloads a new .zip, but the
// running CAD process holds the plugin DLLs in a file lock. This console
// app is launched from the update dialog (or invoked manually), waits for
// the relevant CAD process to terminate, and then unpacks the new files
// into the user's addins/bundle folder.
//
// Supported products:
//   - revit   → process "Revit",  target %APPDATA%\Autodesk\Revit\Addins\<YEAR>\
//   - autocad → process "acad",   target %APPDATA%\Autodesk\ApplicationPlugins\<BUNDLE>.bundle\
//
// Usage:
//   RevitMCPUpdater.exe --zip <path> --product revit   --revit-year 2025 [--wait]
//   RevitMCPUpdater.exe --zip <path> --product autocad --bundle-name AutoCADMCP [--wait]
//
// Backwards-compat shortcut (Revit plugin v0.3 still calls this):
//   RevitMCPUpdater.exe --zip <path> --revit-year 2025 --wait
//   ↳ implies --product revit
//
// Test/escape-hatch overrides (also useful for the Revit MCP test harness):
//   --process-name <name>   — exe to wait for (e.g. "Revit", "acad")
//   --addins-dir   <path>   — absolute target directory (skips product routing)
//
// Harness Engineering notes:
//   - Idempotent: re-running extracts the same files; last one wins.
//   - Safe failure: backup files written with ".bak" suffix so a corrupt
//     zip doesn't leave the user without a working plugin.
//   - Short-lived: exits as soon as the copy finishes; no daemon.

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace RevitMCP.Updater;

internal static class Program
{
    private static readonly TimeSpan WaitPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromMinutes(5);

    public static int Main(string[] args)
    {
        try
        {
            var opts = ParseArgs(args);
            if (opts == null) return 2;

            var resolved = ResolveProfile(opts);
            if (resolved == null) return 2;

            Log($"CAD MCP Updater starting.");
            Log($"  Zip:           {opts.ZipPath}");
            Log($"  Product:       {opts.Product}");
            Log($"  Process name:  {resolved.ProcessName}");
            Log($"  Target:        {resolved.AddinsDir}");
            Log($"  Wait first:    {opts.WaitForExit}");

            if (!File.Exists(opts.ZipPath))
            {
                Log($"ERROR: zip not found at {opts.ZipPath}");
                return 3;
            }

            Directory.CreateDirectory(resolved.AddinsDir);

            if (opts.WaitForExit && !WaitForProcessExit(resolved.ProcessName))
            {
                Log($"ERROR: {resolved.ProcessName} did not exit within timeout. Aborting update.");
                return 4;
            }

            ApplyUpdate(opts.ZipPath, resolved.AddinsDir);

            Log($"Update complete. You can start {resolved.FriendlyName} now.");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    // ─── Profile resolution ──────────────────────────────────────────────

    /// <summary>
    /// Resolved target after merging product profile + per-arg overrides.
    /// </summary>
    private sealed record ResolvedProfile(string ProcessName, string AddinsDir, string FriendlyName);

    /// <summary>
    /// Map (product, year/bundle) into concrete process name + addins path.
    /// Exposed as a pure function so a future test harness can drive it
    /// without launching the full updater.
    /// </summary>
    public static (string processName, string addinsDir, string friendlyName)? ResolveTarget(
        string product, string? revitYear, string? bundleName, string appDataRoot)
    {
        switch (product.ToLowerInvariant())
        {
            case "revit":
                if (string.IsNullOrWhiteSpace(revitYear)) return null;
                var revitDir = Path.Combine(appDataRoot, "Autodesk", "Revit", "Addins", revitYear!);
                return ("Revit", revitDir, $"Revit {revitYear}");

            case "autocad":
                var bundle = string.IsNullOrWhiteSpace(bundleName) ? "AutoCADMCP" : bundleName!;
                if (!bundle.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
                    bundle += ".bundle";
                var acadDir = Path.Combine(appDataRoot, "Autodesk", "ApplicationPlugins", bundle);
                return ("acad", acadDir, "AutoCAD");

            default:
                return null;
        }
    }

    private static ResolvedProfile? ResolveProfile(Options opts)
    {
        // --addins-dir / --process-name override take precedence (test/escape-hatch).
        if (!string.IsNullOrWhiteSpace(opts.AddinsDirOverride))
        {
            var procName = opts.ProcessNameOverride ?? "Revit";
            return new ResolvedProfile(procName, opts.AddinsDirOverride!, "the CAD application");
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var resolved = ResolveTarget(opts.Product, opts.RevitYear, opts.BundleName, appData);
        if (resolved == null)
        {
            Log($"ERROR: cannot resolve target for product '{opts.Product}'. Check --revit-year or --bundle-name.");
            return null;
        }

        var (processName, addinsDir, friendly) = resolved.Value;
        if (!string.IsNullOrWhiteSpace(opts.ProcessNameOverride))
            processName = opts.ProcessNameOverride!;

        return new ResolvedProfile(processName, addinsDir, friendly);
    }

    // ─── Core logic ──────────────────────────────────────────────────────

    /// <summary>
    /// Extract each entry from the zip into the target directory, keeping
    /// a .bak copy of any existing file so a corrupt update can be rolled
    /// back manually.
    /// </summary>
    private static void ApplyUpdate(string zipPath, string targetDir)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            // Skip directories (zip entries ending in '/').
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var destPath = Path.Combine(targetDir, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            // Backup existing file before overwrite.
            if (File.Exists(destPath))
            {
                var bakPath = destPath + ".bak";
                File.Copy(destPath, bakPath, overwrite: true);
                Log($"  Backed up:   {entry.Name} → {Path.GetFileName(bakPath)}");
            }

            entry.ExtractToFile(destPath, overwrite: true);
            Log($"  Extracted:   {entry.Name}");
        }
    }

    /// <summary>
    /// Poll every few seconds until no instance of the named process is
    /// running, or the timeout elapses.
    /// </summary>
    private static bool WaitForProcessExit(string processName)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < WaitTimeout)
        {
            var procs = Process.GetProcessesByName(processName);
            if (procs.Length == 0) return true;

            Log($"  Waiting for {processName} to close ({procs.Length} instance(s))…");
            Thread.Sleep(WaitPollInterval);
        }
        return false;
    }

    // ─── Arg parsing ─────────────────────────────────────────────────────

    private sealed class Options
    {
        public string ZipPath { get; set; } = "";
        public string Product { get; set; } = "revit"; // default keeps v0.3 plugin working
        public string RevitYear { get; set; } = "2025";
        public string? BundleName { get; set; }
        public string? ProcessNameOverride { get; set; }
        public string? AddinsDirOverride { get; set; }
        public bool WaitForExit { get; set; } = true;
    }

    private static Options? ParseArgs(string[] args)
    {
        var opts = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--zip" when i + 1 < args.Length:
                    opts.ZipPath = args[++i];
                    break;
                case "--product" when i + 1 < args.Length:
                    opts.Product = args[++i];
                    break;
                case "--revit-year" when i + 1 < args.Length:
                    opts.RevitYear = args[++i];
                    break;
                case "--bundle-name" when i + 1 < args.Length:
                    opts.BundleName = args[++i];
                    break;
                case "--process-name" when i + 1 < args.Length:
                    opts.ProcessNameOverride = args[++i];
                    break;
                case "--addins-dir" when i + 1 < args.Length:
                    opts.AddinsDirOverride = args[++i];
                    break;
                case "--wait":
                    opts.WaitForExit = true;
                    break;
                case "--no-wait":
                    opts.WaitForExit = false;
                    break;
                default:
                    Log($"Unknown argument: {args[i]}");
                    PrintUsage();
                    return null;
            }
        }

        if (string.IsNullOrWhiteSpace(opts.ZipPath))
        {
            PrintUsage();
            return null;
        }
        return opts;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(@"Usage:
  RevitMCPUpdater.exe --zip <path> --product revit   --revit-year 2025 [--wait]
  RevitMCPUpdater.exe --zip <path> --product autocad --bundle-name <name> [--wait]
  RevitMCPUpdater.exe --zip <path> --revit-year 2025 [--wait]      (legacy shortcut → product=revit)

Options:
  --product <revit|autocad>   Default: revit
  --revit-year <YYYY>         Required when product=revit (e.g. 2025)
  --bundle-name <name>        AutoCAD bundle folder name (default: AutoCADMCP)
  --process-name <name>       Override exe name to wait for
  --addins-dir <path>         Absolute target dir (overrides product routing)
  --wait | --no-wait          Wait for the CAD process to exit before extracting");
    }

    private static void Log(string line)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}");
    }
}
