using Microsoft.AspNetCore.Components;

namespace PoLocalCompare.Client.Layout;

public enum PageActionsAlign { Start, Center, End }

public enum PageActionsVariant { Default, Primary, Secondary, Warn }

/// <summary>
/// One row of the <see cref="PageActions"/> primitive. <see cref="OnClick"/> defaults to
/// <c>EventCallback.Empty</c>; pass <c>default</c> from the caller so an action with no
/// delegate still type-checks.
/// </summary>
public sealed record PageActionDescriptor(
    string Label,
    string? Href = null,
    EventCallback OnClick = default,
    bool Disabled = false,
    PageActionsVariant Variant = PageActionsVariant.Default,
    string? Title = null);
