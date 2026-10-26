using System.Net;
using System.Text;

namespace PoLocalCompare.Api.Common.Inference;

/// <summary>
/// One non-streaming chat call, with the route fallback every Foundry caller needs.
/// </summary>
/// <remarks>
/// A model in a Foundry resource answers on one of two routes depending on how it was
/// provisioned: the per-deployment route, or the model-inference route with the model named in
/// the body. Nothing says which in advance, so the call is "try the first, and on a 404 try
/// the second". That sequence was written out separately in the judge, the availability probe
/// and the prompt sharpener, and each copy had to learn Gemini (one route, Bearer auth) on its
/// own. <see cref="FoundryInferenceProxy"/> keeps its own version on purpose: it streams, it
/// retries, and it remembers which route a deployment answered on — none of which a
/// one-shot call wants.
/// </remarks>
public static class FoundryChatClient
{
    /// <param name="UsedFallback">The deployment route 404'd and this is the model-inference route's answer.</param>
    public sealed record Reply(HttpStatusCode Status, string Body, string? RetryAfter, bool UsedFallback);

    /// <param name="bodyFor">
    /// The JSON body for a route; the argument is whether the route needs the model named in
    /// the body (the model-inference route and Gemini do, the deployment route does not).
    /// </param>
    /// <param name="perSendTimeout">A ceiling on each send, for callers that must not wait on a slow deployment.</param>
    public static async Task<Reply> PostAsync(
        HttpClient http,
        string endpoint,
        string deployment,
        string apiKey,
        Func<bool, string> bodyFor,
        CancellationToken cancellationToken,
        TimeSpan? perSendTimeout = null)
    {
        if (FoundryChatRequest.IsGemini(deployment))
        {
            return await SendAsync(http, FoundryChatRequest.GeminiUrl(endpoint), deployment, apiKey,
                bodyFor(true), usedFallback: false, perSendTimeout, cancellationToken);
        }

        var deploymentUrl = FoundryChatRequest.DeploymentUrl(endpoint, deployment);
        var first = await SendAsync(http, deploymentUrl, deployment, apiKey,
            bodyFor(false), usedFallback: false, perSendTimeout, cancellationToken);
        // A deployment whose name reads classic but whose model reasons rejects the classic
        // shape once; bodyFor rebuilds with the learned shape (see FoundryChatRequest.TryLearnShape).
        if (FoundryChatRequest.TryLearnShape(deployment, first.Status, first.Body))
            first = await SendAsync(http, deploymentUrl, deployment, apiKey,
                bodyFor(false), usedFallback: false, perSendTimeout, cancellationToken);
        if (first.Status != HttpStatusCode.NotFound) return first;

        var modelUrl = FoundryChatRequest.ModelInferenceUrl(endpoint);
        var fallback = await SendAsync(http, modelUrl, deployment, apiKey,
            bodyFor(true), usedFallback: true, perSendTimeout, cancellationToken);
        return FoundryChatRequest.TryLearnShape(deployment, fallback.Status, fallback.Body)
            ? await SendAsync(http, modelUrl, deployment, apiKey, bodyFor(true), usedFallback: true, perSendTimeout, cancellationToken)
            : fallback;
    }

    /// <summary><c>choices[0].message.content</c> of a non-streaming completion, or null.</summary>
    public static string? ContentOf(string payload)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != System.Text.Json.JsonValueKind.Array || choices.GetArrayLength() == 0)
                return null;
            return choices[0].TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == System.Text.Json.JsonValueKind.String
                    ? content.GetString()
                    : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static async Task<Reply> SendAsync(
        HttpClient http, string url, string deployment, string apiKey, string body,
        bool usedFallback, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        FoundryChatRequest.AddAuth(request, deployment, apiKey);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } limit) cts.CancelAfter(limit);

        using var response = await http.SendAsync(request, cts.Token);
        var retryAfter = response.Headers.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null;
        return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync(cts.Token), retryAfter, usedFallback);
    }
}
