using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using PoLocalCompare.Client.Layout;

namespace PoLocalCompare.Client;

/// <summary>Root component: the router, the auth cascade and the not-found page.</summary>
/// <remarks>
/// The Razor compiler sets <c>Router.NotFoundPage</c> through the component parameter path,
/// which the trim analyzer reports as IL2111 because the property carries
/// <c>[DynamicallyAccessedMembers(PublicConstructors)]</c>. That requirement is met by rooting
/// <see cref="NotFound"/>'s constructors explicitly below, so the warning is suppressed here
/// with the dependency it was asking for — this was the one trimming blocker noted in the
/// csproj, and is why <c>PublishTrimmed</c> can be evaluated at all.
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2111",
    Justification = "NotFound's public constructors are rooted by the DynamicDependency on OnInitialized.")]
public sealed partial class App : IDisposable
{
    [Inject] private NavigationManager Nav { get; set; } = default!;

    /// <summary>True once the visitor has moved to another route inside the app.</summary>
    private bool _navigated;

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties, typeof(NotFound))]
    protected override void OnInitialized()
    {
        base.OnInitialized();
        Nav.LocationChanged += OnLocationChanged;
    }

    private void OnLocationChanged(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs e)
    {
        if (_navigated) return;
        _navigated = true;
        StateHasChanged();
    }

    public void Dispose() => Nav.LocationChanged -= OnLocationChanged;
}
