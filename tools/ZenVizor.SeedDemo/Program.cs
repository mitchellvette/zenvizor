// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using ZenVizor.Storage;

namespace ZenVizor.SeedDemo;

/// <summary>
/// DEV-ONLY marketing-screenshot seeder. Populates a THROWAWAY SQLite store
/// with the fixed synthetic dataset the ZenVizor website hardcodes, so the app
/// can be pointed at it (via <c>ZENVIZOR_DATA_DIR</c>) to capture screenshots.
/// Never ships (the csproj hard-fails a Release build) and refuses to run
/// against the real <c>%ProgramData%\ZenVizor\</c> store.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            var command = args[0];
            var dataDir = GetOption(args, "--data-dir");
            var force = HasFlag(args, "--force");

            return command switch
            {
                "seed"     => RunSeed(dataDir, force),
                "teardown" => RunTeardown(dataDir),
                "-h" or "--help" or "help" => PrintUsageAndOk(),
                _ => UnknownCommand(command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    // ─── seed ──────────────────────────────────────────────────────────────

    private static int RunSeed(string? dataDir, bool force)
    {
        if (!TryResolveSafeDataDir(dataDir, out var resolved, out var error))
        {
            Console.Error.WriteLine($"ERROR: {error}");
            return 1;
        }

        var dbPath = Path.Combine(resolved, StorageConstants.DatabaseFileName);

        if (File.Exists(dbPath) && !force)
        {
            Console.Error.WriteLine(
                $"ERROR: a database already exists at {dbPath}.\n" +
                "       Re-run with --force to wipe and reseed it, or pick a fresh --data-dir.");
            return 1;
        }

        Directory.CreateDirectory(resolved);
        DeleteDatabaseFiles(dbPath); // clears any --force leftovers incl. -wal / -shm

        Console.WriteLine($"Migrating schema into {dbPath} ...");
        var migrator = new Migrator();
        var applied = migrator.Migrate(dbPath);
        Console.WriteLine($"  applied {applied.Count} migration(s).");

        Console.WriteLine("Seeding canonical demo dataset ...");
        var summary = DemoDataSeeder.Seed(dbPath);
        Console.WriteLine(summary);

        Console.WriteLine();
        Console.WriteLine("Done. To capture screenshots against this store (full walkthrough in");
        Console.WriteLine("tools/ZenVizor.SeedDemo/README.md):");
        Console.WriteLine( "  1. Stop the installed service:  sc.exe stop ZenVizor   (elevated)");
        Console.WriteLine( "  2. In that same ELEVATED shell, set both overrides:");
        Console.WriteLine($"       $env:ZENVIZOR_DATA_DIR = \"{resolved}\"");
        Console.WriteLine( "       $env:ZENVIZOR_DISABLE_CAPTURE = \"1\"");
        Console.WriteLine( "  3. Run the service in the foreground from that shell (it inherits both vars):");
        Console.WriteLine( "       .\\src\\ZenVizor.Service\\bin\\Release\\net10.0-windows\\ZenVizor.Service.exe");
        Console.WriteLine( "  4. Launch the UI as usual (non-elevated) — it needs no env vars.");
        Console.WriteLine( "  5. When finished: Ctrl+C the service, then run teardown from an ELEVATED");
        Console.WriteLine( "     shell (the service re-ACLs this dir to Administrators):");
        Console.WriteLine($"       dotnet run --project tools/ZenVizor.SeedDemo -c Debug -- teardown --data-dir \"{resolved}\"");
        return 0;
    }

    // ─── teardown ──────────────────────────────────────────────────────────

    private static int RunTeardown(string? dataDir)
    {
        if (!TryResolveSafeDataDir(dataDir, out var resolved, out var error))
        {
            Console.Error.WriteLine($"ERROR: {error}");
            return 1;
        }

        if (!Directory.Exists(resolved))
        {
            Console.WriteLine($"Nothing to remove — {resolved} does not exist.");
            return 0;
        }

        // Sanity gate: only delete a directory that actually looks like a
        // ZenVizor data dir, so a mistyped --data-dir can't nuke an unrelated
        // tree. The prod-path guard in TryResolveSafeDataDir already refused
        // the real store above.
        var dbPath = Path.Combine(resolved, StorageConstants.DatabaseFileName);
        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine(
                $"ERROR: {resolved} has no {StorageConstants.DatabaseFileName}; refusing to delete it.\n" +
                "       Point --data-dir at the demo directory the seed created.");
            return 1;
        }

        Directory.Delete(resolved, recursive: true);
        Console.WriteLine($"Removed demo data directory {resolved}.");
        return 0;
    }

    // ─── safety guard ──────────────────────────────────────────────────────

    /// <summary>
    /// Resolves <paramref name="dataDir"/> to an absolute path and refuses the
    /// real production store. Requires the caller to pass an explicit
    /// <c>--data-dir</c> — there is deliberately no default, so the tool can
    /// never fall back to <c>%ProgramData%\ZenVizor\</c>.
    /// </summary>
    private static bool TryResolveSafeDataDir(string? dataDir, out string resolved, out string error)
    {
        resolved = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(dataDir))
        {
            error = "--data-dir <path> is required (a throwaway directory, NOT the real ZenVizor store).";
            return false;
        }

        resolved = Path.GetFullPath(dataDir);

        var prodDir = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            StorageConstants.DataDirectoryName));

        if (PathsEqual(resolved, prodDir))
        {
            error =
                $"refusing to operate on the production data directory ({prodDir}).\n" +
                "       This tool only ever touches a throwaway demo store.";
            return false;
        }

        return true;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static void DeleteDatabaseFiles(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var p = dbPath + suffix;
            if (File.Exists(p)) File.Delete(p);
        }
    }

    // ─── arg parsing ───────────────────────────────────────────────────────

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
                return args[i + 1];
        }
        return null;
    }

    private static bool HasFlag(string[] args, string name) =>
        Array.Exists(args, a => string.Equals(a, name, StringComparison.Ordinal));

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"ERROR: unknown command '{command}'.");
        PrintUsage();
        return 1;
    }

    private static int PrintUsageAndOk()
    {
        PrintUsage();
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"""
            ZenVizor.SeedDemo — DEV-ONLY marketing-screenshot data seeder.

            USAGE:
              seed     --data-dir <throwaway-dir> [--force]   Create + seed a demo store.
              teardown --data-dir <throwaway-dir>             Delete the demo store.

            Notes:
              * --data-dir is REQUIRED and must NOT be the real ZenVizor store
                ({Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), StorageConstants.DataDirectoryName)}).
              * This tool never ships: the project fails to build in Release.
            """));
    }
}
