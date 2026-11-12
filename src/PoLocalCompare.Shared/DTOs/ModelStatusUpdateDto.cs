using PoLocalCompare.Shared.Ids;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Shared.DTOs;

/// <summary>SignalR message shape for processing-phase status updates.</summary>
public sealed class ModelStatusUpdateDto
{
    public DuelId DuelId { get; init; }
    public ModelId ModelId { get; init; }
    /// <summary>"Left" or "Right" — explicit side assignment, not heuristic.</summary>
    public string Side { get; init; } = string.Empty;
    public DuelStatus Status { get; init; }
    public long ElapsedMs { get; init; }
    public int TokenCount { get; init; }
    public double? TokenVelocity { get; init; }
    /// <summary>Highest tok/s seen so far this run.</summary>
    public double? PeakTokenVelocity { get; init; }
    /// <summary>Milliseconds from request start until the first token arrived (latency / warm-up).</summary>
    public long? WarmUpMs { get; init; }
    /// <summary>True when no new tokens have arrived for >2s during Generating.</summary>
    public bool IsStalled { get; init; }
    // --- HTML content stats (computed from streaming output) ---
    /// <summary>Number of opening HTML tags seen so far.</summary>
    public int? HtmlTagCount { get; init; }
    /// <summary>Current nesting depth of open/unclosed tags.</summary>
    public int? OpenTagDepth { get; init; }
    /// <summary>Number of CSS rule blocks ({) seen inside <style> sections.</summary>
    public int? StyleRuleCount { get; init; }
    /// <summary>0–1 repetition score; >0.3 = possible looping.</summary>
    public double? RepetitionScore { get; init; }
    // --- Local model only ---
    /// <summary>WebLLM prefill throughput in tok/s (prompt processing speed).</summary>
    public double? PrefillSpeedTps { get; init; }
    /// <summary>Whether the model weights were already cached in memory (fast warm-up).</summary>
    public bool? CacheHit { get; init; }
    /// <summary>Optional sub-phase detail, e.g. WebLLM loading progress text.</summary>
    public string? Detail { get; init; }
    /// <summary>
    /// HTML generated so far, for the live preview: a partial document every ~25 tokens while
    /// <see cref="DuelStatus.Generating"/>, then the complete output on the
    /// <see cref="DuelStatus.Done"/> frame — so a side that finishes first can be read while the
    /// other is still going, without waiting for the duel-wide completion message.
    /// </summary>
    public string? HtmlPreview { get; init; }
}
