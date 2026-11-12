using Radzen;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// Standardized notification and confirmation dialog helpers backed by Radzen services (Rule 9).
/// </summary>
public sealed class DialogNotificationHelper(NotificationService notifications, DialogService dialogs)
{
    public void Success(string summary, string? detail = null) =>
        notifications.Notify(NotificationSeverity.Success, summary, detail ?? string.Empty, duration: 3500);

    public void Error(string summary, string? detail = null) =>
        notifications.Notify(NotificationSeverity.Error, summary, detail ?? string.Empty, duration: 5000);

    public async Task<bool> ConfirmAsync(string message, string title = "Confirm") =>
        await dialogs.Confirm(message, title, new ConfirmOptions
        {
            OkButtonText = "Continue",
            CancelButtonText = "Cancel",
        }) == true;
}
