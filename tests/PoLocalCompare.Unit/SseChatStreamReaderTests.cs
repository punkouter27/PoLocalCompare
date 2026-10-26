using System.Diagnostics;
using System.Text;
using PoLocalCompare.Api.Common.Inference;
using PoLocalCompare.Api.Features.Duels;
using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Unit;

/// <summary>
/// Pins the SSE dialect every remote proxy shares. This is the one piece of the inference path
/// with no HTTP of its own — it takes an already-connected response — so it is reachable from
/// the tier CI actually runs.
/// </summary>
/// <remarks>
/// It went untested through the refactor that collapsed the three proxies onto it, and both
/// bugs it shipped with were invisible from outside: the frames parsed to nothing and the
/// reader reported no error, so a duel came back HTTP 200 with zero tokens and was recorded as
/// "Inference completed without output" — a failure that reads exactly like a model refusing to
/// answer. Every assertion below is anchored to real Azure OpenAI frame shapes (captured from
/// gpt-4.1-mini on 2026-09-03), sibling `content_filter_results` objects and all, because the
/// shape is the whole point: a parser that only handles the minimal textbook frame passes its
/// tests and still returns nothing in production.
/// </remarks>
public class SseChatStreamReaderTests
{
    // ── Real frame shapes ─────────────────────────────────────────────────────

    /// <summary>The leading frame Azure sends before any choice: prompt filter results only.</summary>
    private const string PromptFilterFrame =
        """data: {"choices":[],"created":0,"id":"","model":"","object":"","prompt_filter_results":[{"prompt_index":0,"content_filter_results":{"hate":{"filtered":false,"severity":"safe"}}}]}""";

    /// <summary>Role frame: a delta with an empty content string and no visible token.</summary>
    private const string RoleFrame =
        """data: {"choices":[{"content_filter_results":{},"delta":{"content":"","refusal":null,"role":"assistant"},"finish_reason":null,"index":0}],"object":"chat.completion.chunk","usage":null}""";

    /// <summary>The parameterised frames are serialised rather than interpolated into a raw
    /// string: these payloads are mostly braces, and hand-written escaping is the kind of detail
    /// that turns a failing assertion into a puzzle about the test's own quoting.</summary>
    private static string Frame(object payload) =>
        "data: " + System.Text.Json.JsonSerializer.Serialize(payload);

    private static string ContentFrame(string content) => Frame(new
    {
        choices = new object[]
        {
            new
            {
                content_filter_results = new { protected_material_code = new { detected = false, filtered = false } },
                delta = new { content },
                finish_reason = (string?)null,
                index = 0,
            },
        },
        @object = "chat.completion.chunk",
        usage = (object?)null,
    });

    private static string FinishFrame(string finishReason) => Frame(new
    {
        choices = new object[]
        {
            new
            {
                content_filter_results = new { },
                delta = new { },
                finish_reason = finishReason,
                index = 0,
            },
        },
        @object = "chat.completion.chunk",
        usage = (object?)null,
    });

    private static string UsageFrame(int promptTokens, int completionTokens, int reasoningTokens) => Frame(new
    {
        choices = Array.Empty<object>(),
        @object = "chat.completion.chunk",
        usage = new
        {
            completion_tokens = completionTokens,
            completion_tokens_details = new { reasoning_tokens = reasoningTokens },
            prompt_tokens = promptTokens,
            total_tokens = promptTokens + completionTokens,
        },
    });

    private const string DoneFrame = "data: [DONE]";

    private static string Sse(params string[] frames) =>
        string.Concat(frames.Select(f => f + "\n\n"));

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadInto_AccumulatesContentDeltas_FromRealAzureFrames()
    {
        var sse = Sse(
            PromptFilterFrame,
            RoleFrame,
            ContentFrame("<html>"),
            ContentFrame("<body>hi</body>"),
            ContentFrame("</html>"),
            FinishFrame("stop"),
            UsageFrame(promptTokens: 10, completionTokens: 3, reasoningTokens: 0),
            DoneFrame);

        var result = await ReadAsync(sse);

        Assert.False(result.IsFailure);
        Assert.Null(result.FailureReason);
        Assert.Equal("<html><body>hi</body></html>", result.HtmlOutputRaw);
        Assert.Equal("stop", result.FinishReason);
        Assert.False(result.WasTruncated);
    }

    [Fact]
    public async Task ReadInto_PrefersProviderUsage_OverCountedChunks()
    {
        // Chunk count and the provider's completion_tokens are different numbers on purpose:
        // the assertion is meaningless if they agree by accident.
        var sse = Sse(
            ContentFrame("a"), ContentFrame("b"), ContentFrame("c"),
            FinishFrame("stop"),
            UsageFrame(promptTokens: 41, completionTokens: 99, reasoningTokens: 17),
            DoneFrame);

        var result = await ReadAsync(sse);

        Assert.Equal(99, result.TokenCount);
        Assert.Equal(41, result.PromptTokenCount);
        Assert.Equal(17, result.ReasoningTokenCount);
    }

