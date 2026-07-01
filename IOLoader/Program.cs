using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using UEFNMapInstaller;

// -- Initialization --
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.Title = "UEFN Map Installer";

if (OperatingSystem.IsWindows() && !IsRunningAsAdministrator())
{
    if (TryRelaunchAsAdministrator())
        return 0;
    Warn("Not running as administrator; write operations may fail.");
    Warn("If possible, right-click the tool and select \"Run as administrator\".");
}

var quietEnv = new Dictionary<string, string> { ["UEFN_QUIET"] = "1" };
var exeDir   = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
               ?? Directory.GetCurrentDirectory();

var scriptPath = Path.Combine(exeDir, "uefn_downloader.py");
if (!File.Exists(scriptPath))
{
    Error($"uefn_downloader.py not found: {scriptPath}");
    Pause(); return 1;
}

var dataDir = Path.Combine(exeDir, "Data");
Directory.CreateDirectory(dataDir);

// -- Locate Fortnite path first --
string paksFolder;
string? targetExe;
try
{
    paksFolder = FortnitePathLocator.FindFortnitePaksFolder();
    targetExe  = FortnitePathLocator.FindUnrealEditorExe();
}
catch (Exception ex)
{
    Error(ex.Message);
    Pause(); return 1;
}

var win64Dir = targetExe is not null ? Path.GetDirectoryName(targetExe) : null;
var iniPath  = win64Dir is not null ? Path.Combine(win64Dir, "DecrypterSettings.ini") : null;

// Parent directory of GameFeatures
var contentDir      = Directory.GetParent(paksFolder)?.FullName;
var gameDir         = contentDir is not null ? Directory.GetParent(contentDir)?.FullName : null;
var gameFeaturesDir = gameDir is not null ? Path.Combine(gameDir, "Plugins", "GameFeatures") : null;

// installed_plugins.json: tracks installed uplugin folder names
var installedPluginsJson = Path.Combine(dataDir, "installed_plugins.json");

// -- Menu --
Console.WriteLine();
Console.WriteLine("╔══════════════════════════════════╗");
Console.WriteLine("║      UEFN Map Installer          ║");
Console.WriteLine("╠══════════════════════════════════╣");
Console.WriteLine("║  1  Install map                  ║");
Console.WriteLine("║  2  Remove installed data        ║");
Console.WriteLine("╚══════════════════════════════════╝");
Console.WriteLine();

int menuChoice = 0;
while (menuChoice is not 1 and not 2)
{
    Console.Write("Select [1/2]: ");
    var key = Console.ReadLine()?.Trim();
    if (key == "1") menuChoice = 1;
    else if (key == "2") menuChoice = 2;
    else Error("Please enter 1 or 2.");
}

// ════════════════════════════════════════════════════════════════════════════
//  Mode 2: Remove installed data
// ════════════════════════════════════════════════════════════════════════════
if (menuChoice == 2)
{
    Console.WriteLine();
    Console.WriteLine("-- Uninstall ───────────────────────────────────────");

    // Remove plugin.* from paks folder
    var pakFiles = new[] { "plugin.pak", "plugin.ucas", "plugin.utoc", "plugin.sig" };
    int removedPak = 0;
    foreach (var name in pakFiles)
    {
        var path = Path.Combine(paksFolder, name);
        if (!File.Exists(path)) continue;
        try
        {
            ClearReadOnly(path);
            File.Delete(path);
            Console.WriteLine($"[DEL] {path}");
            removedPak++;
        }
        catch (Exception ex)
        {
            Warn($"{path}: deletion failed: {ex.Message}");
        }
    }
    if (removedPak == 0)
        Console.WriteLine("[DEL] No plugin.* files found in paks folder.");

    // Fetch folder list from installed_plugins.json and remove them
    var pluginFolders = LoadInstalledPlugins(installedPluginsJson);
    if (pluginFolders.Count == 0)
    {
        Console.WriteLine("[DEL] No recorded GameFeatures folders.");
    }
    else if (gameFeaturesDir is null)
    {
        Warn("Could not determine GameFeatures folder path. Please remove manually.");
        foreach (var f in pluginFolders)
            Warn($"  To remove: {f}");
    }
    else
    {
        var remaining = new List<string>();
        foreach (var folderName in pluginFolders)
        {
            var fullPath = Path.Combine(gameFeaturesDir, folderName);
            if (!Directory.Exists(fullPath))
            {
                Console.WriteLine($"[DEL] Already gone: {fullPath}");
                // Also remove from json
                continue;
            }
            try
            {
                Directory.Delete(fullPath, recursive: true);
                Console.WriteLine($"[DEL] {fullPath}");
            }
            catch (Exception ex)
            {
                Warn($"{fullPath}: deletion failed: {ex.Message}");
                remaining.Add(folderName); // keep in record if deletion failed
            }
        }
        SaveInstalledPlugins(installedPluginsJson, remaining);
    }

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("Removal complete.");
    Console.ResetColor();
    Pause();
    return 0;
}

