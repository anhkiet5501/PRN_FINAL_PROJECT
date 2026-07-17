using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using BusinessLayer.Models;
using Microsoft.Extensions.Configuration;

namespace BusinessLayer.Services;

/// <summary>
/// Thông tin một model Gemini: mã định danh và tên hiển thị.
/// </summary>
public record GeminiModelInfo(string Id, string DisplayName);

/// <summary>
/// Interface dịch vụ Google Gemini AI.
/// Cung cấp: gọi Gemini để sinh nội dung, streaming SSE, và tạo vector embedding.
/// </summary>
public interface IGeminiChatService
{
    Task<string> GenerateAnswerAsync(string prompt, string? modelName = null, int maxRetries = 4);
    IAsyncEnumerable<string> GenerateStreamingAnswerAsync(string prompt, string? modelName = null, CancellationToken cancellationToken = default);
    Task<string> GenerateJsonContentAsync(string prompt, string? responseSchemaJson = null);
    Task<List<GeminiModelInfo>> GetAvailableModelsAsync();
}

/// <summary>
/// Dịch vụ tương tác với Google Gemini API.
/// Cung cấp: gọi Gemini để sinh nội dung (generateContent), streaming SSE, và JSON structured output.
/// Có fallback model chain khi model chính bị 429/503.
/// </summary>
public class GeminiChatService : IGeminiChatService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _defaultModel;

    /// <summary>
    /// Danh sách model dự phòng khi model chính bị 429/503 (quota tính riêng theo từng model,
    /// nên đổi model là có ngay quota mới). Thứ tự ưu tiên từ trên xuống.
    /// </summary>
    private static readonly string[] FallbackModels =
    {
        "gemini-2.0-flash-lite",
        "gemini-flash-lite-latest",
        "gemini-2.0-flash",
    };

    public GeminiChatService(HttpClient httpClient, ApiKeysSettings apiKeys, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _apiKey = apiKeys.Gemini?.Trim()
                  ?? throw new ArgumentNullException(nameof(apiKeys), "GEMINI_API_KEY is not configured");
        _defaultModel = Environment.GetEnvironmentVariable("GEMINI_MODEL")
                        ?? configuration["GeminiAI:Model"]
                        ?? "gemini-2.0-flash-lite";
    }

    /// <summary>
    /// Chuỗi model để thử lần lượt: model chính trước, sau đó các model dự phòng (bỏ trùng).
    /// </summary>
    private static List<string> BuildModelChain(string primaryModel)
    {
        var chain = new List<string> { primaryModel };
        foreach (var m in FallbackModels)
            if (!chain.Contains(m, StringComparer.OrdinalIgnoreCase))
                chain.Add(m);
        return chain;
    }

    // ─── GenerateJsonContent ───────────────────────────────────────────────
    public async Task<string> GenerateJsonContentAsync(string prompt, string? responseSchemaJson = null)
    {
        var model = _defaultModel;
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={_apiKey}";

        object? schemaObj = null;
        if (!string.IsNullOrEmpty(responseSchemaJson))
            schemaObj = JsonSerializer.Deserialize<object>(responseSchemaJson);

        var payload = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new { responseMimeType = "application/json", responseSchema = schemaObj }
        };

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        var requestBody = JsonSerializer.Serialize(payload, jsonOptions);
        var response = await PostWithRetryAsync(url, requestBody);
        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Gemini API error: {response.StatusCode} - {errorText}");
        }

        var responseBody = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;
        if (root.TryGetProperty("candidates", out var candidates) &&
            candidates.GetArrayLength() > 0 &&
            candidates[0].TryGetProperty("content", out var cc) &&
            cc.TryGetProperty("parts", out var pp) &&
            pp.GetArrayLength() > 0 &&
            pp[0].TryGetProperty("text", out var textEl))
            return textEl.GetString() ?? string.Empty;

        throw new InvalidOperationException($"Cannot parse Gemini response: {responseBody}");
    }

    // ─── PostWithRetry ────────────────────────────────────────────────────
    private async Task<HttpResponseMessage> PostWithRetryAsync(string url, string requestBody, int maxRetries = 4)
    {
        HttpResponseMessage? response = null;
        // Exponential backoff: 5s, 15s, 45s
        int[] backoffSeconds = { 5, 15, 45 };
        for (int i = 0; i < maxRetries; i++)
        {
            var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
            response = await _httpClient.PostAsync(url, content);
            if (response.IsSuccessStatusCode) return response;

            var sc = (int)response.StatusCode;
            bool isRetryable = sc == 429 || sc == 503 || sc == 500;
            if (isRetryable && i < maxRetries - 1)
            {
                int waitMs;
                // Respect Retry-After header if present
                if (response.Headers.TryGetValues("Retry-After", out var retryAfterValues) &&
                    int.TryParse(retryAfterValues.FirstOrDefault(), out int retryAfterSec))
                {
                    // Cap 30s: nếu hết quota theo NGÀY thì Retry-After rất lớn, chờ cũng vô ích
                    waitMs = Math.Min(retryAfterSec + 1, 30) * 1000;
                }
                else
                {
                    waitMs = backoffSeconds[Math.Min(i, backoffSeconds.Length - 1)] * 1000;
                }
                await Task.Delay(waitMs);
                continue;
            }
            break;
        }
        return response ?? new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
    }

    // ─── GenerateAnswer (blocking) ─────────────────────────────────────────
    public async Task<string> GenerateAnswerAsync(string prompt, string? modelName = null, int maxRetries = 4)
    {
        modelName = string.IsNullOrWhiteSpace(modelName) ? _defaultModel : modelName;
        var requestBody = new { contents = new[] { new { parts = new[] { new { text = prompt } } } } };
        var jsonBody = JsonSerializer.Serialize(requestBody);

        // maxRetries = 1 → chế độ "fail nhanh" (vd: rerank), không thử model dự phòng.
        var models = maxRetries <= 1 ? new List<string> { modelName } : BuildModelChain(modelName);

        HttpResponseMessage? response = null;
        foreach (var model in models)
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={_apiKey}";
            // Khi có model dự phòng: mỗi model chỉ gọi 1 lần rồi chuyển ngay sang model kế tiếp
            // (nhanh hơn nhiều so với ngồi chờ backoff 5-45s trên model đã hết quota).
            var retries = models.Count > 1 ? 1 : maxRetries;
            response = await PostWithRetryAsync(url, jsonBody, maxRetries: retries);

            if (response.IsSuccessStatusCode)
            {
                var responseString = await response.Content.ReadAsStringAsync();
                using var result = JsonDocument.Parse(responseString);
                var cands = result.RootElement.GetProperty("candidates");
                if (cands.GetArrayLength() == 0) return string.Empty;
                var parts = cands[0].GetProperty("content").GetProperty("parts");
                if (parts.GetArrayLength() == 0) return string.Empty;
                return parts[0].GetProperty("text").GetString() ?? string.Empty;
            }

            var sc = (int)response.StatusCode;
            // 429/503/500: hết quota hoặc quá tải; 404: model không tồn tại → thử model dự phòng kế tiếp.
            if (sc == 429 || sc == 503 || sc == 500 || sc == 404) continue;
            break;
        }

        var statusCode = response != null ? (int)response.StatusCode : 500;
        if (statusCode == 429) return "⚠️ Trợ lý AI đang quá tải, vui lòng thử lại sau vài giây.";
        if (statusCode == 503) return "⚠️ Dịch vụ AI tạm thời không khả dụng.";
        var errorBody = response != null ? await response.Content.ReadAsStringAsync() : string.Empty;
        return $"Lỗi khi gọi AI: {(response != null ? response.StatusCode.ToString() : "Unknown")} - {errorBody}";
    }

    // ─── GenerateStreamingAnswer (SSE) ─────────────────────────────────────
    public async IAsyncEnumerable<string> GenerateStreamingAnswerAsync(
        string prompt,
        string? modelName = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        modelName = string.IsNullOrWhiteSpace(modelName) ? _defaultModel : modelName;
        var requestBody = new { contents = new[] { new { parts = new[] { new { text = prompt } } } } };
        var jsonBody = JsonSerializer.Serialize(requestBody);

        // Thử lần lượt model chính rồi các model dự phòng — mỗi model 1 lần,
        // chuyển ngay khi gặp 429/503/500/404 (không chờ backoff trên model đã hết quota).
        var models = BuildModelChain(modelName);
        HttpResponseMessage? response = null;

        foreach (var model in models)
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:streamGenerateContent?alt=sse&key={_apiKey}";
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(jsonBody, Encoding.UTF8, "application/json") };
                response = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (response.IsSuccessStatusCode) break;

                var sc = (int)response.StatusCode;
                if (sc == 429 || sc == 503 || sc == 500 || sc == 404)
                {
                    if (model != models[^1]) response.Dispose();
                    continue;
                }
                break;
            }
            catch (OperationCanceledException) { yield break; }
        }

        if (response == null || !response.IsSuccessStatusCode)
        {
            var sc = response != null ? (int)response.StatusCode : 500;
            if (sc == 429) yield return "⚠️ Trợ lý AI đang quá tải, vui lòng thử lại sau vài giây.";
            else if (sc == 503) yield return "⚠️ Dịch vụ AI tạm thời không khả dụng.";
            else yield return $"⚠️ Lỗi kết nối AI ({sc}).";
            response?.Dispose();
            yield break;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new System.IO.StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            string? line;
            try { line = await reader.ReadLineAsync(); }
            catch (OperationCanceledException) { yield break; }

            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data: ")) continue;
            var jsonStr = line.Substring("data: ".Length).Trim();
            if (jsonStr == "[DONE]") break;

            string? chunkText = null;
            try
            {
                using var doc = JsonDocument.Parse(jsonStr);
                var root = doc.RootElement;
                if (root.TryGetProperty("candidates", out var cands) &&
                    cands.GetArrayLength() > 0 &&
                    cands[0].TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts) &&
                    parts.GetArrayLength() > 0 &&
                    parts[0].TryGetProperty("text", out var tp))
                    chunkText = tp.GetString();
            }
            catch { /* bỏ qua chunk lỗi parse */ }

            if (!string.IsNullOrEmpty(chunkText))
                yield return chunkText;
        }
    }

    // ─── GetAvailableModels ───────────────────────────────────────────────
    public async Task<List<GeminiModelInfo>> GetAvailableModelsAsync()
    {
        var defaultList = new List<GeminiModelInfo>
        {
            new("gemini-2.0-flash-lite", "Gemini 2.0 Flash Lite"),
            new("gemini-2.0-flash", "Gemini 2.0 Flash"),
            new("gemini-flash-lite-latest", "Gemini Flash Lite (Latest)"),
            new("gemini-flash-latest", "Gemini Flash (Latest)"),
            new("gemini-3.5-flash", "Gemini 3.5 Flash"),
        };
        try
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={_apiKey}&pageSize=50";
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return defaultList;
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var models = new List<GeminiModelInfo>();
            foreach (var model in doc.RootElement.GetProperty("models").EnumerateArray())
            {
                var name = model.GetProperty("name").GetString() ?? string.Empty;
                var id = name.Replace("models/", "");
                bool supportsGenerate = false;
                if (model.TryGetProperty("supportedGenerationMethods", out var methods))
                    foreach (var m in methods.EnumerateArray())
                        if (m.GetString() == "generateContent") { supportsGenerate = true; break; }
                if (!supportsGenerate) continue;
                var displayName = model.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? id : id;
                models.Add(new GeminiModelInfo(id, displayName));
            }
            return models.Count > 0 ? models : defaultList;
        }
        catch { return defaultList; }
    }
}
