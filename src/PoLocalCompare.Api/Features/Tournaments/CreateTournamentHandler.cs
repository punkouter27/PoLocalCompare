using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;
using PoLocalCompare.Shared.Tournaments;

namespace PoLocalCompare.Api.Features.Tournaments;

/// <summary>Draws a bracket from a chosen field and persists it, ready for the runner.</summary>
public sealed class CreateTournamentHandler(
    IModelRepository modelRepository,
    TournamentRepository tournamentRepository)
{
    /// <summary>
    /// Model types a bracket may contain — all of them, since 2026-08-23.
    /// </summary>
    /// <remarks>
    /// This reverses PRD §9 item 21, which excluded browser (WebGPU) models because a bracket is
    /// designed to keep running after the tab is closed and browser inference cannot. That is
    /// still true, and it is now a <em>caveat</em> rather than a prohibition: the Tournament page
    /// joins the running match's hub group and drives WebGPU inference in the tab, exactly as the
    /// Arena does. The consequence is real and stated on the page — close the tab during a
    /// browser match and that match stalls until the duel's 15-minute watchdog fails it, which
    /// hands the walkover to its opponent. Remote and Ollama matches are unaffected and still
    /// finish with nothing open.
    /// </remarks>
    public static bool IsEligible(ModelType modelType) =>
        modelType is ModelType.Remote or ModelType.LocalService or ModelType.Local;

    /// <summary>
    /// The models that can enter a bracket, strongest first — which is also the seeding order,
    /// so the setup form can show the draw before the user commits to it.
    /// </summary>
    public async Task<IReadOnlyList<TournamentEntrantDto>> ListEntrantsAsync()
    {
        var models = await modelRepository.GetAllAsync();

        return models
            .Where(m => IsEligible(m.ModelType) && !ModelSeeder.RetiredIds.Contains(m.ModelId))
            .OrderByDescending(m => m.CurrentElo)
            .ThenBy(m => m.DisplayName, StringComparer.Ordinal)
            .Select(m => new TournamentEntrantDto
            {
                ModelId = m.ModelId,
                DisplayName = m.DisplayName,
                ModelType = m.ModelType,
                CurrentElo = Math.Round(m.CurrentElo, 1),
            })
            .ToList();
    }

    /// <summary>
    /// Draws and saves the bracket. Throws <see cref="ArgumentException"/> for a field that
    /// cannot make a valid bracket — the endpoint maps that to a 400.
    /// </summary>
    /// <remarks>
    /// The field picks the bracket: 2 models are a 1v1, 9 to 16 are a 16-model bracket whose
    /// missing seeds are byes for the top seeds. Anything else has no bracket to go into.
    /// </remarks>
    public async Task<TournamentDto> HandleAsync(
        IReadOnlyList<ModelId> modelIds,
        string promptText,
        string? actor)
    {
        var count = modelIds.Count;

        if (BracketPlanner.BracketSizeFor(count) is not { } size)
        {
            throw new ArgumentException(
                $"A tournament needs 2 models, or {BracketPlanner.MinimumField(16)} to 16 — got {count}.",
                nameof(modelIds));
        }

        if (modelIds.Distinct().Count() != count)
            throw new ArgumentException("A model cannot enter the same bracket twice.", nameof(modelIds));

        if (string.IsNullOrWhiteSpace(promptText))
            throw new ArgumentException("A tournament needs a prompt.", nameof(promptText));

        if (promptText.Length < CommenceDuelCommand.MinPromptLength)
        {
            throw new ArgumentException(
                $"The prompt must be at least {CommenceDuelCommand.MinPromptLength} characters.",
                nameof(promptText));
        }

        var catalog = (await modelRepository.GetAllAsync()).ToDictionary(m => m.ModelId);

        var entrants = new List<Model>(count);
        foreach (var id in modelIds)
        {
            if (!catalog.TryGetValue(id, out var model))
                throw new ArgumentException($"Model '{id}' is not in the catalog.", nameof(modelIds));

            entrants.Add(model);
        }

        // Seeding is by current rating, strongest first — BracketPlanner stamps the seed numbers
        // and places them so the top two can only meet in the final, and the byes, if any, go
        // to the top seeds.
        var seededField = entrants
            .OrderByDescending(m => m.CurrentElo)
            .ThenBy(m => m.DisplayName, StringComparer.Ordinal)
            .Select(m => new BracketSlot(m.ModelId, m.DisplayName))
            .ToList();

        var tournament = Tournament.Draw(
            TournamentId.New(),
            size,
            seededField,
            promptText.Trim(),
            actor);

        await tournamentRepository.SaveAsync(tournament);

        return tournament.ToDto();
    }
}
