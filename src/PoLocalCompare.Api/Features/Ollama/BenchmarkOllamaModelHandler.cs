using System.Text;
using System.Text.Json;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Api.Features.Ollama;

/// <summary>
/// Runs a timed inference against a local Ollama model and reports the same timing shape the
/// browser-model benchmark produces, so the two are comparable in the health panel.
/// </summary>
/// <remarks>
/// Every failure path returns a populated <see cref="OllamaBenchmarkResultDto"/> with
/// <c>IsFailure</c> set rather than throwing: an absent or wedged daemon is a routine local
/// condition, and the panel renders the reason inline.
/// </remarks>
public sealed class BenchmarkOllamaModelHandler(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<BenchmarkOllamaModelHandler> logger)
{
    public async Task<OllamaBenchmarkResultDto> HandleAsync(OllamaBenchmarkRequest request, CancellationToken ct = default)
    {
        var http = httpClientFactory.CreateClient("Ollama");
        var baseUrl = OllamaBaseUrl.Resolve(configuration);

        // Use /api/chat — model-agnostic chat format; avoids [INST] prompt-template conflicts
        // with newer models like Gemma4, Qwen3.5 that manage their own templates internally.
        var body = JsonSerializer.Serialize(new
        {
            model = request.ModelName,
            messages = new[] { new { role = "user", content = request.Prompt } },
            stream = true,
            options = new { num_predict = 256 }
        });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/chat")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                logger.LogError("Ollama /api/chat returned {StatusCode} for {Model}: {Body}",
                    (int)response.StatusCode, request.ModelName, Truncate(errorBody, 400));
                return Failed($"Ollama returned {(int)response.StatusCode}: {Truncate(errorBody, 200)}");
            }
        }
        catch (Exception ex)
        {
            return Failed($"Cannot reach Ollama: {ex.Message}");
        }

        var output = new StringBuilder();
        var evalCount = 0;
        long loadDurationNs = 0, evalDurationNs = 0, promptEvalDurationNs = 0;

        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var chunk = JsonSerializer.Deserialize<OllamaChatChunk>(line);
                    if (chunk is null) continue;

                    if (!string.IsNullOrEmpty(chunk.Message?.Content))
                        output.Append(chunk.Message.Content);

                    if (chunk.Done)
                    {
                        evalCount = chunk.EvalCount;
                        loadDurationNs = chunk.LoadDuration;
                        evalDurationNs = chunk.EvalDuration;
                        promptEvalDurationNs = chunk.PromptEvalDuration;
                    }
                }
                catch (JsonException ex)
                {
                    logger.LogDebug(ex, "Skipped malformed Ollama chunk for model {Model}", request.ModelName);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stream read error from Ollama for model {Model}", request.ModelName);
            return Failed($"Ollama stream interrupted: {ex.Message}");
        }

        var evalSecs = evalDurationNs / 1_000_000_000.0;

        return new OllamaBenchmarkResultDto
        {
            LoadMs = (int)(loadDurationNs / 1_000_000),
            FirstTokenMs = (int)(promptEvalDurationNs / 1_000_000),
            TokensPerSec = evalSecs > 0 ? (int)Math.Round(evalCount / evalSecs) : 0,
            TotalTokens = evalCount,
            Output = output.ToString()
        };
    }

    private static OllamaBenchmarkResultDto Failed(string reason) =>
        new() { IsFailure = true, FailureReason = reason };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
