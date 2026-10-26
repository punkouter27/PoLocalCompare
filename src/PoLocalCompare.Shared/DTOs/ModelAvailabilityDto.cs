using PoLocalCompare.Shared.Ids;
namespace PoLocalCompare.Shared.DTOs;

public sealed class ModelAvailabilityDto
{
    public ModelId ModelId { get; init; }
    public bool IsAvailable { get; init; }
    public string? Reason { get; init; }

    /// <summary>
    /// One-sentence fix-it hint surfaced when <see cref="IsAvailable"/> is false — for Ollama
    /// this is the <c>ollama pull</c> or <c>ollama cp</c> command that would resolve the
    /// mismatch. The picker renders it directly under the unavailable card.
    /// </summary>
    public string? Suggestion { get; init; }

    /// <summary>
    /// Set when the probe's reply names a different model from the one this entry is named for
    /// — the deployment is a relabelled copy. Only <c>/diag</c> shows it.
    /// </summary>
    public string? ServedModelMismatch { get; init; }
}

