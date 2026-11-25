namespace PoLocalCompare.Rendering;

/// <summary>
/// What the vision judge sees of one page: one frame, or two <see cref="RenderRequest.FrameGapMs"/>
/// apart for a page that looks animated, plus what clicking its buttons did. Also the JSON body
/// <c>POST /render</c> returns (the frames travel as base64).
/// </summary>
public sealed record HtmlRenderEvidence(
    IReadOnlyList<byte[]> Frames,
    string InteractionSummary,
    byte[]? InteractionScreenshot);

/// <summary>One page to render. Also the JSON body of <c>POST /render</c>.</summary>
/// <param name="Html">The model's raw output: untrusted, run with every network request aborted.</param>
/// <param name="Width">Viewport width in CSS pixels (the Arena preview frame).</param>
/// <param name="Height">Viewport height in CSS pixels.</param>
/// <param name="Motion">Shoot an animated-looking page twice, so the judge can tell whether it moves.</param>
/// <param name="FrameGapMs">Gap between those two frames; the judge prompt quotes the same number.</param>
public sealed record RenderRequest(string Html, int Width, int Height, bool Motion, int FrameGapMs)
{
    /// <summary>Largest page accepted. Generated pages run to tens of KB; this only stops abuse.</summary>
    public const int MaxHtmlChars = 1_000_000;

    /// <summary>Null when the request can be rendered, otherwise why not.</summary>
    public string? Problem() =>
        string.IsNullOrWhiteSpace(Html) ? "Html is empty."
        : Html.Length > MaxHtmlChars ? $"Html is over {MaxHtmlChars:N0} characters."
        : Width is < 100 or > 1920 || Height is < 100 or > 1920 ? "Width and Height must be 100-1920."
        : FrameGapMs is < 0 or > 3000 ? "FrameGapMs must be 0-3000."
        : null;
}
