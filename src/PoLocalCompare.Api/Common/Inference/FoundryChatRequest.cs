using System.Text.Json;

namespace PoLocalCompare.Api.Common.Inference;

/// <summary>
/// Builds Azure AI Foundry / Azure OpenAI chat-completion request bodies that are
/// compatible with both classic chat models and GPT-5 / o-series reasoning models.
///
/// Reasoning models (gpt-5*, o1/o3/o4) reject <c>max_tokens</c> (they require
/// <c>max_completion_tokens</c>) and reject any non-default <c>temperature</c>,
/// returning HTTP 400. Classic models use <c>max_tokens</c> and honour temperature.
/// </summary>
public static class FoundryChatRequest
{
    /// <summary>API version that supports <c>max_completion_tokens</c> for reasoning models.</summary>
    public const string ApiVersion = "2024-12-01-preview";

    /// <summary>
    /// The lowest reasoning effort a deployment accepts. See the note at the call site in
    /// <see cref="Build"/> for why this workload wants the floor rather than the default.
    /// </summary>
    /// <remarks>
    /// The floor is not one value across the family, and an unsupported one is a hard HTTP 400,
    /// not a fallback. It was a single <c>"minimal"</c> constant until GPT-5.5 joined the catalog
    /// and failed every duel with "'reasoning_effort' does not support 'minimal' with this model.
    /// Supported values are: 'none', 'low', 'medium', 'high', and 'xhigh'" — which a tournament
    /// shows as its opponent winning by walkover. The original GPT-5 and the 5.4 family accept
    /// <c>minimal</c>; GPT-5.5 replaced it with <c>none</c>; the o-series never had either.
    /// A new GPT-5 deployment that 400s on this field needs its prefix added here.
    /// </remarks>
    public static string ReasoningEffortFor(string deploymentName)
    {
        var n = deploymentName.ToLowerInvariant();
        if (n.StartsWith("gpt-5.5")) return "none";
        if (n.StartsWith("o1") || n.StartsWith("o3") || n.StartsWith("o4")) return "low";
        return "minimal";
    }

    /// <summary>
    /// Per-deployment chat route. A name that is not a deployment in this resource 404s here
    /// but may still resolve on <see cref="ModelInferenceUrl"/> — every Foundry caller runs
    /// that two-endpoint fallback, so both URLs are built here rather than at each call site.
    /// </summary>
    public static string DeploymentUrl(string endpoint, string deploymentName) =>
        $"{endpoint}/openai/deployments/{deploymentName}/chat/completions?api-version={ApiVersion}";

    /// <summary>
    /// Model-inference chat route — the 404 fallback for <see cref="DeploymentUrl"/>. Requires
    /// the model named in the body, so pair it with <c>Build(..., includeModelField: true)</c>.
    /// </summary>
    public static string ModelInferenceUrl(string endpoint) =>
        $"{endpoint}/models/chat/completions?api-version={ApiVersion}";

    /// <summary>
    /// Per-deployment, per-route JSON body template cache. The first call to
    /// <see cref="GetCachedBody"/> serializes the body once with stable placeholders for the
    /// user prompt and the model field; every later call returns the cached prefix/suffix and
    /// only the placeholder regions are replaced. Cuts the JsonSerializer + Dictionary
    /// allocation per duel side — the only thing that varies per call is the user prompt.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Prefix, string Suffix)> BodyTemplateCache = new(StringComparer.Ordinal);

    // Placeholders are bare tokens, and the form searched for in the serialized body is
    // produced by the serializer itself rather than written out by hand. Two things make a
    // hand-written literal wrong, and the original had both: a token that already carries its
    // own quotes comes out double-escaped, and STJ's default encoder escapes < and > to
    // < / >, so "<<<X>>>" never appears literally in the body. Either one makes the
    // substitution search miss, which is what threw "User-prompt sentinel missing from cached
    // body template." on every Foundry duel. Serializing the token here means the search
    // string is escaped exactly the way the body is, whatever the encoder does.
    private const string UserPromptToken = "<<<USER_PROMPT_SENTINEL>>>";
    private const string ModelFieldToken = "<<<MODEL_SENTINEL>>>";

    private static readonly string UserPromptTokenJson = JsonSerializer.Serialize(UserPromptToken);
    private static readonly string ModelFieldTokenJson = JsonSerializer.Serialize(ModelFieldToken);

