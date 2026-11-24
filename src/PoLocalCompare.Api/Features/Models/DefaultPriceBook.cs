using PoLocalCompare.Api.Features.Models;
using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Api.Features.Models;

/// <summary>
/// Default list rates for the Azure AI Foundry catalog, used as the source of truth when
/// a seeded row's <c>InputTokenPricePerMillion</c> / <c>OutputTokenPricePerMillion</c> are
/// null because the deployment was added before its retail price was verified.
/// </summary>
/// <remarks>
/// Prices below are Microsoft Foundry list rates for the named deployment as of 2026-09-02.
/// Update in lockstep with the public price sheet
/// (https://azure.microsoft.com/en-us/pricing/details/ai-foundry/) — a stale number here is a
/// stale number on the ModelCard, the leaderboard's avg-$/duel column and the Arena total.
/// A missing number is worse than a stale one: an unpriced model renders as unknown cost
/// everywhere, so this resolver exists to make sure every seeded deployment has a real rate.
///
/// Match key is the deployment name as it appears on the wire (the model's
/// <c>ApiEndpointRef</c>), normalised to lowercase. The match is suffix-based on purpose:
/// "gpt-5.4-mini-2025-08-07" is a snapshot deployment of the same model and should inherit the
/// snapshot price.
/// </remarks>
public static class DefaultPriceBook
{
    /// <summary>Input USD per million tokens, output USD per million tokens.</summary>
    public record Rate(decimal InputPerMillion, decimal OutputPerMillion);

    private static readonly (string Suffix, Rate Rate)[] Table =
    [
        // OpenAI
        ("gpt-6.1-sol",        new Rate(2.00m,  10.00m)), // 2026-11-23, Global short context
        ("gpt-5.5",            new Rate(5.00m,  40.00m)),
        ("gpt-5.4",            new Rate(2.50m,  15.00m)),
        ("gpt-5.4-mini",       new Rate(0.75m,   4.50m)),
        ("gpt-5.4-nano",       new Rate(0.20m,   1.25m)),
        ("gpt-5-mini",         new Rate(0.40m,   2.40m)),
        ("gpt-5-nano",         new Rate(0.05m,   0.40m)),
        ("gpt-4.1",            new Rate(2.50m,  10.00m)),
        ("gpt-4.1-mini",       new Rate(0.40m,   1.60m)),
        ("gpt-4.1-nano",       new Rate(0.10m,   0.40m)),
        ("gpt-oss-120b",       new Rate(0.15m,   0.60m)),

        // Microsoft first-party
        ("phi-4",              new Rate(0.125m,  0.50m)),
        ("phi-4-mini-instruct",new Rate(0.075m,  0.30m)),

        // Meta on Azure
        ("llama-3.3-70b",      new Rate(0.71m,   0.71m)),

        // Mistral
        ("codestral-2501",     new Rate(0.30m,   0.90m)),

        // xAI
        ("grok-4-1-fast",      new Rate(0.20m,   0.50m)),
        ("grok-4",             new Rate(3.00m,  15.00m)),
        ("grok-4-6",           new Rate(2.00m,  10.00m)),
        ("grok-4.6",           new Rate(2.00m,  10.00m)), // the seeded deployment name uses a dot
        ("grok-4.7",           new Rate(2.00m,   6.00m)), // 2026-11-23, Global

        // Moonshot
        ("kimi-k2",            new Rate(0.60m,   2.50m)),

        // DeepSeek
        ("deepseek-v4",        new Rate(0.14m,   0.28m)),
        ("deepseek-v4-pro",    new Rate(1.74m,   3.48m)), // 2026-11-23, Global
        ("deepseek-v4.1-flash",new Rate(0.30m,   1.20m)), // 2026-11-23, Global
    ];

    /// <summary>
    /// Returns the default list rate for a deployment name, or null if the deployment is
    /// not in the price book (in which case the seed comment warns the cost UI will be empty
    /// and cost challenges will treat the model as zero-spend — same behaviour as today).
    /// </summary>
    public static Rate? Resolve(string? deploymentName)
    {
        if (string.IsNullOrWhiteSpace(deploymentName)) return null;
        var n = deploymentName.ToLowerInvariant();
        // Longest key first. The match is a prefix match, and several keys are prefixes of
        // others — "gpt-5.4" of "gpt-5.4-mini", "phi-4" of "phi-4-mini-instruct", "grok-4" of
        // "grok-4.6" — so first-in-table order priced GPT-5.4 Mini as GPT-5.4 (3.3× too high)
        // and Nano 12.5× too high, and Backfill then overwrote the correct seed rates with
        // those on every startup.
        foreach (var (suffix, rate) in Table.OrderByDescending(t => t.Suffix.Length))
        {
            if (n == suffix || n.StartsWith(suffix, StringComparison.Ordinal) || n.Contains("-" + suffix, StringComparison.Ordinal))
                return rate;
        }
        return null;
    }

    /// <summary>
    /// Reconciles the supplied model list against the price book and returns the rows that
    /// should be re-saved. Two cases write back:
    /// <list type="bullet">
    ///   <item><description>The row has null pricing — fill it from the price book (the original behaviour).</description></item>
    ///   <item><description>The price book has a rate that differs from the stored one — operator updated the table, refresh.</description></item>
    /// </list>
    /// Logs every substitution. Rate rows the price book does not recognise are left alone.
    /// </summary>
    public static IReadOnlyList<Model> Backfill(IEnumerable<Model> models, ILogger logger)
    {
        var updated = new List<Model>();
        foreach (var model in models)
        {
            var rate = Resolve(model.ApiEndpointRef);
            if (rate is null) continue;

            var storedIn = model.InputTokenPricePerMillion;
            var storedOut = model.OutputTokenPricePerMillion;
            if (storedIn == rate.InputPerMillion && storedOut == rate.OutputPerMillion) continue;

            var patched = model.WithPricing(rate.InputPerMillion, rate.OutputPerMillion);
            updated.Add(patched);
            logger.LogInformation(
                "Pricing reconciled for {Model} ({Endpoint}): ${OldIn}/M → ${NewIn}/M in, ${OldOut}/M → ${NewOut}/M out (default price book).",
                model.DisplayName, model.ApiEndpointRef,
                storedIn, rate.InputPerMillion, storedOut, rate.OutputPerMillion);
        }
        return updated;
    }
}
