namespace PoLocalCompare.Shared.Presentation;

/// <summary>An optional feature that a fixed switch in <c>wwwroot/js/prefs.js</c> turns on or off.</summary>
public enum Preference
{
    Effects,
    Sound,
    ArenaDetails,
    LeaderboardChart,
    Assistants,
}

/// <summary>The key each <see cref="Preference"/> has in prefs.js's <c>ON</c> table.</summary>
/// <remarks>
/// There is no settings page (removed 2026-11-19): every visitor gets the values prefs.js holds,
/// which is also the one place to change them. prefs.js owns them rather than C# because effects
/// and sound are needed before Blazor boots. Only decoration and paid extras belong here: what
/// makes ratings converge (the ±, Suggest matchup, tournaments) is never switchable.
/// </remarks>
public static class Preferences
{
    public static string StorageKey(Preference key) => key switch
    {
        Preference.Effects => "effects",
        Preference.Sound => "sound",
        Preference.ArenaDetails => "arena-details",
        Preference.LeaderboardChart => "leaderboard-chart",
        Preference.Assistants => "assistants",
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "No prefs.js key for this preference."),
    };
}
