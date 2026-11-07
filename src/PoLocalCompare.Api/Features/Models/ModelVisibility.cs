using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Models;

internal static class ModelVisibility
{
    /// <summary>
    /// Drops retired seeds (<see cref="ModelSeeder.RetiredIds"/>) everywhere, and Ollama-backed
    /// models outside Development. Ollama needs a local daemon that does not exist in the cloud,
    /// so the hosted catalog would otherwise advertise dead entries — the same reason
    /// <see cref="ModelSeeder"/> only seeds them in Development.
    /// </summary>
    public static List<ModelDto> Filter(IEnumerable<ModelDto> models, IWebHostEnvironment environment) =>
        models
            .Where(model => !ModelSeeder.RetiredIds.Contains(model.ModelId))
            .Where(model => environment.IsDevelopment() || model.ModelType != ModelType.LocalService)
            .ToList();
}
