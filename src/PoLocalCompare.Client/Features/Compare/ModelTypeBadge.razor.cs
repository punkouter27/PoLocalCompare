using System.Diagnostics.CodeAnalysis;

namespace PoLocalCompare.Client.Features.Compare;

[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Component variant enum kept stable")]
public enum ModelTypeBadgeVariant
{
    /// <summary>Uppercase tight form — fits alongside a name in a row.</summary>
    Short,
    /// <summary>Emoji-prefixed form — used as a section heading / standalone.</summary>
    Long
}
