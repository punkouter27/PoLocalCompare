using Microsoft.AspNetCore.Components.Forms;
using PoLocalCompare.Shared.Validation;

namespace PoLocalCompare.Client.Layout;

/// <summary>
/// The validation state behind a prompt box (Compare, Tournament): a <see cref="PromptForm"/>, its
/// <see cref="EditContext"/>, and the rule that errors appear only after a start was attempted —
/// "must be at least 10 characters" under the first keystroke would be noise. Rendered by
/// <see cref="PromptError"/>; the rules are the Shared <see cref="PromptFormValidator"/>, the same
/// ones the API applies to the request.
/// </summary>
public sealed class PromptCheck
{
    private bool _attempted;

    public PromptCheck() => Context = new EditContext(Form);

    public PromptForm Form { get; } = new();

    public EditContext Context { get; }

    private FieldIdentifier Field => Context.Field(nameof(PromptForm.PromptText));

    /// <summary>True when the last check found a problem that is still on screen.</summary>
    public bool HasError => Context.GetValidationMessages(Field).Any();

    /// <summary>Runs the validator for a start attempt; from here on, edits re-check live.</summary>
    public bool Validate(string prompt)
    {
        _attempted = true;
        Form.PromptText = prompt;
        return Context.Validate();
    }

    /// <summary>Call whenever the prompt changes (typing, the picker, Sharpen, Undo).</summary>
    public void Changed(string prompt)
    {
        Form.PromptText = prompt;
        if (_attempted)
            Context.NotifyFieldChanged(Field);
    }
}
