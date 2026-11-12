using FluentValidation;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Prompts;

namespace PoLocalCompare.Shared.Validation;

/// <summary>
/// Shared FluentValidation rules for prompt refinement (Rules 11 & 17).
/// </summary>
public sealed class RefinePromptRequestValidator : AbstractValidator<RefinePromptRequest>
{
    public RefinePromptRequestValidator()
    {
        RuleFor(x => x.Prompt)
            .Cascade(CascadeMode.Stop)
            .Must(p => !string.IsNullOrWhiteSpace(p) && p.Trim().Length >= 3)
            .WithMessage("Prompt must be at least 3 characters.")
            .Must(p => p!.Trim().Length <= PromptRules.MaxPromptLength)
            .WithMessage($"Prompt cannot exceed {PromptRules.MaxPromptLength} characters.");
    }
}
