using System.Text.Json;
using System.Text.RegularExpressions;

namespace UEFNMapInstaller;

/// <summary>
/// Epic device-auth フローでトークンを取得・更新するサービス。
/// device_auth.json がなければデバイスコードログインを促し、
/// 以降はデバイス認証情報でサイレントに更新します。
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

    // ── 公開 API ──────────────────────────────────────────────────────

    /// <summary>Content Service 向けのアクセストークンを返す。期限切れなら自動更新。</summary>
    public async Task<string> GetContentAccessTokenAsync(CancellationToken ct = default)
    {
        var deviceAuth = LoadDeviceAuth();
        if (deviceAuth is null)
        {
            Console.WriteLine("デバイス認証情報が見つかりません。初回ログインを開始します。");
            deviceAuth = await DeviceCodeLoginAsync(ct);
        }

        try
        {
            return await ExchangeToContentTokenAsync(deviceAuth, ct);
        }
        catch (EpicApiException ex)
        {
            Console.WriteLine($"トークン更新に失敗しました: {ex.Message}");
            Console.WriteLine("再ログインを開始します…");
            deviceAuth = await DeviceCodeLoginAsync(ct);
            return await ExchangeToContentTokenAsync(deviceAuth, ct);
        }
    }

    // ── デバイスコードログイン ────────────────────────────────────────

    private async Task<DeviceAuthRecord> DeviceCodeLoginAsync(CancellationToken ct)
    {
        // 1. client_credentials でクライアントトークン取得
        var clientToken = await EpicHttp.FormRequestAsync<TokenResponse>(
            $"{EpicEndpoints.AccountBase}/account/api/oauth/token",
            new Dictionary<string, string> { ["grant_type"] = "client_credentials" },
            EpicEndpoints.JsDeviceAuthBasic, ct);

        if (string.IsNullOrEmpty(clientToken.AccessToken))
            throw new EpicApiException("client_credentials でアクセストークンが返りませんでした");

        // 2. デバイス認証URL取得
        var deviceResp = await EpicHttp.PostJsonAsync<TokenResponse>(
            $"{EpicEndpoints.AccountBase}/account/api/oauth/deviceAuthorization",
            new { prompt = "login" },
            clientToken.AccessToken, ct);

        var verifyUri = deviceResp.VerificationUri ?? "https://www.epicgames.com/activate";
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("======================================================");
        Console.WriteLine(" Epicアカウントでの認証が必要です");
        Console.WriteLine("======================================================");
        Console.ResetColor();
        Console.WriteLine($" 以下のURLをブラウザで開いてログインしてください:\n");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  {verifyUri}");
        Console.ResetColor();
        Console.WriteLine("\n ログイン後、このウィンドウが自動的に続行します...");
        Console.WriteLine();

        if (string.IsNullOrEmpty(deviceResp.DeviceCode))
            throw new EpicApiException("device_code が取得できませんでした");

        // 3. ポーリング
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
            throw new EpicApiException("デバイスコードログインがタイムアウトしました");

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"ログイン成功: {userToken.DisplayName ?? userToken.AccountId}");
        Console.ResetColor();

        // 4. exchange_code → Android クライアントトークン
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
            throw new EpicApiException("Android 交換トークンの取得に失敗しました");

        // 5. device_auth 認証情報を作成・保存
        var deviceCreated = await EpicHttp.PostJsonAsync<DeviceAuthCreatedRecord>(
            $"{EpicEndpoints.AccountBase}/account/api/public/account/{Uri.EscapeDataString(userToken.AccountId)}/deviceAuth",
            new { },
            androidToken.AccessToken, ct);

        if (string.IsNullOrEmpty(deviceCreated.DeviceId) || string.IsNullOrEmpty(deviceCreated.Secret))
            throw new EpicApiException("deviceAuth の作成に失敗しました");

        var record = new DeviceAuthRecord
        {
            AccountId   = userToken.AccountId,
            DeviceId    = deviceCreated.DeviceId,
            Secret      = deviceCreated.Secret,
            DisplayName = userToken.DisplayName ?? userToken.AccountId,
        };
        SaveDeviceAuth(record);
        Console.WriteLine($"デバイス認証情報を保存しました: {DeviceAuthPath}");
        return record;
    }

    // ── 認証情報 → Content Service トークン ─────────────────────────

    private async Task<string> ExchangeToContentTokenAsync(DeviceAuthRecord record, CancellationToken ct)
    {
        // device_auth グラントで Androidトークン取得
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
            throw new EpicApiException("device_auth トークンレスポンスにアクセストークンがありません");

        // exchange_code → Content Service トークンに交換
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
            throw new EpicApiException("Content Service 用トークンの取得に失敗しました");

        return contentToken.AccessToken;
    }

    private async Task<string> GetExchangeCodeAsync(string accessToken, CancellationToken ct)
    {
        var data = await EpicHttp.GetJsonAsync<TokenResponse>(
            $"{EpicEndpoints.AccountBase}/account/api/oauth/exchange",
            accessToken, ct);

        if (string.IsNullOrEmpty(data.Code))
            throw new EpicApiException("exchange レスポンスに code がありません");

        return data.Code;
    }

    // ── device_auth.json 読み書き ──────────────────────────────────

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
