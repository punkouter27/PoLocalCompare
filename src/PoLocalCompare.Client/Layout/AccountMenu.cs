namespace PoLocalCompare.Client.Layout;

/// <summary>
/// Helpers for projecting signed-in identity and dev switcher choices inside <see cref="NavMenu"/>.
/// </summary>
internal static class AccountMenu
{
    public static readonly string[] DevPersonas =
    [
        "dev-reviewer",
        "dev-judge",
    ];

    public static string LocalPart(string? identity)
    {
        if (string.IsNullOrEmpty(identity))
            return string.Empty;

        var at = identity.IndexOf('@');
        return at > 0 ? identity[..at] : identity;
    }

    public static string? DomainPart(string? identity)
    {
        if (string.IsNullOrEmpty(identity))
            return null;

        var at = identity.IndexOf('@');
        return at > 0 && at < identity.Length - 1 ? identity[at..] : null;
    }

    public static string Initial(string? identity)
    {
        var local = LocalPart(identity);
        return local.Length > 0 ? local[..1].ToUpperInvariant() : "?";
    }
}
