// Revit MCP Updater — external DLL replacer.
//
// Purpose: the plugin's UpdateChecker downloads a new .zip, but the old
// plugin DLLs are locked by the running Revit process. This console app
// is launched from the update dialog (or after Revit exits), waits for
// any Revit instance to terminate, then unpacks the new files into the
// user's Revit Addins folder.
//
// Usage:
//   RevitMCPUpdater.exe --zip <path-to-zip> --revit-year 2025 [--wait]
//
// Args:
//   --zip         Path to the downloaded RevitMCPPlugin-<version>-Revit<year>.zip
//   --revit-year  Revit major version year (2023 or 2025)
//   --wait        Poll for any revit.exe process to exit before extracting
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
using System.Linq;
using System.Threading;

namespace RevitMCP.Updater;

internal static class Program
{
    private const string RevitProcessName = "Revit";
    private static readonly TimeSpan WaitPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromMinutes(5);

    public static int Main(string[] args)
    {
        try
        {
            var opts = ParseArgs(args);
            if (opts == null) return 2;

            Log($"Revit MCP Updater starting.");
            Log($"  Zip:         {opts.ZipPath}");
            Log($"  Revit year:  {opts.RevitYear}");
            Log($"  Wait first:  {opts.WaitForRevit}");

            if (!File.Exists(opts.ZipPath))
            {
                Log($"ERROR: zip not found at {opts.ZipPath}");
                return 3;
            }

            var addinsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Autodesk", "Revit", "Addins", opts.RevitYear);
            Directory.CreateDirectory(addinsDir);
            Log($"  Target:      {addinsDir}");

            if (opts.WaitForRevit && !WaitForRevitExit())
            {
                Log("ERROR: Revit did not exit within timeout. Aborting update.");
                return 4;
            }

            ApplyUpdate(opts.ZipPath, addinsDir);

            Log("Update complete. You can start Revit now.");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
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
    /// Poll every few seconds until no revit.exe process is running, or
    /// the timeout elapses.
    /// </summary>
    private static bool WaitForRevitExit()
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < WaitTimeout)
        {
            var revitProcesses = Process.GetProcessesByName(RevitProcessName);
            if (revitProcesses.Length == 0) return true;

            Log($"  Waiting for Revit to close ({revitProcesses.Length} instance(s))…");
            Thread.Sleep(WaitPollInterval);
        }
        return false;
    }

    // ─── Arg parsing ─────────────────────────────────────────────────────

    private sealed class Options
    {
        public string ZipPath { get; set; } = "";
        public string RevitYear { get; set; } = "2025";
        public bool WaitForRevit { get; set; } = true;
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
                case "--revit-year" when i + 1 < args.Length:
                    opts.RevitYear = args[++i];
                    break;
                case "--wait":
                    opts.WaitForRevit = true;
                    break;
                case "--no-wait":
                    opts.WaitForRevit = false;
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
        Console.WriteLine("Usage: RevitMCPUpdater.exe --zip <path> [--revit-year 2025] [--wait|--no-wait]");
    }

    private static void Log(string line)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}");
    }
}
