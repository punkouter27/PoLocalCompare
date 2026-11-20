using System.ComponentModel;
using System.Diagnostics;

namespace PoLocalCompare.TestSupport;

/// <summary>
/// Whether a Docker engine answers, asked once per test run. The Integration and E2E-API tiers start
/// Azurite through Testcontainers; without Docker they SKIP with one clear reason instead of failing
/// every test with DockerUnavailableException (Po web standard, AGENTS.md). In CI (ubuntu-latest)
/// Docker is always there, so they run.
/// </summary>
public static class DockerAvailability
{
    public const string SkipReason =
        "Docker is not running. Start Docker Desktop to run this tier (it starts Azurite through Testcontainers).";

    private static readonly Lazy<bool> Available = new(Probe);

    public static bool IsAvailable => Available.Value;

    private static bool Probe()
    {
        try
        {
            var start = new ProcessStartInfo("docker", "info --format {{.ServerVersion}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }

            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20_000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            // No docker CLI on PATH.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>A <see cref="FactAttribute"/> that skips, with <see cref="DockerAvailability.SkipReason"/>, when Docker is not running.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!DockerAvailability.IsAvailable)
        {
            Skip = DockerAvailability.SkipReason;
        }
    }
}

/// <summary>A <see cref="TheoryAttribute"/> that skips, with <see cref="DockerAvailability.SkipReason"/>, when Docker is not running.</summary>
public sealed class DockerTheoryAttribute : TheoryAttribute
{
    public DockerTheoryAttribute()
    {
        if (!DockerAvailability.IsAvailable)
        {
            Skip = DockerAvailability.SkipReason;
        }
    }
}
