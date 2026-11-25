using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using PoLocalCompare.Api.Common.Inference;

namespace PoLocalCompare.Api.Common.Rendering;

/// <summary>Config section <c>Rendering</c>: where the vision judge's screenshots come from.</summary>
public sealed class RenderingOptions
{
    public const string SectionName = "Rendering";

    /// <summary>
    /// Base URL of PoLocalCompare.RenderService (the Container App). Empty renders in this process
    /// with local Chromium — development only: the Free (F1) App Service cannot launch a browser.
    /// </summary>
    public string? ServiceUrl { get; set; }

    /// <summary>Shared key sent as <c>X-Render-Key</c>. Key Vault: <c>PoLocalCompare--Rendering--ApiKey</c>.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Ceiling on one remote render. Generous because the container scales to zero: a cold start
    /// pulls the image and launches Chromium. The warm-up at duel start normally absorbs that.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 60;

    public bool IsRemote => !string.IsNullOrWhiteSpace(ServiceUrl);
}

/// <summary>
/// Renders a model's HTML output to PNG frames, so the judge can look at the page instead of
/// reading its source — through PoLocalCompare.RenderService when <see cref="RenderingOptions.ServiceUrl"/>
/// is set (Production), otherwise with <see cref="PlaywrightHtmlRenderer"/> in this process.
/// </summary>
/// <remarks>
/// Either way every failure returns null and the judge reads the source instead: a duel must
/// never go unjudged because a screenshot did not render. The outcome of the last remote render
/// is kept for /diag, which reports it rather than probing — a probe every minute would keep the
/// scale-to-zero container awake around the clock.
/// </remarks>
public sealed class HtmlScreenshotRenderer(
    IOptions<RenderingOptions> options,
    IHttpClientFactory httpClientFactory,
    PlaywrightHtmlRenderer local,
    TimeProvider clock,
    ILogger<HtmlScreenshotRenderer> logger)
{
    public const string HttpClientName = "RenderService";

    /// <summary>Gap between the two frames of an animated page; the judge prompt quotes it.</summary>
    public static readonly TimeSpan FrameGap = TimeSpan.FromMilliseconds(800);

    /// <summary>A warm-up inside this window of the last one is skipped: the container is already up.</summary>
    private static readonly TimeSpan WarmUpInterval = TimeSpan.FromSeconds(60);

    private readonly RenderingOptions _options = options.Value;
    private long _lastWarmUpTicks;

    /// <summary>When the last remote render finished and whether it worked; null before the first.</summary>
    public (DateTimeOffset At, bool Ok, string Detail)? LastRemoteOutcome { get; private set; }

    public bool IsRemote => _options.IsRemote;

    /// <summary>One frame, or null if the page could not be rendered.</summary>
    public async Task<byte[]?> RenderAsync(string? html, CancellationToken cancellationToken) =>
        (await RenderCoreAsync(html, motion: false, cancellationToken))?.Frames[0];

    /// <summary>
    /// Like <see cref="RenderAsync"/>, but a page that looks animated is shot twice,
    /// <see cref="FrameGap"/> apart, so the judge can tell a page that moves from one that was
    /// merely drawn. A static page still returns one frame.
    /// </summary>
    public Task<HtmlRenderEvidence?> RenderFramesAsync(string? html, CancellationToken cancellationToken) =>
        RenderCoreAsync(html, motion: true, cancellationToken);

    /// <summary>
    /// Wakes the render container without waiting for it, so a cold start overlaps the models'
    /// generation instead of delaying the verdict. No-op when rendering locally.
    /// </summary>
    public void WarmUp()
    {
        if (!_options.IsRemote) return;
        var now = clock.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastWarmUpTicks);
        if (now - last < WarmUpInterval.Ticks || Interlocked.CompareExchange(ref _lastWarmUpTicks, now, last) != last)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var response = await httpClientFactory.CreateClient(HttpClientName)
                    .GetAsync(new Uri(BaseUri, "health"));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogInformation("Render service warm-up did not answer: {Reason}", ex.Message);
            }
        });
    }

    private Uri BaseUri => new(_options.ServiceUrl!.TrimEnd('/') + "/");

    private async Task<HtmlRenderEvidence?> RenderCoreAsync(string? html, bool motion, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var request = new RenderRequest(html, InferencePrompt.PreviewWidth, InferencePrompt.PreviewHeight, motion,
            (int)FrameGap.TotalMilliseconds);
        return _options.IsRemote
            ? await RenderRemoteAsync(request, cancellationToken)
            : await local.RenderAsync(request, cancellationToken);
    }

    private async Task<HtmlRenderEvidence?> RenderRemoteAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 5, 300)));
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "render"))
            {
                Content = JsonContent.Create(request),
            };
            message.Headers.Add("X-Render-Key", _options.ApiKey ?? string.Empty);

            using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(message, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                // 422 is the service saying this page would not render (it hung, say): the page's
                // fault, not the service's, so it does not mark the service as failing.
                var pageFault = (int)response.StatusCode == 422;
                Record(pageFault, $"HTTP {(int)response.StatusCode}");
                logger.LogWarning("Render service answered HTTP {Status}; judging source only.", (int)response.StatusCode);
                return null;
            }

            var evidence = await response.Content.ReadFromJsonAsync<HtmlRenderEvidence>(timeout.Token);
            Record(evidence is { Frames.Count: > 0 }, "Rendered.");
            return evidence is { Frames.Count: > 0 } ? evidence : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            Record(false, ex.GetType().Name);
            logger.LogWarning(ex, "Render service call failed; judging source only.");
            return null;
        }
    }

    private void Record(bool ok, string detail) => LastRemoteOutcome = (clock.GetUtcNow(), ok, detail);
}
