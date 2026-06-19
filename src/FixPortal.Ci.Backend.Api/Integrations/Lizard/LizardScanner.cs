using System.Globalization;
using System.Text;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Options;
using NodaTime;

namespace FixPortal.Ci.Backend.Api.Integrations.Lizard;

/// <summary>
/// Produces code metrics for a repo by shallow-cloning it and running the Lizard
/// static analyser. Designed for a slow-cadence background worker: on any failure
/// it returns null (the caller keeps last-known-good) and never throws except on
/// host-shutdown cancellation. The clone is always removed afterwards.
/// </summary>
public sealed class LizardScanner(
    IOptions<GitHubOptions> gitHub,
    IOptions<DashboardOptions> dashboard,
    IClock clock,
    ILogger<LizardScanner> logger)
{
    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromMinutes(5);
    private readonly GitHubOptions _gitHub = gitHub.Value;
    private readonly DashboardOptions _dashboard = dashboard.Value;

    public string WorkRoot => string.IsNullOrWhiteSpace(_dashboard.MetricsWorkDirectory)
        ? Path.Combine(Path.GetTempPath(), "ci-dashboard-metrics")
        : _dashboard.MetricsWorkDirectory;

    public async Task<RepoMetrics?> ScanAsync(string repo, CancellationToken ct)
    {
        if (Path.IsPathRooted(repo))
        {
            logger.LogWarning("Path traversal attempt blocked: repository name '{Repo}' is rooted.", repo);
            return null;
        }

        var fullWorkRoot = Path.GetFullPath(WorkRoot);
        var dir = Path.GetFullPath(Path.Combine(fullWorkRoot, repo.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        if (!dir.StartsWith(fullWorkRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Path traversal attempt blocked: repository name '{Repo}' resolves outside WorkRoot.", repo);
            return null;
        }

        TryDeleteDir(dir);
        try
        {
            _ = Directory.CreateDirectory(WorkRoot);
            var (cloneArguments, cloneEnvironment) = BuildCloneCommand(_gitHub.Owner, repo, _gitHub.Token, dir);
            var clone = await ProcessRunner.RunAsync(
                "git",
                cloneArguments,
                CloneTimeout,
                ct,
                cloneEnvironment);
            if (clone.ExitCode != 0)
            {
                logger.LogWarning("git clone failed for {Repo} (exit {Code}): {Err}",
                    repo, clone.ExitCode, Scrub(clone.StdErr));
                return null;
            }

            var scan = await ProcessRunner.RunAsync(
                "lizard",
                [dir, "-x", "*/node_modules/*", "-x", "*/bin/*", "-x", "*/obj/*", "-x", "*/dist/*"],
                ScanTimeout, ct);
            // Lizard exits non-zero when complexity warnings exist; the summary is
            // still printed, so parse the output regardless of exit code.
            var metrics = ParseLizardSummary(scan.StdOut, clock.GetCurrentInstant());
            if (metrics is null)
            {
                var stdoutPrefix = scan.StdOut.Length > 200 ? scan.StdOut[..200] + "..." : scan.StdOut;
                var stderrPrefix = scan.StdErr.Length > 200 ? scan.StdErr[..200] + "..." : scan.StdErr;
                logger.LogWarning("lizard scan for {Repo} was unparseable (exit {ExitCode}). stdout: {Out}, stderr: {Err}",
                    repo, scan.ExitCode, stdoutPrefix, Scrub(stderrPrefix));
            }
            return metrics;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // host shutdown — let it propagate
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Metrics scan failed for {Repo}; keeping last-known-good.", repo);
            return null;
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    public static (IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment) BuildCloneCommand(
        string owner,
        string repo,
        string token,
        string dir)
    {
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));
        return
        (
            ["clone", "--depth", "1", "--single-branch", $"https://github.com/{owner}/{repo}.git", dir],
            new Dictionary<string, string>
            {
                ["GIT_CONFIG_COUNT"] = "1",
                ["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader",
                ["GIT_CONFIG_VALUE_0"] = $"AUTHORIZATION: basic {basic}",
            }
        );
    }

    /// <summary>
    /// Pure: parse Lizard's summary footer into <see cref="RepoMetrics"/>. The
    /// footer row is "Total nloc / Avg.NLOC / AvgCCN / Avg.token / Fun Cnt /
    /// Warning cnt / ...". Returns null if the table is absent or malformed.
    /// </summary>
    public static RepoMetrics? ParseLizardSummary(string stdout, Instant now)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        var lines = stdout.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("Total nloc", StringComparison.OrdinalIgnoreCase)
                || !lines[i].Contains("AvgCCN", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // header at i, separator at i+1, data row at i+2
            if (i + 2 >= lines.Length)
            {
                return null;
            }

            var cols = lines[i + 2].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 6)
            {
                return null;
            }

            if (!int.TryParse(cols[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var nloc)
                || !double.TryParse(cols[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var avgCcn)
                || !int.TryParse(cols[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var funCnt)
                || !int.TryParse(cols[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var warnCnt))
            {
                return null;
            }

            return new RepoMetrics(nloc, avgCcn, funCnt, warnCnt, now);
        }
        return null;
    }

    private string Scrub(string text) =>
        string.IsNullOrEmpty(_gitHub.Token) ? text : text.Replace(_gitHub.Token, "***");

    private static void TryDeleteDir(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        try
        {
            // git clone marks pack files read-only on Windows; clear before delete.
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                f.IsReadOnly = false;
            }

            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; next scan will retry.
        }
    }
}
