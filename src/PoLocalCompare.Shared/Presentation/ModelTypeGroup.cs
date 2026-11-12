using System.Globalization;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Shared.Presentation;

/// <summary>
/// Single source of truth for how models are grouped by type across the app, so the
/// Model Health panel and the Home model picker present identical Remote / Browser /
/// Ollama groups in the same order.
/// </summary>
public static class ModelTypeGroup
{
    /// <summary>Display order: Remote → Browser → Ollama (matches the picker's filter chips).</summary>
    public static int Order(ModelType type) => type switch
    {
        ModelType.Remote => 0,
        ModelType.Local => 1,          // browser / WebLLM
        ModelType.LocalService => 2,   // Ollama
        _ => 9,
    };

    /// <summary>Human label with icon, e.g. "☁ Remote".</summary>
    public static string Label(ModelType type) => type switch
    {
        ModelType.Remote => "☁ Remote",
        ModelType.Local => "🖥 Browser",
        ModelType.LocalService => "🦙 Ollama",
        _ => "Other",
    };

    /// <summary>
    /// Uppercase badge form for tight spaces. Pairs with a
    /// <c>--@ModelTypeGroup.CssModifier(type)</c> class on the same element.
    /// </summary>
    /// <remarks>
    /// These must be the same WORDS the filter chips use, abbreviated to fit — the old
    /// "LOCAL"/"SVC" pair named the same two concepts as "Browser"/"Ollama" with two entirely
    /// different words, so a card could say LOCAL while sitting under a tab that said Browser
    /// and a badge SVC under a chip that said Ollama. "REMOTE / BROWSER / OLLAMA" keep one
    /// vocabulary everywhere; "OLLAMA" is longer than "SVC" but the badge truncates cleanly
    /// (whitespace: nowrap in every consumer) and reads correctly, which "SVC" never did.
    /// </remarks>
    public static string ShortLabel(ModelType type) => type switch
    {
        ModelType.Remote => "REMOTE",
        ModelType.Local => "BROWSER",
        ModelType.LocalService => "OLLAMA",
        _ => "OTHER",
    };

    /// <summary>Lowercase type name for BEM modifier classes, e.g. <c>badge--remote</c>.</summary>
    public static string CssModifier(ModelType type) => type.ToString().ToLower(CultureInfo.InvariantCulture);
}
