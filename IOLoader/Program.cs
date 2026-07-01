using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using UEFNMapInstaller;

// ── 初期化 ────────────────────────────────────────────────────────────────
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.Title = "UEFN Map Installer";

if (OperatingSystem.IsWindows() && !IsRunningAsAdministrator())
{
    if (TryRelaunchAsAdministrator())
        return 0;
    Warn("管理者権限が無いため、書き込みが必要な処理で失敗する可能性があります。");
    Warn("可能であればこのツールを右クリック→「管理者として実行」してください。");
}

var quietEnv = new Dictionary<string, string> { ["UEFN_QUIET"] = "1" };
var exeDir   = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
               ?? Directory.GetCurrentDirectory();

var scriptPath = Path.Combine(exeDir, "uefn_downloader.py");
if (!File.Exists(scriptPath))
{
    Error($"uefn_downloader.py が見つかりません: {scriptPath}");
    Pause(); return 1;
}

var dataDir = Path.Combine(exeDir, "Data");
Directory.CreateDirectory(dataDir);

// ── Fortnite パスを先に特定 ──────────────────────────────────────────────
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

// GameFeatures の親ディレクトリ
var contentDir      = Directory.GetParent(paksFolder)?.FullName;
var gameDir         = contentDir is not null ? Directory.GetParent(contentDir)?.FullName : null;
var gameFeaturesDir = gameDir is not null ? Path.Combine(gameDir, "Plugins", "GameFeatures") : null;

// installed_plugins.json: インストールした uplugin フォルダ名を管理する
var installedPluginsJson = Path.Combine(dataDir, "installed_plugins.json");

// ── メニュー ─────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("╔══════════════════════════════════╗");
Console.WriteLine("║      UEFN Map Installer          ║");
Console.WriteLine("╠══════════════════════════════════╣");
Console.WriteLine("║  1  マップをインストール         ║");
Console.WriteLine("║  2  インストール済みデータを削除 ║");
Console.WriteLine("╚══════════════════════════════════╝");
Console.WriteLine();

int menuChoice = 0;
while (menuChoice is not 1 and not 2)
{
    Console.Write("選択 [1/2]: ");
    var key = Console.ReadLine()?.Trim();
    if (key == "1") menuChoice = 1;
    else if (key == "2") menuChoice = 2;
    else Error("1 か 2 を入力してください。");
}

// ════════════════════════════════════════════════════════════════════════════
//  モード 2: インストール済みデータの削除
// ════════════════════════════════════════════════════════════════════════════
if (menuChoice == 2)
{
    Console.WriteLine();
    Console.WriteLine("── アンインストール ────────────────────────────────");

    // paks フォルダから plugin.* を削除
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
            Warn($"{path} の削除に失敗しました: {ex.Message}");
        }
    }
    if (removedPak == 0)
        Console.WriteLine("[DEL] paks フォルダに plugin.* ファイルは見つかりませんでした。");

    // installed_plugins.json からフォルダ一覧を取得して削除
    var pluginFolders = LoadInstalledPlugins(installedPluginsJson);
    if (pluginFolders.Count == 0)
    {
        Console.WriteLine("[DEL] 記録された GameFeatures フォルダはありません。");
    }
    else if (gameFeaturesDir is null)
    {
        Warn("GameFeatures フォルダのパスを特定できませんでした。手動で削除してください。");
        foreach (var f in pluginFolders)
            Warn($"  削除対象: {f}");
    }
    else
    {
        var remaining = new List<string>();
        foreach (var folderName in pluginFolders)
        {
            var fullPath = Path.Combine(gameFeaturesDir, folderName);
            if (!Directory.Exists(fullPath))
            {
                Console.WriteLine($"[DEL] 既に存在しません: {fullPath}");
                // json からも除去
                continue;
            }
            try
            {
                Directory.Delete(fullPath, recursive: true);
                Console.WriteLine($"[DEL] {fullPath}");
            }
            catch (Exception ex)
            {
                Warn($"{fullPath} の削除に失敗しました: {ex.Message}");
                remaining.Add(folderName); // 削除失敗したものは記録に残す
            }
        }
        SaveInstalledPlugins(installedPluginsJson, remaining);
    }

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("削除処理が完了しました。");
    Console.ResetColor();
    Pause();
    return 0;
}

