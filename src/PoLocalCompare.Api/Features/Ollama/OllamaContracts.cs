using System.Text.Json.Serialization;

namespace PoLocalCompare.Api.Features.Ollama;

/// <summary>Request body for <c>POST /api/ollama/benchmark</c>.</summary>
public sealed record OllamaBenchmarkRequest(string ModelName, string Prompt);

// ── Ollama daemon wire formats ────────────────────────────────────────────────
// Internal to the slice; these mirror Ollama's own JSON, not anything this API exposes.

internal sealed record OllamaTagsResponse(
    [property: JsonPropertyName("models")] List<OllamaTagsModel>? Models);

internal sealed record OllamaTagsModel(
    [property: JsonPropertyName("name")] string Name);

/// <summary><c>/api/chat</c> chunk — <c>message.content</c> carries each token.</summary>
internal sealed record OllamaChatChunk(
    [property: JsonPropertyName("message")] OllamaChatMessage? Message,
    [property: JsonPropertyName("done")] bool Done,
    [property: JsonPropertyName("eval_count")] int EvalCount,
    [property: JsonPropertyName("load_duration")] long LoadDuration,
    [property: JsonPropertyName("eval_duration")] long EvalDuration,
    [property: JsonPropertyName("prompt_eval_duration")] long PromptEvalDuration);

internal sealed record OllamaChatMessage(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("content")] string? Content);