    [Fact]
    public async Task ReadInto_FallsBackToChunkCount_WhenProviderSendsNoUsage()
    {
        // Codestral and the other strict OpenAI-compatible proxies reject stream_options, so
        // no usage frame arrives and the count has to come from the stream itself. The role
        // frame's empty-string delta counts as a chunk, which is why this is 4 and not 3.
        var sse = Sse(
            RoleFrame,
            ContentFrame("a"), ContentFrame("b"), ContentFrame("c"),
            FinishFrame("stop"),
            DoneFrame);

        var result = await ReadAsync(sse);

        Assert.Equal(4, result.TokenCount);
        Assert.Null(result.PromptTokenCount);
        Assert.Equal("abc", result.HtmlOutputRaw);
    }

    [Fact]
    public async Task ReadInto_MarksTruncated_OnLengthFinishReason()
    {
        var sse = Sse(ContentFrame("<html>"), FinishFrame("length"), DoneFrame);

        var result = await ReadAsync(sse);

        Assert.True(result.WasTruncated);
        Assert.Equal("length", result.FinishReason);
        Assert.False(result.IsFailure); // partial output is still output
    }

    // ── The bug that made every remote duel come back empty ───────────────────

    [Fact]
    public async Task ReadInto_ReadsContentInsideDelta_NotABareContentProperty()
    {
        // The regression that shipped: the parser walked property names looking for `content`
        // but never actually descended into `delta`, so the content branch was unreachable and
        // every frame read as null. A single content frame is enough to catch it.
        var result = await ReadAsync(Sse(ContentFrame("hello"), DoneFrame));

        Assert.Equal("hello", result.HtmlOutputRaw);
        Assert.False(result.IsFailure);
    }

    [Fact]
    public async Task ReadInto_IgnoresContentKeysInSiblingObjects()
    {
        // The mirror-image failure: descending on a bare `content` name picks up whatever a
        // provider puts beside `delta`. Both objects here carry one, and `delta.content` is
        // the last of the three, so a first-match scan gets it wrong.
        const string frame =
            """data: {"choices":[{"content_filter_results":{"content":"FILTER-NOISE"},"logprobs":{"content":"LOGPROB-NOISE"},"delta":{"content":"real"},"index":0}]}""";

        var result = await ReadAsync(frame + "\n\n" + DoneFrame + "\n\n");

        Assert.Equal("real", result.HtmlOutputRaw);
    }

    [Fact]
    public async Task ReadInto_SkipsFramesWithNoChoices()
    {
        // The prompt-filter and usage frames both carry "choices":[]. Neither is content, and
        // neither may abort the read — the real content arrives after the first one.
        var result = await ReadAsync(Sse(PromptFilterFrame, ContentFrame("after"), DoneFrame));

        Assert.Equal("after", result.HtmlOutputRaw);
    }

    // ── The bug hiding behind it: buffer management across reads ──────────────

    [Theory]
    [InlineData(1)]    // pathological: one byte per read
    [InlineData(7)]    // splits mid-JSON and mid-`data: ` prefix
    public async Task ReadInto_IsExact_WhenFramesSplitAcrossReads(int chunkSize)
    {
        // Frames do not arrive whole. The reader has to hold a partial line across reads and
        // resume where it left off — the previous implementation rescanned from byte zero on
        // every read, so each frame was parsed again for every subsequent read and the output
        // came out duplicated. Distinct payloads make a duplicate impossible to miss.
        var sse = Sse(
            PromptFilterFrame,
            RoleFrame,
            ContentFrame("<html>"),
            ContentFrame("<h1>one</h1>"),
            ContentFrame("<h1>two</h1>"),
            ContentFrame("</html>"),
            FinishFrame("stop"),
            DoneFrame);

        var result = await ReadAsync(sse, chunkSize);

        Assert.Equal("<html><h1>one</h1><h1>two</h1></html>", result.HtmlOutputRaw);
        Assert.Equal("stop", result.FinishReason);
    }

    [Fact]
    public async Task ReadInto_HandlesStreamsAndFramesLargerThanTheBuffer()
    {
        // The 32 KB buffer was a hard ceiling: once full it read into a zero-length span, which
        // returns 0 and is indistinguishable from EOF, so long documents ended mid-sentence
        // with no error at all. Both halves are exercised — a total well past the buffer, and
        // one single frame that cannot fit in it.
        var oneBigFrame = new string('x', 40_000);
        var frames = new List<string> { ContentFrame(oneBigFrame) };
        for (var i = 0; i < 40; i++)
            frames.Add(ContentFrame(new string('y', 2_000)));
        frames.Add(FinishFrame("stop"));
        frames.Add(DoneFrame);

        var result = await ReadAsync(Sse([.. frames]), chunkSize: 8_192);

        Assert.Equal(40_000 + (40 * 2_000), result.HtmlOutputRaw.Length);
        Assert.StartsWith(oneBigFrame, result.HtmlOutputRaw, StringComparison.Ordinal);
        Assert.False(result.IsFailure);
    }

