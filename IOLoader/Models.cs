using System.Text.Json.Serialization;

namespace UEFNMapInstaller;

// -- Epic API endpoint constants --
internal static class EpicEndpoints
{
    public const string AccountBase = "https://account-public-service-prod.ol.epicgames.com";
    public const string ContentServiceBase = "https://content-service.bfda.live.use1a.on.epicgames.com";
    public const string DefaultMnemonicApi = "https://api.fortnitejp.news/api/fortnite/discovery/mnemonic/{namespace}/{map_code}";
    public const string ModuleKeyBatchUrl = $"{ContentServiceBase}/api/content/v4/module/key/batch";
    public const string DillyMappingsUrl = "https://export-service-new.dillyapis.com/v1/mappings";

    // Basic auth value for device-auth flow (same as Python version)
    public const string JsDeviceAuthBasic = "NzlhOTMxYjM3NTMzNDU3MGFjMzY5MjM0ZjVkYTA1ZWM6ZWU3MzM1ZGYzYzRhNDEyY2I1NzA1NWFiN2FkZTY5M2U=";
    public const string JsContentExchangeBasic = "M2UxM2M1YzU3ZjU5NGE1NzhhYmU1MTZlZWNiNjczZmU6NTMwZTMxNmMzMzdlNDA5ODkzYzU1ZWM0NGYyMmNkNjI=";

    // Android client (used for device_auth / exchange)
    public const string AndroidClientId = "3f69e56c7649492c8cc29f1af08a8a12";
    public const string AndroidClientSecret = "b51ee9cb12234f50a69efa67ef53812e";

    public static string AndroidBasic =>
        Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{AndroidClientId}:{AndroidClientSecret}"));
}

// -- JSON models --

internal sealed class DeviceAuthRecord
{
    [JsonPropertyName("accountId")] public string AccountId { get; set; } = "";
    [JsonPropertyName("deviceId")]  public string DeviceId  { get; set; } = "";
    [JsonPropertyName("secret")]    public string Secret    { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
}

internal sealed class TokenResponse
{
    [JsonPropertyName("access_token")]  public string? AccessToken  { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("expires_at")]    public string? ExpiresAt    { get; set; }
    [JsonPropertyName("account_id")]    public string? AccountId    { get; set; }
    [JsonPropertyName("displayName")]   public string? DisplayName  { get; set; }
    [JsonPropertyName("device_code")]   public string? DeviceCode   { get; set; }
    [JsonPropertyName("verification_uri_complete")] public string? VerificationUri { get; set; }
    [JsonPropertyName("expires_in")]    public int     ExpiresIn    { get; set; }
    [JsonPropertyName("interval")]      public int     Interval     { get; set; }
    [JsonPropertyName("code")]          public string? Code         { get; set; }
}

internal sealed class DeviceAuthCreatedRecord
{
    [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
    [JsonPropertyName("secret")]   public string? Secret   { get; set; }
}

internal sealed class MnemonicResponse
{
    [JsonPropertyName("metadata")] public MnemonicMetadata? Metadata { get; set; }
    [JsonPropertyName("version")]  public object? Version { get; set; }
}

internal sealed class MnemonicMetadata
{
    [JsonPropertyName("title")]          public string?            Title         { get; set; }
    [JsonPropertyName("projectId")]      public string?            ProjectId     { get; set; }
    [JsonPropertyName("public_modules")] public List<object>?      PublicModules { get; set; }
}

internal sealed class V2PackageResponse
{
    [JsonPropertyName("isEncrypted")] public bool IsEncrypted { get; set; }
    [JsonPropertyName("resolved")]    public V2Resolved? Resolved { get; set; }
}

internal sealed class V2Resolved
{
    [JsonPropertyName("root")] public V2Root? Root { get; set; }
}

internal sealed class V2Root
{
    [JsonPropertyName("moduleId")] public string? ModuleId { get; set; }
    [JsonPropertyName("version")]  public object? Version  { get; set; }
}

internal sealed class DillyMappings
{
    [JsonPropertyName("version")] public string? Version { get; set; }
}

internal sealed class V4PackageResponse
{
    [JsonPropertyName("content")] public List<V4ContentItem>? Content { get; set; }
    [JsonPropertyName("status")]  public string? Status { get; set; }
}

internal sealed class V4ContentItem
{
    [JsonPropertyName("binaries")] public V4Binaries? Binaries { get; set; }
}

internal sealed class V4Binaries
{
    [JsonPropertyName("baseUrl")]     public string? BaseUrl     { get; set; }
    [JsonPropertyName("totalSizeKb")] public double  TotalSizeKb { get; set; }
}

internal sealed class LauncherInstalled
{
    [JsonPropertyName("InstallationList")] public List<InstallationEntry>? InstallationList { get; set; }
}

internal sealed class InstallationEntry
{
    [JsonPropertyName("InstallLocation")] public string? InstallLocation { get; set; }
    [JsonPropertyName("AppName")]         public string? AppName         { get; set; }
}

internal sealed class ModuleKeyItem
{
    [JsonPropertyName("key")] public ModuleKeyData? Key { get; set; }
}

internal sealed class ModuleKeyData
{
    [JsonPropertyName("Key")]  public string? Key  { get; set; }
    [JsonPropertyName("Guid")] public string? Guid { get; set; }
}

// -- Signature cache --
// Caches the find-signature result together with the game version (major.minor, e.g. \"41.10\")
// so that on next launch, if the version is unchanged, the DLL re-scan
// can be skipped.
internal sealed class SignatureCache
{
    [JsonPropertyName("gameVersion")] public string? GameVersion { get; set; }
    [JsonPropertyName("signature")]   public string? Signature   { get; set; }
    [JsonPropertyName("functionRva")] public string? FunctionRva { get; set; }
    [JsonPropertyName("updatedAtUtc")] public string? UpdatedAtUtc { get; set; }
}