// ════════════════════════════════════════════════════════════════════════════
//  モード 1: マップのインストール
// ════════════════════════════════════════════════════════════════════════════

if (iniPath is null)
    Warn($"{FortnitePathLocator.UnrealEditorExeName} が見つからないため DecrypterSettings.ini の更新をスキップします。");

// ── マップコード入力 ──────────────────────────────────────────────────────
string mapCode;
while (true)
{
    Console.Write("マップコードを入力してください (例: 1234-5678-9012): ");
    var raw    = Console.ReadLine()?.Trim() ?? "";
    var digits = Regex.Replace(raw, @"\D", "");
    if (digits.Length == 12)
    {
        mapCode = $"{digits[..4]}-{digits[4..8]}-{digits[8..]}";
        break;
    }
    Error("12桁の数字で入力してください。");
}

// ── ログイン (初回のみ) ───────────────────────────────────────────────────
var deviceAuthPath = Path.Combine(dataDir, "auth", "device_auth.json");
if (!File.Exists(deviceAuthPath))
{
    int loginCode = PythonRunner.Run(scriptPath,
        ["device-login", "--data-dir", Path.Combine(dataDir, "auth")],
        exeDir, quietEnv);
    if (loginCode != 0)
    {
        Error($"ログインに失敗しました (終了コード {loginCode})");
        Pause(); return 1;
    }
}

// ── AES キー取得 → DecrypterSettings.ini ─────────────────────────────────
var authDir   = Path.Combine(dataDir, "auth");
var mapOutDir = Path.Combine(dataDir, mapCode);
int aesCode   = PythonRunner.Run(scriptPath,
    ["resolve-v2", mapCode, "--data-dir", authDir, "--out", dataDir],
    exeDir, quietEnv);

if (aesCode != 0)
    Warn("AES キーの取得に失敗しました (非暗号化マップの場合は問題ありません)。");
else if (iniPath is not null)
{
    var keychain = TryBuildKeychain(Path.Combine(mapOutDir, "module_key_v4.json"));
    if (keychain is not null)
    {
        WriteIni(iniPath, "ContentKeys", "Key0", keychain);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[INI] Key0 を書き込みました: {iniPath}");
        Console.ResetColor();
    }
}

// ── Signature 取得 → DecrypterSettings.ini ───────────────────────────────
if (iniPath is not null)
{
    var engineDll = win64Dir is not null
        ? Path.Combine(win64Dir, "UnrealEditorFortnite-Engine-Win64-Shipping.dll")
        : null;

    if (engineDll is null || !File.Exists(engineDll))
        Warn("UnrealEditorFortnite-Engine-Win64-Shipping.dll が見つからないため Signature の更新をスキップします。");
    else
    {
        var sigCachePath   = Path.Combine(dataDir, "signature_cache.json");
        var sigCache       = LoadSignatureCache(sigCachePath);
        var currentVersion = FetchGameVersion(scriptPath, exeDir);

        if (currentVersion is not null)
            Console.WriteLine($"[SIG] 現在のゲームバージョン: {currentVersion}");
        else
            Warn("ゲームバージョンの取得に失敗しました。DLL を再スキャンします。");

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
            Console.WriteLine($"[SIG] ゲームバージョンに変更なし ({currentVersion}) → キャッシュ済み Signature を再利用します (スキャン省略)");
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
                Warn("Signature の取得に失敗しました。DecrypterSettings.ini の Signature は手動で設定してください。");
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
                Console.WriteLine($"[SIG] キャッシュを更新しました: {sigCachePath}");
            }
        }

        if (!string.IsNullOrEmpty(signature))
        {
            WriteIni(iniPath, "Settings", "Signature", signature);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[INI] Signature を書き込みました: {signature}");
            Console.ResetColor();
        }
    }
}