// ════════════════════════════════════════════════════════════════════════════
//  Mode 1: Install map
// ════════════════════════════════════════════════════════════════════════════

if (iniPath is null)
    Warn($"{FortnitePathLocator.UnrealEditorExeName} not found; skipping DecrypterSettings.ini update.");

// -- Map code input --
string mapCode;
while (true)
{
    Console.Write("Enter map code (e.g. 1234-5678-9012): ");
    var raw    = Console.ReadLine()?.Trim() ?? "";
    var digits = Regex.Replace(raw, @"\D", "");
    if (digits.Length == 12)
    {
        mapCode = $"{digits[..4]}-{digits[4..8]}-{digits[8..]}";
        break;
    }
    Error("Please enter a 12-digit number.");
}

// -- Login (first run only) --
var deviceAuthPath = Path.Combine(dataDir, "auth", "device_auth.json");
if (!File.Exists(deviceAuthPath))
{
    int loginCode = PythonRunner.Run(scriptPath,
        ["device-login", "--data-dir", Path.Combine(dataDir, "auth")],
        exeDir, quietEnv);
    if (loginCode != 0)
    {
        Error($"Login failed (exit code {loginCode})");
        Pause(); return 1;
    }
}

// -- Fetch AES key -> DecrypterSettings.ini --
var authDir   = Path.Combine(dataDir, "auth");
var mapOutDir = Path.Combine(dataDir, mapCode);
int aesCode   = PythonRunner.Run(scriptPath,
    ["resolve-v2", mapCode, "--data-dir", authDir, "--out", dataDir],
    exeDir, quietEnv);

if (aesCode != 0)
    Warn("Failed to fetch AES key (no issue if the map is unencrypted).");
else if (iniPath is not null)
{
    var keychain = TryBuildKeychain(Path.Combine(mapOutDir, "module_key_v4.json"));
    if (keychain is not null)
    {
        WriteIni(iniPath, "ContentKeys", "Key0", keychain);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[INI] Key0 written: {iniPath}");
        Console.ResetColor();
    }
}

// -- Fetch Signature -> DecrypterSettings.ini --
if (iniPath is not null)
{
    var engineDll = win64Dir is not null
        ? Path.Combine(win64Dir, "UnrealEditorFortnite-Engine-Win64-Shipping.dll")
        : null;

    if (engineDll is null || !File.Exists(engineDll))
        Warn("UnrealEditorFortnite-Engine-Win64-Shipping.dll not found; skipping Signature update.");
    else
    {
        var sigCachePath   = Path.Combine(dataDir, "signature_cache.json");
        var sigCache       = LoadSignatureCache(sigCachePath);
        var currentVersion = FetchGameVersion(scriptPath, exeDir);

        if (currentVersion is not null)
            Console.WriteLine($"[SIG] Current game version: {currentVersion}");
        else
            Warn("Failed to get game version. Re-scanning DLL.");

        string? signature   = null;
        string? functionRva = null;

        bool useCache = currentVersion is not null
            && sigCache is not null
            && string.Equals(sigCache.GameVersion, currentVersion, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(sigCache.Signature);

        if (useCache)
        {
            signature   = sigCache!.Signature;
            functionRva = sigCache.FunctionRva;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[SIG] Game version unchanged ({currentVersion}) -> Reusing cached Signature (scan skipped)");
            Console.WriteLine($"[SIG] Signature (cache): {signature}");
            Console.ResetColor();
        }
        else
        {
            Console.WriteLine();
            var (sigCode, sigOut) = PythonRunner.RunCaptureWithLiveStderr(
                scriptPath, ["find-signature", engineDll], exeDir,
                stderrColor: ConsoleColor.Cyan);

            foreach (var rawLine in sigOut.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("Signature=", StringComparison.OrdinalIgnoreCase))
                    signature = line["Signature=".Length..].Trim();
                else if (line.StartsWith("FunctionRVA=", StringComparison.OrdinalIgnoreCase))
                    functionRva = line["FunctionRVA=".Length..].Trim();
            }

            if (sigCode != 0 || string.IsNullOrEmpty(signature))
            {
                Warn("Failed to retrieve Signature. Please set Signature in DecrypterSettings.ini manually.");
                signature = null;
            }
            else if (currentVersion is not null)
            {
                SaveSignatureCache(sigCachePath, new SignatureCache
                {
                    GameVersion  = currentVersion,
                    Signature    = signature,
                    FunctionRva  = functionRva,
                    UpdatedAtUtc = DateTime.UtcNow.ToString("O"),
                });
                Console.WriteLine($"[SIG] Cache updated: {sigCachePath}");
            }
        }

        if (!string.IsNullOrEmpty(signature))
        {
            WriteIni(iniPath, "Settings", "Signature", signature);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[INI] Signature written: {signature}");
            Console.ResetColor();
        }
    }
}

