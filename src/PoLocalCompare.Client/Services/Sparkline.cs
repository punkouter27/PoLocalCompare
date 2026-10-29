using System.Runtime.InteropServices.JavaScript;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// [JSImport] binding for <c>wwwroot/js/sparkline.js</c>, the telemetry HUD's tok/s history.
/// </summary>
/// <remarks>
/// The one interop path that hands JS a buffer rather than JSON: the samples cross as a
/// <see cref="JSType.MemoryView"/> over the caller's span, so an update allocates nothing and
/// serialises nothing. Everything else in the client stays on IJSRuntime — its calls carry a
/// selector or a few numbers, where the marshalling cost is noise.
/// </remarks>
public static partial class Sparkline
{
    private const string Module = "sparkline";
    private static Task? _import;

    /// <summary>Loads the module once; later calls return the same task.</summary>
    public static Task EnsureLoadedAsync() =>
        _import ??= JSHost.ImportAsync(Module, "/js/sparkline.js?v=1");

    [JSImport("draw", Module)]
    public static partial void Draw(string canvasId, [JSMarshalAs<JSType.MemoryView>] Span<double> samples);
}
