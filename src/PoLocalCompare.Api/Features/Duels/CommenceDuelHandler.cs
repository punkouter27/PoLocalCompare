// SOLID: Open/Closed — new model types extend without modifying handler
using Microsoft.Extensions.Options;
using NUlid;
using PoLocalCompare.Api.Auth;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

public sealed class CommenceDuelHandler(
    IModelRepository modelRepository,
    IDuelRepository duelRepository,
    IOptions<AutoJudgeOptions> autoJudgeOptions)
{
    /// <summary>
    /// Appended to every duel's prompt, and so read by both contestants AND the judge.
    /// </summary>
    /// <remarks>
    /// Until 2026-10-11 this said the opposite: "Use public CDN links … for all external
    /// libraries". That worked for a person — the Arena's preview frame has network — and
    /// against the page everywhere else: <c>HtmlScreenshotRenderer</c> blocks all network, so a
    /// page that took the advice and loaded three.js from a CDN was screenshotted blank and
    /// judged as not working, and the judge, which is handed this same text as "the request",
    /// was scoring pages on whether they used CDN links (it said so in its analysis). It also
    /// contradicted <see cref="InferencePrompt.System"/> the moment that gained its
    /// no-external-files line. One instruction now, the same in both places.
    /// </remarks>
    private const string SelfContainedSuffix =
        "\n\nIMPORTANT: The page must be fully self-contained. Write all CSS and JavaScript inline and load nothing from the network — no CDN links, external scripts, stylesheets, fonts or images.";

    public async Task<DuelDto> HandleAsync(CommenceDuelCommand command)
    {
        if (command.LeftModelId == command.RightModelId)
            throw new ArgumentException("LeftModelId and RightModelId must differ.");

        if (string.IsNullOrWhiteSpace(command.PromptText))
            throw new ArgumentException("PromptText cannot be empty.", nameof(command));

        if (command.PromptText.Length < CommenceDuelCommand.MinPromptLength)
            throw new ArgumentException($"PromptText must be at least {CommenceDuelCommand.MinPromptLength} characters.", nameof(command));

        if (command.PromptText.Length > CommenceDuelCommand.MaxPromptLength)
            throw new ArgumentException($"PromptText cannot exceed {CommenceDuelCommand.MaxPromptLength} characters.", nameof(command));

        var leftModel = await modelRepository.GetByIdAsync(command.LeftModelId)
            ?? throw new KeyNotFoundException($"Model '{command.LeftModelId}' not found.");
        var rightModel = await modelRepository.GetByIdAsync(command.RightModelId)
            ?? throw new KeyNotFoundException($"Model '{command.RightModelId}' not found.");

        var duelId = DuelId.New();
        var promptFull = command.PromptText + SelfContainedSuffix;

        var duel = new Duel(
            duelId,
            command.PromptText,
            promptFull,
            command.LeftModelId,
            command.RightModelId)
        {
            // Snapshotted at creation so the duel stays readable if either model is later
            // retired from the catalog — see Duel.LeftModelName.
            LeftModelName = leftModel.DisplayName,
            RightModelName = rightModel.DisplayName,
            // Forensic only — never used for authorization. "anonymous" is the sentinel the
            // endpoint injects when the open gate is in effect and no claim is present.
            OwnerId = command.Actor ?? IdentityResolver.AnonymousActor,
        };

        await duelRepository.SaveAsync(duel);

        return new DuelDto
        {
            DuelId = duel.DuelId,
            PromptText = duel.PromptText,
            PromptFull = duel.PromptFull,
            LeftModelId = duel.LeftModelId,
            RightModelId = duel.RightModelId,
            LeftModelName = leftModel.DisplayName,
            RightModelName = rightModel.DisplayName,
            StartedAt = duel.StartedAt,
            Verdict = DuelVerdict.Pending,
            AutoJudgeDelaySeconds = autoJudgeOptions.Value.Enabled
                ? command.AutoJudgeDelaySecondsOverride ?? autoJudgeOptions.Value.DelaySeconds
                : 0,
            IsPartial = false,
            OwnerId = duel.OwnerId,
            VerdictBy = duel.VerdictBy,
        };
    }
}
