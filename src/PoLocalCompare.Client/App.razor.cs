using System.Diagnostics.CodeAnalysis;
using PoLocalCompare.Client.Pages;

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
public partial class App
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties, typeof(NotFound))]
    protected override void OnInitialized() => base.OnInitialized();
}
