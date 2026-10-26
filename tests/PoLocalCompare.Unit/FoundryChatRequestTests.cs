using System.Text.Json;
using PoLocalCompare.Api.Common.Inference;

namespace PoLocalCompare.Unit;

public class FoundryChatRequestTests
{
    [Fact]
    public void Build_ForCodestral_OmitsStreamOptions_StreamingOrNot()
    {
        // Codestral 2501 routes through a strict OpenAI-compatible proxy that rejects
        // stream_options with HTTP 422 (observed live during the 2026-08-13 demo session).
        // The deny-by-default SupportsStreamUsage list keeps Codestral working without a flag
        // trip every time it gets paired in a demo. With stream=false the option is moot, but
        // the body still has to come out well-formed — both halves of that are asserted here.
        var streaming = FoundryChatRequest.Build("Codestral-2501", Array.Empty<object>(), 4096, 0.7, stream: true, includeModelField: false);

        Assert.False(streaming.ContainsKey("stream_options"));
        Assert.True(streaming.ContainsKey("stream"));

        var blocking = FoundryChatRequest.Build("Codestral-2501", Array.Empty<object>(), 4096, 0.7, stream: false, includeModelField: false);

        Assert.False(blocking.ContainsKey("stream_options"));
        Assert.Equal(false, blocking["stream"]);
        Assert.Equal(4096, blocking["max_tokens"]);
    }

    [Fact]
    public void SupportsStreamUsage_IsAnAllowList()
    {
        // Pin the allow-list: Llama went through PoLocalCompare's proxy during the 2026-08-13 demo
        // session and accepted stream_options. Deny-by-default: anything not on the native list
        // (here, an unknown name) must NOT get it — Codestral is pinned by the test above.
        Assert.True(FoundryChatRequest.SupportsStreamUsage("Llama-3.3-70B-Instruct"));
        Assert.False(FoundryChatRequest.SupportsStreamUsage(null));
    }

    /// <summary>
    /// The 2026-10-26 production outage: the text-only judge's deployment is NAMED gpt-4.1-nano
    /// but SERVES gpt-5.4-mini, which rejects max_tokens — so every verdict failed and every duel
    /// stayed Pending. The 400 has to switch that deployment to the reasoning shape, and only
    /// once, or a resend that fails the same way would loop.
    /// </summary>
    [Fact]
    public void TryLearnShape_ReasoningModelBehindAClassicName_SwitchesShapeOnce()
    {
        var deployment = $"classic-name-{Guid.NewGuid():N}";
        const string rejection = """{"error":{"message":"Unsupported parameter: 'max_tokens' is not supported with this model.","type":"invalid_request_error","param":"max_tokens","code":"unsupported_parameter"}}""";
        Assert.False(FoundryChatRequest.IsReasoningModel(deployment));

        Assert.True(FoundryChatRequest.TryLearnShape(deployment, System.Net.HttpStatusCode.BadRequest, rejection));
        Assert.False(FoundryChatRequest.TryLearnShape(deployment, System.Net.HttpStatusCode.BadRequest, rejection));

        var body = FoundryChatRequest.Build(deployment, Array.Empty<object>(), 100, 0.2, stream: false, includeModelField: false);
        Assert.Equal(100, body["max_completion_tokens"]);
        Assert.False(body.ContainsKey("max_tokens"));
        Assert.False(body.ContainsKey("temperature"));
    }

    /// <summary>
    /// What a one-shot call costs is priced by the model the reply says ran — a relabelled
    /// deployment bills at the dearer model's rate — and xAI's separately reported reasoning
    /// tokens are billed as output. The judge's old private copy did neither.
    /// </summary>
    [Fact]
    public void OfCompletion_PricesByServedModel_AndBillsXaiReasoningAsOutput()
    {
        const string relabelled = """{"model":"gpt-5.4-mini-2026-03-17","usage":{"prompt_tokens":1000000,"completion_tokens":0}}""";
        Assert.Equal(0.75, TokenCost.OfCompletion("gpt-4.1-nano", relabelled)!.Value, 6);

        const string grok = """{"model":"grok-4.6","usage":{"prompt_tokens":0,"completion_tokens":1000,"completion_tokens_details":{"reasoning_tokens":3000}}}""";
        Assert.Equal(0.04, TokenCost.OfCompletion("grok-4.6", grok)!.Value, 6);
    }

