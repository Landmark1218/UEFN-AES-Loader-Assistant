using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UEFNMapInstaller;

/// <summary>
/// Fetches the AES key from the map code,
/// and downloads plugin.ucas / plugin.utoc / plugin.sig / plugin.pak
/// to the specified directory.
/// </summary>
internal sealed class UefnDownloaderService
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _dataDir;

    public UefnDownloaderService(string dataDir) => _dataDir = dataDir;

    // -- Normalize map code --

    public static string NormalizeMapCode(string raw)
    {
        var digits = Regex.Replace(raw, @"\D", "");
        if (digits.Length != 12)
            throw new ArgumentException("Map code must be a 12-digit number");
        return $"{digits[..4]}-{digits[4..8]}-{digits[8..]}";
    }

    // -- Fetch AES key --

    /// <returns>Tuple of (aesKeyHex \"0xABC...\", guid \"XXXXXXXX-...\"). Returns null for unencrypted maps.</returns>
    public async Task<(string AesKeyHex, string Guid)?> FetchAesKeyAsync(
        string mapCode,
        string token,
        string outputDir,
        CancellationToken ct = default)
    {
        Console.WriteLine($"[AES] Resolving v2 package...");

        // Fetch latest version triplet
        var (major, minor, patch) = await GetLatestDillyVersionAsync(ct);
        Console.WriteLine($"[AES] Fortnite version: {major}.{minor}-CL-{patch}");

        // Query v2 package
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
                Console.WriteLine("[AES] This map has no encryption (v1.0 map)");
                return null;
            }
            Console.WriteLine($"[AES] Failed to fetch v2 package: {ex.Message}");
            return null;
        }

        if (!v2Data.IsEncrypted)
        {
            Console.WriteLine("[AES] This map is not encrypted");
            return null;
        }

        var moduleId      = v2Data.Resolved?.Root?.ModuleId;
        var moduleVersion = v2Data.Resolved?.Root?.Version;

        if (string.IsNullOrEmpty(moduleId) || moduleVersion is null)
        {
            Console.WriteLine("[AES] Could not obtain moduleId/moduleVersion");
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
            throw new EpicApiException("AES key batch response is empty");

        var aesKeyHex = Convert.FromBase64String(keyData.Key).ToHexString().ToUpperInvariant();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[AES] AES key retrieved: 0x{aesKeyHex}");
        Console.WriteLine($"[AES] GUID: {keyData.Guid}");
        Console.ResetColor();

        // Save AES key to file
        Directory.CreateDirectory(outputDir);
        await File.WriteAllTextAsync(Path.Combine(outputDir, "aes_key.txt"),
            $"AESKey=0x{aesKeyHex}\nGUID={keyData.Guid}\n", ct);

        return ($"0x{aesKeyHex}", keyData.Guid ?? "");
    }

    // -- Download content (BPS chunk method) --

    public async Task<List<string>> DownloadMapFilesAsync(
        string mapCode,
        string token,
        string outputDir,
        CancellationToken ct = default)
    {
        Console.WriteLine($"[DL] Resolving v4 package...");

        // Fetch version via mnemonic
        var mnemonicUrl = EpicEndpoints.DefaultMnemonicApi
            .Replace("{namespace}", "fn")
            .Replace("{map_code}", mapCode);

        MnemonicResponse mnemonic;
        try
        {
            // mnemonic API may return a list or an object
            var raw = await EpicHttp.GetJsonAsync<JsonElement>(mnemonicUrl, null, ct);
            mnemonic = raw.ValueKind == JsonValueKind.Array
                ? raw.EnumerateArray().First().Deserialize<MnemonicResponse>(_jsonOpts)!
                : raw.Deserialize<MnemonicResponse>(_jsonOpts)!;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DL] mnemonic fetch error (continuing): {ex.Message}");
            mnemonic = new MnemonicResponse();
        }

        // v4 cooked-content-package
        var linkVersion = mnemonic.Version?.ToString();
        var v4Url = BuildV4PackageUrl(mapCode, linkVersion);
        Console.WriteLine("[DL] v4 package URL: " + v4Url);

        var v4Resp = await EpicHttp.GetJsonAsync<V4PackageResponse>(v4Url, token, ct);
        if (v4Resp.Content is null || v4Resp.Content.Count == 0)
            throw new EpicApiException($"v4 package has no content (status={v4Resp.Status})");

        // Select the largest item
        var selected = v4Resp.Content
            .Where(i => !string.IsNullOrEmpty(i.Binaries?.BaseUrl))
            .MaxBy(i => i.Binaries?.TotalSizeKb ?? 0)
            ?? throw new EpicApiException("No downloadable binaries found");

        var baseUrl = selected.Binaries!.BaseUrl!;
        Console.WriteLine($"[DL] baseUrl={baseUrl}");

        Directory.CreateDirectory(outputDir);

        // Download plugin.manifest
        var manifestUrl  = baseUrl.TrimEnd('/') + "/alt/plugin.manifest";
        var manifestPath = Path.Combine(outputDir, "plugin.manifest");
        Console.WriteLine("[DL] Downloading plugin.manifest...");
        await EpicHttp.DownloadFileAsync(manifestUrl, manifestPath, null, null, ct);
        Console.WriteLine("[DL] plugin.manifest download complete");

        // Parse BPS manifest and reconstruct files
        Console.WriteLine("[DL] Reconstructing files from BPS chunks...");
        var written = await BpsDownloader.ReconstructFilesAsync(
            manifestPath, baseUrl, outputDir, ct: ct);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[DL] {written.Count} file(s) downloaded:");
        foreach (var f in written)
            Console.WriteLine($"  {Path.GetFileName(f)}");
        Console.ResetColor();

        return written;
    }

    // -- Helpers --

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
            throw new EpicApiException("Dilly mappings contain no version info");

        var m = Regex.Match(data.Version, @"Release-(\d+)\.(\d+)-CL-(\d+)");
        if (!m.Success)
            throw new EpicApiException($"Failed to parse version string: {data.Version}");

        return (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
    }
}

// -- byte[] extensions --

internal static class ByteExtensions
{
    public static string ToHexString(this byte[] bytes) =>
        BitConverter.ToString(bytes).Replace("-", "");
}