// ── ゲームデータダウンロード ──────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("ゲームデータダウンロード中...");
int dlCode = PythonRunner.Run(scriptPath,
    ["download", mapCode, "--data-dir", authDir, "--out", dataDir, "--skip-aes-key"],
    exeDir, quietEnv);

if (dlCode != 0)
{
    Error($"ダウンロードに失敗しました (終了コード {dlCode})");
    Pause(); return 1;
}

// ── plugin.* を paks フォルダへ移動 ──────────────────────────────────────
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
    Console.WriteLine($"[ERROR] {paksFolder} への書き込みが拒否されました。");
    Console.WriteLine($"          icacls \"{paksFolder}\" /grant *S-1-5-32-544:(OI)(CI)F /T");
    Console.ResetColor();
    Pause(); return 1;
}
catch (IOException ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[ERROR] {paksFolder} への書き込みに失敗しました。Epic Games Launcher と Fortnite を完全に終了してから再実行してください。");
    Console.WriteLine($"        詳細: {ex.Message}");
    Console.ResetColor();
    Pause(); return 1;
}

Console.WriteLine();
if (movedCount == 0)
{
    Warn("移動できるファイルがありませんでした。");
    Warn($"ダウンロードフォルダを確認してください: {mapOutDir}");
}
else
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"インストール完了！ {movedCount} ファイルを移動しました。");
    Console.WriteLine($"インストール先: {paksFolder}");
    Console.ResetColor();
}

// ── GameFeatures プラグインのエクスポート・配置 ───────────────────────────
var (gfModuleId, gfAesKeyHex, gfGuid) = GameFeaturePluginExporter.ReadModuleKeyJson(
    Path.Combine(mapOutDir, "module_key_v4.json"));

if (!string.IsNullOrEmpty(gfModuleId) && !string.IsNullOrEmpty(gfAesKeyHex))
{
    try
    {
        bool gfOk = GameFeaturePluginExporter.ExportPlugin(paksFolder, gfModuleId!, gfAesKeyHex!, gfGuid);
        if (gfOk)
        {
            // インストールしたフォルダ名を記録 (モード2での削除対象として使用)
            var installed = LoadInstalledPlugins(installedPluginsJson);
            if (!installed.Contains(gfModuleId!, StringComparer.OrdinalIgnoreCase))
            {
                installed.Add(gfModuleId!);
                SaveInstalledPlugins(installedPluginsJson, installed);
                Console.WriteLine($"[GF] フォルダ名を記録しました: {gfModuleId}");
            }
        }
    }
    catch (Exception ex)
    {
        Warn($"GameFeatures プラグインの配置に失敗しました: {ex.Message}");
    }
}
else
{
    Console.WriteLine("[GF] moduleId/aesKeyHex が取得できなかったため GameFeatures プラグインの配置をスキップします。");
}

// ── EditorPerProjectUserSettings.ini の ContentBrowserDrawer.SelectedPaths を更新 ──
if (!string.IsNullOrEmpty(gfModuleId))
{
    var editorIniPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UnrealEditorFortnite", "Saved", "Config", "WindowsEditor",
        "EditorPerProjectUserSettings.ini");

    UpdateContentBrowserSelectedPaths(editorIniPath, gfModuleId!);
}

// ── ゲーム起動 ────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── ゲーム起動 ──────────────────────────────────────");

// amfrt64.dll チェック
var amfrtDll  = win64Dir is not null ? Path.Combine(win64Dir, "amfrt64.dll") : null;
var gameExe   = win64Dir is not null ? Path.Combine(win64Dir, "UnrealEditorFortnite-Win64-Shipping.exe") : null;

