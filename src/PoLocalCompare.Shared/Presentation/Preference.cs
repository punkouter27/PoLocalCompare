namespace PoLocalCompare.Shared.Presentation;

/// <summary>An optional feature the user can switch on from /settings.</summary>
public enum Preference
{
    Effects,
    Sound,
    ArenaDetails,
    LeaderboardChart,
    Assistants,
}

/// <summary>What /settings shows for one <see cref="Preference"/>, and the key prefs.js stores it under.</summary>
public sealed record PreferenceInfo(Preference Key, string StorageKey, string Label, string Description);

/// <summary>
/// The optional features, in the order /settings lists them.
/// </summary>
/// <remarks>
/// Every one is off until the user turns it on (prefs.js reads a missing key as false), so the
/// default app is the plain picker → duel → verdict flow. Only decoration and paid extras belong
/// here: the app is a benchmark whose ratings converge over time, so what serves that — the ± on
/// a rating, Suggest matchup, tournaments — is never optional. Adding a feature here is the whole
/// registration: the settings page renders this list, and the feature asks
/// <c>PreferencesService.IsOn</c> before it renders or calls anything.
/// </remarks>
public static class Preferences
{
    public static readonly IReadOnlyList<PreferenceInfo> All =
    [
        new(Preference.Effects, "effects", "Visual effects",
            "Page transitions, confetti, the verdict nebula and the champion storm. Reduced motion still turns them off."),
        new(Preference.Sound, "sound", "Sound",
            "Short synthesised cues when a duel starts, finishes and is judged."),
        new(Preference.ArenaDetails, "arena-details", "Arena telemetry",
            "Live tokens, speed, cost and energy under each panel while a duel runs."),
        new(Preference.LeaderboardChart, "leaderboard-chart", "Leaderboard chart",
            "The rating-for-the-money chart beside the leaderboard."),
        new(Preference.Assistants, "assistants", "Paid AI helpers",
            "Sharpen prompt, and the scouting report on a model's profile. Each use is a paid model call."),
    ];

    public static PreferenceInfo Info(Preference key) => All.First(p => p.Key == key);
}
