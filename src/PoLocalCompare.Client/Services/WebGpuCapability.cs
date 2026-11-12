using Microsoft.JSInterop;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// Single-flight WebGPU capability probe for the Home model picker.
///
/// Without this, every page that mounts a model picker re-requests a GPU adapter and
/// device even when a sister component on the same page has already done so — both
/// round-trips are async, both run on first render, and neither cache. The probe
/// itself costs ~one adapter request + one device request, which is small but
/// not free, and the result never changes during the lifetime of the tab.
/// </summary>
public sealed class WebGpuCapability
{
    private readonly IJSRuntime _js;
    private readonly Lazy<Task<WebGpuInfo?>> _probe;

    public WebGpuCapability(IJSRuntime js)
    {
        _js = js;
        _probe = new Lazy<Task<WebGpuInfo?>>(
            InvokeProbeAsync,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Task<WebGpuInfo?> GetAsync() => _probe.Value;

    private async Task<WebGpuInfo?> InvokeProbeAsync()
    {
        try
        {
            // Loaded on demand rather than by a <script> tag on every page: Home is its only
            // Blazor caller. It is a classic script that assigns window globals, which is also
            // valid as a module, so importing it defines checkWebGpu exactly as the tag did.
            // The ?v= must match /diag's tag in Diag.cshtml — bump both together.
            await _js.InvokeAsync<IJSObjectReference>("import", "/js/diag-interop.js?v=8");
            return await _js.InvokeAsync<WebGpuInfo>("checkWebGpu");
        }
        catch (JSException)
        {
            // diag-interop.js failed to load — treat as unsupported so callers default to
            // a safe path rather than racing on an undefined promise.
            return new WebGpuInfo { Supported = false, Reason = "WebGPU probe unavailable." };
        }
        catch (JSDisconnectedException)
        {
            return new WebGpuInfo { Supported = false, Reason = "Browser disconnected." };
        }
    }

    /// <summary>Minimal shape mirrored from diag-interop.js's checkWebGpu return.</summary>
    public sealed class WebGpuInfo
    {
        public bool Supported { get; set; }
        public string Vendor { get; set; } = "";
        public string Architecture { get; set; } = "";
        public string Device { get; set; } = "";
        public string Reason { get; set; } = "";

        /// <summary>The adapter's <c>maxStorageBufferBindingSize</c>, in MB. 0 when not reported.</summary>
        public double MaxStorageBufferBindingMb { get; set; }

        /// <summary>
        /// True when the adapter cannot give WebLLM the 1 GB binding it asks for, so the runtime
        /// falls back to 128 MB and only builds marked <c>low_resource_required</c> load. This is
        /// a hard limit the adapter states, not an estimate — browsers do not expose VRAM, so
        /// "is there enough memory" cannot be answered ahead of time and is not attempted.
        /// </summary>
        public bool LowResourceOnly => Supported && MaxStorageBufferBindingMb is > 0 and < 1024;
    }
}