    // ── Empty output is a failure, and says why ───────────────────────────────

    [Fact]
    public async Task ReadInto_FailsWithFinishReason_WhenNoVisibleTokenArrives()
    {
        // A content filter stops the model before it emits anything. There is no output to
        // judge, and the reason is the only thing that distinguishes this from a crash.
        var sse = Sse(RoleFrame, FinishFrame("content_filter"), DoneFrame);

        var result = await ReadAsync(sse);

        Assert.True(result.IsFailure);
        Assert.Contains("content_filter", result.FailureReason);
    }

    [Fact]
    public async Task ReadInto_FailsWithoutFinishReason_WhenStreamEndsEmpty()
    {
        var result = await ReadAsync(Sse(PromptFilterFrame, DoneFrame));

        Assert.True(result.IsFailure);
        Assert.Equal("Inference completed without output.", result.FailureReason);
    }

    [Fact]
    public async Task ReadInto_SkipsMalformedFrames_WithoutAbortingTheStream()
    {
        // A truncated or non-JSON frame is survivable: the stream after it is still good, and
        // dropping the whole duel over one bad frame loses output that did arrive.
        var sse = Sse(
            ContentFrame("good "),
            """data: {"choices":[{"delta":{"content":"unterminated""",
            "data: not json at all",
            ContentFrame("still good"),
            FinishFrame("stop"),
            DoneFrame);

        var result = await ReadAsync(sse);

        Assert.Equal("good still good", result.HtmlOutputRaw);
        Assert.False(result.IsFailure);
    }

    [Fact]
    public async Task ReadInto_StopsAtDoneMarker_AndIgnoresTrailingBytes()
    {
        var sse = Sse(ContentFrame("kept"), DoneFrame, ContentFrame("after done"));

        var result = await ReadAsync(sse);

        Assert.Equal("kept", result.HtmlOutputRaw);
    }

    [Fact]
    public async Task ReadInto_ReportsCancellation_AsAFailureNotAnEmptyResult()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = new DuelResult(DuelId.New(), ModelId.From("01SEED000000000000000000V"));
        using var response = ResponseFor(Sse(ContentFrame("x"), DoneFrame), chunkSize: 1);

        var error = await SseChatStreamReader.ReadIntoAsync(
            response, result, Stopwatch.StartNew(), warmUpMs: 0,
            (_, _, _) => Task.CompletedTask, cts.Token);

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.True(result.IsFailure);
        Assert.Equal("Inference cancelled (timeout or user abort).", result.FailureReason);
    }

    // ── Progress callbacks ────────────────────────────────────────────────────

    [Fact]
    public async Task ReadInto_RaisesFirstProgressCallbackImmediately()
    {
        // The Arena shows nothing until the first callback lands, so the first token must not
        // wait out the 500 ms throttle window.
        var callbacks = 0;
        var result = new DuelResult(DuelId.New(), ModelId.From("01SEED000000000000000000V"));
        using var response = ResponseFor(Sse(ContentFrame("first"), DoneFrame));

        await SseChatStreamReader.ReadIntoAsync(
            response, result, Stopwatch.StartNew(), warmUpMs: 0,
            (_, _, _) => { callbacks++; return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(1, callbacks);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static async Task<DuelResult> ReadAsync(string sse, int chunkSize = int.MaxValue)
    {
        var result = new DuelResult(DuelId.New(), ModelId.From("01SEED000000000000000000V"));
        using var response = ResponseFor(sse, chunkSize);

        var error = await SseChatStreamReader.ReadIntoAsync(
            response, result, Stopwatch.StartNew(), warmUpMs: 0,
            (_, _, _) => Task.CompletedTask, CancellationToken.None);

        Assert.Null(error);
        return result;
    }

    private static HttpResponseMessage ResponseFor(string sse, int chunkSize = int.MaxValue) =>
        new(System.Net.HttpStatusCode.OK)
        {
            Content = new StreamContent(new ChunkedStream(Encoding.UTF8.GetBytes(sse), chunkSize)),
        };

    /// <summary>
    /// A stream that hands back at most <c>chunkSize</c> bytes per read, so a test can put a
    /// frame boundary anywhere it likes.
    /// </summary>
    /// <remarks>
    /// A plain <see cref="MemoryStream"/> is useless here: it satisfies every read in one go,
    /// which is exactly the case the buggy buffer handling got right. Network reads split
    /// wherever the socket happens to split, and that is the case that was broken.
    /// </remarks>
    private sealed class ChunkedStream(byte[] data, int chunkSize) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, chunkSize), data.Length - _position);
            if (n <= 0) return 0;
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var n = Math.Min(Math.Min(buffer.Length, chunkSize), data.Length - _position);
            if (n <= 0) return ValueTask.FromResult(0);
            data.AsSpan(_position, n).CopyTo(buffer.Span);
            _position += n;
            return ValueTask.FromResult(n);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
