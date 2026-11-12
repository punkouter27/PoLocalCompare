using PoLocalCompare.Api.Features.Leaderboard;
using PoLocalCompare.Api.Features.Models;
using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Unit;

/// <summary>
/// The read-side projections added over the ELO history — the Bradley–Terry uncertainty band,
/// the profile's highlights — and the bundle parse that gates runtime model discovery.
/// </summary>
public class LeaderboardInsightTests
{
    private static readonly ModelId A = ModelId.From("model-a");
    private static readonly ModelId B = ModelId.From("model-b");
    private static readonly ModelId Idle = ModelId.From("model-idle");

    private static EloRecord Row(ModelId model, ModelId opponent, string outcome, string duel,
        double before = 1200, double after = 1200, double opponentBefore = 1200, int minute = 0) => new()
        {
            ModelId = model,
            OpponentModelId = opponent,
            Outcome = outcome,
            DuelId = DuelId.From(duel),
            EloBefore = before,
            EloAfter = after,
            EloShift = after - before,
            OpponentEloBefore = opponentBefore,
            RecordedAt = DateTimeOffset.UnixEpoch.AddMinutes(minute),
        };

    [Fact]
    public void BradleyTerry_RanksByEvidence_NarrowsWithGames_AndCountsEachDuelOnce()
    {
        // A 20–5 record against B, written the way RecordVerdictHandler writes it: one row in
        // each model's partition per duel. Reading both halves must not double the evidence.
        var history = new List<EloRecord>();
        for (var i = 0; i < 25; i++)
        {
            var aWon = i < 20;
            history.Add(Row(A, B, aWon ? "Win" : "Loss", $"duel-{i}"));
            history.Add(Row(B, A, aWon ? "Loss" : "Win", $"duel-{i}"));
        }

        var fit = BradleyTerry.Fit([A, B, Idle], BradleyTerry.GamesFrom(history));

        Assert.Equal(25, fit[A].Games);
        Assert.True(fit[A].Rating > fit[B].Rating);

        // Unregularised, 20–5 is 400·log10(4) ≈ 241 points apart; the prior only shrinks it.
        var gap = fit[A].Rating - fit[B].Rating;
        Assert.InRange(gap, 150, 241);

        // A model with no duels sits at the start with at least the prior's width: nothing has
        // placed it against the models that have played.
        Assert.Equal(1200, fit[Idle].Rating, precision: 1);
        Assert.True(fit[Idle].Interval95 >= 1.96 * BradleyTerry.PriorSdElo);
        Assert.True(fit[Idle].IsProvisional);

        Assert.True(fit[A].Interval95 < fit[Idle].Interval95 / 2, $"A ±{fit[A].Interval95} vs idle ±{fit[Idle].Interval95}");
        Assert.False(fit[A].IsProvisional);
    }

    [Fact]
    public void ProfileHighlights_JudgesUpsetsOnPreDuelRatings_AndDrawsBreakStreaks()
    {
        var history = new List<EloRecord>
        {
            Row(A, B, "Win",  "d1", before: 1200, after: 1220, opponentBefore: 1300, minute: 1), // upset +100
            Row(A, B, "Win",  "d2", before: 1220, after: 1250, opponentBefore: 1520, minute: 2), // upset +300
            Row(A, B, "Win",  "d3", before: 1250, after: 1255, opponentBefore: 1100, minute: 3), // expected win
            Row(A, B, "Draw", "d4", before: 1255, after: 1252, opponentBefore: 1200, minute: 4),
            Row(A, B, "Win",  "d5", before: 1252, after: 1260, opponentBefore: 1240, minute: 5),
        };

        // Shuffled on purpose: the repository hands rows back newest-first.
        var highlights = ProfileHighlights.Compute(history.AsEnumerable().Reverse().ToList(), _ => "B");

        Assert.Equal(1260, highlights.PeakElo);
        Assert.Equal(DuelId.From("d5"), highlights.PeakDuelId);
        Assert.Equal([300.0, 100.0], highlights.Upsets.Select(u => u.RatingGap));
        Assert.Equal(3, highlights.LongestWinStreak);
    }

    [Fact]
    public void WebLlmBundle_ParsesEachEntryInIsolation_AndSuggestsDistinctNames()
    {
        // Shaped like prebuiltAppConfig. The second entry has no size hints, so a window that
        // runs on past its own braces would hand it the first entry's — or the third's.
        const string bundle = """
            model_list: [
                {
                    model: "https://huggingface.co/mlc-ai/Qwen2.5-0.5B-Instruct-q4f32_1-MLC",
                    model_id: "Qwen2.5-0.5B-Instruct-q4f32_1-MLC",
                    model_lib: modelLibURLPrefix + modelVersion + "/Qwen2-0.5B.wasm",
                    low_resource_required: true,
                    vram_required_MB: 1060.2,
                },
                {
                    model: "https://huggingface.co/mlc-ai/Llama-3.2-1B-Instruct-q4f16_1-MLC",
                    model_id: "Llama-3.2-1B-Instruct-q4f16_1-MLC",
                    model_lib: modelLibURLPrefix + modelVersion + "/Llama.wasm",
                },
                {
                    model: "https://huggingface.co/mlc-ai/SmolLM2-135M-Instruct-q0f32-MLC",
                    model_id: "SmolLM2-135M-Instruct-q0f32-MLC",
                    low_resource_required: true,
                    vram_required_MB: 359.7,
                },
            ]
            """;

        var entries = WebLlmBundleCatalog.Parse(bundle);

        Assert.Equal(3, entries.Count);
        Assert.Equal(new WebLlmBundleCatalog.Entry("Qwen2.5-0.5B-Instruct-q4f32_1-MLC", "mlc-ai/Qwen2.5-0.5B-Instruct-q4f32_1-MLC", 1060.2, true), entries[0]);
        Assert.Equal(new WebLlmBundleCatalog.Entry("Llama-3.2-1B-Instruct-q4f16_1-MLC", "mlc-ai/Llama-3.2-1B-Instruct-q4f16_1-MLC", null, false), entries[1]);

        Assert.Equal("Qwen2.5 0.5B Instruct (q4f32)", WebLlmBundleCatalog.SuggestName(entries[0].ModelId));
        Assert.Equal("SmolLM2 135M Instruct (q0f32)", WebLlmBundleCatalog.SuggestName(entries[2].ModelId));
        Assert.Equal("llama3.2 3b (Ollama)", WebLlmBundleCatalog.SuggestOllamaName("llama3.2:3b"));
    }
}
