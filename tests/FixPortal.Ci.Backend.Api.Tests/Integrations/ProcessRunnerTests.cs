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

    // CB-H5: the existing tests are happy-path (process exits fast) plus a synthetic
    // WaitForExitAndDrainAsync drain using Task.CompletedTask, so CancelAfter never
    // actually fires against a real process. Spawn a genuinely long-running child and
    // time it out almost immediately: RunAsync must both throw and actually kill the
    // OS process — deleting TryKill's Kill() call would still leave the exception
    // (the timeout alone throws OperationCanceledException) but the child would keep
    // running past the deadline, which is what this test catches.
    [Fact]
    public async Task RunAsync_should_kill_a_genuinely_blocking_child_process_on_timeout()
    {
        var (fileName, arguments, processName) = OperatingSystem.IsWindows()
            ? ("ping", new[] { "-n", "60", "127.0.0.1" }, "ping")
            : ("sleep", new[] { "60" }, "sleep");
        var before = Process.GetProcessesByName(processName).Select(p => p.Id).ToHashSet();

        var runTask = ProcessRunner.RunAsync(
            fileName,
            arguments,
            TimeSpan.FromMilliseconds(300),
            TestContext.Current.CancellationToken);

        // Capture the spawned child's PID early, while it is certainly still alive
        // (well inside the 300ms window before the timeout fires). Windows has no
        // zombie-process state, so a PID looked up only *after* RunAsync throws would
        // already have vanished from the process table on a successful kill — that
        // false negative is exactly what a naive "check after the throw" would hit.
        int? childId = null;
        var captureDeadline = DateTime.UtcNow.AddMilliseconds(250);
        while (childId is null && DateTime.UtcNow < captureDeadline)
        {
            var spawned = Process.GetProcessesByName(processName).Where(p => !before.Contains(p.Id)).ToList();
            if (spawned.Count > 0)
            {
                childId = spawned[0].Id;
            }
            else
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }
        _ = childId.Should().NotBeNull("ProcessRunner should have spawned a new {0} process before the timeout fired", processName);

        var act = async () => await runTask;
        _ = await act.Should().ThrowAsync<OperationCanceledException>();

        // Poll-until-condition with a generous timeout: a killed process exits within
        // milliseconds; the un-killed 60s ping/sleep would still be alive throughout
        // this entire window, so seeing it gone here is proof TryKill actually ran.
        var stillRunning = true;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (stillRunning && DateTime.UtcNow < deadline)
        {
            try
            {
                using var child = Process.GetProcessById(childId!.Value);
                stillRunning = !child.HasExited;
            }
            catch (ArgumentException)
            {
                stillRunning = false; // no process with this id exists any more
            }

            if (stillRunning)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }
        _ = stillRunning.Should().BeFalse("the timed-out child process must be killed, not left running");
    }
}
