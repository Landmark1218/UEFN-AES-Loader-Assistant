using System.Text.Json;
using System.Text.RegularExpressions;

namespace UEFNMapInstaller;

/// <summary>
/// Service that acquires and refreshes tokens via the Epic device-auth flow.
/// Prompts device-code login if device_auth.json is absent,
/// then silently refreshes using stored device credentials.
/// </summary>
internal sealed class EpicAuthService
{
    private readonly string _dataDir;
    private readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    private string DeviceAuthPath => Path.Combine(_dataDir, "device_auth.json");

    public EpicAuthService(string dataDir)
    {
        _dataDir = dataDir;
        Directory.CreateDirectory(dataDir);
    }

    // -- Public API --

    /// <summary>Returns an access token for Content Service. Auto-refreshes when expired.</summary>
    public async Task<string> GetContentAccessTokenAsync(CancellationToken ct = default)
    {
        var deviceAuth = LoadDeviceAuth();
        if (deviceAuth is null)
        {
            Console.WriteLine("Device credentials not found. Starting first-time login.");
            deviceAuth = await DeviceCodeLoginAsync(ct);
        }

        try
        {
            return await ExchangeToContentTokenAsync(deviceAuth, ct);
        }
        catch (EpicApiException ex)
        {
            Console.WriteLine($"Token refresh failed: {ex.Message}");
            Console.WriteLine("Re-login started...");
            deviceAuth = await DeviceCodeLoginAsync(ct);
            return await ExchangeToContentTokenAsync(deviceAuth, ct);
        }
    }

    // -- Device-code login --

    private async Task<DeviceAuthRecord> DeviceCodeLoginAsync(CancellationToken ct)
    {
        // 1. Fetch client token via client_credentials
        var clientToken = await EpicHttp.FormRequestAsync<TokenResponse>(
            $"{EpicEndpoints.AccountBase}/account/api/oauth/token",
            new Dictionary<string, string> { ["grant_type"] = "client_credentials" },
            EpicEndpoints.JsDeviceAuthBasic, ct);

        if (string.IsNullOrEmpty(clientToken.AccessToken))
            throw new EpicApiException("client_credentials returned no access token");

        // 2. Fetch device auth URL
        var deviceResp = await EpicHttp.PostJsonAsync<TokenResponse>(
            $"{EpicEndpoints.AccountBase}/account/api/oauth/deviceAuthorization",
            new { prompt = "login" },
            clientToken.AccessToken, ct);

        var verifyUri = deviceResp.VerificationUri ?? "https://www.epicgames.com/activate";
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("======================================================");
        Console.WriteLine(" Epic account authentication required");
        Console.WriteLine("======================================================");
        Console.ResetColor();
        Console.WriteLine($" Open the URL below in your browser and log in:\n");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  {verifyUri}");
        Console.ResetColor();
        Console.WriteLine("\n After logging in, this window will continue automatically...");
        Console.WriteLine();

        if (string.IsNullOrEmpty(deviceResp.DeviceCode))
            throw new EpicApiException("Failed to obtain device_code");

        // 3. Polling
        int expiresIn = deviceResp.ExpiresIn > 0 ? deviceResp.ExpiresIn : 300;
        int interval  = deviceResp.Interval  > 0 ? deviceResp.Interval  : 5;
        var deadline  = DateTime.UtcNow.AddSeconds(expiresIn);
        TokenResponse? userToken = null;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            try
            {
                userToken = await EpicHttp.FormRequestAsync<TokenResponse>(
                    $"{EpicEndpoints.AccountBase}/account/api/oauth/token",
                    new Dictionary<string, string>
                    {
                        ["grant_type"]  = "device_code",
                        ["device_code"] = deviceResp.DeviceCode,
                    },
                    EpicEndpoints.JsDeviceAuthBasic, ct);
                break;
            }
            catch (EpicApiException ex) when (
                ex.Message.Contains("authorization_pending") ||
                ex.Message.Contains("slow_down"))
            {
                Console.Write(".");
                continue;
            }
        }

        if (userToken is null || string.IsNullOrEmpty(userToken.AccessToken))
            throw new EpicApiException("Device-code login timed out");

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Login successful: {userToken.DisplayName ?? userToken.AccountId}");
        Console.ResetColor();

