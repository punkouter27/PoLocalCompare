namespace PoLocalCompare.Shared.DTOs;

/// <summary>
/// The body of <c>GET /api/diag</c>, which the /diag page renders. Built from the same
/// <c>HealthCheckService</c> report /health serves, and deliberately free of anything
/// identifying: no endpoints, URIs, keys, connection strings, paths, ids, emails or exception
/// messages — at most an exception's type name.
/// </summary>
public sealed record DiagReportDto(
    string Status,
    string Environment,
    string Version,
    DateTimeOffset CheckedAt,
    IReadOnlyList<DiagCheckDto> Stores,
    IReadOnlyList<DiagCheckDto> Services);

/// <param name="Name">Human name, e.g. "Table Storage".</param>
/// <param name="Purpose">One plain-English line on what the app uses it for.</param>
/// <param name="Status">Healthy, Degraded or Unhealthy.</param>
/// <param name="Configured">False when this environment does not use the dependency at all.</param>
/// <param name="LatencyMs">Probe time, when the dependency was actually reached.</param>
/// <param name="Problem">The exception type name when the probe failed — never its message.</param>
public sealed record DiagCheckDto(
    string Name,
    string Purpose,
    string Status,
    bool Configured,
    long? LatencyMs,
    DateTimeOffset CheckedAt,
    string? Problem);