    /// <summary>
    /// Returns the ready-to-send JSON body for a (deployment, route, stream) triple, with
    /// <paramref name="userPrompt"/> substituted into the user message slot.
    /// </summary>
    /// <remarks>
    /// The system prompt, max_tokens / max_completion_tokens, stream_options and reasoning
    /// effort are deployment-fixed and live in the cached prefix. Model and user-prompt bytes
    /// are substituted into the cached suffix region. Everything that shapes the template is
    /// in the cache key — route, stream shape, reasoning shape, token budget, temperature and
    /// the system prompt — so a caller that varies any of them cannot be served another
    /// caller's body.
    /// </remarks>
    public static string GetCachedBody(
        string deploymentName,
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        double temperature,
        bool stream,
        bool includeModelField)
    {
        var cacheKey = string.Join(
            '|',
            deploymentName,
            includeModelField ? "mi" : "dep",
            stream ? "s" : "n",
            // Learned at runtime (TryLearnShape), so it must be in the key or a body cached
            // under the classic shape would outlive the lesson.
            IsReasoningModel(deploymentName) ? "r" : "c",
            maxTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            temperature.ToString(System.Globalization.CultureInfo.InvariantCulture),
            systemPrompt.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            systemPrompt.GetHashCode(StringComparison.Ordinal).ToString(System.Globalization.CultureInfo.InvariantCulture));

        var template = BodyTemplateCache.GetOrAdd(cacheKey, _ =>
        {
            // Same message shape as the uncached path: a real two-element array with the
            // system prompt first. Handing Build a single message object instead drops the
            // system prompt and sends "messages" as an object, which Foundry rejects.
            var messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = UserPromptToken },
            };
            var dictionary = Build(deploymentName, messages, maxTokens, temperature, stream, includeModelField: includeModelField);
            if (includeModelField)
            {
                // Force the model field to a token as well, so the cached suffix carries it as
                // a literal the per-call substitution replaces cleanly.
                dictionary["model"] = ModelFieldToken;
            }

            var serialized = JsonSerializer.Serialize(dictionary);
            var idx = serialized.IndexOf(UserPromptTokenJson, StringComparison.Ordinal);
            if (idx < 0) throw new InvalidOperationException("User-prompt sentinel missing from cached body template.");
            var prefix = serialized[..idx];
            var suffix = serialized[(idx + UserPromptTokenJson.Length)..];
            return (prefix, suffix);
        });

        var body = template.Prefix + JsonSerializer.Serialize(userPrompt) + template.Suffix;
        if (includeModelField)
        {
            body = body.Replace(ModelFieldTokenJson, JsonSerializer.Serialize(deploymentName), StringComparison.Ordinal);
        }
        return body;
    }

    /// <summary>Google's OpenAI-compatible Gemini endpoint, used when <c>Gemini:Endpoint</c> is unset.</summary>
    public const string GeminiDefaultEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai";

    /// <summary>
    /// A <c>gemini-*</c> model reference is served by Google's OpenAI-compatible endpoint
    /// (<c>Gemini:Endpoint</c> / <c>Gemini:ApiKey</c>) instead of Foundry. Same SSE shape, same
    /// body builder — only the URL and the auth header differ, so it rides the Remote proxy
    /// rather than getting a ModelType and a proxy of its own.
    /// </summary>
    public static bool IsGemini(string? modelRef) =>
        modelRef is not null && modelRef.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase);

    /// <summary>The single Gemini chat route; the model is named in the body.</summary>
    public static string GeminiUrl(string endpoint) => $"{endpoint}/chat/completions";

    /// <summary>Foundry takes an <c>api-key</c> header; Google's endpoint takes a Bearer token.</summary>
    public static void AddAuth(HttpRequestMessage request, string modelRef, string apiKey)
    {
        if (IsGemini(modelRef))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        else
            request.Headers.Add("api-key", apiKey);
    }

    /// <summary>Endpoint and key for a model reference, from whichever provider serves it.</summary>
    public static (string? Endpoint, string? ApiKey) ResolveProvider(IConfiguration configuration, string? modelRef) =>
        IsGemini(modelRef)
            ? ((configuration["Gemini:Endpoint"] ?? GeminiDefaultEndpoint).TrimEnd('/'), configuration["Gemini:ApiKey"])
            : (configuration["AzureAiFoundry:Endpoint"]?.TrimEnd('/'), configuration["AzureAiFoundry:ApiKey"]);

    /// <summary>
    /// xAI's reasoning Grok deployments — every <c>grok-*</c> that is not explicitly a
    /// <c>non-reasoning</c> build. They are NOT <see cref="IsReasoningModel"/>: they take the
    /// classic <c>max_tokens</c> and <c>temperature</c>, so they need only the effort field.
    /// </summary>
    /// <remarks>
    /// Found on 2026-10-11 from the leaderboard: Grok 4.6's time to first token was 145 s as
    /// a MEDIAN. Every stored result showed 2,700–6,400 reasoning tokens spent before the first
    /// visible character, because this deployment was being sent no effort at all and thought
    /// at its default — the same waste the GPT-5 note in <see cref="Build"/> describes.
    /// </remarks>
    public static bool IsGrokReasoning(string? deploymentName) =>
        deploymentName is not null
        && deploymentName.StartsWith("grok-", StringComparison.OrdinalIgnoreCase)
        && !deploymentName.Contains("non-reasoning", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this provider reports reasoning tokens OUTSIDE <c>completion_tokens</c>. OpenAI
    /// counts them inside it; xAI reports them beside it — a stored Grok 4.6 result has 2,345
    /// completion tokens and 6,417 reasoning tokens, which cannot be a subset. Both bill them
    /// at the output rate, so for these deployments cost has to add them back
    /// (see <see cref="TokenCost"/>).
    /// </summary>
    public static bool ReportsReasoningSeparately(string? deploymentName) =>
        deploymentName is not null && deploymentName.StartsWith("grok-", StringComparison.OrdinalIgnoreCase);

    public static bool IsReasoningModel(string? deploymentName)
    {
        if (string.IsNullOrWhiteSpace(deploymentName)) return false;
        var n = deploymentName.ToLowerInvariant();
        return n.StartsWith("gpt-5")
            || n.StartsWith("o1")
            || n.StartsWith("o3")
            || n.StartsWith("o4")
            || LearnedReasoning.ContainsKey(deploymentName);
    }

    /// <summary>
    /// Deployments whose NAME says classic but whose MODEL is a reasoning one, learned from the
    /// 400 they return. Static for the same reason as the route memo in
    /// <see cref="FoundryInferenceProxy"/>: it is a property of the deployment.
    /// </summary>
    /// <remarks>
    /// Found on 2026-10-26: in po-aiservices-shared2 the deployments named <c>gpt-4.1-nano</c>,
    /// <c>gpt-5-nano</c> and <c>gpt-5.4-nano</c> all serve gpt-5.4-mini, because the
    /// subscription has no quota for the real models. A name is a label an operator typed, not
    /// a contract. The production text-only judge is <c>gpt-4.1-nano</c>; it was sent
    /// <c>max_tokens</c> and <c>temperature</c>, answered "Unsupported parameter: 'max_tokens'",
    /// and every production duel was left Pending.
    /// </remarks>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> LearnedReasoning =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True — once per deployment — when <paramref name="body"/> is the 400 a reasoning model
    /// returns for the classic request shape. The deployment is then treated as reasoning, so a
    /// caller that rebuilds its body and resends gets the shape the model accepts.
    /// </summary>
    public static bool TryLearnShape(string? deploymentName, System.Net.HttpStatusCode status, string? body)
    {
        if (status != System.Net.HttpStatusCode.BadRequest || string.IsNullOrWhiteSpace(deploymentName)
            || string.IsNullOrWhiteSpace(body) || IsReasoningModel(deploymentName))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var error)
                || !error.TryGetProperty("param", out var param) || param.ValueKind != JsonValueKind.String)
                return false;
            return param.GetString() is "max_tokens" or "temperature"
                && LearnedReasoning.TryAdd(deploymentName, 0);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The <c>model</c> a non-streaming completion says served it, or null.</summary>
    public static string? ServedModel(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String
                ? model.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the model a provider reports serving is plausibly the one the deployment is named
    /// for — "gpt-5.4-mini-2026-03-17" is <c>gpt-5.4-mini</c>, "gpt-5-mini-2025-08-07" is not
    /// <c>gpt-5.4</c>. Punctuation and case are ignored; an unknown served model is a match, and
    /// so is "default", which is all the model-inference route says about what it ran.
    /// </summary>
    public static bool ServedModelMatches(string? deploymentName, string? servedModel)
    {
        if (string.IsNullOrWhiteSpace(deploymentName) || string.IsNullOrWhiteSpace(servedModel)
            || servedModel.Equals("default", StringComparison.OrdinalIgnoreCase)) return true;
        static string Squash(string s) => new([.. s.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
        var served = Squash(servedModel.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? servedModel[7..] : servedModel);
        var named = Squash(deploymentName);
        return served.StartsWith(named, StringComparison.Ordinal) || named.StartsWith(served, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a streaming chat-completion request to this deployment may include
    /// <c>stream_options: { include_usage = true }</c>. True for native Azure OpenAI deployments
    /// (the GPT/Phi/Llama/Grok families we route today); false for deployments proxied through
    /// a stricter OpenAI-compatible endpoint (e.g. Codestral 2501 via the Mistral MaaS route)
    /// that reject the extra field with HTTP 422 — see AutoJudgeTests' codestral pin.
    /// </summary>
    /// <remarks>
    /// Deny-by-default: a deployment that does not match the known-native prefix list gets
    /// the lean body. Add a model to the list only after confirming the upstream accepts the
    /// OpenAI streaming-extension shape.
    /// </remarks>
    public static bool SupportsStreamUsage(string? deploymentName)
    {
        if (string.IsNullOrWhiteSpace(deploymentName)) return false;
        var n = deploymentName.ToLowerInvariant();
        // Native Azure OpenAI deployments — accept stream_options without complaint.
        return n.StartsWith("gpt-")
            || n.StartsWith("o1")
            || n.StartsWith("o3")
            || n.StartsWith("o4")
            || n.StartsWith("phi-")
            || n.StartsWith("llama-")
            || n.StartsWith("llama_")
            || n.StartsWith("grok-")
            // Google documents stream_options.include_usage on its OpenAI-compatible endpoint.
            || n.StartsWith("gemini-");
    }

    /// <summary>
    /// Builds a request-body dictionary ready for <c>JsonSerializer.Serialize</c>.
    /// For reasoning models, <paramref name="temperature"/> is omitted and the token
    /// budget is sent as <c>max_completion_tokens</c>.
    /// </summary>
    /// <param name="includeModelField">
    /// True for the <c>/models/chat/completions</c> inference endpoint (which requires a
    /// top-level <c>model</c> field); false for the per-deployment endpoint.
    /// </param>
    public static Dictionary<string, object?> Build(
        string deploymentName,
        object messages,
        int maxTokens,
        double temperature,
        bool stream,
        bool includeModelField)
    {
        var body = new Dictionary<string, object?>
        {
            ["messages"] = messages,
            ["stream"] = stream,
        };

        if (stream && SupportsStreamUsage(deploymentName))
        {
            // Codestral 2501 (and any other strict OpenAI-compatible proxy) rejects this with
            // HTTP 422 — "Extra inputs are not permitted" on stream_options.include_usage. Only
            // native Azure OpenAI deployments accept the OpenAI streaming-extension shape; see
            // SupportsStreamUsage for the deny-by-default list.
            body["stream_options"] = new { include_usage = true };
        }

        if (includeModelField)
        {
            body["model"] = deploymentName;
        }

        if (IsReasoningModel(deploymentName))
        {
            body["max_completion_tokens"] = maxTokens;
            // temperature intentionally omitted — reasoning models only allow the default.

            // Reasoning effort was previously left at the service default, which meant every
            // GPT-5-family duel spent thousands of reasoning tokens before emitting a single
            // visible character — billed at output rates, and paid on the clock the TokenRace
            // is measuring. That is close to pure waste for this workload: the task is "return
            // an HTML document", the output format is fixed, and there is no multi-step problem
            // to think through. So ask for the lowest effort each deployment allows — which
            // differs across the family; see ReasoningEffortFor.
            //
            // Deployments that predate the parameter ignore an unknown field rather than
            // rejecting it; a value a deployment does not support, however, is rejected.
            body["reasoning_effort"] = ReasoningEffortFor(deploymentName);
        }
        else
        {
            body["max_tokens"] = maxTokens;
            body["temperature"] = temperature;
        }

        if (IsGrokReasoning(deploymentName))
        {
            // The lowest effort xAI's reasoning models accept. See IsGrokReasoning.
            body["reasoning_effort"] = "low";
        }

        if (IsGemini(deploymentName))
        {
            // Gemini thinks by default, for the same waste the GPT-5 note above describes.
            // Google's compatibility layer accepts "none" on 2.5 models only; 3.x cannot turn
            // thinking off and takes "minimal" as its floor.
            body["reasoning_effort"] = deploymentName.StartsWith("gemini-2.5", StringComparison.OrdinalIgnoreCase)
                ? "none"
                : "minimal";
        }

        return body;
    }
}
