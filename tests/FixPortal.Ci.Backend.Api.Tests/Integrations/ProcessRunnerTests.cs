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
            new Dictionary<string, string> { ["FIXPORTAL_TEST_SECRET"] = "from-env" });

        _ = result.ExitCode.Should().Be(0);
        _ = result.StdOut.Should().Contain("from-env");
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
            TestContext.Current.CancellationToken);

        _ = wait.IsCompleted.Should().BeFalse();

        stdoutClosed.SetResult();
        stderrClosed.SetResult();

        await wait;

        _ = stdoutClosed.Task.IsCompleted.Should().BeTrue();
        _ = stderrClosed.Task.IsCompleted.Should().BeTrue();
    }
}
