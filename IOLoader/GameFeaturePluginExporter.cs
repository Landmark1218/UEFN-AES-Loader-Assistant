using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;

namespace UEFNMapInstaller;

/// <summary>
/// Creates a folder named after moduleId inside the game directory's Plugins/GameFeatures,
/// as specified in module_key_v4.json,
/// then searches for and exports moduleId + ".uplugin" using CUE4Parse,
/// sets \"EnabledByDefault\" to true, and saves the file.
/// </summary>
internal static class GameFeaturePluginExporter
{
    /// <param name="paksFolder">FortniteGame\\Content\\Paks folder (path returned by FortnitePathLocator)</param>
    /// <param name="moduleId">moduleId from module_key_v4.json</param>
    /// <param name="aesKeyHex">aesKeyHex from module_key_v4.json (with or without \"0x\" prefix)</param>
    /// <param name="pakGuid">
    /// guid from module_key_v4.json (with or without hyphens).
    /// plugin.pak is encrypted with this pak-specific GUID, not the main static key (GUID=0),
    /// so without this the pak index cannot be decrypted
    /// and no files (including the uplugin) will be visible.
    /// </param>
    public static bool ExportPlugin(string paksFolder, string moduleId, string aesKeyHex, string? pakGuid = null)
    {
        if (string.IsNullOrWhiteSpace(moduleId))
        {
            Console.WriteLine("[GF] moduleId is empty; skipping.");
            return false;
        }

        // paksFolder = <Install>\FortniteGame\Content\Paks
        //   -> one level up: Content
        //   -> two levels up: FortniteGame (= the game directory)
        var contentDir     = Directory.GetParent(paksFolder)?.FullName;
        var gameDir        = contentDir is not null ? Directory.GetParent(contentDir)?.FullName : null;
        if (gameDir is null)
        {
            Console.WriteLine($"[GF] Failed to determine game directory: {paksFolder}");
            return false;
        }

        var gameFeaturesDir = Path.Combine(gameDir, "Plugins", "GameFeatures");
        if (!Directory.Exists(gameFeaturesDir))
        {
            Console.WriteLine($"[GF] Plugins/GameFeatures folder not found: {gameFeaturesDir}");
            return false;
        }

        var targetDir = Path.Combine(gameFeaturesDir, moduleId);
        Directory.CreateDirectory(targetDir);

        Console.WriteLine();
        Console.WriteLine("[GF] Extracting uplugin...");

        FAesKey aesKey;
        try
        {
            var normalizedHex = aesKeyHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? aesKeyHex
                : "0x" + aesKeyHex;
            aesKey = new FAesKey(normalizedHex);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GF] Invalid AES key format: {ex.Message}");
            return false;
        }

        using var provider = new DefaultFileProvider(
            paksFolder,
            SearchOption.AllDirectories,
            new VersionContainer(EGame.GAME_UE5_8));

        try
        {
            provider.Initialize();
            provider.Mount();

            // Main static key (GUID=0) for unencrypted/shared paks
            provider.SubmitKey(new FGuid(), aesKey);

            // Pak-specific encryption key. Without it the pak index
            // cannot be decrypted and the uplugin will not be found.
            if (!string.IsNullOrWhiteSpace(pakGuid))
            {
                var normalizedGuid = pakGuid.Replace("-", "").Replace("{", "").Replace("}", "");
                if (normalizedGuid.Length == 32)
                {
                    var guid = new FGuid(normalizedGuid);
                    provider.SubmitKey(guid, aesKey);
                }
                else
                {
                    Console.WriteLine($"[GF] Invalid guid format; skipped: {pakGuid}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GF] Failed to load game files: {ex.Message}");
            return false;
        }

        // Search for moduleId + ".uplugin"
        var pluginFile = provider.Files.Values.FirstOrDefault(f =>
            string.Equals(f.Extension, "uplugin", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(f.NameWithoutExtension, moduleId, StringComparison.OrdinalIgnoreCase));

        // Fall back to any path containing moduleId if no exact match
        pluginFile ??= provider.Files.Values.FirstOrDefault(f =>
            string.Equals(f.Extension, "uplugin", StringComparison.OrdinalIgnoreCase) &&
            f.Path.Contains(moduleId, StringComparison.OrdinalIgnoreCase));

        if (pluginFile is null)
        {
            var upluginCount = provider.Files.Values.Count(f =>
                string.Equals(f.Extension, "uplugin", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"[GF] {moduleId}.uplugin was not found." +
                               $"(total .uplugin files visible after mount: {upluginCount})");
            if (upluginCount == 0)
                Console.WriteLine("[GF] No .uplugin files visible at all. The AES key/GUID may be wrong or pak decryption failed.");
            return false;
        }

        Console.WriteLine($"[GF] Found: {pluginFile.Path}");

        if (!provider.TrySaveAsset(pluginFile, out var data))
        {
            Console.WriteLine($"[GF] {pluginFile.Path} export failed.");
            return false;
        }

        string json;
        try
        {
            var text = Encoding.UTF8.GetString(data).TrimStart('\uFEFF');
            var node = JsonNode.Parse(text)?.AsObject()
                       ?? throw new InvalidOperationException("Failed to parse uplugin JSON.");

            node["EnabledByDefault"] = true;

            json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GF] Failed to edit uplugin: {ex.Message}");
            return false;
        }

        var outPath = Path.Combine(targetDir, moduleId + ".uplugin");
        File.WriteAllText(outPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[GF] Placed (EnabledByDefault=true): {outPath}");
        Console.ResetColor();
        return true;
    }

    /// <summary>Reads moduleId, aesKeyHex, and guid (pak-specific encryption GUID) from module_key_v4.json.</summary>
    public static (string? ModuleId, string? AesKeyHex, string? Guid) ReadModuleKeyJson(string jsonPath)
    {
        if (!File.Exists(jsonPath)) return (null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = doc.RootElement;
            var moduleId  = root.TryGetProperty("moduleId",  out var m) ? m.GetString() : null;
            var aesKeyHex = root.TryGetProperty("aesKeyHex", out var a) ? a.GetString() : null;
            var guid      = root.TryGetProperty("guid",      out var g) ? g.GetString() : null;
            return (moduleId, aesKeyHex, guid);
        }
        catch
        {
            return (null, null, null);
        }
    }
}
