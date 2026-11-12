namespace PoLocalCompare.Client.Layout;

public enum EmptyStateActionVariant
{
    Primary,
    Secondary,
    Ghost
}

public sealed record EmptyStateAction(
    string Label,
    string Href,
    EmptyStateActionVariant Variant = EmptyStateActionVariant.Primary,
    string? Title = null);
