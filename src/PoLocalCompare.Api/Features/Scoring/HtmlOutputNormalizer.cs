using System.Text.RegularExpressions;

namespace PoLocalCompare.Api.Features.Scoring;

public static class HtmlOutputNormalizer
{
    private static readonly Regex FencedBlockRegex = new(
        @"^\s*```(?:html)?\s*(?<body>[\s\S]*?)\s*```\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FirstFenceRegex = new(
        @"```(?:html)?\s*(?<body>[\s\S]*?)\s*```",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A reasoning model's scratchpad. Qwen3 on WebLLM emits one (an empty one even with
    // thinking switched off), and rendered in the preview it is a paragraph of plain text
    // above the page.
    private static readonly Regex ThinkBlockRegex = new(
        @"^\s*<think>[\s\S]*?</think>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var trimmed = ThinkBlockRegex.Replace(raw, string.Empty).Trim();

        var fencedBlock = FencedBlockRegex.Match(trimmed);
        if (fencedBlock.Success)
        {
            var body = fencedBlock.Groups["body"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(body))
            {
                return body;
            }
        }

        var firstFence = FirstFenceRegex.Match(trimmed);
        if (firstFence.Success)
        {
            var body = firstFence.Groups["body"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(body))
            {
                return body;
            }
        }

        return trimmed;
    }
}
