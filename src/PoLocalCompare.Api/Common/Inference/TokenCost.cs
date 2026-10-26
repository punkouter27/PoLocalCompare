namespace PoLocalCompare.Api.Common.Inference;

/// <summary>
/// The one place a token count becomes dollars.
/// </summary>
/// <remarks>
/// It was an inline expression in <see cref="FoundryInferenceProxy"/> until 2026-10-11, when
/// re-pricing stored results needed the same arithmetic: 39 of 111 results recorded between
/// 2026-09-23 and 2026-09-26 carried costs from the price-book bug (GPT-5.4 Nano billed at
/// GPT-5.4's rate, twelve times too high), and a repair that restated the formula could have
/// drifted from the one that prices new duels.
/// </remarks>
public static class TokenCost
{
    /// <summary>
    /// Cost of one call, or null when the model has no rate at all — an unpriced model is
    /// "unknown", which the leaderboard treats differently from "free".
    /// </summary>
    public static double? Compute(
        decimal? inputPricePerMillion,
        decimal? outputPricePerMillion,
        int? promptTokens,
        int outputTokens,
        int? separateReasoningTokens = null)
    {
        if (!inputPricePerMillion.HasValue && !outputPricePerMillion.HasValue) return null;

        // Reasoning tokens are billed as output. Most providers already count them inside
        // outputTokens; pass them here only for one that reports them beside it
        // (FoundryChatRequest.ReportsReasoningSeparately), or they are billed and not counted.
        var billedOutput = outputTokens + (separateReasoningTokens ?? 0);

        var inputCost = ((promptTokens ?? 0) / 1_000_000.0) * (double)(inputPricePerMillion ?? 0);
        var outputCost = (billedOutput / 1_000_000.0) * (double)(outputPricePerMillion ?? 0);
        return inputCost + outputCost;
    }

    /// <summary>
    /// Cost of one non-streaming completion, from its own <c>usage</c> block. Priced by the model
    /// the reply says served it when the price book knows that model, else by the deployment
    /// name; null when neither is priced or the reply carries no usage — unknown, not free.
    /// </summary>
    /// <remarks>
    /// One copy for every one-shot caller (judge, checklist, prompt refine, scouting report).
    /// The judge's private copy priced by deployment name and never added xAI's separately
    /// reported reasoning tokens, so a Grok judge or a relabelled deployment was under-billed
    /// in <c>SpendGuard</c>.
    /// </remarks>
    public static double? OfCompletion(string deployment, string payload)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("usage", out var usage)
                || usage.ValueKind != System.Text.Json.JsonValueKind.Object)
                return null;

            if ((DefaultPriceBook.Resolve(FoundryChatRequest.ServedModel(payload)) ?? DefaultPriceBook.Resolve(deployment))
                is not { } rate)
                return null;

            int Read(string name) => usage.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;
            int? reasoning = usage.TryGetProperty("completion_tokens_details", out var details)
                && details.ValueKind == System.Text.Json.JsonValueKind.Object
                && details.TryGetProperty("reasoning_tokens", out var r) && r.TryGetInt32(out var rt)
                    ? rt
                    : null;

            return Compute(rate.InputPerMillion, rate.OutputPerMillion, Read("prompt_tokens"), Read("completion_tokens"),
                FoundryChatRequest.ReportsReasoningSeparately(deployment) ? reasoning : null);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
