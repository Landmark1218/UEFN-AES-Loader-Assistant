using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;

namespace UEFNMapInstaller;

/// <summary>
/// 取得したゲームディレクトリの Plugins/GameFeatures フォルダ内に、
/// module_key_v4.json の "moduleId" と同名のフォルダを作成し、
/// CUE4Parse で moduleId + ".uplugin" を検索・エクスポートして配置、
/// "EnabledByDefault" を true にして保存します。
/// </summary>
internal static class GameFeaturePluginExporter
{
    /// <param name="paksFolder">FortniteGame\Content\Paks フォルダ (FortnitePathLocator が返すパス)</param>
    /// <param name="moduleId">module_key_v4.json の moduleId</param>
    /// <param name="aesKeyHex">module_key_v4.json の aesKeyHex ("0x" 有無どちらでも可)</param>
    /// <param name="pakGuid">
    /// module_key_v4.json の guid (ハイフン有無どちらでも可)。
    /// plugin.pak はメインの静的キー (GUID=0) ではなく、この pak 専用の GUID で
    /// 暗号化されているため、これを渡さないとインデックスが復号できず
    /// 中身のファイル (uplugin 含む) が一切見えなくなる。
    /// </param>
    public static bool ExportPlugin(string paksFolder, string moduleId, string aesKeyHex, string? pakGuid = null)
    {
        if (string.IsNullOrWhiteSpace(moduleId))
        {
            Console.WriteLine("[GF] moduleId が空のためスキップします。");
            return false;
        }

        // paksFolder = <Install>\FortniteGame\Content\Paks
        //   → 1つ上がる: Content
        //   → 2つ上がる: FortniteGame  (= "取得したゲームのディレクトリ")
        var contentDir     = Directory.GetParent(paksFolder)?.FullName;
        var gameDir        = contentDir is not null ? Directory.GetParent(contentDir)?.FullName : null;
        if (gameDir is null)
        {
            Console.WriteLine($"[GF] ゲームディレクトリの特定に失敗しました: {paksFolder}");
            return false;
        }

        var gameFeaturesDir = Path.Combine(gameDir, "Plugins", "GameFeatures");
        if (!Directory.Exists(gameFeaturesDir))
        {
            Console.WriteLine($"[GF] Plugins/GameFeatures フォルダが見つかりません: {gameFeaturesDir}");
            return false;
        }

        var targetDir = Path.Combine(gameFeaturesDir, moduleId);
        Directory.CreateDirectory(targetDir);

        Console.WriteLine();
        Console.WriteLine("[GF] upluginを抽出中...");

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
            Console.WriteLine($"[GF] AES キーの形式が不正です: {ex.Message}");
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

            // メインの静的キー (GUID=0)。非暗号化/共通の pak 向け
            provider.SubmitKey(new FGuid(), aesKey);

            // この plugin.pak 専用の暗号化キー。これが無いと当該 pak のインデックスが
            // 復号できず、中の uplugin が見つからない。
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
                    Console.WriteLine($"[GF] guid の形式が不正なためスキップしました: {pakGuid}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GF] ゲームファイルの読み込みに失敗しました: {ex.Message}");
            return false;
        }

        // moduleId + ".uplugin" を検索
        var pluginFile = provider.Files.Values.FirstOrDefault(f =>
            string.Equals(f.Extension, "uplugin", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(f.NameWithoutExtension, moduleId, StringComparison.OrdinalIgnoreCase));

        // 完全一致が無ければパスに moduleId を含むものへフォールバック
        pluginFile ??= provider.Files.Values.FirstOrDefault(f =>
            string.Equals(f.Extension, "uplugin", StringComparison.OrdinalIgnoreCase) &&
            f.Path.Contains(moduleId, StringComparison.OrdinalIgnoreCase));

        if (pluginFile is null)
        {
            var upluginCount = provider.Files.Values.Count(f =>
                string.Equals(f.Extension, "uplugin", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"[GF] {moduleId}.uplugin が見つかりませんでした。" +
                               $"(マウント後に確認できた .uplugin 総数: {upluginCount})");
            if (upluginCount == 0)
                Console.WriteLine("[GF] .uplugin が1件も見えていません。AES キー/GUID が誤っているか、pak の復号に失敗している可能性があります。");
            return false;
        }

        Console.WriteLine($"[GF] 発見: {pluginFile.Path}");

        if (!provider.TrySaveAsset(pluginFile, out var data))
        {
            Console.WriteLine($"[GF] {pluginFile.Path} のエクスポートに失敗しました。");
            return false;
        }

        string json;
        try
        {
            var text = Encoding.UTF8.GetString(data).TrimStart('\uFEFF');
            var node = JsonNode.Parse(text)?.AsObject()
                       ?? throw new InvalidOperationException("uplugin の JSON 解析に失敗しました。");

            node["EnabledByDefault"] = true;

            json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GF] uplugin の編集に失敗しました: {ex.Message}");
            return false;
        }

        var outPath = Path.Combine(targetDir, moduleId + ".uplugin");
        File.WriteAllText(outPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[GF] 配置完了 (EnabledByDefault=true): {outPath}");
        Console.ResetColor();
        return true;
    }

    /// <summary>module_key_v4.json から moduleId / aesKeyHex / guid (pak 固有の暗号化GUID) を取り出します。</summary>
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
