using System.Text.RegularExpressions;

namespace PoLocalCompare.Api.Features.Judging;

/// <summary>
/// Deterministic, browser-free checks for runtime rendering defects that a source-only judge
/// easily misses when <c>AiJudge:VisionEnabled</c> is off.
/// </summary>
/// <remarks>
/// Added after a text-only judge tied a genuine CSS 3D cube with a page whose CSS collapsed
/// every face into a flat 2D parallelogram. Only concrete, high-confidence defects are reported;
/// clean documents produce an empty list so healthy duels receive zero extra prompt noise.
/// </remarks>
internal static partial class HtmlSourceDiagnostics
{
    /// <summary>
    /// Inspects <paramref name="html"/> against <paramref name="promptFull"/> and returns short
    /// factual warning lines for verified structural defects (or an empty list when clean).
    /// </summary>
    public static IReadOnlyList<string> Inspect(string? html, string promptFull)
    {
        if (string.IsNullOrWhiteSpace(html)) return [];

        var warnings = new List<string>();
        var asksFor3D = PromptAsksFor3D(promptFull);
        var usesCanvas = html.Contains("<canvas", StringComparison.OrdinalIgnoreCase);
        var usesCss3D = Css3DTransformPattern().IsMatch(html);
        var hasPreserve3D = html.Contains("preserve-3d", StringComparison.OrdinalIgnoreCase);

        if (usesCss3D && !usesCanvas && !hasPreserve3D)
        {
            warnings.Add(
                "STATIC CHECK WARNING: This page uses CSS 3D transforms without declaring 'transform-style: preserve-3d', " +
                "so child faces flatten onto a single 2D plane instead of forming a 3D volume.");
        }

        if (hasPreserve3D && HasFlatteningPropertyOnPreserve3DRule(html))
        {
            warnings.Add(
                "STATIC CHECK WARNING: A CSS rule declaring 'transform-style: preserve-3d' also sets a grouping property " +
                "(overflow: hidden/clip/scroll/auto, filter, backdrop-filter, clip-path, isolation: isolate, mix-blend-mode, " +
                "or opacity < 1), which forces browsers to flatten 3D children back to 2D.");
        }

        if (asksFor3D && usesCss3D && !usesCanvas && !HasMultipleDepthOffsets(html))
        {
            warnings.Add(
                "STATIC CHECK WARNING: Fewer than 2 non-zero 3D depth translations (translateZ/translate3d/matrix3d) were found, " +
                "so faces may be missing or collapsed onto one plane.");
        }

        if (PromptAsksForMotion(promptFull) && !HasAnimationLoop(html))
        {
            warnings.Add(
                "STATIC CHECK WARNING: The request asks for continuous motion, but no @keyframes rule or JS animation loop " +
                "(requestAnimationFrame/setInterval/Element.animate) was found.");
        }

        return warnings;
    }

    private static bool PromptAsksFor3D(string? prompt) =>
        !string.IsNullOrWhiteSpace(prompt)
        && (prompt.Contains("3d", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("cube", StringComparison.OrdinalIgnoreCase));

    private static bool PromptAsksForMotion(string? prompt) =>
        !string.IsNullOrWhiteSpace(prompt)
        && (prompt.Contains("rotat", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("spin", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("continuously", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("animat", StringComparison.OrdinalIgnoreCase));

    private static bool HasAnimationLoop(string html) =>
        html.Contains("@keyframes", StringComparison.OrdinalIgnoreCase)
        || html.Contains("requestAnimationFrame", StringComparison.OrdinalIgnoreCase)
        || html.Contains("setInterval", StringComparison.OrdinalIgnoreCase)
        || html.Contains(".animate(", StringComparison.OrdinalIgnoreCase);

    private static bool HasFlatteningPropertyOnPreserve3DRule(string html)
    {
        var stripped = CssCommentPattern().Replace(html, string.Empty);
        foreach (Match block in CssRuleBlockPattern().Matches(stripped))
        {
            var body = block.Groups[1].Value;
            if (!body.Contains("preserve-3d", StringComparison.OrdinalIgnoreCase)) continue;
            if (FlatteningPropertyPattern().IsMatch(body)) return true;
        }
        return false;
    }

    private static bool HasMultipleDepthOffsets(string html)
    {
        // A script-driven CSS cube often builds faces in a loop with a single template literal,
        // so one translateZ/translate3d/matrix3d call inside <script> is plausible.
        if (html.Contains("<script", StringComparison.OrdinalIgnoreCase)
            && DepthScriptPattern().IsMatch(html))
        {
            return true;
        }

        var count = 0;
        foreach (Match m in NonZeroTranslateZPattern().Matches(html))
        {
            if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var val)
                && Math.Abs(val) > 0.001)
            {
                count++;
                if (count >= 2) return true;
            }
        }
        return Translate3DOrMatrixPattern().Count(html) + count >= 2;
    }

    [GeneratedRegex(@"\b(?:rotate[XY]|rotate3d|translateZ|translate3d|matrix3d)\s*\(", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex Css3DTransformPattern();

    [GeneratedRegex(@"/\*[\s\S]*?\*/", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex CssCommentPattern();

    [GeneratedRegex(@"\{([^{}]*)\}", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex CssRuleBlockPattern();

    [GeneratedRegex(
        @"(?:^|[;\s])(?:overflow(?:-[xy])?\s*:\s*(?:hidden|clip|scroll|auto)|filter\s*:\s*(?!none\b)|backdrop-filter\s*:\s*(?!none\b)|clip-path\s*:\s*(?!none\b)|isolation\s*:\s*isolate|mix-blend-mode\s*:\s*(?!normal\b)|opacity\s*:\s*0(?:\.\d+)?(?:\s*[;}\r\n]|$))",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex FlatteningPropertyPattern();

    [GeneratedRegex(@"\btranslateZ\s*\(\s*([-+]?\d*\.?\d+)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex NonZeroTranslateZPattern();

    [GeneratedRegex(@"\b(?:translate3d|matrix3d)\s*\(", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex Translate3DOrMatrixPattern();

    [GeneratedRegex(@"\b(?:translateZ|translate3d|matrix3d)\s*\(", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex DepthScriptPattern();
}