// -- Download game data --
Console.WriteLine();
Console.WriteLine("Downloading game data...");
int dlCode = PythonRunner.Run(scriptPath,
    ["download", mapCode, "--data-dir", authDir, "--out", dataDir, "--skip-aes-key"],
    exeDir, quietEnv);

if (dlCode != 0)
{
    Error($"Download failed (exit code {dlCode})");
    Pause(); return 1;
}

// -- Move plugin.* to paks folder --
var targetFiles = new[] { "plugin.pak", "plugin.ucas", "plugin.utoc", "plugin.sig" };
int movedCount  = 0;

try
{
    foreach (var name in targetFiles)
    {
        var src = Path.Combine(mapOutDir, name);
        if (!File.Exists(src)) continue;

        var dst = Path.Combine(paksFolder, name);
        if (File.Exists(dst)) { ClearReadOnly(dst); File.Delete(dst); }

        ClearReadOnly(src);
        File.Move(src, dst);
        movedCount++;
    }
}
catch (UnauthorizedAccessException)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[ERROR] {paksFolder}: write access denied.");
    Console.WriteLine($"          icacls \"{paksFolder}\" /grant *S-1-5-32-544:(OI)(CI)F /T");
    Console.ResetColor();
    Pause(); return 1;
}
catch (IOException ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[ERROR] Write to {paksFolder} failed. Please fully exit Epic Games Launcher and Fortnite, then try again.");
    Console.WriteLine($"        Details: {ex.Message}");
    Console.ResetColor();
    Pause(); return 1;
}

Console.WriteLine();
if (movedCount == 0)
{
    Warn("No files could be moved.");
    Warn($"Please check the download folder: {mapOutDir}");
}
else
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"Installation complete! {movedCount} file(s) moved.");
    Console.WriteLine($"Installed to: {paksFolder}");
    Console.ResetColor();
}

// -- Export and place GameFeatures plugin --
var (gfModuleId, gfAesKeyHex, gfGuid) = GameFeaturePluginExporter.ReadModuleKeyJson(
    Path.Combine(mapOutDir, "module_key_v4.json"));

if (!string.IsNullOrEmpty(gfModuleId) && !string.IsNullOrEmpty(gfAesKeyHex))
{
    try
    {
        bool gfOk = GameFeaturePluginExporter.ExportPlugin(paksFolder, gfModuleId!, gfAesKeyHex!, gfGuid);
        if (gfOk)
        {
            // Record installed folder name (used as removal target in mode 2)
            var installed = LoadInstalledPlugins(installedPluginsJson);
            if (!installed.Contains(gfModuleId!, StringComparer.OrdinalIgnoreCase))
            {
                installed.Add(gfModuleId!);
                SaveInstalledPlugins(installedPluginsJson, installed);
                Console.WriteLine($"[GF] Folder name recorded: {gfModuleId}");
            }
        }
    }
    catch (Exception ex)
    {
        Warn($"Failed to place GameFeatures plugin: {ex.Message}");
    }
}
else
{
    Console.WriteLine("[GF] Could not obtain moduleId/aesKeyHex; skipping GameFeatures plugin placement.");
}

// -- Update ContentBrowserDrawer.SelectedPaths in EditorPerProjectUserSettings.ini --
if (!string.IsNullOrEmpty(gfModuleId))
{
    var editorIniPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UnrealEditorFortnite", "Saved", "Config", "WindowsEditor",
        "EditorPerProjectUserSettings.ini");

    UpdateContentBrowserSelectedPaths(editorIniPath, gfModuleId!);
}

// -- Launch game --
Console.WriteLine();
Console.WriteLine("-- Launch game ─────────────────────────────────────");

