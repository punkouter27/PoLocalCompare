using Microsoft.Extensions.Caching.Hybrid;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Models;

/// <summary>
/// Registers a model picked from <see cref="DiscoverModelsHandler"/>'s list.
/// </summary>
/// <remarks>
/// Separate from <c>POST /api/models</c> on purpose. That endpoint takes any
/// <c>WebLlmModelId</c> it is given, which is how a typo becomes a browser model that stalls at
/// <c>Initializing</c> forever. This one re-checks the id against the live discovery list —
/// the bundle's <c>prebuiltAppConfig</c> or the daemon's pulled tags — so the only models it
/// can create are ones that will actually load.
/// </remarks>
public sealed class AddDiscoveredModelHandler(
    DiscoverModelsHandler discovery,
    IModelRepository modelRepository,
    ListOllamaModelsHandler ollamaModels,
    RegisterModelHandler register,
    IWebHostEnvironment environment,
    HybridCache cache)
{
    /// <summary>
    /// Browser models are costed like the seeded ones: the TDP of the reference GPU. It is a
    /// required field on a <c>Local</c> model and there is nothing better to put in it.
    /// </summary>
    private const double DefaultBrowserTdpWatts = 115;

    /// <exception cref="ArgumentException">The id is not a current discovery candidate.</exception>
    /// <exception cref="InvalidOperationException">That build, or a model with that display name, is already registered.</exception>
    public async Task<ModelDto> HandleAsync(AddDiscoveredModelRequest request, CancellationToken ct = default)
    {
        var id = request.Id?.Trim() ?? string.Empty;
        RegisterModelCommand command;

        if (request.Kind == DiscoveredModelKind.Browser)
        {
            var bundle = await discovery.ReadBundleAsync(ct)
                ?? throw new ArgumentException("The WebLLM bundle is missing or is a Git LFS pointer, so no browser model can run.");
            var entry = bundle.FirstOrDefault(e => string.Equals(e.ModelId, id, StringComparison.Ordinal))
                ?? throw new ArgumentException($"'{id}' has no prebuiltAppConfig entry in the vendored WebLLM bundle.");

            command = new RegisterModelCommand(
                NameOrSuggested(request.DisplayName, WebLlmBundleCatalog.SuggestName(entry.ModelId)),
                ModelType.Local,
                DefaultBrowserTdpWatts,
                entry.ModelId,
                ApiEndpointRef: null,
                InputTokenPricePerMillion: null,
                OutputTokenPricePerMillion: null);
        }
        else
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Test"))
                throw new ArgumentException("Ollama models are Development-only.");

            var tags = await ollamaModels.HandleAsync(ct);
            var tag = tags.FirstOrDefault(t => string.Equals(t, id, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Ollama does not report '{id}' as pulled.");

            command = new RegisterModelCommand(
                NameOrSuggested(request.DisplayName, WebLlmBundleCatalog.SuggestOllamaName(tag)),
                ModelType.LocalService,
                TdpWatts: null,
                WebLlmModelId: null,
                ApiEndpointRef: tag,
                InputTokenPricePerMillion: null,
                OutputTokenPricePerMillion: null);
        }

        // RegisterModelHandler only refuses a duplicate *name*, so renaming would otherwise let
        // the same build into the catalog twice — two rows that split one model's record.
        var registered = await modelRepository.GetAllAsync();
        if (registered.Any(m =>
                (command.WebLlmModelId is not null && string.Equals(m.WebLlmModelId, command.WebLlmModelId, StringComparison.OrdinalIgnoreCase)) ||
                (command.ModelType == ModelType.LocalService && m.ModelType == ModelType.LocalService &&
                 string.Equals(m.ApiEndpointRef, command.ApiEndpointRef, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException($"'{id}' is already in the catalog.");

        var dto = await register.HandleAsync(command);

        // The leaderboard and profiles list every catalog row, and they are cached under this
        // tag for 30 s; without this the new model is missing from them until it expires.
        await cache.RemoveByTagAsync(CacheTags.Leaderboard, ct);
        return dto;
    }

    private static string NameOrSuggested(string? requested, string suggested) =>
        string.IsNullOrWhiteSpace(requested) ? suggested : requested.Trim();
}
