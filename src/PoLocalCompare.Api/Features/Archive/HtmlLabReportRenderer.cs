// GoF: Template Method — HTML string builder defines report skeleton
using System.Text;
using System.Text.RegularExpressions;

namespace PoLocalCompare.Api.Features.Archive;

/// <summary>
/// Renders a self-contained HTML Lab Report for a completed duel. Static: the body is a pure
/// string transform with no state and no dependencies, and it had exactly one implementation
/// and no test double behind the interface it used to sit behind.
/// </summary>
public static class HtmlLabReportRenderer
{
    public static string Render(Duel duel, IEnumerable<DuelResult> results, IEnumerable<EloRecord> eloHistory)
    {
        var resultList = results.ToList();
        var eloList = eloHistory.ToList();

        var leftResult = resultList.FirstOrDefault(r => r.ModelId == duel.LeftModelId);
        var rightResult = resultList.FirstOrDefault(r => r.ModelId == duel.RightModelId);

        var leftElo = eloList.Where(e => e.ModelId == duel.LeftModelId).OrderByDescending(e => e.RecordedAt).FirstOrDefault();
        var rightElo = eloList.Where(e => e.ModelId == duel.RightModelId).OrderByDescending(e => e.RecordedAt).FirstOrDefault();

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  <title>Lab Report — {EscapeHtml(duel.DuelId)}</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    body { background: #000; color: #e5e5e5; font-family: 'Segoe UI', sans-serif; margin: 0; padding: 24px; }");
        sb.AppendLine("    h1 { color: #fff; border-bottom: 1px solid #333; padding-bottom: 12px; }");
        sb.AppendLine("    h2 { color: #22c55e; margin-top: 32px; }");
        sb.AppendLine("    h3 { color: #a3a3a3; }");
        sb.AppendLine("    .header-meta { color: #737373; font-size: 0.875rem; margin-bottom: 24px; }");
        sb.AppendLine("    .verdict-badge { display: inline-block; padding: 4px 12px; border-radius: 4px; font-weight: 600; }");
        sb.AppendLine("    .verdict-left { background: #22c55e; color: #000; }");
        sb.AppendLine("    .verdict-right { background: #22c55e; color: #000; }");
        sb.AppendLine("    .verdict-pending { background: #eab308; color: #000; }");
        sb.AppendLine("    table { width: 100%; border-collapse: collapse; margin: 16px 0; }");
        sb.AppendLine("    th { background: #111; color: #a3a3a3; text-align: left; padding: 8px 12px; border-bottom: 1px solid #333; }");
        sb.AppendLine("    td { padding: 8px 12px; border-bottom: 1px solid #1a1a1a; }");
        sb.AppendLine("    .prompt-box { background: #111; border-left: 4px solid #22c55e; padding: 16px; margin: 16px 0; white-space: pre-wrap; font-family: monospace; font-size: 0.875rem; word-break: break-word; }");
        sb.AppendLine("    .source-panel { background: #0a0a0a; border: 1px solid #333; padding: 16px; margin: 16px 0; }");
        sb.AppendLine("    .source-panel pre { overflow-x: auto; white-space: pre-wrap; word-break: break-word; font-size: 0.8rem; color: #d4d4d4; margin: 0; }");
        sb.AppendLine("    .winner-label { color: #22c55e; font-weight: 600; }");
        sb.AppendLine("    .loser-label { color: #737373; }");
        sb.AppendLine("    .section { margin-top: 40px; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        // Header
        sb.AppendLine("  <h1>🧪 Lab Report</h1>");
        sb.AppendLine("  <div class=\"header-meta\">");
        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"    <div>Duel ID: <code>{EscapeHtml(duel.DuelId)}</code></div>");
        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"    <div>Date: {duel.StartedAt:yyyy-MM-dd HH:mm:ss} UTC</div>");
        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"    <div>Completed: {(duel.CompletedAt.HasValue ? duel.CompletedAt.Value.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " UTC" : "N/A")}</div>");
        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"    <div>Verdict: <span class=\"verdict-badge verdict-{duel.Verdict.ToString().ToLowerInvariant()}\">{duel.Verdict}</span></div>");
        sb.AppendLine("  </div>");

        // Prompt
        sb.AppendLine("  <div class=\"section\">");
        sb.AppendLine("    <h2>Prompt</h2>");
        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"    <div class=\"prompt-box\">{EscapeHtml(duel.PromptText)}</div>");
        sb.AppendLine("  </div>");

        // Telemetry table
        sb.AppendLine("  <div class=\"section\">");
        sb.AppendLine("    <h2>Telemetry</h2>");
        sb.AppendLine("    <table>");
        sb.AppendLine("      <thead><tr>");
        sb.AppendLine("        <th>Metric</th>");
        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"        <th>{EscapeHtml(duel.LeftModelId)}{(duel.WinnerModelId == duel.LeftModelId ? " 🏆" : "")}</th>");
        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"        <th>{EscapeHtml(duel.RightModelId)}{(duel.WinnerModelId == duel.RightModelId ? " 🏆" : "")}</th>");
        sb.AppendLine("      </tr></thead>");
        sb.AppendLine("      <tbody>");
        AppendTelemetryRow(sb, "Token Count", leftResult?.TokenCount.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—", rightResult?.TokenCount.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—");
        AppendTelemetryRow(sb, "Token Velocity (tok/s)", leftResult?.TokenVelocity.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "—", rightResult?.TokenVelocity.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "—");
        AppendTelemetryRow(sb, "Warm-up (ms)", leftResult?.WarmUpDurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—", rightResult?.WarmUpDurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—");
        AppendTelemetryRow(sb, "Generation (ms)", leftResult?.GenerationDurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—", rightResult?.GenerationDurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—");
        AppendTelemetryRow(sb, "Total (ms)", leftResult?.TotalDurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—", rightResult?.TotalDurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—");
        AppendTelemetryRow(sb, "Output Size (bytes)", leftResult?.HtmlOutputSizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—", rightResult?.HtmlOutputSizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—");
        AppendTelemetryRow(sb, "Character Density", leftResult?.CharacterDensityRatio.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) ?? "—", rightResult?.CharacterDensityRatio.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) ?? "—");
        AppendTelemetryRow(sb, "Energy (Wh)", leftResult?.EnergyWh?.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) ?? "—", rightResult?.EnergyWh?.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) ?? "—");
        AppendTelemetryRow(sb, "Energy Cost (USD)", leftResult?.EnergyCostUsd?.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) ?? "—", rightResult?.EnergyCostUsd?.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) ?? "—");
        AppendTelemetryRow(sb, "API Cost (USD)", leftResult?.ApiCostUsd?.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) ?? "—", rightResult?.ApiCostUsd?.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) ?? "—");
        AppendTelemetryRow(sb, "Failure", leftResult?.IsFailure == true ? leftResult.FailureReason ?? "Yes" : "No", rightResult?.IsFailure == true ? rightResult.FailureReason ?? "Yes" : "No");
        sb.AppendLine("      </tbody>");
        sb.AppendLine("    </table>");
        sb.AppendLine("  </div>");

        // ELO Shifts
        sb.AppendLine("  <div class=\"section\">");
        sb.AppendLine("    <h2>ELO Shifts</h2>");
        sb.AppendLine("    <table>");
        sb.AppendLine("      <thead><tr><th>Model</th><th>Before</th><th>After</th><th>Shift</th></tr></thead>");
        sb.AppendLine("      <tbody>");
        if (leftElo is not null)
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"      <tr><td>{EscapeHtml(duel.LeftModelId)}</td><td>{leftElo.EloBefore:F1}</td><td>{leftElo.EloAfter:F1}</td><td>{(leftElo.EloShift >= 0 ? "+" : "")}{leftElo.EloShift:F1}</td></tr>");
        if (rightElo is not null)
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"      <tr><td>{EscapeHtml(duel.RightModelId)}</td><td>{rightElo.EloBefore:F1}</td><td>{rightElo.EloAfter:F1}</td><td>{(rightElo.EloShift >= 0 ? "+" : "")}{rightElo.EloShift:F1}</td></tr>");
        sb.AppendLine("      </tbody>");
        sb.AppendLine("    </table>");
        sb.AppendLine("  </div>");

        // Source Code Panels (winner first)
        sb.AppendLine("  <div class=\"section\">");
        sb.AppendLine("    <h2>Source Code Output</h2>");

        var orderedResults = new List<(ModelId modelId, DuelResult? result, bool isWinner)>
        {
            (duel.WinnerModelId == duel.LeftModelId ? duel.LeftModelId : duel.RightModelId,
             duel.WinnerModelId == duel.LeftModelId ? leftResult : rightResult, true),
            (duel.WinnerModelId == duel.LeftModelId ? duel.RightModelId : duel.LeftModelId,
             duel.WinnerModelId == duel.LeftModelId ? rightResult : leftResult, false),
        };

        foreach (var (modelId, result, isWinner) in orderedResults)
        {
            var label = isWinner ? "Winner" : "Loser";
            var labelClass = isWinner ? "winner-label" : "loser-label";
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"    <h3><span class=\"{labelClass}\">{label}</span>: {EscapeHtml(modelId)}</h3>");
            sb.AppendLine("    <div class=\"source-panel\">");
            // Strip <script> tags and on* attributes from output (sanitise)
            var sanitised = SanitiseHtml(result?.HtmlOutputRaw ?? "(no output)");
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"      <pre>{EscapeHtml(sanitised)}</pre>");
            sb.AppendLine("    </div>");
        }

        sb.AppendLine("  </div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    private static void AppendTelemetryRow(StringBuilder sb, string metric, string left, string right)
    {
        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"      <tr><td>{EscapeHtml(metric)}</td><td>{EscapeHtml(left)}</td><td>{EscapeHtml(right)}</td></tr>");
    }

    private static string EscapeHtml(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        return input
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");
    }

    private static string SanitiseHtml(string html)
    {
        // Remove <script> tags and content
        html = Regex.Replace(html, @"<script\b[^<]*(?:(?!<\/script>)<[^<]*)*<\/script>", string.Empty, RegexOptions.IgnoreCase);
        // Remove on* event attributes
        html = Regex.Replace(html, @"\s+on\w+\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]+)", string.Empty, RegexOptions.IgnoreCase);
        return html;
    }
}