// Check amfrt64.dll
var amfrtDll  = win64Dir is not null ? Path.Combine(win64Dir, "amfrt64.dll") : null;
var gameExe   = win64Dir is not null ? Path.Combine(win64Dir, "UnrealEditorFortnite-Win64-Shipping.exe") : null;

if (amfrtDll is null || !File.Exists(amfrtDll))
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("[!!] amfrt64.dll not found.");
    Console.WriteLine("[!!] AES Loader setup required:");
    Console.WriteLine("[!!]   https://github.com/Aleman-sein-Vater/UEFN-AES-Loader");
    Console.ResetColor();
    Console.WriteLine();
    Console.Write("Open the above URL in your browser? [Y/n]: ");
    var ans = Console.ReadLine()?.Trim();
    if (!string.Equals(ans, "n", StringComparison.OrdinalIgnoreCase))
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/Aleman-sein-Vater/UEFN-AES-Loader") { UseShellExecute = true }); }
        catch { Warn("Could not open browser. Please visit the URL manually."); }
    }
}
else if (gameExe is null || !File.Exists(gameExe))
{
    Warn($"UnrealEditorFortnite-Win64-Shipping.exe not found. Please launch manually.");
}
else
{
    // Pass the same name as the uplugin (= gfModuleId) to the -enableplugins= argument
    var enablePlugins = !string.IsNullOrEmpty(gfModuleId) ? gfModuleId : "";

    var launchArgs =
        $"-disableplugins=\"AtomVK,FNChaosVD,ValkyrieFortnite,FNTraceBasedDebuggers,FNRewindDebugger,UEFN\" " +
        $"-enableplugins=\"{enablePlugins}\"";

    Console.WriteLine($"[LAUNCH] {Path.GetFileName(gameExe)}");
    Console.WriteLine($"[LAUNCH] {launchArgs}");

    try
    {
        Process.Start(new ProcessStartInfo(gameExe, launchArgs)
        {
            UseShellExecute  = true,
            WorkingDirectory = win64Dir!,
        });
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("[LAUNCH] Launched.");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Warn($"Launch failed: {ex.Message}");
    }
}

Pause();
return 0;

// -- Helpers --

static string? TryBuildKeychain(string jsonPath)
{
    if (!File.Exists(jsonPath)) return null;
    try
    {
        using var doc  = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var root       = doc.RootElement;
        var guid       = root.TryGetProperty("guid",  out var g) ? g.GetString() : null;
        if (string.IsNullOrEmpty(guid)) return null;

        string? base64 = null;
        if (root.TryGetProperty("raw", out var raw)
            && raw.TryGetProperty("key", out var keyObj)
            && keyObj.TryGetProperty("Key", out var keyVal))
            base64 = keyVal.GetString();

        if (string.IsNullOrEmpty(base64))
        {
            var hex = root.TryGetProperty("aesKeyHex", out var h) ? h.GetString() : null;
            if (string.IsNullOrEmpty(hex)) return null;
            if (hex!.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
            base64 = Convert.ToBase64String(Convert.FromHexString(hex));
        }
        return $"{guid!.Replace("-", "").ToUpperInvariant()}:{base64}";
    }
    catch { return null; }
}

static void WriteIni(string iniPath, string section, string key, string value)
{
    const string IniTemplate =
        "[Settings]\r\nModuleName=unrealeditorfortnite-engine-win64-shipping.dll\r\nSignature=??\r\nTimeout=10000\r\n\r\n[ContentKeys]\r\nKey0=\r\n";
    try   { IniEditor.SetValue(iniPath, section, key, value, IniTemplate); }
    catch (UnauthorizedAccessException)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[ERROR] {iniPath}: write access denied. Please run as administrator.");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[ERROR] Failed to update ini: {ex.Message}");
        Console.ResetColor();
    }
}

static List<string> LoadInstalledPlugins(string jsonPath)
{
    if (!File.Exists(jsonPath)) return [];
    try   { return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(jsonPath)) ?? []; }
    catch { return []; }
}

static void SaveInstalledPlugins(string jsonPath, List<string> folders)
{
    try   { File.WriteAllText(jsonPath, JsonSerializer.Serialize(folders, new JsonSerializerOptions { WriteIndented = true })); }
    catch (Exception ex) { Warn($"Failed to save installed_plugins.json: {ex.Message}"); }
}

static void Warn(string msg)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"[!!] {msg}");
    Console.ResetColor();
}

static void Error(string msg)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[ERROR] {msg}");
    Console.ResetColor();
}

