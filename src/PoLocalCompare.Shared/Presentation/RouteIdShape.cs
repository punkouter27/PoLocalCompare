namespace PoLocalCompare.Shared.Presentation;

/// <summary>
/// Whether an id taken from a URL is even shaped like one of ours, before it costs a request.
/// </summary>
/// <remarks>
/// Deliberately loose. <c>ModelId</c> and <c>DuelId</c> do not require ULID shape — the seeded
/// models are 25 characters (<c>01SEED0000000000000000002</c>) and must keep round-tripping —
/// so this only rejects what no id can be: punctuation, spaces, or a length nothing mints.
/// <c>/arena/not-a-guid</c> used to send the API a request it could only 404, which logged a
/// console error on every mistyped link before the page said "not found" anyway.
/// </remarks>
public static class RouteIdShape
{
    public static bool IsPlausible(string? text) =>
        text is { Length: >= 20 and <= 32 } && text.All(char.IsAsciiLetterOrDigit);
}