if (amfrtDll is null || !File.Exists(amfrtDll))
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("[!!] amfrt64.dll が見つかりません。");
    Console.WriteLine("[!!] AES Loader のセットアップが必要です:");
    Console.WriteLine("[!!]   https://github.com/Aleman-sein-Vater/UEFN-AES-Loader");
    Console.ResetColor();
    Console.WriteLine();
    Console.Write("ブラウザで上記 URL を開きますか？ [Y/n]: ");
    var ans = Console.ReadLine()?.Trim();
    if (!string.Equals(ans, "n", StringComparison.OrdinalIgnoreCase))
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/Aleman-sein-Vater/UEFN-AES-Loader") { UseShellExecute = true }); }
        catch { Warn("ブラウザを開けませんでした。手動でアクセスしてください。"); }
    }
}
else if (gameExe is null || !File.Exists(gameExe))
{
    Warn($"UnrealEditorFortnite-Win64-Shipping.exe が見つかりません。手動で起動してください。");
}
else
{
    // -enableplugins= の引数に uplugin と同じ名前 (= gfModuleId) を渡す
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
        Console.WriteLine("[LAUNCH] 起動しました。");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Warn($"起動に失敗しました: {ex.Message}");
    }
}

Pause();
return 0;

// ── ヘルパー ──────────────────────────────────────────────────────────────

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
        Console.WriteLine($"[ERROR] {iniPath} への書き込みが拒否されました。管理者として実行してください。");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[ERROR] ini の更新に失敗しました: {ex.Message}");
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
    catch (Exception ex) { Warn($"installed_plugins.json の保存に失敗しました: {ex.Message}"); }
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
    Console.WriteLine("Enterキーを押すと終了します...");
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
    catch (Exception ex) { Warn($"シグネチャキャッシュの保存に失敗しました: {ex.Message}"); }
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
/// の [/Script/ContentBrowser.ContentBrowserSettings] セクション内にある
/// ContentBrowserDrawer.SelectedPaths= の値を /<moduleId> に書き換えます。
/// 行が存在しない場合はセクションの末尾に追加します。
/// ファイル自体が存在しない場合は何もせず警告のみ出します。
/// </summary>
static void UpdateContentBrowserSelectedPaths(string iniPath, string moduleId)
{
    // 書き込む値: /2b3d37a4-4c69-d552-f25d-818ee9d96a77
    var newValue = $"/{moduleId}";
    const string key = "ContentBrowserDrawer.SelectedPaths";

    if (!File.Exists(iniPath))
    {
        Console.WriteLine($"[CB] EditorPerProjectUserSettings.ini が見つかりません。スキップします: {iniPath}");
        return;
    }

    try
    {
        var lines   = File.ReadAllLines(iniPath);
        var updated = false;

        for (int i = 0; i < lines.Length; i++)
        {
            // キーが "ContentBrowserDrawer.SelectedPaths=" で始まる行を探す
            // (前後の空白を無視、大小文字を区別しない)
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                var existing = trimmed[(key.Length + 1)..]; // "=" の右側
                if (string.Equals(existing, newValue, StringComparison.Ordinal))
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[CB] ContentBrowserDrawer.SelectedPaths は既に正しい値です: {newValue}");
                    Console.ResetColor();
                    return;
                }

                // インデント (行頭スペース等) を保持して値だけ差し替える
                var indent = lines[i].Length - trimmed.Length;
                lines[i] = lines[i][..indent] + key + "=" + newValue;
                updated   = true;
                break;
            }
        }

        if (!updated)
        {
            // キーが存在しなかった場合: [/Script/ContentBrowser.ContentBrowserSettings]
            // セクションの直後に挿入。見つからなければファイル末尾に追加する。
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
        Console.WriteLine($"[CB] ContentBrowserDrawer.SelectedPaths を更新しました: {newValue}");
        Console.WriteLine($"[CB] ファイル: {iniPath}");
        Console.ResetColor();
    }
    catch (UnauthorizedAccessException)
    {
        Warn($"EditorPerProjectUserSettings.ini への書き込みが拒否されました: {iniPath}");
    }
    catch (Exception ex)
    {
        Warn($"EditorPerProjectUserSettings.ini の更新に失敗しました: {ex.Message}");
    }
}
