namespace PoLocalCompare.Shared.Diagnostics;

/// <summary>
/// What <c>GET /api/diag</c> answers (Po web standard, AGENTS.md) and the <c>/diag</c> "System status" page shows.
/// Anonymous, so plain words only: never a connection string, key, URL, path, id, email or exception message.
/// </summary>
/// <param name="Status">"Healthy", "Degraded" or "Unhealthy": the worst of <paramref name="Checks"/>.</param>
/// <param name="Environment">The host environment name (Production, Development, …).</param>
/// <param name="Version">The build's <c>BuildVersion</c> stamp, UTC <c>yyMMdd</c>.</param>
/// <param name="CheckedAt">When the checks ran (UTC).</param>
/// <param name="CheckedMs">How long all the checks took together, in milliseconds.</param>
public sealed record DiagReportDto(
    string Status,
    string Environment,
    string Version,
    DateTimeOffset CheckedAt,
    double CheckedMs,
    DiagCheckDto[] Checks);

/// <summary>One row on <c>/diag</c>: one dependency the app really calls.</summary>
/// <param name="Name">Stable machine id (e.g. "api", "table-storage").</param>
/// <param name="Group">"api", "data" or "service": the section it is shown in.</param>
/// <param name="Label">What the page calls it.</param>
/// <param name="Status">"Healthy", "Degraded", "Unhealthy", or "Off" for a service switched off here on purpose
/// (neutral: it never makes the report Degraded or Unhealthy).</param>
/// <param name="Purpose">What the app uses it for, in one line.</param>
/// <param name="Detail">How it is, in one line.</param>
/// <param name="LatencyMs">How long the check took.</param>
/// <param name="Problem">What is wrong, in plain words, when it is Degraded or Unhealthy (an exception's type at most).</param>
public sealed record DiagCheckDto(
    string Name,
    string Group,
    string Label,
    string Status,
    string Purpose,
    string Detail,
    double LatencyMs,
    string? Problem);