        // 4. exchange_code -> Android client token
        var exchangeCode = await GetExchangeCodeAsync(userToken.AccessToken, ct);
        var androidToken = await EpicHttp.FormRequestAsync<TokenResponse>(
            $"{EpicEndpoints.AccountBase}/account/api/oauth/token",
            new Dictionary<string, string>
            {
                ["grant_type"]    = "exchange_code",
                ["exchange_code"] = exchangeCode,
            },
            EpicEndpoints.AndroidBasic, ct);

        if (string.IsNullOrEmpty(androidToken.AccessToken) || string.IsNullOrEmpty(userToken.AccountId))
            throw new EpicApiException("Failed to obtain Android exchange token");

        // 5. Create and save device_auth credentials
        var deviceCreated = await EpicHttp.PostJsonAsync<DeviceAuthCreatedRecord>(
            $"{EpicEndpoints.AccountBase}/account/api/public/account/{Uri.EscapeDataString(userToken.AccountId)}/deviceAuth",
            new { },
            androidToken.AccessToken, ct);

        if (string.IsNullOrEmpty(deviceCreated.DeviceId) || string.IsNullOrEmpty(deviceCreated.Secret))
            throw new EpicApiException("Failed to create deviceAuth");

        var record = new DeviceAuthRecord
        {
            AccountId   = userToken.AccountId,
            DeviceId    = deviceCreated.DeviceId,
            Secret      = deviceCreated.Secret,
            DisplayName = userToken.DisplayName ?? userToken.AccountId,
        };
        SaveDeviceAuth(record);
        Console.WriteLine($"Device credentials saved: {DeviceAuthPath}");
        return record;
    }

    // -- Credentials -> Content Service token --

    private async Task<string> ExchangeToContentTokenAsync(DeviceAuthRecord record, CancellationToken ct)
    {
        // Fetch Android token via device_auth grant
        var authToken = await EpicHttp.FormRequestAsync<TokenResponse>(
            $"{EpicEndpoints.AccountBase}/account/api/oauth/token",
            new Dictionary<string, string>
            {
                ["grant_type"] = "device_auth",
                ["account_id"] = record.AccountId,
                ["device_id"]  = record.DeviceId,
                ["secret"]     = record.Secret,
                ["token_type"] = "eg1",
            },
            EpicEndpoints.AndroidBasic, ct);

        if (string.IsNullOrEmpty(authToken.AccessToken))
            throw new EpicApiException("device_auth token response contains no access token");

        // Exchange exchange_code for Content Service token
        var exchangeCode = await GetExchangeCodeAsync(authToken.AccessToken, ct);
        var contentToken = await EpicHttp.FormRequestAsync<TokenResponse>(
            $"{EpicEndpoints.AccountBase}/account/api/oauth/token",
            new Dictionary<string, string>
            {
                ["grant_type"]    = "exchange_code",
                ["exchange_code"] = exchangeCode,
            },
            EpicEndpoints.JsContentExchangeBasic, ct);

        if (string.IsNullOrEmpty(contentToken.AccessToken))
            throw new EpicApiException("Failed to obtain Content Service token");

        return contentToken.AccessToken;
    }

    private async Task<string> GetExchangeCodeAsync(string accessToken, CancellationToken ct)
    {
        var data = await EpicHttp.GetJsonAsync<TokenResponse>(
            $"{EpicEndpoints.AccountBase}/account/api/oauth/exchange",
            accessToken, ct);

        if (string.IsNullOrEmpty(data.Code))
            throw new EpicApiException("exchange response contains no code");

        return data.Code;
    }

    // -- device_auth.json read/write --

    private DeviceAuthRecord? LoadDeviceAuth()
    {
        if (!File.Exists(DeviceAuthPath)) return null;
        try
        {
            var json = File.ReadAllText(DeviceAuthPath);
            var record = JsonSerializer.Deserialize<DeviceAuthRecord>(json);
            if (record is null) return null;
            if (string.IsNullOrEmpty(record.AccountId) ||
                string.IsNullOrEmpty(record.DeviceId)  ||
                string.IsNullOrEmpty(record.Secret))
                return null;
            return record;
        }
        catch
        {
            return null;
        }
    }

    private void SaveDeviceAuth(DeviceAuthRecord record)
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(DeviceAuthPath,
            JsonSerializer.Serialize(record, _jsonOpts));
    }
}
