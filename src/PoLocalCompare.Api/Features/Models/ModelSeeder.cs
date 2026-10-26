using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Models;

/// <summary>
/// Seeds the default set of models into the Model Registry on first run.
/// Only runs when the Models table is completely empty (idempotent).
/// </summary>
public static class ModelSeeder
{
    private static readonly List<Model> DefaultModels =
    [
        // ── Local WebLLM (in-browser) ──────────────────────────────────────
        // 001 (SmolLM2 135M) is retired, not reused: at 135M parameters it does not produce a
        // working page, so every duel it entered was a walkover that taught the leaderboard
        // nothing, and it still cost a 580 MB download. Existing rows are left alone.
        //
        // 010 is the one code-tuned model in the browser tier. General chat models of this
        // size mostly return prose or broken markup for "build a single HTML file"; a coder
        // fine-tune of the same size is the cheapest quality lift the WebGPU tier has.
        new Model(ModelId.From("01SEED0000000000000000002"), "SmolLM2 360M",  ModelType.Local, tdpWatts: 115, webLlmModelId: "SmolLM2-360M-Instruct-q4f32_1-MLC"),
        new Model(ModelId.From("01SEED0000000000000000003"), "SmolLM2 1.7B",  ModelType.Local, tdpWatts: 115, webLlmModelId: "SmolLM2-1.7B-Instruct-q4f16_1-MLC"),
        new Model(ModelId.From("01SEED0000000000000000004"), "Qwen2.5 0.5B",  ModelType.Local, tdpWatts: 115, webLlmModelId: "Qwen2.5-0.5B-Instruct-q4f32_1-MLC"),
        new Model(ModelId.From("01SEED0000000000000000005"), "Qwen3 1.7B",    ModelType.Local, tdpWatts: 115, webLlmModelId: "Qwen3-1.7B-q4f16_1-MLC"),
        new Model(ModelId.From("01SEED0000000000000000006"), "Llama 3.2 1B",  ModelType.Local, tdpWatts: 115, webLlmModelId: "Llama-3.2-1B-Instruct-q4f16_1-MLC"),
        new Model(ModelId.From("01SEED0000000000000000009"), "Gemma 2 2B",    ModelType.Local, tdpWatts: 115, webLlmModelId: "gemma-2-2b-it-q4f16_1-MLC"),
        new Model(ModelId.From("01SEED0000000000000000010"), "Qwen2.5 Coder 1.5B", ModelType.Local, tdpWatts: 115, webLlmModelId: "Qwen2.5-Coder-1.5B-Instruct-q4f16_1-MLC"),

        // Ids 007 (Llama 3.2 3B) and 008 (Phi-3.5 Mini) are retired, not reused. Both failed to
        // load in the browser across three independent runs, each on a dedicated cold browser:
        // Llama 3.2 3B loses the GPU device ("A valid external Instance reference no longer
        // exists") and Phi-3.5 Mini aborts with exit(1). Neither is a leak from an earlier model
        // — Llama 3.2 3B failed running first with 11.3 GB of 12 GB VRAM free. Phi-3.5 Mini's
        // 2.1 GB of q4f32 weights exceed the adapter's 2048 MB maxBufferSize, which explains it;
        // Llama 3.2 3B at 1.7 GB sits under every measured limit and remains unexplained.
        // Their weights are still on disk under wwwroot/models/, so re-adding is a one-line
        // change if a future WebGPU or MLC release fixes them.

        // ── Ollama local service ───────────────────────────────────────────
        // NOTE on the `:latest` tags. These are deliberately unpinned, and that is a real
        // trade-off rather than an oversight: this app does not pull models, it uses whatever
        // the developer already has locally, and pinning to a specific size/quant (say
        // `gemma4:4b-instruct-q4_K_M`) would make the entry resolve to nothing on most
        // machines. The cost is that two developers can run the same seeded "Gemma 4 (Ollama)"
        // against different weights and pool the results into one ELO row. Ollama models seed
        // in Development only, so this never affects the deployed leaderboard — but it does
        // mean local Ollama ratings are not comparable between machines.
        new Model(ModelId.From("01SEED000000000000000000A"), "Gemma 4 (Ollama)",  ModelType.LocalService, tdpWatts: 115, apiEndpointRef: "gemma4:latest"),
        new Model(ModelId.From("01SEED000000000000000000B"), "Qwen 3.5 (Ollama)", ModelType.LocalService, tdpWatts: 115, apiEndpointRef: "qwen3.5:latest"),

        // ── Azure remote models (po-aiservices-shared2) ────────────────────
        // Only deployments that actually exist in the Foundry resource are seeded.
        // Pricing below is Microsoft Foundry list rates as of 2026-08-02 — update in lockstep
        // with the public price sheet (https://azure.microsoft.com/en-us/pricing/details/ai-foundry/)
        // if a deployment is repriced. The cost UI (ModelCard, leaderboard avg-$/duel, Arena total)
        // reads straight from these fields, so a stale number here is a stale number on screen.
        new Model(ModelId.From("01SEED000000000000000000M"), "GPT-5 Nano",     ModelType.Remote, apiEndpointRef: "gpt-5-nano",                    inputTokenPricePerMillion: 0.05m,  outputTokenPricePerMillion: 0.40m),
        new Model(ModelId.From("01SEED000000000000000000N"), "GPT-5.4 Nano",   ModelType.Remote, apiEndpointRef: "gpt-5.4-nano",                  inputTokenPricePerMillion: 0.20m,  outputTokenPricePerMillion: 1.25m),
        new Model(ModelId.From("01SEED000000000000000000P"), "Phi-4",          ModelType.Remote, apiEndpointRef: "phi-4",                         inputTokenPricePerMillion: 0.125m, outputTokenPricePerMillion: 0.50m),
        new Model(ModelId.From("01SEED000000000000000000Q"), "Phi-4 Mini",     ModelType.Remote, apiEndpointRef: "phi-4-mini-instruct",           inputTokenPricePerMillion: 0.075m, outputTokenPricePerMillion: 0.30m),
        new Model(ModelId.From("01SEED000000000000000000R"), "GPT-5.4 Mini",   ModelType.Remote, apiEndpointRef: "gpt-5.4-mini",                  inputTokenPricePerMillion: 0.75m,  outputTokenPricePerMillion: 4.50m),

        // Added 2026-08-13. Pricing is deliberately null: the Foundry list rates for these were
        // not verified at the time they were seeded, and a guessed number here becomes a wrong
        // number on the ModelCard, the leaderboard avg-$/duel and the Arena total. Filling the
        // two pricing arguments in later patches the existing rows automatically — the
        // reconcile loop below backfills any seed entry whose stored row has null prices.
        new Model(ModelId.From("01SEED000000000000000000S"), "GPT-5.4",        ModelType.Remote, apiEndpointRef: "gpt-5.4"),
        new Model(ModelId.From("01SEED000000000000000000T"), "GPT-5 Mini",     ModelType.Remote, apiEndpointRef: "gpt-5-mini"),
        new Model(ModelId.From("01SEED000000000000000000V"), "GPT-4.1 Mini",   ModelType.Remote, apiEndpointRef: "gpt-4.1-mini"),
        new Model(ModelId.From("01SEED000000000000000000W"), "Llama 3.3 70B",  ModelType.Remote, apiEndpointRef: "Llama-3.3-70B-Instruct"),
        new Model(ModelId.From("01SEED000000000000000000X"), "Codestral 2501", ModelType.Remote, apiEndpointRef: "Codestral-2501"),
        new Model(ModelId.From("01SEED000000000000000000Y"), "Kimi K2.7 Code", ModelType.Remote, apiEndpointRef: "Kimi-K2.7-Code"),
        new Model(ModelId.From("01SEED000000000000000000Z"), "Grok 4.1 Fast",  ModelType.Remote, apiEndpointRef: "grok-4-1-fast-non-reasoning"),

        // Added 2026-09-02 to spread the catalog across a real price range. Before this the
        // roster topped out at GPT-5.4 Mini ($0.75/$4.50) — a cheap shelf and a middle shelf
        // with no flagship tier at all, so a duel could never ask whether paying an order of
        // magnitude more buys better HTML. These fill the ends and add two vendors (DeepSeek,
        // and Anthropic once the entries below are unblocked).
        //
        // The point of the selection is *ladders*, not variety for its own sake: OpenAI now runs
        // nano → mini → standard → pro with vendor held constant, which is the only arrangement
        // in which the ELO difference is attributable to tier rather than to house style.
        //
        // Pricing is null for the same reason as the 2026-08-13 batch — the Foundry list rates
        // could not be verified (the retail-prices API returns no rows for AI meters, the model
        // metadata carries `cost: null`, and the price sheet is client-rendered). An unpriced
        // model shows as unknown cost on the ModelCard, the leaderboard's avg-$/duel column and
        // the Arena total. Fill the two pricing arguments and the reconcile loop backfills the
        // stored rows on the next startup.
        new Model(ModelId.From("01SEED000000000000000000C"), "GPT-4.1 Nano",      ModelType.Remote, apiEndpointRef: "gpt-4.1-nano"),
        new Model(ModelId.From("01SEED000000000000000000D"), "GPT-OSS 120B",      ModelType.Remote, apiEndpointRef: "gpt-oss-120b"),
        new Model(ModelId.From("01SEED000000000000000000E"), "DeepSeek V4 Flash", ModelType.Remote, apiEndpointRef: "DeepSeek-V4-Flash"),
        new Model(ModelId.From("01SEED000000000000000000F"), "GPT-5.5",           ModelType.Remote, apiEndpointRef: "gpt-5.5"),
        new Model(ModelId.From("01SEED000000000000000000G"), "Grok 4.6",          ModelType.Remote, apiEndpointRef: "grok-4.6"),

        // GPT-5.4 Pro was the intended flagship and is NOT seeded, though the deployment exists.
        // Pro-tier models are Responses-API only: POST /openai/deployments/gpt-5.4-pro/chat/completions
        // returns {"error":{"message":"The requested operation is unsupported."}} on every
        // api-version, while POST /openai/responses with model=gpt-5.4-pro answers normally.
        // FoundryChatRequest builds the chat/completions path unconditionally, so seeding it
        // would put a permanently-failing model in the picker. GPT-5.5 stands in as the flagship.
        // Adding a Responses-API branch to IRemoteInferenceProxy would unlock gpt-5.4-pro and
        // gpt-5-pro (both already have quota); until then, don't re-add them.
        //
        // Grok 4.6 IS seeded and works, but note it fails the availability probe with
        // "Probe timed out" — it spent 211 reasoning tokens answering "say hi" and took 18.8 s.
        // Phi-4 has always shown the same way. Duels are fine (RemoteTimeoutSeconds is 120).

        // Anthropic: ids H, J and K are RESERVED for claude-haiku-4-5, claude-sonnet-4-6 and
        // claude-opus-5 — deployment is blocked on Azure's InvalidModelProviderData, which
        // demands an industry / organizationName / countryCode declaration the CLI cannot pass.
        // Note claude-sonnet-5 is unavailable regardless: its GlobalStandard quota is 0 on this
        // account, which is why 4-6 is the intended middle rung.
    ];

