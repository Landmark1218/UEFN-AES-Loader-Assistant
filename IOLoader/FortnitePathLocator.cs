using System.Text.Json;
using System.Text.Json.Serialization;

namespace UEFNMapInstaller;

/// <summary>
/// LauncherInstalled.dat から Fortnite の paks フォルダを特定します。
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
                Console.WriteLine($"[PATH] インストール先        : {installDir}");
                Console.WriteLine($"[PATH] paks フォルダ         : {paks}");
                return paks;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PATH] {datPath} の読み取りに失敗: {ex.Message}");
            }
        }

        throw new DirectoryNotFoundException(
            "Fortnite の paks フォルダが見つかりませんでした。\n" +
            "  <ドライブ>:\\ProgramData\\Epic\\UnrealEngineLauncher\\LauncherInstalled.dat を確認してください。");
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

    // ── UEFN エディタ実行ファイルの特定 ──────────────────────────────────

    /// <summary>x64dbg で解析する対象の実行ファイル名。</summary>
    public const string UnrealEditorExeName = "UnrealEditorFortnite-Win64-Shipping.exe";

    private static readonly string[] Win64RelativePaths =
    [
        @"FortniteGame\Binaries\Win64",
        @"Engine\Binaries\Win64",
    ];

    /// <summary>
    /// LauncherInstalled.dat から Fortnite/UEFN のインストール先を列挙します。
    /// </summary>
    public static IEnumerable<string> EnumerateInstallDirs()
    {
        foreach (var datPath in EnumerateDatPaths())
        {
            if (!File.Exists(datPath)) continue;

            string? installDir = null;
            try { installDir = ReadFortniteInstallDir(datPath); }
            catch { /* 壊れた dat は無視 */ }

            if (!string.IsNullOrEmpty(installDir) && Directory.Exists(installDir))
                yield return installDir!;
        }
    }

    /// <summary>
    /// UnrealEditorFortnite-Win64-Shipping.exe のフルパスを特定します。
    /// 既知の Binaries\Win64 候補 → インストール先全体の再帰探索の順で探します。
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

            // 候補に無ければインストール先を再帰的に探索 (最終手段)
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
            // アクセス拒否などは無視
        }
        return null;
    }
}
