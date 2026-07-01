using System.Text.Json;
using System.Text.RegularExpressions;

namespace UEFNMapInstaller;

/// <summary>
/// DecrypterSettings.ini (Binaries\Win64) を生成・更新する独立コマンド。
/// 触るのは [Settings] Signature と [ContentKeys] Key0 の2か所のみで、
/// それ以外の既存キー・コメントはそのまま保持します。
///   - Key0      : 取得したキーチェーン (GUID(ダッシュ無し大文字):base64)
///   - Signature : uefn_downloader.py find-signature で抽出したバイトパターン
/// </summary>
internal static class DecrypterSettingsCommand
{
    private const string IniFileName = "DecrypterSettings.ini";

    // 例の ini に合わせた初期テンプレート (新規作成時のみ使用)
    private const string IniTemplate =
        "[Settings]\r\n" +
        "ModuleName=unrealeditorfortnite-engine-win64-shipping.dll\r\n" +
        "Signature=??\r\n" +
        "Timeout=10000\r\n" +
        "\r\n" +
        "[ContentKeys]\r\n" +
        "Key0=\r\n";

    // GUID(32桁hex,ダッシュ無し) : base64
    private static readonly Regex KeychainPattern =
        new(@"^[0-9A-Fa-f]{32}:[A-Za-z0-9+/]+={0,2}$", RegexOptions.Compiled);

    public static int Run(string exeDir)
    {
        Console.WriteLine();
        Console.WriteLine("== DecrypterSettings.ini 更新 ==");

        // ── 1. 対象 exe / Binaries\Win64 / ini パスを特定 ────────────────
        var targetExe = FortnitePathLocator.FindUnrealEditorExe();
        if (targetExe is null)
        {
            Error($"{FortnitePathLocator.UnrealEditorExeName} が見つかりませんでした。");
            Error("UEFN (Fortnite) がインストールされているか確認してください。");
            Pause();
            return 1;
        }

        var win64Dir = Path.GetDirectoryName(targetExe)!;
        var iniPath = Path.Combine(win64Dir, IniFileName);
        Console.WriteLine($"[INI] 出力先 : {iniPath}");

        // ── 2. キーチェーン (Key0) ───────────────────────────────────────
        string? keychain = ResolveKeychain(exeDir);

        // ── 3. Signature ─────────────────────────────────────────────────
        var (signature, functionRva) = ResolveSignature(exeDir, win64Dir);

        if (keychain is null && signature is null)
        {
            Warn("Key0 / Signature いずれも更新対象がありません。処理を終了します。");
            Pause();
            return 1;
        }

        // ── 4. ini を更新 (該当キーのみ) ────────────────────────────────
        try
        {
            if (keychain is not null)
            {
                IniEditor.SetValue(iniPath, "ContentKeys", "Key0", keychain, IniTemplate);
                Console.WriteLine($"[INI] Key0      = {keychain}");
            }
            if (signature is not null)
            {
                IniEditor.SetValue(iniPath, "Settings", "Signature", signature, IniTemplate);
                Console.WriteLine($"[INI] Signature = {signature}");
            }
        }
        catch (UnauthorizedAccessException)
        {
            Error($"{iniPath} への書き込みが拒否されました。");
            Error("管理者として実行するか、書き込み権限を確認してください。");
            Pause();
            return 1;
        }
        catch (Exception ex)
        {
            Error($"ini の更新に失敗しました: {ex.Message}");
            Pause();
            return 1;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"DecrypterSettings.ini を更新しました: {iniPath}");
        Console.ResetColor();
        Pause();
        return 0;
    }

    // ── キーチェーン取得 ────────────────────────────────────────────────

    private static string? ResolveKeychain(string exeDir)
    {
        Console.WriteLine();
        Console.Write("Key0 用のマップコード または キーチェーン(GUID:base64) を入力\n  (Key0 を更新しない場合は空 Enter): ");
        var input = Console.ReadLine()?.Trim() ?? "";
        if (input.Length == 0)
            return null;

        // "KeyChain: ..." 形式の貼り付けに対応
        if (input.StartsWith("KeyChain:", StringComparison.OrdinalIgnoreCase))
            input = input["KeyChain:".Length..].Trim();

        // 直接キーチェーンが貼られた場合
        if (KeychainPattern.IsMatch(input))
            return input;

        // それ以外はマップコードとして resolve-v2 を実行して取得
        string mapCode;
        try
        {
            mapCode = UefnDownloaderService.NormalizeMapCode(input);
        }
        catch
        {
            Warn("入力がマップコード(12桁)でもキーチェーン(GUID:base64)でもありません。Key0 はスキップします。");
            return null;
        }

        return FetchKeychainByMapCode(exeDir, mapCode);
    }

