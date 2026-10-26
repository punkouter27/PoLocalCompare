using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace PoLocalCompare.Api.Common.Inference;

/// <summary>
/// Calls Azure OpenAI deployment endpoint with streaming SSE.
/// If deployment lookup returns 404, falls back to Azure AI Foundry model inference endpoint.
/// </summary>
public sealed class FoundryInferenceProxy(
    HttpClient http,
    IConfiguration configuration,
    ILogger<FoundryInferenceProxy> logger) : IRemoteInferenceProxy
{
    /// <summary>
    /// Which of the two Foundry chat routes a deployment actually answers on.
    /// </summary>
    /// <remarks>
    /// Foundry exposes the same model at <c>/openai/deployments/{name}/chat/completions</c> and
    /// at <c>/models/chat/completions</c>, and which one works depends on how the model was
    /// provisioned. The proxy discovers that by trying the first and falling back on a 404 —
    /// correct, but nothing remembered the answer, so every single call to a fallback-route
    /// model paid a wasted round-trip (and its full latency) before the real request even
    /// started. On a duel that is two wasted trips, and on an eight-model bracket, fourteen.
    ///
    /// Static because the answer is a property of the deployment, not of a scoped proxy
    /// instance — the proxy is transient and is resolved fresh for every side of every duel.
    /// Keyed by deployment name; the set of names is the seeded catalog, so this cannot grow
    /// unboundedly. A wrong entry is self-correcting: the request 404s and the flag is cleared.
    /// </remarks>
    private static readonly ConcurrentDictionary<string, bool> UsesModelInferenceRoute = new(StringComparer.Ordinal);

    private readonly HttpClient _http = http;
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<FoundryInferenceProxy> _logger = logger;

    public async Task<DuelResult> RunInferenceAsync(
        Model model,
        DuelId duelId,
        string promptFull,
        Func<int, long, HtmlStreamStats?, Task> onTokenUpdate,
        CancellationToken cancellationToken)
    {
        var result = new DuelResult(duelId, model.ModelId);
        var sw = Stopwatch.StartNew();

        var deploymentName = model.ApiEndpointRef;
        var gemini = FoundryChatRequest.IsGemini(deploymentName);
        var (endpoint, apiKey) = FoundryChatRequest.ResolveProvider(_configuration, deploymentName);

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(deploymentName))
        {
            result.IsFailure = true;
            result.FailureReason = gemini
                ? "Gemini configuration is incomplete (Gemini:ApiKey missing)."
                : "Azure AI Foundry configuration is incomplete (Endpoint, ApiKey, or ApiEndpointRef missing).";
            return result;
        }

        // Gemini has one route, with the model in the body; pointing both "routes" at it keeps
        // the Foundry two-endpoint discovery below unchanged (a 404 just retries the same URL).
        var deploymentUrl = gemini ? FoundryChatRequest.GeminiUrl(endpoint) : FoundryChatRequest.DeploymentUrl(endpoint, deploymentName);
        var modelInferenceUrl = gemini ? deploymentUrl : FoundryChatRequest.ModelInferenceUrl(endpoint);

        // Reasoning tokens are drawn from max_completion_tokens before visible output, so a
        // reasoning model still needs headroom above the classic budget — but far less of it
        // now that FoundryChatRequest pins reasoning_effort to each deployment's floor. 16,384 existed to
        // survive an unbounded default-effort reasoning pass; 8,192 leaves roughly the same
        // room for the document itself while capping the damage if a model ignores the hint.
        var maxTokens = FoundryChatRequest.IsReasoningModel(deploymentName) ? 8_192 : 4_096;

        // Reasoning models (gpt-5*, o-series) require max_completion_tokens and reject custom temperature.
        // 0.2 rather than 0.7. The task is "return valid HTML5 with inline CSS" — a fixed
        // output format with no creative latitude worth sampling for — and high temperature
        // buys only longer, more meandering documents: more tokens, more latency, more cost.
        // It also makes duels more reproducible, which matters more here than in a typical
        // app because these outputs are judged and the verdict moves a persistent rating.
        // Reasoning models ignore this (they reject a non-default temperature and
        // FoundryChatRequest omits the field for them).
        const double GenerationTemperature = 0.2;

        // The cached body returns ready-to-send JSON — system prompt, budget and stream shape
        // baked into a cached prefix/suffix, with only the user prompt (and, on the
        // model-inference route, the model name) spliced in per call.
        var deploymentJson = FoundryChatRequest.GetCachedBody(
            deploymentName, InferencePrompt.System, promptFull, maxTokens, GenerationTemperature,
            stream: true, includeModelField: gemini);

        var modelInferenceJson = FoundryChatRequest.GetCachedBody(
            deploymentName, InferencePrompt.System, promptFull, maxTokens, GenerationTemperature,
            stream: true, includeModelField: true);

        async Task<(HttpResponseMessage? Response, string? TransportError)> SendWithRetryAsync(
            string url,
            string payload,
            string target,
            CancellationToken ct)
        {
            const int maxAttempts = 3;
            int attempt = 0;

            while (true)
            {
                attempt++;
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                FoundryChatRequest.AddAuth(request, deploymentName, apiKey);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                HttpResponseMessage response;
                try
                {
                    response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                }
                catch (Exception ex)
                {
                    return (null, $"HTTP request failed: {ex.Message}");
                }

                var isTransient = response.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable
                                                        or System.Net.HttpStatusCode.BadGateway
                                                        or System.Net.HttpStatusCode.TooManyRequests;
                if (isTransient && attempt < maxAttempts)
                {
                    var delay = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                        ? GetRetryAfter(response.Headers.RetryAfter)
                        : TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    var statusCode = response.StatusCode;
                    response.Dispose();
                    _logger.LogWarning("Transient {StatusCode} for {Target}, retry {Attempt}/{Max} in {Delay}s",
                        (int)statusCode, target, attempt, maxAttempts, delay.TotalSeconds);
                    await Task.Delay(delay, ct);
                    continue;
                }

                return (response, null);
            }
        }

        // Skip straight to the route this deployment is already known to answer on.
        var knownModelInferenceRoute = UsesModelInferenceRoute.TryGetValue(deploymentName, out var known) && known;

        var (deploymentResponse, deploymentTransportError) = await SendWithRetryAsync(
            knownModelInferenceRoute ? modelInferenceUrl : deploymentUrl,
            knownModelInferenceRoute ? modelInferenceJson : deploymentJson,
            knownModelInferenceRoute ? $"model '{deploymentName}'" : $"deployment '{deploymentName}'",
            cancellationToken);

        if (deploymentResponse is null)
        {
            result.IsFailure = true;
            result.FailureReason = deploymentTransportError ?? "Unknown transport error while calling Azure AI Foundry.";
            _logger.LogError("HTTP request failed for model {Model}: {Error}", deploymentName, result.FailureReason);
            return result;
        }

        var response = deploymentResponse;

        // A 404 on the remembered route means the deployment moved or the memo was wrong; drop
        // it so the next call rediscovers rather than pinning a broken answer forever.
        if (knownModelInferenceRoute && response.StatusCode == System.Net.HttpStatusCode.NotFound)
            UsesModelInferenceRoute.TryRemove(deploymentName, out _);

        if (!knownModelInferenceRoute && response.IsSuccessStatusCode)
            UsesModelInferenceRoute[deploymentName] = false;

        if (!knownModelInferenceRoute && !response.IsSuccessStatusCode && response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            var deploymentNotFoundBody = await response.Content.ReadAsStringAsync(cancellationToken);
            response.Dispose();

            _logger.LogWarning(
                "Deployment endpoint returned 404 for {Model}. Trying Foundry model inference endpoint /models/chat/completions.",
                deploymentName);

            var (modelInferenceResponse, modelInferenceTransportError) = await SendWithRetryAsync(
                modelInferenceUrl,
                modelInferenceJson,
                $"model '{deploymentName}'",
                cancellationToken);

            if (modelInferenceResponse is null)
            {
                result.IsFailure = true;
                result.FailureReason = modelInferenceTransportError ?? "Unknown transport error while calling model inference endpoint.";
                _logger.LogError("Fallback model inference call failed for {Model}: {Error}", deploymentName, result.FailureReason);
                return result;
            }

            response = modelInferenceResponse;

            // Discovered: this deployment lives on the model-inference route. Remembering it
            // is the whole point of the memo — every later call skips the 404 probe.
            if (response.IsSuccessStatusCode)
                UsesModelInferenceRoute[deploymentName] = true;

            if (!response.IsSuccessStatusCode)
            {
                var fallbackBody = await response.Content.ReadAsStringAsync(cancellationToken);
                if (FoundryChatRequest.TryLearnShape(deploymentName, response.StatusCode, fallbackBody))
                {
                    response.Dispose();
                    return await RunInferenceAsync(model, duelId, promptFull, onTokenUpdate, cancellationToken);
                }
                result.IsFailure = true;
                result.FailureReason =
                    $"Model '{deploymentName}' failed on both endpoints. " +
                    $"Deployment endpoint: HTTP 404. Model inference endpoint: HTTP {(int)response.StatusCode}: {fallbackBody[..Math.Min(200, fallbackBody.Length)]}";
                _logger.LogError(
                    "Model '{Model}' failed on deployment endpoint (404) and model inference endpoint ({StatusCode}). Deployment body: {DeployBody}. Fallback body: {FallbackBody}",
                    deploymentName,
                    (int)response.StatusCode,
                    deploymentNotFoundBody[..Math.Min(300, deploymentNotFoundBody.Length)],
                    fallbackBody[..Math.Min(300, fallbackBody.Length)]);
                return result;
            }

            _logger.LogInformation("Model '{Model}' succeeded via model inference endpoint fallback.", deploymentName);
        }
        else if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            // Once per deployment: a reasoning model behind a classic-looking name. The retry
            // rebuilds the body with the learned shape; TryLearnShape is false the second time.
            if (FoundryChatRequest.TryLearnShape(deploymentName, response.StatusCode, body))
            {
                response.Dispose();
                return await RunInferenceAsync(model, duelId, promptFull, onTokenUpdate, cancellationToken);
            }
            result.IsFailure = true;
            result.FailureReason = $"HTTP {(int)response.StatusCode}: {body[..Math.Min(300, body.Length)]}";
            _logger.LogError("Azure OpenAI HTTP {StatusCode} for {Model}: {Body}", (int)response.StatusCode, deploymentName, body[..Math.Min(500, body.Length)]);
            return result;
        }

        var warmUpMs = sw.ElapsedMilliseconds; // time-to-first-byte (response headers received)

        var streamError = await SseChatStreamReader.ReadIntoAsync(
            response, result, sw, warmUpMs, onTokenUpdate, cancellationToken);

        if (streamError is not null)
        {
            if (streamError is not OperationCanceledException)
                _logger.LogError(streamError, "Stream read failed for model {Model}", deploymentName);
            return result;
        }

        // Estimate API cost from provider-reported usage. TokenCount falls back to stream chunks
        // only for providers that do not send usage, so cost remains explicitly approximate there.
        // Priced by the model the provider says it ran when the price book knows it: a deployment
        // name is a label, and three names in this resource serve a dearer model than they say.
        var served = DefaultPriceBook.Resolve(result.ServedModel);
        if (TokenCost.Compute(served?.InputPerMillion ?? model.InputTokenPricePerMillion,
                served?.OutputPerMillion ?? model.OutputTokenPricePerMillion,
                result.PromptTokenCount, result.TokenCount,
                FoundryChatRequest.ReportsReasoningSeparately(deploymentName) ? result.ReasoningTokenCount : null) is { } cost)
        {
            result.ApiCostUsd = cost;
        }

        _logger.LogInformation(
            "Inference complete for {Model}: {Tokens} tokens, {Bytes} bytes, {Ms}ms",
            deploymentName, result.TokenCount, result.HtmlOutputSizeBytes, result.TotalDurationMs);

        return result;
    }

    private static TimeSpan GetRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta)
            return TimeSpan.FromSeconds(Math.Clamp(delta.TotalSeconds, 1, 90));
        if (retryAfter?.Date is { } retryAt)
            return TimeSpan.FromSeconds(Math.Clamp((retryAt - DateTimeOffset.UtcNow).TotalSeconds, 1, 90));
        return TimeSpan.FromSeconds(30);
    }
}
