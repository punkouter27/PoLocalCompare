using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace PoLocalCompare.Api.Common.Inference;

/// <summary>
/// Calls a local Ollama instance via its OpenAI-compatible streaming SSE endpoint.
/// POST {BaseUrl}/v1/chat/completions — no auth required.
/// ApiEndpointRef on the Model is the Ollama model name (e.g. "llama3.2").
/// </summary>
public sealed class OllamaInferenceProxy(
    HttpClient http,
    IConfiguration configuration,
    ILogger<OllamaInferenceProxy> logger) : IRemoteInferenceProxy
{
    private readonly HttpClient _http = http;
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<OllamaInferenceProxy> _logger = logger;

    private static readonly JsonSerializerOptions OmitNulls =
        new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public async Task<DuelResult> RunInferenceAsync(
        Model model,
        DuelId duelId,
        string promptFull,
        Func<int, long, HtmlStreamStats?, Task> onTokenUpdate,
        CancellationToken cancellationToken)
    {
        var result = new DuelResult(duelId, model.ModelId);
        var sw = Stopwatch.StartNew();

        var baseUrl = (_configuration["Ollama:BaseUrl"] ?? "http://localhost:11434").TrimEnd('/');
        var modelName = model.ApiEndpointRef;

        if (string.IsNullOrWhiteSpace(modelName))
        {
            result.IsFailure = true;
            result.FailureReason = "ApiEndpointRef (Ollama model name) is not set on this model.";
            return result;
        }

        var url = $"{baseUrl}/v1/chat/completions";

        var requestBody = new
        {
            model = modelName,
            messages = new[]
            {
                new { role = "system", content = InferencePrompt.System },
                new { role = "user", content = promptFull }
            },
            stream = true,
            max_tokens = 4096,
            // 0.2, matching FoundryInferenceProxy. The task is a fixed output format — "return
            // valid HTML5 with inline CSS" — so sampling variance buys nothing but longer,
            // more meandering documents, and these outputs are judged and move a persistent
            // rating, so reproducibility is worth more here than in a typical app. Keeping the
            // two proxies on the same value also means a remote-vs-Ollama duel is not quietly
            // comparing two different decoding settings.
            temperature = 0.2,
            // Pin the model in memory for an hour across requests. The Ollama default is 5 min,
            // which sounds generous until a tournament's first round loads a 17 GB model for
            // 30 s, runs inference, and gets unloaded before round two — paying the 30 s load
            // tax again. One hour covers every realistic tournament bracket; longer sessions
            // also win, at the cost of more VRAM held.
            keep_alive = "1h",
            // gpt-oss reasons at medium unless told otherwise — the same waste FoundryChatRequest
            // pins every Foundry family against. Ollama's OpenAI route accepts the field for it;
            // null (every other model) is left out of the body.
            reasoning_effort = modelName.StartsWith("gpt-oss", StringComparison.OrdinalIgnoreCase) ? "low" : null,
        };

        var json = JsonSerializer.Serialize(requestBody, OmitNulls);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex)
        {
            result.IsFailure = true;
            result.FailureReason = $"HTTP request to Ollama failed: {ex.Message}. Is Ollama running at {baseUrl}?";
            // Connection refused is expected when Ollama is not running locally — warn instead of error.
            if (ex is HttpRequestException { InnerException: System.Net.Sockets.SocketException })
                _logger.LogWarning("Ollama unavailable for model {Model} at {Url} — ensure Ollama is running for local model support.", modelName, url);
            else
                _logger.LogError(ex, "HTTP request to Ollama failed for model {Model} at {Url}", modelName, url);
            return result;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            result.IsFailure = true;
            result.FailureReason = $"Ollama HTTP {(int)response.StatusCode}: {body[..Math.Min(300, body.Length)]}";
            _logger.LogError("Ollama HTTP {StatusCode} for {Model}: {Body}", (int)response.StatusCode, modelName, body[..Math.Min(500, body.Length)]);
            return result;
        }

        var warmUpMs = sw.ElapsedMilliseconds;

        var streamError = await SseChatStreamReader.ReadIntoAsync(
            response, result, sw, warmUpMs, onTokenUpdate, cancellationToken);

        if (streamError is not null)
        {
            // A dropped local connection is expected when Ollama stops mid-duel — warn, don't error.
            if (streamError is System.IO.IOException or System.Net.Sockets.SocketException)
                _logger.LogWarning(streamError, "Stream read failed for Ollama model {Model} — connection may have been dropped.", modelName);
            else if (streamError is not OperationCanceledException)
                _logger.LogError(streamError, "Stream read failed for Ollama model {Model}", modelName);
            return result;
        }

        _logger.LogInformation(
            "Ollama inference complete for {Model}: {Tokens} tokens, {Bytes} bytes, {Ms}ms",
            modelName, result.TokenCount, result.HtmlOutputSizeBytes, result.TotalDurationMs);

        return result;
    }
}
