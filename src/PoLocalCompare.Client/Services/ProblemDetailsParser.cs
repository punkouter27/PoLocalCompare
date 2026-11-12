using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// Shared RFC 7807 ProblemDetails response parser (Rule 14).
/// </summary>
internal static class ProblemDetailsParser
{
    public static async Task<DuelApiClient.ProblemDetailsLite?> TryReadAsync(HttpResponseMessage response, CancellationToken ct = default)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(ClientJson.Default.ProblemDetailsLite, ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
