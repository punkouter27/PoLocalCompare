using System.Globalization;
using System.Text.RegularExpressions;

namespace PoLocalCompare.Api.Features.Models;

/// <summary>
/// Reads the model list out of the vendored <c>web-llm.js</c> — the <c>prebuiltAppConfig</c>
/// the browser worker resolves every model against.
/// </summary>
/// <remarks>
/// Same parse as <c>scripts/plan-webllm-artifacts.py</c>, and for the same reason: the worker
/// looks a model up in <c>prebuiltAppConfig</c> to find its <c>model_lib</c> wasm, so an id
/// with no entry there cannot load no matter what the catalog says. Discovery lists only what
/// this returns, which is how a runtime-added browser model is guaranteed to be runnable.
/// </remarks>
public static partial class WebLlmBundleCatalog
{
    /// <summary>One <c>model_list</c> entry.</summary>
    public sealed record Entry(string ModelId, string? HubRepo, double? VramRequiredMb, bool LowResource);

    /// <summary>
    /// A Git LFS pointer is ~130 bytes. Anything this small is not the bundle, and reading it
    /// as one would silently report "no models" instead of the real problem.
    /// </summary>
    public const int MinimumBundleBytes = 64 * 1024;

    public static IReadOnlyList<Entry> Parse(string bundle)
    {
        var lines = bundle.Split('\n');
        var entries = new List<Entry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < lines.Length; i++)
        {
            var id = ModelIdLine().Match(lines[i]);
            if (!id.Success || !seen.Add(id.Groups[1].Value)) continue;

            // The entry's `model:` URL sits just above model_id and the size hints just below —
            // the same window the Python planner reads, but cut short at the next entry's
            // `model:` line, so an entry with no vram hint cannot borrow its neighbour's. The
            // start is pinned to this entry's own `model:` line for the mirror-image reason.
            var start = i;
            for (var j = i - 1; j >= Math.Max(0, i - 4); j--)
            {
                if (!HubRepoLine().IsMatch(lines[j])) continue;
                start = j;
                break;
            }
            var end = Math.Min(lines.Length, i + 12);
            for (var j = i + 1; j < end; j++)
            {
                if (!ModelIdLine().IsMatch(lines[j])) continue;
                end = Math.Max(i + 1, j - 1);
                break;
            }
            var window = string.Join('\n', lines[start..end]);
            var repo = HubRepoLine().Match(window);
            var vram = VramLine().Match(window);

            entries.Add(new Entry(
                id.Groups[1].Value,
                repo.Success ? repo.Groups[1].Value : null,
                vram.Success && double.TryParse(vram.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mb) ? mb : null,
                LowResourceLine().IsMatch(window)));
        }

        return entries;
    }

    /// <summary>
    /// "Qwen2.5-0.5B-Instruct-q4f32_1-MLC" → "Qwen2.5 0.5B Instruct (q4f32)". The quantisation
    /// stays in the name because the bundle ships several of the same model, and the catalog
    /// refuses two models with one display name.
    /// </summary>
    public static string SuggestName(string webLlmModelId)
    {
        var name = webLlmModelId.EndsWith("-MLC", StringComparison.OrdinalIgnoreCase)
            ? webLlmModelId[..^4]
            : webLlmModelId;

        var quant = Quantisation().Match(name);
        if (quant.Success) name = name.Remove(quant.Index, quant.Length);

        name = name.Replace('-', ' ').Trim();
        return quant.Success ? $"{name} ({quant.Groups[1].Value})" : name;
    }

    /// <summary>"llama3.2:3b" → "llama3.2 3b (Ollama)". The suffix keeps it apart from a browser build of the same model.</summary>
    public static string SuggestOllamaName(string tag) =>
        $"{tag.Replace(":latest", "", StringComparison.OrdinalIgnoreCase).Replace(':', ' ')} (Ollama)";

    [GeneratedRegex("""model_id:\s*"([^"]+)"\s*,""")]
    private static partial Regex ModelIdLine();

    [GeneratedRegex("""model:\s*"https://huggingface\.co/([^"]+?)/?"\s*,""")]
    private static partial Regex HubRepoLine();

    [GeneratedRegex("""vram_required_MB:\s*([0-9.]+)""")]
    private static partial Regex VramLine();

    [GeneratedRegex("""low_resource_required:\s*true""")]
    private static partial Regex LowResourceLine();

    [GeneratedRegex("""-(q\d+f\d+)(?:_\d+)?""")]
    private static partial Regex Quantisation();
}