    private static string? FetchKeychainByMapCode(string exeDir, string mapCode)
    {
        var scriptPath = Path.Combine(exeDir, "uefn_downloader.py");
        if (!File.Exists(scriptPath))
        {
            Warn($"uefn_downloader.py が見つからないためキーチェーン取得をスキップします: {scriptPath}");
            return null;
        }

        var quietEnv = new Dictionary<string, string> { ["UEFN_QUIET"] = "1" };
        var dataDir = Path.Combine(exeDir, "Data");
        var authDir = Path.Combine(dataDir, "auth");
        Directory.CreateDirectory(authDir);

        // 初回のみデバイスログイン
        var deviceAuthPath = Path.Combine(authDir, "device_auth.json");
        if (!File.Exists(deviceAuthPath))
        {
            int loginCode = PythonRunner.Run(scriptPath,
                ["device-login", "--data-dir", authDir], exeDir, quietEnv);
            if (loginCode != 0)
            {
                Warn($"ログインに失敗しました (終了コード {loginCode})。Key0 はスキップします。");
                return null;
            }
        }

        // AES キー (module_key_v4.json) を取得
        int aesCode = PythonRunner.Run(scriptPath,
            ["resolve-v2", mapCode, "--data-dir", authDir, "--out", dataDir],
            exeDir, quietEnv);
        if (aesCode != 0)
        {
            Warn("AES キーの取得に失敗しました (非暗号化マップ等)。Key0 はスキップします。");
            return null;
        }

        var keyJson = Path.Combine(dataDir, mapCode, "module_key_v4.json");
        if (!File.Exists(keyJson))
        {
            Warn($"module_key_v4.json が見つかりません: {keyJson}。Key0 はスキップします。");
            return null;
        }

        try
        {
            return BuildKeychainFromModuleKeyJson(keyJson);
        }
        catch (Exception ex)
        {
            Warn($"キーチェーンの組み立てに失敗しました: {ex.Message}。Key0 はスキップします。");
            return null;
        }
    }

    /// <summary>
    /// module_key_v4.json から Key0 形式 ("GUID(ダッシュ無し大文字):base64") を組み立てます。
    /// </summary>
    private static string BuildKeychainFromModuleKeyJson(string jsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var root = doc.RootElement;

        var guid = root.TryGetProperty("guid", out var g) ? g.GetString() : null;
        if (string.IsNullOrEmpty(guid))
            throw new InvalidDataException("guid フィールドがありません");

        // base64 鍵: raw.key.Key を優先、無ければ aesKeyHex から再計算
        string? base64 = null;
        if (root.TryGetProperty("raw", out var raw)
            && raw.TryGetProperty("key", out var keyObj)
            && keyObj.TryGetProperty("Key", out var keyVal))
        {
            base64 = keyVal.GetString();
        }
        if (string.IsNullOrEmpty(base64))
        {
            var hex = root.TryGetProperty("aesKeyHex", out var h) ? h.GetString() : null;
            if (string.IsNullOrEmpty(hex))
                throw new InvalidDataException("aesKeyHex / raw.key.Key のいずれもありません");
            base64 = Convert.ToBase64String(HexToBytes(hex!));
        }

        var guidNoDash = guid!.Replace("-", "").ToUpperInvariant();
        return $"{guidNoDash}:{base64}";
    }

