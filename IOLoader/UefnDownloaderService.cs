using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UEFNMapInstaller;

/// <summary>
/// マップコードからAESキーを取得し、
/// plugin.ucas / plugin.utoc / plugin.sig / plugin.pak を
/// 指定ディレクトリにダウンロードします。
/// </summary>
internal sealed class UefnDownloaderService
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _dataDir;

    public UefnDownloaderService(string dataDir) => _dataDir = dataDir;

    // ── マップコード正規化 ─────────────────────────────────────────

    public static string NormalizeMapCode(string raw)
    {
        var digits = Regex.Replace(raw, @"\D", "");
        if (digits.Length != 12)
            throw new ArgumentException("マップコードは12桁の数字でなければなりません");
        return $"{digits[..4]}-{digits[4..8]}-{digits[8..]}";
    }

    // ── AES キー取得 ──────────────────────────────────────────────

    /// <returns>(aesKeyHex "0xABC…", guid "XXXXXXXX-…") のタプル。非暗号化マップは null。</returns>
    public async Task<(string AesKeyHex, string Guid)?> FetchAesKeyAsync(
        string mapCode,
        string token,
        string outputDir,
        CancellationToken ct = default)
    {
        Console.WriteLine($"[AES] v2 パッケージを解決中...");

        // 最新バージョントリプレット取得
        var (major, minor, patch) = await GetLatestDillyVersionAsync(ct);
        Console.WriteLine($"[AES] Fortnite バージョン: {major}.{minor}-CL-{patch}");

        // v2 パッケージ問い合わせ
        var v2Url = $"{EpicEndpoints.ContentServiceBase}/api/content/v2/link/{Uri.EscapeDataString(mapCode)}/cooked-content-package"
                  + $"?role=client&platform=windows&major={major}&minor={minor}&patch={patch}";

        V2PackageResponse v2Data;
        try
        {
            v2Data = await EpicHttp.GetJsonAsync<V2PackageResponse>(v2Url, token, ct);
        }
        catch (EpicApiException ex)
        {
            if (ex.Message.Contains("unexpected_link_type"))
            {
                Console.WriteLine("[AES] このマップは暗号化なし (1.0マップ) です");
                return null;
            }
            Console.WriteLine($"[AES] v2 パッケージ取得に失敗しました: {ex.Message}");
            return null;
        }

        if (!v2Data.IsEncrypted)
        {
            Console.WriteLine("[AES] このマップは暗号化されていません");
            return null;
        }

        var moduleId      = v2Data.Resolved?.Root?.ModuleId;
        var moduleVersion = v2Data.Resolved?.Root?.Version;

        if (string.IsNullOrEmpty(moduleId) || moduleVersion is null)
        {
            Console.WriteLine("[AES] moduleId/moduleVersion が取得できませんでした");
            return null;
        }

        Console.WriteLine($"[AES] moduleId={moduleId}, moduleVersion={moduleVersion}");

        // module key batch API
        var payload = new[]
        {
            new { moduleId = moduleId, version = moduleVersion },
        };
        var keyList = await EpicHttp.PostJsonAsync<List<ModuleKeyItem>>(
            EpicEndpoints.ModuleKeyBatchUrl, payload, token, ct);

        var keyData = keyList?.FirstOrDefault()?.Key;
        if (keyData is null || string.IsNullOrEmpty(keyData.Key))
            throw new EpicApiException("AES キーのバッチレスポンスが空です");

        var aesKeyHex = Convert.FromBase64String(keyData.Key).ToHexString().ToUpperInvariant();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[AES] AES キー取得成功: 0x{aesKeyHex}");
        Console.WriteLine($"[AES] GUID: {keyData.Guid}");
        Console.ResetColor();

        // AES キーをファイルに保存
        Directory.CreateDirectory(outputDir);
        await File.WriteAllTextAsync(Path.Combine(outputDir, "aes_key.txt"),
            $"AESKey=0x{aesKeyHex}\nGUID={keyData.Guid}\n", ct);

        return ($"0x{aesKeyHex}", keyData.Guid ?? "");
    }

    // ── コンテンツダウンロード (BPS chunk方式) ────────────────────

    public async Task<List<string>> DownloadMapFilesAsync(
        string mapCode,
        string token,
        string outputDir,
        CancellationToken ct = default)
    {
        Console.WriteLine($"[DL] v4 パッケージを解決中...");

        // mnemonic でバージョン取得
        var mnemonicUrl = EpicEndpoints.DefaultMnemonicApi
            .Replace("{namespace}", "fn")
            .Replace("{map_code}", mapCode);

        MnemonicResponse mnemonic;
        try
        {
            // mnemonic APIはリスト or オブジェクトを返す可能性あり
            var raw = await EpicHttp.GetJsonAsync<JsonElement>(mnemonicUrl, null, ct);
            mnemonic = raw.ValueKind == JsonValueKind.Array
                ? raw.EnumerateArray().First().Deserialize<MnemonicResponse>(_jsonOpts)!
                : raw.Deserialize<MnemonicResponse>(_jsonOpts)!;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DL] mnemonic 取得エラー (続行): {ex.Message}");
            mnemonic = new MnemonicResponse();
        }

        // v4 cooked-content-package
        var linkVersion = mnemonic.Version?.ToString();
        var v4Url = BuildV4PackageUrl(mapCode, linkVersion);
        Console.WriteLine("[DL] v4 パッケージURL: " + v4Url);

        var v4Resp = await EpicHttp.GetJsonAsync<V4PackageResponse>(v4Url, token, ct);
        if (v4Resp.Content is null || v4Resp.Content.Count == 0)
            throw new EpicApiException($"v4 パッケージにコンテンツがありません (status={v4Resp.Status})");

        // 最大サイズのアイテムを選択
        var selected = v4Resp.Content
            .Where(i => !string.IsNullOrEmpty(i.Binaries?.BaseUrl))
            .MaxBy(i => i.Binaries?.TotalSizeKb ?? 0)
            ?? throw new EpicApiException("ダウンロード可能な binaries が見つかりません");

        var baseUrl = selected.Binaries!.BaseUrl!;
        Console.WriteLine($"[DL] baseUrl={baseUrl}");

        Directory.CreateDirectory(outputDir);

        // plugin.manifest をダウンロード
        var manifestUrl  = baseUrl.TrimEnd('/') + "/alt/plugin.manifest";
        var manifestPath = Path.Combine(outputDir, "plugin.manifest");
        Console.WriteLine("[DL] plugin.manifest をダウンロード中...");
        await EpicHttp.DownloadFileAsync(manifestUrl, manifestPath, null, null, ct);
        Console.WriteLine("[DL] plugin.manifest ダウンロード完了");

        // BPS マニフェストを解析してファイルを再構築
        Console.WriteLine("[DL] BPS チャンクからファイルを復元中...");
        var written = await BpsDownloader.ReconstructFilesAsync(
            manifestPath, baseUrl, outputDir, ct: ct);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[DL] {written.Count} ファイルをダウンロードしました:");
        foreach (var f in written)
            Console.WriteLine($"  {Path.GetFileName(f)}");
        Console.ResetColor();

        return written;
    }

    // ── ヘルパー ─────────────────────────────────────────────────

    private static string BuildV4PackageUrl(string mapCode, string? version)
    {
        var q = "platform=Windows&role=client";
        if (version is not null) q += $"&version={Uri.EscapeDataString(version)}";
        return $"{EpicEndpoints.ContentServiceBase}/api/content/v4/link/{Uri.EscapeDataString(mapCode)}/cooked-content-package?{q}";
    }

    private static async Task<(string major, string minor, string patch)> GetLatestDillyVersionAsync(CancellationToken ct)
    {
        var data = await EpicHttp.GetJsonAsync<DillyMappings>(EpicEndpoints.DillyMappingsUrl, null, ct);
        if (string.IsNullOrEmpty(data.Version))
            throw new EpicApiException("Dilly mappings にバージョン情報がありません");

        var m = Regex.Match(data.Version, @"Release-(\d+)\.(\d+)-CL-(\d+)");
        if (!m.Success)
            throw new EpicApiException($"バージョン文字列を解析できませんでした: {data.Version}");

        return (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
    }
}

// ── byte[] 拡張 ──────────────────────────────────────────────────────

internal static class ByteExtensions
{
    public static string ToHexString(this byte[] bytes) =>
        BitConverter.ToString(bytes).Replace("-", "");
}
