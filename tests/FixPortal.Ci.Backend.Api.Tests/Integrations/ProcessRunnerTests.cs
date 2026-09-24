using System.Diagnostics;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Integrations.Lizard;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Integrations;

public class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_passes_extra_environment_to_the_child_process()
    {
        // Use the OS-native shell rather than pwsh: sh/cmd are guaranteed present,
        // start in milliseconds, and exit immediately with a clean pipe EOF. pwsh
        // is a fragile CI fixture (cold-start, telemetry, update checks) and was
        // deterministically tripping the timeout on the Linux runner.
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "echo %FIXPORTAL_TEST_SECRET%" })
            : ("/bin/sh", ["-c", "printf %s \"$FIXPORTAL_TEST_SECRET\""]);

        var result = await ProcessRunner.RunAsync(
            fileName,
            arguments,
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken,
            new Dictionary<string, string> { ["FIXPORTAL_TEST_SECRET"] = "from-env" }
        );

        _ = result.ExitCode.Should().Be(0);
        _ = result.StdOut.Should().Contain("from-env");
    }

    [Fact]
    public async Task RunAsync_caps_captured_output_and_preserves_the_trailing_summary()
    {
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? (
                "powershell.exe",
                new[]
                {
                    "-NoLogo",
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "Write-Output ('x' * 2100000); Write-Output 'TAIL_MARKER'",
                }
            )
            : ("/bin/sh", new[] { "-c", "printf '%*s\\n' 2100000 '' | tr ' ' x; printf 'TAIL_MARKER\\n'" });

        var result = await ProcessRunner.RunAsync(
            fileName,
            arguments,
            TimeSpan.FromSeconds(20),
            TestContext.Current.CancellationToken
        );

        _ = result.ExitCode.Should().Be(0);
        _ = result.StdOut.Length.Should().BeLessThanOrEqualTo(1_000_002);
        _ = result.StdOut.TrimEnd().Should().EndWith("TAIL_MARKER");
    }

    [Fact]
    public async Task WaitForExitAndDrainAsync_allows_pipe_drain_to_finish_after_process_exit()
    {
        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var wait = ProcessRunner.WaitForExitAndDrainAsync(
            Task.CompletedTask,
            stdoutClosed.Task,
            stderrClosed.Task,
            TimeSpan.FromMilliseconds(10),
            TestContext.Current.CancellationToken
        );

        _ = wait.IsCompleted.Should().BeFalse();

        stdoutClosed.SetResult();
        stderrClosed.SetResult();

        await wait;

        _ = stdoutClosed.Task.IsCompleted.Should().BeTrue();
        _ = stderrClosed.Task.IsCompleted.Should().BeTrue();
    }

    /// <summary>Proves caller cancellation kills the exact child process that announced readiness.</summary>
    [Fact]
    public async Task RunAsync_should_kill_the_exact_child_process_on_caller_cancellation()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"fixportal-child-{Guid.NewGuid():N}.pid");
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? (
                "powershell.exe",
                new[]
                {
                    "-NoLogo",
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    $"Set-Content -LiteralPath '{marker}' -Value $PID; Start-Sleep -Seconds 60",
                }
            )
            : ("/bin/sh", new[] { "-c", $"echo $$ > '{marker}'; exec sleep 60" });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Process? child = null;
        try
        {
            var runTask = ProcessRunner.RunAsync(fileName, arguments, TimeSpan.FromSeconds(30), cancellation.Token);
            var childId = await WaitForChildPidAsync(
                marker,
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken
            );
            child = Process.GetProcessById(childId);
            await cancellation.CancelAsync();

            var act = async () => await runTask;
            _ = await act.Should().ThrowAsync<OperationCanceledException>();
            await child
                .WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            _ = child.HasExited.Should().BeTrue("the exact child process that announced readiness must be killed");
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    _ = child.WaitForExit(5000).Should().BeTrue("test cleanup must not leave its child running");
                }
                child.Dispose();
            }
            File.Delete(marker);
        }
    }

    /// <summary>Proves the configured process wait timeout cancels an incomplete wait independently of caller cancellation.</summary>
    [Fact]
    public async Task WaitForExitAndDrainAsync_should_cancel_when_the_configured_timeout_expires()
    {
        var neverExits = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var act = () =>
            ProcessRunner
                .WaitForExitAndDrainAsync(
                    neverExits.Task,
                    Task.CompletedTask,
                    Task.CompletedTask,
                    TimeSpan.FromMilliseconds(25),
                    TestContext.Current.CancellationToken
                )
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        _ = await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static async Task<int> WaitForChildPidAsync(string marker, TimeSpan timeout, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            try
            {
                if (File.Exists(marker) && int.TryParse(await File.ReadAllTextAsync(marker, ct), out var pid))
                {
                    return pid;
                }
            }
            catch (IOException)
            {
                // The child still holds the marker open for its write (Windows sharing violation).
            }
            await Task.Delay(20, ct);
        }
        throw new TimeoutException("The child did not publish its PID before the readiness deadline.");
    }
}