    [Fact]
    public void Build_ReasoningRequest_UsesCompletionBudgetWithoutTemperature()
    {
        var body = FoundryChatRequest.Build("gpt-5-nano", Array.Empty<object>(), 16_384, 0.7, stream: true, includeModelField: false);

        Assert.Equal(16_384, body["max_completion_tokens"]);
        Assert.False(body.ContainsKey("temperature"));

        // The effort floor differs per deployment and an unsupported value is a 400: GPT-5.5
        // rejects "minimal", which lost it every duel by walkover.
        Assert.Equal("minimal", body["reasoning_effort"]);
        var gpt55 = FoundryChatRequest.Build("gpt-5.5", Array.Empty<object>(), 16_384, 0.7, stream: true, includeModelField: false);
        Assert.Equal("none", gpt55["reasoning_effort"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetCachedBody_ProducesTheSameShapeAsBuild(bool includeModelField)
    {
        // The template cache shipped with the sentinel pre-quoted, so the serialized body
        // carried an *escaped* token the substitution search never matched — every Foundry
        // duel died on "User-prompt sentinel missing from cached body template." before a
        // request went out. It also handed Build a single message object, which dropped the
        // system prompt and made "messages" an object rather than an array. Parsing the body
        // back catches both: a shape defect cannot hide behind a string compare.
        const string system = "You are a \"strict\" HTML generator.\nNo prose.";
        const string user = "Build a stopwatch — \"one screen\", 100ms ticks.\backslash";

        var json = FoundryChatRequest.GetCachedBody(
            "gpt-5-nano", system, user, 8_192, 0.2, stream: true, includeModelField: includeModelField);

        using var parsed = JsonDocument.Parse(json);
        var messages = parsed.RootElement.GetProperty("messages");
        Assert.Equal(JsonValueKind.Array, messages.ValueKind);
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal(system, messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal(user, messages[1].GetProperty("content").GetString());

        if (includeModelField)
        {
            Assert.Equal("gpt-5-nano", parsed.RootElement.GetProperty("model").GetString());
        }
        else
        {
            Assert.False(parsed.RootElement.TryGetProperty("model", out _));
        }
    }

    [Fact]
    public void GetCachedBody_ReusedTemplate_SwapsOnlyTheUserPrompt()
    {
        // Second call for the same deployment is served from the cached prefix/suffix — the
        // path that matters in production, where every duel after the first hits it.
        const string system = "system prompt";
        _ = FoundryChatRequest.GetCachedBody("phi-4", system, "first", 4_096, 0.2, stream: true, includeModelField: false);
        var second = FoundryChatRequest.GetCachedBody("phi-4", system, "second", 4_096, 0.2, stream: true, includeModelField: false);

        using var parsed = JsonDocument.Parse(second);
        Assert.Equal(system, parsed.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("second", parsed.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal(4_096, parsed.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void GetCachedBody_DifferentSystemPrompts_DoNotShareATemplate()
    {
        // The cache key covers everything baked into the prefix; a caller that varies the
        // system prompt (or the budget) must not be served another caller's body.
        var a = FoundryChatRequest.GetCachedBody("phi-4", "prompt A", "u", 4_096, 0.2, stream: false, includeModelField: false);
        var b = FoundryChatRequest.GetCachedBody("phi-4", "prompt B", "u", 4_096, 0.2, stream: false, includeModelField: false);

        Assert.Equal("prompt A", JsonDocument.Parse(a).RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("prompt B", JsonDocument.Parse(b).RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }
}
