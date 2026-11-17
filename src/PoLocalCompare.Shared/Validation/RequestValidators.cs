using FluentValidation;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Prompts;

namespace PoLocalCompare.Shared.Validation;

/// <summary>
/// The one prompt rule every prompt-taking form and endpoint applies: present, at least
/// <see cref="PromptRules.MinPromptLength"/> characters once trimmed, at most
/// <see cref="PromptRules.MaxPromptLength"/>. The page and the API read the same validator, so
/// a prompt the form accepts is never refused by the server for a different reason.
/// </summary>
public static class PromptRuleExtensions
{
    /// <summary>The first thing wrong with <paramref name="prompt"/>, or null when it is usable.</summary>
    public static string? PromptProblem(string? prompt) =>
        string.IsNullOrWhiteSpace(prompt) ? "Write a prompt first."
        : prompt.Trim().Length < PromptRules.MinPromptLength ? $"The prompt must be at least {PromptRules.MinPromptLength} characters."
        : prompt.Length > PromptRules.MaxPromptLength ? $"The prompt cannot exceed {PromptRules.MaxPromptLength} characters."
        : null;

    public static IRuleBuilderOptionsConditions<T, string?> ValidDuelPrompt<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Custom((prompt, context) =>
        {
            if (PromptProblem(prompt) is { } problem)
                context.AddFailure(problem);
        });
}

/// <summary>The prompt box on Compare and on Tournament — the form model the page edits.</summary>
public sealed class PromptForm
{
    public string PromptText { get; set; } = string.Empty;
}

public sealed class PromptFormValidator : AbstractValidator<PromptForm>
{
    public PromptFormValidator() => RuleFor(x => x.PromptText).ValidDuelPrompt();
}

/// <summary><c>POST /api/duels</c>: two different models and a valid prompt.</summary>
public sealed class CommenceDuelRequestValidator : AbstractValidator<CommenceDuelRequest>
{
    public CommenceDuelRequestValidator()
    {
        RuleFor(x => x.LeftModelId).Must(id => !string.IsNullOrWhiteSpace(id.Value)).WithMessage("Pick the left model.");
        RuleFor(x => x.RightModelId).Must(id => !string.IsNullOrWhiteSpace(id.Value)).WithMessage("Pick the right model.");
        RuleFor(x => x.RightModelId)
            .Must((request, right) => right != request.LeftModelId)
            .WithMessage("Pick two different models — a duel against the same model twice is not a duel.");
        RuleFor(x => x.PromptText).ValidDuelPrompt();
    }
}

/// <summary>
/// <c>POST /api/tournaments</c>: a valid prompt and at least two entrants. Field size and
/// duplicates are the handler's (it knows the bracket rules and the catalog).
/// </summary>
public sealed class CreateTournamentRequestValidator : AbstractValidator<CreateTournamentRequest>
{
    public CreateTournamentRequestValidator()
    {
        RuleFor(x => x.ModelIds).Must(ids => ids is { Count: >= 2 }).WithMessage("Pick at least two models.");
        RuleFor(x => x.PromptText).ValidDuelPrompt();
    }
}

/// <summary><c>POST /api/models/discovered</c>: an id, and a display name that is sensible if given.</summary>
public sealed class AddDiscoveredModelRequestValidator : AbstractValidator<AddDiscoveredModelRequest>
{
    public const int MaxDisplayNameLength = 80;

    public AddDiscoveredModelRequestValidator()
    {
        RuleFor(x => x.Id).NotEmpty().WithMessage("Choose a model to add.");
        RuleFor(x => x.DisplayName)
            .Must(name => name is null || name.Length == 0 || name.Trim().Length > 0)
            .WithMessage("A name cannot be only spaces — clear it to use the suggested name.")
            .MaximumLength(MaxDisplayNameLength)
            .WithMessage($"Keep the name to {MaxDisplayNameLength} characters or fewer.");
    }
}