static void Pause()
{
    Console.WriteLine();
    Console.WriteLine("Press Enter to exit...");
    Console.ReadLine();
}

static void ClearReadOnly(string path)
{
    try
    {
        var attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
            File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
    }
    catch { }
}

static string? FetchGameVersion(string scriptPath, string exeDir)
{
    try
    {
        var (code, stdout, _) = PythonRunner.RunCapture(scriptPath, ["game-version"], exeDir);
        if (code != 0) return null;
        var v = stdout.Trim();
        return v.Length > 0 ? v : null;
    }
    catch { return null; }
}

static SignatureCache? LoadSignatureCache(string cachePath)
{
    if (!File.Exists(cachePath)) return null;
    try   { return JsonSerializer.Deserialize<SignatureCache>(File.ReadAllText(cachePath)); }
    catch { return null; }
}

static void SaveSignatureCache(string cachePath, SignatureCache cache)
{
    try
    {
        File.WriteAllText(cachePath, JsonSerializer.Serialize(cache,
            new JsonSerializerOptions { WriteIndented = true }));
    }
    catch (Exception ex) { Warn($"Failed to save signature cache: {ex.Message}"); }
}

static bool IsRunningAsAdministrator()
{
    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
}

static bool TryRelaunchAsAdministrator()
{
    try
    {
        var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return false;
        Process.Start(new ProcessStartInfo(exePath)
        {
            UseShellExecute  = true,
            Verb             = "runas",
            WorkingDirectory = Environment.CurrentDirectory,
        });
        return true;
    }
    catch { return false; }
}

/// <summary>
/// %LocalAppData%\UnrealEditorFortnite\Saved\Config\WindowsEditor\EditorPerProjectUserSettings.ini
/// inside the [/Script/ContentBrowser.ContentBrowserSettings] section
/// and sets ContentBrowserDrawer.SelectedPaths= to /<moduleId>.
/// Appends the key at the end of the section if it does not exist.
/// Does nothing and only warns if the file itself does not exist.
/// </summary>
static void UpdateContentBrowserSelectedPaths(string iniPath, string moduleId)
{
    // Value to write: /2b3d37a4-4c69-d552-f25d-818ee9d96a77
    var newValue = $"/{moduleId}";
    const string key = "ContentBrowserDrawer.SelectedPaths";

    if (!File.Exists(iniPath))
    {
        Console.WriteLine($"[CB] EditorPerProjectUserSettings.ini not found; skipping: {iniPath}");
        return;
    }

    try
    {
        var lines   = File.ReadAllLines(iniPath);
        var updated = false;

        for (int i = 0; i < lines.Length; i++)
        {
            // Find the line starting with \"ContentBrowserDrawer.SelectedPaths=\"
            // (ignoring surrounding whitespace, case-insensitive)
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                var existing = trimmed[(key.Length + 1)..]; // right side of \"=\"
                if (string.Equals(existing, newValue, StringComparison.Ordinal))
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[CB] ContentBrowserDrawer.SelectedPaths already has the correct value: {newValue}");
                    Console.ResetColor();
                    return;
                }

                // Preserve indentation (leading spaces etc.) and replace only the value
                var indent = lines[i].Length - trimmed.Length;
                lines[i] = lines[i][..indent] + key + "=" + newValue;
                updated   = true;
                break;
            }
        }

        if (!updated)
        {
            // Key not found: insert after [/Script/ContentBrowser.ContentBrowserSettings]
            // section header. Append to end of file if section is not found.
            const string targetSection = "[/Script/ContentBrowser.ContentBrowserSettings]";
            var list = lines.ToList();
            int insertAt = -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].TrimStart().Equals(targetSection, StringComparison.OrdinalIgnoreCase))
                {
                    insertAt = i + 1;
                    break;
                }
            }

            var newLine = key + "=" + newValue;
            if (insertAt >= 0)
                list.Insert(insertAt, newLine);
            else
                list.Add(newLine);

            lines = [.. list];
        }

        File.WriteAllLines(iniPath, lines, new System.Text.UTF8Encoding(false));

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[CB] ContentBrowserDrawer.SelectedPaths updated: {newValue}");
        Console.WriteLine($"[CB] File: {iniPath}");
        Console.ResetColor();
    }
    catch (UnauthorizedAccessException)
    {
        Warn($"Write access denied for EditorPerProjectUserSettings.ini: {iniPath}");
    }
    catch (Exception ex)
    {
        Warn($"EditorPerProjectUserSettings.Failed to update ini: {ex.Message}");
    }
}