    /// <summary>
    /// Google models over the Gemini API's OpenAI-compatible endpoint (see
    /// <c>FoundryChatRequest.IsGemini</c>). Seeded only when <c>Gemini:ApiKey</c> is configured,
    /// so a machine without a key gets no permanently-failing entries. 2.5 Flash-Lite is the
    /// cheapest multimodal model on any provider here and can run with thinking off; 3.5
    /// Flash-Lite is its successor, which cannot. Added 2026-09-26.
    /// </summary>
    private static readonly List<Model> GeminiModels =
    [
        new Model(ModelId.From("01SEED0000000000000000011"), "Gemini 2.5 Flash-Lite", ModelType.Remote, apiEndpointRef: "gemini-2.5-flash-lite", inputTokenPricePerMillion: 0.10m, outputTokenPricePerMillion: 0.40m),
        new Model(ModelId.From("01SEED0000000000000000012"), "Gemini 3.5 Flash-Lite", ModelType.Remote, apiEndpointRef: "gemini-3.5-flash-lite", inputTokenPricePerMillion: 0.30m, outputTokenPricePerMillion: 2.50m),
    ];

    public static async Task SeedAsync(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<ModelSeederMarker>>();
        var environment = services.GetRequiredService<IHostEnvironment>();

        using var scope = services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IModelRepository>();

        // Ollama (LocalService) models require a local Ollama daemon that does not exist in the
        // cloud — only seed them in Development so the Production catalog has no dead entries.
        var modelsToSeed = environment.IsDevelopment()
            ? DefaultModels
            : DefaultModels.Where(m => m.ModelType != ModelType.LocalService).ToList();
        if (!string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Gemini:ApiKey"]))
            modelsToSeed = [.. modelsToSeed, .. GeminiModels];

        var existing = (await repo.GetAllAsync()).ToList();

        // De-dupe (Path 3): a one-time manual add in some dev environments left a `Phi-4`
        // (or `Phi-4 Mini`) catalog row next to the seed replica with a non-seed id. Both
        // rows render in the picker, both probe `Deployment reachable`, and the user can pick
        // them on opposite sides — running a duel against the same model twice. Remove the
        // non-seed replica when the seed has a matching display name so the catalog on a
        // running machine converges without requiring `docker compose down -v`.
        var deduped = await DedupeByNameAsync(repo, existing, modelsToSeed, logger);
        if (deduped > 0)
        {
            existing = (await repo.GetAllAsync()).ToList();
        }

        if (existing.Count == 0)
        {
            logger.LogInformation("ModelSeeder: No models found — seeding {Count} default models.", modelsToSeed.Count);

            foreach (var model in modelsToSeed)
            {
                await repo.SaveAsync(model);
                logger.LogInformation("ModelSeeder: Registered '{DisplayName}'.", model.DisplayName);
            }

            logger.LogInformation("ModelSeeder: Seed complete.");
            return;
        }

        logger.LogInformation("ModelSeeder: {Count} models already registered — skipping seed.", existing.Count);

        // Reconcile the existing catalog against the seed list. Two kinds of changes can land
        // here on a machine that has already run:
        //
        //   1. A seed entry that exists in Table Storage but was seeded before its row had a
        //      price. Patch the two pricing columns only — never DisplayName, ELO, or any
        //      other field a user could have changed in the meantime.
        //
        //   2. A seed entry that didn't exist in Table Storage yet (e.g. Phi-4 was added to
        //      the seed list after the user's last `docker compose down -v`). Insert it. New
        //      models start at default ELO/duels; nothing is overwritten because there is
        //      nothing to overwrite.
        //
        // Both paths are idempotent and safe to run on every startup, which is what makes
        // editing the seed list land on a running machine without a data-wiping reset.
        var updated = 0;
        var added = 0;
        foreach (var seed in modelsToSeed)
        {
            var match = existing.FirstOrDefault(e => MatchesSeed(e, seed));
            if (match is null)
            {
                // Path 2: new in the seed list, missing from storage. Use SaveAsync — it
                // swallows 409s (idempotent), so a race with another node adding the same
                // row is harmless.
                await repo.SaveAsync(seed);
                added++;
                logger.LogInformation("ModelSeeder: Added new seed entry '{DisplayName}'.", seed.DisplayName);
                continue;
            }

            if (match.InputTokenPricePerMillion.HasValue && match.OutputTokenPricePerMillion.HasValue) continue;

            // No-op if both target prices are null (e.g. matching a local WebLLM model against
            // an unpriced seed entry). Writing null over null would burn an ETag for no signal
            // and produce a misleading "Backfilled" log line every startup.
            if (!seed.InputTokenPricePerMillion.HasValue && !seed.OutputTokenPricePerMillion.HasValue) continue;

            var priced = match.WithPricing(seed.InputTokenPricePerMillion, seed.OutputTokenPricePerMillion);
            try
            {
                await repo.UpdateAsync(priced);
                updated++;
                logger.LogInformation("ModelSeeder: Backfilled pricing for '{DisplayName}'.", match.DisplayName);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 412)
            {
                // Concurrent edit — a duel write, say, updated the same row in parallel. The
                // pricing backfill is opportunistic, not load-bearing; skip and let the next
                // startup catch it.
                logger.LogInformation("ModelSeeder: Lost optimistic-concurrency race updating '{DisplayName}' — pricing backfill deferred.", match.DisplayName);
            }
        }
        if (updated > 0)
            logger.LogInformation("ModelSeeder: Pricing backfill complete — {Count} model(s) updated.", updated);
        if (added > 0)
            logger.LogInformation("ModelSeeder: Catalog reconciled — {Count} new model(s) added.", added);

        // Default price book — fills rows that were added before their retail rate was known
        // (e.g. the 2026-09-02 catalog expansion shipped with null pricing because the Azure
        // retail-prices API returned no rows for those meters). Without this, the seeded
        // flagship would have empty Cost UI everywhere. The price
        // book is curated manually — see DefaultPriceBook.cs for the table.
        var defaultBackfilled = DefaultPriceBook.Backfill(
            await repo.GetAllAsync(),
            services.GetRequiredService<ILogger<DefaultPriceBookMarker>>());
        foreach (var model in defaultBackfilled)
        {
            try
            {
                await repo.UpdateAsync(model);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 412)
            {
                logger.LogInformation(
                    "ModelSeeder: Lost optimistic-concurrency race backfilling '{DisplayName}' from the default price book — deferred to next startup.",
                    model.DisplayName);
            }
        }
        if (defaultBackfilled.Count > 0)
            logger.LogInformation("ModelSeeder: Default price book backfill — {Count} model(s) updated.", defaultBackfilled.Count);
    }

    /// <summary>
    /// Marker type used as a generic parameter for the seed logger so log scopes from
    /// <see cref="DefaultPriceBook"/> do not attach to the same category as the seeder.
    /// </summary>
    private sealed class DefaultPriceBookMarker;

    /// <summary>
    /// Matches a stored row to a seed entry by deployment name (remote / Ollama) or WebLLM
    /// model id (local). <see cref="Model.ApiEndpointRef"/> and <see cref="Model.WebLlmModelId"/>
    /// are both unique per model in the seed list, so either is a safe join key.
    /// </summary>
    private static bool MatchesSeed(Model existing, Model seed)
    {
        if (!string.IsNullOrWhiteSpace(seed.ApiEndpointRef)
            && string.Equals(existing.ApiEndpointRef, seed.ApiEndpointRef, StringComparison.Ordinal))
            return true;
        if (!string.IsNullOrWhiteSpace(seed.WebLlmModelId)
            && string.Equals(existing.WebLlmModelId, seed.WebLlmModelId, StringComparison.Ordinal))
            return true;
        return false;
    }

    /// <summary>
    /// Remove catalog rows whose ModelId is not in the seed list and whose DisplayName
    /// matches a seed entry by name. The seed replica (the one with the seed id) is kept.
    /// Skips rows whose name is not in the seed list — those are user-registered models and
    /// must outlive a seed reconciliation.
    /// </summary>
    private static async Task<int> DedupeByNameAsync(
        IModelRepository repo,
        IReadOnlyList<Model> existing,
        IReadOnlyList<Model> seedModels,
        ILogger logger)
    {
        var seedIds = new HashSet<ModelId>(seedModels.Select(m => m.ModelId));
        var seedNames = new HashSet<string>(
            seedModels.Select(m => m.DisplayName),
            StringComparer.OrdinalIgnoreCase);

        var removals = existing
            .Where(m => !seedIds.Contains(m.ModelId))
            .Where(m => !string.IsNullOrWhiteSpace(m.DisplayName)
                     && seedNames.Contains(m.DisplayName))
            .ToList();

        foreach (var orphan in removals)
        {
            try
            {
                await repo.DeleteAsync(orphan.ModelId);
                logger.LogInformation("ModelSeeder: Removed duplicate non-seed id '{ModelId}' for '{DisplayName}'.",
                    orphan.ModelId, orphan.DisplayName);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                // Row already gone — fine, the next sweep won't see it.
            }
        }

        return removals.Count;
    }
}

file sealed class ModelSeederMarker { }
