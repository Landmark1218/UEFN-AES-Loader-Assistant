using System.Text.Json;
using System.Text.Json.Serialization;

namespace UEFNMapInstaller;

/// <summary>
/// Locates the Fortnite paks folder from LauncherInstalled.dat.
/// </summary>
internal static class FortnitePathLocator
{
    private static readonly string[] PaksRelativePaths =
    [
        @"FortniteGame\Content\Paks",
        @"FortniteGame\Content\paks",
    ];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string FindFortnitePaksFolder()
    {
        foreach (var datPath in EnumerateDatPaths())
        {
            if (!File.Exists(datPath)) continue;

            try
            {
                var installDir = ReadFortniteInstallDir(datPath);
                if (installDir is null) continue;

                var paks = FindPaksDir(installDir);
                if (paks is null) continue;

                Console.WriteLine($"[PATH] LauncherInstalled.dat : {datPath}");
                Console.WriteLine($"[PATH] Install location : {installDir}");
                Console.WriteLine($"[PATH] Paks folder       : {paks}");
                return paks;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PATH] {datPath}: read failed: {ex.Message}");
            }
        }

        throw new DirectoryNotFoundException(
            "Fortnite paks folder not found.\n" +
            "  Please check <drive>:\\ProgramData\\Epic\\UnrealEngineLauncher\\LauncherInstalled.dat.");
    }

    private static IEnumerable<string> EnumerateDatPaths()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            yield return Path.Combine(drive.RootDirectory.FullName.TrimEnd('\\'),
                @"ProgramData\Epic\UnrealEngineLauncher\LauncherInstalled.dat");
    }

    private static string? ReadFortniteInstallDir(string datPath)
    {
        var data = JsonSerializer.Deserialize<LauncherInstalled>(
            File.ReadAllText(datPath), JsonOpts);

        return data?.InstallationList?
            .FirstOrDefault(e =>
                string.Equals(e.AppName, "Fortnite",        StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.AppName, "Fortnite_Studio", StringComparison.OrdinalIgnoreCase))
            ?.InstallLocation;
    }

    private static string? FindPaksDir(string installDir)
    {
        foreach (var rel in PaksRelativePaths)
        {
            var candidate = Path.Combine(installDir, rel);
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    // -- Locate UEFN editor executable --

    /// <summary>Name of the executable targeted for analysis with x64dbg.</summary>
    public const string UnrealEditorExeName = "UnrealEditorFortnite-Win64-Shipping.exe";

    private static readonly string[] Win64RelativePaths =
    [
        @"FortniteGame\Binaries\Win64",
        @"Engine\Binaries\Win64",
    ];

    /// <summary>
    /// Enumerates Fortnite/UEFN install locations from LauncherInstalled.dat.
    /// </summary>
    public static IEnumerable<string> EnumerateInstallDirs()
    {
        foreach (var datPath in EnumerateDatPaths())
        {
            if (!File.Exists(datPath)) continue;

            string? installDir = null;
            try { installDir = ReadFortniteInstallDir(datPath); }
            catch { /* Ignore corrupted dat */ }

            if (!string.IsNullOrEmpty(installDir) && Directory.Exists(installDir))
                yield return installDir!;
        }
    }

    /// <summary>
    /// Locates the full path of UnrealEditorFortnite-Win64-Shipping.exe.
    /// Searches known Binaries\\Win64 candidates, then falls back to a full recursive search.
    /// </summary>
    public static string? FindUnrealEditorExe()
    {
        foreach (var installDir in EnumerateInstallDirs())
        {
            foreach (var rel in Win64RelativePaths)
            {
                var candidate = Path.Combine(installDir, rel, UnrealEditorExeName);
                if (File.Exists(candidate))
                {
                    Console.WriteLine($"[PATH] UEFN exe : {candidate}");
                    return candidate;
                }
            }

            // Not in candidates — recursively search the install dir (last resort)
            var found = SafeFindFile(installDir, UnrealEditorExeName);
            if (found is not null)
            {
                Console.WriteLine($"[PATH] UEFN exe : {found}");
                return found;
            }
        }
        return null;
    }

    private static string? SafeFindFile(string root, string fileName)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories))
                return path;
        }
        catch
        {
            // Ignore access-denied errors
        }
        return null;
    }
}