    private static byte[] HexToBytes(string hex)
    {
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        if (hex.Length % 2 != 0) throw new FormatException("16進文字列の長さが不正です");
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    // ── Signature 取得 ──────────────────────────────────────────────────

    private const string SignatureCacheFileName = "signature_cache.json";

    private static (string? Signature, string? FunctionRva) ResolveSignature(string exeDir, string win64Dir)
    {
        Console.WriteLine();
        Console.Write("Signature を自動取得しますか? [Y/n]: ");
        var ans = Console.ReadLine()?.Trim();
        if (string.Equals(ans, "n", StringComparison.OrdinalIgnoreCase))
            return (null, null);

        var scriptPath = Path.Combine(exeDir, "uefn_downloader.py");
        if (!File.Exists(scriptPath))
        {
            Warn($"uefn_downloader.py が見つかりません: {scriptPath}。Signature はスキップします。");
            return (null, null);
        }

        // DLL を Win64 フォルダから探す
        var engineDll = Path.Combine(win64Dir, "UnrealEditorFortnite-Engine-Win64-Shipping.dll");
        if (!File.Exists(engineDll))
        {
            Warn($"DLL が見つかりません: {engineDll}。Signature はスキップします。");
            return (null, null);
        }

        var dataDir = Path.Combine(exeDir, "Data");
        Directory.CreateDirectory(dataDir);
        var cachePath = Path.Combine(dataDir, SignatureCacheFileName);
        var cache = LoadSignatureCache(cachePath);

        // 現在のゲームバージョン (例: "41.10") を取得。失敗してもスキャン自体は続行する
        // (バージョン比較ができないだけで、致命的なエラーにはしない)。
        string? currentVersion = FetchGameVersion(scriptPath, exeDir);
        if (currentVersion is not null)
            Console.WriteLine($"[SIG] 現在のゲームバージョン: {currentVersion}");
        else
            Warn("ゲームバージョンの取得に失敗しました。バージョン比較をスキップしてDLLを再スキャンします。");

        // キャッシュのバージョンと一致していれば DLL の再スキャンを省略し、
        // 保存済みの Signature / FunctionRVA をそのまま再利用する。
        if (currentVersion is not null
            && cache is not null
            && string.Equals(cache.GameVersion, currentVersion, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(cache.Signature))
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[SIG] ゲームバージョンに変更なし ({currentVersion}) → キャッシュ済み Signature を再利用します");
            Console.WriteLine($"[SIG] Signature (cache): {cache.Signature}");
            Console.ResetColor();
            return (cache.Signature, cache.FunctionRva);
        }

        // stderr をリアルタイムでコンソールに流しながら stdout をキャプチャ
        var (code, stdout) = PythonRunner.RunCaptureWithLiveStderr(
            scriptPath, ["find-signature", engineDll], exeDir,
            stderrColor: ConsoleColor.Cyan);

        string? sig = null;
        string? functionRva = null;
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Signature=", StringComparison.OrdinalIgnoreCase))
                sig = line["Signature=".Length..].Trim();
            else if (line.StartsWith("FunctionRVA=", StringComparison.OrdinalIgnoreCase))
                functionRva = line["FunctionRVA=".Length..].Trim();
        }

        if (code == 0 && !string.IsNullOrEmpty(sig))
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[SIG] Signature 取得成功: {sig}");
            Console.ResetColor();

            // 次回以降のスキップ判定用にバージョンと一緒にキャッシュへ保存。
            // バージョン取得に失敗していた場合はキャッシュを書かない
            // (次回必ず再スキャンさせて安全側に倒す)。
            if (currentVersion is not null)
            {
                SaveSignatureCache(cachePath, new SignatureCache
                {
                    GameVersion  = currentVersion,
                    Signature    = sig,
                    FunctionRva  = functionRva,
                    UpdatedAtUtc = DateTime.UtcNow.ToString("O"),
                });
                Console.WriteLine($"[SIG] キャッシュを更新しました: {cachePath}");
            }

            return (sig, functionRva);
        }

        Warn("Signature の取得に失敗しました。手動で DecrypterSettings.ini の Signature を設定してください。");
        return (null, null);
    }

    /// <summary>
    /// uefn_downloader.py game-version を実行し、"41.10" のような
    /// major.minor 文字列を取得します。失敗時は null を返します。
    /// </summary>
    private static string? FetchGameVersion(string scriptPath, string exeDir)
    {
        try
        {
            var (code, stdout, _) = PythonRunner.RunCapture(scriptPath, ["game-version"], exeDir);
            if (code != 0)
                return null;

            var version = stdout.Trim();
            return version.Length > 0 ? version : null;
        }
        catch
        {
            return null;
        }
    }

    private static SignatureCache? LoadSignatureCache(string cachePath)
    {
        if (!File.Exists(cachePath))
            return null;
        try
        {
            return JsonSerializer.Deserialize<SignatureCache>(File.ReadAllText(cachePath));
        }
        catch
        {
            // 壊れたキャッシュは無視して再スキャンさせる
            return null;
        }
    }

    private static void SaveSignatureCache(string cachePath, SignatureCache cache)
    {
        try
        {
            var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(cachePath, json);
        }
        catch (Exception ex)
        {
            Warn($"シグネチャキャッシュの保存に失敗しました: {ex.Message}");
        }
    }

    // ── UI ヘルパー ──────────────────────────────────────────────────────

    private static void Warn(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[!!] {msg}");
        Console.ResetColor();
    }

    private static void Error(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[ERROR] {msg}");
        Console.ResetColor();
    }

    private static void Pause()
    {
        Console.WriteLine();
        Console.WriteLine("Enterキーを押すと終了します...");
        Console.ReadLine();
    }
}
