using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Models;

/// <summary>
/// Answers "can this model actually run right now?" per registered model, so the Compare page
/// only offers pairings that will not fail on submit.
/// </summary>
/// <remarks>
/// The three model types are checked three different ways and that asymmetry is deliberate:
/// browser models are always selectable because they download on first use, Ollama models are
/// resolved from a single /api/tags call shared across all of them, and Foundry deployments are
/// probed individually with a real (tiny) completion because nothing cheaper distinguishes
/// "deployment exists" from "key is wrong".
/// </remarks>
public sealed class GetModelAvailabilityHandler(
    IModelRepository modelRepository,
    IWebHostEnvironment environment,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory)
{
    /// <summary>Per-probe timeout. The whole poll has its own shorter deadline in the endpoint.</summary>
    private const int ProbeTimeoutSeconds = 6;

    /// <summary>
    /// Caps simultaneous Foundry probes per poll. Foundry is one resource with one rate-limit
    /// bucket; firing 17 probes at once on a 17-model catalog burns the bucket for everyone
    /// else in the process. 8 is small enough to stay well under any tier limit and large enough
    /// to keep the wall-clock under 1.5 s for a healthy resource.
    /// </summary>
    private const int MaxConcurrentProbes = 8;

    public async Task<IReadOnlyList<ModelAvailabilityDto>> HandleAsync(CancellationToken ct = default)
    {
        var models = ModelVisibility.Filter((await modelRepository.GetAllAsync()).Select(m => m.ToDto()), environment);

        var ollama = await ProbeOllamaAsync(models, ct);
        var foundry = new FoundryProbeContext(
            configuration,
            httpClientFactory.CreateClient("Foundry"),
            new SemaphoreSlim(MaxConcurrentProbes, MaxConcurrentProbes));

        // Task.WhenAll already runs the per-model checks concurrently; the semaphore inside
        // each Foundry probe prevents the catalog from saturating the HTTP pool.
        return await Task.WhenAll(models.Select(model => CheckAsync(model, ollama, foundry, ct)));
    }

    /// <summary>True when the installed Ollama tag is the registry's tag (or a size-qualified variant of it).</summary>
    private static bool OllamaTagMatches(string installedTag, string registryTag)
    {
        if (string.IsNullOrWhiteSpace(installedTag) || string.IsNullOrWhiteSpace(registryTag)) return false;
        if (installedTag.Equals(registryTag, StringComparison.OrdinalIgnoreCase)) return true;
        return installedTag.StartsWith(registryTag + ":", StringComparison.OrdinalIgnoreCase);
    }

    // ── Ollama ────────────────────────────────────────────────────────────────

    private sealed record OllamaProbe(string[] Available, bool Checked, string? Error);

    private async Task<OllamaProbe> ProbeOllamaAsync(IReadOnlyList<ModelDto> models, CancellationToken ct)
    {
        if (!models.Any(m => m.ModelType == ModelType.LocalService))
            return new OllamaProbe([], Checked: false, Error: null);

        var baseUrl = (configuration["Ollama:BaseUrl"] ?? "http://localhost:11434").TrimEnd('/');
        var client = httpClientFactory.CreateClient("OllamaStatus");
        try
        {
            var tags = await client.GetFromJsonAsync<OllamaTagsResponse>($"{baseUrl}/api/tags", ct);
            return new OllamaProbe(tags?.Models?.Select(m => m.Name).ToArray() ?? [], Checked: true, Error: null);
        }
        catch (Exception ex)
        {
            return new OllamaProbe([], Checked: false, Error: $"Ollama unavailable: {ex.Message}");
        }
    }

    // ── Foundry ───────────────────────────────────────────────────────────────

    /// <summary>Open connections to Foundry, throttled per probe to honour rate limits.</summary>
    private sealed record FoundryProbeContext(IConfiguration Configuration, HttpClient Client, SemaphoreSlim Throttle);

    private static readonly object[] ProbeMessages = [new { role = "user", content = "Say OK." }];

    /// <summary>
    /// The probe body for one deployment. Built per model, not once per poll: the body's shape
    /// depends on the deployment (reasoning models take <c>max_completion_tokens</c> and a
    /// reasoning effort, classic ones <c>max_tokens</c> and a temperature), and the
    /// model-inference route names the model in the body. The hoisted version built both from
    /// the first remote model in the catalog, so every GPT-5 deployment was sent a classic body,
    /// answered 400 and was reported unavailable — and the 404 fallback asked about the wrong
    /// model entirely.
    /// </summary>
    private static string ProbeBody(string deploymentName, bool includeModelField) =>
        JsonSerializer.Serialize(FoundryChatRequest.Build(
            deploymentName, ProbeMessages, maxTokens: 16, temperature: 0, stream: false, includeModelField));

    // ── Per-model dispatch ────────────────────────────────────────────────────

    private static async Task<ModelAvailabilityDto> CheckAsync(
        ModelDto model, OllamaProbe ollama, FoundryProbeContext foundry, CancellationToken ct) =>
        model.ModelType switch
        {
            // Browser models may be downloaded on first run, so keep them selectable.
            ModelType.Local => Available(model),
            ModelType.LocalService => CheckOllama(model, ollama),
            _ => await CheckFoundryAsync(model, foundry, ct)
        };

    private static ModelAvailabilityDto CheckOllama(ModelDto model, OllamaProbe ollama)
    {
        var modelRef = model.ApiEndpointRef ?? string.Empty;
        if (string.IsNullOrWhiteSpace(modelRef))
            return Unavailable(model, "ApiEndpointRef is empty.");

        if (!ollama.Checked)
            return Unavailable(model, ollama.Error ?? "Unable to verify Ollama availability.");

        var available = ollama.Available.Any(m =>
            m.Equals(modelRef, StringComparison.OrdinalIgnoreCase) ||
            m.StartsWith(modelRef + ":", StringComparison.OrdinalIgnoreCase));

        return available ? Available(model) : Unavailable(model, $"Not found in Ollama: {modelRef}",
            $"Install with `ollama pull {modelRef}` (or copy an existing local tag with `ollama cp <existing>:<tag> {modelRef}`).");
    }

    private static async Task<ModelAvailabilityDto> CheckFoundryAsync(
        ModelDto model, FoundryProbeContext foundry, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model.ApiEndpointRef))
            return Unavailable(model, "ApiEndpointRef is empty.");

        var deploymentName = model.ApiEndpointRef;
        var gemini = FoundryChatRequest.IsGemini(deploymentName);
        var (endpoint, apiKey) = FoundryChatRequest.ResolveProvider(foundry.Configuration, deploymentName);
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
            return Unavailable(model, gemini ? "Gemini API key is missing." : "AzureAiFoundry endpoint or API key is missing.");

        try
        {
            // The throttle is held across both routes of one probe: it bounds how many
            // deployments are being asked at once, which is what protects the HTTP pool.
            FoundryChatClient.Reply reply;
            await foundry.Throttle.WaitAsync(ct);
            try
            {
                reply = await FoundryChatClient.PostAsync(
                    foundry.Client, endpoint, deploymentName, apiKey,
                    includeModelField => ProbeBody(deploymentName, includeModelField),
                    ct, TimeSpan.FromSeconds(ProbeTimeoutSeconds));
            }
            finally
            {
                foundry.Throttle.Release();
            }

            var deploymentStatus = reply.Status;
            if ((int)deploymentStatus is >= 200 and < 300)
            {
                var served = FoundryChatRequest.ServedModel(reply.Body);
                return FoundryChatRequest.ServedModelMatches(deploymentName, served)
                    ? Available(model)
                    : new() { ModelId = model.ModelId, IsAvailable = true, ServedModelMismatch = served };
            }

            if (gemini)
                return Unavailable(model, $"Gemini endpoint unavailable (HTTP {(int)deploymentStatus}).");

            // The deployment route 404'd and the model-inference route did not answer either;
            // which one a resource exposes depends on how the model was provisioned.
            if (reply.UsedFallback)
            {
                return Unavailable(model, deploymentStatus == HttpStatusCode.NotFound
                    ? "Model/deployment not found in this Azure AI Foundry resource."
                    : $"Model endpoint unavailable (HTTP {(int)deploymentStatus}).");
            }

            return Unavailable(model, deploymentStatus switch
            {
                HttpStatusCode.Unauthorized => "Foundry API key is invalid.",
                HttpStatusCode.Forbidden => "Foundry access forbidden for this key/resource.",
                HttpStatusCode.TooManyRequests => "Rate limited while probing. Try again shortly.",
                _ => $"Deployment endpoint unavailable (HTTP {(int)deploymentStatus})."
            });
        }
        catch (OperationCanceledException)
        {
            return Unavailable(model, "Probe timed out.");
        }
        catch (Exception)
        {
            return Unavailable(model, "Probe failed.");
        }
    }

    private static ModelAvailabilityDto Available(ModelDto model) =>
        new() { ModelId = model.ModelId, IsAvailable = true, Reason = null };

    private static ModelAvailabilityDto Unavailable(ModelDto model, string reason, string? suggestion = null) =>
        new() { ModelId = model.ModelId, IsAvailable = false, Reason = reason, Suggestion = suggestion };
}
