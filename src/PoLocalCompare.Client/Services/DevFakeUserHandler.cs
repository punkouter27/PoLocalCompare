namespace PoLocalCompare.Client.Services;

/// <summary>
/// Attaches <c>X-Fake-User</c> on outbound API calls during Development/Test when the user selects
/// a persona via the header dev switcher (Rule 16).
/// </summary>
public sealed class DevFakeUserHandler : DelegatingHandler
{
    public static string? ActiveFakeUser { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(ActiveFakeUser) && !request.Headers.Contains("X-Fake-User"))
        {
            request.Headers.TryAddWithoutValidation("X-Fake-User", ActiveFakeUser);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
