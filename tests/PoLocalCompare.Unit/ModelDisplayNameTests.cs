using PoLocalCompare.Shared.Ids;
using PoLocalCompare.Shared.Models;

namespace PoLocalCompare.Unit;

/// <summary>
/// One resolver, three former call sites. The Archive, the Home page's recent list and the
/// kill-list handler each had their own copy of this and disagreed on the answer, so the rules
/// are pinned here rather than in whichever surface happens to be looked at.
/// </summary>
public class ModelDisplayNameTests
{
    private static readonly ModelId Id = ModelId.From("01SEED000000000000000000P");

    [Theory]
    // The live catalog wins, so renaming a model shows through on old duels.
    [InlineData("GPT-5.4 Nano", "Old Name", "GPT-5.4 Nano")]
    // Model retired from the catalog → the snapshot taken when the duel was created.
    // Neither available (a row predating the snapshot) → the ID, which is the signal callers
    // use to detect "unresolved" and substitute their own label.
    [InlineData(null, null, "01SEED000000000000000000P")]
    public void Resolve_PrefersLiveNameThenSnapshotThenId(string? live, string? snapshot, string expected)
    {
        Assert.Equal(expected, ModelDisplayName.Resolve(live, snapshot, Id));
    }

    [Fact]
    public void IsUnresolved_TrueWhenTheNameIsTheIdOrBlank()
    {
        // The id itself, the id with different casing and surrounding space, and a blank name
        // all mean the same thing to a caller: nothing was resolved.
        Assert.True(ModelDisplayName.IsUnresolved(Id.Value, Id.Value));
        Assert.True(ModelDisplayName.IsUnresolved($"  {Id.Value.ToLowerInvariant()} ", Id.Value));
        Assert.True(ModelDisplayName.IsUnresolved("   ", Id.Value));
    }

    [Fact]
    public void ResolveForDisplay_SubstitutesThePlaceholderAndPassesRealNamesThrough()
    {
        // The whole point: a 26-character ULID must never reach a table cell. The positive half
        // is the same rule read the other way — a real name is passed through, trimmed.
        Assert.Equal(ModelDisplayName.RetiredPlaceholder, ModelDisplayName.ResolveForDisplay(null, null, Id));
        Assert.Equal("Phi-4", ModelDisplayName.ResolveForDisplay("  Phi-4  ", null, Id));
        Assert.Equal("Phi-4", ModelDisplayName.Resolve("  Phi-4 ", null, Id));
        Assert.Equal("Phi-4", ModelDisplayName.Resolve(null, " Phi-4  ", Id));
        Assert.False(ModelDisplayName.IsUnresolved("Phi-4", Id.Value));
    }
}
