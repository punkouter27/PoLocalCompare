using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PoLocalCompare.Api.Common.Rendering;
using PoLocalCompare.Api.Features.Judging;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Unit;

/// <summary>
/// Pins the source-only 3D/runtime defect diagnostics and mutual-Tie tiebreak escalation added
/// after a text-only judge tied a real CSS 3D cube with a 2D-flattened parallelogram.
/// </summary>
public class FoundryDuelJudgeTests
{
    private const string CubePrompt =
        "Build a self-contained single HTML file showing a 3D cube rotating continuously on two axes. Use CSS 3D transforms.";

    [Fact]
    public void SourceOnlyJudging_FlagsFlattenedCss3DAndEscalatesMutualTie()
    {
        const string validCube = """
            <style>
              .scene { perspective: 700px; }
              .cube { transform-style: preserve-3d; animation: spin 6s linear infinite; }
              .front { transform: translateZ(60px); }
              .back { transform: rotateY(180deg) translateZ(60px); }
              @keyframes spin { from { transform: rotateX(0) rotateY(0); } to { transform: rotateX(360deg) rotateY(360deg); } }
            </style>
            <div class="scene"><div class="cube"><div class="front"></div><div class="back"></div></div></div>
            """;
        Assert.Empty(HtmlSourceDiagnostics.Inspect(validCube, CubePrompt));

        const string missingPreserve3D = """
            <style>
              .cube { animation: spin 6s linear infinite; }
              .front { transform: translateZ(60px); }
              .back { transform: rotateY(180deg) translateZ(60px); }
              @keyframes spin { from { transform: rotateX(0) rotateY(0); } to { transform: rotateX(360deg) rotateY(360deg); } }
            </style>
            """;
        Assert.Contains(
            HtmlSourceDiagnostics.Inspect(missingPreserve3D, CubePrompt),
            w => w.Contains("preserve-3d", StringComparison.OrdinalIgnoreCase));

        const string overflowFlattened = """
            <style>
              .cube { transform-style: preserve-3d; overflow: hidden; animation: spin 6s linear infinite; }
              .front { transform: translateZ(60px); }
              .back { transform: rotateY(180deg) translateZ(60px); }
              @keyframes spin { from { transform: rotateX(0) rotateY(0); } to { transform: rotateX(360deg) rotateY(360deg); } }
            </style>
            """;
        Assert.Contains(
            HtmlSourceDiagnostics.Inspect(overflowFlattened, CubePrompt),
            w => w.Contains("flatten", StringComparison.OrdinalIgnoreCase));

        var tie = new JudgeDecision(DuelVerdict.Tie, "Equivalent.");
        var win = new JudgeDecision(DuelVerdict.Right, "Right is 3D.");
        Assert.True(FoundryDuelJudge.IsSourceOnlyMutualTie(useVision: false, tie, tie));
        Assert.False(FoundryDuelJudge.IsSourceOnlyMutualTie(useVision: true, tie, tie));
        Assert.False(FoundryDuelJudge.IsSourceOnlyMutualTie(useVision: false, tie, win));
        Assert.False(FoundryDuelJudge.IsSourceOnlyMutualTie(useVision: false, win, win));

        var judge = new FoundryDuelJudge(
            new HttpClient(),
            new ConfigurationBuilder().Build(),
            Options.Create(new AutoJudgeOptions { MaxOutputChars = 40_000 }),
            new HtmlScreenshotRenderer(NullLogger<HtmlScreenshotRenderer>.Instance),
            null!,
            NullLogger<FoundryDuelJudge>.Instance);

        var block = judge.DocumentBlock(
            new JudgedPage("<style>.cube { transform: rotateX(20deg); }</style>"),
            "A",
            evidence: null,
            CubePrompt,
            "DELIM");
        Assert.Contains("STATIC CHECK WARNING", block);
    }
}
