using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace UEFNMapInstaller;

/// <summary>Helper class that handles HTTP communication with the Epic API</summary>
internal static class EpicHttp
{
    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
    })
    {
        Timeout = TimeSpan.FromSeconds(60),
        DefaultRequestHeaders =
        {
            UserAgent = { ProductInfoHeaderValue.Parse("uefn-installer/1.0") },
        },
    };

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // -- Form requests --

    public static async Task<T> FormRequestAsync<T>(
        string url,
        Dictionary<string, string> form,
        string basicValue,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicValue);
        req.Content = new FormUrlEncodedContent(form);

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new EpicApiException($"HTTP {(int)resp.StatusCode} from {url}: {body}");

        return JsonSerializer.Deserialize<T>(body, _json)
               ?? throw new EpicApiException($"Empty response from {url}");
    }

    // ── GET JSON ───────────────────────────────────────────────────────

    public static async Task<T> GetJsonAsync<T>(
        string url,
        string? bearerToken = null,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (bearerToken is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new EpicApiException($"HTTP {(int)resp.StatusCode} from {url}: {body}");

        return JsonSerializer.Deserialize<T>(body, _json)
               ?? throw new EpicApiException($"Empty response from {url}");
    }

    // ── POST JSON ──────────────────────────────────────────────────────

    public static async Task<T> PostJsonAsync<T>(
        string url,
        object payload,
        string bearerToken,
        CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(payload, _json);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new EpicApiException($"HTTP {(int)resp.StatusCode} from {url}: {body}");

        return JsonSerializer.Deserialize<T>(body, _json)
               ?? throw new EpicApiException($"Empty response from {url}");
    }

    // -- Binary download (with progress display) --

    public static async Task DownloadFileAsync(
        string url,
        string outputPath,
        string? bearerToken = null,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        if (bearerToken is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new EpicApiException($"HTTP {(int)resp.StatusCode} from {url}: {err}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var total = resp.Content.Headers.ContentLength ?? -1L;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(outputPath);

        var buf = new byte[1024 * 256];
        long downloaded = 0;
        int read;
        while ((read = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, read), ct);
            downloaded += read;
            progress?.Report(downloaded);
        }
    }
}

internal sealed class EpicApiException : Exception
{
    public EpicApiException(string message) : base(message) { }
}
