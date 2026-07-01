using System.Text.Json;
using System.Text.RegularExpressions;

namespace UEFNMapInstaller;

/// <summary>
/// Standalone command to generate/update DecrypterSettings.ini (Binaries\\Win64).
/// Only [Settings] Signature and [ContentKeys] Key0 are modified;
/// all other existing keys and comments are preserved.
///   - Key0      : Fetched key chain (GUID(no-dash, uppercase):base64)
///   - Signature : Byte pattern extracted by uefn_downloader.py find-signature
/// </summary>
internal static class DecrypterSettingsCommand
{
    private const string IniFileName = "DecrypterSettings.ini";

    // Initial template matching the example ini (used only when creating a new file)
    private const string IniTemplate =
        "[Settings]\r\n" +
        "ModuleName=unrealeditorfortnite-engine-win64-shipping.dll\r\n" +
        "Signature=??\r\n" +
        "Timeout=10000\r\n" +
        "\r\n" +
        "[ContentKeys]\r\n" +
        "Key0=\r\n";

    // GUID(32-char hex, no dashes) : base64
    private static readonly Regex KeychainPattern =
        new(@"^[0-9A-Fa-f]{32}:[A-Za-z0-9+/]+={0,2}$", RegexOptions.Compiled);

    public static int Run(string exeDir)
    {
        Console.WriteLine();
        Console.WriteLine("== Updating DecrypterSettings.ini ==");

        // -- 1. Locate target exe / Binaries\\Win64 / ini path --
        var targetExe = FortnitePathLocator.FindUnrealEditorExe();
        if (targetExe is null)
        {
            Error($"{FortnitePathLocator.UnrealEditorExeName} was not found.");
            Error("Please verify that UEFN (Fortnite) is installed.");
            Pause();
            return 1;
        }

        var win64Dir = Path.GetDirectoryName(targetExe)!;
        var iniPath = Path.Combine(win64Dir, IniFileName);
        Console.WriteLine($"[INI] Output: {iniPath}");

        // -- 2. Key chain (Key0) --
        string? keychain = ResolveKeychain(exeDir);

        // ── 3. Signature ─────────────────────────────────────────────────
        var (signature, functionRva) = ResolveSignature(exeDir, win64Dir);

        if (keychain is null && signature is null)
        {
            Warn("Neither Key0 nor Signature has an update target. Exiting.");
            Pause();
            return 1;
        }

        // -- 4. Update ini (matching keys only) --
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
            Error($"{iniPath}: write access denied.");
            Error("Please run as administrator or check write permissions.");
            Pause();
            return 1;
        }
        catch (Exception ex)
        {
            Error($"Failed to update ini: {ex.Message}");
            Pause();
            return 1;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"DecrypterSettings.ini updated: {iniPath}");
        Console.ResetColor();
        Pause();
        return 0;
    }

    // -- Fetch key chain --

    private static string? ResolveKeychain(string exeDir)
    {
        Console.WriteLine();
        Console.Write("Enter map code or key chain (GUID:base64) for Key0\n  (press Enter to skip Key0): ");
        var input = Console.ReadLine()?.Trim() ?? "";
        if (input.Length == 0)
            return null;

        // Support pasting in \"KeyChain: ...\" format
        if (input.StartsWith("KeyChain:", StringComparison.OrdinalIgnoreCase))
            input = input["KeyChain:".Length..].Trim();

        // If a raw key chain was pasted
        if (KeychainPattern.IsMatch(input))
            return input;

        // Otherwise treat as a map code and run resolve-v2 to fetch
        string mapCode;
        try
        {
            mapCode = UefnDownloaderService.NormalizeMapCode(input);
        }
        catch
        {
            Warn("Input is neither a map code (12 digits) nor a key chain (GUID:base64). Skipping Key0.");
            return null;
        }

        return FetchKeychainByMapCode(exeDir, mapCode);
    }

    private static string? FetchKeychainByMapCode(string exeDir, string mapCode)
    {
        var scriptPath = Path.Combine(exeDir, "uefn_downloader.py");
        if (!File.Exists(scriptPath))
        {
            Warn($"uefn_downloader.py not found; skipping key chain fetch: {scriptPath}");
            return null;
        }

        var quietEnv = new Dictionary<string, string> { ["UEFN_QUIET"] = "1" };
        var dataDir = Path.Combine(exeDir, "Data");
        var authDir = Path.Combine(dataDir, "auth");
        Directory.CreateDirectory(authDir);

        // Device login on first run only
        var deviceAuthPath = Path.Combine(authDir, "device_auth.json");
        if (!File.Exists(deviceAuthPath))
        {
            int loginCode = PythonRunner.Run(scriptPath,
                ["device-login", "--data-dir", authDir], exeDir, quietEnv);
            if (loginCode != 0)
            {
                Warn($"Login failed (exit code {loginCode}). Skipping Key0.");
                return null;
            }
        }

        // Fetch AES key (module_key_v4.json)
        int aesCode = PythonRunner.Run(scriptPath,
            ["resolve-v2", mapCode, "--data-dir", authDir, "--out", dataDir],
            exeDir, quietEnv);
        if (aesCode != 0)
        {
            Warn("Failed to fetch AES key (unencrypted map etc.). Skipping Key0.");
            return null;
        }

        var keyJson = Path.Combine(dataDir, mapCode, "module_key_v4.json");
        if (!File.Exists(keyJson))
        {
            Warn($"module_key_v4.json not found: {keyJson}. Skipping Key0.");
            return null;
        }

        try
        {
            return BuildKeychainFromModuleKeyJson(keyJson);
        }
        catch (Exception ex)
        {
            Warn($"Failed to build key chain: {ex.Message}. Skipping Key0.");
            return null;
        }
    }

    /// <summary>
    /// Builds Key0 format (\"GUID(no-dash, uppercase):base64\") from module_key_v4.json.
    /// </summary>
    private static string BuildKeychainFromModuleKeyJson(string jsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var root = doc.RootElement;

        var guid = root.TryGetProperty("guid", out var g) ? g.GetString() : null;
        if (string.IsNullOrEmpty(guid))
            throw new InvalidDataException("Missing guid field");

        // base64 key: prefer raw.key.Key, otherwise derive from aesKeyHex
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
                throw new InvalidDataException("Neither aesKeyHex nor raw.key.Key is present");
            base64 = Convert.ToBase64String(HexToBytes(hex!));
        }

        var guidNoDash = guid!.Replace("-", "").ToUpperInvariant();
        return $"{guidNoDash}:{base64}";
    }

    private static byte[] HexToBytes(string hex)
    {
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        if (hex.Length % 2 != 0) throw new FormatException("Hex string has invalid length");
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    // -- Fetch Signature --

    private const string SignatureCacheFileName = "signature_cache.json";

    private static (string? Signature, string? FunctionRva) ResolveSignature(string exeDir, string win64Dir)
    {
        Console.WriteLine();
        Console.Write("Auto-fetch Signature? [Y/n]: ");
        var ans = Console.ReadLine()?.Trim();
        if (string.Equals(ans, "n", StringComparison.OrdinalIgnoreCase))
            return (null, null);

        var scriptPath = Path.Combine(exeDir, "uefn_downloader.py");
        if (!File.Exists(scriptPath))
        {
            Warn($"uefn_downloader.py not found: {scriptPath}. Skipping Signature.");
            return (null, null);
        }

        // Look for DLL in Win64 folder
        var engineDll = Path.Combine(win64Dir, "UnrealEditorFortnite-Engine-Win64-Shipping.dll");
        if (!File.Exists(engineDll))
        {
            Warn($"DLL not found: {engineDll}. Skipping Signature.");
            return (null, null);
        }

        var dataDir = Path.Combine(exeDir, "Data");
        Directory.CreateDirectory(dataDir);
        var cachePath = Path.Combine(dataDir, SignatureCacheFileName);
        var cache = LoadSignatureCache(cachePath);

        // Fetch current game version (e.g. \"41.10\"). Scan continues even on failure
        // (only version comparison is skipped; not a fatal error).
        string? currentVersion = FetchGameVersion(scriptPath, exeDir);
        if (currentVersion is not null)
            Console.WriteLine($"[SIG] Current game version: {currentVersion}");
        else
            Warn("Failed to get game version. Skipping version comparison and re-scanning DLL.");

        // If cache version matches, skip DLL re-scan and
        // reuse the saved Signature / FunctionRVA.
        if (currentVersion is not null
            && cache is not null
            && string.Equals(cache.GameVersion, currentVersion, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(cache.Signature))
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[SIG] Game version unchanged ({currentVersion}) -> Reusing cached Signature");
            Console.WriteLine($"[SIG] Signature (cache): {cache.Signature}");
            Console.ResetColor();
            return (cache.Signature, cache.FunctionRva);
        }

        // Stream stderr to console in real time while capturing stdout
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
            Console.WriteLine($"[SIG] Signature retrieved: {sig}");
            Console.ResetColor();

            // Save to cache with version for skip decision on next run.
            // Do not write cache if version fetch failed
            // (force re-scan next time to stay safe).
            if (currentVersion is not null)
            {
                SaveSignatureCache(cachePath, new SignatureCache
                {
                    GameVersion  = currentVersion,
                    Signature    = sig,
                    FunctionRva  = functionRva,
                    UpdatedAtUtc = DateTime.UtcNow.ToString("O"),
                });
                Console.WriteLine($"[SIG] Cache updated: {cachePath}");
            }

            return (sig, functionRva);
        }

        Warn("Failed to retrieve Signature. Please set Signature in DecrypterSettings.ini manually.");
        return (null, null);
    }

    /// <summary>
    /// Runs uefn_downloader.py game-version and returns a
    /// major.minor string such as \"41.10\". Returns null on failure.
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
            // Ignore corrupted cache and force re-scan
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
            Warn($"Failed to save signature cache: {ex.Message}");
        }
    }

    // -- UI helpers --

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
        Console.WriteLine("Press Enter to exit...");
        Console.ReadLine();
    }
}
